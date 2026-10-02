using System.Text;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Cables;

/// <summary>A panel / equipment name taken apart: type, building, zone, level and the remaining suffix tokens.</summary>
public sealed record PanelName(string Raw, string Key, string Type, string Building, string Zone, string Level, IReadOnlyList<string> Suffix, IReadOnlyList<string> Tokens)
{
    /// <summary>Recognised panel type and at least one location token (building / zone / level).</summary>
    public bool IsStructured => Type.Length > 0 && (Building.Length > 0 || Zone.Length > 0 || Level.Length > 0);
    /// <summary>Only a type ("SMDB") - too little to identify one panel.</summary>
    public bool IsIncomplete => Type.Length > 0 && Tokens.Count == 1;
    public bool IsPanelType => Type.Length > 0 && PanelNames.BoardTypes.Contains(Type);
    public string Compact => Key.Replace("-", "");
}

/// <summary>
/// [cables] Panel-name normaliser. Site statements and drawings write the same board in many ways
/// ('SMDB HT-Z1-LB2- CM 01', 'SMDB-HT-Z1-LB2-CM-01', 'smdb-ht-z1-lb2-cm-1', 'SMDB-HT-Z1-LBO2-CM-O1'): the key ignores spaces / hyphens / case,
/// leading zeros and O-for-0 inside level / zone / numbers, and puts the type, building, zone and level tokens in a fixed order, so
/// all of them get the same key. Similar but not identical names (FAN vs FANS) are only SUGGESTED (<see cref="Similarity"/>) and need a confirm.
/// Pattern: &lt;TYPE&gt;-&lt;BLDG BR/HT&gt;-&lt;ZONE Z1..&gt;-&lt;LEVEL LB1/LB2/L00/LL1..&gt;-&lt;SUFFIX&gt;.
/// </summary>
public static class PanelNames
{
    /// <summary>Distribution boards / switchboards / control panels.</summary>
    public static readonly HashSet<string> BoardTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MDB", "EMDB", "SMDB", "ESMDB", "SSMDB", "MSB", "SMSB", "EMSB", "LVP", "LVSB", "EMCC", "MCC", "DB", "EDB", "LDB", "ELDB", "PDB", "FDB", "MLDB",
        "FACP", "FCP", "CP", "LCP", "ATS", "UPS", "DP", "SDB", "TDB", "KDB", "MMDB", "SMB", "LP", "PP", "ELP", "FAP", "BMS", "PFC", "CAP", "VFD", "MV", "TR", "TX", "GEN", "DG",
    };

    /// <summary>Equipment fed by a cable (jet fans, air handling units, pumps ...).</summary>
    public static readonly HashSet<string> EquipmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "JF", "MAF", "EAF", "SAF", "FAF", "GRMAF", "CPEAF", "FFP", "AHU", "FAHU", "MAHU", "FCU", "SWWP", "WWSP", "WP", "SP", "BP", "HP", "TP", "FP", "JP", "CHILLER", "CH",
        "EF", "SF", "KEF", "SEF", "PEF", "LIFT", "ESC", "EV", "EVC", "HVAC", "CT", "CWP", "HWP", "CHWP", "VRF", "ODU", "IDU", "BOOSTER", "PUMP", "FAN", "HEATER", "WF",
    };

    private static readonly Regex Level = new(@"^(LB|LL|BL|B|L)([0-9O]{1,2})$", RegexOptions.Compiled);
    private static readonly Regex ZoneRx = new(@"^Z([0-9O]{1,2})$", RegexOptions.Compiled);
    private static readonly Regex SplitRx = new(@"[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex AlphaNum = new(@"^([A-Z]{2,})([0-9]+[A-Z]?)$", RegexOptions.Compiled);
    private static readonly Regex Num = new(@"^[0-9O]*[0-9][0-9O]*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> BuildingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BR"] = "BR", ["BRANDED"] = "BR", ["RES"] = "BR", ["HT"] = "HT", ["HOTEL"] = "HT", ["HTL"] = "HT",
    };

    private static readonly Dictionary<string, string> LevelWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GF"] = "L0", ["RF"] = "RF", ["ROOF"] = "RF", ["MEZZ"] = "MZ", ["MZ"] = "MZ",
    };

    /// <summary>Normalised key of a name (see the class remarks). Empty for an empty name.</summary>
    public static string KeyOf(string? raw, string? building = null) => Parse(raw, building).Key;

    /// <summary>BRANDED -> BR, HOTEL -> HT (the building token of panel names), "" otherwise.</summary>
    public static string BuildingCode(string? building) => (building ?? "").Trim().ToUpperInvariant() switch
    {
        "BRANDED" or "BR" or "RESIDENCE" or "RESIDENCES" => "BR",
        "HOTEL" or "HT" => "HT",
        _ => "",
    };

    /// <summary>
    /// Parses a name. <paramref name="building"/> (BRANDED / HOTEL) scopes names that do not carry a building token, so 'EV CHARGER-04' in the
    /// hotel and in the branded residences are two panels, while 'SMDB-Z3-LB1-01' in BRANDED = 'SMDB-BR-Z3-LB1-01'.
    /// </summary>
    public static PanelName Parse(string? raw, string? building = null)
    {
        raw ??= "";
        var defaultBuilding = building;
        var tokens = Tokens(raw);
        string type = "", zone = "", level = "";
        building = "";
        var suffix = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (i == 0 && (BoardTypes.Contains(t) || EquipmentTypes.Contains(t))) { type = t; continue; }
            if (building.Length == 0 && BuildingTokens.TryGetValue(t, out var b) && i <= 2) { building = b; continue; }
            if (zone.Length == 0 && ZoneRx.Match(t) is { Success: true } z) { zone = "Z" + Digits(z.Groups[1].Value); continue; }
            if (level.Length == 0 && Level.Match(t) is { Success: true } l && i > 0) { level = LevelPrefix(l.Groups[1].Value) + Digits(l.Groups[2].Value); continue; }
            if (level.Length == 0 && i > 0 && LevelWords.TryGetValue(t, out var lw)) { level = lw; continue; }
            suffix.AddRange(SplitSuffix(t));
        }
        if (building.Length == 0 && tokens.Count > 0) building = BuildingCode(defaultBuilding);
        var parts = new List<string>();
        if (type.Length > 0) parts.Add(type);
        if (building.Length > 0) parts.Add(building);
        if (zone.Length > 0) parts.Add(zone);
        if (level.Length > 0) parts.Add(level);
        parts.AddRange(suffix);
        var key = string.Join("-", parts);
        return new PanelName(raw.Trim(), key, type, building, zone, level, suffix, tokens);
    }

    /// <summary>Upper-case alphanumeric tokens ("SMDB HT-Z1-LB2- CM 01" -> SMDB, HT, Z1, LB2, CM, 01).</summary>
    public static List<string> Tokens(string raw) =>
        SplitRx.Split((raw ?? "").ToUpperInvariant().Replace('–', '-').Replace('—', '-')).Where(t => t.Length > 0).ToList();

    private static string LevelPrefix(string p) => p == "BL" ? "LB" : p;

    /// <summary>"O1" / "01" / "1" -> "1"; "00" / "OO" -> "0".</summary>
    private static string Digits(string s)
    {
        var d = s.Replace('O', '0').TrimStart('0');
        return d.Length == 0 ? "0" : d;
    }

    private static IEnumerable<string> SplitSuffix(string t)
    {
        if (Num.IsMatch(t) && t.Any(char.IsDigit)) { yield return Digits(t); yield break; }
        var m = AlphaNum.Match(t);
        if (m.Success && !BoardTypes.Contains(t) && !EquipmentTypes.Contains(t))
        {
            yield return m.Groups[1].Value;
            var n = m.Groups[2].Value;
            var letters = new string(n.SkipWhile(char.IsDigit).ToArray());
            yield return Digits(new string(n.TakeWhile(char.IsDigit).ToArray())) + letters;
            yield break;
        }
        yield return t;
    }

    /// <summary>True when the text looks like a board / equipment name worth treating as a node (SLD label, schedule cell).</summary>
    public static bool LooksLikePanel(string? text)
    {
        var p = Parse(text);
        if (p.Tokens.Count == 0 || p.Tokens.Count > 8) return false;
        if (p.IsStructured) return true;
        return p.Type.Length > 0 && p.Tokens.Count >= 2 && p.Tokens.Skip(1).Any(t => t.Any(char.IsDigit));
    }

    /// <summary>
    /// 0..1 similarity of two names. Conflicting type / building / zone / level -> 0. Otherwise the best of the character similarity of the
    /// compact keys (O read as 0) and the token overlap, reduced when one name lacks a location token the other has.
    /// </summary>
    public static double Similarity(string? a, string? b) => Similarity(Parse(a), Parse(b));

    public static double Similarity(PanelName a, PanelName b)
    {
        if (a.Key.Length == 0 || b.Key.Length == 0) return 0;
        if (a.Key == b.Key) return 1;
        if (Conflict(a.Type, b.Type) || Conflict(a.Building, b.Building) || Conflict(a.Zone, b.Zone) || Conflict(a.Level, b.Level)) return 0;
        // numbered panels are different panels: KT-01 vs KT-02, WF-03A vs WF-03B
        var na = a.Suffix.Where(t => t.Any(char.IsDigit)).ToList();
        var nb = b.Suffix.Where(t => t.Any(char.IsDigit)).ToList();
        if (na.Count > 0 && nb.Count > 0 && !na.SequenceEqual(nb)) return 0;
        if (a.Suffix.Count == b.Suffix.Count)
            for (var i = 0; i < a.Suffix.Count; i++)
            {
                var (x, y) = (a.Suffix[i], b.Suffix[i]);
                if (x != y && x.All(char.IsLetter) && y.All(char.IsLetter) && !x.StartsWith(y) && !y.StartsWith(x)) return 0;
            }
        var ca = a.Compact.Replace('O', '0');
        var cb = b.Compact.Replace('O', '0');
        if (ca == cb) return 0.97;
        var lev = 1.0 - (double)Levenshtein(ca, cb) / Math.Max(ca.Length, cb.Length);
        var ta = a.Key.Split('-').ToHashSet();
        var tb = b.Key.Split('-').ToHashSet();
        var jac = (double)ta.Intersect(tb).Count() / Math.Max(1, ta.Union(tb).Count());
        var s = Math.Max(lev, jac);
        foreach (var (x, y) in new[] { (a.Building, b.Building), (a.Zone, b.Zone), (a.Level, b.Level), (a.Type, b.Type) })
            if ((x.Length == 0) != (y.Length == 0)) s *= 0.85;
        return Math.Round(s, 3);
    }

    private static bool Conflict(string x, string y) => x.Length > 0 && y.Length > 0 && !string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Tidy display form: upper case, single spaces, hyphens without spaces around them.</summary>
    public static string Tidy(string? raw)
    {
        var s = Regex.Replace((raw ?? "").ToUpperInvariant().Trim(), @"\s+", " ");
        s = Regex.Replace(s, @"\s*-\s*", "-");
        return s;
    }

    /// <summary>Building from the name (BR / HT tokens) as BRANDED / HOTEL, or "".</summary>
    public static string BuildingOf(PanelName p) => p.Building switch { "BR" => Domain.Buildings.Branded, "HT" => Domain.Buildings.Hotel, _ => "" };

    /// <summary>Rank used to decide the feeding direction between two nodes (higher feeds lower).</summary>
    public static int Rank(PanelName p) => p.Type.ToUpperInvariant() switch
    {
        "TR" or "TX" or "MV" or "GEN" or "DG" => 100,
        "MDB" or "MSB" or "LVP" or "LVSB" or "EMDB" or "EMSB" or "MMDB" => 90,
        "ATS" => 85,
        "SMDB" or "ESMDB" or "SMSB" or "SSMDB" or "EMCC" or "MCC" or "PFC" => 70,
        "DB" or "EDB" or "LDB" or "ELDB" or "PDB" or "FDB" or "MLDB" or "SDB" or "TDB" or "KDB" or "SMB" or "LP" or "PP" or "ELP" or "DP" => 50,
        "FACP" or "FCP" or "CP" or "LCP" or "UPS" or "FAP" or "BMS" or "VFD" or "CAP" => 40,
        _ => p.Type.Length > 0 ? 20 : 10,
    };

    internal static string Signature(IEnumerable<string> parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p).Append('|');
        return sb.ToString();
    }
}

