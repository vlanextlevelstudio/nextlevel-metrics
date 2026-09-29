using System.Windows;

namespace NextLevelMetrics;

public partial class App : System.Windows.Application
{
    private RtssSharedMemoryClient? _rtss;
    private MetricsRuntime? _runtime;
    private TrayController? _tray;
    private ConfiguracionWindow? _configurationWindow;
    private OverlaySettings _savedSettings = new();
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\NextLevelMetrics.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            base.OnStartup(e);
            Shutdown();
            return;
        }
        _ownsMutex = true;

        if (e.Args.Length > 0 &&
            (e.Args[0] == "--rtss-static" || e.Args[0] == "--rtss-fps"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            _ = RunRtssProbeAsync(e.Args[0] == "--rtss-fps");
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        _savedSettings = OverlaySettingsStore.Load();
        SincronizarInicioConWindows();
        _runtime = new MetricsRuntime(_savedSettings);
        _tray = new TrayController(_runtime, ShowConfiguration, ExitFromTray);
        _ = _runtime.RunAsync();
    }

    private void ShowConfiguration()
    {
        if (_configurationWindow is not null)
        {
            if (_configurationWindow.WindowState == WindowState.Minimized)
                _configurationWindow.WindowState = WindowState.Normal;
            _configurationWindow.Activate();
            return;
        }

        SincronizarInicioConWindows();
        _configurationWindow = new ConfiguracionWindow(_savedSettings,
            settings => _runtime?.SetSettings(settings),
            settings =>
            {
                bool inicioAnterior = InicioConWindows.EstaActivo();
                InicioConWindows.Establecer(settings.IniciarConWindows);
                try { OverlaySettingsStore.Save(settings); }
                catch
                {
                    if (inicioAnterior != settings.IniciarConWindows)
                    {
                        try { InicioConWindows.Establecer(inicioAnterior); }
                        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"No se pudo restaurar el inicio con Windows: {ex}"); }
                    }
                    throw;
                }
                _savedSettings = settings.Clone();
                _runtime?.SetSettings(settings);
            });
        _configurationWindow.Closed += (_, _) => _configurationWindow = null;
        _configurationWindow.Show();
        _configurationWindow.Activate();
    }

    private void SincronizarInicioConWindows()
    {
        try
        {
            bool activo = InicioConWindows.EstaActivo();
            if (_savedSettings.IniciarConWindows == activo) return;
            _savedSettings.IniciarConWindows = activo;
            OverlaySettingsStore.Save(_savedSettings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"No se pudo sincronizar el inicio con Windows: {ex}");
        }
    }

    private void ExitFromTray()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _configurationWindow?.Close();
        _runtime?.Dispose();
        _tray?.Dispose();
        _runtime?.CloseOwnedRtss();
        _rtss?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private async Task RunRtssProbeAsync(bool showFps)
    {
        string logPath = System.IO.Path.Combine(
            Environment.CurrentDirectory, "externos", "rtss-poc.log");
        void Log(string message) => System.IO.File.AppendAllText(
            logPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");

        try
        {
            Log(showFps ? "START FPS" : "START STATIC");
            _rtss = new RtssSharedMemoryClient();
            int slot = _rtss.AcquireOsdSlot();
            Log($"RTSS version=0x{_rtss.Version:X8} slot={slot}");
            _rtss.WriteOsd(showFps
                ? "FPS -- | CPU -- | GPU -- - --"
                : "NEXT LEVEL METRICS | TEST");

            for (int tick = 0; tick < 180; tick++)
            {
                RtssGameSample? game = _rtss.ReadGame(
                    "BeastOfReincarnation-Win64-Shipping.exe", showFps);
                if (tick % 4 == 0)
                    Log(game is null
                        ? "game=not detected"
                        : $"game={System.IO.Path.GetFileName(game.Name)} pid={game.Pid} " +
                          $"frames={game.Frames} time0={game.Time0} time1={game.Time1} " +
                          $"fps={(game.Fps?.ToString("F1") ?? "--")}");

                if (showFps)
                    _rtss.WriteOsd($"FPS {(game?.Fps is double fps ? fps.ToString("F0") : "--")} | CPU -- | GPU -- - --");

                await Task.Delay(250);
            }

            Log("END");
        }
        catch (Exception ex)
        {
            try { Log($"ERROR {ex}"); } catch { }
        }
        finally
        {
            _rtss?.Dispose();
            _rtss = null;
            Shutdown();
        }
    }
}
