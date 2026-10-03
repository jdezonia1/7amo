using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Invoicing;

namespace Raffaello.Core.Mos;

/// <summary>
/// Owner MOS valuation export. Excel = the owner App F layout ("11. App F - MOS On", columns A-Q, no VAT - VAT is applied once on
/// 01.IPC) plus a CHECK sheet; the PDF keeps the summary layout.
/// Template-swappable: when a template workbook is given, the lines are written under its header row (columns found by their
/// header text: BOQ / DESCRIPTION / UNIT / RATE / DELIVERED / INSTALLED / ON SITE / MOS % / AMOUNT / PREVIOUS / CURRENT).
/// </summary>
public static class MosExporter
{
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");

    static MosExporter() { QuestPDF.Settings.License = LicenseType.Community; }

    public static readonly string[] Headers = { "NO", "BOQ CODE", "DESCRIPTION", "UNIT", "BOQ RATE", "DELIVERED QTY", "INSTALLED QTY", "ON SITE QTY", "MOS %", "MOS AMOUNT (CUM)", "PREVIOUS", "CURRENT", "SOURCES" };

    public static void ExportExcel(string path, MosBuild b, InvoiceHeaderInfo info, string? templatePath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath)) { FromTemplate(path, b, templatePath); return; }
        using var wb = new XLWorkbook();
        WriteAppF(wb.Worksheets.Add("11. App F - MOS On"), b, info);
        WriteCheck(wb.Worksheets.Add("CHECK"), b);
        wb.SaveAs(path);
    }

    /// <summary>App F columns (owner IPC "11. App F - MOS On"): rows 13+ are pasted into the IPC; O total is carried to 01.IPC 1.6.</summary>
    public static readonly string[] AppFHeaders =
    {
        "Item", "BOQ Item", "BoQ Description", "Material", "Contract Qty", "Unit", "BoQ Rate", "Previous", "This Month", "To Date",
        "Qty used at site", "Balance Qty", "Market Rate", "75% of Market Rate", "Amount", "MIR Ref", "PO / Subcontract Ref",
    };

    private static void WriteAppF(IXLWorksheet ws, MosBuild b, InvoiceHeaderInfo info)
    {
        var h = b.Header;
        var pct = h.MosPct.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ws.Cell("A1").Value = "APPENDIX F - MATERIALS ON SITE"; ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A3").Value = "Project"; ws.Cell("C3").Value = info.ProjectName;
        ws.Cell("A4").Value = "Project code"; ws.Cell("C4").Value = info.ProjectCode;
        ws.Cell("A5").Value = "Valuation"; ws.Cell("C5").Value = $"{h.Title}  {h.Status}";
        ws.Cell("A6").Value = "Period to"; ws.Cell("C6").Value = h.PeriodTo; ws.Cell("C6").Style.NumberFormat.Format = "dd-mmm-yyyy";
        ws.Cell("A7").Value = "MOS rule"; ws.Cell("C7").Value = $"{h.MosPct:P0} of the market (supplier / PO) rate x balance qty, capped at {h.MosPct:P0} of BoQ rate x contract qty";
        ws.Range("A3:A7").Style.Font.Bold = true;
        // two header rows as in App F: "Delivered Qty" over Previous / This Month / To Date
        for (var i = 0; i < AppFHeaders.Length; i++)
        {
            if (i is >= 7 and <= 9) { ws.Cell(11, i + 1).Value = AppFHeaders[i]; continue; }
            ws.Cell(10, i + 1).Value = AppFHeaders[i];
            ws.Range(10, i + 1, 11, i + 1).Merge();
        }
        ws.Range("H10:J10").Merge(); ws.Cell("H10").Value = "Delivered Qty";
        var head = ws.Range(10, 1, 11, AppFHeaders.Length);
        head.Style.Fill.BackgroundColor = Grey; head.Style.Font.Bold = true; head.Style.Font.FontColor = XLColor.Black;
        head.Style.Alignment.WrapText = true; head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell("A12").Value = "Electrical Items"; ws.Range("A12:Q12").Style.Font.Bold = true;
        var r = 13; var n = 1;
        foreach (var l in b.Lines)
        {
            ws.Cell(r, 1).Value = n++; ws.Cell(r, 2).Value = l.BoqCode; ws.Cell(r, 3).Value = l.BoqDescription; ws.Cell(r, 4).Value = l.Material;
            ws.Cell(r, 5).Value = l.ContractQty; ws.Cell(r, 6).Value = l.Unit; ws.Cell(r, 7).Value = l.BoqRate;
            ws.Cell(r, 8).Value = l.PrevDeliveredQty; ws.Cell(r, 9).FormulaA1 = $"J{r}-H{r}"; ws.Cell(r, 10).Value = l.DeliveredQty;
            ws.Cell(r, 11).Value = l.InstalledQty; ws.Cell(r, 12).FormulaA1 = $"MAX(0,J{r}-K{r})";
            if (l.UsesMarketRate)
            {
                ws.Cell(r, 13).Value = l.MarketRate; ws.Cell(r, 14).FormulaA1 = $"M{r}*{pct}";
                // O = 75% market rate x balance (limited to contract qty once material is used), capped at 75% x BoQ rate x contract qty
                ws.Cell(r, 15).FormulaA1 = $"ROUND(IF(G{r}*E{r}>0,MIN(N{r}*IF(AND(K{r}>0,E{r}>0),MIN(L{r},E{r}),L{r}),G{r}*E{r}*{pct}),N{r}*L{r}),2)";
            }
            else
            {
                ws.Cell(r, 13).Value = "no PO rate"; ws.Cell(r, 13).Style.Font.FontColor = XLColor.Red;
                ws.Cell(r, 15).FormulaA1 = $"ROUND(L{r}*G{r}*{pct},2)";
            }
            ws.Cell(r, 16).Value = l.MirRefs; ws.Cell(r, 17).Value = l.PoRefs;
            r++;
        }
        var last = r - 1;
        ws.Cell(r, 3).Value = "Total carried forward to IPC"; ws.Cell(r, 15).FormulaA1 = last >= 13 ? $"SUM(O13:O{last})" : "0";
        ws.Range(r, 1, r, 17).Style.Font.Bold = true; ws.Range(r, 1, r, 17).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Range(13, 5, r, 5).Style.NumberFormat.Format = "#,##0.##";
        ws.Range(13, 7, r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(13, 8, r, 12).Style.NumberFormat.Format = "#,##0.##";
        ws.Range(13, 13, r, 15).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(1).Width = 6; ws.Column(2).Width = 26; ws.Column(3).Width = 48; ws.Column(4).Width = 34; ws.Column(16).Width = 22; ws.Column(17).Width = 24;
        foreach (var c in new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 }) ws.Column(c).Width = 12;
        ws.Range(13, 3, Math.Max(13, last), 4).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(11);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape; ws.PageSetup.PaperSize = XLPaperSize.A3Paper; ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(10, 11);
    }

    /// <summary>Per line: previous / this period amounts and why (cap, no PO rate) - for checking, not pasted into the IPC.</summary>
    private static void WriteCheck(IXLWorksheet ws, MosBuild b)
    {
        string[] hd = { "BOQ Item", "Description", "Balance Qty", "Market Rate", "Cap (75% BOQ value)", "Amount (cum)", "Previous", "This period", "Note" };
        for (var i = 0; i < hd.Length; i++) ws.Cell(1, i + 1).Value = hd[i];
        var head = ws.Range(1, 1, 1, hd.Length); head.Style.Fill.BackgroundColor = Grey; head.Style.Font.Bold = true;
        var r = 2;
        foreach (var l in b.Lines)
        {
            ws.Cell(r, 1).Value = l.BoqCode; ws.Cell(r, 2).Value = l.BoqDescription; ws.Cell(r, 3).Value = l.OnSiteQty; ws.Cell(r, 4).Value = l.MarketRate;
            ws.Cell(r, 5).Value = l.Cap; ws.Cell(r, 6).Value = l.CumAmount; ws.Cell(r, 7).Value = l.PrevAmount; ws.Cell(r, 8).Value = l.CurrAmount;
            ws.Cell(r, 9).Value = !l.UsesMarketRate ? "no PO rate - BOQ rate used" : l.Capped ? "capped at 75% of BOQ value" : l.Released > 0 ? "MOS released (installed)" : "";
            r++;
        }
        ws.Cell(r, 2).Value = "TOTAL"; ws.Cell(r, 6).Value = b.CumAmount; ws.Cell(r, 7).Value = b.PrevAmount; ws.Cell(r, 8).Value = b.CurrAmount;
        ws.Range(r, 1, r, 9).Style.Font.Bold = true;
        ws.Range(2, 3, r, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(1).Width = 26; ws.Column(2).Width = 50; ws.Column(9).Width = 30;
        foreach (var c in new[] { 3, 4, 5, 6, 7, 8 }) ws.Column(c).Width = 14;
        ws.SheetView.FreezeRows(1);
    }

    /// <summary>Writes into a copy of the user's template: the first row whose cells include "BOQ" and "RATE" is the header row.</summary>
    private static void FromTemplate(string path, MosBuild b, string templatePath)
    {
        using var wb = new XLWorkbook(templatePath);
        var ws = wb.Worksheets.FirstOrDefault(s => s.Name.Contains("MOS", StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.First();
        int headerRow = 0; var map = new Dictionary<string, int>();
        foreach (var row in ws.RowsUsed().Take(60))
        {
            var cells = row.CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetFormattedString().ToUpperInvariant());
            if (cells.Values.Any(v => v.Contains("BOQ")) && cells.Values.Any(v => v.Contains("RATE"))) { headerRow = row.RowNumber(); foreach (var (col, v) in cells) map[v] = col; break; }
        }
        if (headerRow == 0) throw new InvalidDataException("The MOS template has no header row with BOQ and RATE.");
        int? Col(params string[] keys) { foreach (var (h, c) in map) if (keys.Any(k => h.Contains(k))) return c; return null; }
        var cols = new (int? Col, Func<MosLine, object> Val)[]
        {
            (Col("BOQ"), l => l.BoqCode), (Col("DESCRIPTION"), l => l.BoqDescription), (Col("UNIT"), l => l.Unit), (Col("RATE"), l => l.BoqRate),
            (Col("DELIVERED"), l => l.DeliveredQty), (Col("INSTALLED"), l => l.InstalledQty), (Col("ON SITE"), l => l.OnSiteQty), (Col("MOS %", "PERCENT"), l => l.MosPct),
            (Col("CUM", "AMOUNT"), l => l.CumAmount), (Col("PREV"), l => l.PrevAmount), (Col("CURR"), l => l.CurrAmount),
        };
        var r = headerRow + 1;
        foreach (var l in b.Lines)
        {
            foreach (var (c, val) in cols)
                if (c is int ci) { var v = val(l); ws.Cell(r, ci).Value = v switch { double d => d, _ => v.ToString() }; }
            r++;
        }
        wb.SaveAs(path);
    }

    public static void ExportPdf(string path, MosBuild b, InvoiceHeaderInfo info)
    {
        var h = b.Header;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape()); page.Margin(20); page.DefaultTextStyle(x => x.FontSize(7.5f));
            page.Header().Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("MATERIALS ON SITE (MOS) VALUATION").FontSize(14).Bold().FontColor("#8B0000");
                    r.ConstantItem(260).AlignRight().Text($"{h.Title}  |  {h.Status}  |  period to {h.PeriodTo:dd-MMM-yyyy}").Bold();
                });
                c.Item().Text($"{info.ProjectName}   {info.ProjectCode}   {info.Location}   MOS {h.MosPct:P0}   Aconex {h.AconexNo}");
            });
            page.Content().PaddingTop(6).Column(col =>
            {
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(20); cd.ConstantColumn(105); cd.RelativeColumn(4); cd.ConstantColumn(30); cd.ConstantColumn(55); cd.ConstantColumn(55); cd.ConstantColumn(55); cd.ConstantColumn(55); cd.ConstantColumn(32); cd.ConstantColumn(65); cd.ConstantColumn(60); cd.ConstantColumn(60); });
                    t.Header(hd => { foreach (var s in Headers.Take(12)) hd.Cell().Background("#A6A6A6").Padding(2).Text(s).Bold().FontColor(Colors.Black); });
                    var n = 1;
                    foreach (var l in b.Lines)
                    {
                        void T(string s) => t.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(s);
                        void N(double v, string f = "N2") => t.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight().Text(v.ToString(f));
                        T((n++).ToString()); T(l.BoqCode); T(l.BoqDescription); T(l.Unit); N(l.BoqRate); N(l.DeliveredQty); N(l.InstalledQty); N(l.OnSiteQty); N(l.MosPct, "P0"); N(l.CumAmount); N(l.PrevAmount); N(l.CurrAmount);
                    }
                });
                col.Item().PaddingTop(8).AlignRight().Width(330).Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(2); cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                    foreach (var s in new[] { "", "Cum.", "Prev.", "Curr." }) t.Cell().Background("#A6A6A6").Padding(2).Text(s).Bold();
                    void L(string label, double a, double p, double c) { t.Cell().Padding(2).Text(label).Bold(); foreach (var v in new[] { a, p, c }) t.Cell().Padding(2).AlignRight().Text(v.ToString("N2")); }
                    L("Gross MOS", b.CumAmount, b.PrevAmount, b.CurrAmount);
                });
                if (b.Released > 0) col.Item().PaddingTop(4).AlignRight().Text($"MOS released on installed material this period: SAR {b.Released:N2}").Italic();
                col.Item().PaddingTop(30).Row(r => { foreach (var s in new[] { "Prepared by (QS)", "Commercial Manager", "Project Manager", "Project Director" }) r.RelativeItem().BorderTop(0.6f).PaddingTop(2).AlignCenter().Text(s).SemiBold(); });
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Raffaello  |  {h.Title}  |  printed {DateTime.Now:dd-MMM-yyyy HH:mm}").FontColor(Colors.Grey.Darken1);
                r.ConstantItem(80).AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
            });
        })).GeneratePdf(path);
    }
}
