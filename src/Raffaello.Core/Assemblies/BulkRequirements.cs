using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Assemblies;

/// <summary>One BOQ / contract item in a bulk run.</summary>
public sealed class BulkRow
{
    public AssemblySource Source { get; init; } = new();
    public Breakdown Breakdown { get; init; } = new();
    public double Qty { get; init; }
    /// <summary>Quantity installed so far (from the last approved invoices), 0 when unknown.</summary>
    public double InstalledQty { get; init; }
    public string ItemType => Breakdown.Spec.ItemType;
    public double Rate => Breakdown.Rate;
    public double Amount => Math.Round(Qty * Rate, 2);
    public double ReferenceAmount => Math.Round(Qty * (Breakdown.ReferenceRate ?? 0), 2);
    public string Verdict => Breakdown.Verdict;
}

/// <summary>Total requirement of one material over the selected items, compared with the POs, deliveries and installed work.</summary>
public sealed class RequirementLine
{
    public string Key { get; init; } = "";
    public string Spec { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Kind { get; init; } = ComponentKinds.Material;
    public double RequiredQty { get; set; }
    /// <summary>Theoretical consumption of the work installed so far.</summary>
    public double InstalledQty { get; set; }
    public double Cost { get; set; }
    public double UnitPrice { get; set; }
    public string PriceSource { get; set; } = "";
    public int ItemCount { get; set; }
    public List<string> Items { get; } = new();
    public double OrderedQty { get; set; }
    public double DeliveredQty { get; set; }
    public List<string> PoRefs { get; } = new();
    public bool FreeIssue { get; set; }

    public double PoBalance => Math.Round(OrderedQty - RequiredQty, 2);
    public double ToDeliver => Math.Round(OrderedQty - DeliveredQty, 2);
    /// <summary>Delivered minus the theoretical consumption of the installed work (stock on site + wastage).</summary>
    public double SiteBalance => Math.Round(DeliveredQty - InstalledQty, 2);
    public string ItemsText => string.Join(", ", Items.Take(6)) + (Items.Count > 6 ? $" +{Items.Count - 6}" : "");
    public string PoRefsText => string.Join(", ", PoRefs.Take(4)) + (PoRefs.Count > 4 ? $" +{PoRefs.Count - 4}" : "");

    public string Status =>
        OrderedQty <= 0 ? "NOT ON ANY PO"
        : OrderedQty + 1e-6 < RequiredQty ? "SHORT ON PO"
        : OrderedQty > RequiredQty * 1.10 + 1e-6 ? "OVER-ORDERED"
        : DeliveredQty + 1e-6 < InstalledQty ? "INSTALLED > DELIVERED"
        : "COVERED";
}

public sealed class BulkResult
{
    public List<BulkRow> Rows { get; init; } = new();
    public List<RequirementLine> Materials { get; init; } = new();
    public int Unrecognised => Rows.Count(r => !r.Breakdown.Spec.Recognised);
    public double Amount => Math.Round(Rows.Sum(r => r.Amount), 2);
    public double ReferenceAmount => Math.Round(Rows.Sum(r => r.ReferenceAmount), 2);
    public string Summary =>
        $"{Rows.Count} items ({Unrecognised} not recognised), built-up value {Amount:N0}, reference value {ReferenceAmount:N0}; {Materials.Count} materials, " +
        $"{Materials.Count(m => m.Status == "NOT ON ANY PO")} not on any PO, {Materials.Count(m => m.Status == "SHORT ON PO")} short on PO";
}

/// <summary>
/// [assemblies] Bulk breakdown: every item x quantity -> material requirements (conduit m, couplings, boxes, wire m by size, glue litres ...),
/// compared with PO quantities and DN deliveries (procurement gaps) and with the theoretical consumption of the installed work
/// (reconciliation: delivered vs installed). <see cref="Reconcile"/> is the hook other modules (Insights) can call.
/// </summary>
public static class BulkRequirements
{
    public static BulkResult Run(IEnumerable<(AssemblySource Source, Breakdown Breakdown, double InstalledQty)> items, MaterialsSnapshot? materials, double poMatchScore = 0.75, double stickLen = 3)
    {
        var rows = items.Select(i => new BulkRow { Source = i.Source, Breakdown = i.Breakdown, Qty = i.Source.Qty, InstalledQty = i.InstalledQty }).ToList();
        var mats = Aggregate(rows);
        if (materials != null) CompareWithPo(mats, materials, poMatchScore, stickLen);
        return new BulkResult { Rows = rows, Materials = mats };
    }

