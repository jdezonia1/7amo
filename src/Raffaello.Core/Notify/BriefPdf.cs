using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Localization;

namespace Raffaello.Core.Notify;

/// <summary>The morning brief as a one- or two-page A4 PDF in the house style (dark red title, #A6A6A6 section bars). Arabic is laid out right to left.</summary>
public static class BriefPdf
{
    /// <summary>Fonts with Arabic glyphs first on Windows (Segoe UI / Tahoma), DejaVu Sans elsewhere.</summary>
    public static readonly string[] Fonts = { "Segoe UI", "Tahoma", "DejaVu Sans", "Arial" };

    public static byte[] Render(Brief b)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var rtl = b.Language == Loc.Arabic;
        var c = Loc.Culture(b.Language);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(28);
            if (rtl) page.ContentFromRightToLeft();
            page.DefaultTextStyle(x => x.FontFamily(Fonts).FontSize(9.5f));
            page.Header().Background("#8B0000").Padding(10).Column(col =>
            {
                col.Item().Text(Loc.Get("Brief_Title", b.Language)).FontSize(16).Bold().FontColor(Colors.White);
                col.Item().Text($"{b.Date.ToString("dddd dd MMM yyyy", c)}  |  {b.Owner}  |  {Loc.Get("Brief_Since", b.Language)} {b.Since.ToString("dd MMM HH:mm", c)}").FontColor(Colors.White);
            });
            page.Content().PaddingTop(10).Column(col =>
            {
                if (b.Summary.Length > 0)
                    col.Item().Background("#FFFF00").Padding(6).Text(b.Summary.Trim());
                foreach (var s in b.Sections)
                {
                    col.Item().PaddingTop(8).Background("#A6A6A6").Padding(4).Row(r =>
                    {
                        r.RelativeItem().Text(s.Title(b.Language)).Bold().FontColor(Colors.Black);
                        r.AutoItem().Text(s.Count.ToString(c) + (s.Delta is int d && d != 0 ? (d > 0 ? "  (+" : "  (") + d.ToString(c) + ")" : "")).Bold();
                    });
                    if (s.Items.Count == 0)
                        col.Item().PaddingHorizontal(6).PaddingTop(2).Text(Loc.Get("Brief_Nothing", b.Language)).FontColor(Colors.Grey.Darken1);
                    foreach (var i in s.Items.Take(15))
                        col.Item().PaddingHorizontal(6).PaddingTop(2).Row(r =>
                        {
                            r.ConstantItem(44).Text(i.Severity).Bold().FontColor(i.Severity == "OVER" ? "#8B0000" : Colors.Black);
                            r.RelativeItem().Text(t =>
                            {
                                if (i.IsNew) t.Span("[" + Loc.Get("Brief_New", b.Language).ToUpper(c) + "] ").Bold().FontColor("#8B0000");
                                t.Span(i.Title).SemiBold();
                                if (i.Detail.Length > 0) t.Span("  " + i.Detail).FontColor(Colors.Grey.Darken2);
                            });
                        });
                    if (s.Items.Count > 15)
                        col.Item().PaddingHorizontal(6).Text($"... +{(s.Items.Count - 15).ToString(c)}").FontColor(Colors.Grey.Darken1);
                }
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Raffaello  |  {Loc.Get("Brief_Generated", b.Language)} {b.BuiltAt.ToString("dd MMM yyyy HH:mm", c)}").FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                r.AutoItem().Text(x => { x.CurrentPageNumber().FontSize(7.5f); x.Span(" / ").FontSize(7.5f); x.TotalPages().FontSize(7.5f); });
            });
        })).WithMetadata(new DocumentMetadata { Title = "Morning brief", Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = b.BuiltAt, ModifiedDate = b.BuiltAt })
          .GeneratePdf();
    }

    public static void Export(string path, Brief b) => File.WriteAllBytes(path, Render(b));
}
