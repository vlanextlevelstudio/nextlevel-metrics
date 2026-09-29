namespace NextLevelMetrics;

internal sealed record OverlayPart(string Text, string Color);

internal sealed class OverlayPresentation
{
    public IReadOnlyList<OverlayPart> Parts { get; }
    public string PlainText => string.Concat(Parts.Select(part => part.Text));

    private OverlayPresentation(List<OverlayPart> parts) => Parts = parts;

    public static OverlayPresentation Create(string fps, double? cpu, float? gpu,
        float? hotspot, OverlaySettings settings)
    {
        string main = settings.ColorPrincipal;
        var parts = new List<OverlayPart>
        {
            new("FPS " + fps + " | CPU ", main),
            new(FormatTemperature(cpu), TemperatureColor(cpu, settings.CpuAviso,
                settings.CpuCritico, settings)),
            new(" | GPU ", main),
            new(FormatTemperature(gpu), TemperatureColor(gpu, settings.GpuAviso,
                settings.GpuCritico, settings)),
            new(" - ", main),
            new(FormatTemperature(hotspot), TemperatureColor(hotspot, settings.HotspotAviso,
                settings.HotspotCritico, settings))
        };
        return new OverlayPresentation(parts);
    }

    private static string TemperatureColor(double? value, int warning, int critical,
        OverlaySettings settings) => !settings.AvisosPorColor || value is null
            ? settings.ColorPrincipal
            : value >= critical ? "#FF0000" : value >= warning ? "#FFFF00" : settings.ColorPrincipal;

    private static string FormatTemperature(double? value) => value is > 0 and < double.PositiveInfinity
        ? $"{Math.Round(value.Value, 0, MidpointRounding.AwayFromZero):F0}°"
        : "--";
}
