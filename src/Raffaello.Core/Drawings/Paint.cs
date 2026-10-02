namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Minimal raster painting (overlays, synthetic test drawings). Thick lines, circles, rectangles, highlighter strokes.</summary>
public static class Paint
{
    public static void Line(this ColorImage img, double x0, double y0, double x1, double y1, Rgb c, double thickness = 1)
    {
        var r = Math.Max(0.5, thickness / 2);
        var ri = (int)Math.Ceiling(r);
        var stamp = new List<(int, int)>();
        for (var dy = -ri; dy <= ri; dy++)
            for (var dx = -ri; dx <= ri; dx++)
                if (dx * dx + dy * dy <= r * r + 0.25) stamp.Add((dx, dy));
        var len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var steps = Math.Max(1, (int)Math.Ceiling(len * 2));
        int lastX = int.MinValue, lastY = int.MinValue;
        for (var s = 0; s <= steps; s++)
        {
            var t = (double)s / steps;
            var px = (int)Math.Round(x0 + (x1 - x0) * t); var py = (int)Math.Round(y0 + (y1 - y0) * t);
            if (px == lastX && py == lastY) continue;
            lastX = px; lastY = py;
            foreach (var (dx, dy) in stamp) img.Set(px + dx, py + dy, c);
        }
    }

    /// <summary>Highlighter stroke: multiply blend along a thick segment.</summary>
    public static void Highlight(this ColorImage img, double x0, double y0, double x1, double y1, Rgb c, double width)
    {
        var r = width / 2;
        var a = new PointD(x0, y0); var b = new PointD(x1, y1);
        for (var y = (int)Math.Max(0, Math.Min(y0, y1) - r - 1); y <= (int)Math.Min(img.Height - 1, Math.Max(y0, y1) + r + 1); y++)
            for (var x = (int)Math.Max(0, Math.Min(x0, x1) - r - 1); x <= (int)Math.Min(img.Width - 1, Math.Max(x0, x1) + r + 1); x++)
                if (Poly.ToSegment(new PointD(x, y), a, b).Distance <= r) img.Multiply(x, y, c);
    }

    public static void HighlightDisc(this ColorImage img, double cx, double cy, double radius, Rgb c)
    {
        for (var y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
            for (var x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) img.Multiply(x, y, c);
    }

    public static void Circle(this ColorImage img, double cx, double cy, double radius, Rgb c, double thickness = 1)
    {
        var r0 = radius - thickness / 2; var r1 = radius + thickness / 2;
        for (var y = (int)(cy - r1 - 1); y <= (int)(cy + r1 + 1); y++)
            for (var x = (int)(cx - r1 - 1); x <= (int)(cx + r1 + 1); x++)
            {
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d >= r0 && d <= r1) img.Set(x, y, c);
            }
    }

    public static void Disc(this ColorImage img, double cx, double cy, double radius, Rgb c)
    {
        for (var y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
            for (var x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) img.Set(x, y, c);
    }

    public static void Rect(this ColorImage img, double x, double y, double w, double h, Rgb c, double thickness = 1)
    {
        img.Line(x, y, x + w, y, c, thickness); img.Line(x + w, y, x + w, y + h, c, thickness);
        img.Line(x + w, y + h, x, y + h, c, thickness); img.Line(x, y + h, x, y, c, thickness);
    }

    public static void FillRect(this ColorImage img, double x, double y, double w, double h, Rgb c, double alpha = 1)
    {
        for (var j = (int)Math.Max(0, y); j < (int)Math.Min(img.Height, y + h); j++)
            for (var i = (int)Math.Max(0, x); i < (int)Math.Min(img.Width, x + w); i++)
                if (alpha >= 1) img.Set(i, j, c); else img.Blend(i, j, c, alpha);
    }

    public static void Polyline(this ColorImage img, IReadOnlyList<PointD> pts, Rgb c, double thickness = 1, bool closed = false)
    {
        for (var i = 1; i < pts.Count; i++) img.Line(pts[i - 1].X, pts[i - 1].Y, pts[i].X, pts[i].Y, c, thickness);
        if (closed && pts.Count > 2) img.Line(pts[^1].X, pts[^1].Y, pts[0].X, pts[0].Y, c, thickness);
    }

    /// <summary>Tints the mask pixels (overlay of a highlight / change mask).</summary>
    public static void Tint(this ColorImage img, BitMask m, Rgb c, double alpha, Affine2D? maskToImage = null)
    {
        if (maskToImage is null && m.Width == img.Width && m.Height == img.Height)
        {
            for (var i = 0; i < m.Bits.Length; i++) if (m.Bits[i]) img.Blend(i % img.Width, i / img.Width, c, alpha);
            return;
        }
        var inv = (maskToImage ?? Affine2D.Identity).Inverse();
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                if (m[(int)Math.Round(p.X), (int)Math.Round(p.Y)]) img.Blend(x, y, c, alpha);
            }
    }

