using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Export;

namespace Raffaello.Core.Assemblies;

/// <summary>Header information printed on the rate analysis sheets.</summary>
public sealed record RateSheetInfo(string Project, string PreparedBy, DateTime Date, string Currency = "SAR");

/// <summary>
/// [assemblies] Exports: breakdown / bulk requirements / template workbooks in the house style (#A6A6A6 headers, bold black text,
/// via <see cref="ExcelExporter"/>) and a PDF "Rate analysis" sheet per item.
/// </summary>
public static class AssemblyExporter
{
    static AssemblyExporter() => QuestPDF.Settings.License = LicenseType.Community;

    public static ExportSheet BreakdownSheet(AssemblySource src, Breakdown b, string? name = null) => new()
    {
        Name = name ?? "RATE ANALYSIS",
        Title = $"RATE ANALYSIS - {src.KindLabel} {src.Code}".Trim(),
        Subtitle = $"{Short(src.Description, 180)} | {b.Spec.Summary} | template {b.TemplateCode} | labour {b.LabourMode}",
        Columns = new()
        {
            new("STAGE"), new("COMPONENT", Width: 26), new("SPECIFICATION", Width: 44), new("UNIT"), new("QTY / UNIT", ColumnKind.Number), new("WASTE", ColumnKind.Percent),
            new("TOTAL QTY", ColumnKind.Number), new("UNIT PRICE", ColumnKind.Money), new("AMOUNT", ColumnKind.Money), new("KIND"), new("PRICE SOURCE"), new("PRICE REF", Width: 36),
            new("PRICE DATE"), new("FLAG"), new("IN RATE"),
        },
        Rows = b.Lines.Select(l => new object?[]
        {
            l.Stage, l.Component, l.Spec, l.Unit, l.QtyPerUnit, l.WastePct, l.TotalQty, l.UnitPrice, l.Amount, l.Kind, l.PriceSource, l.PriceRef, l.PriceDateText, l.Flag, l.Included ? "YES" : "NO",
        }).Concat(Summary(b)).ToList(),
    };

    private static IEnumerable<object?[]> Summary(Breakdown b)
    {
        object?[] R(string label, double? v, string note = "") => new object?[] { "", label, note, "", null, null, null, null, v, "", "", "", "", "", "" };
        yield return R("", null);
        yield return R("MATERIAL per unit", b.Material, b.FreeIssueMaterial > 0 ? $"free issue (not in rate): {b.FreeIssueMaterial:N2}" : "");
        yield return R("LABOUR per unit", b.Labour, b.LabourMode);
        if (b.Equipment > 0) yield return R("EQUIPMENT per unit", b.Equipment);
        yield return R("DIRECT COST", b.Direct);
        yield return R($"OVERHEAD {b.OverheadPct:P1}", b.Overhead);
        yield return R($"PROFIT {b.ProfitPct:P1}", b.Profit);
        yield return R($"BUILT-UP RATE per {b.Unit}", b.Rate);
        if (b.ReferenceRate is { } r)
        {
            yield return R($"{b.ReferenceLabel} RATE", r);
            yield return R("MARGIN (reference - built-up)", b.Margin, b.Verdict);
        }
        if (b.RouteLengthSource.Length > 0) yield return R("ROUTE", null, b.RouteLengthSource);
        foreach (var f in b.Flags) yield return R("NOTE", null, f);
        foreach (var n in b.Spec.Notes) yield return R("PARSE", null, n);
    }

    public static ExportSheet BulkItemsSheet(BulkResult r) => new()
    {
        Name = "ITEMS",
        Title = "BULK BREAKDOWN - ITEMS",
        Subtitle = r.Summary,
        Columns = new()
        {
            new("SOURCE"), new("CODE"), new("DESCRIPTION", Width: 60), new("UNIT"), new("QTY", ColumnKind.Number), new("ITEM TYPE"), new("TEMPLATE"), new("MATERIAL", ColumnKind.Money),
            new("LABOUR", ColumnKind.Money), new("BUILT-UP RATE", ColumnKind.Money), new("REFERENCE RATE", ColumnKind.Money), new("MARGIN", ColumnKind.Money), new("AMOUNT", ColumnKind.Money),
            new("INSTALLED", ColumnKind.Number), new("VERDICT"), new("FLAGS", Width: 50),
        },
        Rows = r.Rows.Select(x => new object?[]
        {
            x.Source.KindLabel, x.Source.Code, x.Source.Description, x.Source.Unit, x.Qty, x.ItemType, x.Breakdown.TemplateCode, x.Breakdown.Material, x.Breakdown.Labour, x.Rate,
            x.Breakdown.ReferenceRate, x.Breakdown.Margin, x.Amount, x.InstalledQty, x.Verdict, string.Join("; ", x.Breakdown.Flags),
        }).ToList(),
        TotalRow = new object?[] { "TOTAL", "", "", "", null, "", "", null, null, null, null, null, r.Amount, null, "", "" },
    };

