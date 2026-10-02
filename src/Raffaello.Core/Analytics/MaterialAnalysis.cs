using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Analytics;

public sealed class PoLineProgress
{
    public required PoLine Line { get; init; }
    public Dictionary<long, double> ByDn { get; } = new();
    public double Delivered => ByDn.Values.Sum();
    public double Remaining => Line.Qty - Delivered;
    public double DeliveredPct => Line.Qty <= 0 ? 0 : Delivered / Line.Qty;
    public double PoValue => Line.Qty * Line.Rate;
    public double DeliveredValue => Delivered * Line.Rate;
    public bool IsOver => MaterialRules.IsOverPo(Delivered, Line.Qty);
    public string Status => IsOver ? "OVER" : DeliveredPct >= 0.999 ? "OK" : Delivered > 0 ? "OPEN" : "DUE";
}

public sealed class PoProgress
{
    public required PurchaseOrder Po { get; init; }
    public List<DeliveryNote> Dns { get; init; } = new();
    public List<PoLineProgress> Lines { get; init; } = new();
    public PoTotalCheck TotalCheck { get; init; } = new(0, 0, 0, true);
    public double PoValue => Lines.Sum(l => l.PoValue);
    public double DeliveredValue => MaterialRules.DeliveredValue(Lines.Select(l => (l.Delivered, l.Line.Rate)));
    public int OverCount => Lines.Count(l => l.IsOver);
    public double DeliveredPct => PoValue <= 0 ? 0 : DeliveredValue / PoValue;
    /// <summary>SUMPRODUCT of each DN column with the PO rates (value delivered by that DN).</summary>
    public double DnValue(long dnId) => Lines.Sum(l => l.ByDn.GetValueOrDefault(dnId) * l.Line.Rate);
}

public static class MaterialAnalysis
{
    public static PoProgress ForPo(ProjectSnapshot s, PurchaseOrder po)
    {
        var lines = s.PoLines.Where(l => l.PoId == po.Id).OrderBy(l => l.LineNo).ToList();
        var dns = s.DeliveryNotes.Where(d => d.PoId == po.Id).OrderBy(d => d.DnDate).ThenBy(d => d.DnNo).ToList();
        var dnIds = dns.Select(d => d.Id).ToHashSet();
        var lineIds = lines.Select(l => l.Id).ToHashSet();
        var progress = lines.Select(l => new PoLineProgress { Line = l }).ToDictionary(p => p.Line.Id);
        foreach (var d in s.DnLines.Where(d => dnIds.Contains(d.DnId) && lineIds.Contains(d.PoLineId)))
        {
            var p = progress[d.PoLineId];
            p.ByDn[d.DnId] = p.ByDn.GetValueOrDefault(d.DnId) + d.Qty;
        }
        return new PoProgress
        {
            Po = po,
            Dns = dns,
            Lines = progress.Values.OrderBy(p => p.Line.LineNo).ToList(),
            TotalCheck = MaterialRules.CheckPoTotal(lines.Select(l => (l.Qty, l.Rate)), po.StatedTotal),
        };
    }

    public static List<PoProgress> All(ProjectSnapshot s) => s.PurchaseOrders.OrderBy(p => p.PoNo).Select(p => ForPo(s, p)).ToList();

    /// <summary>Cumulative delivered value over time for one PO.</summary>
    public static List<(DateTime Date, double Cumulative)> CumulativeDeliveries(PoProgress p)
    {
        double cum = 0;
        return p.Dns.Select(d => { cum += p.DnValue(d.Id); return (d.DnDate, cum); }).ToList();
    }
}
