using System.Globalization;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Cables;

public static class CableFlagCodes
{
    /// <summary>Same FROM-TO (+ size) already claimed for the same stage (same or another subcontractor, any invoice).</summary>
    public const string Duplicate = "DUPLICATE FROM-TO";
    /// <summary>Claimed length above design / measured length x (1 + tolerance).</summary>
    public const string OverLength = "OVER LENGTH";
    /// <summary>Cumulative claimed for a run and stage above 100 % of its length.</summary>
    public const string Cumulative = "CUMULATIVE > 100%";
    /// <summary>Companion earth claimed without its phase cable, or with a different length (info).</summary>
    public const string Earth = "EARTH COMPANION";
    /// <summary>Termination / test (or handover) claimed before the pulling of that run.</summary>
    public const string StageOrder = "STAGE BEFORE PULLING";
    /// <summary>Run not in the register (no SLD / schedule / drawing) - created from statements only.</summary>
    public const string UnknownRun = "UNKNOWN RUN";
    /// <summary>The same run claimed under different panel spellings (caught by the normaliser).</summary>
    public const string Spelling = "DIFFERENT SPELLING";
    public static readonly string[] All = { Duplicate, OverLength, Cumulative, Earth, StageOrder, UnknownRun, Spelling };
}

public sealed class CableOptions
{
    /// <summary>Claimed length may exceed the design / measured length by this share before it is flagged (0.05 = 5 %).</summary>
    public double LengthTolerance { get; set; } = 0.05;
    /// <summary>Earth vs phase length difference (m) that is reported.</summary>
    public double EarthDiffM { get; set; } = 1;
    public static CableOptions Default => new();
}

/// <summary>One warning on a cable claim. Never blocking: it can be bypassed with a reason (audited).</summary>
public sealed record CableFlag(string Code, Verdict Severity, CableClaim Claim, CableRun? Run, string Message, IReadOnlyList<CableClaim> Related)
{
    public string Key => $"{Code}|{Claim.SourceKey}";
    public CableFlagDecision? Decision { get; init; }
    public bool IsBypassed => Decision != null;
    public string Tag => IsBypassed ? "BYPASSED" : VerdictText.Of(Severity);
    public string ClaimText => $"{Claim.Subcontractor} INV {Claim.InvoiceNo} {Claim.Stage} {Claim.FromRaw} -> {Claim.ToRaw} {Claim.SizeKey} {Claim.Qty:0.##} m";
    public override string ToString() => $"[{Code}] {ClaimText}: {Message}" + (IsBypassed ? $" (bypassed by {Decision!.By}: {Decision.Reason})" : "");
}

/// <summary>
/// [cables] Duplicate / conflict checks on cable claims. All of them are WARNINGS (Mohamed's rule: never block, bypass with a reason, audited).
/// Claims are taken in order (invoice no., entry time, id), so a duplicate always points at the EARLIER claim(s).
/// </summary>
public static class CableFlagEngine
{
    public static List<CableFlag> Evaluate(CableSnapshot snap, CableOptions? options = null, IEnumerable<CableClaim>? extra = null)
    {
        var o = options ?? CableOptions.Default;
        var runs = snap.Runs.ToDictionary(r => r.Id);
        var claims = snap.Claims.Concat(extra ?? Array.Empty<CableClaim>()).ToList();
        var ordered = claims.OrderBy(c => c.InvoiceNo <= 0 ? int.MaxValue : c.InvoiceNo).ThenBy(c => c.EnteredAt).ThenBy(c => c.Id <= 0 ? long.MaxValue : c.Id).ThenBy(c => c.SourceKey, StringComparer.Ordinal).ToList();
        var rank = new Dictionary<CableClaim, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < ordered.Count; i++) rank[ordered[i]] = i;
        var flags = new List<CableFlag>();
        CableRun? RunOf(CableClaim c) => c.RunId is long id && runs.TryGetValue(id, out var r) ? r : null;
        string Group(CableClaim c) => c.RunId is long id && id != 0 ? "R" + id : "K" + c.Route + "|" + c.SizeKey;

