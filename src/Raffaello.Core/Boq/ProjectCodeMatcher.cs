using System.Text.RegularExpressions;

namespace Raffaello.Core.Boq;

/// <summary>A project code from the project code list (e.g. B6-01-01-00-6-26-AM-3).</summary>
public sealed record ProjectCodeRef(string Code, string Description, string Unit);

public sealed record ProjectCodeMatchResult(int Items, int Exact, int Shifted, int Unmatched, IReadOnlyList<string> Issues)
{
    public string Summary => Items == 0 ? "no project code list loaded" :
        $"project codes: {Exact + Shifted:N0} / {Items:N0} items ({Shifted:N0} via a page map), {Unmatched:N0} without a project code";
}

/// <summary>
/// Project code = {bill}-01-01-00-{bill no}-{CSI division}-{Ref}-{page} (Section / Page / Rev are always 01 / 01 / 00).
/// The contract BOQ has no division, so an item is matched on bill + Ref + page, the division must suit the section
/// (R electrical = 26 / 27 / 28) and the description must agree. Some bills are numbered one page apart from the project
/// codes (B2 / B5), so page +-1 is tried with a stricter description check. The project code becomes BoqCode; the printed
/// reference stays in Section / Page / Ref.
/// </summary>
public static class ProjectCodeMatcher
{
    private static readonly Regex Code = new(@"^(B\d+)-\d{2}-\d{2}-\d{2}-(\d+)-(\d+)-([A-Z]+)-(\d+)(?:-.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[A-Z0-9]+", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new() { "THE", "AND", "OF", "TO", "IN", "FOR", "WITH", "ALL", "A", "AN", "ON", "AS", "BE", "OR", "INCLUDING", "COMPLETE", "SUPPLY", "INSTALL", "INSTALLATION" };

    private static readonly Dictionary<string, int[]> Divisions = new()
    {
        ["R"] = new[] { 26, 27, 28 }, ["Q"] = new[] { 21, 22, 23, 25, 28 },
    };

    private sealed record Cand(ProjectCodeRef C, int Div, int Page, HashSet<string> W);

    public static ProjectCodeMatchResult Apply(IList<BoqLine> rows, IEnumerable<ProjectCodeRef> codes)
    {
        var byRef = new Dictionary<(string Bill, string Ref), List<Cand>>();
        foreach (var c in codes)
        {
            var m = Code.Match(c.Code.Trim());
            if (!m.Success) continue;
            var key = (m.Groups[1].Value.ToUpperInvariant(), m.Groups[4].Value.ToUpperInvariant());
            if (!byRef.TryGetValue(key, out var list)) byRef[key] = list = new();
            list.Add(new Cand(c, int.Parse(m.Groups[3].Value), int.Parse(m.Groups[5].Value), Words(c.Description)));
        }
        var items = rows.Where(r => !r.IsHeading && r.Page > 0).ToList();
        if (byRef.Count == 0) return new(0, 0, 0, items.Count, Array.Empty<string>());

        List<Cand> CandsOf(BoqLine r)
        {
            if (!byRef.TryGetValue((r.Bill.ToUpperInvariant(), r.Ref.ToUpperInvariant()), out var list)) return new();
            var allowed = Divisions.TryGetValue(r.Section, out var d) ? d : null;
            return list.Where(x => allowed is null || allowed.Contains(x.Div)).ToList();
        }

        // pass 1: strong description matches vote for "printed page -> (division, code page)" (project code pages
        // restart per division, e.g. B2 distribution boards on BOQ page R/3 are B2-..-2-27-C-1)
        var votes = new Dictionary<(string, string, int), Dictionary<(int Div, int Page), int>>();
        foreach (var r in items)
        {
            var w = Words(r.Description);
            if (w.Count < 2) continue;
            var scored = CandsOf(r).Select(x => (x, S: Similarity(w, x.W))).OrderByDescending(x => x.S).ToList();
            if (scored.Count == 0 || scored[0].S < 0.6 || scored.Count > 1 && scored[1].S > scored[0].S - 0.1) continue;
            var pk = (r.Bill, r.Section, r.Page);
            if (!votes.TryGetValue(pk, out var v)) votes[pk] = v = new();
            var target = (scored[0].x.Div, scored[0].x.Page);
            v[target] = v.GetValueOrDefault(target) + 1;
        }
        var map = votes.ToDictionary(kv => kv.Key, kv => kv.Value.OrderByDescending(x => x.Value).First().Key);

        var score = new Dictionary<BoqLine, double>();
        var keyOf = new Dictionary<BoqLine, (string Code, string Source)>();
        foreach (var r in items)
        {
            var w = Words(r.Description);
            var cands = CandsOf(r);
            Cand? best = null; var source = "PROJECT CODE";
            if (map.TryGetValue((r.Bill, r.Section, r.Page), out var target))
            {
                best = Pick(cands.Where(x => x.Div == target.Div && x.Page == target.Page).ToList(), w, 0.1);
                if (best != null && target.Page != r.Page) source = "PROJECT CODE (PAGE MAP)";
            }
            if (best is null) best = Pick(cands.Where(x => x.Page == r.Page).ToList(), w, 0.15);
            if (best is null)
            {
                best = Pick(cands.Where(x => Math.Abs(x.Page - r.Page) == 1).ToList(), w, 0.35);
                source = "PROJECT CODE (PAGE MAP)";
            }
            if (best is null) continue;
            keyOf[r] = (best.C.Code.ToUpperInvariant(), source);
            score[r] = Similarity(w, best.W) + (source == "PROJECT CODE" ? 0.05 : 0);
        }
        // a project code belongs to one contract BOQ item: the best description match keeps it, the others stay unmatched
        var issues = new List<string>();
        foreach (var g in keyOf.GroupBy(kv => kv.Value.Code).Where(g => g.Count() > 1))
        {
            var keep = g.OrderByDescending(kv => score[kv.Key]).First().Key;
            var dropped = g.Where(kv => kv.Key != keep).Select(kv => kv.Key).ToList();
            foreach (var d in dropped) keyOf.Remove(d);
            if (issues.Count < 20) issues.Add($"project code {g.Key} fits {g.Count()} items - kept {keep.PageRef}, left without code: {string.Join(", ", dropped.Select(x => x.PageRef))}");
        }
        int exact = 0, shifted = 0;
        foreach (var (r, k) in keyOf)
        {
            r.BoqCode = k.Code; r.CodeSource = k.Source;
            if (k.Source == "PROJECT CODE") exact++; else shifted++;
        }
        return new(items.Count, exact, shifted, items.Count - exact - shifted, issues);
    }

    private static Cand? Pick(List<Cand> cands, HashSet<string> w, double min)
    {
        if (cands.Count == 0) return null;
        var scored = cands.Select(x => (x, S: Similarity(w, x.W))).OrderByDescending(x => x.S).ToList();
        if (scored.Count == 1 && min < 0.2 && (scored[0].S >= 0.05 || w.Count == 0 || scored[0].x.C.Description.Length == 0)) return scored[0].x;
        return scored[0].S >= min ? scored[0].x : null;
    }

    private static HashSet<string> Words(string s) =>
        Word.Matches(s.ToUpperInvariant()).Select(m => m.Value).Where(x => x.Length > 1 && !Stop.Contains(x)).ToHashSet();

    private static double Similarity(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / Math.Min(a.Count, b.Count);
}
