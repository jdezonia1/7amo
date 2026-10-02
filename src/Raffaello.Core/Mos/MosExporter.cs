using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Invoicing;

namespace Raffaello.Core.Mos;

/// <summary>
/// Owner MOS valuation export in the subcontractor-invoice style (grey bold headers, header block, totals, VAT, signatures).
/// Template-swappable: when a template workbook is given, the lines are written under its header row (columns found by their
/// header text: BOQ / DESCRIPTION / UNIT / RATE / DELIVERED / INSTALLED / ON SITE / MOS % / AMOUNT / PREVIOUS / CURRENT).
/// </summary>
public static class MosExporter
{
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");
    private const double Vat = 0.15;

    static MosExporter() { QuestPDF.Settings.License = LicenseType.Community; }

    public static readonly string[] Headers = { "NO", "BOQ CODE", "DESCRIPTION", "UNIT", "BOQ RATE", "DELIVERED QTY", "INSTALLED QTY", "ON SITE QTY", "MOS %", "MOS AMOUNT (CUM)", "PREVIOUS", "CURRENT", "SOURCES" };

    public static void ExportExcel(string path, MosBuild b, InvoiceHeaderInfo info, string? templatePath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath)) { FromTemplate(path, b, templatePath); return; }
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(b.Header.Title.Replace(" ", "_"));
        var h = b.Header;
        ws.Cell("A1").Value = "MATERIALS ON SITE (MOS) VALUATION"; ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14; ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#8B0000");
        void Pair(int row, string l1, object v1, string l2, object v2)
        {
            ws.Cell(row, 1).Value = l1; ws.Cell(row, 3).Value = v1 switch { DateTime d => d, double x => x, _ => v1.ToString() };
            ws.Cell(row, 8).Value = l2; ws.Cell(row, 10).Value = v2 switch { DateTime d => d, double x => x, _ => v2.ToString() };
            ws.Cell(row, 1).Style.Font.Bold = true; ws.Cell(row, 8).Style.Font.Bold = true;
        }
        Pair(3, "Project", info.ProjectName, "Valuation", $"{h.Title}  {h.Status}");
        Pair(4, "Project Code", info.ProjectCode, "Period to", h.PeriodTo);
        Pair(5, "Location", info.Location, "MOS %", h.MosPct);
        Pair(6, "Project Director", info.ProjectDirector, "Aconex", h.AconexNo);
        ws.Cell(4, 10).Style.NumberFormat.Format = "dd-mmm-yyyy"; ws.Cell(5, 10).Style.NumberFormat.Format = "0%";
        const int hr = 8;
        for (var i = 0; i < Headers.Length; i++) ws.Cell(hr, i + 1).Value = Headers[i];
        var head = ws.Range(hr, 1, hr, Headers.Length);
        head.Style.Fill.BackgroundColor = Grey; head.Style.Font.Bold = true; head.Style.Font.FontColor = XLColor.Black; head.Style.Alignment.WrapText = true;
        var r = hr + 1;
        var n = 1;
        foreach (var l in b.Lines)
        {
            ws.Cell(r, 1).Value = n++; ws.Cell(r, 2).Value = l.BoqCode; ws.Cell(r, 3).Value = l.BoqDescription; ws.Cell(r, 4).Value = l.Unit;
            ws.Cell(r, 5).Value = l.BoqRate; ws.Cell(r, 6).Value = l.DeliveredQty; ws.Cell(r, 7).Value = l.InstalledQty;
            ws.Cell(r, 8).FormulaA1 = $"MAX(0,F{r}-G{r})"; ws.Cell(r, 9).Value = l.MosPct;
            ws.Cell(r, 10).FormulaA1 = $"ROUND(H{r}*E{r}*I{r},2)"; ws.Cell(r, 11).Value = l.PrevAmount; ws.Cell(r, 12).FormulaA1 = $"J{r}-K{r}";
            ws.Cell(r, 13).Value = l.Sources;
            r++;
        }
        var last = r - 1;
        var f = r + 1;
        string Sum(string c) => last >= hr + 1 ? $"SUM({c}{hr + 1}:{c}{last})" : "0";
        ws.Cell(f, 9).Value = "Gross MOS"; ws.Cell(f, 10).FormulaA1 = Sum("J"); ws.Cell(f, 11).FormulaA1 = Sum("K"); ws.Cell(f, 12).FormulaA1 = Sum("L");
        ws.Cell(f + 1, 9).Value = "VAT @ 15%"; foreach (var c in new[] { "J", "K", "L" }) ws.Cell(f + 1, c).FormulaA1 = $"{c}{f}*{Vat.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        ws.Cell(f + 2, 9).Value = "Total incl. VAT"; foreach (var c in new[] { "J", "K", "L" }) ws.Cell(f + 2, c).FormulaA1 = $"{c}{f}+{c}{f + 1}";
        ws.Range(f, 9, f + 2, 12).Style.Font.Bold = true;
        ws.Range(hr + 1, 5, f + 2, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(hr + 1, 9, Math.Max(hr + 1, last), 9).Style.NumberFormat.Format = "0%";
        ws.Range(hr + 1, 10, f + 2, 12).Style.NumberFormat.Format = "#,##0.00";
        var sig = new[] { "Prepared by (QS)", "Commercial Manager", "Project Manager", "Project Director" };
        for (var i = 0; i < sig.Length; i++) { ws.Cell(f + 5, 2 + i * 3).Value = sig[i]; ws.Cell(f + 5, 2 + i * 3).Style.Font.Bold = true; }
        ws.Column(1).Width = 5; ws.Column(2).Width = 24; ws.Column(3).Width = 50; ws.Column(13).Width = 40;
        foreach (var c in new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12 }) ws.Column(c).Width = 13;
        ws.SheetView.FreezeRows(hr);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape; ws.PageSetup.PaperSize = XLPaperSize.A3Paper; ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(hr, hr);
        wb.SaveAs(path);
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
                    L("VAT @ 15%", b.CumAmount * Vat, b.PrevAmount * Vat, b.CurrAmount * Vat);
                    L("Total incl. VAT", b.CumAmount * (1 + Vat), b.PrevAmount * (1 + Vat), b.CurrAmount * (1 + Vat));
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
