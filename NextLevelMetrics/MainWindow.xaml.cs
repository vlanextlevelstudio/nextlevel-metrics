using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Globalization;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace NextLevelMetrics;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Computer? _computer;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _timer.Tick += (_, _) => RefreshReadings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true
            };
            _computer.Open();
            RefreshReadings();
            _timer.Start();
        }
        catch (Exception ex)
        {
            SensorDiagnosticsText.Text = $"No se pudieron iniciar los sensores: {ex.Message}";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _computer?.Close();
    }

    private void RefreshReadings()
    {
        if (_computer is null)
            return;

        var readings = new List<TemperatureReading>();
        var errors = new List<string>();
        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu || IsGpu(hardware.HardwareType))
                CollectTemperatures(hardware, hardware, readings, errors);
        }

        var cpuReadings = readings.Where(r => r.Root.HardwareType == HardwareType.Cpu).ToList();
        var gpuReadings = readings.Where(r => IsGpu(r.Root.HardwareType)).ToList();
        TemperatureReading? cpu = FindNamed(cpuReadings,
            "CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core (Tdie)");
        TemperatureReading? gpu = FindNamed(gpuReadings,
            "GPU Core", "GPU Temperature", "GPU Core Temperature");
        IHardware? selectedGpu = gpu?.Root ?? gpuReadings
            .FirstOrDefault(r => IsHotspot(r.Sensor.Name) && HasReading(r.Sensor))?.Root;
        TemperatureReading? hotspot = selectedGpu is null ? null : gpuReadings
            .FirstOrDefault(r => ReferenceEquals(r.Root, selectedGpu)
                && IsHotspot(r.Sensor.Name) && HasReading(r.Sensor));

        CpuTemperatureText.Text = $"CPU: {DisplayTemperature(cpu)}";
        GpuTemperatureText.Text = $"GPU: {DisplayTemperature(gpu)}";
        HotspotTemperatureText.Text = $"HOTSPOT: {DisplayTemperature(hotspot)}";

        var lines = new List<string> { $"Actualizado: {DateTime.Now:HH:mm:ss}" };
        foreach (TemperatureReading reading in readings)
        {
            string value = reading.Sensor.Value is float temperature
                ? $"{temperature.ToString("F1", CultureInfo.InvariantCulture)} °C"
                : "No disponible";
            lines.Add($"{reading.Hardware.HardwareType} | {reading.Hardware.Name} | " +
                $"{reading.Sensor.Name} | {value} | {reading.Sensor.Identifier}");
        }
        if (readings.Count == 0)
            lines.Add("No se encontraron sensores de temperatura CPU/GPU.");
        lines.AddRange(errors.Select(error => $"Error: {error}"));
        SensorDiagnosticsText.Text = string.Join(Environment.NewLine, lines);
        System.Diagnostics.Debug.WriteLine(SensorDiagnosticsText.Text);
    }

    private static void CollectTemperatures(IHardware root, IHardware hardware,
        List<TemperatureReading> readings, List<string> errors)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            errors.Add($"{hardware.Name}: {ex.Message}");
        }

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature)
                readings.Add(new TemperatureReading(root, hardware, sensor));
        }
        foreach (IHardware child in hardware.SubHardware)
            CollectTemperatures(root, child, readings, errors);
    }

    private static TemperatureReading? FindNamed(IEnumerable<TemperatureReading> readings,
        params string[] names) => names.Select(name => readings.FirstOrDefault(r =>
            string.Equals(r.Sensor.Name, name, StringComparison.OrdinalIgnoreCase)
            && HasReading(r.Sensor))).FirstOrDefault(r => r is not null);

    private static bool IsGpu(HardwareType type) => type is
        HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel;

    private static bool IsHotspot(string name) =>
        !name.Contains("Memory", StringComparison.OrdinalIgnoreCase) &&
        (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Junction", StringComparison.OrdinalIgnoreCase));

    private static bool HasReading(ISensor sensor) =>
        sensor.Value is float value && float.IsFinite(value) && value > 0;

    private static string DisplayTemperature(TemperatureReading? reading) =>
        reading is not null && reading.Sensor.Value is float value
            ? $"{value.ToString("F1", CultureInfo.InvariantCulture)} °C"
            : "No disponible";

    private sealed record TemperatureReading(IHardware Root, IHardware Hardware, ISensor Sensor);
}
