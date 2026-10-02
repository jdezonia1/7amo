using System.Text;

namespace Raffaello.Core.Documents.Ocr;

/// <summary>A text line rebuilt from word boxes (words that share a baseline band).</summary>
public sealed class LayoutLine
{
    public List<OcrWord> Words { get; } = new();
    public Box Box => Box.Union(Words.Select(w => w.Box));
    public bool IsArabic => Words.Sum(w => ArabicText.ArabicLetters(w.Text)) > Words.Sum(w => w.Text.Count(char.IsAsciiLetter));
    /// <summary>Words in reading order: right-to-left when the line is mostly Arabic.</summary>
    public IEnumerable<OcrWord> ReadingOrder => IsArabic ? Words.OrderByDescending(w => w.Box.Right) : Words.OrderBy(w => w.Box.X);
    public string Text => string.Join(" ", ReadingOrder.Select(w => w.Text));
    public double Confidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);
}

/// <summary>
/// Groups OCR word boxes into lines and renders them as layout text (columns kept as runs of spaces, visual left-to-right column
/// order like pdftotext -layout) so the regex parsers written for PDF text layers read OCR output unchanged.
/// </summary>
public static class LayoutBuilder
{
    public static List<LayoutLine> Lines(IEnumerable<OcrWord> words)
    {
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Box.H > 0).OrderBy(w => w.Box.Cy).ToList();
        var lines = new List<LayoutLine>();
        var boxes = new List<(double Top, double Bottom)>();
        foreach (var w in list)
        {
            var best = -1; double bestOv = 0;
            for (var i = Math.Max(0, lines.Count - 6); i < lines.Count; i++)
            {
                var (t, b) = boxes[i];
                var ov = Math.Max(0, Math.Min(b, w.Box.Bottom) - Math.Max(t, w.Box.Y));
                var minH = Math.Min(b - t, w.Box.H);
                if (minH > 0 && ov / minH >= 0.5 && ov > bestOv && !lines[i].Words.Any(x => x.Box.HorizontalOverlap(w.Box) > 0.5 * Math.Min(x.Box.W, w.Box.W)))
                { best = i; bestOv = ov; }
            }
            if (best < 0) { var l = new LayoutLine(); l.Words.Add(w); lines.Add(l); boxes.Add((w.Box.Y, w.Box.Bottom)); }
            else
            {
                lines[best].Words.Add(w);
                // keep the band anchored on the first words (tall phrases must not swallow the next line)
                var (t, b) = boxes[best];
                boxes[best] = ((t * 3 + w.Box.Y) / 4, (b * 3 + w.Box.Bottom) / 4);
            }
        }
        return lines.OrderBy(l => l.Box.Cy).ToList();
    }

    /// <summary>Median width of one character over all words (for column padding).</summary>
    public static double CharWidth(IEnumerable<OcrWord> words)
    {
        var w = words.Where(x => x.Text.Length > 0 && x.Box.W > 0).Select(x => x.Box.W / x.Text.Length).OrderBy(x => x).ToList();
        return w.Count == 0 ? 10 : Math.Max(2, w[w.Count / 2]);
    }

    /// <summary>Layout text: one line per text line, words placed at column x / char width, at least two spaces between separate boxes.</summary>
    public static string Text(OcrPage page) => Text(page.Words);

    public static string Text(IEnumerable<OcrWord> words)
    {
        var all = words.ToList();
        if (all.Count == 0) return "";
        var cw = CharWidth(all);
        var minX = all.Min(w => w.Box.X);
        var sb = new StringBuilder();
        foreach (var line in Lines(all))
        {
            var row = new StringBuilder();
            OcrWord? prev = null;
            foreach (var w in line.Words.OrderBy(w => w.Box.X))
            {
                var col = (int)Math.Round((w.Box.X - minX) / cw);
                if (prev != null)
                {
                    var gap = w.Box.X - prev.Box.Right;
                    var min = gap > cw * 1.2 ? 2 : 1;
                    col = Math.Max(col, row.Length + min);
                }
                if (col > row.Length) row.Append(' ', col - row.Length);
                row.Append(w.Text.Replace('\n', ' '));
                prev = w;
            }
            sb.Append(row.ToString().TrimEnd()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Plain reading-order text (Arabic lines right-to-left), for the search index and the classifier.</summary>
    public static string ReadingText(IEnumerable<OcrWord> words) => string.Join("\n", Lines(words).Select(l => l.Text));
}
