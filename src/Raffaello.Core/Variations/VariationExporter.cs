using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Export;

namespace Raffaello.Core.Variations;

/// <summary>Header block printed on the submission (typed in Settings / on screen, never shipped in code).</summary>
public sealed record VariationHeaderInfo(string Project = "", string Contractor = "", string PreparedBy = "", string Currency = "SAR");

/// <summary>Submission workbook / PDF and the register, in the house style (headers #A6A6A6, bold black text).</summary>
public static class VariationExporter
{
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");
    private static readonly XLColor Red = XLColor.FromHtml("#8E1B22");
    private static readonly XLColor TotalFill = XLColor.FromHtml("#F4E3E3");

    static VariationExporter() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly (string Kind, string Title)[] Sections =
    {
        (VariationLineKinds.Omission, "A. OMISSIONS (existing items, contract rates)"),
        (VariationLineKinds.Addition, "B. ADDITIONS (existing items, contract rates)"),
        (VariationLineKinds.NewItem, "C. NEW ITEMS (rate build-up)"),
    };

    // ------------------------------------------------------------------ submission Excel

    public static void SubmissionExcel(string path, Variation v, IReadOnlyList<VariationLine> lines, IReadOnlyList<VariationDoc> docs, VariationHeaderInfo info)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheet(v.Number));
        var r = 1;
        ws.Cell(r, 1).Value = $"{TypeName(v.Type)} SUBMISSION";
        ws.Cell(r, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(Red);
        r += 2;
        void Hdr(string label, object? value)
        {
            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 3).Value = value switch { null => "", DateTime d => d, double x => x, _ => value.ToString() };
            if (value is DateTime) ws.Cell(r, 3).Style.NumberFormat.Format = "dd-mmm-yyyy";
            ws.Cell(r, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            r++;
        }
        Hdr("PROJECT", info.Project);
        Hdr("CONTRACTOR", info.Contractor);
        Hdr("VARIATION NO", v.Number);
        Hdr("TYPE", TypeName(v.Type));
        Hdr("DATE", v.Date);
        Hdr("CONSULTANT REF", v.ConsultantRef);
        Hdr("TITLE", v.Title);
        Hdr("BUILDING", v.Building);
        Hdr("STATUS", v.Status);
        Hdr("ACONEX WORKFLOW", v.AconexWorkflowNo);
        if (!string.IsNullOrWhiteSpace(v.Description))
        {
            ws.Cell(r, 1).Value = "DESCRIPTION"; ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 3).Value = v.Description;
            ws.Range(r, 3, r, 8).Merge().Style.Alignment.SetWrapText(true).Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            ws.Row(r).Height = Math.Min(160, 15 * (1 + v.Description.Length / 90));
            r++;
        }
        r++;

        var headers = new[] { "ITEM", "BOQ / ITEM CODE", "DESCRIPTION", "UNIT", "QTY", $"RATE ({info.Currency})", $"AMOUNT ({info.Currency})", "NOTES" };
        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(r, c + 1);
            cell.Value = headers[c];
            cell.Style.Fill.BackgroundColor = Grey;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.Black;
            cell.Style.Alignment.WrapText = true;
        }
        var headerRow = r++;
        var subtotalCells = new Dictionary<string, string>();
        foreach (var (kind, title) in Sections)
        {
            var rows = lines.Where(l => l.Kind == kind).ToList();
            ws.Cell(r, 1).Value = title;
            ws.Range(r, 1, r, 8).Style.Font.Bold = true;
            ws.Range(r, 1, r, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#EDEDED");
            r++;
            var first = r;
            var n = 0;
            foreach (var l in rows)
            {
                n++;
                ws.Cell(r, 1).Value = $"{kind[0]}{n}";
                ws.Cell(r, 2).Value = l.ItemCode;
                ws.Cell(r, 3).Value = l.Description;
                ws.Cell(r, 3).Style.Alignment.WrapText = true;
                ws.Cell(r, 4).Value = l.Unit;
                ws.Cell(r, 5).Value = VariationMath.SignedQty(l);
                ws.Cell(r, 6).Value = VariationMath.RateOf(l);
                ws.Cell(r, 7).FormulaA1 = $"ROUND(E{r}*F{r},2)";
                ws.Cell(r, 8).Value = l.Notes;
                r++;
            }
            if (n == 0) { ws.Cell(r, 3).Value = "(none)"; ws.Cell(r, 3).Style.Font.Italic = true; r++; }
            ws.Cell(r, 6).Value = "SUBTOTAL";
            ws.Cell(r, 7).FormulaA1 = n == 0 ? "0" : $"SUM(G{first}:G{r - 1})";
            ws.Range(r, 1, r, 8).Style.Font.Bold = true;
            ws.Range(r, 1, r, 8).Style.Fill.BackgroundColor = TotalFill;
            subtotalCells[kind] = $"G{r}";
            r += 2;
        }
        var t = VariationMath.Totals(lines);
        void Sum(string label, string formula, double check)
        {
            ws.Cell(r, 6).Value = label;
            ws.Cell(r, 7).FormulaA1 = formula;
            ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(r, 6, r, 7).Style.Font.Bold = true;
            ws.Cell(r, 9).Value = check; // cached value for readers that do not calculate (hidden column)
            r++;
        }
        Sum("TOTAL ADDITIONS (B + C)", $"{subtotalCells[VariationLineKinds.Addition]}+{subtotalCells[VariationLineKinds.NewItem]}", t.AddTotal);
        Sum("TOTAL OMISSIONS (A)", subtotalCells[VariationLineKinds.Omission], t.OmitTotal);
        Sum("NET VARIATION", $"{subtotalCells[VariationLineKinds.Addition]}+{subtotalCells[VariationLineKinds.NewItem]}+{subtotalCells[VariationLineKinds.Omission]}", t.Net);
        ws.Range(r - 1, 6, r - 1, 7).Style.Fill.BackgroundColor = TotalFill;
        ws.Column(9).Hide();
        ws.Range(headerRow + 1, 5, r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Column(1).Width = 8; ws.Column(2).Width = 22; ws.Column(3).Width = 60; ws.Column(4).Width = 8;
        ws.Column(5).Width = 12; ws.Column(6).Width = 14; ws.Column(7).Width = 16; ws.Column(8).Width = 28;
        ws.SheetView.FreezeRows(headerRow);
        r += 1;
        ws.Cell(r, 1).Value = $"Prepared by: {info.PreparedBy}"; ws.Cell(r, 5).Value = "Checked by:"; r += 2;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);

        // new item rate build-up
        var nb = wb.Worksheets.Add("RATE BUILD-UP");
        var bh = new[] { "ITEM", "DESCRIPTION", "UNIT", "MATERIAL", "LABOUR", "EQUIPMENT", "DIRECT COST", "OVERHEAD %", "OVERHEAD", "PROFIT %", "PROFIT", "RATE" };
        for (var c = 0; c < bh.Length; c++)
        {
            var cell = nb.Cell(1, c + 1);
            cell.Value = bh[c];
            cell.Style.Fill.BackgroundColor = Grey; cell.Style.Font.Bold = true; cell.Style.Font.FontColor = XLColor.Black;
        }
        var br = 2; var k = 0;
        foreach (var l in lines.Where(l => l.Kind == VariationLineKinds.NewItem))
        {
            k++;
            nb.Cell(br, 1).Value = $"N{k}";
            nb.Cell(br, 2).Value = l.Description;
            nb.Cell(br, 3).Value = l.Unit;
            nb.Cell(br, 4).Value = l.Material; nb.Cell(br, 5).Value = l.Labour; nb.Cell(br, 6).Value = l.Equipment;
            nb.Cell(br, 7).FormulaA1 = $"D{br}+E{br}+F{br}";
            nb.Cell(br, 8).Value = l.OverheadPct;
            nb.Cell(br, 9).FormulaA1 = $"G{br}*H{br}";
            nb.Cell(br, 10).Value = l.ProfitPct;
            nb.Cell(br, 11).FormulaA1 = $"(G{br}+I{br})*J{br}";
            nb.Cell(br, 12).FormulaA1 = $"ROUND(G{br}+I{br}+K{br},2)";
            br++;
        }
        if (k == 0) nb.Cell(2, 2).Value = "(no new items)";
        nb.Range(2, 4, Math.Max(2, br), 12).Style.NumberFormat.Format = "#,##0.00";
        nb.Range(2, 8, Math.Max(2, br), 8).Style.NumberFormat.Format = "0.0%";
        nb.Range(2, 10, Math.Max(2, br), 10).Style.NumberFormat.Format = "0.0%";
        nb.Column(2).Width = 60; foreach (var c in new[] { 1, 3 }) nb.Column(c).Width = 8; for (var c = 4; c <= 12; c++) nb.Column(c).Width = 13;

        // documents
        var ds = wb.Worksheets.Add("DOCUMENTS");
        var dh = new[] { "FILE", "PAGES", "TEXT", "SHA-256", "ADDED" };
        for (var c = 0; c < dh.Length; c++)
        {
            var cell = ds.Cell(1, c + 1);
            cell.Value = dh[c]; cell.Style.Fill.BackgroundColor = Grey; cell.Style.Font.Bold = true; cell.Style.Font.FontColor = XLColor.Black;
        }
        var dr = 2;
        foreach (var d in docs)
        {
            ds.Cell(dr, 1).Value = d.FileName; ds.Cell(dr, 2).Value = d.Pages; ds.Cell(dr, 3).Value = d.TextStatus; ds.Cell(dr, 4).Value = d.Sha256;
            ds.Cell(dr, 5).Value = d.AddedAt; ds.Cell(dr, 5).Style.NumberFormat.Format = "dd-mmm-yyyy";
            dr++;
        }
        ds.Column(1).Width = 50; ds.Column(4).Width = 66; ds.Column(5).Width = 14;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    // ------------------------------------------------------------------ submission PDF

    public static void SubmissionPdf(string path, Variation v, IReadOnlyList<VariationLine> lines, VariationHeaderInfo info)
    {
        var t = VariationMath.Totals(lines);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontSize(8));
            page.Header().Column(col =>
            {
                col.Item().Row(rw =>
                {
                    rw.RelativeItem().Text($"{TypeName(v.Type)} SUBMISSION - {v.Number}").FontSize(14).Bold().FontColor("#8E1B22");
                    rw.ConstantItem(200).AlignRight().Text($"{v.Status}  |  {v.Date:dd-MMM-yyyy}").Bold();
                });
                col.Item().PaddingTop(4).Table(tb =>
                {
                    tb.ColumnsDefinition(c => { c.ConstantColumn(90); c.RelativeColumn(); c.ConstantColumn(90); c.RelativeColumn(); });
                    void Cell(string l, string val) { tb.Cell().Text(l).SemiBold(); tb.Cell().Text(val); }
                    Cell("Project", info.Project); Cell("Contractor", info.Contractor);
                    Cell("Consultant ref", v.ConsultantRef); Cell("Aconex workflow", v.AconexWorkflowNo);
                    Cell("Title", v.Title); Cell("Building", v.Building);
                });
                if (!string.IsNullOrWhiteSpace(v.Description)) col.Item().PaddingTop(4).Text(v.Description).Italic();
            });
            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(c => { c.ConstantColumn(30); c.ConstantColumn(110); c.RelativeColumn(); c.ConstantColumn(36); c.ConstantColumn(60); c.ConstantColumn(70); c.ConstantColumn(80); });
                    tb.Header(h =>
                    {
                        foreach (var s in new[] { "ITEM", "BOQ / ITEM CODE", "DESCRIPTION", "UNIT", "QTY", "RATE", "AMOUNT" })
                            h.Cell().Background("#A6A6A6").Padding(2).Text(s).Bold().FontColor(Colors.Black);
                    });
                    foreach (var (kind, title) in Sections)
                    {
                        tb.Cell().ColumnSpan(7).Background("#EDEDED").Padding(2).Text(title).Bold();
                        var n = 0;
                        foreach (var l in lines.Where(l => l.Kind == kind))
                        {
                            n++;
                            void T(string s) => tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(s);
                            void N(double x) => tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight().Text(x.ToString("N2"));
                            T($"{kind[0]}{n}"); T(l.ItemCode); T(l.Description); T(l.Unit); N(VariationMath.SignedQty(l)); N(VariationMath.RateOf(l)); N(VariationMath.Amount(l));
                        }
                        var sub = lines.Where(l => l.Kind == kind).Sum(VariationMath.Amount);
                        tb.Cell().ColumnSpan(6).Background("#F4E3E3").Padding(2).AlignRight().Text("SUBTOTAL").Bold();
                        tb.Cell().Background("#F4E3E3").Padding(2).AlignRight().Text(sub.ToString("N2")).Bold();
                    }
                });
                col.Item().PaddingTop(10).AlignRight().Width(300).Table(tb =>
                {
                    tb.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); });
                    void L(string label, double val, bool bold = false)
                    {
                        var a = tb.Cell().Padding(2).Text(label); var b = tb.Cell().Padding(2).AlignRight().Text($"{val:N2} {info.Currency}");
                        if (bold) { a.Bold(); b.Bold(); }
                    }
                    L("Total additions (B + C)", t.AddTotal);
                    L("Total omissions (A)", t.OmitTotal);
                    L("NET VARIATION", t.Net, true);
                });
                var newItems = lines.Where(l => l.Kind == VariationLineKinds.NewItem).ToList();
                if (newItems.Count > 0)
                {
                    col.Item().PaddingTop(12).Text("NEW ITEM RATE BUILD-UP").Bold().FontColor("#8E1B22");
                    col.Item().Table(tb =>
                    {
                        tb.ColumnsDefinition(c => { c.RelativeColumn(); for (var i = 0; i < 7; i++) c.ConstantColumn(62); });
                        tb.Header(h => { foreach (var s in new[] { "DESCRIPTION", "MATERIAL", "LABOUR", "EQUIPMENT", "DIRECT", "OH %", "PROFIT %", "RATE" }) h.Cell().Background("#A6A6A6").Padding(2).Text(s).Bold(); });
                        foreach (var l in newItems)
                        {
                            tb.Cell().Padding(2).Text(l.Description);
                            foreach (var s in new[] { l.Material.ToString("N2"), l.Labour.ToString("N2"), l.Equipment.ToString("N2"), (l.Material + l.Labour + l.Equipment).ToString("N2"), l.OverheadPct.ToString("P1"), l.ProfitPct.ToString("P1"), VariationMath.RateOf(l).ToString("N2") })
                                tb.Cell().Padding(2).AlignRight().Text(s);
                        }
                    });
                }
            });
            page.Footer().Row(rw =>
            {
                rw.RelativeItem().Text($"Prepared by: {info.PreparedBy}        Checked by: ____________        Approved by: ____________");
                rw.ConstantItem(60).AlignRight().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
            });
        })).GeneratePdf(path);
    }

    // ------------------------------------------------------------------ register

    public sealed record RegisterRow(Variation V, VariationTotals Totals, int AgeDays, int Docs);

    public static List<RegisterRow> RegisterRows(IEnumerable<Variation> vars, IEnumerable<VariationLine> lines, IEnumerable<VariationDoc>? docs, DateTime today)
    {
        var byVar = lines.GroupBy(l => l.VariationId).ToDictionary(g => g.Key, g => g.ToList());
        var docCount = (docs ?? Array.Empty<VariationDoc>()).GroupBy(d => d.VariationId).ToDictionary(g => g.Key, g => g.Count());
        return vars.Select(v => new RegisterRow(v, VariationMath.Totals(byVar.GetValueOrDefault(v.Id) ?? new()), VariationMath.AgeDays(v, today), docCount.GetValueOrDefault(v.Id)))
            .OrderBy(r => r.V.Type).ThenBy(r => r.V.Number).ToList();
    }

    public static ExportSheet RegisterSheet(IReadOnlyList<RegisterRow> rows, DateTime today) => new()
    {
        Name = "VARIATION REGISTER", Title = "VARIATIONS / EI REGISTER",
        Subtitle = $"As of {today:dd-MMM-yyyy}  |  {rows.Count} entries  |  open {rows.Count(r => !VariationStatus.IsClosed(r.V.Status))}",
        Columns = new()
        {
            new("NO"), new("TYPE"), new("DATE", ColumnKind.Date), new("CONSULTANT REF", Width: 18), new("TITLE", Width: 44), new("STATUS"), new("ACONEX WF"),
            new("ADDITIONS", ColumnKind.Money), new("OMISSIONS", ColumnKind.Money), new("NET", ColumnKind.Money), new("APPROVED", ColumnKind.Money),
            new("SUBMITTED", ColumnKind.Date), new("DECIDED", ColumnKind.Date), new("AGE (DAYS)", ColumnKind.Integer), new("AGE BUCKET"), new("DOCS", ColumnKind.Integer),
        },
        Rows = rows.Select(r => new object?[]
        {
            r.V.Number, r.V.Type, r.V.Date, r.V.ConsultantRef, r.V.Title, r.V.Status, r.V.AconexWorkflowNo, r.Totals.AddTotal, r.Totals.OmitTotal, r.Totals.Net,
            r.V.ApprovedAmount, r.V.SubmittedAt, r.V.DecidedAt, r.AgeDays, VariationStatus.IsClosed(r.V.Status) ? "CLOSED" : VariationMath.AgeBucket(r.AgeDays), r.Docs,
        }).ToList(),
        TotalRow = new object?[]
        {
            "TOTAL", "", null, "", "", "", "", rows.Sum(r => r.Totals.AddTotal), rows.Sum(r => r.Totals.OmitTotal), rows.Sum(r => r.Totals.Net),
            rows.Sum(r => r.V.ApprovedAmount ?? 0), null, null, null, "", rows.Sum(r => r.Docs),
        },
    };

    public static ExportSheet AgeingSheet(IReadOnlyList<AgeingRow> rows) => new()
    {
        Name = "AGEING", Title = "OPEN VARIATIONS - AGEING",
        Columns = new() { new("AGE"), new("COUNT", ColumnKind.Integer), new("NET VALUE", ColumnKind.Money) },
        Rows = rows.Select(a => new object?[] { a.Bucket, a.Count, a.Net }).ToList(),
        TotalRow = new object?[] { "TOTAL", rows.Sum(a => a.Count), rows.Sum(a => a.Net) },
    };

    public static string TypeName(string type) => type switch
    {
        VariationTypes.Ei => "ENGINEER'S INSTRUCTION",
        VariationTypes.Si => "SITE INSTRUCTION",
        _ => "VARIATION ORDER",
    };

    private static string SafeSheet(string s)
    {
        var n = new string((s.Length == 0 ? "VARIATION" : s).Select(ch => ":\\/?*[]".Contains(ch) ? '-' : ch).ToArray());
        return n.Length > 31 ? n[..31] : n;
    }
}
