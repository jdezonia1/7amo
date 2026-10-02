using Raffaello.Core.Export;

namespace Raffaello.Core.Cables;

/// <summary>One run with what was claimed on it per stage (after SITE %), for the register view and the export.</summary>
public sealed record RunProgress(CableRun Run, double PulledM, double TerminatedM, double HandedOverM, double EarthPulledM, int Claims, int OpenFlags, string Subcontractors)
{
    public double? Length => Run.ReferenceLength;
    /// <summary>Pulling progress 0..1 (claimed after SITE % / length; claimed &gt; 0 with no length = 1).</summary>
    public double PullPct => Length is > 0 ? PulledM / Length.Value : PulledM > 0 ? 1 : 0;
    public double TermPct => Length is > 0 ? TerminatedM / Length.Value : TerminatedM > 0 ? 1 : 0;
    public double HandPct => Length is > 0 ? HandedOverM / Length.Value : HandedOverM > 0 ? 1 : 0;
    /// <summary>Weighted by the payment split 70 / 20 / 10.</summary>
    public double WeightedPct => Math.Min(1, PullPct) * 0.7 + Math.Min(1, TermPct) * 0.2 + Math.Min(1, HandPct) * 0.1;
    public string Status => OpenFlags > 0 ? "FLAGGED" : WeightedPct >= 0.999 ? "DONE" : PulledM > 0 ? "IN PROGRESS" : "NOT STARTED";
}

/// <summary>A node of the SLD-like panel tree (parent = feeding panel).</summary>
public sealed class PanelNode
{
    public required CablePanel Panel { get; init; }
    public List<PanelNode> Children { get; } = new();
    public List<RunProgress> Feeders { get; } = new();
    public double Progress { get; set; }
    public int Depth { get; set; }
}

/// <summary>[cables] Register progress, panel tree and the Excel export (house style: #A6A6A6 bold black headers).</summary>
public static class CableReports
{
    public static List<RunProgress> Progress(CableSnapshot snap, IReadOnlyList<CableFlag>? flags = null)
    {
        var byRun = snap.Claims.Where(c => c.RunId is > 0).GroupBy(c => c.RunId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var flagCount = (flags ?? Array.Empty<CableFlag>()).Where(f => !f.IsBypassed && f.Run != null).GroupBy(f => f.Run!.Id).ToDictionary(g => g.Key, g => g.Count());
        return snap.Runs.Select(r =>
        {
            var cl = byRun.GetValueOrDefault(r.Id) ?? new List<CableClaim>();
            double S(string stage, bool earth) => cl.Where(c => c.Stage == stage && c.IsEarth == earth).Sum(c => c.QtyAfterSite);
            return new RunProgress(r, S(CableStages.Pulling, false), S(CableStages.Termination, false), S(CableStages.Handover, false), S(CableStages.Pulling, true),
                cl.Count, flagCount.GetValueOrDefault(r.Id), string.Join(", ", cl.Select(c => c.Subcontractor).Distinct().OrderBy(x => x)));
        }).ToList();
    }

    /// <summary>Panels as a tree (roots = panels without a known feeder), each with its outgoing runs and a progress figure.</summary>
    public static List<PanelNode> Tree(CableSnapshot snap, IReadOnlyList<RunProgress> progress)
    {
        var nodes = snap.Panels.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => new PanelNode { Panel = g.First() });
        foreach (var rp in progress)
            if (nodes.TryGetValue(rp.Run.FromKey, out var n)) n.Feeders.Add(rp);
        var roots = new List<PanelNode>();
        foreach (var n in nodes.Values)
        {
            var parent = n.Panel.ParentKey;
            if (parent.Length > 0 && parent != n.Panel.Key && nodes.TryGetValue(parent, out var p) && !IsAncestor(n, p, nodes)) p.Children.Add(n);
            else roots.Add(n);
        }
        void Walk(PanelNode n, int d, HashSet<PanelNode> seen)
        {
            if (!seen.Add(n)) return;
            n.Depth = d;
            foreach (var c in n.Children) Walk(c, d + 1, seen);
            var all = n.Feeders.Select(f => f.WeightedPct).Concat(n.Children.Select(c => c.Progress)).ToList();
            n.Progress = all.Count == 0 ? 0 : all.Average();
            n.Children.Sort((a, b) => string.CompareOrdinal(a.Panel.Name, b.Panel.Name));
        }
        var seen = new HashSet<PanelNode>();
        foreach (var r in roots) Walk(r, 0, seen);
        return roots.OrderByDescending(r => r.Children.Count + r.Feeders.Count).ThenBy(r => r.Panel.Name).ToList();
    }

