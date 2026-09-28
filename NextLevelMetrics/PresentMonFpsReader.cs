using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace NextLevelMetrics;

internal sealed class PresentMonFpsReader : IDisposable
{
    private const double HistorySeconds = 1.0;
    private const double GraceSeconds = 1.7;
    private const string SessionName = "NextLevelMetrics";
    private static readonly HashSet<string> ExcludedApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "NextLevelMetrics.exe", "PresentMon.exe", "dwm.exe", "explorer.exe",
        "RadeonSoftware.exe", "GameBar.exe", "steam.exe", "steamwebhelper.exe",
        "Discord.exe", "Code.exe", "chrome.exe", "msedge.exe"
    };

    private readonly object _gate = new();
    private readonly Dictionary<(int Pid, string SwapChain), Chain> _chains = new();
    private readonly string? _diagnosticPath = Environment.GetEnvironmentVariable("NEXTLEVELMETRICS_FPS_LOG");
    private Process? _process;
    private bool _reportedExit;
    private bool _reportedNoFrames;
    private long _startedAt;
    private int _applicationColumn = -1;
    private int _processColumn = -1;
    private int _swapChainColumn = -1;
    private int _displayedColumn = -1;
    private int _displayedTimeColumn = -1;
    private int _presentedColumn = -1;
    private int _applicationFpsColumn = -1;
    private int? _targetPid;
    private string? _targetSwapChain;
    private int? _foregroundCandidatePid;
    private long _foregroundCandidateSince;
    private long _lastValueAt;
    private long _lastLoggedAt;
    private double? _lastDisplayedFps;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public bool Start()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "PresentMon.exe");
        if (!File.Exists(path)) { Log($"PresentMon: no existe {path}"); return false; }

        try
        {
            Log($"PresentMon: iniciando {path}");
            _process = new Process
            {
                StartInfo = new ProcessStartInfo(path)
                {
                    Arguments = $"--output_stdout --no_console_stats --no_track_gpu --no_track_input --session_name {SessionName} --stop_existing_session",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            _process.OutputDataReceived += (_, e) => AcceptLine(e.Data);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) Log($"PresentMon: {e.Data}");
            };
            if (!_process.Start()) { Log("PresentMon: Process.Start devolvió false"); return false; }
            _startedAt = Stopwatch.GetTimestamp();
            Log($"PresentMon: proceso iniciado PID={_process.Id}");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Log($"PresentMon no pudo iniciarse: {ex.Message}");
            Dispose();
            return false;
        }
    }

    public double? GetFps()
    {
        long now = Stopwatch.GetTimestamp();
        int foregroundPid = ForegroundProcessId();
        lock (_gate)
        {
            if (!_reportedExit && _process is { HasExited: true })
            {
                _reportedExit = true;
                Log($"PresentMon: proceso terminado, código={_process.ExitCode}");
            }
            if (!_reportedNoFrames && _processColumn < 0 && SecondsSince(now, _startedAt) >= 5)
            {
                _reportedNoFrames = true;
                Log("PresentMon: no se recibieron filas CSV en cinco segundos.");
            }
            Prune(now);
            if (_targetPid is int currentPid)
            {
                if (!IsRunning(currentPid))
                    ClearTarget("proceso terminado");
                else if (FindChain(currentPid, now, false) is null &&
                         SecondsSince(now, _lastValueAt) > GraceSeconds)
                    ClearTarget("sin fotogramas visibles");
            }

            Chain? foreground = FindChain(foregroundPid, now, true);
            if (foreground is not null && foreground.Pid != _targetPid)
            {
                if (_targetPid is null)
                    Select(foreground, "ventana en primer plano");
                else if (_foregroundCandidatePid != foreground.Pid)
                {
                    _foregroundCandidatePid = foreground.Pid;
                    _foregroundCandidateSince = now;
                }
                else if (SecondsSince(now, _foregroundCandidateSince) >= 0.5)
                    Select(foreground, "nuevo proceso gráfico en primer plano");
            }
            else
                _foregroundCandidatePid = null;

            if (_targetPid is null)
            {
                Chain? fallback = _chains.Values
                    .Where(c => IsCandidate(c, now, true))
                    .OrderByDescending(c => c.Displayed.Count)
                    .ThenByDescending(c => c.LastDisplayedAt)
                    .FirstOrDefault();
                if (fallback is not null) Select(fallback, "actividad gráfica de respaldo");
            }

            if (_targetPid is not int pid) return null;
            Chain? chain = FindChain(pid, now, false);
            if (chain is not null)
            {
                if (_targetSwapChain != chain.SwapChain)
                {
                    _targetSwapChain = chain.SwapChain;
                    Log($"PresentMon: swap chain seleccionada PID={pid}, dirección={chain.SwapChain}");
                }

                double? displayed = AverageFps(chain.Displayed);
                if (displayed is > 0 && chain.LastDisplayedAt > _lastValueAt)
                {
                    _lastDisplayedFps = displayed;
                    _lastValueAt = chain.LastDisplayedAt;
                }

                if (SecondsSince(now, _lastLoggedAt) >= 2 && _lastDisplayedFps is not null)
                {
                    _lastLoggedAt = now;
                    Log($"PresentMon: PID={pid}, proceso={chain.Name}, " +
                        $"Displayed={Format(_lastDisplayedFps)}, " +
                        $"Presented={Format(AverageFps(chain.Presented))}, " +
                        $"Application≈{Format(AverageFps(chain.Application))}, " +
                        $"última muestra={SecondsSince(now, _lastValueAt):F2}s");
                }
            }

            return _lastDisplayedFps is not null &&
                   SecondsSince(now, _lastValueAt) <= GraceSeconds
                ? _lastDisplayedFps : null;
        }
    }

    private void AcceptLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        List<string> values = ParseCsv(line);
        lock (_gate)
        {
            if (_processColumn < 0)
            {
                _processColumn = Column(values, "ProcessID");
                _applicationColumn = Column(values, "Application");
                _swapChainColumn = Column(values, "SwapChainAddress");
                _displayedColumn = Column(values, "MsBetweenDisplayChange");
                _displayedTimeColumn = Column(values, "DisplayedTime");
                _presentedColumn = Column(values, "MsBetweenPresents");
                _applicationFpsColumn = Column(values, "MsBetweenAppStart");
                if (_processColumn >= 0)
                    Log($"PresentMon: columnas Displayed={_displayedColumn}, Presented={_presentedColumn}, Application={_applicationFpsColumn}");
                if (_processColumn >= 0 && _displayedColumn < 0)
                    Log("PresentMon: el CSV no contiene MsBetweenDisplayChange.");
                return;
            }

            if (_processColumn >= values.Count || !int.TryParse(values[_processColumn], out int pid))
                return;
            string name = Value(values, _applicationColumn);
            if (ExcludedApplications.Contains(name)) return;
            string swap = Value(values, _swapChainColumn);
            if (string.IsNullOrEmpty(swap)) return;
            var key = (pid, swap);
            if (!_chains.TryGetValue(key, out Chain? chain))
                _chains[key] = chain = new Chain(pid, name, swap);

            long now = Stopwatch.GetTimestamp();
            Add(chain.Presented, Value(values, _presentedColumn), now);
            Add(chain.Application, Value(values, _applicationFpsColumn), now);
            if (Value(values, _displayedTimeColumn).Equals("NA", StringComparison.OrdinalIgnoreCase))
                return;
            if (Add(chain.Displayed, Value(values, _displayedColumn), now))
                chain.LastDisplayedAt = now;
        }
    }

    private Chain? FindChain(int pid, long now, bool requireCandidate)
    {
        if (pid <= 0) return null;
        Chain? current = _chains.Values.FirstOrDefault(c =>
            c.Pid == pid && c.SwapChain == _targetSwapChain &&
            IsCandidate(c, now, false));
        if (!requireCandidate && current is not null) return current;
        return _chains.Values
            .Where(c => c.Pid == pid && IsCandidate(c, now, requireCandidate))
            .OrderByDescending(c => c.Displayed.Count)
            .ThenByDescending(c => c.LastDisplayedAt)
            .FirstOrDefault();
    }

    private static bool IsCandidate(Chain chain, long now, bool requireActivity) =>
        !ExcludedApplications.Contains(chain.Name) &&
        chain.Displayed.Count >= (requireActivity ? 3 : 1) &&
        SecondsSince(now, chain.LastDisplayedAt) <= (requireActivity ? 0.6 : GraceSeconds) &&
        (!requireActivity || AverageFps(chain.Displayed) is >= 10);

    private void Select(Chain chain, string reason)
    {
        _targetPid = chain.Pid;
        _targetSwapChain = chain.SwapChain;
        _foregroundCandidatePid = null;
        _lastDisplayedFps = null;
        _lastValueAt = 0;
        Log($"PresentMon: PID={chain.Pid}, proceso={chain.Name}; motivo={reason}");
    }

    private void ClearTarget(string reason)
    {
        Log($"PresentMon: PID={_targetPid}; motivo del cambio={reason}");
        _targetPid = null;
        _targetSwapChain = null;
        _lastDisplayedFps = null;
        _lastValueAt = 0;
    }

    private void Prune(long now)
    {
        long cutoff = now - (long)(Stopwatch.Frequency * HistorySeconds);
        foreach (Chain chain in _chains.Values)
        {
            PruneQueue(chain.Displayed, cutoff);
            PruneQueue(chain.Presented, cutoff);
            PruneQueue(chain.Application, cutoff);
        }
        foreach (var key in _chains.Where(kv => kv.Value.Displayed.Count == 0 &&
                     kv.Value.Presented.Count == 0 && kv.Value.Application.Count == 0)
                     .Select(kv => kv.Key).ToList())
            _chains.Remove(key);
    }

    private static void PruneQueue(Queue<Interval> queue, long cutoff)
    {
        while (queue.Count > 0 && queue.Peek().At < cutoff) queue.Dequeue();
    }

    private static bool Add(Queue<Interval> queue, string text, long now)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ||
            !double.IsFinite(ms) || ms <= 0 || ms > 1000)
            return false;
        queue.Enqueue(new Interval(now, ms));
        return true;
    }

    private static double? AverageFps(Queue<Interval> intervals)
    {
        if (intervals.Count < 2) return null;
        double totalMs = intervals.Sum(x => x.Milliseconds);
        return totalMs > 0 ? 1000 * intervals.Count / totalMs : null;
    }

    private static string Format(double? fps) =>
        fps is double value ? value.ToString("F1", CultureInfo.InvariantCulture) : "--";

    private static double SecondsSince(long now, long then) =>
        then == 0 ? double.PositiveInfinity : (now - then) / (double)Stopwatch.Frequency;

    private static int Column(List<string> values, string name) =>
        values.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Value(List<string> values, int column) =>
        column >= 0 && column < values.Count ? values[column] : "";

    private static int ForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint pid);
        return pid <= int.MaxValue ? (int)pid : 0;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Log(string message)
    {
        Debug.WriteLine(message);
        if (string.IsNullOrWhiteSpace(_diagnosticPath)) return;
        try
        {
            lock (_gate)
                File.AppendAllText(_diagnosticPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static List<string> ParseCsv(string line)
    {
        var result = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { result.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        result.Add(field.ToString());
        if (result.Count > 0) result[0] = result[0].TrimStart('\uFEFF');
        return result;
    }

    public void Dispose()
    {
        if (_process is null) return;
        try
        {
            using Process? stop = Process.Start(new ProcessStartInfo(
                Path.Combine(AppContext.BaseDirectory, "PresentMon.exe"))
            {
                Arguments = $"--session_name {SessionName} --terminate_existing_session",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            stop?.WaitForExit(2000);
        }
        catch (Exception ex) { Log($"PresentMon no pudo cerrar la sesión ETW: {ex.Message}"); }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        _process = null;
    }

    private sealed record Interval(long At, double Milliseconds);

    private sealed class Chain(int pid, string name, string swapChain)
    {
        public int Pid { get; } = pid;
        public string Name { get; } = name;
        public string SwapChain { get; } = swapChain;
        public Queue<Interval> Displayed { get; } = new();
        public Queue<Interval> Presented { get; } = new();
        public Queue<Interval> Application { get; } = new();
        public long LastDisplayedAt { get; set; }
    }
}
