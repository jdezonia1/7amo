using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Contracts;

public static class FixStages
{
    public const string First = "1ST FIX";
    public const string Second = "2ND FIX";
    public const string Third = "3RD FIX";
}

public static class Conduits
{
    public const string Pvc = "PVC";
    public const string Emt = "EMT";
    public const string Rs = "RS";
    public const string Flex = "FLEX";
    public const string None = "NONE";
}

public static class Mounts
{
    public const string Wall = "WALL";
    public const string Ceiling = "CEILING";
    public const string Both = "BOTH";
}

public static class HeightBands
{
    public const string Low = "LOW";
    public const string High = "HIGH";
    public const string Any = "ANY";
}

public sealed record ParsedAttributes(
    string FixStage, string Conduit, string Mount, string Height, bool Pulling, bool Homerun,
    IReadOnlyList<string> Systems, string Category, string SizeKey, IReadOnlyList<string> Notes);

/// <summary>
/// Reads the rate-driving attributes out of a contract item description (English and Arabic):
/// stage (1st / 2nd / 3rd fix), conduit (PVC / EMT / RS / flexible), wall vs ceiling, height band (&lt; / &gt; 4.5 m),
/// 2nd-fix pulling (15 m rule), systems covered, category and size key (panel ways, cable cores x size, tray band).
/// Everything is a best guess the user can correct; notes say what was inferred.
/// </summary>
public static class ContractAttributeParser
{
    private static readonly (string System, string[] Keys)[] SystemKeys =
    {
        ("DALI", new[] { "dali" }),
        ("FIRE", new[] { "fire alarm", "انذار" }),
        ("EVACUATION", new[] { "speaker", "bgm", "voice evac" }),
        ("AV", new[] { "a/v", " tv", "tv point", "tv,", "تلفزيون" }),
        ("BMS", new[] { "bms" }),
        ("DATA", new[] { "data", "telephone", "it / tele", "داتا", "تليفون" }),
        ("CCTV", new[] { "cctv", "camera" }),
        ("GRMS", new[] { "grms" }),
        ("DISABLED", new[] { "disabled" }),
        ("ACCESS", new[] { "access control" }),
        ("LIGHTING CONTROL", new[] { "lighting control" }),
        ("INTERCOM", new[] { "intercom" }),
        ("PARKING", new[] { "parking", "gate barrier" }),
        ("METERING", new[] { "metering" }),
        ("EV", new[] { "ev charging" }),
        ("FLOOR BOX", new[] { "floor box" }),
        ("ISOLATOR", new[] { "isolator" }),
    };

