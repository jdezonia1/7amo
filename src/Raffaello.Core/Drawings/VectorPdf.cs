using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] Mapping between PDF user space (points, y up, crop box, /Rotate) and sheet pixels (top-left origin, rendered at a DPI,
/// rotation applied - the frame PDFium renders).
/// </summary>
public sealed record PageFrame(double Left, double Bottom, double Right, double Top, int Rotation)
{
    public static PageFrame Of(Page p)
    {
        var b = p.CropBox.Bounds;
        return new PageFrame(b.Left, b.Bottom, b.Right, b.Top, ((p.Rotation.Value % 360) + 360) % 360);
    }

    public static PageFrame Of(string pdf, int page)
    {
        using var d = PdfDocument.Open(pdf);
        return Of(d.GetPage(page));
    }

    public double DisplayWidthPt => Rotation is 90 or 270 ? Top - Bottom : Right - Left;
    public double DisplayHeightPt => Rotation is 90 or 270 ? Right - Left : Top - Bottom;

    public PointD ToSheet(double x, double y, int dpi)
    {
        var (dx, dy) = Rotation switch
        {
            90 => (y - Bottom, x - Left),
            180 => (Right - x, y - Bottom),
            270 => (Top - y, Right - x),
            _ => (x - Left, Top - y),
        };
        var k = dpi / 72.0;
        return new PointD(dx * k, dy * k);
    }

    public PointD ToUser(PointD sheetPx, int dpi)
    {
        var k = 72.0 / dpi;
        double dx = sheetPx.X * k, dy = sheetPx.Y * k;
        return Rotation switch
        {
            90 => new PointD(Left + dy, Bottom + dx),
            180 => new PointD(Right - dx, Bottom + dy),
            270 => new PointD(Right - dy, Top - dx),
            _ => new PointD(Left + dx, Top - dy),
        };
    }
}

/// <summary>[drawings] Stroke style of a vector line (the legend sample the user picks: colour + weight + dash pattern).</summary>
public sealed record VectorStyle(Rgb Colour, double WidthPt, string Dash)
{
    public string Key => $"{Colour.Hex}|{WidthPt.ToString("0.##", CultureInfo.InvariantCulture)}|{Dash}";
    public override string ToString() => $"{Colour.Hex} {WidthPt:0.##} pt{(Dash.Length > 0 ? " dash " + Dash : "")}";
}

public sealed record VectorSegment(PointD A, PointD B, VectorStyle Style)
{
    public double Length => A.DistanceTo(B);
}

public sealed record VectorWord(string Text, RectD Box);

/// <summary>[drawings] The vector content of one PDF page in sheet pixels: stroked segments with style, words, scale text.</summary>
public sealed class VectorPage
{
    public int Dpi { get; init; }
    public required PageFrame Frame { get; init; }
    public List<VectorSegment> Segments { get; } = new();
    public List<VectorWord> Words { get; } = new();
    public double WidthPx => Frame.DisplayWidthPt * Dpi / 72.0;
    public double HeightPx => Frame.DisplayHeightPt * Dpi / 72.0;

    /// <summary>Styles by total length, for the legend picker.</summary>
    public List<(VectorStyle Style, double LengthPx, int Segments)> Styles() =>
        Segments.GroupBy(s => s.Style.Key).Select(g => (g.First().Style, g.Sum(s => s.Length), g.Count())).OrderByDescending(x => x.Item2).ToList();

    /// <summary>The segment nearest a clicked point (legend sample / snapping).</summary>
    public (VectorSegment? Segment, double Distance, PointD Closest) Nearest(PointD p)
    {
        VectorSegment? best = null; var bd = double.MaxValue; var bc = p;
        foreach (var s in Segments)
        {
            var (d, c, _) = Poly.ToSegment(p, s.A, s.B);
            if (d < bd) { bd = d; best = s; bc = c; }
        }
        return (best, bd, bc);
    }

    /// <summary>Drawing scale 1:N from the title block ("SCALE 1:50", "1:100 @ A1"), 0 when not found.</summary>
    public double ScaleFromText()
    {
        var text = string.Join(" ", Words.Select(w => w.Text));
        var m = Regex.Match(text, @"SCALE\s*[:=]?\s*1\s*[:/]\s*(\d{1,4})", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(text, @"(?<![\d.])1\s*:\s*(\d{2,4})(?!\d)");
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 0;
    }

    /// <summary>Mounting height "H=1350mm" / "H=1350" written next to a point (metres), 0 when none within the radius.</summary>
    public double HeightNear(PointD p, double radiusPx)
    {
        double best = double.MaxValue, value = 0;
        foreach (var w in Words)
        {
            var m = Regex.Match(w.Text, @"^H\s*=\s*(\d{2,5})\s*(MM)?$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var d = w.Box.Center.DistanceTo(p);
            if (d < radiusPx && d < best) { best = d; value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0; }
        }
        return value;
    }
}

/// <summary>[drawings] Reads vector geometry from a PDF page with PdfPig (Béziers flattened).</summary>
public static class VectorPdfReader
{
    public static VectorPage Read(string pdf, int page, int dpi)
    {
        using var doc = PdfDocument.Open(pdf);
        var p = doc.GetPage(page);
        var frame = PageFrame.Of(p);
        var vp = new VectorPage { Dpi = dpi, Frame = frame };
        foreach (var path in p.Paths)
        {
            if (!path.IsStroked || path.IsClipping) continue;
            var rgb = Colour(path.StrokeColor);
            var dash = path.LineDashPattern is { } dp && dp.Array.Count > 0 ? string.Join(" ", dp.Array.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))) : "";
            var style = new VectorStyle(rgb, Math.Round(path.LineWidth, 2), dash);
            foreach (var sub in path)
            {
                PdfPoint? start = null, cur = null;
                foreach (var cmd in sub.Commands)
                {
                    switch (cmd)
                    {
                        case PdfSubpath.Move mv: cur = start = mv.Location; break;
                        case PdfSubpath.Line ln:
                            Add(vp, frame, dpi, ln.From, ln.To, style); cur = ln.To; break;
                        case PdfSubpath.CubicBezierCurve bz:
                            var prev = bz.StartPoint;
                            for (var i = 1; i <= 12; i++)
                            {
                                var t = i / 12.0;
                                var pt = Bezier(bz.StartPoint, bz.FirstControlPoint, bz.SecondControlPoint, bz.EndPoint, t);
                                Add(vp, frame, dpi, prev, pt, style); prev = pt;
                            }
                            cur = bz.EndPoint; break;
                        case PdfSubpath.BezierCurve qb:
                            Add(vp, frame, dpi, qb.StartPoint, qb.EndPoint, style); cur = qb.EndPoint; break;
                        case PdfSubpath.Close:
                            if (cur is { } c && start is { } s0 && (c.X != s0.X || c.Y != s0.Y)) Add(vp, frame, dpi, c, s0, style);
                            cur = start; break;
                    }
                }
            }
        }
        foreach (var w in p.GetWords())
        {
            var a = frame.ToSheet(w.BoundingBox.Left, w.BoundingBox.Top, dpi);
            var b = frame.ToSheet(w.BoundingBox.Right, w.BoundingBox.Bottom, dpi);
            vp.Words.Add(new VectorWord(w.Text, new RectD(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y))));
        }
        return vp;
    }

    private static void Add(VectorPage vp, PageFrame f, int dpi, PdfPoint a, PdfPoint b, VectorStyle s)
    {
        var pa = f.ToSheet(a.X, a.Y, dpi); var pb = f.ToSheet(b.X, b.Y, dpi);
        if (pa.DistanceTo(pb) > 1e-6) vp.Segments.Add(new VectorSegment(pa, pb, s));
    }

    private static PdfPoint Bezier(PdfPoint p0, PdfPoint p1, PdfPoint p2, PdfPoint p3, double t)
    {
        var u = 1 - t;
        double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
        double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
        return new PdfPoint(x, y);
    }

    private static Rgb Colour(UglyToad.PdfPig.Graphics.Colors.IColor? c)
    {
        if (c is null) return Rgb.Black;
        try
        {
            var (r, g, b) = c.ToRGBValues();
            return new Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
        catch (Exception) { return Rgb.Black; }
    }
}

/// <summary>[drawings] Chains segments of the same style into polylines (shared end points within a tolerance).</summary>
public static class Polylines
{
    public static List<List<PointD>> Chain(IEnumerable<(PointD A, PointD B)> segments, double tol = 0.75)
    {
        var segs = segments.ToList();
        var used = new bool[segs.Count];
        var byEnd = new Dictionary<(long, long), List<int>>();
        (long, long) Key(PointD p) => ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol));
        IEnumerable<int> Near(PointD p)
        {
            var (kx, ky) = Key(p);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (byEnd.TryGetValue((kx + dx, ky + dy), out var l)) foreach (var i in l) yield return i;
        }
        for (var i = 0; i < segs.Count; i++)
            foreach (var p in new[] { segs[i].A, segs[i].B })
            {
                if (!byEnd.TryGetValue(Key(p), out var l)) byEnd[Key(p)] = l = new List<int>();
                l.Add(i);
            }
        var res = new List<List<PointD>>();
        for (var i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var line = new LinkedList<PointD>(new[] { segs[i].A, segs[i].B });
            foreach (var forward in new[] { true, false })
            {
                while (true)
                {
                    var end = forward ? line.Last!.Value : line.First!.Value;
                    var next = Near(end).FirstOrDefault(j => !used[j] && (segs[j].A.DistanceTo(end) <= tol || segs[j].B.DistanceTo(end) <= tol), -1);
                    if (next < 0) break;
                    used[next] = true;
                    var other = segs[next].A.DistanceTo(end) <= tol ? segs[next].B : segs[next].A;
                    if (forward) line.AddLast(other); else line.AddFirst(other);
                }
            }
            res.Add(line.ToList());
        }
        return res;
    }

    /// <summary>Douglas-Peucker simplification.</summary>
    public static List<PointD> Simplify(IReadOnlyList<PointD> pts, double tol)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double md = 0; var mi = -1;
            for (var i = a + 1; i < b; i++)
            {
                var d = Poly.ToSegment(pts[i], pts[a], pts[b]).Distance;
                if (d > md) { md = d; mi = i; }
            }
            if (mi >= 0 && md > tol) { keep[mi] = true; stack.Push((a, mi)); stack.Push((mi, b)); }
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>Traces a 1-pixel skeleton into polylines (from end points first, then remaining loops).</summary>
    public static List<List<PointD>> TraceSkeleton(BitMask skel, double offsetX = 0, double offsetY = 0, double simplify = 1.0)
    {
        int w = skel.Width, h = skel.Height;
        var visited = new bool[w * h];
        var res = new List<List<PointD>>();
        int Degree(int x, int y)
        {
            var n = 0;
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) if ((dx != 0 || dy != 0) && skel[x + dx, y + dy]) n++;
            return n;
        }
        void Walk(int x, int y)
        {
            var line = new List<PointD> { new(x + offsetX, y + offsetY) };
            visited[y * w + x] = true;
            while (true)
            {
                var moved = false;
                // orthogonal neighbours first so diagonal shortcuts do not skip pixels
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (!skel[nx, ny] || visited[ny * w + nx]) continue;
                    visited[ny * w + nx] = true; x = nx; y = ny; line.Add(new PointD(x + offsetX, y + offsetY)); moved = true; break;
                }
                if (!moved) break;
            }
            if (line.Count >= 2) res.Add(simplify > 0 ? Simplify(line, simplify) : line);
        }
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (skel.Bits[y * w + x] && !visited[y * w + x] && Degree(x, y) == 1) Walk(x, y);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) if (skel.Bits[y * w + x] && !visited[y * w + x]) Walk(x, y);
        return res;
    }
}
