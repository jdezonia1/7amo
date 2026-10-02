using Raffaello.Core.Domain;

namespace Raffaello.Core.Rules;

public sealed record InvoiceCopyFinding(Invoice Invoice, Invoice CopyOf, int MatchingLines, int TotalLines, double Similarity)
{
    public string Message => $"{Invoice.Subcontractor} {Invoice.InvoiceNo} is a line-for-line copy of {CopyOf.InvoiceNo} ({MatchingLines}/{TotalLines} lines identical)";
}

/// <summary>
/// Detects a statement that repeats a previous one line for line (the BANDER SEIF INV-03 = INV-02 case).
/// Statements are cumulative, so a genuine new statement normally moves at least some lines.
/// </summary>
public static class InvoiceCopyDetector
{
    public static IReadOnlyList<InvoiceCopyFinding> Detect(IEnumerable<Invoice> invoices, IEnumerable<InvoiceLine> lines, double threshold = 0.98)
    {
        var byInvoice = lines.GroupBy(l => l.InvoiceId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<InvoiceCopyFinding>();
        foreach (var sub in invoices.GroupBy(i => i.Subcontractor))
        {
            var ordered = sub.OrderBy(i => i.InvDate).ThenBy(i => i.InvoiceNo, StringComparer.OrdinalIgnoreCase).ToList();
            for (var k = 1; k < ordered.Count; k++)
            {
                var cur = ordered[k];
                if (!byInvoice.TryGetValue(cur.Id, out var curLines) || curLines.Count == 0) continue;
                for (var j = k - 1; j >= 0; j--)
                {
                    var prev = ordered[j];
                    if (!byInvoice.TryGetValue(prev.Id, out var prevLines) || prevLines.Count == 0) continue;
                    var (match, total) = Compare(curLines, prevLines);
                    var sim = total == 0 ? 0 : (double)match / total;
                    if (sim >= threshold) { result.Add(new InvoiceCopyFinding(cur, prev, match, total, sim)); break; }
                }
            }
        }
        return result;
    }

    private static (int match, int total) Compare(List<InvoiceLine> a, List<InvoiceLine> b)
    {
        var mapB = b.GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.CumQty));
        var mapA = a.GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.CumQty));
        var keys = mapA.Keys.Union(mapB.Keys).ToList();
        var match = keys.Count(k => mapA.TryGetValue(k, out var qa) && mapB.TryGetValue(k, out var qb) && Math.Abs(qa - qb) < 0.0001);
        return (match, keys.Count);
    }
}
