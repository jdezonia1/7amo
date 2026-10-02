using System.IO.Compression;
using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;

namespace Raffaello.Core.Materials;

/// <summary>A supplier invoice built from DN lines, in the subcontractor-invoice template layout.</summary>
public sealed class SupplierInvoiceBuild
{
    public required InvoiceBuild Build { get; init; }
    public required MatPo Po { get; init; }
    /// <summary>DN lines invoiced in this invoice number (current period).</summary>
    public List<MatDnLine> CurrentLines { get; init; } = new();
    public List<string> Warnings => Build.Warnings;
}

/// <summary>
/// Supplier (material) invoices. Each PO has its own invoice series (INV-01, INV-02 ... with revisions). The invoice is built from
/// matched DN lines: CUM = DN qty locked to invoices up to this number, PREV = DN qty of earlier invoice numbers, CURR = this one.
/// A DN line can be invoiced once (hard lock in the store). The workbook / PDF / revision / approval flow is the subcontractor one
/// (<see cref="InvoiceWorkflow"/>, <see cref="InvoiceExcelExporter"/>, <see cref="InvoicePdfExporter"/>), with the PO number as
/// contract number and the supplier as vendor. PO lines split over several owner BOQ codes (scope-of-work sheet) get one row per code.
/// </summary>
public static class SupplierInvoices
{
    public const string Marker = "SUPPLIER INVOICE";

    /// <summary>DN lines of a PO that can still be invoiced (matched to a PO line and not locked by another invoice number).</summary>
    public static List<MatDnLine> Invoiceable(MaterialsSnapshot m, MatPo po, int invoiceNo)
    {
        var poLineIds = m.LinesOf(po).Select(l => l.Id).ToHashSet();
        var locks = m.Locks.ToDictionary(k => k.DnLineId);
        return m.DnLines.Where(d => d.PoLineId is long id && poLineIds.Contains(id)
                                    && (!locks.TryGetValue(d.Id, out var k) || (k.InvoiceNo == invoiceNo && MaterialsSnapshot.PoKey(k.PoNo) == MaterialsSnapshot.PoKey(po.PoNo))))
            .ToList();
    }

