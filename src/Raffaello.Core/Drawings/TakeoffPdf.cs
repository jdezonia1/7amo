using System.Globalization;
using System.Reflection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Writer;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] One mark on the drawing: circle + numbered tag.</summary>
public sealed record MarkSpec(PointD Center, double RadiusPx, Rgb Colour, string Label, bool Excluded = false);

/// <summary>[drawings] A measured run drawn as a coloured polyline with its length.</summary>
public sealed record RunSpec(List<PointD> Points, Rgb Colour, string Label);

/// <summary>[drawings] A table in the left panel.</summary>
public sealed class PanelTable
{
    public List<string> Headers { get; init; } = new();
    /// <summary>Relative column widths.</summary>
    public List<double> Widths { get; init; } = new();
    public List<PanelRow> Rows { get; init; } = new();
    public PanelRow? Total { get; init; }
    public string Footer { get; init; } = "";
    /// <summary>Index of the image column (-1 = none).</summary>
    public int ImageColumn { get; init; } = -1;
    /// <summary>Columns printed bold (numbers).</summary>
    public HashSet<int> BoldColumns { get; init; } = new();
}

public sealed record PanelRow(List<string> Cells, byte[]? ImagePng = null, bool Highlight = false);

/// <summary>[drawings] One output page: the drawing (vector PDF page kept as is, or a picture) + the left panel.</summary>
public sealed class TakeoffPage
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public List<string> Notes { get; init; } = new();
    public List<PanelTable> Tables { get; init; } = new();
    public List<MarkSpec> Marks { get; init; } = new();
    public List<RunSpec> Runs { get; init; } = new();
    /// <summary>Base: a PDF page (kept as vector) ...</summary>
    public string? PdfPath { get; init; }
    public int PdfPage { get; init; } = 1;
    /// <summary>... or a picture (CAD / IFC / image sheets).</summary>
    public byte[]? ImagePng { get; init; }
    public int ImageWidthPx { get; init; }
    public int ImageHeightPx { get; init; }
    /// <summary>Sheet pixels per inch of the coordinates of marks / runs (and of the picture).</summary>
    public int Dpi { get; init; } = 150;
}

/// <summary>
/// [drawings] Writes takeoff / statement-check sheets in the house layout (Mohamed's QS markup): a left panel added to the original
/// sheet (title bar dark red with the source file in yellow, rules, a SYMBOL | ITEM / TAG | SYSTEM | C/W | CEIL | 1ST | 2ND | FLEX |
/// DALI table with a pale yellow TOTAL row, "N marks on this sheet"), and on the drawing a circle per counted symbol (dark red = wall,
/// amber = ceiling) with a yellow numbered tag. The original PDF page stays vector: its page box is widened to the left and annotations
/// (review clouds) are kept; rotated pages are re-placed upright (annotations then dropped - noted).
/// </summary>
public static class TakeoffPdf
{
    public static readonly Rgb DarkRed = new(0x8B, 0, 0);
    public static readonly Rgb Amber = new(0xE3, 0xAE, 0x12);
    public static readonly Rgb TagYellow = new(0xFF, 0xE6, 0x00);
    public static readonly Rgb HeaderGrey = new(0x33, 0x33, 0x33);
    public static readonly Rgb Zebra = new(0xF0, 0xF0, 0xF0);
    public static readonly Rgb TotalYellow = new(0xFF, 0xF2, 0xA8);
    public static readonly Rgb ExclGrey = new(0x99, 0x99, 0x99);

    public sealed record BuildResult(byte[] Pdf, List<string> Notes);

