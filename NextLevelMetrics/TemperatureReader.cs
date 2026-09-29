using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace NextLevelMetrics;

internal sealed class TemperatureReader : IDisposable
{
    private Computer? _computer;
    private bool _cpuReady;

    public double? Cpu { get; private set; }
    public float? Gpu { get; private set; }
    public float? Hotspot { get; private set; }

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AbrirCpu();

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int LeerTemperaturaCpu(out double temperature);

    [DllImport("NextLevelCpuBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CerrarCpu();

    public TemperatureReader()
    {
        try { _cpuReady = AbrirCpu() == 0; }
        catch (Exception ex) { Debug.WriteLine($"No se pudo iniciar el SDK de AMD: {ex.Message}"); }

        try
        {
            _computer = new Computer { IsGpuEnabled = true };
            _computer.Open();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No se pudieron iniciar los sensores GPU: {ex.Message}");
            _computer = null;
        }
    }

    public void Refresh()
    {
        Cpu = null;
        if (_cpuReady)
        {
            try
            {
                if (LeerTemperaturaCpu(out double value) == 0 && double.IsFinite(value) && value > 0)
                    Cpu = value;
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

        Gpu = gpu?.Sensor.Value;
        Hotspot = hotspot?.Sensor.Value;
        foreach (string error in errors) Debug.WriteLine($"Sensor GPU: {error}");
    }

    private static void CollectTemperatures(IHardware root, IHardware hardware,
        List<TemperatureReading> readings, List<string> errors)
    {
        try { hardware.Update(); }
        catch (Exception ex) { errors.Add($"{hardware.Name}: {ex.Message}"); }

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature)
                readings.Add(new TemperatureReading(root, sensor));
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

    public void Dispose()
    {
        _computer?.Close();
        _computer = null;
        if (_cpuReady) CerrarCpu();
        _cpuReady = false;
    }

    private sealed record TemperatureReading(IHardware Root, ISensor Sensor);
}
