using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Invoicing;

/// <summary>
/// Printable invoice without Excel: only rows with a current or cumulative quantity (plus the section rows above them),
/// the header block, column headers repeated on every page, and the summary + signature footer. Landscape A3 by default.
/// </summary>
public static class InvoicePdfExporter
{
    private static readonly string Red = "#8E1B22";
    private static readonly string Grey = "#A6A6A6";

    static InvoicePdfExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>Rows printed: items with CURR or CUM != 0, and the section / note rows that give them context.</summary>
    public static List<SubInvoiceLine> FilteredRows(IEnumerable<SubInvoiceLine> lines)
    {
        var ordered = lines.OrderBy(l => l.RowOrder).ToList();
        var keep = new List<SubInvoiceLine>();
        SubInvoiceLine? pendingSection = null;
        foreach (var l in ordered)
        {
            if (l.Kind == "SECTION") { pendingSection = l; continue; }
            if (l.Kind != "ITEM") continue;
            if (Math.Abs(l.CurrQty) < 1e-9 && Math.Abs(l.CumQty) < 1e-9) continue;
            if (pendingSection != null) { keep.Add(pendingSection); pendingSection = null; }
            keep.Add(l);
        }
        return keep;
    }

    /// <param name="stamp">Fixed print date + PDF metadata dates: makes the file reproducible (package builds).</param>
    public static void Export(string path, InvoiceBuild build, InvoiceHeaderInfo? info = null, bool a3 = true, DateTime? stamp = null)
    {
        var printed = stamp ?? DateTime.Now;
        info ??= new InvoiceHeaderInfo();
        var h = build.Header;
        var t = build.Totals;
        var rows = FilteredRows(build.Lines);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(a3 ? PageSizes.A3.Landscape() : PageSizes.A4.Landscape());
                page.Margin(18);
                page.DefaultTextStyle(x => x.FontSize(a3 ? 8 : 6.5f));
                page.Header().Column(col =>
                {
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Text("Quantity Surveyor (Q.S) Summary Sheet").FontSize(14).Bold().FontColor(Red);
                        r.ConstantItem(220).AlignRight().Text($"{h.Subcontractor}  INV-{h.InvoiceNo:00}  Rev {h.Revision}  |  {h.Status}").Bold();
                    });
                    col.Item().PaddingTop(4).Table(tb =>
                    {
                        tb.ColumnsDefinition(c => { for (var i = 0; i < 6; i++) { c.ConstantColumn(90); c.RelativeColumn(); } });
                        void Cell(string label, string value) { tb.Cell().Text(label).SemiBold(); tb.Cell().Text(value); }
                        Cell("Project Name", info.ProjectName); Cell("Vendor Name", h.Subcontractor); Cell("Subcontract No", h.ContractNo);
                        Cell("Project Code", info.ProjectCode); Cell("Vendor No", info.VendorNo); Cell("Contract Value", t.SubcontractValue.ToString("N2"));
                        Cell("Location", info.Location); Cell("Category", info.Category.Trim()); Cell("Retention %", h.RetentionPct.ToString("P0"));
                        Cell("Project Director", info.ProjectDirector); Cell("Scope Of Work", info.ScopeOfWork); Cell("Advanced %", h.AdvancePct.ToString("P0"));
                        Cell("Inv. Date", h.CreatedAt.ToString("dd-MMM-yyyy")); Cell("PC No", h.InvoiceNo.ToString("00")); Cell("Aconex WF", h.AconexWorkflowNo);
                    });
                    col.Item().PaddingTop(2).Text($"Showing {rows.Count(r => r.Kind == "ITEM")} of {build.Lines.Count(l => l.Kind == "ITEM")} rows - only rows with current or cumulative quantity are printed.").Italic().FontColor(Colors.Grey.Darken1);
                });
                page.Content().PaddingTop(6).Column(col =>
                {
                    col.Item().Table(tb =>
                    {
                        tb.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(28); c.ConstantColumn(110); c.RelativeColumn(4); c.ConstantColumn(28); c.ConstantColumn(52); c.ConstantColumn(44); c.ConstantColumn(30); c.ConstantColumn(64);
                            c.ConstantColumn(50); c.ConstantColumn(50); c.ConstantColumn(50); c.ConstantColumn(62); c.ConstantColumn(62); c.ConstantColumn(62);
                        });
                        tb.Header(hd =>
                        {
                            foreach (var s in new[] { "Item", "BOQ item No", "Contract Description / BOQ", "Unit", "QTY", "Rate", "%", "Amount", "Exec. Prev", "Exec. Curr", "Exec. Cum", "Amt Prev", "Amt Curr", "Amt Cum" })
                                hd.Cell().Background(Grey).Padding(2).Text(s).Bold().FontColor(Colors.Black);
                        });
                        foreach (var l in rows)
                        {
                            if (l.Kind == "SECTION")
                            {
                                tb.Cell().ColumnSpan(14).Background("#F4E3E3").Padding(2).Text(l.Description).Bold();
                                continue;
                            }
                            var desc = l.BoqDescription.Length > 0 ? $"{Short(l.Description, 110)}  [{l.BoqDescription}]" : Short(l.Description, 140);
                            void N(double v, string fmt = "N2", bool bold = false) { var x = tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).AlignRight().Text(Math.Abs(v) < 1e-9 ? "" : v.ToString(fmt)); if (bold) x.Bold(); }
                            void T(string s) => tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).Padding(2).Text(s);
                            T(l.ItemNo); T(l.BoqCode); T(desc); T(l.Unit);
                            N(l.ContractQty); N(l.Rate); N(l.StagePct, "P0"); N(l.Amount);
                            N(l.PrevQty); N(l.CurrQty, "N2", true); N(l.CumQty); N(l.PrevAmount); N(l.CurrAmount, "N2", true); N(l.CumAmount);
                        }
                    });
                    col.Item().PaddingTop(10).AlignRight().Width(420).Table(tb =>
                    {
                        tb.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                        foreach (var s in new[] { "", "Prev.", "Curr.", "Cum." }) tb.Cell().Background(Grey).Padding(2).Text(s).Bold();
                        void L(string label, double p, double c, double cum, bool bold = false)
                        {
                            var a = tb.Cell().Padding(2).Text(label); if (bold) a.Bold();
                            foreach (var v in new[] { p, c, cum }) { var x = tb.Cell().Padding(2).AlignRight().Text(v.ToString("N2")); if (bold) x.Bold(); }
                        }
                        L("Gross Certified Amount", t.PrevGross, t.CurrGross, t.CumGross);
                        L($"Retention {t.RetentionPct:P0}", t.PrevRetention, t.CurrRetention, t.CumRetention);
                        L("Recovered Adv. Payment", 0, t.RecoveredAdvance, t.RecoveredAdvance);
                        L("Discount", 0, t.Discount, t.Discount);
                        L("Net Payment", t.NetPrev, t.NetCurr, t.NetCum, true);
                        L("VAT @ 15%", t.NetPrev * InvoiceTotals.VatRate, t.VatCurr, t.VatCum);
                        L("Net Payment Incl. VAT", t.NetPrev * (1 + InvoiceTotals.VatRate), t.NetInclVatCurr, t.NetInclVatCum, true);
                    });
                    col.Item().PaddingTop(4).AlignRight().Text($"Subcontract value {t.SubcontractValue:N2}  |  executed to date {t.CumGross:N2} ({(t.SubcontractValue <= 0 ? 0 : t.CumGross / t.SubcontractValue):P1})");
                    col.Item().PaddingTop(30).Table(tb =>
                    {
                        var roles = info.SignatureRoles;
                        tb.ColumnsDefinition(c => { foreach (var _ in roles) c.RelativeColumn(); });
                        foreach (var role in roles) tb.Cell().BorderTop(0.6f).PaddingTop(2).AlignCenter().Text(role.Trim()).SemiBold();
                        for (var i = 0; i < roles.Length; i++) tb.Cell().AlignCenter().Text(i < info.SignatureNames.Length ? info.SignatureNames[i] : "");
                    });
                });
                page.Footer().Row(r =>
                {
                    r.RelativeItem().Text($"Raffaello  |  {h.Subcontractor} INV-{h.InvoiceNo:00} Rev {h.Revision}  |  printed {printed:dd-MMM-yyyy HH:mm}").FontColor(Colors.Grey.Darken1);
                    r.ConstantItem(80).AlignRight().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                });
            });
        }).WithMetadata(new DocumentMetadata { Title = $"{h.Title}", Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = printed, ModifiedDate = printed })
          .GeneratePdf(path);
    }

    private static string Short(string s, int n)
    {
        s = (s ?? "").Trim().Replace("\n", " ");
        return s.Length <= n ? s : s[..n] + "...";
    }
}