        // 1. duplicates (+ spelling variants) per run (or route + size) x stage x phase/earth
        foreach (var g in ordered.Where(c => c.Qty > 0).GroupBy(c => (Group(c), c.Stage, c.IsEarth)))
        {
            var list = g.ToList();
            for (var i = 1; i < list.Count; i++)
            {
                var c = list[i];
                var earlier = list.Take(i).ToList();
                var others = earlier.Where(e => !(e.Subcontractor == c.Subcontractor && e.InvoiceNo == c.InvoiceNo && e.StatementNo == c.StatementNo && e.Source == c.Source && e.SourceKey == c.SourceKey)).ToList();
                if (others.Count == 0) continue;
                var who = string.Join("; ", others.Take(4).Select(e => $"{e.Subcontractor} INV {e.InvoiceNo}{(e.StatementNo.Length > 0 ? " " + e.StatementNo : "")} {e.Qty:0.##} m"));
                var sameSub = others.All(e => e.Subcontractor == c.Subcontractor);
                flags.Add(new CableFlag(CableFlagCodes.Duplicate, Verdict.Over, c, RunOf(c),
                    $"{c.FromRaw} -> {c.ToRaw} {c.SizeKey}{(c.IsEarth ? " (earth)" : "")} {c.Stage} already claimed by {(sameSub ? "the same subcontractor" : "another subcontractor")}: {who}{(others.Count > 4 ? $" (+{others.Count - 4})" : "")}.",
                    others));
            }
            var first = list[0];
            foreach (var c in list.Skip(1))
                if (!SameSpelling(first.FromRaw, c.FromRaw) || !SameSpelling(first.ToRaw, c.ToRaw))
                {
                    var differs = !SameSpelling(first.FromRaw, c.FromRaw) ? $"'{c.FromRaw}' vs '{first.FromRaw}'" : $"'{c.ToRaw}' vs '{first.ToRaw}'";
                    flags.Add(new CableFlag(CableFlagCodes.Spelling, Verdict.Check, c, RunOf(c),
                        $"Same run claimed under another spelling ({differs}) - the normaliser treats them as one panel.", new[] { first }));
                }
        }

        // 2. single claim above the design / measured length, and 3. cumulative per run x stage
        foreach (var g in ordered.Where(c => RunOf(c)?.ReferenceLength is > 0).GroupBy(c => (c.RunId, c.Stage, c.IsEarth)))
        {
            var run = RunOf(g.First())!;
            var len = run.ReferenceLength!.Value;
            var limit = len * (1 + o.LengthTolerance);
            var cum = 0.0;
            var crossed = false;
            var seen = new List<CableClaim>();
            foreach (var c in g)
            {
                if (c.Qty > limit + 1e-9)
                    flags.Add(new CableFlag(CableFlagCodes.OverLength, Verdict.Check, c, run,
                        $"Claimed {c.Qty:0.##} m > {(run.MeasuredLength is > 0 ? "measured" : "design")} length {len:0.##} m (+{o.LengthTolerance:P0} tolerance).", Array.Empty<CableClaim>()));
                cum += c.QtyAfterSite;
                seen.Add(c);
                if (!crossed && cum > limit + 1e-9)
                {
                    crossed = true;
                    flags.Add(new CableFlag(CableFlagCodes.Cumulative, Verdict.Over, c, run,
                        $"Cumulative {c.Stage}{(c.IsEarth ? " (earth)" : "")} after SITE % = {cum:0.##} m = {cum / len:P0} of {len:0.##} m.", seen.Take(seen.Count - 1).ToList()));
                }
            }
        }

