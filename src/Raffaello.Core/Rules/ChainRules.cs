using Raffaello.Core.Chain;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Rules;

public sealed record Finding(Verdict Severity, string RuleCode, string Message);

/// <summary>Tunable thresholds for the rules engine (edited from Settings).</summary>
public sealed class RuleOptions
{
    /// <summary>Open WIR older than this many days becomes DUE.</summary>
    public int WirDueDays { get; set; } = 14;
    /// <summary>Allowed gap between SITE % and WIR % before it is flagged.</summary>
    public double SiteTolerance { get; set; } = 0.15;
    /// <summary>Default metres per piece for pipes / conduits delivered in PCS.</summary>
    public double PipeLengthM { get; set; } = 3.0;
    public double Epsilon { get; set; } = 0.0001;
    public DateTime Today { get; set; } = DateTime.Today;
}

/// <summary>
/// One house rule, applied to a single chain row. Rules are independent and individually tested,
/// so they can be added, removed or reworked without touching the engine.
/// </summary>
public interface IChainRule
{
    string Code { get; }
    string Title { get; }
    string Description { get; }
    IEnumerable<Finding> Evaluate(ChainRow row, RuleOptions o);
}

public sealed class GivenOverQsRule : IChainRule
{
    public string Code => "GIVEN>QS";
    public string Title => "Exceeded lines post at full qty";
    public string Description => "GIVEN above QS is posted at the full quantity and carries an OVER tag.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        var post = PostingRules.Post(r.Given, r.Qs);
        if (post.IsOver)
            yield return new(Verdict.Over, Code, $"GIVEN {r.Given:N0} > QS {r.Qs:N0}: posted at full qty, OVER +{post.Excess:N0}");
    }
}

public sealed class ClaimOverCapRule : IChainRule
{
    public string Code => "CLAIM>CAP";
    public string Title => "PROJECT QTY caps subcontractor claims";
    public string Description => "A claim above PROJECT QTY (or QS while PROJECT QTY is empty) is OVER; it is posted but never certified above the cap.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.Claimed > r.Cap + o.Epsilon)
        {
            var capName = r.ProjectQty.HasValue ? "PROJECT QTY" : "QS (PROJECT QTY empty)";
            yield return new(Verdict.Over, Code, $"CLAIMED {r.Claimed:N0} > {capName} {r.Cap:N0}: OVER +{r.Claimed - r.Cap:N0}");
        }
    }
}

public sealed class ClaimOverDoneRule : IChainRule
{
    public string Code => "CLAIM>DONE";
    public string Title => "Claimed above WIR";
    public string Description => "CLAIMED greater than DONE (approved WIR) means CHECK: hold before certifying.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.Claimed > r.Done + o.Epsilon)
            yield return new(Verdict.Check, Code, $"CLAIMED {r.Claimed:N0} > DONE (WIR) {r.Done:N0}: hold before certifying");
    }
}

public sealed class DoneOverGivenRule : IChainRule
{
    public string Code => "DONE>GIVEN";
    public string Title => "Done above given";
    public string Description => "Approved WIR quantity above what was given to the subcontractor needs checking.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.Done > r.Given + o.Epsilon)
            yield return new(Verdict.Check, Code, $"DONE {r.Done:N0} > GIVEN {r.Given:N0}: check the allocation");
    }
}

public sealed class SiteVsWirRule : IChainRule
{
    public string Code => "SITE%";
    public string Title => "Compare after SITE %";
    public string Description => "Progress is compared after applying SITE %: site well ahead of WIR means a WIR is due; WIR ahead of site needs checking.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.Qs <= 0) yield break;
        var gap = ProgressRules.SiteGap(r.SitePct, r.WirPct);
        if (gap > o.SiteTolerance)
            yield return new(Verdict.Due, Code, $"SITE {r.SitePct:P0} vs WIR {r.WirPct:P0}: raise WIR for {r.SiteQty - r.Done:N0}");
        else if (gap < -o.SiteTolerance)
            yield return new(Verdict.Check, Code, $"WIR {r.WirPct:P0} ahead of SITE {r.SitePct:P0}: verify on site");
    }
}

public sealed class OpenWirDueRule : IChainRule
{
    public string Code => "WIR-AGE";
    public string Title => "Open WIR overdue";
    public string Description => "An open WIR older than the due threshold is DUE for follow-up with the consultant.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.OpenWirs > 0 && r.OldestOpenWirDays > o.WirDueDays)
            yield return new(Verdict.Due, Code, $"{r.OpenWirs} open WIR ({r.LastWirNo}) waiting {r.OldestOpenWirDays} days");
    }
}

public sealed class OpenWorkRule : IChainRule
{
    public string Code => "OPEN";
    public string Title => "Work still open";
    public string Description => "Quantity not yet given, or given but not yet done.";
    public IEnumerable<Finding> Evaluate(ChainRow r, RuleOptions o)
    {
        if (r.Remaining > o.Epsilon)
            yield return new(Verdict.Open, Code, $"REMAINING {r.Remaining:N0} to give");
        else if (r.Done + o.Epsilon < r.Given)
            yield return new(Verdict.Open, Code, $"{r.Given - r.Done:N0} given, not yet done");
    }
}

/// <summary>Runs every registered rule over the chain rows and sets each row's verdict to its worst finding.</summary>
public sealed class RulesEngine
{
    public IReadOnlyList<IChainRule> Rules { get; }
    public RuleOptions Options { get; }

    public RulesEngine(RuleOptions? options = null, IEnumerable<IChainRule>? rules = null)
    {
        Options = options ?? new RuleOptions();
        Rules = (rules ?? DefaultRules()).ToList();
    }

    public static IEnumerable<IChainRule> DefaultRules() => new IChainRule[]
    {
        new GivenOverQsRule(), new ClaimOverCapRule(), new ClaimOverDoneRule(), new DoneOverGivenRule(),
        new SiteVsWirRule(), new OpenWirDueRule(), new OpenWorkRule(),
    };

    public void Apply(ChainRow row)
    {
        row.Findings.Clear();
        foreach (var rule in Rules) row.Findings.AddRange(rule.Evaluate(row, Options));
        row.Verdict = row.Findings.Count == 0 ? Verdict.Ok : row.Findings.Max(f => f.Severity);
    }

    public void Apply(IEnumerable<ChainRow> rows)
    {
        foreach (var r in rows) Apply(r);
    }
}
