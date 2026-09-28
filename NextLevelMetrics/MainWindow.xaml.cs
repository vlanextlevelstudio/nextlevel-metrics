using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Globalization;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace NextLevelMetrics;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _temperatureTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Computer? _computer;
    private bool _cpuReady;
    private bool _fpsReady;
    private double? _fps;
    private long _lastFpsAt;
    private double? _cpuTemperature;
    private float? _gpuTemperature;
    private float? _hotspotTemperature;

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AbrirCpu();

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LeerTemperaturaCpu(out double temperature);

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CerrarCpu();

    [DllImport("NextLevelFpsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AbrirFps();

    [DllImport("NextLevelFpsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LeerFps(out int fps);

    [DllImport("NextLevelFpsBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CerrarFps();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        _temperatureTimer.Tick += (_, _) => RefreshReadings();
        _fpsTimer.Tick += (_, _) => RefreshFps();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { _cpuReady = AbrirCpu() == 0; }
        catch (Exception ex) { Debug.WriteLine($"No se pudo iniciar el SDK de AMD: {ex.Message}"); }

        try
        {
            _computer = new Computer
            {
                IsGpuEnabled = true
            };
            _computer.Open();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No se pudieron iniciar los sensores GPU: {ex.Message}");
            _computer = null;
        }

        try { _fpsReady = AbrirFps() == 0; }
        catch (Exception ex) { Debug.WriteLine($"No se pudo iniciar ADLX para FPS: {ex.Message}"); }
        RefreshReadings();
        _temperatureTimer.Start();
        _fpsTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _temperatureTimer.Stop();
        _fpsTimer.Stop();
        if (_fpsReady) CerrarFps();
        _computer?.Close();
        if (_cpuReady) CerrarCpu();
    }

    private void RefreshReadings()
    {
        _cpuTemperature = null;
        if (_cpuReady)
        {
            try
            {
                if (LeerTemperaturaCpu(out double value) == 0 && double.IsFinite(value) && value > 0)
                    _cpuTemperature = value;
            }
            catch (Exception ex) { Debug.WriteLine($"Error al leer la CPU: {ex.Message}"); }
        }

        var readings = new List<TemperatureReading>();
        var errors = new List<string>();
        foreach (IHardware hardware in _computer?.Hardware ?? [])
        {
            if (IsGpu(hardware.HardwareType))
                CollectTemperatures(hardware, hardware, readings, errors);
        }

        var gpuReadings = readings.Where(r => IsGpu(r.Root.HardwareType)).ToList();
        TemperatureReading? gpu = FindNamed(gpuReadings,
            "GPU Core", "GPU Temperature", "GPU Core Temperature");
        IHardware? selectedGpu = gpu?.Root ?? gpuReadings
            .FirstOrDefault(r => IsHotspot(r.Sensor.Name) && HasReading(r.Sensor))?.Root;
        TemperatureReading? hotspot = selectedGpu is null ? null : gpuReadings
            .FirstOrDefault(r => ReferenceEquals(r.Root, selectedGpu)
                && IsHotspot(r.Sensor.Name) && HasReading(r.Sensor));

        _gpuTemperature = gpu?.Sensor.Value;
        _hotspotTemperature = hotspot?.Sensor.Value;
        foreach (string error in errors) Debug.WriteLine($"Sensor GPU: {error}");
        RenderLine();
        Debug.WriteLine(MetricsText.Text);
    }

    private void RenderLine()
    {
        MetricsText.Text = $"FPS {FormatFps(_fps)} | CPU {FormatTemperature(_cpuTemperature)} | " +
            $"GPU {FormatTemperature(_gpuTemperature)} - {FormatTemperature(_hotspotTemperature)}";
    }

    private void RefreshFps()
    {
        if (_fpsReady)
        {
            try
            {
                int result = LeerFps(out int value);
                if (result == 0 && value > 0)
                {
                    _fps = value;
                    _lastFpsAt = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"Error al leer FPS de ADLX: {ex.Message}"); }
        }

        if (_fps is not null &&
            Stopwatch.GetElapsedTime(_lastFpsAt) > TimeSpan.FromSeconds(3))
            _fps = null;
        RenderLine();
    }

    private static string FormatFps(double? value) => value is > 0 and < double.PositiveInfinity
        ? Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)
        : "--";

    private static string FormatTemperature(double? value) => value is > 0 and < double.PositiveInfinity
        ? $"{Math.Round(value.Value, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)}°"
        : "--";

    private static string FormatTemperature(float? value) =>
        FormatTemperature(value is float number ? (double)number : null);

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

    private sealed record TemperatureReading(IHardware Root, IHardware Hardware, ISensor Sensor);
}
