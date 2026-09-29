using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NextLevelMetrics;

internal enum OverlayMode { Automatico, MostrarSiempre, Oculto }

internal sealed class MetricsRuntime : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly FpsTracker _fps = new();
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "metrics-runtime.log");
    private RtssSharedMemoryClient? _rtss;
    private Process? _rtssStartedByUs;
    private DateTime _rtssStartTime;
    private string? _rtssStartedPath;
    private TemperatureReader? _temperatures;
    private DesktopOverlayWindow? _desktopWindow;
    private uint _gamePid;
    private long _lastTemperatures;
    private long _lastLog;
    private bool _disposed;
    private OverlayMode _mode = OverlayMode.Automatico;

    public void SetOverlayMode(OverlayMode mode)
    {
        _mode = mode;
        if (mode != OverlayMode.MostrarSiempre)
            _desktopWindow?.Hide();
        if (mode == OverlayMode.Oculto)
        {
            try { _rtss?.WriteOsd(""); } catch (Exception ex) { Log($"ERROR hide OSD: {ex.Message}"); }
        }
        Log($"overlay mode={mode}");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private const uint WmClose = 0x0010;

    private void Log(string message)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    public async Task RunAsync()
    {
        Log("START integrated RTSS");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_rtss is null)
                {
                    if (EnsureRtssRunning())
                        await Task.Delay(2000, _stop.Token);
                    _rtss = new RtssSharedMemoryClient();
                    int slot = _rtss.AcquireOsdSlot();
                    _rtss.WriteOsd("");
                    Log($"RTSS ready version=0x{_rtss.Version:X8} slot={slot}");
                }

                _temperatures ??= new TemperatureReader();
                if (_lastTemperatures == 0 ||
                    Stopwatch.GetElapsedTime(_lastTemperatures) >= TimeSpan.FromSeconds(1))
                {
                    _temperatures.Refresh();
                    _lastTemperatures = Stopwatch.GetTimestamp();
                }

                uint foregroundPid = ForegroundProcessId();
                RtssGameSample? game = _mode == OverlayMode.Oculto
                    ? null
                    : _rtss.ReadProcess(foregroundPid);
                if (game is null)
                {
                    if (_gamePid != 0) Log("foreground game left; OSD cleared");
                    _gamePid = 0;
                    _fps.Reset();
                    _rtss.WriteOsd("");
                    if (_mode == OverlayMode.MostrarSiempre)
                    {
                        _desktopWindow ??= new DesktopOverlayWindow();
                        _desktopWindow.SetMetrics($"FPS -- | CPU {FormatTemperature(_temperatures.Cpu)} | " +
                            $"GPU {FormatTemperature(_temperatures.Gpu)} - {FormatTemperature(_temperatures.Hotspot)}");
                        if (!_desktopWindow.IsVisible) _desktopWindow.Show();
                    }
                    if (_lastLog == 0 || Stopwatch.GetElapsedTime(_lastLog) >= TimeSpan.FromSeconds(1))
                    {
                        Log($"idle foregroundPid={foregroundPid} cpu={FormatTemperature(_temperatures.Cpu)} " +
                            $"gpu={FormatTemperature(_temperatures.Gpu)} hotspot={FormatTemperature(_temperatures.Hotspot)}");
                        _lastLog = Stopwatch.GetTimestamp();
                    }
                }
                else
                {
                    _desktopWindow?.Hide();
                    if (_gamePid != game.Pid)
                    {
                        _gamePid = game.Pid;
                        _fps.Reset();
                        Log($"foreground game={Path.GetFileName(game.Name)} pid={game.Pid}");
                    }

                    double? fps = _fps.Update(game);
                    string text = $"FPS {FormatFps(fps)} | CPU {FormatTemperature(_temperatures.Cpu)} | " +
                        $"GPU {FormatTemperature(_temperatures.Gpu)} - {FormatTemperature(_temperatures.Hotspot)}";
                    _rtss.WriteOsd(text);

                    if (_lastLog == 0 || Stopwatch.GetElapsedTime(_lastLog) >= TimeSpan.FromSeconds(1))
                    {
                        Log($"pid={game.Pid} rawFps={game.Fps?.ToString("F1", CultureInfo.InvariantCulture) ?? "--"} " +
                            $"text={text}");
                        _lastLog = Stopwatch.GetTimestamp();
                    }
                }

                await Task.Delay(250, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
                _desktopWindow?.Hide();
                try { _rtss?.Dispose(); } catch { }
                _rtss = null;
                _gamePid = 0;
                _fps.Reset();
                try { await Task.Delay(1000, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        Dispose();
    }

    private bool EnsureRtssRunning()
    {
        Process[] processes = Process.GetProcessesByName("RTSS");
        bool running = processes.Length > 0;
        foreach (Process existing in processes) existing.Dispose();
        if (running) return false;

        string path = FindRtssPath() ?? throw new FileNotFoundException("RTSS.exe no está instalado.");
        var start = new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process? process = Process.Start(start);
        if (process is null) throw new InvalidOperationException("RTSS no pudo iniciarse.");
        _rtssStartedByUs = process;
        _rtssStartTime = process.StartTime;
        _rtssStartedPath = Path.GetFullPath(path);
        Log($"RTSS iniciado por Next Level Metrics pid={process.Id}");
        return true;
    }

    public void CloseOwnedRtss()
    {
        Process? process = _rtssStartedByUs;
        _rtssStartedByUs = null;
        if (process is null) return;

        try
        {
            process.Refresh();
            if (process.HasExited) return;
            string? currentPath = process.MainModule?.FileName;
            if (!string.Equals(process.ProcessName, "RTSS", StringComparison.OrdinalIgnoreCase) ||
                process.StartTime != _rtssStartTime ||
                currentPath is null || _rtssStartedPath is null ||
                !string.Equals(Path.GetFullPath(currentPath), _rtssStartedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                Log($"RTSS pid={process.Id} no coincide con la instancia iniciada; se conserva.");
                return;
            }

            bool closeRequested = process.CloseMainWindow();
            if (!closeRequested)
            {
                uint ownedPid = (uint)process.Id;
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out uint windowPid);
                    if (windowPid == ownedPid)
                        closeRequested |= PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            }

            if (!closeRequested)
            {
                Log($"RTSS pid={process.Id}: no se encontró una ventana para solicitar el cierre.");
                return;
            }

            if (process.WaitForExit(5000))
                Log($"RTSS pid={process.Id} cerrado correctamente.");
            else
                Log($"RTSS pid={process.Id} no respondió al cierre normal; se conserva.");
        }
        catch (Exception ex)
        {
            Log($"No se pudo cerrar el RTSS iniciado por Next Level Metrics: {ex.Message}");
        }
        finally { process.Dispose(); }
    }

    private static string? FindRtssPath()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = root.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RTSS");
            string? location = key?.GetValue("InstallLocation") as string;
            string? icon = key?.GetValue("DisplayIcon") as string;
            string? directory = !string.IsNullOrWhiteSpace(location)
                ? location
                : !string.IsNullOrWhiteSpace(icon)
                    ? Path.GetDirectoryName(icon.Trim('"').Split(',')[0])
                    : null;
            if (directory is not null)
            {
                string candidate = Path.Combine(directory, "RTSS.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        })
        {
            string candidate = Path.Combine(root, "RivaTuner Statistics Server", "RTSS.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static uint ForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint pid);
        return pid;
    }

    private static string FormatFps(double? value) => value is > 0 and < double.PositiveInfinity
        ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)
        : "--";

    private static string FormatTemperature(double? value) => value is > 0 and < double.PositiveInfinity
        ? $"{Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)}°"
        : "--";

    private static string FormatTemperature(float? value) =>
        FormatTemperature(value is float number ? (double)number : null);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _desktopWindow?.Close();
        _desktopWindow = null;
        try { _rtss?.Dispose(); } catch { }
        _rtss = null;
        try { _temperatures?.Dispose(); } catch { }
        _temperatures = null;
        Log("STOP");
        _stop.Dispose();
    }

    private sealed class FpsTracker
    {
        private double? _last;
        private double? _pending;
        private uint _lastTime1;
        private long _lastValidAt;

        public double? Update(RtssGameSample game)
        {
            if (game.Time1 != _lastTime1)
            {
                _lastTime1 = game.Time1;
                double? candidate = game.Fps;
                if (candidate is > 1 and < 1000)
                {
                    if (_last is null || candidate >= _last * 0.5 && candidate <= _last * 1.5)
                    {
                        _last = candidate;
                        _pending = null;
                        _lastValidAt = Stopwatch.GetTimestamp();
                    }
                    else if (_pending is double previous &&
                             Math.Abs(candidate.Value - previous) / previous <= 0.15)
                    {
                        _last = candidate;
                        _pending = null;
                        _lastValidAt = Stopwatch.GetTimestamp();
                    }
                    else
                    {
                        _pending = candidate;
                    }
                }
            }

            return _last is not null &&
                   Stopwatch.GetElapsedTime(_lastValidAt) <= TimeSpan.FromSeconds(3)
                ? _last
                : null;
        }

        public void Reset()
        {
            _last = null;
            _pending = null;
            _lastTime1 = 0;
            _lastValidAt = 0;
        }
    }
}
