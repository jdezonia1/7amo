using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Chain;

public sealed class CertLine
{
    public required ChainRow Row { get; init; }
    public double Claimed { get; init; }
    public double Done { get; init; }
    public double Cap { get; init; }
    public double Certifiable { get; init; }
    public double PreviousCertified { get; init; }
    public double ThisPeriod => Certifiable - PreviousCertified;
    public double Rate => Row.Rate;
    public double ValueToDate => Certifiable * Rate;
    public double ValueThisPeriod => ThisPeriod * Rate;
    public double HeldQty => Math.Max(0, Claimed - Certifiable);
    public string Status => Claimed > Cap + 0.0001 ? "OVER" : Claimed > Done + 0.0001 ? "CHECK" : "OK";
}

public sealed class ClaimDraft
{
    public string Subcontractor { get; init; } = "";
    public Invoice? Statement { get; init; }
    public Contract? Contract { get; init; }
    public List<CertLine> Lines { get; init; } = new();
    public double GrossToDate => Lines.Sum(l => l.ValueToDate);
    public double PreviousGross => Lines.Sum(l => l.PreviousCertified * l.Rate);
    public double ThisPeriodGross => GrossToDate - PreviousGross;
    public double RetentionPct => Contract?.RetentionPct ?? 0.10;
    public double Retention => ThisPeriodGross * RetentionPct;
    public double AdvanceRecovery => ThisPeriodGross * (Contract?.AdvancePct ?? 0);
    public double NetPayable => ThisPeriodGross - Retention - AdvanceRecovery;
    public double ClaimedValue => Lines.Sum(l => l.Claimed * l.Rate);
    public double HeldValue => Lines.Sum(l => l.HeldQty * l.Rate);
}

/// <summary>
/// Builds a certificate from certified chain values: per line the certifiable quantity is
/// min(claimed, DONE by WIR, PROJECT QTY cap). Claims above WIR are held; OVER lines are capped.
/// </summary>
public static class ClaimBuilder
{
    public static ClaimDraft Build(ProjectSnapshot s, IReadOnlyDictionary<long, ChainRow> chain, string subcontractor, Invoice? statement = null)
    {
        statement ??= s.Invoices.Where(i => i.Subcontractor == subcontractor && i.Status != InvoiceStatus.Rejected).OrderBy(i => i.InvDate).LastOrDefault();
        var prevCert = s.Invoices.Where(i => i.Subcontractor == subcontractor && i.Status == InvoiceStatus.Certified && (statement == null || i.Id != statement.Id))
                         .OrderBy(i => i.InvDate).LastOrDefault();
        var prevLines = prevCert is null ? new Dictionary<long, double>() :
            s.InvoiceLines.Where(l => l.InvoiceId == prevCert.Id).GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.CertifiedQty));
        var lines = new List<CertLine>();
        if (statement != null)
            foreach (var il in s.InvoiceLines.Where(l => l.InvoiceId == statement.Id))
            {
                if (!chain.TryGetValue(il.LineId, out var row)) continue;
                lines.Add(new CertLine
                {
                    Row = row, Claimed = il.CumQty, Done = row.Done, Cap = row.Cap,
                    Certifiable = ClaimRules.CertifiableQty(il.CumQty, row.Done, row.Cap),
                    PreviousCertified = prevLines.GetValueOrDefault(il.LineId),
                });
            }
        return new ClaimDraft
        {
            Subcontractor = subcontractor, Statement = statement,
            Contract = s.Contracts.FirstOrDefault(c => c.Subcontractor == subcontractor),
            Lines = lines.OrderBy(l => l.Row.Building).ThenBy(l => l.Row.Level).ThenBy(l => l.Row.Room).ThenBy(l => l.Row.System).ToList(),
        };
    }
}
