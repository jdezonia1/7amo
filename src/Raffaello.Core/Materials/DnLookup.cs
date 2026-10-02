using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Materials;

/// <summary>One answer of the DN lookup: which PO, which invoice (no / rev / status / Aconex), which MIR, dates and match status.</summary>
public sealed record DnLookupRow(
    string Supplier, string DnNo, DateTime? DnDate, string PoNo, DateTime? PoDate, int Lines, double Qty, string Unit,
    string Invoice, string InvoiceStatus, string AconexNo, string MirNo, string MirStatus, DateTime? MirDate, string Match, string Batches, string MatchedOn, long DnId)
{
    public string Tag => Match.Contains(MatchStatus.OverPo) || Match.Contains(MatchStatus.NotOnPo) ? "OVER" : Match.Contains(MatchStatus.QtyMismatch) ? "CHECK"
        : Match.Contains(MatchStatus.DnWithoutMir) ? "DUE" : Match.Length == 0 ? "" : "OK";
}

/// <summary>DN lookup: filter by supplier + DN no / PO no / batch (suppliers have many POs, each with its own invoice series).</summary>
public static class DnLookup
{
    private static string Key(string? s) => new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static List<string> Suppliers(MaterialsSnapshot m) =>
        m.Dns.Select(d => d.Supplier).Concat(m.Pos.Select(p => p.Supplier)).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

    public static List<DnLookupRow> Search(ProjectSnapshot p, MaterialsSnapshot m, string? supplier, string? query)
    {
        var q = Key(query);
        var invoices = p.SubInvoices.ToDictionary(i => i.Id);
        var rows = new List<DnLookupRow>();
        foreach (var dn in m.Dns)
        {
            if (!string.IsNullOrWhiteSpace(supplier) && !dn.Supplier.Contains(supplier.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var lines = m.LinesOf(dn).ToList();
            string on = "";
            if (q.Length > 0)
            {
                if (Key(dn.DnNo).Contains(q)) on = "DN";
                else if (Key(dn.PoNo).Contains(q)) on = "PO";
                else if (lines.Any(l => Key(l.Batch).Contains(q))) on = "BATCH";
                else if (Key(dn.OrderNo).Contains(q)) on = "ORDER";
                else continue;
            }
            var po = m.FindPo(dn.PoNo);
            var locks = m.Locks.Where(k => lines.Any(l => l.Id == k.DnLineId)).ToList();
            var inv = locks.GroupBy(k => (k.PoNo, k.InvoiceNo)).Select(g =>
            {
                var latest = p.SubInvoices.Where(i => i.ContractNo == g.Key.PoNo && i.Subcontractor.Equals(g.First().Supplier, StringComparison.OrdinalIgnoreCase) && i.InvoiceNo == g.Key.InvoiceNo)
                    .OrderByDescending(i => i.Revision).FirstOrDefault() ?? invoices.GetValueOrDefault(g.First().SubInvoiceId);
                return (Text: $"INV-{g.Key.InvoiceNo:00}" + (latest is null ? "" : $" Rev {latest.Revision}") + (g.Count() < lines.Count ? $" ({g.Count()}/{lines.Count} lines)" : ""), Inv: latest);
            }).ToList();
            var mirLink = m.MirDns.Where(x => x.DnNo.Equals(dn.DnNo, StringComparison.OrdinalIgnoreCase)).Select(x => m.Mirs.FirstOrDefault(mm => mm.Id == x.MirId)).Where(x => x != null).Cast<MatMir>().ToList();
            var statuses = lines.Select(l => l.MatchStatus).Where(s => s.Length > 0).GroupBy(s => s).OrderByDescending(g => MatchStatus.Severity(g.Key)).Select(g => $"{g.Key} {g.Count()}");
            rows.Add(new DnLookupRow(
                dn.Supplier, dn.DnNo, dn.DnDate, po?.PoNo ?? dn.PoNo, po?.PoDate ?? dn.PoDate, lines.Count, lines.Sum(l => l.Qty), lines.Select(l => l.Unit).FirstOrDefault() ?? "",
                inv.Count == 0 ? "NOT INVOICED" : string.Join(" + ", inv.Select(i => i.Text)), string.Join(" / ", inv.Select(i => i.Inv?.Status).Where(s => s != null).Distinct()),
                string.Join(" / ", inv.Select(i => i.Inv?.AconexWorkflowNo).Where(s => !string.IsNullOrEmpty(s)).Distinct()),
                mirLink.Count == 0 ? "NO MIR" : string.Join(", ", mirLink.Select(x => x.MirNo)), string.Join(", ", mirLink.Select(x => x.Status).Distinct()),
                mirLink.Select(x => x.MirDate).FirstOrDefault(), string.Join(", ", statuses), string.Join(" ", lines.Select(l => l.Batch).Where(b => b.Length > 0)), on, dn.Id));
        }
        return rows.OrderByDescending(r => r.DnDate ?? DateTime.MinValue).ThenBy(r => r.DnNo).ToList();
    }
}
