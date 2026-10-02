using System.Text.RegularExpressions;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Insights;

/// <summary>
/// When was a ledger claim made? The tracker ledger has no dates, so: (1) the invoice period entered on the insights pages,
/// (2) the subcontractor invoice revision (period to / submitted / created), (3) the entry date of a line typed in the app.
/// Lines imported from the tracker without (1) or (2) have no date.
/// </summary>
public sealed class ClaimDating
{
    private readonly Dictionary<(string, int), DateTime> _periods;
    private readonly Dictionary<(string, int), DateTime> _invoices;

    public ClaimDating(ProjectSnapshot s, IEnumerable<InsightInvoicePeriod> periods)
    {
        _periods = periods.GroupBy(p => (Norm(p.Subcontractor), p.InvoiceNo)).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Id).First().PeriodEnd.Date);
        _invoices = s.SubInvoices.Where(InvoiceKinds.IsSubcontractor).GroupBy(i => (Norm(i.Subcontractor), i.InvoiceNo))
            .Select(g => (g.Key, Date: g.Select(i => i.PeriodTo ?? i.SubmittedAt ?? (i.CreatedAt == default ? (DateTime?)null : i.CreatedAt)).Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty().Min()))
            .Where(x => x.Date != default).ToDictionary(x => x.Key, x => x.Date.Date);
    }

    public static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>Date of a subcontractor invoice and where it came from (PERIOD / INVOICE), or null.</summary>
    public (DateTime Date, string Source)? Invoice(string sub, int invoiceNo)
    {
        if (_periods.TryGetValue((Norm(sub), invoiceNo), out var p)) return (p, "PERIOD");
        if (_invoices.TryGetValue((Norm(sub), invoiceNo), out var i)) return (i, "INVOICE");
        return null;
    }

    public (DateTime Date, string Source)? Of(ClaimLine c)
    {
        if (Invoice(c.Subcontractor, c.InvoiceNo) is { } d) return d;
        if (c.Source is not ("TRACKER" or "SPLIT") && c.EnteredAt != default) return (c.EnteredAt.Date, "ENTERED");
        return null;
    }

    /// <summary>WIR numbers written on a ledger line ("WIR-12; WIR-13").</summary>
    public static IEnumerable<string> WirNumbers(string? wirNo) =>
        Regex.Split(wirNo ?? "", @"[;,/\s]+").Select(w => w.Trim()).Where(w => w.Length > 0);

    public static string WirKey(string s) => Regex.Replace((s ?? "").ToUpperInvariant(), "[^A-Z0-9]", "");
}
