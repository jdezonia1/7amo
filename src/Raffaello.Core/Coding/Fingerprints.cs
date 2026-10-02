using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Coding;

/// <summary>Attributes of a power cable read from any supplier / BOQ spelling ("4X10mm²RM_CU/XL/SW/HF_1kV", "4C 10mm2 Cu/XLPE/SWA/LSHZ", "4X10 CU/XLPE/SWA/LSOH").</summary>
public sealed record CableSpec(int Cores, double Size, string Conductor, bool FireRated, string Sheath, bool Armoured, double? VoltageKv, bool Earth, int Runs)
{
    /// <summary>LV / MV (MV = 3.3 kV and above).</summary>
    public string Class => VoltageKv is >= 3 ? "MV" : "LV";
    public string CoreSize => $"{Cores}X{Size.ToString("0.##", CultureInfo.InvariantCulture)}";

    /// <summary>Stable key used for exact history and the 3-way match.</summary>
    public string Key => $"CABLE|{CoreSize}|{Conductor}|{(FireRated ? "FR" : "STD")}|{Class}";

    public override string ToString() =>
        $"{(Runs > 1 ? Runs + " x " : "")}{CoreSize} {Conductor}{(FireRated ? " FIRE-RATED" : "")}{(Armoured ? " ARMOURED" : "")} {Sheath} {Class}{(VoltageKv is double v ? $" {v:0.##}kV" : "")}{(Earth ? " EARTH" : "")}".Replace("  ", " ").Trim();
}

/// <summary>
/// Attribute fingerprints for supplier / BOQ lines: cables (cores x size, CU/AL, MICA/fire-rated, sheath with HF = LSOH = LSZH,
/// armour, voltage) and a generic token fingerprint for conduit, fittings, trays, luminaires (type words + sizes + wattage).
/// </summary>
public static class Fingerprints
{
    private static readonly Regex CoreC = new(@"(?<![\d.])(\d{1,2})\s*(?:/\s*C|C|CORE|CORES)\s*[xX×*]?\s*(\d{1,4}(?:\.\d+)?)\s*(?:mm²|mm2|sqmm|sq\.?\s*mm|mm)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoreX = new(@"(?<![\d.])(\d{1,2})\s*[xX×*]\s*(\d{1,4}(?:\.\d+)?)(?!\s*[xX×*]\s*\d)(?:\s*(?:mm²|mm2|sqmm|mm))?", RegexOptions.Compiled);
    private static readonly Regex Runs = new(@"(?<![\d.])(\d)\s*[xX×]\s*(\d{1,2})\s*(?:C|CORE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Kv = new(@"(\d{1,2}(?:\.\d{1,2})?)\s*(?:/\s*\d{1,2}(?:\.\d{1,2})?\s*)?kV", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingKv = new(@"(?:PVC|XLPE|SWA|LSZH|LSOH)\s+(\d{1,2}\.\d{1,2})\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CableWord = new(@"\b(CU|AL|XLPE|XL|LSZH|LSOH|LSHZ|LS0H|LSF|HF|PVC|SWA|SW|AWA|MICA|CABLE|WIRE|G/Y|Y/G|FIRE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CableSpec? Cable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace('_', ' ').Replace("²", "2");
        if (!CableWord.IsMatch(t)) return null;
        // first conductor spec only (BOQ lines carry "+ 1C 10mm2 earth")
        var main = t.Split('+')[0];
        int runs = 1;
        var rm = Runs.Match(main);
        if (rm.Success) { runs = int.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture); main = main[(rm.Index + rm.Groups[1].Length)..].TrimStart(' ', 'x', 'X', '×'); }
        var m = CoreC.Match(main);
        if (!m.Success) m = CoreX.Match(main);
        if (!m.Success) return null;
        var cores = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var size = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (cores < 1 || cores > 61 || size <= 0) return null;
        var u = " " + t.ToUpperInvariant().Replace('/', ' ').Replace('-', ' ').Replace(',', ' ').Replace('(', ' ').Replace(')', ' ').Replace('.', ' ') + " ";
        string conductor = Regex.IsMatch(u, @"\s(AL|ALU|ALUMINIUM|ALUMINUM)\s") ? "AL" : "CU";
        var fire = Regex.IsMatch(u, @"\s(MICA|FR|FP|FIRE|FIRE\s*RATED|FIRERATED|CWZ|FP200|FP400)\s") || u.Contains("FIRE RES", StringComparison.Ordinal);
        string sheath = Regex.IsMatch(u, @"\s(LSZH|LSOH|LS0H|LSHZ|LSF|HF|LSZH\.|LSOH\.|HFFR|LSNH)\s") ? "LSZH" : Regex.IsMatch(u, @"\sPVC\s") ? "PVC" : "";
        var armoured = Regex.IsMatch(u, @"\s(SWA|SW|AWA|STA|ARMOURED|ARMORED)\s");
        double? kv = null;
        var km = Kv.Match(t);
        if (km.Success) kv = double.Parse(km.Groups[1].Value, CultureInfo.InvariantCulture);
        else
        {
            var tk = TrailingKv.Match(main.Trim());
            if (tk.Success && double.TryParse(tk.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 3.3 and <= 36) kv = v;
        }
        var earth = cores == 1 && Regex.IsMatch(u, @"\s(G\s*Y|Y\s*G|ECC|EARTH|GREEN)\s");
        return new CableSpec(cores, size, conductor, fire, sheath, armoured, kv, earth, runs);
    }

    /// <summary>1.0 = same cable; 0 = different cable. Soft attributes (sheath, armour, earth) lower the score only when both sides state them.</summary>
    public static double CompareCables(CableSpec a, CableSpec b)
    {
        if (a.Cores != b.Cores || Math.Abs(a.Size - b.Size) > 1e-6 || a.Conductor != b.Conductor || a.FireRated != b.FireRated || a.Class != b.Class || a.Runs != b.Runs) return 0;
        var s = 1.0;
        if (a.Sheath.Length > 0 && b.Sheath.Length > 0 && a.Sheath != b.Sheath) s -= 0.3;
        if (a.Armoured != b.Armoured && a.Cores > 1 && b.Cores > 1 && a.Sheath.Length > 0 && b.Sheath.Length > 0) s -= 0.15;
        if (a.Earth != b.Earth) s -= 0.1;
        return s;
    }

    // ------------------------------------------------------------------ generic (conduit, fittings, tray, luminaires)

    private static readonly Dictionary<string, string> Syn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CONDUITS"] = "CONDUIT", ["PIPE"] = "CONDUIT", ["PIPES"] = "CONDUIT", ["TUBE"] = "CONDUIT",
        ["GI"] = "GI", ["GALVANISED"] = "GI", ["GALVANIZED"] = "GI", ["HDG"] = "GI",
        ["FLEXIBLE"] = "FLEX", ["FLEXI"] = "FLEX", ["UPVC"] = "PVC",
        ["BENDS"] = "BEND", ["ELBOW"] = "BEND", ["ELBOWS"] = "BEND", ["COUPLER"] = "COUPLING", ["COUPLINGS"] = "COUPLING", ["COUPLERS"] = "COUPLING",
        ["SADDLES"] = "SADDLE", ["GLANDS"] = "GLAND", ["BOXES"] = "BOX", ["JUNCTION"] = "JB", ["J.B"] = "JB",
        ["LADDER"] = "LADDER", ["TRAYS"] = "TRAY", ["TRUNKINGS"] = "TRUNKING", ["LUMINAIRE"] = "LIGHT", ["LUMINAIRES"] = "LIGHT", ["FIXTURE"] = "LIGHT", ["DOWNLIGHT"] = "LIGHT",
        ["SOCKETS"] = "SOCKET", ["OUTLET"] = "SOCKET", ["SWITCHES"] = "SWITCH", ["LUGS"] = "LUG", ["SLEEVES"] = "SLEEVE",
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    { "THE", "AND", "OF", "FOR", "WITH", "TO", "IN", "ON", "A", "AN", "AS", "PER", "ALL", "SUPPLY", "INSTALL", "INSTALLATION", "COMPLETE", "INCLUDING", "INCL", "TYPE", "SIZE", "MM", "NO", "NOS", "PCS", "PC", "M", "MTR", "MT", "ITEM", "APPROVED", "EQUAL", "SAME", "WORKS" };

    /// <summary>Normalised tokens: synonyms folded, sizes as "25MM", wattage as "18W".</summary>
    public static List<string> Tokens(string? text)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return res;
        var t = text.ToUpperInvariant().Replace("²", "2");
        t = Regex.Replace(t, @"(\d+(?:\.\d+)?)\s*(MM2|MM|W|A|V|KV|KW|KVA|M)\b", "$1$2");
        t = Regex.Replace(t, @"(\d)\s*/\s*(\d)\s*""", "$1/$2IN");
        foreach (var raw in Regex.Split(t, @"[^A-Z0-9./]+"))
        {
            var w = raw.Trim('.', '/');
            if (w.Length == 0 || Stop.Contains(w)) continue;
            if (Syn.TryGetValue(w, out var s)) w = s;
            if (Regex.IsMatch(w, @"^0+\d")) w = w.TrimStart('0');
            res.Add(w);
        }
        return res;
    }

    /// <summary>Key of a non-cable line: type words + sizes (order-free). Cables get <see cref="CableSpec.Key"/>.</summary>
    public static string Key(string? text)
    {
        var c = Cable(text);
        if (c != null) return c.Key;
        var toks = Tokens(text).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        return toks.Count == 0 ? "" : "GEN|" + string.Join(" ", toks);
    }

    /// <summary>Generic similarity: Jaccard on tokens, with sizes / wattage required to agree when both sides have them.</summary>
    public static double CompareGeneric(string? a, string? b)
    {
        var ta = Tokens(a).ToHashSet(); var tb = Tokens(b).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        static bool IsSize(string x) => Regex.IsMatch(x, @"^\d+(\.\d+)?(MM2|MM|W|A|KV|KW|KVA|IN|M)?$");
        var sa = ta.Where(IsSize).ToHashSet(); var sb = tb.Where(IsSize).ToHashSet();
        if (sa.Count > 0 && sb.Count > 0 && !sa.Overlaps(sb)) return 0;
        var inter = ta.Intersect(tb).Count();
        return inter / (double)ta.Union(tb).Count();
    }

    /// <summary>Best of cable / generic comparison (0..1).</summary>
    public static double Compare(string? a, string? b)
    {
        var ca = Cable(a); var cb = Cable(b);
        if (ca != null && cb != null) return CompareCables(ca, cb);
        if (ca != null || cb != null) return 0;
        return CompareGeneric(a, b);
    }

    /// <summary>Normalised description for exact history (case, spacing and punctuation folded; HF/LSOH/LSHZ -> LSZH).</summary>
    public static string NormalizeDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.ToUpperInvariant().Replace("²", "2");
        t = Regex.Replace(t, @"\b(LSOH|LS0H|LSHZ|HF)\b", "LSZH");
        var sb = new StringBuilder();
        foreach (var ch in t) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
