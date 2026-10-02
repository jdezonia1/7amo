using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Analytics;

public sealed class InvoiceSummary
{
    public required Invoice Invoice { get; init; }
    public int Lines { get; init; }
    public double ClaimedValue { get; init; }
    public double PreviousClaimedValue { get; init; }
    public double ThisPeriod => ClaimedValue - PreviousClaimedValue;
    public int OverLines { get; init; }
    public int CheckLines { get; init; }
    public InvoiceCopyFinding? CopyOf { get; init; }
    public string Flag => CopyOf != null ? "COPY" : Invoice.Status == InvoiceStatus.Redo ? "REDO" : OverLines > 0 ? "OVER" : CheckLines > 0 ? "CHECK" : "OK";
}

public sealed class SubcontractorScore
{
    public string Name { get; init; } = "";
    public double QsValue { get; init; }
    public double ClaimedPct { get; init; }
    public double WirPct { get; init; }
    public double CertifiedValue { get; init; }
    public double ClaimedValue { get; init; }
    public int OverCount { get; init; }
    public int CheckCount { get; init; }
    public double AvgCertDays { get; init; }
    public int Invoices { get; init; }
    public int OpenWirs { get; init; }
}

public static class StatementAnalysis
{
    public static List<InvoiceSummary> Invoices(ProjectSnapshot s, IReadOnlyDictionary<long, ChainRow> chain, string subcontractor)
    {
        var copies = InvoiceCopyDetector.Detect(s.Invoices, s.InvoiceLines).ToDictionary(c => c.Invoice.Id);
        var list = new List<InvoiceSummary>();
        double prev = 0;
        foreach (var inv in s.Invoices.Where(i => i.Subcontractor == subcontractor).OrderBy(i => i.InvDate).ThenBy(i => i.InvoiceNo))
        {
            var lines = s.InvoiceLines.Where(l => l.InvoiceId == inv.Id).ToList();
            var value = lines.Sum(l => l.CumQty * l.Rate);
            int over = 0, check = 0;
            foreach (var l in lines)
            {
                if (!chain.TryGetValue(l.LineId, out var r)) continue;
                if (l.CumQty > r.Cap + 0.0001) over++;
                else if (l.CumQty > r.Done + 0.0001) check++;
            }
            list.Add(new InvoiceSummary
            {
                Invoice = inv, Lines = lines.Count, ClaimedValue = value, PreviousClaimedValue = prev,
                OverLines = over, CheckLines = check, CopyOf = copies.GetValueOrDefault(inv.Id),
            });
            if (inv.Status != InvoiceStatus.Rejected) prev = value;
        }
        return list;
    }

    public static List<SubcontractorScore> Scorecard(ProjectSnapshot s, IReadOnlyList<ChainRow> rows)
    {
        var names = s.Subcontractors.Select(x => x.Name).Union(s.Invoices.Select(i => i.Subcontractor)).Distinct().OrderBy(n => n).ToList();
        var result = new List<SubcontractorScore>();
        foreach (var n in names)
        {
            var mine = rows.Where(r => r.Subcontractors.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
            var qsV = mine.Sum(r => r.Qs * r.Rate);
            var invs = s.Invoices.Where(i => i.Subcontractor == n).ToList();
            result.Add(new SubcontractorScore
            {
                Name = n,
                QsValue = qsV,
                ClaimedValue = mine.Sum(r => r.ClaimedValue),
                CertifiedValue = mine.Sum(r => r.CertifiedValue),
                ClaimedPct = qsV <= 0 ? 0 : mine.Sum(r => r.ClaimedValue) / qsV,
                WirPct = qsV <= 0 ? 0 : mine.Sum(r => r.DoneValue) / qsV,
                OverCount = mine.Count(r => r.Verdict == Verdict.Over),
                CheckCount = mine.Count(r => r.Verdict == Verdict.Check),
                AvgCertDays = ProjectAnalytics.AvgCertificationDays(invs),
                Invoices = invs.Count,
                OpenWirs = s.Wirs.Count(w => w.Subcontractor == n && w.Status == WirStatus.Open),
            });
        }
        return result;
    }
}
