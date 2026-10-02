using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Variations;

/// <summary>An item a variation line can point at: owner BOQ / E-Promise row or a subcontract item.</summary>
public sealed record SuggestCandidate(string Source, long Id, string Code, string Description, string Unit, double Rate, string Extra = "")
{
    public static IEnumerable<SuggestCandidate> From(IEnumerable<BoqItem> boq, IEnumerable<ContractItem> contract)
    {
        foreach (var b in boq)
        {
            var desc = string.Join(" - ", new[] { b.Description, b.BudgetResource }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            yield return new SuggestCandidate("BOQ", b.Id, b.ItemCode, desc, b.Unit, b.Rate, b.Bill);
        }
        foreach (var c in contract)
            yield return new SuggestCandidate("CONTRACT", c.Id, $"{c.ContractNo} #{c.ItemNo}", c.Description, c.Unit, c.Rate, c.Section);
    }
}

public sealed record Suggestion(SuggestCandidate Candidate, double Score, double KeywordScore, double AttributeScore, string Why)
{
    public string Source => Candidate.Source;
    public string Code => Candidate.Code;
    public string Description => Candidate.Description;
    public string Unit => Candidate.Unit;
    public double Rate => Candidate.Rate;
    public string ScoreText => Score.ToString("P0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Technical attributes that decide whether two electrical items are "the same thing": cable cores x size, conductor,
/// fire rating / LSOH, voltage; conduit size + type; amps, poles, ways, watts, IP rating; device family.
/// </summary>
public static class AttributeFingerprint
{
    private static readonly (string Feature, Regex Rx)[] Flags =
    {
        ("CU", new(@"\b(cu|copper|نحاس)\b", RegexOptions.IgnoreCase)),
        ("AL", new(@"\b(al|aluminium|aluminum)\b", RegexOptions.IgnoreCase)),
        ("FIRE", new(@"\b(mica|fire[\s-]?(rated|resistant|proof)|fr|fp200|cwz)\b", RegexOptions.IgnoreCase)),
        ("LSOH", new(@"\b(lsoh|lszh|lsf|hf|halogen[\s-]?free)\b", RegexOptions.IgnoreCase)),
        ("ARMOURED", new(@"\b(swa|armou?red|sta|awa)\b", RegexOptions.IgnoreCase)),
        ("PVC", new(@"\bpvc\b", RegexOptions.IgnoreCase)),
        ("EMT", new(@"\bemt\b", RegexOptions.IgnoreCase)),
        ("GI", new(@"\b(gi|galvani[sz]ed|rigid steel|rs)\b", RegexOptions.IgnoreCase)),
        ("FLEX", new(@"\bflex(ible)?\b", RegexOptions.IgnoreCase)),
        ("SOCKET", new(@"\b(socket|outlet|receptacle|power point|small power|بريزة|مخرج)\b", RegexOptions.IgnoreCase)),
        ("SWITCH", new(@"\b(switch(es)?|مفتاح)\b", RegexOptions.IgnoreCase)),
        ("ISOLATOR", new(@"\bisolators?\b", RegexOptions.IgnoreCase)),
        ("LIGHT", new(@"\b((light(ing)?|luminaire|downlight|fixture|fitting)s?|انارة|إنارة)\b", RegexOptions.IgnoreCase)),
        ("EMERGENCY", new(@"\bemergency\b", RegexOptions.IgnoreCase)),
        ("DATA", new(@"\b(data|cat ?6a?|rj45|telephone)\b", RegexOptions.IgnoreCase)),
        ("TRAY", new(@"\b((cable )?tray|trunking|ladder)\b", RegexOptions.IgnoreCase)),
        ("PANEL", new(@"\b(distribution board|db|smdb|mdb|panel ?board|panel)\b", RegexOptions.IgnoreCase)),
        ("EARTH", new(@"\b(earth(ing)?|grounding)\b", RegexOptions.IgnoreCase)),
        ("CABLE", new(@"\b((cable|wire|wiring)s?|كابل|كيبل|اسلاك|أسلاك)\b", RegexOptions.IgnoreCase)),
        ("CONDUIT", new(@"\b(conduits?|مواسير|ماسورة)\b", RegexOptions.IgnoreCase)),
        ("FIRE ALARM", new(@"\b(fire alarm|smoke detector|heat detector|call point)\b", RegexOptions.IgnoreCase)),
        ("CCTV", new(@"\b(cctv|camera)\b", RegexOptions.IgnoreCase)),
        ("GRMS", new(@"\bgrms\b", RegexOptions.IgnoreCase)),
    };

    private static readonly Regex CoresSize = new(@"\b(\d{1,2})\s*(?:c|core|cores|x)\s*[x×*]?\s*(\d{1,3}(?:\.\d+)?)\s*(?:mm2|mm²|sq\.?\s?mm|sqmm|mm)?", RegexOptions.IgnoreCase);
    private static readonly Regex SizeMm = new(@"\b(\d{2,3})\s*mm\b(?!2|²)", RegexOptions.IgnoreCase);
    private static readonly Regex Amps = new(@"\b(\d{1,4})\s*a(mp)?s?\b", RegexOptions.IgnoreCase);
    private static readonly Regex Watts = new(@"\b(\d{1,4}(?:\.\d)?)\s*w(att)?s?\b", RegexOptions.IgnoreCase);
    private static readonly Regex Ways = new(@"\b(\d{1,3})\s*-?\s*ways?\b", RegexOptions.IgnoreCase);
    private static readonly Regex Ip = new(@"\bip\s?(\d{2})\b", RegexOptions.IgnoreCase);
    private static readonly Regex Poles = new(@"\b(sp|dp|tp|tpn|1p|2p|3p|4p)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Kv = new(@"\b(0\.6\s*/\s*1|1|11|13\.8|33)\s*kv\b", RegexOptions.IgnoreCase);

    public static HashSet<string> Of(string text)
    {
        var f = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return f;
        foreach (var (feature, rx) in Flags) if (rx.IsMatch(text)) f.Add(feature);
        foreach (Match m in CoresSize.Matches(text))
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cores) && cores is >= 1 and <= 61)
                f.Add($"CABLE {cores}X{Num(m.Groups[2].Value)}");
        foreach (Match m in SizeMm.Matches(text)) f.Add($"SIZE {m.Groups[1].Value}MM");
        foreach (Match m in Amps.Matches(text)) f.Add($"{m.Groups[1].Value}A");
        foreach (Match m in Watts.Matches(text)) f.Add($"{Num(m.Groups[1].Value)}W");
        foreach (Match m in Ways.Matches(text)) f.Add($"{m.Groups[1].Value} WAY");
        foreach (Match m in Ip.Matches(text)) f.Add($"IP{m.Groups[1].Value}");
        foreach (Match m in Poles.Matches(text)) f.Add(m.Groups[1].Value.ToUpperInvariant() switch { "1P" => "SP", "2P" => "DP", "3P" => "TP", "4P" => "TPN", var p => p });
        foreach (Match m in Kv.Matches(text)) f.Add(Regex.Replace(m.Groups[1].Value, @"\s", "") + "KV");
        return f;
    }

    private static string Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : s;

    /// <summary>Features that must agree when both sides have one (a 4x16 cable is not a 4x25 cable).</summary>
    public static bool IsSizing(string feature) =>
        feature.StartsWith("CABLE ", StringComparison.Ordinal) || feature.StartsWith("SIZE ", StringComparison.Ordinal) || feature.EndsWith(" WAY", StringComparison.Ordinal)
        || Regex.IsMatch(feature, @"^\d+(\.\d+)?[AW]$") || feature.StartsWith("IP", StringComparison.Ordinal);

    private static string Family(string feature) => feature switch
    {
        _ when feature.StartsWith("CABLE ", StringComparison.Ordinal) => "CABLE",
        _ when feature.StartsWith("SIZE ", StringComparison.Ordinal) => "SIZE",
        _ when feature.EndsWith(" WAY", StringComparison.Ordinal) => "WAY",
        _ when feature.StartsWith("IP", StringComparison.Ordinal) => "IP",
        _ when feature.EndsWith('A') => "AMP",
        _ when feature.EndsWith('W') => "WATT",
        _ => feature,
    };

    /// <summary>0..1: weighted share of the query's features found in the candidate (sizes weigh 3), minus a penalty per conflicting size.</summary>
    public static (double Score, List<string> Matched, List<string> Conflicts) Compare(IReadOnlySet<string> query, IReadOnlySet<string> cand)
    {
        if (query.Count == 0) return (0, new(), new());
        var matched = query.Where(cand.Contains).ToList();
        var conflicts = new List<string>();
        foreach (var fam in query.Where(IsSizing).Select(Family).Distinct())
        {
            var q = query.Where(x => IsSizing(x) && Family(x) == fam).ToHashSet();
            var c = cand.Where(x => IsSizing(x) && Family(x) == fam).ToHashSet();
            if (c.Count > 0 && !q.Overlaps(c)) conflicts.Add($"{string.Join("/", q)} vs {string.Join("/", c)}");
        }
        static double W(string f) => IsSizing(f) ? 3 : 1; // an exact size (4x16, 32A, 48 way) says more than a shared word
        var score = matched.Sum(W) / query.Sum(W) - 0.35 * conflicts.Count;
        return (Math.Clamp(score, 0, 1), matched, conflicts);
    }
}

/// <summary>
/// Suggests BOQ / contract items related to a variation: keyword relevance (TF-IDF over the candidate list, English and
/// Arabic words) blended with the attribute fingerprint. Deterministic and offline; an optional AI re-ranker can refine it.
/// </summary>
public sealed class BoqSuggester
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "of", "to", "in", "on", "at", "by", "or", "as", "be", "is", "are", "all", "any", "per", "including", "incl", "complete",
        "supply", "install", "installation", "installing", "providing", "provide", "fixing", "testing", "commissioning", "works", "work", "item", "items",
        "as", "required", "specified", "drawings", "drawing", "approved", "shall", "etc", "type", "no", "nos", "nr", "from", "this", "that", "please", "kindly",
        "اعمال", "توريد", "تركيب", "مع", "في", "من", "على", "الى", "إلى",
    };