        // 4. companion earth (info): per subcontractor x invoice x run x stage
        foreach (var g in ordered.Where(c => c.Qty > 0 && (c.RunId is not null || c.Route.Length > 2)).GroupBy(c => (c.Subcontractor, c.InvoiceNo, Group(c), c.Stage)))
        {
            var phase = g.Where(c => !c.IsEarth).ToList();
            var earth = g.Where(c => c.IsEarth).ToList();
            var run = RunOf(g.First());
            if (earth.Count > 0 && phase.Count == 0)
                foreach (var e in earth)
                    flags.Add(new CableFlag(CableFlagCodes.Earth, Verdict.Open, e, run, $"Earth {e.SizeKey} claimed without its phase cable on this route in INV {e.InvoiceNo}.", Array.Empty<CableClaim>()));
            else if (earth.Count > 0)
            {
                var pq = phase.Sum(c => c.Qty);
                var eq = earth.Sum(c => c.Qty);
                if (Math.Abs(pq - eq) > o.EarthDiffM)
                    flags.Add(new CableFlag(CableFlagCodes.Earth, Verdict.Open, earth[0], run, $"Earth {eq:0.##} m vs phase {pq:0.##} m on the same route.", phase));
            }
            else if (run is { EarthSizeKey.Length: > 0 } && phase.Count > 0 && ordered.All(c => !(c.IsEarth && c.RunId == run.Id && c.Stage == g.Key.Stage)))
                flags.Add(new CableFlag(CableFlagCodes.Earth, Verdict.Open, phase[0], run, $"Run has a {run.EarthSizeKey} earth - not claimed yet.", Array.Empty<CableClaim>()));
        }

        // 5. termination / handover before pulling
        foreach (var c in ordered.Where(c => c.Qty > 0 && CableStages.Order(c.Stage) > 1))
        {
            var before = ordered.Where(p => p.Stage == CableStages.Pulling && p.Qty > 0 && Group(p) == Group(c) && p.IsEarth == c.IsEarth && rank[p] < rank[c]).ToList();
            if (before.Count == 0)
                flags.Add(new CableFlag(CableFlagCodes.StageOrder, Verdict.Check, c, RunOf(c), $"{c.Stage} claimed but the pulling of this run has not been claimed (in this or an earlier invoice).", Array.Empty<CableClaim>()));
        }

        // 6. run not in the register (from statements only)
        foreach (var c in ordered.Where(c => c.Qty > 0))
        {
            var run = RunOf(c);
            if (run is not null && run.Status != CableStatus.Provisional && run.Status != CableStatus.Rejected) continue;
            var why = new List<string>();
            if (run is null) why.Add("no run");
            else if (run.Status == CableStatus.Rejected) why.Add("run rejected in the register");
            else why.Add("run from statements only - no design length");
            var f = PanelNames.Parse(c.FromRaw);
            var t = PanelNames.Parse(c.ToRaw);
            if (f.Key.Length == 0) why.Add("FROM missing");
            else if (f.IsIncomplete) why.Add($"FROM '{c.FromRaw}' incomplete");
            if (t.Key.Length == 0) why.Add("TO missing");
            else if (t.IsIncomplete) why.Add($"TO '{c.ToRaw}' incomplete");
            flags.Add(new CableFlag(CableFlagCodes.UnknownRun, run is null || run.Status == CableStatus.Rejected ? Verdict.Check : Verdict.Open, c, run,
                $"Not in the cable register ({string.Join(", ", why)}).", Array.Empty<CableClaim>()));
        }

        var decisions = snap.Decisions.GroupBy(d => d.FlagKey).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.At).First());
        return flags.Select(f => decisions.TryGetValue(f.Key, out var d) ? f with { Decision = d } : f)
            .OrderByDescending(f => f.IsBypassed ? 0 : (int)f.Severity).ThenBy(f => rank[f.Claim]).ToList();
    }

    public static bool SameSpelling(string a, string b) => PanelNames.Tidy(a) == PanelNames.Tidy(b);

    /// <summary>Flags of the given claims only (statement preview, invoice, ledger entry), against everything already stored.</summary>
    public static List<CableFlag> For(CableSnapshot snap, IReadOnlyCollection<CableClaim> subject, CableOptions? options = null, bool subjectIsNew = true)
    {
        var all = Evaluate(snap, options, subjectIsNew ? subject : null);
        var set = new HashSet<CableClaim>(subject, ReferenceEqualityComparer.Instance);
        var keys = subject.Select(c => c.SourceKey).Where(k => k.Length > 0).ToHashSet();
        return all.Where(f => set.Contains(f.Claim) || keys.Contains(f.Claim.SourceKey)).ToList();
    }

    public static string Summary(IEnumerable<CableFlag> flags)
    {
        var open = flags.Where(f => !f.IsBypassed).ToList();
        if (open.Count == 0) return "no cable flags";
        return string.Join(", ", open.GroupBy(f => f.Code).Select(g => $"{g.Count()} {g.Key}"));
    }

    internal static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
