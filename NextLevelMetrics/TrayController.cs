using Forms = System.Windows.Forms;

namespace NextLevelMetrics;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _automatic;
    private readonly Forms.ToolStripMenuItem _show;
    private readonly Forms.ToolStripMenuItem _hide;
    private readonly MetricsRuntime _runtime;

    public TrayController(MetricsRuntime runtime, Action showConfiguration, Action exit)
    {
        _runtime = runtime;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(new Forms.ToolStripMenuItem("Next Level Metrics") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _automatic = new Forms.ToolStripMenuItem("Overlay automático");
        _show = new Forms.ToolStripMenuItem("Mostrar siempre");
        _hide = new Forms.ToolStripMenuItem("Ocultar overlay");
        _automatic.Click += (_, _) => SetMode(OverlayMode.Automatico);
        _show.Click += (_, _) => SetMode(OverlayMode.MostrarSiempre);
        _hide.Click += (_, _) => SetMode(OverlayMode.Oculto);
        _menu.Items.Add(_automatic);
        _menu.Items.Add(_show);
        _menu.Items.Add(_hide);

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var configuration = new Forms.ToolStripMenuItem("Configuración...");
        configuration.Click += (_, _) => showConfiguration();
        _menu.Items.Add(configuration);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Salir");
        quit.Click += (_, _) => exit();
        _menu.Items.Add(quit);

        _icon = new Forms.NotifyIcon
        {
            Text = "Next Level Metrics",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true
        };
        SetMode(OverlayMode.Automatico);
    }

    private void SetMode(OverlayMode mode)
    {
        _runtime.SetOverlayMode(mode);
        _automatic.Checked = mode == OverlayMode.Automatico;
        _show.Checked = mode == OverlayMode.MostrarSiempre;
        _hide.Checked = mode == OverlayMode.Oculto;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
