using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextLevelMetrics;

internal enum OverlayPosition { SuperiorIzquierda, SuperiorDerecha, InferiorIzquierda, InferiorDerecha }
internal enum OverlayBackground { Transparente, OscuroSuave }

internal sealed class OverlaySettings
{
    public string ColorPrincipal { get; set; } = "#FFFFFF";
    public int TamanoPorcentaje { get; set; } = 100;
    public OverlayPosition Posicion { get; set; } = OverlayPosition.SuperiorIzquierda;
    public string Fuente { get; set; } = "Segoe UI Semibold";
    public OverlayBackground Fondo { get; set; } = OverlayBackground.Transparente;
    public bool AvisosPorColor { get; set; } = true;
    public int CpuAviso { get; set; } = 80;
    public int CpuCritico { get; set; } = 90;
    public int GpuAviso { get; set; } = 75;
    public int GpuCritico { get; set; } = 85;
    public int HotspotAviso { get; set; } = 85;
    public int HotspotCritico { get; set; } = 92;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();

    public string? Validate()
    {
        if (ColorPrincipal.Length != 7 || ColorPrincipal[0] != '#' ||
            !ColorPrincipal.AsSpan(1).ToString().All(Uri.IsHexDigit))
            return "El color principal debe tener el formato #RRGGBB.";
        if (TamanoPorcentaje is < 50 or > 250)
            return "El tamaño debe estar entre 50 % y 250 %.";
        if (!Enum.IsDefined(Posicion) || !Enum.IsDefined(Fondo))
            return "Selecciona una posición y un fondo válidos.";
        if (!AvailableFonts.Contains(Fuente, StringComparer.OrdinalIgnoreCase))
            return "Selecciona una fuente disponible en Windows.";
        if (!ValidThresholds(CpuAviso, CpuCritico) ||
            !ValidThresholds(GpuAviso, GpuCritico) ||
            !ValidThresholds(HotspotAviso, HotspotCritico))
            return "Cada aviso debe estar entre 20° y 119° y ser inferior a su valor crítico; el crítico no puede superar 120°.";
        return null;
    }

    private static bool ValidThresholds(int warning, int critical) =>
        warning is >= 20 and <= 119 && critical is >= 21 and <= 120 && warning < critical;

    public static IReadOnlyList<string> AvailableFonts { get; } = FindAvailableFonts();

    private static IReadOnlyList<string> FindAvailableFonts()
    {
        string[] desired = ["Segoe UI", "Segoe UI Semibold", "Consolas",
            "Cascadia Mono", "Cascadia Code", "Arial", "Verdana", "Tahoma",
            "Calibri", "Trebuchet MS", "Courier New", "Lucida Console", "Bahnschrift"];
        const string fontKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
        using var machineFonts = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(fontKey);
        using var userFonts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(fontKey);
        string[] names = (machineFonts?.GetValueNames() ?? [])
            .Concat(userFonts?.GetValueNames() ?? [])
            .ToArray();
        return desired.Where(font => names.Any(name =>
            name.StartsWith(font + " (", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(font + " Regular (", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}

internal static class OverlaySettingsStore
{
    public static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NextLevelMetrics", "configuracion.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new OverlaySettings();
            OverlaySettings? saved = JsonSerializer.Deserialize<OverlaySettings>(
                File.ReadAllText(PathName), JsonOptions);
            return saved is not null && saved.Validate() is null ? saved : new OverlaySettings();
        }
        catch { return new OverlaySettings(); }
    }

    public static void Save(OverlaySettings settings)
    {
        string? error = settings.Validate();
        if (error is not null) throw new ArgumentException(error);
        string directory = Path.GetDirectoryName(PathName)!;
        Directory.CreateDirectory(directory);
        string temporary = PathName + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, PathName, true);
    }
}
