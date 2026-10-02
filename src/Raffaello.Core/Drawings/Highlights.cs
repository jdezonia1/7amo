namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Highlighter colours recognised on marked-up statements (HSV ranges, tuned on scanned A3 prints).</summary>
public sealed record HighlightColour(string Name, double HueMin, double HueMax, double SatMin, double ValMin, double SatMax = 1.0)
{
    public static readonly HighlightColour Green = new("GREEN", 70, 170, 0.22, 0.55);
    public static readonly HighlightColour Yellow = new("YELLOW", 40, 70, 0.30, 0.70);
    public static readonly HighlightColour Pink = new("PINK", 290, 352, 0.18, 0.70, 0.75);
    public static readonly HighlightColour Orange = new("ORANGE", 18, 40, 0.35, 0.75);
    public static readonly HighlightColour[] All = { Green, Yellow, Pink, Orange };

    public static HighlightColour? Find(string? name) => All.FirstOrDefault(c => c.Name.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    public static List<HighlightColour> ParseList(string? csv)
    {
        var list = (csv ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Find).Where(c => c != null).Select(c => c!).ToList();
        return list.Count > 0 ? list : new List<HighlightColour> { Green };
    }

    public bool Matches(byte r, byte g, byte b)
    {
        var (h, s, v) = Rgb.Hsv(r, g, b);
        if (s < SatMin || s > SatMax || v < ValMin) return false;
        return HueMin <= HueMax ? h >= HueMin && h <= HueMax : h >= HueMin || h <= HueMax;
    }
}

/// <summary>SPOT = a dab on a symbol; STROKE = a highlighted run (conduit / cable route) or a highlighted note.</summary>
public static class HighlightKinds
{
    public const string Spot = "SPOT", Stroke = "STROKE";
}

/// <summary>[drawings] One highlighter mark found on a page.</summary>
public sealed class HighlightBlob
{
    public string Colour { get; init; } = "";
    public string Kind { get; init; } = HighlightKinds.Spot;
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
    public int Area { get; init; }
    /// <summary>Centre-line length in pixels (skeleton), for strokes.</summary>
    public double LengthPx { get; init; }
    /// <summary>Mean stroke width in pixels (area / length).</summary>
    public double WidthPx { get; init; }
    /// <summary>Centre line (skeleton pixels ordered roughly), page pixels - for drawing and for room splits.</summary>
    public List<PointD> Skeleton { get; init; } = new();
    public RectD Box => new(X, Y, W, H);
}

public sealed class HighlightOptions
{
    public List<HighlightColour> Colours { get; set; } = new() { HighlightColour.Green };
    /// <summary>Close gaps where drawing lines cross the highlight (pixels).</summary>
    public int CloseRadius { get; set; } = 2;
    /// <summary>Ignore specks smaller than this (pixels).</summary>
    public int MinArea { get; set; } = 40;
    /// <summary>A mark longer than this many stroke widths is a STROKE (route / note), else a SPOT.</summary>
    public double StrokeRatio { get; set; } = 3.5;
    /// <summary>Only look inside this region (page pixels).</summary>
    public RectD? Region { get; set; }
}

public sealed class HighlightResult
{
    public required BitMask Mask { get; init; }
    public List<HighlightBlob> Blobs { get; init; } = new();
    public IEnumerable<HighlightBlob> Spots => Blobs.Where(b => b.Kind == HighlightKinds.Spot);
    public IEnumerable<HighlightBlob> Strokes => Blobs.Where(b => b.Kind == HighlightKinds.Stroke);
    public double CoveredShare => Mask.Count / (double)(Mask.Width * Mask.Height);
}

/// <summary>[drawings] Highlighter detection: HSV colour masks, closing, connected components, skeleton length of strokes.</summary>
public static class HighlightDetector
{
    public static BitMask ColourMask(ColorImage img, IReadOnlyList<HighlightColour> colours, RectD? region = null)
    {
        var m = new BitMask(img.Width, img.Height);
        int x0 = 0, y0 = 0, x1 = img.Width, y1 = img.Height;
        if (region is { } r) { x0 = Math.Max(0, (int)r.X); y0 = Math.Max(0, (int)r.Y); x1 = Math.Min(img.Width, (int)r.Right); y1 = Math.Min(img.Height, (int)r.Bottom); }
        Parallel.For(y0, y1, y =>
        {
            for (var x = x0; x < x1; x++)
            {
                var i = (y * img.Width + x) * 3;
                byte rr = img.Data[i], gg = img.Data[i + 1], bb = img.Data[i + 2];
                foreach (var c in colours)
                    if (c.Matches(rr, gg, bb)) { m.Bits[y * img.Width + x] = true; break; }
            }
        });
        return m;
    }

    /// <summary>Pixels close to a sample colour (raster fallback for tracing coloured cable / tray lines on scans).</summary>
    public static BitMask NearColour(ColorImage img, Rgb sample, double tolerance)
    {
        var m = new BitMask(img.Width, img.Height);
        var t2 = tolerance * tolerance;
        Parallel.For(0, img.Height, y =>
        {
            for (var x = 0; x < img.Width; x++)
            {
                var i = (y * img.Width + x) * 3;
                double dr = img.Data[i] - sample.R, dg = img.Data[i + 1] - sample.G, db = img.Data[i + 2] - sample.B;
                m.Bits[y * img.Width + x] = dr * dr + dg * dg + db * db <= t2;
            }
        });
        return m;
    }

