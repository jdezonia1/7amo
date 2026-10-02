using System.Text.Json;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Materials;

/// <summary>Phase-3 per-user settings (kept beside settings.json in %APPDATA%\Raffaello\materials.json).</summary>
public sealed class MaterialsSettings
{
    /// <summary>Send scan / photo pages to Claude when the text layer and local OCR are not enough. Off by default.</summary>
    public bool CloudReading { get; set; }
    public string VisionEffort { get; set; } = "medium";
    /// <summary>CLAUSE (PO conditions, default) / HEADER (PO header block) / CUSTOM.</summary>
    public string ToleranceMode { get; set; } = "CLAUSE";
    public double CustomTolerancePct { get; set; } = 0.05;
    /// <summary>Default metres per piece for conduit / pipe (falls back to the app's PipeLengthM).</summary>
    public double PipeLengthM { get; set; } = 6;
    /// <summary>Owner MOS % applied to BOQ rate x delivered-not-installed qty.</summary>
    public double MosPct { get; set; } = 0.75;
    /// <summary>Only DN lines covered by a MIR count as materials on site.</summary>
    public bool MosRequiresMir { get; set; } = true;
    /// <summary>Auto-coding thresholds: at or above High = filled, at or above Medium = filled + flagged, below = ask.</summary>
    public double AutoCodeHigh { get; set; } = 0.85;
    public double AutoCodeMedium { get; set; } = 0.6;
    /// <summary>Let the BOQ categoriser ask Claude for rows no keyword rule covers (needs an API key).</summary>
    public bool BoqAiFallback { get; set; }
    /// <summary>Optional custom MOS export template (xlsx with a MOS sheet); empty = built-in clean layout.</summary>
    public string MosTemplatePath { get; set; } = "";

    public static string DefaultPath => Path.Combine(AppSettings.SettingsFolder, "materials.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MaterialsSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<MaterialsSettings>(File.ReadAllText(path), Json) ?? new(); }
        catch { /* corrupt: defaults */ }
        return new MaterialsSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public double ToleranceFor(MatPo po) => ToleranceMode switch
    {
        "HEADER" => po.ToleranceHeaderPct ?? 0,
        "CUSTOM" => CustomTolerancePct,
        _ => po.EffectiveTolerancePct,
    };
}
