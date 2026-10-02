using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Queue;

/// <summary>Where a queue item navigates to.</summary>
public sealed record NavTarget(string Module, long? LineId = null, string? Key = null);

public sealed record QueueItem(Verdict Severity, string Category, string Title, string Detail, NavTarget Target, double Score)
{
    public string Tag => VerdictText.Of(Severity);
}

/// <summary>
/// Builds the "Needs you today" list from the rules engine output plus statement, WIR and material checks.
/// Items are ranked by severity then by money/qty at stake.
/// </summary>
public static class NeedsTodayQueue
{
    public static List<QueueItem> Build(ProjectSnapshot s, IReadOnlyList<ChainRow> rows, RuleOptions o, int maxLineItems = 12)
    {
        var q = new List<QueueItem>();

        // 1. copy statements and invoices to redo
        foreach (var c in InvoiceCopyDetector.Detect(s.Invoices, s.InvoiceLines))
            q.Add(new(Verdict.Over, "STATEMENT", $"{c.Invoice.Subcontractor} {c.Invoice.InvoiceNo} = copy of {c.CopyOf.InvoiceNo}",
                $"{c.MatchingLines}/{c.TotalLines} lines identical. Reject or ask for a corrected statement.", new("Statements", Key: c.Invoice.Subcontractor + "|" + c.Invoice.InvoiceNo), 1e9));
        foreach (var inv in s.Invoices.Where(i => i.Status == InvoiceStatus.Redo))
            q.Add(new(Verdict.Check, "STATEMENT", $"{inv.Subcontractor} {inv.InvoiceNo} must be redone",
                string.IsNullOrWhiteSpace(inv.Notes) ? "Marked REDO." : inv.Notes, new("Statements", Key: inv.Subcontractor + "|" + inv.InvoiceNo), 9e8));

        // 2. statements waiting for certification with lines claimed above WIR
        var pending = s.Invoices.Where(i => i.Status == InvoiceStatus.Received).ToList();
        var byLine = rows.ToDictionary(r => r.Id);
        foreach (var inv in pending)
        {
            var lines = s.InvoiceLines.Where(l => l.InvoiceId == inv.Id).ToList();
            var hold = lines.Where(l => byLine.TryGetValue(l.LineId, out var r) && l.CumQty > r.Done + o.Epsilon).ToList();
            if (hold.Count > 0)
            {
                var val = hold.Sum(l => (l.CumQty - byLine[l.LineId].Done) * l.Rate);
                q.Add(new(Verdict.Check, "CERTIFY", $"Hold {inv.Subcontractor} {inv.InvoiceNo}: {hold.Count} lines claimed > WIR",
                    $"SAR {val:N0} claimed without approved WIR. Certify only the WIR quantity.", new("Invoices", Key: inv.Subcontractor + "|" + inv.InvoiceNo), 5e8 + val));
            }
        }

        // 3. worst OVER lines individually, the rest as a group
        var over = rows.Where(r => r.Verdict == Verdict.Over).OrderByDescending(r => Math.Max(r.Claimed - r.Cap, r.Given - r.Qs) * Math.Max(1, r.Rate)).ToList();
        foreach (var r in over.Take(maxLineItems))
            q.Add(new(Verdict.Over, "OVER", $"{r.Building} {r.Level} {r.Room} {r.System} {r.Stage}", r.Reason, new("Quantities", r.Id), 4e8 + Math.Max(r.Claimed - r.Cap, r.Given - r.Qs) * Math.Max(1, r.Rate)));
        if (over.Count > maxLineItems)
            q.Add(new(Verdict.Over, "OVER", $"{over.Count - maxLineItems} more OVER lines", "Open Quantities filtered to OVER.", new("Quantities", Key: "OVER"), 3.9e8));

        // 4. CHECK lines (claimed > done) grouped by subcontractor
        foreach (var g in rows.Where(r => r.Verdict == Verdict.Check).GroupBy(r => string.IsNullOrEmpty(r.Subcontractors) ? "UNASSIGNED" : r.Subcontractors))
        {
            var first = g.OrderByDescending(r => (r.Claimed - r.Done) * r.Rate).First();
            q.Add(new(Verdict.Check, "CHECK", $"{g.Count()} CHECK lines - {g.Key}", first.Reason, new("Quantities", first.Id, "CHECK"), 2e8 + g.Count()));
        }

        // 5. WIR ageing
        var late = s.Wirs.Where(w => w.Status == WirStatus.Open && (o.Today - w.SubmittedAt.Date).TotalDays > o.WirDueDays).OrderBy(w => w.SubmittedAt).ToList();
        if (late.Count > 0)
            q.Add(new(Verdict.Due, "WIR", $"{late.Count} WIRs open > {o.WirDueDays} days",
                $"Oldest {late[0].WirNo} ({late[0].Subcontractor}) waiting {(o.Today - late[0].SubmittedAt.Date).TotalDays:N0} days.", new("Wir", Key: late[0].WirNo), 1.5e8 + late.Count));

        // 6. site ahead of WIR -> raise WIRs
        foreach (var g in rows.Where(r => r.Findings.Any(f => f.RuleCode == "SITE%" && f.Severity == Verdict.Due)).GroupBy(r => r.Subcontractors))
            q.Add(new(Verdict.Due, "SITE %", $"Raise WIRs - {(string.IsNullOrEmpty(g.Key) ? "UNASSIGNED" : g.Key)}",
                $"{g.Count()} lines where SITE % is ahead of approved WIR by more than {o.SiteTolerance:P0}.", new("Quantities", g.First().Id, "DUE"), 1e8 + g.Count()));

        // 7. materials
        foreach (var p in MaterialAnalysis.All(s))
        {
            foreach (var l in p.Lines.Where(l => l.IsOver))
                q.Add(new(Verdict.Over, "MATERIAL", $"{p.Po.PoNo} line {l.Line.LineNo}: delivered > PO",
                    $"{l.Line.Description}: {l.Delivered:N0} {l.Line.Unit} delivered vs {l.Line.Qty:N0} ordered.", new("Materials", Key: p.Po.PoNo), 3e8 + (l.Delivered - l.Line.Qty) * l.Line.Rate));
            if (!p.TotalCheck.Matches)
                q.Add(new(Verdict.Check, "MATERIAL", $"{p.Po.PoNo}: PO total does not add up",
                    $"Lines SAR {p.TotalCheck.LinesTotal:N2} vs stated SAR {p.TotalCheck.StatedTotal:N2} (diff {p.TotalCheck.Difference:N2}).", new("Materials", Key: p.Po.PoNo), 2.5e8));
        }

        // 8. real workflow: checks, room caps, invoices
        var heightPending = s.Claims.Where(Ledger.HeightCheck.IsPending).ToList();
        if (heightPending.Count > 0)
            q.Add(new(Verdict.Due, "HEIGHT", $"{heightPending.Count} claims wait for the >4.5 m check",
                $"{heightPending.Sum(c => c.QtyAbove45):N0} points claimed above 4.5 m are held out of the invoices until checked.", new("Checks", Key: "HEIGHT"), 1.6e8 + heightPending.Count));
        var lengthPending = s.Claims.Where(Ledger.LengthCheck.IsPending).ToList();
        if (lengthPending.Count > 0)
            q.Add(new(Verdict.Due, "LENGTH", $"{lengthPending.Count} claims wait for the 15 m length check",
                $"Claimed {lengthPending.Sum(c => c.LengthClaimedQty):N0} vs plan {lengthPending.Sum(c => c.Qty):N0} points - held out of the invoices.", new("Checks", Key: "LENGTH"), 1.6e8 + lengthPending.Count));
        if (s.RoomQtys.Count > 0)
        {
            var overCap = Ledger.LedgerRules.Balances(s.RoomQtys, s.Claims).Values.Where(b => b.HasCap && b.IsOver).OrderByDescending(b => b.Claimed - b.ProjectQty).ToList();
            if (overCap.Count > 0)
                q.Add(new(Verdict.Over, "LEDGER", $"{overCap.Count} room / stage / item keys claimed above PROJECT QTY",
                    $"Worst: {overCap[0].Room} {overCap[0].Stage} {overCap[0].Item} claimed {overCap[0].Claimed:N1} of {overCap[0].ProjectQty:N1}.", new("Ledger", Key: overCap[0].Room), 3.5e8 + overCap.Count));
        }
        foreach (var inv in s.SubInvoices.Where(i => i.Status is SubInvoiceStatus.Submitted or SubInvoiceStatus.Approved && !i.Notes.StartsWith("Imported from")))
        {
            var key = Attachment.InvoiceKey(inv.ContractNo, inv.Subcontractor, inv.InvoiceNo, inv.Revision);
            if (!s.Attachments.Any(a => a.OwnerKind == AttachmentKinds.Invoice && a.OwnerKey == key && a.Kind == AttachmentKinds.Signed))
                q.Add(new(Verdict.Due, "PACKAGE", $"{inv.Title}: package missing the signed invoice", "Attach the signed scan on the Invoices page, then build the FINAL package.",
                    new("Invoices", Key: $"{inv.ContractNo}|{inv.Subcontractor}|{inv.InvoiceNo}"), 1.5e8));
            if (inv.PackageMissingWirs > 0)
                q.Add(new(Verdict.Check, "PACKAGE", $"{inv.PackageMissingWirs} WIRs missing for {inv.Subcontractor} INV-{inv.InvoiceNo:00}", "Put the WIR PDFs in the WIR folder (Settings) and rebuild the package.",
                    new("Invoices", Key: $"{inv.ContractNo}|{inv.Subcontractor}|{inv.InvoiceNo}"), 1.4e8));
        }
        foreach (var blk in Ledger.CumulativeSplit.Pending(s.Claims))
            q.Add(new(Verdict.Due, "CUMULATIVE", $"{blk.Sub} INV 1-{blk.InvoiceNo} cumulative - awaiting invoice files to split",
                $"{blk.Lines:N0} ledger lines count once as INV {blk.InvoiceNo}. Import his INV 1..{blk.InvoiceNo} files on the Invoices page to split them.", new("Invoices", Key: $"{blk.Sub}|{blk.InvoiceNo}"), 3e8));
        foreach (var inv in s.SubInvoices.Where(i => i.Status == SubInvoiceStatus.Rejected
                     && !s.SubInvoices.Any(n => n.ContractNo == i.ContractNo && n.Subcontractor == i.Subcontractor && n.InvoiceNo == i.InvoiceNo && n.Revision > i.Revision)))
            q.Add(new(Verdict.Check, "INVOICE", $"{inv.Title} rejected - prepare Rev {inv.Revision + 1}", inv.RejectionReason, new("Invoices", Key: $"{inv.ContractNo}|{inv.Subcontractor}|{inv.InvoiceNo}"), 4.5e8));
        foreach (var inv in s.SubInvoices.Where(i => i.Status == SubInvoiceStatus.Submitted))
            q.Add(new(Verdict.Due, "INVOICE", $"{inv.Title} in Aconex {inv.AconexWorkflowNo}", $"Submitted {inv.SubmittedAt:dd MMM} - waiting for head office.", new("Invoices", Key: $"{inv.ContractNo}|{inv.Subcontractor}|{inv.InvoiceNo}"), 1.2e8));

        // 9. PROJECT QTY coverage
        var withCap = rows.Count(r => r.ProjectQty.HasValue);
        if (rows.Count > 0 && withCap < rows.Count * 0.5)
            q.Add(new(Verdict.Open, "BOQ", $"PROJECT QTY only {(double)withCap / rows.Count:P0} filled",
                "Claims fall back to QS as the cap until PROJECT QTY is entered.", new("Contracts"), 1e7));

        return q.OrderByDescending(i => i.Severity).ThenByDescending(i => i.Score).ToList();
    }
}