    public static ParsedAttributes Parse(string description, string unit = "")
    {
        var notes = new List<string>();
        var raw = description ?? "";
        var d = " " + Regex.Replace(raw.ToLowerInvariant(), @"\s+", " ") + " ";
        // phrases that mention walls / heights without describing the mounting
        var forMount = Regex.Replace(d, @"wall chasing(?:/?cutting)?|chasing/cutting|التكسير بالجدران|بالجدران|walls?/floor|trenches or walls", " ");

        // ---- stage
        var stage = d.Contains("1st fix") || d.Contains("1st-fix") ? FixStages.First
                  : d.Contains("2nd fix") || d.Contains("2nd-fix") ? FixStages.Second
                  : d.Contains("3rd fix") || d.Contains("3rd-fix") ? FixStages.Third
                  : d.Contains("مرحلة أولى") ? FixStages.First : d.Contains("مرحلة ثانية") ? FixStages.Second : d.Contains("مرحلة ثالثة") ? FixStages.Third : "";

        // ---- conduit
        var conduit = d.Contains("flexible conduit") || d.Contains("فليكس") || Regex.IsMatch(d, @"\bflexible\b") && !d.Contains("flexible conduit for internal") ? Conduits.Flex
                    : Regex.IsMatch(d, @"\bemt\b") ? Conduits.Emt
                    : Regex.IsMatch(d, @"\brs\b") || d.Contains("rigid steel") ? Conduits.Rs
                    : Regex.IsMatch(d, @"\bpvc\b") ? Conduits.Pvc
                    : Conduits.None;
        if (d.Contains("isolator") || d.Contains("internal and external flexible")) conduit = Conduits.None;

        // ---- mount
        string mount;
        if (Regex.IsMatch(forMount, @"wall-mounted or ceiling-mounted|wall/ceiling|wall or ceiling|جداري او سقفي|جداري أو سقفي")) mount = Mounts.Both;
        else
        {
            var ceiling = Regex.IsMatch(forMount, @"ceiling|سقفي");
            var wall = Regex.IsMatch(forMount, @"\bwall\b|wall-mounted|wall type|جداري");
            mount = ceiling && !wall ? Mounts.Ceiling : wall && !ceiling ? Mounts.Wall : Mounts.Both;
        }

        // ---- height band
        var height = Regex.IsMatch(d, @"(below|less than|under|lower than|<)\s*4\.5|أقل\s*(من)?\s*4\.5|اقل\s*(من)?\s*4\.5") ? HeightBands.Low
                   : Regex.IsMatch(d, @"(above|over|more than|higher than|>)\s*4\.5|فوق\s*4\.5|أعلى\s*(من)?\s*4\.5|اعلى\s*(من)?\s*4\.5") ? HeightBands.High
                   : HeightBands.Any;

        var homerun = d.Contains("homerun") || d.Contains("home run");
        var pulling = stage == FixStages.Second && (d.Contains("pulling") || d.Contains("pull ") || d.Contains("سحب") || d.Contains("wiring"))
                      || Regex.IsMatch(d, @"15\s*m\b|15\s*متر");

        // ---- systems
        var systems = new List<string>();
        var facade = d.Contains("façade") || d.Contains("facade");
        var linear = d.Contains("linear lighting");
        if (facade) systems.Add("FACADE LIGHT");
        else if (linear) systems.Add("LINEAR LIGHT");
        else
        {
            if (d.Contains("power socket") || d.Contains("بريزة") || d.Contains("socket outlet")) systems.Add("POWER");
            if (d.Contains("lighting switch") || d.Contains("lighting point") || d.Contains("light fixture") || d.Contains("مفتاح انارة") || d.Contains("مخرج إنارة") || d.Contains("مخرج انارة"))
            {
                systems.Add("LIGHT");
                systems.Add("EMERGENCY LIGHT");
            }
            foreach (var (sys, keys) in SystemKeys)
                if (keys.Any(k => d.Contains(k)) && !systems.Contains(sys)) systems.Add(sys);
            if (systems.Contains("LIGHTING CONTROL")) { systems.Remove("LIGHT"); systems.Remove("EMERGENCY LIGHT"); }
            if (d.Contains("dali") && !d.Contains("lighting point") && !d.Contains("lighting switch")) { systems.Remove("LIGHT"); systems.Remove("EMERGENCY LIGHT"); systems.Remove("POWER"); }
        }

        // ---- category + size key
        var category = "OTHER";
        var size = "";
        var mm = Regex.Match(d, @"(\d+(?:\.\d+)?)\s*mm²|(\d+(?:\.\d+)?)\s*mm2");
        var ways = Regex.Match(d, @"\((\d+)\s*(?:–|-|to)?\s*(\d+)?\s*ways\)|(\d+)\s*ways");
        if (d.Contains("cable support") || d.Contains("cable tray") || d.Contains("cable trunking") || d.Contains("cable ladder"))
        {
            category = d.Contains("covers for") ? "TRAY COVER" : "TRAY";
            size = d.Contains("above 80") ? "TRAY 900+" : Regex.IsMatch(d, @"40\s*cm\s*to\s*80") ? "TRAY 400-800" : Regex.IsMatch(d, @"5\s*cm\s*to\s*30") ? "TRAY 50-300" : "TRAY";
        }
        else if (Regex.IsMatch(d, @"\bpanel\b|distribution board|\bmdb\b|\bsmdb\b|mcc|capacitor|harmonic|cabinet|power supply"))
        {
            category = "PANEL";
            if (ways.Success)
                size = ways.Groups[1].Success
                    ? (ways.Groups[2].Success && ways.Groups[2].Value.Length > 0 ? $"PANEL {ways.Groups[1].Value}-{ways.Groups[2].Value}" : $"PANEL {ways.Groups[1].Value}")
                    : $"PANEL {ways.Groups[3].Value}";
        }
        else if (mm.Success && (d.Contains("cable") || d.Contains("كابل")))
        {
            var s = mm.Groups[1].Success ? mm.Groups[1].Value : mm.Groups[2].Value;
            var cores = Regex.IsMatch(d, @"\b4c\b|3\s*[×x]\s*\d") ? "4" : Regex.IsMatch(d, @"\b2c\b") ? "2" : Regex.IsMatch(d, @"\b1c\b") ? "1" : "";
            size = cores.Length > 0 ? $"{cores}X{s}" : s;
            category = d.Contains("earth cable") ? "EARTH TERMINATION"
                     : d.Contains("pull and hand over") || d.Contains("install, pull") || d.Contains("pull, terminate") ? "CABLE"
                     : "CABLE TERMINATION";
            if (d.Contains("earth cable")) size = "E" + size;
        }
        else if (d.Contains("earth") || d.Contains("lightning protection") || d.Contains("copper tape")) category = "EARTHING";
        else if (d.Contains("isolator")) category = "ISOLATOR";
        else if (d.Contains("floor box")) category = "DEVICE";
        else if (stage == FixStages.Second || d.Contains("wire pulling") || d.Contains("سحب سلك")) category = "WIRING";
        else if (stage == FixStages.First) category = "OUTLET";
        else if (stage == FixStages.Third) category = conduit == Conduits.Flex ? "FLEX CONDUIT" : "DEVICE";
        else if (d.Contains("final connection")) category = "CONNECTION";

        if (stage.Length == 0 && category is "OUTLET" or "WIRING" or "DEVICE") notes.Add("stage not found in text");
        if (systems.Count == 0 && category is "OUTLET" or "WIRING" or "DEVICE" or "FLEX CONDUIT") notes.Add("no system keyword found");
        if (height == HeightBands.Any && category is "OUTLET" or "WIRING" or "DEVICE" or "FLEX CONDUIT") notes.Add("height band not stated");
        _ = unit;
        return new ParsedAttributes(stage, conduit, mount, height, pulling, homerun, systems, category, size, notes);
    }

