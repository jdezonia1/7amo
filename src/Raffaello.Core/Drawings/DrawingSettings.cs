using System.Text.Json;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Drawings;

/// <summary>Which symbols a takeoff column counts.</summary>
public static class StageApplies
{
    public const string All = "ALL", Wall = "WALL", Ceiling = "CEILING", CeilingLight = "CEILING LIGHT";
    public static readonly string[] Values = { All, Wall, Ceiling, CeilingLight };
}

/// <summary>[drawings] One column of the takeoff table: CEIL / 1ST / 2ND / FLEX / DALI -> PROJECT QTY stage (+ optional item).</summary>
public sealed class StageRule
{
    public string Column { get; set; } = "";
    public string Stage { get; set; } = "";
    /// <summary>ALL / WALL / CEILING / CEILING LIGHT.</summary>
    public string Applies { get; set; } = StageApplies.All;
    /// <summary>PROJECT QTY item instead of the symbol's item ("DALI").</summary>
    public string ItemOverride { get; set; } = "";
    /// <summary>Use the symbol's 2nd-fix factor (twin data outlet = 2).</summary>
    public bool UseSecondFixFactor { get; set; }
}

/// <summary>Heights per building / level / room type (m).</summary>
public sealed class HeightRow
{
    public string Building { get; set; } = "";
    public string Level { get; set; } = "";
    public string RoomType { get; set; } = "";
    public double FloorToCeilingM { get; set; } = 3.0;
    public double CeilingVoidM { get; set; } = 0.6;
    public double SlabToSlabM { get; set; } = 3.9;
}

/// <summary>
/// [drawings] Settings of the drawings module (per user, %APPDATA%\Raffaello\drawings.json): counting rules (stage columns),
/// panel notes, matcher defaults, highlight colours, heights table, mounting heights, allowances and spare for cable lengths.
/// </summary>
public sealed class DrawingSettings
{
    public List<StageRule> StageRules { get; set; } = DefaultStageRules();

    /// <summary>Counting rules printed in the left panel of every takeoff sheet.</summary>
    public List<string> RuleNotes { get; set; } = new()
    {
        "twin socket = 1 point  |  \"T\" = twin data outlet (2nd fix = 2)  |  terraces / WP sockets excluded  |  H>=3000 = ceiling  |  GRMS: C & ms not counted, CP-4 = thermostat",
        "CEILING = all items | 1ST FIX = wall items | 2ND FIX = all (wiring)",
        "FLEX = ceiling items (flexible drop) | DALI = ceiling light fittings",
    };

    public string TakeoffWorkbookName { get; set; } = "QTY_TAKEOFF.xlsx";
    public int DefaultDpi { get; set; } = 150;
    public double Threshold { get; set; } = 0.70;
    public bool Rotations { get; set; } = true;
    public bool Mirror { get; set; } = true;
    public string Scales { get; set; } = "1.0";
    /// <summary>Symbols at or above this mounting height count as CEILING items (house rule H &gt;= 3000).</summary>
    public double CeilingFromHeightM { get; set; } = 3.0;
    public string HighlightColours { get; set; } = "GREEN";
    /// <summary>Share of a symbol box that must be under highlighter to count as highlighted.</summary>
    public double HighlightCoverage { get; set; } = 0.12;
    /// <summary>Claude vision checks hits whose score is within this margin above the threshold (cloud reading only).</summary>
    public double VisionMargin { get; set; } = 0.08;

