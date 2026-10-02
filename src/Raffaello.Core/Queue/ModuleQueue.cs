using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Rules;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Queue;

/// <summary>[phase6] "Needs you today" items from the modules that keep their own tables: materials, Aconex, variations.</summary>
public static class ModuleQueue
{
    public static IEnumerable<QueueItem> Materials(MaterialsSnapshot m, MaterialsSettings settings, DateTime today, int dnWithoutMirDays = 7)
    {
        if (m.Dns.Count == 0 && m.Pos.Count == 0) yield break;
        var match = ThreeWayMatcher.Match(m, settings);
        var noMir = match.Rows.Where(r => r.Status == MatchStatus.DnWithoutMir).Select(r => r.Dn).Distinct().ToList();
        var old = noMir.Where(d => d.DnDate is { } dt && (today - dt.Date).TotalDays > dnWithoutMirDays).ToList();
        if (noMir.Count > 0)
            yield return new QueueItem(old.Count > 0 ? Verdict.Due : Verdict.Open, "MATERIAL", $"{noMir.Count} DNs without MIR",
                $"Raise MIRs: {string.Join(", ", noMir.Take(6).Select(d => d.DnNo))}{(noMir.Count > 6 ? " ..." : "")}" + (old.Count > 0 ? $" ({old.Count} older than {dnWithoutMirDays} days)" : ""),
                new NavTarget("Materials", Key: "MATCH"), 1.3e8 + noMir.Count);
        var over = match.Rows.Where(r => r.Status == MatchStatus.OverPo).ToList();
        if (over.Count > 0)
            yield return new QueueItem(Verdict.Over, "MATERIAL", $"{over.Select(r => r.PoLine?.Id).Distinct().Count()} PO lines delivered above PO + tolerance",
                string.Join("; ", over.Take(4).Select(r => $"{r.Dn.PoNo} line {r.PoLine?.LineNo:00}: {r.CumQty:N0} of {r.PoQty:N0} {r.Unit}")), new NavTarget("Materials", Key: "MATCH"), 3.6e8 + over.Count);
        var mismatch = match.Rows.Count(r => r.Status is MatchStatus.QtyMismatch or MatchStatus.NotOnPo);
        if (mismatch > 0)
            yield return new QueueItem(Verdict.Check, "MATERIAL", $"{mismatch} DN lines need a look (qty mismatch / not on PO)", match.Summary, new NavTarget("Materials", Key: "MATCH"), 1.8e8 + mismatch);
        var conflicts = m.Pos.Where(p => p.ToleranceHeaderPct is { } h && p.ToleranceClausePct is { } c && Math.Abs(h - c) > 1e-9).ToList();
        if (conflicts.Count > 0)
            yield return new QueueItem(Verdict.Check, "MATERIAL", $"{conflicts.Count} POs with a tolerance conflict (header vs clause)",
                string.Join(", ", conflicts.Take(5).Select(p => $"{p.PoNo}: {p.ToleranceHeaderPct:P0} vs {p.ToleranceClausePct:P0}")), new NavTarget("Materials", Key: "PO"), 1.5e8);
    }

    public static IEnumerable<QueueItem> Aconex(IReadOnlyList<StatusBoardRow> board)
    {
        var overdue = board.Where(r => r.IsOverdue).OrderByDescending(r => r.DaysOverdue).ToList();
        foreach (var r in overdue.Take(5))
            yield return new QueueItem(Verdict.Due, "ACONEX", $"{r.Invoice} overdue {r.DaysOverdue} days at '{r.CurrentStep}'", $"Workflow {r.WorkflowNo} with {r.WithWhom}, due {r.DateDue:dd MMM}.",
                new NavTarget("Aconex", Key: r.WorkflowNo), 2.6e8 + r.DaysOverdue);
        foreach (var r in board.Where(r => r.State == WorkflowStates.Rejected && r.InvoiceStatus != SubInvoiceStatus.Rejected))
            yield return new QueueItem(Verdict.Check, "ACONEX", $"{r.Invoice} rejected in Aconex", $"Workflow {r.WorkflowNo}: {r.Outcome}. Record it and prepare the next revision.", new NavTarget("Aconex", Key: r.WorkflowNo), 4.2e8);
        foreach (var r in board.Where(r => r.State == WorkflowStates.Approved && r.InvoiceStatus != SubInvoiceStatus.Approved))
            yield return new QueueItem(Verdict.Due, "ACONEX", $"{r.Invoice} approved in Aconex", $"Workflow {r.WorkflowNo}: mark the revision APPROVED (it becomes the previous for the next invoice).", new NavTarget("Aconex", Key: r.WorkflowNo), 2.5e8);
        var notChecked = board.Count(r => r.State == WorkflowStates.NotChecked && r.WorkflowNo.Length > 0);
        if (notChecked > 0)
            yield return new QueueItem(Verdict.Open, "ACONEX", $"{notChecked} invoice workflows never checked", "Run REFRESH ALL on the Aconex status board.", new NavTarget("Aconex"), 0.9e8);
    }

    public static IEnumerable<QueueItem> Variations(IEnumerable<Variation> vos, DateTime today, int ageingDays = 30)
    {
        var aged = vos.Where(v => v.SubmittedAt is { } s && !VariationStatus.IsClosed(v.Status) && (today - s.Date).TotalDays > ageingDays).OrderBy(v => v.SubmittedAt).ToList();
        if (aged.Count > 0)
            yield return new QueueItem(Verdict.Due, "VO", $"{aged.Count} variations waiting more than {ageingDays} days",
                string.Join(", ", aged.Take(6).Select(v => $"{v.Number} ({(today - v.SubmittedAt!.Value.Date).TotalDays:0} d)")), new NavTarget("Variations", Key: aged[0].Number), 1.1e8 + aged.Count);
        var drafts = vos.Count(v => v.Status == VariationStatus.Draft && (today - v.Date.Date).TotalDays > 14);
        if (drafts > 0)
            yield return new QueueItem(Verdict.Open, "VO", $"{drafts} draft variations older than 2 weeks", "Submit or withdraw them.", new NavTarget("Variations"), 0.8e8);
    }
}
