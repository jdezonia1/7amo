using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Invoicing;

/// <summary>A subcontractor's past invoice file (template layout) with executed prev / curr / cum per item x BOQ code row.</summary>
public sealed class PastInvoiceFile
{
    public string FileName { get; init; } = "";
    public string SheetName { get; init; } = "";
    public string Vendor { get; init; } = "";
    public int InvoiceNo { get; set; }
    public string InvoiceNoSource { get; set; } = "";
    public List<InvoiceTemplateRow> Rows { get; init; } = new();
    public List<ImportIssue> Issues { get; } = new();

    public static string RowKey(string itemNo, string boqCode) => $"{itemNo}|{boqCode}".ToUpperInvariant();

    /// <summary>Executed cumulative quantity per "item|code" (rows repeated in the template are added).</summary>
    public Dictionary<string, double> CumByRow() =>
        Rows.Where(r => r.Kind == "ITEM").GroupBy(r => RowKey(r.ItemNo, r.BoqCode)).ToDictionary(g => g.Key, g => g.Sum(r => r.ImportedExecutedCum));

    public string Summary => $"{Path.GetFileName(FileName)}: {Vendor} INV {InvoiceNo} ({InvoiceNoSource}), {Rows.Count(r => r.Kind == "ITEM" && r.ImportedExecutedCum != 0)} rows with executed qty, " +
                             $"cum amount SAR {Rows.Where(r => r.Kind == "ITEM").Sum(r => r.ImportedExecutedCum * r.Rate * r.StagePct):N2}";
}

/// <summary>
/// Reads a subcontractor's past invoice workbooks (same layout as the invoice template: O prev / P curr / Q cum executed qty).
/// The invoice no. comes from the header (L6 'Subcon.Inv.No' value in M6, else Y6 'PC No'), else the sheet name, else the file name.
/// </summary>
public static class PastInvoiceImporter
{
    public static PastInvoiceFile Read(string path, string contractNo, string? sheetName = null)
    {
        var t = InvoiceTemplateImporter.Read(path, contractNo, sheetName);
        string m6 = "", y6 = "";
        using (var x = new XlsxStreamReader(path))
            foreach (var r in x.ReadRows(t.SheetName))
            {
                if (r.Number == 6) { m6 = r.Get("M").Trim(); y6 = r.Get("Y").Trim(); }
                if (r.Number >= 6) break;
            }
        var f = new PastInvoiceFile { FileName = path, SheetName = t.SheetName, Vendor = t.VendorName, Rows = t.Rows };
        f.Issues.AddRange(t.Issues);
        (f.InvoiceNo, f.InvoiceNoSource) = Number(m6) is { } a ? (a, "header M6") : Number(y6) is { } b ? (b, "header PC No") :
            Trailing(t.SheetName) is { } c ? (c, "sheet name") : FromFileName(Path.GetFileNameWithoutExtension(path)) is { } d ? (d, "file name") : (0, "not found");
        if (f.InvoiceNo <= 0) f.Issues.Add(new(6, IssueLevel.Error, $"{Path.GetFileName(path)}: invoice number not found (M6 / Y6 / sheet / file name)."));
        foreach (var r in f.Rows.Where(r => r.Kind == "ITEM" && Math.Abs(r.ImportedExecutedPrev + r.ImportedExecutedCurr - r.ImportedExecutedCum) > 0.01 && (r.ImportedExecutedPrev != 0 || r.ImportedExecutedCurr != 0)))
            f.Issues.Add(new(r.RowOrder, IssueLevel.Warning, $"Row {r.ItemNo} {r.BoqCode}: prev {r.ImportedExecutedPrev:0.##} + curr {r.ImportedExecutedCurr:0.##} <> cum {r.ImportedExecutedCum:0.##}."));
        return f;
    }

    private static int? Number(string s) => int.TryParse(Regex.Match(s ?? "", @"\d+").Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    private static int? Trailing(string s) => Regex.Match(s ?? "", @"INV\D{0,3}(\d+)\s*$", RegexOptions.IgnoreCase) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    private static int? FromFileName(string s) =>
        Regex.Match(s ?? "", @"(?:INV|INVOICE)[\s_-]*0*(\d{1,3})\b|-0*(\d{1,3})_", RegexOptions.IgnoreCase) is { Success: true } m
            ? int.Parse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Stores the file as an APPROVED, locked invoice (it was certified outside Raffaello), so the next invoice's "previous" is right.
    /// Does nothing when that invoice already exists for the contract / subcontractor.
    /// </summary>
    public static SubInvoice? Commit(PastInvoiceFile f, IProjectStore store, string contractNo, string subcontractor)
    {
        var sub = subcontractor.Trim().ToUpperInvariant();
        if (store.All<SubInvoice>().Any(i => i.ContractNo == contractNo && i.Subcontractor == sub && i.InvoiceNo == f.InvoiceNo)) return null;
        var h = new SubInvoice
        {
            ContractNo = contractNo, Subcontractor = sub, InvoiceNo = f.InvoiceNo, Revision = 0, Status = SubInvoiceStatus.Approved, Locked = true,
            CreatedAt = DateTime.Now, ApprovedAt = DateTime.Now, Notes = $"Imported from {Path.GetFileName(f.FileName)}",
        };
        var lines = f.Rows.Select(r => new SubInvoiceLine
        {
            RowOrder = r.RowOrder, Kind = r.Kind, ItemNo = r.ItemNo, BoqCode = r.BoqCode, CostCode = r.CostCode, BudgetResourceCode = r.BudgetResourceCode,
            Description = r.Description, Unit = r.Unit, ContractQty = r.Qty, Rate = r.Rate, StagePct = r.StagePct,
            PrevQty = r.ImportedExecutedPrev, CurrQty = r.ImportedExecutedCurr, CumQty = r.ImportedExecutedCum, Explanation = "imported invoice file",
        }).ToList();
        store.Batch(w =>
        {
            w.Insert(h);
            foreach (var l in lines) l.SubInvoiceId = h.Id;
            w.InsertMany(lines);
        }, $"{h.Title} imported from file (approved outside Raffaello)");
        return h;
    }
}