    // ---- cable / route lengths
    public List<HeightRow> Heights { get; set; } = new() { new HeightRow() };
    /// <summary>Default mounting heights per item / tag keyword (m): SOCKET 0.45, SWITCH 1.2 ...</summary>
    public Dictionary<string, double> MountingHeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SOCKET"] = 0.45, ["POWER"] = 0.45, ["DATA"] = 0.45, ["AV"] = 0.45, ["SWITCH"] = 1.2, ["GRMS"] = 1.2, ["CP-4"] = 1.2,
        ["ISOLATOR"] = 1.5, ["FCU"] = 2.6, ["LIGHT"] = 0, ["DOWNLIGHT"] = 0,
    };
    /// <summary>Cable entry height at the DB / panel (m above floor) - top entry.</summary>
    public double PanelEntryHeightM { get; set; } = 2.0;
    /// <summary>Termination allowance per cable end (m).</summary>
    public double TerminationAllowanceM { get; set; } = 0.5;
    /// <summary>Spare (0.05 = 5 %) added on top of the measured length.</summary>
    public double SparePct { get; set; } = 0.05;
    /// <summary>15 m rule: one extra point per started 15 m (proportional, minimum 1).</summary>
    public double LengthRuleM { get; set; } = 15;

    public static List<StageRule> DefaultStageRules() => new()
    {
        new() { Column = "CEIL", Stage = "CEILING", Applies = StageApplies.All },
        new() { Column = "1ST", Stage = "1ST FIX", Applies = StageApplies.Wall },
        new() { Column = "2ND", Stage = "2ND FIX", Applies = StageApplies.All, UseSecondFixFactor = true },
        new() { Column = "FLEX", Stage = "FLEXIBLE", Applies = StageApplies.Ceiling },
        new() { Column = "DALI", Stage = "2ND FIX", Applies = StageApplies.CeilingLight, ItemOverride = "DALI" },
    };

    public double[] ParsedScales() =>
        Scales.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v > 0.3 && v < 3).DefaultIfEmpty(1.0).ToArray();

    public HeightRow HeightsFor(string building, string level, string roomType) =>
        Heights.Where(h => (h.Building.Length == 0 || h.Building.Equals(building, StringComparison.OrdinalIgnoreCase))
                        && (h.Level.Length == 0 || h.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                        && (h.RoomType.Length == 0 || h.RoomType.Equals(roomType, StringComparison.OrdinalIgnoreCase)))
               .OrderByDescending(h => (h.Building.Length > 0 ? 4 : 0) + (h.Level.Length > 0 ? 2 : 0) + (h.RoomType.Length > 0 ? 1 : 0))
               .FirstOrDefault() ?? new HeightRow();

    /// <summary>Mounting height for a symbol: H= note on the drawing, the symbol's own default, then the keyword table.</summary>
    public double MountingHeightFor(DwgSymbol? s, double fromDrawing = 0)
    {
        if (fromDrawing > 0) return fromDrawing;
        if (s is null) return MountingHeights.GetValueOrDefault("POWER", 0.45);
        if (s.MountingHeightM > 0) return s.MountingHeightM;
        foreach (var key in new[] { s.Tag, s.Name, s.Item, s.System })
            foreach (var kv in MountingHeights.OrderByDescending(k => k.Key.Length))
                if (!string.IsNullOrEmpty(key) && key.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return 0.45;
    }

    // ------------------------------------------------------------------ persistence

    public static string FilePath => Path.Combine(AppSettings.SettingsFolder, "drawings.json");

    public static DrawingSettings Load(string? path = null)
    {
        try
        {
            var p = path ?? FilePath;
            if (File.Exists(p))
            {
                var s = JsonSerializer.Deserialize<DrawingSettings>(File.ReadAllText(p));
                if (s != null)
                {
                    if (s.StageRules.Count == 0) s.StageRules = DefaultStageRules();
                    s.MountingHeights = new Dictionary<string, double>(s.MountingHeights, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch (Exception) { /* a broken file falls back to defaults */ }
        return new DrawingSettings();
    }

    public void Save(string? path = null)
    {
        var p = path ?? FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>[drawings] Turns a symbol + the stage rules into the PROJECT QTY keys one hit feeds.</summary>
public static class StageRules
{
    public sealed record Cell(string Column, string Stage, string Item, double Qty);

    /// <summary>Per-column quantities of one hit (explicit symbol targets win over the rules).</summary>
    public static List<Cell> Cells(DwgSymbol s, IReadOnlyList<StageRule> rules, string? mountOverride = null)
    {
        if (s.Excluded) return new();
        var item = (s.Item.Length > 0 ? s.Item : s.System).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(s.Targets))
            return QtyTarget.Parse(s.Targets).Select(t => new Cell(ColumnOf(t.Stage, rules), t.Stage, t.Item, t.Factor)).ToList();
        var mount = (mountOverride ?? s.Mount).Trim().ToUpperInvariant();
        var ceiling = mount.StartsWith("C");
        var res = new List<Cell>();
        foreach (var r in rules)
        {
            var applies = r.Applies switch
            {
                StageApplies.Wall => !ceiling,
                StageApplies.Ceiling => ceiling,
                StageApplies.CeilingLight => ceiling && s.IsLightFitting,
                _ => true,
            };
            if (!applies) continue;
            var qty = r.UseSecondFixFactor && s.SecondFixFactor > 0 ? s.SecondFixFactor : 1;
            res.Add(new Cell(r.Column, r.Stage.ToUpperInvariant(), (r.ItemOverride.Length > 0 ? r.ItemOverride : item).ToUpperInvariant(), qty));
        }
        return res;
    }

    private static string ColumnOf(string stage, IReadOnlyList<StageRule> rules) =>
        rules.FirstOrDefault(r => r.Stage.Equals(stage, StringComparison.OrdinalIgnoreCase))?.Column ?? stage;
}