    public static ExportSheet RequirementsSheet(BulkResult r) => new()
    {
        Name = "MATERIAL REQUIREMENTS",
        Title = "MATERIAL REQUIREMENTS vs PO / DELIVERIES / INSTALLED",
        Subtitle = r.Summary,
        Columns = new()
        {
            new("SPECIFICATION", Width: 50), new("UNIT"), new("KIND"), new("REQUIRED", ColumnKind.Number), new("ORDERED (PO)", ColumnKind.Number), new("PO BALANCE", ColumnKind.Number),
            new("DELIVERED (DN)", ColumnKind.Number), new("TO DELIVER", ColumnKind.Number), new("INSTALLED (THEORETICAL)", ColumnKind.Number), new("SITE BALANCE", ColumnKind.Number),
            new("STATUS"), new("UNIT PRICE", ColumnKind.Money), new("COST", ColumnKind.Money), new("PRICE SOURCE"), new("FREE ISSUE"), new("ITEMS", Width: 40), new("PO LINES", Width: 30),
        },
        Rows = r.Materials.Select(m => new object?[]
        {
            m.Spec, m.Unit, m.Kind, m.RequiredQty, m.OrderedQty, m.PoBalance, m.DeliveredQty, m.ToDeliver, m.InstalledQty, m.SiteBalance, m.Status, m.UnitPrice, m.Cost, m.PriceSource,
            m.FreeIssue ? "YES" : "", m.ItemsText, m.PoRefsText,
        }).ToList(),
        TotalRow = new object?[] { "TOTAL", "", "", null, null, null, null, null, null, null, "", null, Math.Round(r.Materials.Sum(m => m.Cost), 2), "", "", "", "" },
    };

    public static IEnumerable<ExportSheet> TemplateSheets(AssemblyLibrary lib)
    {
        yield return new ExportSheet
        {
            Name = "TEMPLATES",
            Title = "ASSEMBLY TEMPLATES (defaults to be confirmed)",
            Columns = new() { new("CODE"), new("NAME", Width: 30), new("ITEM TYPE"), new("UNIT"), new("ORIGIN"), new("CONFIRMED"), new("DESCRIPTION", Width: 70) },
            Rows = lib.Templates.Select(t => new object?[] { t.Code, t.Header.Name, t.ItemType, t.Header.Unit, t.Header.Origin, t.Header.Confirmed ? "YES" : "NO", t.Header.Description }).ToList(),
        };
        yield return new ExportSheet
        {
            Name = "PARAMETERS",
            Title = "PARAMETERS (global + per template)",
            Columns = new() { new("TEMPLATE"), new("NAME"), new("VALUE", ColumnKind.Number), new("UNIT"), new("LABEL", Width: 34), new("NOTE", Width: 50), new("CONFIRMED") },
            Rows = lib.Globals.Select(p => new object?[] { "(GLOBAL)", p.Name, p.Value, p.Unit, p.Label, p.Note, p.Confirmed ? "YES" : "NO" })
                .Concat(lib.Templates.SelectMany(t => t.Params.Select(p => new object?[] { t.Code, p.Name, p.Value, p.Unit, p.Label, p.Note, p.Confirmed ? "YES" : "NO" }))).ToList(),
        };
        yield return new ExportSheet
        {
            Name = "COMPONENTS",
            Title = "COMPONENTS AND QUANTITY FORMULAS",
            Columns = new() { new("TEMPLATE"), new("#", ColumnKind.Integer), new("KEY"), new("COMPONENT", Width: 28), new("SPECIFICATION", Width: 40), new("UNIT"), new("QTY FORMULA", Width: 50), new("WASTE", ColumnKind.Percent), new("STAGE"), new("KIND"), new("LABOUR BASIS"), new("LABOUR CATEGORY"), new("DEFAULT PRICE", Width: 40), new("NOTES", Width: 30) },
            Rows = lib.Templates.SelectMany(t => t.Components.OrderBy(c => c.Order).Select(c => new object?[] { t.Code, c.Order, c.Key, c.Name, c.Spec, c.Unit, c.QtyFormula, c.WastePct, c.Stage, c.Kind, c.LabourBasis, c.LabourCategory, c.DefaultPrice, c.Notes })).ToList(),
        };
    }

    public static void BreakdownExcel(string path, AssemblySource src, Breakdown b) => ExcelExporter.Export(path, BreakdownSheet(src, b));

    public static void BulkExcel(string path, BulkResult r) => ExcelExporter.Export(path, BulkItemsSheet(r), RequirementsSheet(r));

    // ------------------------------------------------------------------ PDF rate analysis