    public static BuildResult Build(IReadOnlyList<TakeoffPage> pages)
    {
        var b = new PdfDocumentBuilder();
        var regular = b.AddStandard14Font(Standard14Font.Helvetica);
        var bold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var notes = new List<string>();
        var sources = new Dictionary<string, PdfDocument>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var pg in pages)
            {
                PdfPageBuilder page;
                Func<PointD, PdfPoint> toPt;
                double left, bottom, top, scale;
                if (pg.PdfPath != null)
                {
                    if (!sources.TryGetValue(pg.PdfPath, out var src)) sources[pg.PdfPath] = src = PdfDocument.Open(pg.PdfPath);
                    var sp = src.GetPage(pg.PdfPage);
                    var frame = PageFrame.Of(sp);
                    var panelW = PanelWidth(frame.DisplayHeightPt);
                    if (frame.Rotation == 0 && TryWidenCopy(b, src, pg.PdfPage, frame, panelW, out var wide))
                    {
                        page = wide!;
                        left = frame.Left - panelW; bottom = frame.Bottom; top = frame.Top;
                        toPt = p => { var u = frame.ToUser(p, pg.Dpi); return new PdfPoint(u.X, u.Y); };
                    }
                    else
                    {
                        // upright copy with the content transformed, panel on the left
                        var w = frame.DisplayWidthPt; var h = frame.DisplayHeightPt;
                        page = b.AddPage(w + panelW, h);
                        page.CopyFrom(sp);
                        var m = DisplayMatrix(frame, panelW);
                        page.SelectContentStream(0);
                        page.NewContentStreamBefore();
                        Ops(page).Add(Push.Value);
                        Ops(page).Add(new ModifyCurrentTransformationMatrix(m));
                        page.SelectContentStream(page.ContentStreams.Count - 1);
                        page.NewContentStreamAfter();
                        Ops(page).Add(Pop.Value);
                        left = 0; bottom = 0; top = h;
                        var k = 72.0 / pg.Dpi;
                        toPt = p => new PdfPoint(panelW + p.X * k, h - p.Y * k);
                        if (frame.Rotation != 0) notes.Add($"{Path.GetFileName(pg.PdfPath)} p{pg.PdfPage}: rotated page re-placed upright; PDF annotations on it are not copied.");
                    }
                    scale = panelW / 620.0;
                    DrawPanel(page, pg, left, bottom, top, panelW, scale, regular, bold);
                }
                else
                {
                    var k = 72.0 / pg.Dpi;
                    var w = pg.ImageWidthPx * k; var h = pg.ImageHeightPx * k;
                    var panelW = PanelWidth(h);
                    page = b.AddPage(w + panelW, h);
                    if (pg.ImagePng != null) page.AddPng(pg.ImagePng, new PdfRectangle(panelW, 0, panelW + w, h));
                    left = 0; bottom = 0; top = h;
                    toPt = p => new PdfPoint(panelW + p.X * k, h - p.Y * k);
                    scale = panelW / 620.0;
                    DrawPanel(page, pg, left, bottom, top, panelW, scale, regular, bold);
                }
                DrawRuns(page, pg, toPt, scale, bold);
                DrawMarks(page, pg, toPt, scale, bold);
            }
            return new BuildResult(b.Build(), notes);
        }
        finally
        {
            foreach (var d in sources.Values) d.Dispose();
        }
    }

    private static double PanelWidth(double pageHeightPt) => Math.Clamp(pageHeightPt * 0.62, 430, 1500);

    private static List<IGraphicsStateOperation> Ops(PdfPageBuilder p) =>
        p.CurrentStream.Operations as List<IGraphicsStateOperation> ?? throw new InvalidOperationException("PdfPig content stream layout changed.");

    /// <summary>Copies the page (content, resources, annotations) and widens its media / crop box to the left.</summary>
    private static bool TryWidenCopy(PdfDocumentBuilder b, PdfDocument src, int pageNo, PageFrame f, double panelW, out PdfPageBuilder? page)
    {
        page = null;
        try
        {
            var p = b.AddPage(src, pageNo, new PdfDocumentBuilder.AddPageOptions { KeepAnnotations = true });
            var field = typeof(PdfPageBuilder).GetField("pageDictionary", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(p) is not Dictionary<NameToken, IToken> dict) return false;
            var box = new ArrayToken(new IToken[] { new NumericToken(f.Left - panelW), new NumericToken(f.Bottom), new NumericToken(f.Right), new NumericToken(f.Top) });
            dict[NameToken.MediaBox] = box;
            dict[NameToken.CropBox] = box;
            page = p;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>cm matrix that maps user space of a rotated page to the upright display frame shifted right by the panel.</summary>
    private static double[] DisplayMatrix(PageFrame f, double panelW) => f.Rotation switch
    {
        90 => new[] { 0, -1.0, 1, 0, panelW - f.Bottom, f.Right },
        180 => new[] { -1.0, 0, 0, -1, panelW + f.Right, f.Top },
        270 => new[] { 0, 1.0, -1, 0, panelW + f.Top, -f.Left },
        _ => new[] { 1.0, 0, 0, 1, panelW - f.Left, -f.Bottom },
    };

    private static void Fill(PdfPageBuilder p, Rgb c) { p.SetTextAndFillColor(c.R, c.G, c.B); p.SetStrokeColor(c.R, c.G, c.B); }

    private static void Rect(PdfPageBuilder p, double x, double y, double w, double h, Rgb c)
    {
        Fill(p, c);
        p.DrawRectangle(new PdfPoint(x, y), w, h, 0.1, true);
    }

    private static void Text(PdfPageBuilder p, string s, double size, double x, double y, PdfDocumentBuilder.AddedFont f, Rgb c)
    {
        p.SetTextAndFillColor(c.R, c.G, c.B);
        p.AddText(Ascii(s), size, new PdfPoint(x, y), f);
    }

    /// <summary>Standard-14 fonts are WinAnsi: replace what they cannot show.</summary>
    private static string Ascii(string s) => new(s.Select(ch => ch is >= ' ' and <= '~' ? ch : ch switch { '≥' => '>', '×' => 'x', '–' or '—' => '-', _ => '?' }).ToArray());

    private static string Fit(string s, double width, double size, bool bold)
    {
        var per = size * (bold ? 0.6 : 0.53);
        var max = Math.Max(1, (int)(width / per));
        return s.Length <= max ? s : s[..Math.Max(1, max - 1)] + ".";
    }

    private static void DrawPanel(PdfPageBuilder p, TakeoffPage pg, double left, double bottom, double top, double panelW, double s,
        PdfDocumentBuilder.AddedFont regular, PdfDocumentBuilder.AddedFont bold)
    {
        var x0 = left + 12 * s; var w = panelW - 24 * s;
        var y = top - 12 * s;
        // frame
        p.SetStrokeColor(DarkRed.R, DarkRed.G, DarkRed.B);
        p.DrawRectangle(new PdfPoint(x0, bottom + 12 * s), w, top - bottom - 24 * s, 2 * s, false);
        // title bar
        var barH = 56 * s;
        Rect(p, x0, y - barH, w, barH, DarkRed);
        Text(p, Fit(pg.Title, w - 24 * s, 19 * s, true), 19 * s, x0 + 12 * s, y - 27 * s, bold, Rgb.White);
        Text(p, Fit(pg.Subtitle, w - 24 * s, 10 * s, false), 10 * s, x0 + 12 * s, y - 46 * s, regular, new Rgb(0xFF, 0xD7, 0x40));
        y -= barH + 20 * s;
        foreach (var n in pg.Notes)
        {
            foreach (var line in Wrap(n, w - 24 * s, 9.5 * s))
            {
                Text(p, line, 9.5 * s, x0 + 12 * s, y, regular, Rgb.Black);
                y -= 15 * s;
            }
        }
        y -= 10 * s;
        foreach (var t in pg.Tables)
        {
            y = DrawTable(p, t, x0 + 10 * s, y, w - 20 * s, s, regular, bold);
            y -= 18 * s;
            if (y < bottom + 40 * s) break;
        }
    }

    private static IEnumerable<string> Wrap(string text, double width, double size)
    {
        var max = Math.Max(10, (int)(width / (size * 0.52)));
        var words = text.Split(' ');
        var line = "";
        foreach (var word in words)
        {
            if ((line + " " + word).Trim().Length > max && line.Length > 0) { yield return line; line = word; }
            else line = (line + " " + word).Trim();
        }
        if (line.Length > 0) yield return line;
    }

    private static double DrawTable(PdfPageBuilder p, PanelTable t, double x, double y, double w, double s, PdfDocumentBuilder.AddedFont regular, PdfDocumentBuilder.AddedFont bold)
    {
        var total = t.Widths.Count == t.Headers.Count ? t.Widths.Sum() : t.Headers.Count;
        var widths = t.Headers.Select((_, i) => (t.Widths.Count == t.Headers.Count ? t.Widths[i] : 1) / total * w).ToList();
        var xs = new List<double> { x };
        foreach (var cw in widths) xs.Add(xs[^1] + cw);
        var headH = 22 * s; var rowH = t.ImageColumn >= 0 ? 26 * s : 18 * s;
        Rect(p, x, y - headH, w, headH, HeaderGrey);
        for (var i = 0; i < t.Headers.Count; i++) Text(p, Fit(t.Headers[i], widths[i] - 6 * s, 9 * s, true), 9 * s, xs[i] + 4 * s, y - 15 * s, bold, Rgb.White);
        y -= headH;
        var n = 0;
        foreach (var r in t.Rows)
        {
            Rect(p, x, y - rowH, w, rowH, r.Highlight ? new Rgb(0xFF, 0xDD, 0xDD) : n++ % 2 == 0 ? Zebra : Rgb.White);
            for (var i = 0; i < t.Headers.Count && i < r.Cells.Count; i++)
            {
                if (i == t.ImageColumn && r.ImagePng != null)
                {
                    try { p.AddPng(r.ImagePng, new PdfRectangle(xs[i] + 2 * s, y - rowH + 2 * s, xs[i] + 2 * s + (rowH - 4 * s), y - 2 * s)); }
                    catch (Exception) { /* an image the PDF writer cannot take is left out */ }
                    continue;
                }
                var f = t.BoldColumns.Contains(i) ? bold : regular;
                Text(p, Fit(r.Cells[i], widths[i] - 6 * s, 9 * s, t.BoldColumns.Contains(i)), 9 * s, xs[i] + 4 * s, y - rowH / 2 - 3 * s, f, Rgb.Black);
            }
            y -= rowH;
        }
        if (t.Total != null)
        {
            var th = 22 * s;
            Rect(p, x, y - th, w, th, TotalYellow);
            for (var i = 0; i < t.Headers.Count && i < t.Total.Cells.Count; i++)
                if (t.Total.Cells[i].Length > 0) Text(p, Fit(t.Total.Cells[i], i == 0 ? w * 0.6 : widths[i] - 6 * s, 10 * s, true), 10 * s, xs[i] + 4 * s, y - 15 * s, bold, DarkRed);
            y -= th;
        }
        if (t.Footer.Length > 0)
        {
            y -= 16 * s;
            Text(p, t.Footer, 9.5 * s, x + 4 * s, y, regular, Rgb.Black);
        }
        return y;
    }

    private static void DrawMarks(PdfPageBuilder p, TakeoffPage pg, Func<PointD, PdfPoint> toPt, double s, PdfDocumentBuilder.AddedFont bold)
    {
        var k = 72.0 / pg.Dpi;
        foreach (var m in pg.Marks)
        {
            var c = toPt(m.Center);
            var r = Math.Max(3.5, m.RadiusPx * k);
            var col = m.Excluded ? ExclGrey : m.Colour;
            p.SetStrokeColor(col.R, col.G, col.B);
            p.DrawCircle(c, 2 * r, Math.Max(1.2, 2 * s * 0.6), false);
            var label = m.Excluded ? "EXCL" : m.Label;
            if (label.Length == 0) continue;
            var fs = Math.Max(4.5, r * 0.75);
            var tw = label.Length * fs * 0.6 + 2;
            var tx = c.X + r * 0.55; var ty = c.Y + r * 0.45;
            Rect(p, tx, ty, tw, fs + 1.5, m.Excluded ? new Rgb(0xDD, 0xDD, 0xDD) : TagYellow);
            Text(p, label, fs, tx + 1, ty + 1.6, bold, m.Excluded ? new Rgb(0x55, 0x55, 0x55) : DarkRed);
        }
    }

    private static void DrawRuns(PdfPageBuilder p, TakeoffPage pg, Func<PointD, PdfPoint> toPt, double s, PdfDocumentBuilder.AddedFont bold)
    {
        foreach (var r in pg.Runs)
        {
            if (r.Points.Count < 2) continue;
            p.SetStrokeColor(r.Colour.R, r.Colour.G, r.Colour.B);
            for (var i = 1; i < r.Points.Count; i++) p.DrawLine(toPt(r.Points[i - 1]), toPt(r.Points[i]), Math.Max(1.2, 1.6 * s * 0.6));
            if (r.Label.Length == 0) continue;
            var mid = toPt(Midpoint(r.Points));
            var fs = 5.5;
            Rect(p, mid.X + 2, mid.Y + 2, r.Label.Length * fs * 0.6 + 2, fs + 1.5, TagYellow);
            Text(p, r.Label, fs, mid.X + 3, mid.Y + 3.6, bold, DarkRed);
        }
    }

    private static PointD Midpoint(IReadOnlyList<PointD> pts)
    {
        var half = Poly.PolylineLength(pts) / 2; double acc = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            var d = pts[i].DistanceTo(pts[i - 1]);
            if (acc + d >= half) return pts[i - 1] + (pts[i] - pts[i - 1]) * ((half - acc) / Math.Max(d, 1e-9));
            acc += d;
        }
        return pts[^1];
    }

    public static string Num(double v) => Math.Abs(v) < 1e-9 ? "-" : v.ToString(Math.Abs(v % 1) < 1e-9 ? "0" : "0.##", CultureInfo.InvariantCulture);
}
