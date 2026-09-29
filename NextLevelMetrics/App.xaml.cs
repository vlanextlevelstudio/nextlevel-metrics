using System.Windows;

namespace NextLevelMetrics;

public partial class App : Application
{
    private RtssSharedMemoryClient? _rtss;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 &&
            (e.Args[0] == "--rtss-static" || e.Args[0] == "--rtss-fps"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            _ = RunRtssProbeAsync(e.Args[0] == "--rtss-fps");
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
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