    public static int NextInvoiceNo(MaterialsSnapshot m, MatPo po) =>
        m.Locks.Where(k => MaterialsSnapshot.PoKey(k.PoNo) == MaterialsSnapshot.PoKey(po.PoNo) && string.Equals(k.Supplier, po.Supplier, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.InvoiceNo + 1).DefaultIfEmpty(1).Max();

    public static SupplierInvoiceBuild Build(ProjectSnapshot p, MaterialsSnapshot m, MaterialsSettings settings, MatPo po, int invoiceNo, IReadOnlyCollection<long> currentDnLineIds, int revision = 0)
    {
        var poKey = MaterialsSnapshot.PoKey(po.PoNo);
        var locks = m.Locks.Where(k => MaterialsSnapshot.PoKey(k.PoNo) == poKey).ToList();
        var dnById = m.DnLines.ToDictionary(d => d.Id);
        var dnHeader = m.Dns.ToDictionary(d => d.Id);
        var current = currentDnLineIds.Where(dnById.ContainsKey).Select(i => dnById[i]).ToList();
        var prevIds = locks.Where(k => k.InvoiceNo < invoiceNo).Select(k => k.DnLineId).ToHashSet();
        var prev = prevIds.Where(dnById.ContainsKey).Select(i => dnById[i]).ToList();
        var header = new SubInvoice
        {
            Subcontractor = po.Supplier, ContractNo = po.PoNo, InvoiceNo = invoiceNo, Revision = revision, Status = SubInvoiceStatus.Draft, CreatedAt = DateTime.Now,
            RetentionPct = po.RetentionPct, AdvancePct = po.AdvancePct, Notes = $"{Marker} | PO {po.PoNo}", Kind = InvoiceKinds.Supplier,
            PeriodTo = current.Select(l => dnHeader.GetValueOrDefault(l.DnId)?.DnDate).Where(d => d != null).DefaultIfEmpty(null).Max(),
        };
        var build = new InvoiceBuild { Header = header, PreviousApproved = p.SubInvoices.Where(i => i.ContractNo == po.PoNo && i.Subcontractor == po.Supplier && i.Status == SubInvoiceStatus.Approved && i.InvoiceNo < invoiceNo).OrderByDescending(i => i.InvoiceNo).FirstOrDefault() };
        var conflicts = m.Locks.Where(k => currentDnLineIds.Contains(k.DnLineId) && !(k.InvoiceNo == invoiceNo && MaterialsSnapshot.PoKey(k.PoNo) == poKey)).ToList();
        foreach (var k in conflicts) build.Warnings.Add($"DN line {dnById.GetValueOrDefault(k.DnLineId)?.ItemNo} is already invoiced in {k.Supplier} {k.PoNo} INV-{k.InvoiceNo:00} - it will be refused on save");
        var tol = settings.ToleranceFor(po);
        var order = 0;
        build.Lines.Add(new SubInvoiceLine { Kind = "SECTION", RowOrder = order++, Description = $"PO {po.PoNo} - {po.Supplier} - {po.Scope}".Trim(' ', '-') });
        foreach (var pl in m.LinesOf(po))
        {
            double Sum(IEnumerable<MatDnLine> ls) => ls.Where(l => l.PoLineId == pl.Id).Sum(l => l.Qty);
            var prevQty = Sum(prev);
            var currLines = current.Where(l => l.PoLineId == pl.Id).ToList();
            var currQty = Sum(currLines);
            var cumQty = prevQty + currQty;
            if (cumQty > pl.Qty * (1 + tol) + 1e-6)
                build.Warnings.Add($"PO line {pl.LineNo} {pl.Description}: invoiced to date {Units.Fmt(cumQty)} {pl.Unit} exceeds PO {Units.Fmt(pl.Qty)} +{tol:P0}");
            foreach (var bad in currLines.Where(l => l.MatchStatus is MatchStatus.QtyMismatch or MatchStatus.OverPo or MatchStatus.DnWithoutMir))
                build.Warnings.Add($"DN {dnHeader.GetValueOrDefault(bad.DnId)?.DnNo} line {bad.ItemNo}: {bad.MatchStatus} - {bad.MatchNote}");
            var why = currLines.Count == 0 ? "" : "DN " + string.Join(", ", currLines.GroupBy(l => dnHeader.GetValueOrDefault(l.DnId)?.DnNo).Select(g => $"{g.Key} ({string.Join("+", g.Select(l => Units.Fmt(l.Qty)))})"));
            var splits = Splits(m, po, pl);
            foreach (var (code, share, src) in splits)
            {
                var budget = p.BoqItems.FirstOrDefault(b => b.ItemCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                build.Lines.Add(new SubInvoiceLine
                {
                    Kind = "ITEM", RowOrder = order++, ItemNo = pl.LineNo.ToString("00"), BoqCode = code, BoqDescription = budget?.Description ?? "",
                    CostCode = pl.CostCode.Length > 0 && splits.Count == 1 ? pl.CostCode : budget?.CostCode ?? pl.CostCode,
                    BudgetResourceCode = pl.BudgetResourceCode.Length > 0 && splits.Count == 1 ? pl.BudgetResourceCode : budget?.BudgetResourceCode ?? pl.BudgetResourceCode,
                    Description = pl.Description, Unit = Units.Normalize(pl.Unit), ContractQty = Math.Round(pl.Qty * share, 4), Rate = pl.Rate, StagePct = 1,
                    PrevQty = Math.Round(prevQty * share, 4), CurrQty = Math.Round(currQty * share, 4), CumQty = Math.Round(cumQty * share, 4),
                    Explanation = (splits.Count > 1 ? $"{share:P1} of PO line {pl.LineNo} ({src}). " : src.Length > 0 ? src + ". " : "") + why,
                });
            }
            if (pl.BoqCode.Length == 0 && currQty > 0) build.Warnings.Add($"PO line {pl.LineNo} {pl.Description} has no BOQ code - run AUTO-CODE or pick one");
        }
        if (current.Any(l => l.PoLineId is null)) build.Warnings.Add($"{current.Count(l => l.PoLineId is null)} selected DN line(s) are not matched to a PO line and are left out");
        return new SupplierInvoiceBuild { Build = build, Po = po, CurrentLines = current.Where(l => l.PoLineId != null).ToList() };
    }

    /// <summary>BOQ code split of a PO line: the PO scope-of-work rows with the same fingerprint (by approved qty), else the line's own code.</summary>
    public static List<(string Code, double Share, string Source)> Splits(MaterialsSnapshot m, MatPo po, MatPoLine pl)
    {
        var rows = pl.Fingerprint.Length == 0 ? new List<MatPoScope>() : m.PoScope.Where(s => s.PoId == po.Id && s.Fingerprint == pl.Fingerprint && Units.Agree(s.Unit, pl.Unit) && s.BoqCode.Length > 0).ToList();
        var total = rows.Sum(r => r.Qty);
        if (rows.Select(r => r.BoqCode).Distinct().Count() > 1 && total > 0 && pl.CodeStatus is not (CodeStatus.Manual or CodeStatus.Confirmed))
            return rows.GroupBy(r => r.BoqCode).Select(g => (g.Key, g.Sum(r => r.Qty) / total, $"PO scope-of-work: {Units.Fmt(g.Sum(r => r.Qty))} {g.First().Unit} on {g.Key}")).ToList();
        return new List<(string, double, string)> { (pl.BoqCode, 1.0, pl.CodeSource) };
    }

    /// <summary>Locks the DN lines, then saves the draft through the shared invoice workflow; locks point to the saved invoice.</summary>
    public static SubInvoice Save(IProjectStore projectStore, IMaterialsStore store, SupplierInvoiceBuild b)
    {
        var h = b.Build.Header;
        var ids = b.CurrentLines.Select(l => l.Id).ToList();
        var already = store.All<MatDnInvoiceLock>().Select(k => k.DnLineId).ToHashSet();
        var fresh = ids.Where(i => !already.Contains(i)).ToList();
        store.LockDnLines(ids, h.Subcontractor, h.ContractNo, h.InvoiceNo, 0);
        SubInvoice saved;
        try { saved = InvoiceWorkflow.SaveDraft(projectStore, b.Build); }
        catch
        {
            // the invoice was not saved: undo the locks this call added, so no orphan locks are left behind
            store.ReleaseLines(fresh);
            throw;
        }
        store.LockDnLines(ids, h.Subcontractor, h.ContractNo, h.InvoiceNo, saved.Id);
        return saved;
    }

    /// <summary>Excel + PDF in the template layout and a ZIP named with the revision (adds the MIR tracker and any source files given).</summary>
    public static string ExportPackage(string folder, SupplierInvoiceBuild b, InvoiceHeaderInfo info, MaterialsSnapshot m, MaterialsSettings settings, IEnumerable<string>? attachments = null)
    {
        Directory.CreateDirectory(folder);
        var h = b.Build.Header;
        var name = Safe($"{h.Subcontractor}-{h.ContractNo}-INV-{h.InvoiceNo:00}-Rev{h.Revision}");
        var xlsx = Path.Combine(folder, name + ".xlsx");
        var pdf = Path.Combine(folder, name + ".pdf");
        var tracker = Path.Combine(folder, Safe($"{h.ContractNo}-MIR-TRACKER") + ".xlsx");
        var sheetInfo = new InvoiceHeaderInfo
        {
            ProjectName = info.ProjectName, ProjectCode = info.ProjectCode, Location = info.Location, ProjectDirector = info.ProjectDirector, VendorNo = info.VendorNo,
            Category = " MATERIAL SUPPLY ", ScopeOfWork = b.Po.Scope.Length > 0 ? b.Po.Scope.ToUpperInvariant() : "SUPPLY OF MATERIALS", CoveredPeriod = info.CoveredPeriod,
            SignatureRoles = info.SignatureRoles, SignatureNames = info.SignatureNames,
        };
        InvoiceExcelExporter.Export(xlsx, b.Build, sheetInfo, $"{h.Subcontractor} INV {h.InvoiceNo}");
        InvoicePdfExporter.Export(pdf, b.Build, sheetInfo);
        MirTrackerExporter.Export(tracker, m, settings, b.Po);
        var zip = Path.Combine(folder, name + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var f in new[] { xlsx, pdf, tracker }.Concat(attachments ?? Array.Empty<string>()).Where(File.Exists).Distinct())
                z.CreateEntryFromFile(f, Path.GetFileName(f));
        }
        return zip;
    }

    public static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
