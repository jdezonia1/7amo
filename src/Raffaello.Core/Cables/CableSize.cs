using System.Globalization;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Cables;

/// <summary>
/// [cables] A cable size as written on SLDs, schedules and statements: "4x16", "4C x 16mm²", "4Cx16mm2", "4 core 16 sq.mm", "1x16 E",
/// "4x240+1x120 E", "4C X 240 + E120", "(4x1C)x240", "2x(4x240)". <see cref="Key"/> is the normalised phase size ("4X16"),
/// <see cref="EarthKey"/> the companion earth ("1X120") when written with it.
/// </summary>
public sealed record CableSize(string Raw, int Cores, double Mm2, string Key, string EarthKey, int Parallel, bool SingleCoreBundle, bool IsEarthOnly,
    string Conductor, string Insulation)
{
    public bool IsValid => Key.Length > 0;
    public bool IsFireRated => Insulation == "MICA";
    public static readonly CableSize Empty = new("", 0, 0, "", "", 1, false, false, "", "");

    private static readonly Regex Paren = new(@"^(\d+)\s*X\s*\((.+)\)$", RegexOptions.Compiled);
    private static readonly Regex Phase = new(@"(?<c>\d+(?:\.\d+)?)\s*C?\s*X\s*(?<one>1\s*C\s*X\s*)?(?<s>\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex CoreFirst = new(@"(?<c>\d+(?:\.\d+)?)\s*C\s+(?<s>\d+(?:\.\d+)?)\b", RegexOptions.Compiled);
    private static readonly Regex SizeFirst = new(@"(?<s>\d+(?:\.\d+)?)\s*(?:MM2|MM)\s*(?<c>\d+(?:\.\d+)?)\s*C\b", RegexOptions.Compiled);
    private static readonly Regex EarthPart = new(@"\+\s*(?:(?:1\s*C?\s*X\s*)?(?:E|ECC|CPC)?\s*(?<e>\d+(?:\.\d+)?)|(?<e2>\d+(?:\.\d+)?)\s*C?\s*X\s*(?<e3>\d+(?:\.\d+)?))", RegexOptions.Compiled);
    private static readonly Regex EarthWord = new(@"(^|[^A-Z])(E|ECC|CPC|EARTH|GRN/YEL|G/Y|PE)([^A-Z]|$)", RegexOptions.Compiled);
    private static readonly Regex EarthPrefix = new(@"^E\s*(\d)", RegexOptions.Compiled);

    public static CableSize Parse(string? raw)
    {
        var original = raw ?? "";
        var s = Normalise(original);
        if (s.Length == 0) return Empty with { Raw = original };
        var conductor = Regex.IsMatch(s, @"(^|[^A-Z])(AL|ALU|ALUMINIUM|ALUMINUM)([^A-Z]|$)") ? "AL" : "CU";
        var insulation = Regex.IsMatch(s, @"MICA|FIRE|(^|[^A-Z])FR([^A-Z]|$)|FRLS|FP200") ? "MICA"
            : Regex.IsMatch(s, @"LSOH|LSZH|LS0H|(^|[^A-Z])HF([^A-Z]|$)|LSF") ? "LSOH"
            : s.Contains("XLPE") ? "XLPE" : s.Contains("PVC") ? "PVC" : "";
        var parallel = 1;
        var body = s;
        var pm = Paren.Match(body.Trim());
        if (pm.Success) { parallel = int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture); body = pm.Groups[2].Value; }
        body = body.Replace("(", " ").Replace(")", " ");
        var phaseText = body.Split('+')[0];
        var earthOnlyPrefix = EarthPrefix.IsMatch(phaseText.Trim());
        if (earthOnlyPrefix) phaseText = phaseText.Trim()[1..];

        double cores = 0, mm2 = 0;
        var bundle = false;
        var m = Phase.Match(phaseText);
        if (m.Success)
        {
            cores = D(m.Groups["c"].Value);
            mm2 = D(m.Groups["s"].Value);
            bundle = m.Groups["one"].Success;
        }
        else if (CoreFirst.Match(phaseText) is { Success: true } cf) { cores = D(cf.Groups["c"].Value); mm2 = D(cf.Groups["s"].Value); }
        else if (SizeFirst.Match(phaseText) is { Success: true } sf) { cores = D(sf.Groups["c"].Value); mm2 = D(sf.Groups["s"].Value); }
        if (cores <= 0 || mm2 <= 0 || cores > 61 || mm2 > 1000) return Empty with { Raw = original };

        var earth = "";
        var em = EarthPart.Match(body);
        if (em.Success)
        {
            if (em.Groups["e"].Success) earth = "1X" + Fmt(D(em.Groups["e"].Value));
            else if (em.Groups["e3"].Success) earth = Fmt(D(em.Groups["e2"].Value)) + "X" + Fmt(D(em.Groups["e3"].Value));
        }
        var key = bundle ? $"{Fmt(cores)}X1CX{Fmt(mm2)}" : $"{Fmt(cores)}X{Fmt(mm2)}";
        if (parallel > 1) key = $"{parallel}X({key})";
        var earthOnly = cores == 1 && !bundle && earth.Length == 0 && (earthOnlyPrefix || EarthWord.IsMatch(Regex.Replace(s, @"\d+(\.\d+)?\s*C?\s*X\s*\d+(\.\d+)?", " ")));
        return new CableSize(original, (int)Math.Round(cores), mm2, key, earth, parallel, bundle, earthOnly, conductor, insulation);
    }

    /// <summary>Normalised key of a size text, or the tidied text when it is not a size.</summary>
    public static string KeyOf(string? raw)
    {
        var p = Parse(raw);
        return p.IsValid ? p.Key : Regex.Replace((raw ?? "").Trim().ToUpperInvariant(), @"\s+", "");
    }

    private static readonly Regex SizeRx = new(@"(\d+\s*X\s*\()?\d+(\.\d+)?\s*C?\s*X\s*(1\s*C\s*X\s*)?\d+(\.\d+)?(\s*MM2)?\)?(\s*\+\s*(1\s*C?\s*X\s*)?(E|ECC|CPC)?\s*\d+(\.\d+)?(\s*MM2)?(\s*(E|ECC|CPC)\b)?)?(\s*(E|ECC|CPC)\b)?", RegexOptions.Compiled);

    /// <summary>Finds size annotations inside free text (SLD labels): "4C x 16mm² CU/XLPE/SWA/PVC + 1x16 E" ...</summary>
    public static IEnumerable<CableSize> FindAll(string? text)
    {
        var s = Normalise(text ?? "");
        if (s.Length == 0) yield break;
        var whole = Parse(s);
        foreach (Match m in SizeRx.Matches(s))
        {
            var p = Parse(m.Value);
            if (p.IsValid) yield return p with { Conductor = whole.Conductor, Insulation = whole.Insulation };
        }
    }

    /// <summary>The text (upper case, normalised) with every size annotation removed - what is left can be a panel name.</summary>
    public static string Strip(string? text) => Regex.Replace(SizeRx.Replace(Normalise(text ?? ""), " "), @"\s+", " ").Trim();

    /// <summary>True when the text contains something that reads as a cable size.</summary>
    public static bool Contains(string? text) => FindAll(text).Any();

    private static string Normalise(string s)
    {
        s = s.ToUpperInvariant().Replace('×', 'X').Replace('*', 'X').Replace("²", "2").Replace("SQ.MM", "MM2").Replace("SQMM", "MM2").Replace("SQ MM", "MM2")
            .Replace("MM 2", "MM2").Replace("CORES", "C").Replace("CORE", "C").Replace("MM2", " MM2 ").Replace(',', '.');
        s = Regex.Replace(s, @"(\d)\s*MM2?\b", "$1 MM2");
        // "4C X 16 MM2" -> keep; drop the unit for the phase regex
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    public static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