    private readonly List<(SuggestCandidate C, Dictionary<string, int> Tf, int Len, HashSet<string> Attr)> _index = new();
    private readonly Dictionary<string, double> _idf = new(StringComparer.Ordinal);

    public BoqSuggester(IEnumerable<SuggestCandidate> candidates)
    {
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var tokens = Tokens(c.Description + " " + c.Extra);
            var tf = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var t in tf.Keys) df[t] = df.GetValueOrDefault(t) + 1;
            _index.Add((c, tf, Math.Max(1, tokens.Count), AttributeFingerprint.Of(c.Description)));
        }
        var n = Math.Max(1, _index.Count);
        foreach (var (t, d) in df) _idf[t] = Math.Log(1 + (double)n / d);
    }

    public int Count => _index.Count;

    public static List<string> Tokens(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var w = sb.ToString().ToLowerInvariant();
            sb.Clear();
            if (w.Length < 2 && !w.All(char.IsDigit)) return;
            if (Stop.Contains(w)) return;
            if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) && !w.EndsWith("ses", StringComparison.Ordinal)) w = w[..^2];
            else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            list.Add(w);
        }
        foreach (var ch in text ?? "")
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch); else Flush();
        }
        Flush();
        return list;
    }

    /// <summary>Top suggestions for the variation text (title, description, documents). Title words weigh more.</summary>
    public List<Suggestion> Suggest(string title, string body, int take = 15, string? unit = null)
    {
        var q = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string text, double w)
        {
            foreach (var t in Tokens(text)) q[t] = q.GetValueOrDefault(t) + w;
        }
        Add(title, 3);
        Add(body.Length > 20000 ? body[..20000] : body, 1);
        if (q.Count == 0) return new();
        // cap the weight of words that repeat a lot in a long document
        foreach (var k in q.Keys.ToList()) q[k] = Math.Min(q[k], 6);
        var qAttr = AttributeFingerprint.Of(title + "\n" + body);
        var qNorm = Math.Sqrt(q.Sum(kv => Math.Pow(kv.Value * _idf.GetValueOrDefault(kv.Key, 0), 2)));
        var raw = new List<(SuggestCandidate C, double Kw, double Attr, List<string> Words, List<string> Matched, List<string> Conflicts)>();
        foreach (var (c, tf, len, attr) in _index)
        {
            double dot = 0, cNorm = 0;
            var words = new List<string>();
            foreach (var (t, n) in tf)
            {
                var idf = _idf.GetValueOrDefault(t);
                cNorm += Math.Pow(n * idf, 2);
                if (q.TryGetValue(t, out var qw)) { dot += qw * idf * n * idf; words.Add(t); }
            }
            var kw = qNorm <= 0 || cNorm <= 0 ? 0 : dot / (qNorm * Math.Sqrt(cNorm));
            var (a, matched, conflicts) = AttributeFingerprint.Compare(qAttr, attr);
            if (kw <= 0 && a <= 0) continue;
            raw.Add((c, kw, a, words, matched, conflicts));
        }
        if (raw.Count == 0) return new();
        var maxKw = Math.Max(1e-9, raw.Max(r => r.Kw));
        var list = raw.Select(r =>
        {
            var kwN = r.Kw / maxKw;
            var score = qAttr.Count == 0 ? kwN : 0.5 * kwN + 0.5 * r.Attr;
            if (unit != null && r.C.Unit.Length > 0 && !UnitsAgree(unit, r.C.Unit)) score *= 0.7;
            if (r.Conflicts.Count > 0) score *= 0.6;
            var why = new List<string>();
            if (r.Words.Count > 0) why.Add("words: " + string.Join(", ", r.Words.OrderByDescending(w => _idf.GetValueOrDefault(w)).Take(5)));
            if (r.Matched.Count > 0) why.Add("attributes: " + string.Join(", ", r.Matched));
            if (r.Conflicts.Count > 0) why.Add("size differs: " + string.Join("; ", r.Conflicts));
            return new Suggestion(r.C, Math.Round(Math.Clamp(score, 0, 1), 3), Math.Round(kwN, 3), Math.Round(r.Attr, 3), string.Join(" | ", why));
        });
        return list.OrderByDescending(s => s.Score).ThenBy(s => s.Candidate.Code, StringComparer.Ordinal).Take(take).ToList();
    }

    public static bool UnitsAgree(string a, string b)
    {
        static string N(string u)
        {
            var s = (u ?? "").Trim().ToLowerInvariant().Replace(".", "");
            return s switch
            {
                "no" or "nos" or "nr" or "ea" or "each" or "pcs" or "pc" or "pt" or "point" or "points" or "عدد" => "no",
                "m" or "lm" or "rm" or "mtr" or "meter" or "metre" or "م" or "مط" or "م ط" => "m",
                "set" or "sets" or "lot" or "ls" or "lump sum" => "set",
                "m2" or "sqm" => "m2",
                _ => s,
            };
        }
        return N(a) == N(b);
    }
}