    /// <summary>Normalised text used to pair items that differ only by the height band.</summary>
    public static string PairKey(string description) =>
        Regex.Replace(Regex.Replace((description ?? "").ToLowerInvariant(),
            @"(below|less than|under|lower than|above|over|more than|higher than|أقل|اقل|فوق|أعلى|اعلى)\s*(من)?\s*4\.5\s*(m|متر)?", ""), @"\s+", " ").Trim();

    public static void Apply(ContractItem item, ParsedAttributes a)
    {
        item.FixStage = a.FixStage;
        item.ConduitType = a.Conduit;
        item.Mount = a.Mount;
        item.HeightBand = a.Height;
        item.Is2ndFixPulling = a.Pulling;
        item.IsHomerun = a.Homerun;
        item.Systems = string.Join(",", a.Systems);
        item.Category = a.Category;
        item.SizeKey = a.SizeKey;
        item.ParseNotes = string.Join("; ", a.Notes);
    }

    /// <summary>
    /// Items whose height is not stated but that sit next to an otherwise identical item with a different rate are
    /// split LOW (cheaper) / HIGH (dearer) - the contract lists the &lt; 4.5 m item first.
    /// </summary>
    public static int InferHeightPairs(IList<ContractItem> items)
    {
        var n = 0;
        for (var i = 0; i + 1 < items.Count; i++)
        {
            var a = items[i]; var b = items[i + 1];
            if (a.HeightBand != HeightBands.Any || b.HeightBand != HeightBands.Any) continue;
            if (PairKey(a.Description) != PairKey(b.Description) || Math.Abs(a.Rate - b.Rate) < 0.0001) continue;
            var (lo, hi) = a.Rate < b.Rate ? (a, b) : (b, a);
            lo.HeightBand = HeightBands.Low; hi.HeightBand = HeightBands.High;
            lo.ParseNotes = Append(lo.ParseNotes, "height LOW inferred from rate pair with item " + hi.ItemNo);
            hi.ParseNotes = Append(hi.ParseNotes, "height HIGH inferred from rate pair with item " + lo.ItemNo);
            n += 2;
            i++;
        }
        return n;
    }

    private static string Append(string s, string add) => string.IsNullOrEmpty(s) ? add : s + "; " + add;

    public static double DefaultStagePct(ContractItem item) => item.Category switch
    {
        "PANEL" or "CABLE" or "CABLE TERMINATION" or "TRAY" or "TRAY COVER" => 0.7,
        _ => 0.9,
    };

    public static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}
