using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Chain;

/// <summary>Builds the chain row for every line from allocations, WIRs, statements and delivery notes.</summary>
public static class ChainBuilder
{
    public static List<ChainRow> Build(ProjectSnapshot s, RulesEngine engine)
    {
        var today = engine.Options.Today;
        var given = s.Allocations.GroupBy(a => a.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var wirs = s.Wirs.ToDictionary(w => w.Id);
        var wirLines = s.WirLines.GroupBy(w => w.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var invoices = s.Invoices.ToDictionary(i => i.Id);
        var invLines = s.InvoiceLines.GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.ToList());
        var dnLines = s.DnLines.Where(d => d.LineId.HasValue).GroupBy(d => d.LineId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));

        var rows = new List<ChainRow>(s.Lines.Count);
        foreach (var line in s.Lines)
        {
            var r = new ChainRow { Line = line };
            if (given.TryGetValue(line.Id, out var al))
            {
                r.Given = al.Sum(a => a.Qty);
                r.Subcontractors = string.Join(", ", al.Select(a => a.Subcontractor).Distinct());
            }
            if (wirLines.TryGetValue(line.Id, out var wl))
            {
                foreach (var x in wl)
                {
                    if (!wirs.TryGetValue(x.WirId, out var w)) continue;
                    if (ProgressRules.CountsAsProgress(w, x)) r.Done += x.Qty;
                    else if (x.IsRework || w.System == Systems.Emt) r.Rework += x.Qty;
                    if (w.Status == WirStatus.Open)
                    {
                        r.OpenWirs++;
                        var days = (int)(today - w.SubmittedAt.Date).TotalDays;
                        if (days >= r.OldestOpenWirDays) { r.OldestOpenWirDays = days; r.LastWirNo = w.WirNo; }
                    }
                    else if (string.IsNullOrEmpty(r.LastWirNo)) r.LastWirNo = w.WirNo;
                }
            }
            if (invLines.TryGetValue(line.Id, out var il))
            {
                // statements are cumulative: per subcontractor take the latest live statement, never add statements up
                var live = il.Where(x => invoices.TryGetValue(x.InvoiceId, out var inv) && inv.Status != InvoiceStatus.Rejected)
                             .Select(x => (x, inv: invoices[x.InvoiceId])).ToList();
                foreach (var sub in live.GroupBy(t => t.inv.Subcontractor))
                {
                    var latest = sub.OrderBy(t => t.inv.InvDate).ThenBy(t => t.inv.Id).Last();
                    r.Claimed += latest.x.CumQty;
                    r.LastInvoiceNo = latest.inv.InvoiceNo;
                    var cert = sub.Where(t => t.inv.Status == InvoiceStatus.Certified).OrderBy(t => t.inv.InvDate).ThenBy(t => t.inv.Id).LastOrDefault();
                    if (cert.inv != null) r.Certified += cert.x.CertifiedQty;
                }
            }
            if (dnLines.TryGetValue(line.Id, out var del)) r.Delivered = del;
            engine.Apply(r);
            rows.Add(r);
        }
        return rows;
    }
}
