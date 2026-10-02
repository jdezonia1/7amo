using System.Globalization;
using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Boq;

public sealed class BoqImportResult
{
    public string FileName { get; init; } = "";
    public List<BoqLine> Rows { get; } = new();
    public List<string> Issues { get; } = new();
    public List<string> Sheets { get; } = new();
    public int Items => Rows.Count(r => !r.IsHeading);
    public double Total => Rows.Where(r => !r.IsHeading).Sum(r => r.Amount);
    public string Summary => $"{Sheets.Count} sheet(s), {Items} items, {Rows.Count(r => r.IsHeading)} headings, SAR {Total:N2}; categorised {Rows.Count(r => !r.IsHeading && r.System.Length > 0)} / {Items}";
}

/// <summary>
/// Owner BOQ workbook import: every sheet with a header row (DESCRIPTION + UNIT + QTY / RATE) is read; rows without qty and rate
/// are headings that give context to the rows below. Checks qty x rate = amount. Then <see cref="BoqCategorizer"/> runs.
/// </summary>
public static class BoqImporter
{
    public static BoqImportResult Read(string path, IEnumerable<BoqCatRule>? learned = null)
    {
        var res = new BoqImportResult { FileName = Path.GetFileName(path) };
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            if (used is null) continue;
            int headerRow = 0; var cols = new Dictionary<string, int>();
            var lastCol = used.LastColumn().ColumnNumber();
            for (var r = used.FirstRow().RowNumber(); r <= Math.Min(used.LastRow().RowNumber(), 40) && headerRow == 0; r++)
            {
                var heads = Enumerable.Range(1, lastCol).Select(c => (c, h: ws.Cell(r, c).GetFormattedString().Trim().ToUpperInvariant())).Where(x => x.h.Length > 0).ToList();
                if (heads.Any(x => x.h.Contains("DESCRIPTION") || x.h.Contains("الوصف")) && heads.Any(x => x.h is "UNIT" or "UOM" || x.h.StartsWith("UNIT") || x.h.Contains("الوحدة"))
                    && heads.Any(x => x.h.StartsWith("QTY") || x.h.Contains("QUANTITY") || x.h.Contains("RATE") || x.h.Contains("الكمية")))
                {
                    headerRow = r;
                    foreach (var (c, h) in heads)
                    {
                        string? key = h.Contains("DESCRIPTION") || h.Contains("الوصف") ? "DESC" : h is "UNIT" or "UOM" || h.StartsWith("UNIT ") && !h.Contains("RATE") && !h.Contains("PRICE") || h.Contains("الوحدة") ? "UNIT"
                            : h.StartsWith("QTY") || h.Contains("QUANTITY") || h.Contains("الكمية") ? "QTY" : h.Contains("RATE") || h.Contains("UNIT PRICE") || h.Contains("سعر") ? "RATE"
                            : h.Contains("AMOUNT") || h.Contains("TOTAL") || h.Contains("الإجمالي") ? "AMOUNT" : h.Contains("BOQ") || h.Contains("CODE") || h.Contains("REF") ? "CODE"
                            : h is "ITEM" or "NO" or "NO." or "S/N" or "ITEM NO" or "البند" ? "ITEM" : h.Contains("BILL") ? "BILL" : null;
                        if (key != null && !cols.ContainsKey(key)) cols[key] = c;
                    }
                }
            }
            if (headerRow == 0 || !cols.ContainsKey("DESC")) continue;
            res.Sheets.Add(ws.Name);
            for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
            {
                string S(string k) => cols.TryGetValue(k, out var c) ? ws.Cell(r, c).GetFormattedString().Trim() : "";
                double? N(string k)
                {
                    if (!cols.TryGetValue(k, out var c)) return null;
                    var cell = ws.Cell(r, c);
                    if (cell.DataType == XLDataType.Number) return cell.GetDouble();
                    return Units.ParseNumber(cell.GetFormattedString());
                }
                var desc = S("DESC");
                if (desc.Length == 0) continue;
                var qty = N("QTY"); var rate = N("RATE"); var amt = N("AMOUNT");
                var item = S("ITEM"); var code = S("CODE");
                var heading = (qty ?? 0) == 0 && (rate ?? 0) == 0 && (amt ?? 0) == 0;
                if (!BoqCodes.IsCode(code)) code = item.Length > 0 && !heading ? $"{ws.Name}-{item}".ToUpperInvariant().Replace(' ', '-') : code;
                var line = new BoqLine
                {
                    Source = res.FileName, Sheet = ws.Name, RowNo = r, Bill = S("BILL").Length > 0 ? S("BILL") : (BoqCodes.IsCode(code) ? BoqCodes.Bill(code) : ws.Name),
                    ItemNo = item, BoqCode = code.ToUpperInvariant(), Description = desc, Unit = S("UNIT"), Qty = qty ?? 0, Rate = rate ?? 0,
                    Amount = amt ?? Math.Round((qty ?? 0) * (rate ?? 0), 2), IsHeading = heading,
                };
                if (!heading && qty is double q && rate is double rt && amt is double a && !Documents.Checks.AmountOk(q, rt, a))
                    res.Issues.Add($"{ws.Name} row {r}: qty x rate = {q * rt:N2} but amount is {a:N2}");
                res.Rows.Add(line);
            }
        }
        if (res.Sheets.Count == 0) res.Issues.Add("No sheet with a DESCRIPTION / UNIT / QTY header row was found.");
        foreach (var dup in res.Rows.Where(r => !r.IsHeading && r.BoqCode.Length > 0).GroupBy(r => r.BoqCode).Where(g => g.Count() > 1).Take(20))
            res.Issues.Add($"BOQ code {dup.Key} appears {dup.Count()} times");
        BoqCategorizer.Apply(res.Rows, learned ?? Array.Empty<BoqCatRule>());
        return res;
    }

    /// <summary>Replaces the rows previously imported from the same file name.</summary>
    public static int Commit(IMaterialsStore store, BoqImportResult res)
    {
        var old = store.All<BoqLine>().Where(b => b.Source.Equals(res.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        store.Batch(w =>
        {
            foreach (var o in old) w.Delete(o);
            foreach (var r in res.Rows) r.Id = 0;
            w.InsertMany(res.Rows);
        }, $"Owner BOQ {res.FileName}: {res.Summary}");
        return res.Rows.Count;
    }

    internal static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);
}