    public static HighlightResult Detect(ColorImage img, HighlightOptions? options = null)
    {
        var o = options ?? new HighlightOptions();
        var blobs = new List<HighlightBlob>();
        var all = new BitMask(img.Width, img.Height);
        foreach (var colour in o.Colours)
        {
            var mask = ColourMask(img, new[] { colour }, o.Region).Close(o.CloseRadius);
            all = all.Or(mask);
            foreach (var comp in mask.Components(o.MinArea)) blobs.Add(Describe(comp, colour.Name, o.StrokeRatio));
        }
        return new HighlightResult { Mask = all, Blobs = blobs };
    }

    /// <summary>Fills enclosed holes (the dark ring of a symbol under a highlighter dab) so a dab stays a compact spot.</summary>
    public static BitMask FillHoles(BitMask m)
    {
        var reached = new bool[m.Bits.Length];
        var stack = new Stack<int>();
        void Seed(int x, int y) { var i = y * m.Width + x; if (!m.Bits[i] && !reached[i]) { reached[i] = true; stack.Push(i); } }
        for (var x = 0; x < m.Width; x++) { Seed(x, 0); Seed(x, m.Height - 1); }
        for (var y = 0; y < m.Height; y++) { Seed(0, y); Seed(m.Width - 1, y); }
        while (stack.Count > 0)
        {
            var p = stack.Pop(); int px = p % m.Width, py = p / m.Width;
            if (px > 0) Seed(px - 1, py);
            if (px < m.Width - 1) Seed(px + 1, py);
            if (py > 0) Seed(px, py - 1);
            if (py < m.Height - 1) Seed(px, py + 1);
        }
        var o = m.Clone();
        for (var i = 0; i < o.Bits.Length; i++) if (!reached[i]) o.Bits[i] = true;
        return o;
    }

    public static HighlightBlob Describe(Component comp, string colour, double strokeRatio)
    {
        var local = FillHoles(comp.LocalMask());
        var area = local.Count;
        var skel = Skeleton.Thin(local);
        var length = Skeleton.Length(skel);
        var width = length > 0 ? area / length : Math.Sqrt(area);
        var longest = Math.Max(comp.W, comp.H);
        var kind = length > strokeRatio * width && longest > 2.5 * width ? HighlightKinds.Stroke : HighlightKinds.Spot;
        var pts = new List<PointD>();
        for (var y = 0; y < skel.Height; y++)
            for (var x = 0; x < skel.Width; x++)
                if (skel[x, y]) pts.Add(new PointD(x - 1 + comp.X, y - 1 + comp.Y));
        return new HighlightBlob
        {
            Colour = colour, Kind = kind, X = comp.X, Y = comp.Y, W = comp.W, H = comp.H, Cx = comp.Cx, Cy = comp.Cy, Area = area,
            LengthPx = kind == HighlightKinds.Stroke ? length : 0, WidthPx = width, Skeleton = pts,
        };
    }
}

/// <summary>[drawings] Zhang-Suen thinning and skeleton length (orthogonal steps 1, diagonal steps sqrt 2).</summary>
public static class Skeleton
{
    public static BitMask Thin(BitMask src)
    {
        var m = src.Clone();
        int w = m.Width, h = m.Height;
        var changed = true;
        var del = new List<int>();
        while (changed)
        {
            changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                del.Clear();
                for (var y = 1; y < h - 1; y++)
                    for (var x = 1; x < w - 1; x++)
                    {
                        if (!m.Bits[y * w + x]) continue;
                        bool p2 = m[x, y - 1], p3 = m[x + 1, y - 1], p4 = m[x + 1, y], p5 = m[x + 1, y + 1], p6 = m[x, y + 1], p7 = m[x - 1, y + 1], p8 = m[x - 1, y], p9 = m[x - 1, y - 1];
                        var b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;
                        var seq = new[] { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                        var a = 0;
                        for (var k = 0; k < 8; k++) if (!seq[k] && seq[k + 1]) a++;
                        if (a != 1) continue;
                        if (pass == 0) { if ((p2 && p4 && p6) || (p4 && p6 && p8)) continue; }
                        else { if ((p2 && p4 && p8) || (p2 && p6 && p8)) continue; }
                        del.Add(y * w + x);
                    }
                foreach (var i in del) m.Bits[i] = false;
                if (del.Count > 0) changed = true;
            }
        }
        return m;
    }

    /// <summary>Length of a 1-pixel skeleton: each 4-neighbour link counts 1, each diagonal link not already joined by an orthogonal path counts sqrt 2, halved (every link seen twice).</summary>
    public static double Length(BitMask skel)
    {
        double len = 0; var any = false;
        for (var y = 0; y < skel.Height; y++)
            for (var x = 0; x < skel.Width; x++)
            {
                if (!skel[x, y]) continue;
                any = true;
                if (skel[x + 1, y]) len += 1;
                if (skel[x, y + 1]) len += 1;
                if (skel[x + 1, y + 1] && !skel[x + 1, y] && !skel[x, y + 1]) len += Math.Sqrt(2);
                if (skel[x - 1, y + 1] && !skel[x - 1, y] && !skel[x, y + 1]) len += Math.Sqrt(2);
            }
        return any ? Math.Max(1, len) : 0;
    }
}
