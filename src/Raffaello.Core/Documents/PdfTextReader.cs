using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace Raffaello.Core.Documents;

/// <summary>
/// First pass of the reader pipeline: the PDF text layer via PdfPig, rebuilt into layout lines (words grouped by baseline,
/// columns kept as runs of spaces, like pdftotext -layout) so the parsers can split on column gaps.
/// Also exposes each page's dominant embedded image (scans / phone photos) and single-page PDF extraction for cloud reading.
/// </summary>
public static class PdfTextReader
{
    public static DocText Read(string path)
    {
        using var doc = PdfDocument.Open(path);
        var text = new DocText { FileName = Path.GetFileName(path) };
        foreach (var page in doc.GetPages())
            text.Pages.Add(ReadPage(page));
        return text;
    }

    public static DocText Read(byte[] pdf, string fileName = "document.pdf")
    {
        using var doc = PdfDocument.Open(pdf);
        var text = new DocText { FileName = fileName };
        foreach (var page in doc.GetPages())
            text.Pages.Add(ReadPage(page));
        return text;
    }

    private static DocPage ReadPage(Page page)
    {
        List<Word> words;
        try { words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList(); }
        catch { words = new List<Word>(); }
        PageImage? img = null;
        try { img = DominantImage(page); } catch { /* broken image streams: no image */ }
        return new DocPage
        {
            Number = page.Number,
            Text = Layout(words, page.Width),
            Source = words.Count > 0 ? TextSource.TextLayer : TextSource.None,
            HasTextLayer = words.Count > 0,
            WordCount = words.Count,
            DominantImage = img,
            WidthPt = page.Width,
            HeightPt = page.Height,
        };
    }

    /// <summary>Groups words into lines by baseline and pads columns with spaces proportional to their x position.</summary>
    internal static string Layout(IReadOnlyList<Word> words, double pageWidth)
    {
        if (words.Count == 0) return "";
        var charWidths = words.Where(w => w.Text.Length > 0).Select(w => w.BoundingBox.Width / w.Text.Length).Where(x => x > 0.5).OrderBy(x => x).ToList();
        var cw = charWidths.Count == 0 ? 5.0 : charWidths[charWidths.Count / 2];
        cw = Math.Max(2.5, cw);
        var heights = words.Select(w => w.BoundingBox.Height).Where(h => h > 0.5).OrderBy(h => h).ToList();
        var lh = heights.Count == 0 ? 8 : heights[heights.Count / 2];

        var lines = new List<List<Word>>();
        var lineY = new List<double>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            var y = w.BoundingBox.Bottom;
            var idx = -1;
            for (var i = lineY.Count - 1; i >= 0 && i >= lineY.Count - 3; i--)
                if (Math.Abs(lineY[i] - y) <= lh * 0.45) { idx = i; break; }
            if (idx < 0) { lines.Add(new List<Word> { w }); lineY.Add(y); }
            else lines[idx].Add(w);
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var row = new StringBuilder();
            Word? prev = null;
            foreach (var w in line.OrderBy(w => w.BoundingBox.Left))
            {
                var col = (int)Math.Round(w.BoundingBox.Left / cw);
                if (prev != null)
                {
                    var gap = w.BoundingBox.Left - prev.BoundingBox.Right;
                    var min = gap > cw * 1.8 ? 2 : gap > cw * 0.15 ? 1 : 0;
                    col = Math.Max(col, row.Length + min);
                    if (min >= 2) col = Math.Max(col, row.Length + 2);
                }
                if (col > row.Length) row.Append(' ', col - row.Length);
                row.Append(w.Text);
                prev = w;
            }
            sb.Append(row.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static PageImage? DominantImage(Page page)
    {
        var area = page.Width * page.Height;
        if (area <= 0) return null;
        IPdfImage? best = null; double bestCov = 0;
        foreach (var im in page.GetImages())
        {
            var b = im.BoundingBox;
            var cov = Math.Abs(b.Width * b.Height) / area;
            if (cov > bestCov) { bestCov = cov; best = im; }
        }
        if (best is null || bestCov < 0.35) return null;
        var filter = best.ImageDictionary.Data.TryGetValue("Filter", out var ft) ? ft.ToString() ?? "" : "";
        if (filter.Contains("DCTDecode", StringComparison.Ordinal))
            return new PageImage { Bytes = best.RawMemory.ToArray(), MediaType = "image/jpeg", Width = best.WidthInSamples, Height = best.HeightInSamples, Coverage = bestCov };
        if (best.TryGetPng(out var png))
            return new PageImage { Bytes = png, MediaType = "image/png", Width = best.WidthInSamples, Height = best.HeightInSamples, Coverage = bestCov };
        return null;
    }

    /// <summary>Copies one page (1-based) into a new PDF - used to send a single page to the vision reader.</summary>
    public static byte[] ExtractPage(string path, int pageNumber)
    {
        using var doc = PdfDocument.Open(path);
        var builder = new PdfDocumentBuilder();
        builder.AddPage(doc, pageNumber);
        return builder.Build();
    }

    public static int PageCount(string path)
    {
        using var doc = PdfDocument.Open(path);
        return doc.NumberOfPages;
    }
}