/// <summary>Result of resolving a raw name against the register.</summary>
public sealed record PanelMatch(string Raw, string Key, CablePanel? Panel, string How, IReadOnlyList<(CablePanel Panel, double Score)> Suggestions)
{
    public bool Known => Panel != null;
}

/// <summary>
/// Resolves raw names to register panels: exact normalised key, then confirmed aliases (learned), else NEW with fuzzy suggestions
/// (never merged automatically). Rejected aliases ("not the same") are never suggested again.
/// </summary>
public sealed class PanelResolver
{
    private readonly Dictionary<string, CablePanel> _byKey;
    private readonly Dictionary<string, string> _alias;
    private readonly HashSet<string> _rejected;
    private readonly List<(CablePanel P, PanelName N)> _parsed;

    public PanelResolver(IEnumerable<CablePanel> panels, IEnumerable<CablePanelAlias> aliases)
    {
        var list = panels.Where(p => p.Key.Length > 0).ToList();
        _byKey = list.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
        var al = aliases.ToList();
        _alias = al.Where(a => a.Kind != "REJECTED" && a.AliasKey.Length > 0).GroupBy(a => a.AliasKey).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Id).First().PanelKey);
        _rejected = al.Where(a => a.Kind == "REJECTED").Select(a => a.AliasKey + "=>" + a.PanelKey).ToHashSet();
        _parsed = list.Select(p => (p, PanelNames.Parse(p.Name.Length > 0 ? p.Name : p.Key, p.Building))).ToList();
    }

    /// <summary>Key after alias redirects (chains followed, max 5 hops).</summary>
    public string Canonical(string key)
    {
        for (var i = 0; i < 5 && _alias.TryGetValue(key, out var to) && to != key; i++) key = to;
        return key;
    }

    public PanelMatch Resolve(string? raw, string? building = null, double suggestFrom = 0.75, int maxSuggestions = 3)
    {
        var parsed = PanelNames.Parse(raw, building);
        var key = parsed.Key;
        if (key.Length == 0) return new(raw ?? "", "", null, "EMPTY", Array.Empty<(CablePanel, double)>());
        if (_byKey.TryGetValue(key, out var exact)) return new(raw!, key, exact, "EXACT", Array.Empty<(CablePanel, double)>());
        var canon = Canonical(key);
        if (canon != key && _byKey.TryGetValue(canon, out var aliased)) return new(raw!, canon, aliased, "ALIAS", Array.Empty<(CablePanel, double)>());
        var sugg = _parsed.Select(x => (x.P, Score: PanelNames.Similarity(parsed, x.N)))
            .Where(x => x.Score >= suggestFrom && !_rejected.Contains(key + "=>" + x.P.Key))
            .OrderByDescending(x => x.Score).Take(maxSuggestions).ToList();
        return new(raw!, key, null, "NEW", sugg);
    }
}
