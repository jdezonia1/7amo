using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Boq;

/// <summary>
/// The contract BOQ ("Modified BOQ rev 0", 03-Oct): print-style bills. Each printed page has a title block, the header
/// "Ref | Description | Quantity | Unit | Rate | Total" (repeated per page), a section banner "SECTION R - ELECTRICAL
/// INSTALLATIONS", items with a Ref letter and a footer "B6.R / Page 3" in the Total column. Most bills carry two column
/// blocks: the original (description wrapped over many rows) and a merged block (full description on the item row);
/// the merged block - the LAST "Ref" header - is read. Bill 03 has only the merged block, with a blank Description header.
/// There are no codes in the workbook: an item is identified by bill + section + page + Ref; <see cref="ProjectCodeMatcher"/>
/// then finds its project code.
/// </summary>
public static class ContractBoqReader
{
    private static readonly Regex Footer = new(@"^\s*(B\d+)\.([A-Z]+)\s*/\s*Page\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Banner = new(@"^\s*SECTION\s+([A-Z]{1,3})\s*[-–]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RefRx = new(@"^[A-Z]{1,3}$", RegexOptions.Compiled);
    private static readonly Regex SheetBill = new(@"Bill\s*No\.?\s*0*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record Layout(int HeaderRow, int Ref, int Desc, int Qty, int Unit, int Rate, int Total);

    /// <summary>A sheet is print-style when it has a Ref / Quantity / Rate / Total header and "Bx.Y / Page n" footers.</summary>
    public static Layout? Detect(IXLWorksheet ws)
    {
        var used = ws.RangeUsed();
        if (used is null) return null;
        var lastCol = Math.Min(used.LastColumn().ColumnNumber(), 30);
        var lastRow = used.LastRow().RowNumber();
        Layout? layout = null;
        for (var r = 1; r <= Math.Min(lastRow, 40) && layout is null; r++)
        {
            var heads = Enumerable.Range(1, lastCol).Select(c => (c, h: ws.Cell(r, c).GetFormattedString().Trim().ToUpperInvariant())).Where(x => x.h.Length > 0).ToList();
            var refs = heads.Where(x => x.h == "REF").Select(x => x.c).ToList();
            if (refs.Count == 0) continue;
            var rc = refs.Max();                                                  // the merged block is the right-hand one
            int After(Func<string, bool> f) => heads.Where(x => x.c > rc && f(x.h)).Select(x => x.c).DefaultIfEmpty(0).Min();
            var qty = After(h => h.StartsWith("QUANTITY") || h.StartsWith("QTY"));
            var unit = After(h => h.StartsWith("UNIT"));
            var rate = After(h => h.StartsWith("RATE"));
            var total = After(h => h.StartsWith("TOTAL") || h.StartsWith("AMOUNT"));
            if (qty > 0 && rate > 0 && total > 0) layout = new Layout(r, rc, rc + 1, qty, unit, rate, total);
        }
        if (layout is null) return null;
        for (var r = layout.HeaderRow; r <= Math.Min(lastRow, layout.HeaderRow + 400); r++)
            for (var c = layout.Qty; c <= lastCol; c++)
                if (Footer.IsMatch(ws.Cell(r, c).GetFormattedString())) return layout;
        return null;
    }

    /// <summary>Reads one print-style bill into <paramref name="res"/>. Items get Section / Page / Ref when their page footer is reached.</summary>
    public static void ReadSheet(IXLWorksheet ws, Layout l, BoqImportResult res)
    {
        var used = ws.RangeUsed()!;
        var lastCol = Math.Min(used.LastColumn().ColumnNumber(), 30);
        var lastRow = used.LastRow().RowNumber();
        var m = SheetBill.Match(ws.Name);
        var bill = m.Success ? "B" + int.Parse(m.Groups[1].Value) : "";
        var section = "";
        var page = new List<BoqLine>();
        int items = 0, rateOnly = 0;

        string T(int r, int c) => c <= 0 ? "" : ws.Cell(r, c).GetFormattedString().Trim();
        double? N(int r, int c)
        {
            if (c <= 0) return null;
            var cell = ws.Cell(r, c);
            if (cell.DataType == XLDataType.Number) return cell.GetDouble();
            return Units.ParseNumber(cell.GetFormattedString());
        }

        for (var r = l.HeaderRow + 1; r <= lastRow; r++)
        {
            var refTxt = T(r, l.Ref);
            var desc = T(r, l.Desc);
            if (refTxt.Equals("REF", StringComparison.OrdinalIgnoreCase)) continue;                 // header repeated on every page

            // footer "B6.R / Page 3" in the Total column (collection lists carry the same text in the Description column)
            Match? f = null;
            for (var c = l.Qty; c <= lastCol && f is null; c++) { var fm = Footer.Match(T(r, c)); if (fm.Success) f = fm; }
            if (f != null)
            {
                var fBill = f.Groups[1].Value.ToUpperInvariant(); var fSec = f.Groups[2].Value.ToUpperInvariant(); var fPage = int.Parse(f.Groups[3].Value);
                if (bill.Length == 0) bill = fBill;
                foreach (var it in page)
                {
                    it.Bill = fBill; it.Section = fSec; it.Page = fPage;
                    it.ItemNo = $"{fSec}-{fPage}-{it.Ref}";
                    it.BoqCode = $"{fBill}.{fSec}/P{fPage}/{it.Ref}";
                    it.CodeSource = "PAGE KEY";
                }
                page.Clear();
                continue;
            }
            var b = Banner.Match(desc);
            if (b.Success) { section = b.Groups[1].Value.ToUpperInvariant(); continue; }
            var rowText = string.Join(" ", Enumerable.Range(l.Ref, Math.Max(1, l.Total - l.Ref + 1)).Select(c => T(r, c)));
            if (rowText.Contains("To Collection", StringComparison.OrdinalIgnoreCase) || desc.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase)) continue;

            var qty = N(r, l.Qty); var rate = N(r, l.Rate);
            var totalTxt = T(r, l.Total); var total = N(r, l.Total);
            var upperRef = refTxt.ToUpperInvariant();
            if (RefRx.IsMatch(upperRef) && (qty.HasValue || rate.HasValue || totalTxt.Length > 0))
            {
                var isRateOnly = totalTxt.Length > 0 && total is null;                                  // "Rate Only", "Note", "Incl."
                var line = new BoqLine
                {
                    Source = res.FileName, Sheet = ws.Name, RowNo = r, Bill = bill, Section = section, Ref = upperRef,
                    Description = desc.Length > 0 ? desc : "(no description)", Unit = NormUnit(T(r, l.Unit)),
                    Qty = qty ?? 0, Rate = rate ?? 0, Amount = isRateOnly ? 0 : total ?? Math.Round((qty ?? 0) * (rate ?? 0), 2),
                    RateOnly = isRateOnly,
                };
                if (!isRateOnly && qty is double q && rate is double rt && total is double a && !Documents.Checks.AmountOk(q, rt, a))
                    res.Issues.Add($"{ws.Name} row {r} ({bill}.{section} {upperRef}): qty x rate = {q * rt:N2} but total is {a:N2}");
                page.Add(line); res.Rows.Add(line); items++;
                if (isRateOnly) rateOnly++;
                continue;
            }
            if (refTxt.Length == 0 && desc.Length > 0 && qty is null && rate is null && total is null)
                res.Rows.Add(new BoqLine { Source = res.FileName, Sheet = ws.Name, RowNo = r, Bill = bill, Section = section, Description = desc, IsHeading = true });
        }
        if (page.Count > 0) res.Issues.Add($"{ws.Name}: {page.Count} item(s) after the last page footer - no page number.");
        if (rateOnly > 0) res.Issues.Add($"{ws.Name}: {rateOnly} rate-only item(s) (text in the Total column) - priced, no value.");
        res.Issues.Add($"{ws.Name}: {items} items read (contract BOQ layout, bill {bill}).");
    }

    private static string NormUnit(string u)
    {
        var s = Regex.Replace(u.Trim(), @"\s+", " ").ToLowerInvariant();
        return s switch { "no" or "no." or "nos" or "nr." => "nr", "m²" or "sqm" => "m2", "m³" or "cum" => "m3", "lm" or "l.m" or "rm" => "m", _ => s };
    }
}