    private static bool IsAncestor(PanelNode node, PanelNode candidateParent, Dictionary<string, PanelNode> nodes)
    {
        var k = candidateParent.Panel.ParentKey;
        for (var i = 0; i < 50 && k.Length > 0; i++)
        {
            if (k == node.Panel.Key) return true;
            k = nodes.TryGetValue(k, out var p) ? p.Panel.ParentKey : "";
        }
        return false;
    }

    public static IEnumerable<PanelNode> Flatten(IEnumerable<PanelNode> roots)
    {
        foreach (var r in roots)
        {
            yield return r;
            foreach (var c in Flatten(r.Children)) yield return c;
        }
    }

    public static List<ExportSheet> Export(CableSnapshot snap, IReadOnlyList<CableFlag> flags)
    {
        var prog = Progress(snap, flags);
        var runs = snap.Runs.ToDictionary(r => r.Id);
        var register = new ExportSheet
        {
            Name = "CABLE REGISTER", Title = "CABLE REGISTER", Subtitle = $"{snap.Runs.Count} runs, {snap.Panels.Count} panels - progress after SITE % (70 / 20 / 10 stage split)",
            Columns = new()
            {
                new("REF"), new("BUILDING"), new("LEVEL"), new("FROM", Width: 28), new("TO", Width: 28), new("SIZE"), new("EARTH"), new("CU/AL"), new("TYPE"),
                new("DESIGN M", ColumnKind.Number), new("MEASURED M", ColumnKind.Number), new("PULLED M", ColumnKind.Number), new("PULL %", ColumnKind.Percent),
                new("TERM & TEST M", ColumnKind.Number), new("HANDOVER M", ColumnKind.Number), new("EARTH PULLED M", ColumnKind.Number), new("WEIGHTED %", ColumnKind.Percent),
                new("STATUS"), new("REGISTER"), new("SOURCE", Width: 30), new("SUBCONTRACTORS", Width: 24), new("OPEN FLAGS", ColumnKind.Integer),
            },
            Rows = prog.OrderBy(p => p.Run.Building).ThenBy(p => p.Run.FromName).ThenBy(p => p.Run.ToName).Select(p => new object?[]
            {
                p.Run.Ref, p.Run.Building, p.Run.Level, p.Run.FromName, p.Run.ToName, p.Run.SizeKey, p.Run.EarthSizeKey, p.Run.Conductor, p.Run.Insulation,
                p.Run.DesignLength, p.Run.MeasuredLength, p.PulledM, p.PullPct, p.TerminatedM, p.HandedOverM, p.EarthPulledM, p.WeightedPct,
                p.Status, p.Run.Status, $"{p.Run.SourceKind} {p.Run.SourceDoc}{(p.Run.SourcePage > 0 ? " p" + p.Run.SourcePage : "")}".Trim(), p.Subcontractors, p.OpenFlags,
            }).ToList(),
        };
        var flagByClaim = flags.GroupBy(f => f.Claim.SourceKey).ToDictionary(g => g.Key, g => g.ToList());
        var claims = new ExportSheet
        {
            Name = "CABLE CLAIMS", Title = "CABLE CLAIMS", Subtitle = $"{snap.Claims.Count} claims",
            Columns = new()
            {
                new("SUBCONTRACTOR"), new("INVOICE #", ColumnKind.Integer), new("STATEMENT"), new("STAGE"), new("BUILDING"), new("LEVEL"), new("FROM", Width: 28), new("TO", Width: 28),
                new("SIZE"), new("EARTH"), new("QTY M", ColumnKind.Number), new("SITE %", ColumnKind.Percent), new("WIR %", ColumnKind.Percent), new("FINAL QTY", ColumnKind.Number),
                new("WIR NO"), new("RUN"), new("MATCH"), new("SOURCE"), new("IN LEDGER"), new("FLAGS", Width: 40),
            },
            Rows = snap.Claims.OrderBy(c => c.Subcontractor).ThenBy(c => c.InvoiceNo).ThenBy(c => c.FromRaw).Select(c => new object?[]
            {
                c.Subcontractor, c.InvoiceNo, c.StatementNo, c.Stage, c.Building, c.Level, c.FromRaw, c.ToRaw, c.SizeKey, c.IsEarth ? "EARTH" : "", c.Qty, c.SitePct, c.WirPct, c.QtyAfterWir,
                c.WirNo, c.RunId is long id && runs.TryGetValue(id, out var r) ? r.Ref : "", c.MatchStatus, c.Source, c.LedgerSourceKey.Length > 0 ? "YES" : "",
                flagByClaim.TryGetValue(c.SourceKey, out var fl) ? string.Join("; ", fl.Select(f => f.IsBypassed ? f.Code + " (bypassed)" : f.Code)) : "",
            }).ToList(),
        };
        var flagSheet = new ExportSheet
        {
            Name = "CABLE FLAGS", Title = "CABLE FLAGS (warnings - bypass with a reason)", Subtitle = CableFlagEngine.Summary(flags),
            Columns = new() { new("FLAG"), new("SEVERITY"), new("SUBCONTRACTOR"), new("INVOICE #", ColumnKind.Integer), new("STAGE"), new("FROM", Width: 28), new("TO", Width: 28), new("SIZE"),
                new("QTY M", ColumnKind.Number), new("DETAIL", Width: 70), new("DECISION", Width: 40) },
            Rows = flags.Select(f => new object?[]
            {
                f.Code, f.Tag, f.Claim.Subcontractor, f.Claim.InvoiceNo, f.Claim.Stage, f.Claim.FromRaw, f.Claim.ToRaw, f.Claim.SizeKey, f.Claim.Qty, f.Message,
                f.IsBypassed ? $"{f.Decision!.By} {f.Decision.At:dd-MMM-yy}: {f.Decision.Reason}" : "",
            }).ToList(),
        };
        var aliasCount = snap.Aliases.Where(a => a.Kind != "REJECTED").GroupBy(a => a.PanelKey).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(a => a.Alias)));
        var spellings = snap.Claims.SelectMany(c => new[] { (c.FromKey, c.FromRaw), (c.ToKey, c.ToRaw) }).Where(x => x.Item1.Length > 0)
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Select(x => PanelNames.Tidy(x.Item2)).Distinct().ToList());
        var panels = new ExportSheet
        {
            Name = "PANELS", Title = "PANELS", Subtitle = $"{snap.Panels.Count} panels / equipment",
            Columns = new() { new("NAME", Width: 30), new("KEY", Width: 30), new("TYPE"), new("BUILDING"), new("ZONE"), new("LEVEL"), new("FED FROM", Width: 30), new("STATUS"),
                new("SPELLINGS SEEN", Width: 50), new("CONFIRMED ALIASES", Width: 40), new("SOURCE", Width: 30) },
            Rows = snap.Panels.OrderBy(p => p.Building).ThenBy(p => p.Name).Select(p => new object?[]
            {
                p.Name, p.Key, p.IsEquipment ? (p.Type.Length > 0 ? p.Type + " (equipment)" : "EQUIPMENT") : p.Type, p.Building, p.Zone, p.Level, p.ParentKey, p.Status,
                spellings.TryGetValue(p.Key, out var sp) ? string.Join(" | ", sp) : "", aliasCount.GetValueOrDefault(p.Key, ""), $"{p.SourceKind} {p.SourceDoc}".Trim(),
            }).ToList(),
        };
        return new() { register, claims, flagSheet, panels };
    }
}