    /// <summary>Digits and capitals in a 3x5 pixel font, scaled (labels on overlays without a font engine).</summary>
    public static void Text(this ColorImage img, double x, double y, string text, Rgb c, int scale = 2)
    {
        var cx = x;
        foreach (var ch in text.ToUpperInvariant())
        {
            if (Glyphs.TryGetValue(ch, out var g))
                for (var row = 0; row < 5; row++)
                    for (var col = 0; col < 3; col++)
                        if (g[row][col] == '#')
                            img.FillRect(cx + col * scale, y + row * scale, scale, scale, c);
            cx += 4 * scale;
        }
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { "###", "#.#", "#.#", "#.#", "###" }, ['1'] = new[] { ".#.", "##.", ".#.", ".#.", "###" },
        ['2'] = new[] { "###", "..#", "###", "#..", "###" }, ['3'] = new[] { "###", "..#", "###", "..#", "###" },
        ['4'] = new[] { "#.#", "#.#", "###", "..#", "..#" }, ['5'] = new[] { "###", "#..", "###", "..#", "###" },
        ['6'] = new[] { "###", "#..", "###", "#.#", "###" }, ['7'] = new[] { "###", "..#", ".#.", ".#.", ".#." },
        ['8'] = new[] { "###", "#.#", "###", "#.#", "###" }, ['9'] = new[] { "###", "#.#", "###", "..#", "###" },
        ['.'] = new[] { "...", "...", "...", "...", ".#." }, ['-'] = new[] { "...", "...", "###", "...", "..." },
        ['+'] = new[] { "...", ".#.", "###", ".#.", "..." }, ['M'] = new[] { "#.#", "###", "###", "#.#", "#.#" },
        ['A'] = new[] { ".#.", "#.#", "###", "#.#", "#.#" }, ['B'] = new[] { "##.", "#.#", "##.", "#.#", "##." },
        ['C'] = new[] { "###", "#..", "#..", "#..", "###" }, ['D'] = new[] { "##.", "#.#", "#.#", "#.#", "##." },
        ['E'] = new[] { "###", "#..", "##.", "#..", "###" }, ['F'] = new[] { "###", "#..", "##.", "#..", "#.." },
        ['G'] = new[] { "###", "#..", "#.#", "#.#", "###" }, ['H'] = new[] { "#.#", "#.#", "###", "#.#", "#.#" },
        ['I'] = new[] { "###", ".#.", ".#.", ".#.", "###" }, ['L'] = new[] { "#..", "#..", "#..", "#..", "###" },
        ['N'] = new[] { "##.", "#.#", "#.#", "#.#", "#.#" }, ['O'] = new[] { "###", "#.#", "#.#", "#.#", "###" },
        ['P'] = new[] { "###", "#.#", "###", "#..", "#.." }, ['R'] = new[] { "##.", "#.#", "##.", "#.#", "#.#" },
        ['S'] = new[] { "###", "#..", "###", "..#", "###" }, ['T'] = new[] { "###", ".#.", ".#.", ".#.", ".#." },
        ['U'] = new[] { "#.#", "#.#", "#.#", "#.#", "###" }, ['V'] = new[] { "#.#", "#.#", "#.#", "#.#", ".#." },
        ['W'] = new[] { "#.#", "#.#", "###", "###", "#.#" }, ['X'] = new[] { "#.#", "#.#", ".#.", "#.#", "#.#" },
        ['Y'] = new[] { "#.#", "#.#", ".#.", ".#.", ".#." }, ['K'] = new[] { "#.#", "##.", "#..", "##.", "#.#" },
        [' '] = new[] { "...", "...", "...", "...", "..." }, [':'] = new[] { "...", ".#.", "...", ".#.", "..." },
        ['/'] = new[] { "..#", "..#", ".#.", "#..", "#.." }, ['%'] = new[] { "#.#", "..#", ".#.", "#..", "#.#" },
    };
}