    /// <summary>One "Rate analysis" page per item.</summary>
    public static void RateAnalysisPdf(string path, IReadOnlyList<(AssemblySource Source, Breakdown Breakdown)> items, RateSheetInfo info)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Document.Create(doc =>
        {
            foreach (var (src, b) in items)
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(22);
                    page.DefaultTextStyle(x => x.FontSize(7.5f));
                    page.Header().Column(col =>
                    {
                        col.Item().Row(rw =>
                        {
                            rw.RelativeItem().Text("RATE ANALYSIS").FontSize(14).Bold().FontColor("#8B0000");
                            rw.ConstantItem(260).AlignRight().Text($"{info.Project}  |  {info.Date:dd-MMM-yyyy}").Bold();
                        });
                        col.Item().PaddingTop(3).Table(tb =>
                        {
                            tb.ColumnsDefinition(c => { c.ConstantColumn(80); c.RelativeColumn(); c.ConstantColumn(80); c.ConstantColumn(170); });
                            void Cell(string l, string v) { tb.Cell().Text(l).SemiBold(); tb.Cell().Text(v); }
                            Cell("Item", $"{src.KindLabel} {src.Code}"); Cell("Unit", b.Unit);
                            Cell("Description", Short(src.Description, 400)); Cell("Template", $"{b.TemplateCode} ({b.LabourMode})");
                            Cell("Parsed spec", b.Spec.Summary); Cell("Qty", src.Qty > 0 ? src.Qty.ToString("N2") : "-");
                        });
                    });
                    page.Content().PaddingTop(6).Column(col =>
                    {
                        col.Item().Table(tb =>
                        {
                            tb.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(44); c.ConstantColumn(100); c.RelativeColumn(); c.ConstantColumn(30); c.ConstantColumn(46); c.ConstantColumn(34); c.ConstantColumn(46);
                                c.ConstantColumn(50); c.ConstantColumn(54); c.ConstantColumn(110);
                            });
                            tb.Header(h =>
                            {
                                foreach (var s in new[] { "STAGE", "COMPONENT", "SPECIFICATION", "UNIT", "QTY/UNIT", "WASTE", "TOTAL", "PRICE", "AMOUNT", "PRICE SOURCE" })
                                    h.Cell().Background("#A6A6A6").Padding(2).Text(s).Bold().FontColor(Colors.Black);
                            });
                            foreach (var l in b.Lines)
                            {
                                var grey = !l.Included;
                                void T(string s) { var t = tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(s); if (grey) t.FontColor(Colors.Grey.Medium); }
                                void N(double x, string f = "N2") { var t = tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight().Text(x.ToString(f)); if (grey) t.FontColor(Colors.Grey.Medium); }
                                T(l.Stage.Replace(" FIX", "")); T(l.Component); T(l.Spec); T(l.Unit); N(l.QtyPerUnit, "N3"); N(l.WastePct, "P0"); N(l.TotalQty, "N3"); N(l.UnitPrice); N(l.Amount);
                                T(l.Flag.Length > 0 ? $"{l.PriceSource} - {l.Flag}" : $"{l.PriceSource} {l.PriceRef}".Trim());
                            }
                        });
                        col.Item().PaddingTop(8).Row(rw =>
                        {
                            rw.RelativeItem().Column(notes =>
                            {
                                if (b.RouteLengthSource.Length > 0) notes.Item().Text("• " + b.RouteLengthSource);
                                foreach (var f in b.Flags) notes.Item().Text("• " + f).FontColor("#8B0000");
                                foreach (var n in b.Spec.Notes) notes.Item().Text("• " + n).Italic();
                                if (b.FreeIssueMaterial > 0) notes.Item().Text($"• Free-issue material (not in the rate): {b.FreeIssueMaterial:N2} {info.Currency} per {b.Unit}");
                                notes.Item().PaddingTop(4).Text("Quantities and indicative prices are template defaults to be confirmed; PO prices are taken from the Materials module.").FontSize(6.5f).FontColor(Colors.Grey.Darken1);
                            });
                            rw.ConstantItem(250).Table(tb =>
                            {
                                tb.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); });
                                void L(string label, double? v, bool bold = false, bool fill = false)
                                {
                                    var bg = fill ? "#A6A6A6" : "#FFFFFF";
                                    var t1 = tb.Cell().Background(bg).Padding(2).Text(label);
                                    var t2 = tb.Cell().Background(bg).Padding(2).AlignRight().Text(v is { } x ? $"{x:N2} {info.Currency}" : "-");
                                    if (bold) { t1.Bold(); t2.Bold(); }
                                }
                                L("Material per unit", b.Material);
                                L("Labour per unit", b.Labour);
                                if (b.Equipment > 0) L("Equipment per unit", b.Equipment);
                                L("Direct cost", b.Direct, true);
                                L($"Overhead {b.OverheadPct:P1}", b.Overhead);
                                L($"Profit {b.ProfitPct:P1}", b.Profit);
                                L($"BUILT-UP RATE / {b.Unit}", b.Rate, true, true);
                                if (b.ReferenceRate is { } r)
                                {
                                    L($"{b.ReferenceLabel} rate", r);
                                    L($"Margin ({b.Verdict})", b.Margin, true);
                                }
                            });
                        });
                    });
                    page.Footer().Row(rw =>
                    {
                        rw.RelativeItem().Text($"Prepared by: {info.PreparedBy}        Checked by: ____________        Approved by: ____________");
                        rw.ConstantItem(60).AlignRight().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                    });
                });
        }).GeneratePdf(path);
    }

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "..." : s;
}
