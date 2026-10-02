using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Export;

namespace Raffaello.Core.AconexWeb;

/// <summary>Prints one or more <see cref="ExportSheet"/>s as landscape PDF tables in the house style (grey #A6A6A6 header, bold black).</summary>
public static class PdfTables
{
    private const string Red = "#8E1B22";
    private const string Grey = "#A6A6A6";
    private const string TotalFill = "#F4E3E3";

    static PdfTables() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Optional image (e.g. a workflow screenshot) printed under a sheet.</summary>
    public sealed record PdfImage(string Caption, string Path);

    public static void Export(string path, string footer, IEnumerable<ExportSheet> sheets, IEnumerable<PdfImage>? images = null, bool a3 = false)
    {
        var list = sheets.ToList();
        var imgs = (images ?? Array.Empty<PdfImage>()).Where(i => File.Exists(i.Path)).ToList();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        Document.Create(doc =>
        {
            foreach (var s in list)
            {
                doc.Page(page =>
                {
                    page.Size(a3 ? PageSizes.A3.Landscape() : PageSizes.A4.Landscape());
                    page.Margin(20);
                    page.DefaultTextStyle(x => x.FontSize(7.5f));
                    page.Header().Column(col =>
                    {
                        col.Item().Text((s.Title ?? s.Name).ToUpperInvariant()).FontSize(14).Bold().FontColor(Red);
                        if (!string.IsNullOrEmpty(s.Subtitle)) col.Item().Text(s.Subtitle).Italic().FontColor(Colors.Grey.Darken2);
                    });
                    page.Content().PaddingTop(6).Table(tb =>
                    {
                        tb.ColumnsDefinition(c =>
                        {
                            foreach (var col in s.Columns)
                                if (col.Width > 0) c.RelativeColumn((float)Math.Max(1, col.Width / 12)); else c.RelativeColumn(col.Kind == ColumnKind.Text ? 1.4f : 1f);
                        });
                        tb.Header(h =>
                        {
                            foreach (var col in s.Columns) h.Cell().Background(Grey).Padding(2).Text(col.Header.ToUpperInvariant()).Bold().FontColor(Colors.Black);
                        });
                        foreach (var row in s.Rows) Row(tb, s.Columns, row, false);
                        if (s.TotalRow != null) Row(tb, s.Columns, s.TotalRow, true);
                    });
                    page.Footer().Row(r =>
                    {
                        r.RelativeItem().Text(footer).FontColor(Colors.Grey.Darken1);
                        r.ConstantItem(80).AlignRight().Text(t => { t.CurrentPageNumber(); t.Span(" / "); t.TotalPages(); });
                    });
                });
            }
            foreach (var img in imgs)
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(20);
                    page.Header().Text(img.Caption).FontSize(12).Bold().FontColor(Red);
                    page.Content().PaddingTop(6).Image(img.Path).FitArea();
                });
            }
        }).GeneratePdf(path);
    }

    private static void Row(TableDescriptor tb, List<ExportColumn> cols, object?[] row, bool total)
    {
        for (var i = 0; i < cols.Count; i++)
        {
            var v = i < row.Length ? row[i] : null;
            var cell = tb.Cell().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2);
            if (total) cell = cell.Background(TotalFill);
            var text = Format(v, cols[i].Kind);
            var c = cols[i].Kind == ColumnKind.Text ? cell.Padding(2).Text(text) : cell.Padding(2).AlignRight().Text(text);
            if (total) c.Bold();
        }
    }

    public static string Format(object? v, ColumnKind kind) => v switch
    {
        null => "",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "dd-MMM-yyyy" : "dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture),
        double x when double.IsNaN(x) || double.IsInfinity(x) => "",
        double x => kind switch
        {
            ColumnKind.Percent => x.ToString("P1", CultureInfo.InvariantCulture),
            ColumnKind.Integer => x.ToString("N0", CultureInfo.InvariantCulture),
            _ => x.ToString("N2", CultureInfo.InvariantCulture),
        },
        int n => n.ToString("N0", CultureInfo.InvariantCulture),
        long n => n.ToString("N0", CultureInfo.InvariantCulture),
        bool b => b ? "Y" : "",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
