using System.Globalization;
using System.Windows;
using Forms = System.Windows.Forms;
using WpfTextBox = System.Windows.Controls.TextBox;
using Media = System.Windows.Media;

namespace NextLevelMetrics;

public partial class ConfiguracionWindow : Window
{
    private readonly OverlaySettings _original;
    private readonly Action<OverlaySettings> _preview;
    private readonly Action<OverlaySettings> _save;
    private bool _loading;
    private bool _saved;

    internal ConfiguracionWindow(OverlaySettings current,
        Action<OverlaySettings> preview, Action<OverlaySettings> save)
    {
        InitializeComponent();
        _original = current.Clone();
        _preview = preview;
        _save = save;
        FontCombo.ItemsSource = OverlaySettings.AvailableFonts;
        Populate(current);

        foreach (WpfTextBox box in AllTextBoxes())
            box.TextChanged += (_, _) => OnChanged();
        SizeSlider.ValueChanged += (_, _) => OnChanged();
        PositionCombo.SelectionChanged += (_, _) => OnChanged();
        FontCombo.SelectionChanged += (_, _) => OnChanged();
        BackgroundCombo.SelectionChanged += (_, _) => OnChanged();
        AlertsCheck.Checked += (_, _) => OnChanged();
        AlertsCheck.Unchecked += (_, _) => OnChanged();
        PickColorButton.Click += (_, _) => PickColor();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
        RestoreButton.Click += (_, _) => RestoreDefaults();
    }

    private IEnumerable<WpfTextBox> AllTextBoxes() =>
        [ColorText, CpuWarningText, CpuCriticalText, GpuWarningText,
         GpuCriticalText, HotspotWarningText, HotspotCriticalText];

    private void Populate(OverlaySettings settings)
    {
        _loading = true;
        ColorText.Text = settings.ColorPrincipal;
        SizeSlider.Value = settings.TamanoPorcentaje;
        SizeLabel.Text = $"{settings.TamanoPorcentaje} %";
        PositionCombo.SelectedIndex = (int)settings.Posicion;
        FontCombo.SelectedItem = settings.Fuente;
        BackgroundCombo.SelectedIndex = (int)settings.Fondo;
        AlertsCheck.IsChecked = settings.AvisosPorColor;
        CpuWarningText.Text = settings.CpuAviso.ToString(CultureInfo.InvariantCulture);
        CpuCriticalText.Text = settings.CpuCritico.ToString(CultureInfo.InvariantCulture);
        GpuWarningText.Text = settings.GpuAviso.ToString(CultureInfo.InvariantCulture);
        GpuCriticalText.Text = settings.GpuCritico.ToString(CultureInfo.InvariantCulture);
        HotspotWarningText.Text = settings.HotspotAviso.ToString(CultureInfo.InvariantCulture);
        HotspotCriticalText.Text = settings.HotspotCritico.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        UpdateColorPreview();
        StatusText.Text = "";
    }

    private void OnChanged()
    {
        if (_loading) return;
        SizeLabel.Text = $"{(int)SizeSlider.Value} %";
        UpdateColorPreview();
        if (TryRead(out OverlaySettings settings, out string error))
        {
            StatusText.Text = "";
            try { _preview(settings); }
            catch { StatusText.Text = "No se pudo actualizar la vista previa."; }
        }
        else
            StatusText.Text = error;
    }

    private void UpdateColorPreview()
    {
        try
        {
            var color = (Media.Color)Media.ColorConverter.ConvertFromString(ColorText.Text)!;
            ColorPreview.Background = new Media.SolidColorBrush(color);
        }
        catch { ColorPreview.Background = Media.Brushes.Transparent; }
    }

    private static bool ReadNumber(WpfTextBox box, string label, out int value, out string error)
    {
        if (int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            error = "";
            return true;
        }
        error = $"Introduce un número entero válido para {label}.";
        return false;
    }

    private bool TryRead(out OverlaySettings settings, out string error)
    {
        settings = new OverlaySettings();
        if (!ReadNumber(CpuWarningText, "el aviso de CPU", out int cpuWarning, out error) ||
            !ReadNumber(CpuCriticalText, "el crítico de CPU", out int cpuCritical, out error) ||
            !ReadNumber(GpuWarningText, "el aviso de GPU", out int gpuWarning, out error) ||
            !ReadNumber(GpuCriticalText, "el crítico de GPU", out int gpuCritical, out error) ||
            !ReadNumber(HotspotWarningText, "el aviso de hotspot", out int hotspotWarning, out error) ||
            !ReadNumber(HotspotCriticalText, "el crítico de hotspot", out int hotspotCritical, out error))
            return false;

        settings.ColorPrincipal = ColorText.Text.Trim().ToUpperInvariant();
        settings.TamanoPorcentaje = (int)SizeSlider.Value;
        settings.Posicion = (OverlayPosition)PositionCombo.SelectedIndex;
        settings.Fuente = FontCombo.SelectedItem as string ?? "";
        settings.Fondo = (OverlayBackground)BackgroundCombo.SelectedIndex;
        settings.AvisosPorColor = AlertsCheck.IsChecked == true;
        settings.CpuAviso = cpuWarning;
        settings.CpuCritico = cpuCritical;
        settings.GpuAviso = gpuWarning;
        settings.GpuCritico = gpuCritical;
        settings.HotspotAviso = hotspotWarning;
        settings.HotspotCritico = hotspotCritical;
        error = settings.Validate() ?? "";
        return error.Length == 0;
    }

    private void PickColor()
    {
        using var dialog = new Forms.ColorDialog { AnyColor = true, FullOpen = true };
        try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(ColorText.Text); }
        catch { dialog.Color = System.Drawing.Color.White; }
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            ColorText.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private void Save()
    {
        if (!TryRead(out OverlaySettings settings, out string error))
        {
            StatusText.Text = error;
            return;
        }
        try
        {
            _save(settings);
            _saved = true;
            Close();
        }
        catch
        {
            StatusText.Text = "No se pudo guardar la configuración. Comprueba que tienes acceso a tu carpeta de usuario.";
        }
    }

    private void RestoreDefaults()
    {
        MessageBoxResult choice = System.Windows.MessageBox.Show(this,
            "¿Quieres restaurar los valores predeterminados? Los cambios se guardarán solo si pulsas Guardar.",
            "Restaurar valores predeterminados", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        Populate(new OverlaySettings());
        OnChanged();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_saved) _preview(_original);
        base.OnClosed(e);
    }
}