    /// <summary>Material / equipment lines x item quantity, summed by specification and unit (labour is left out).</summary>
    public static List<RequirementLine> Aggregate(IEnumerable<BulkRow> rows)
    {
        var map = new Dictionary<string, RequirementLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            foreach (var l in r.Breakdown.Lines.Where(l => l.Kind != ComponentKinds.Labour && l.TotalQty > 0))
            {
                var key = PriceBook.KeyOf(l.Spec) + "|" + Units.Normalize(l.Unit);
                if (!map.TryGetValue(key, out var m))
                {
                    m = new RequirementLine { Key = key, Spec = l.Spec, Unit = Units.Normalize(l.Unit) is { Length: > 0 } u ? u : l.Unit, Kind = l.Kind, UnitPrice = l.UnitPrice, PriceSource = l.PriceSource, FreeIssue = !l.Included };
                    map[key] = m;
                }
                m.RequiredQty += l.TotalQty * r.Qty;
                m.InstalledQty += l.TotalQty * r.InstalledQty;
                m.Cost += l.TotalQty * r.Qty * l.UnitPrice;
                m.ItemCount++;
                var label = r.Source.Code.Length > 0 ? r.Source.Code : r.Source.Key;
                if (!m.Items.Contains(label)) m.Items.Add(label);
            }
        }
        foreach (var m in map.Values)
        {
            m.RequiredQty = Math.Round(m.RequiredQty, 2);
            m.InstalledQty = Math.Round(m.InstalledQty, 2);
            m.Cost = Math.Round(m.Cost, 2);
        }
        return map.Values.OrderBy(m => m.Kind).ThenBy(m => m.Spec, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Fills ordered (PO lines matched by fingerprint) and delivered (DN lines of those PO lines) quantities, in the requirement unit.</summary>
    public static void CompareWithPo(List<RequirementLine> lines, MaterialsSnapshot snap, double minScore = 0.75, double stickLen = 3)
    {
        var pos = snap.Pos.ToDictionary(p => p.Id);
        var dnByPoLine = snap.DnLines.Where(d => d.PoLineId is not null).GroupBy(d => d.PoLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(d => d.Qty));
        foreach (var m in lines)
        {
            m.OrderedQty = 0; m.DeliveredQty = 0; m.PoRefs.Clear();
            foreach (var pl in snap.PoLines)
            {
                if (Fingerprints.Compare(m.Spec, pl.Description) < minScore) continue;
                var factor = QtyFactor(pl.Unit, m.Unit, pl.Description, pl.LengthPerPcs, stickLen);
                if (factor is null) continue;
                m.OrderedQty += pl.Qty * factor.Value;
                if (dnByPoLine.TryGetValue(pl.Id, out var dq)) m.DeliveredQty += dq * factor.Value;
                var po = pos.TryGetValue(pl.PoId, out var p) ? p.PoNo : $"PO#{pl.PoId}";
                var refText = $"{po} L{pl.LineNo}";
                if (!m.PoRefs.Contains(refText)) m.PoRefs.Add(refText);
            }
            m.OrderedQty = Math.Round(m.OrderedQty, 2);
            m.DeliveredQty = Math.Round(m.DeliveredQty, 2);
        }
    }

    /// <summary>Multiplier from a PO quantity unit to the requirement unit (null = not comparable).</summary>
    public static double? QtyFactor(string? poUnit, string? reqUnit, string description, double lengthPerPcs, double stickLen)
    {
        var f = Units.Normalize(poUnit); var t = Units.Normalize(reqUnit);
        if (f.Length == 0 || t.Length == 0 || f == t) return 1;
        if (f == Units.Km && t == Units.M) return 1000;
        if (f == Units.Roll && t == Units.M) return PriceBook.RollLength(description);
        if (f == Units.Pcs && t == Units.M) return lengthPerPcs > 0 ? lengthPerPcs : stickLen;
        if (Units.Family(f) == "COUNT" && Units.Family(t) == "COUNT") return 1;
        return null;
    }

    /// <summary>
    /// Reconciliation hook: theoretical consumption of installed quantities per material vs what was delivered.
    /// <paramref name="installed"/> = (breakdown, installed qty of the item). Returns spec -> (theoretical, unit).
    /// </summary>
    public static List<RequirementLine> Reconcile(IEnumerable<(Breakdown Breakdown, double InstalledQty, string Label)> installed, MaterialsSnapshot? materials, double minScore = 0.75)
    {
        var rows = installed.Select(i => new BulkRow { Source = new AssemblySource { Code = i.Label }, Breakdown = i.Breakdown, Qty = 0, InstalledQty = i.InstalledQty });
        var lines = Aggregate(rows);
        if (materials != null) CompareWithPo(lines, materials, minScore);
        return lines;
    }

    /// <summary>Executed (cumulative) quantity per contract item from the last approved invoice of each contract.</summary>
    public static Dictionary<string, double> InstalledFromInvoices(ProjectSnapshot snap)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var approved = snap.SubInvoices.Where(i => i.Status == SubInvoiceStatus.Approved)
            .GroupBy(i => i.ContractNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision).First()).ToList();
        foreach (var inv in approved)
            foreach (var g in snap.SubInvoiceLines.Where(l => l.SubInvoiceId == inv.Id && l.Kind == "ITEM").GroupBy(l => l.ItemNo))
                res[$"{inv.ContractNo}|{g.Key}"] = g.Sum(l => l.CumQty);
        return res;
    }
}
