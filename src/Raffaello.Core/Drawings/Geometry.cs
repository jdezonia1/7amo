using System.Globalization;
using System.Text;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] A 2D point (drawing pixels, PDF points, CAD units or normalised plan coordinates, depending on context).</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
    public static PointD operator *(PointD a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(PointD o) => (this - o).Length;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X:0.###},{Y:0.###}");
}

/// <summary>[drawings] Axis-aligned rectangle.</summary>
public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public PointD Center => new(X + W / 2, Y + H / 2);
    public double Area => Math.Max(0, W) * Math.Max(0, H);
    public bool Contains(PointD p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public double IntersectionArea(RectD o)
    {
        var w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        var h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    public static RectD Bounds(IEnumerable<PointD> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        return x0 > x1 ? default : new RectD(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>
/// [drawings] Affine transform x' = A x + B y + C, y' = D x + E y + F. Fitted from 2 point pairs (similarity: scale, rotation,
/// shift) or from 3+ pairs (least-squares affine).
/// </summary>
public readonly record struct Affine2D(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine2D Identity = new(1, 0, 0, 0, 1, 0);

    public PointD Apply(PointD p) => new(A * p.X + B * p.Y + C, D * p.X + E * p.Y + F);

    /// <summary>Mean linear scale factor.</summary>
    public double Scale => Math.Sqrt(Math.Abs(A * E - B * D));

    /// <summary>Rotation in degrees (of the x axis).</summary>
    public double RotationDeg => Math.Atan2(D, A) * 180 / Math.PI;

    public Affine2D Inverse()
    {
        var det = A * E - B * D;
        if (Math.Abs(det) < 1e-15) throw new InvalidOperationException("The calibration points are degenerate (on one line or on top of each other).");
        var ia = E / det; var ib = -B / det; var id = -D / det; var ie = A / det;
        return new Affine2D(ia, ib, -(ia * C + ib * F), id, ie, -(id * C + ie * F));
    }

    /// <summary>this after first: p -> this(first(p)).</summary>
    public Affine2D After(Affine2D first) => new(
        A * first.A + B * first.D, A * first.B + B * first.E, A * first.C + B * first.F + C,
        D * first.A + E * first.D, D * first.B + E * first.E, D * first.C + E * first.F + F);

    public static Affine2D Similarity(double scale, double rotationDeg, double tx, double ty)
    {
        var r = rotationDeg * Math.PI / 180; var c = Math.Cos(r) * scale; var s = Math.Sin(r) * scale;
        return new Affine2D(c, -s, tx, s, c, ty);
    }

    public static Affine2D ScaleOnly(double sx, double sy) => new(sx, 0, 0, 0, sy, 0);

    /// <summary>Fits src -> dst. Two pairs give a similarity, three or more a least-squares affine.</summary>
    public static Affine2D Fit(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        if (src.Count != dst.Count) throw new ArgumentException("Point lists differ in length.");
        if (src.Count < 2) throw new InvalidOperationException("Click at least 2 point pairs to calibrate.");
        if (src.Count == 2) return FitSimilarity(src, dst);
        // least squares for each output row: [x y 1] * [a b c]^T = x'
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, n = src.Count;
        double bx1 = 0, bx2 = 0, bx3 = 0, by1 = 0, by2 = 0, by3 = 0;
        for (var i = 0; i < src.Count; i++)
        {
            var p = src[i]; var q = dst[i];
            sxx += p.X * p.X; sxy += p.X * p.Y; sx += p.X; syy += p.Y * p.Y; sy += p.Y;
            bx1 += p.X * q.X; bx2 += p.Y * q.X; bx3 += q.X;
            by1 += p.X * q.Y; by2 += p.Y * q.Y; by3 += q.Y;
        }
        var m = new[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var r1 = Solve3(m, bx1, bx2, bx3);
        var r2 = Solve3(m, by1, by2, by3);
        if (r1 is null || r2 is null) return FitSimilarity(src, dst);
        return new Affine2D(r1[0], r1[1], r1[2], r2[0], r2[1], r2[2]);
    }

    /// <summary>Least-squares similarity (Umeyama without reflection).</summary>
    public static Affine2D FitSimilarity(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        var n = src.Count;
        double mx = src.Average(p => p.X), my = src.Average(p => p.Y), nx = dst.Average(p => p.X), ny = dst.Average(p => p.Y);
        double a = 0, b = 0, ss = 0;
        for (var i = 0; i < n; i++)
        {
            double x = src[i].X - mx, y = src[i].Y - my, u = dst[i].X - nx, v = dst[i].Y - ny;
            a += x * u + y * v; b += x * v - y * u; ss += x * x + y * y;
        }
        if (ss < 1e-12) throw new InvalidOperationException("The calibration points are on top of each other.");
        a /= ss; b /= ss;
        return new Affine2D(a, -b, nx - (a * mx - b * my), b, a, ny - (b * mx + a * my));
    }

    private static double[]? Solve3(double[,] m, double b1, double b2, double b3)
    {
        double Det(double[,] x) =>
            x[0, 0] * (x[1, 1] * x[2, 2] - x[1, 2] * x[2, 1]) - x[0, 1] * (x[1, 0] * x[2, 2] - x[1, 2] * x[2, 0]) + x[0, 2] * (x[1, 0] * x[2, 1] - x[1, 1] * x[2, 0]);
        var d = Det(m);
        if (Math.Abs(d) < 1e-12 * Math.Max(1, Math.Abs(m[0, 0] * m[1, 1]))) return null;
        var res = new double[3];
        var bs = new[] { b1, b2, b3 };
        for (var c = 0; c < 3; c++)
        {
            var mc = (double[,])m.Clone();
            for (var r = 0; r < 3; r++) mc[r, c] = bs[r];
            res[c] = Det(mc) / d;
        }
        return res;
    }

    public string Serialize() => string.Join(",", new[] { A, B, C, D, E, F }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

    public static Affine2D? Parse(string? s)
    {
        var p = (s ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 6) return null;
        var v = new double[6];
        for (var i = 0; i < 6; i++) if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return new Affine2D(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    /// <summary>Residual (max distance) of the fit at the given pairs.</summary>
    public double MaxResidual(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        var t = this;
        return src.Count == 0 ? 0 : src.Select((p, i) => t.Apply(p).DistanceTo(dst[i])).Max();
    }
}

/// <summary>[drawings] Polygon helpers: point-in-polygon, area, clipping a segment to a polygon, text form.</summary>
public static class Poly
{
    /// <summary>Ray casting (even-odd).</summary>
    public static bool Contains(IReadOnlyList<PointD> poly, PointD p)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-300) + a.X) inside = !inside;
        }
        return inside;
    }

    public static double Area(IReadOnlyList<PointD> poly)
    {
        double s = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) s += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return Math.Abs(s / 2);
    }

    /// <summary>Length of the segment a-b that lies inside the polygon (exact: split at edge crossings, test each piece's midpoint).</summary>
    public static double LengthInside(IReadOnlyList<PointD> poly, PointD a, PointD b)
    {
        var ts = new List<double> { 0, 1 };
        var d = b - a;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var p = poly[j]; var e = poly[i] - p;
            var den = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(den) < 1e-15) continue;
            var w = p - a;
            var t = (w.X * e.Y - w.Y * e.X) / den;
            var u = (w.X * d.Y - w.Y * d.X) / den;
            if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
        }
        ts.Sort();
        double inside = 0; var len = d.Length;
        for (var k = 0; k + 1 < ts.Count; k++)
        {
            var mid = a + d * ((ts[k] + ts[k + 1]) / 2);
            if (Contains(poly, mid)) inside += (ts[k + 1] - ts[k]) * len;
        }
        return inside;
    }

    /// <summary>"x,y x,y ..." (invariant culture).</summary>
    public static string Format(IEnumerable<PointD> pts) => string.Join(" ", pts.Select(p => p.ToString()));

    public static List<PointD> ParsePoints(string? s)
    {
        var res = new List<PointD>();
        foreach (var tok in (s ?? "").Split(new[] { ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = tok.Split(',');
            if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                res.Add(new PointD(x, y));
        }
        return res;
    }

    /// <summary>Distance from p to segment a-b and the closest point.</summary>
    public static (double Distance, PointD Closest, double T) ToSegment(PointD p, PointD a, PointD b)
    {
        var d = b - a; var l2 = d.X * d.X + d.Y * d.Y;
        var t = l2 < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / l2, 0, 1);
        var c = a + d * t;
        return (p.DistanceTo(c), c, t);
    }

    public static double PolylineLength(IReadOnlyList<PointD> pts)
    {
        double s = 0;
        for (var i = 1; i < pts.Count; i++) s += pts[i].DistanceTo(pts[i - 1]);
        return s;
    }
}

/// <summary>[drawings] Small CSV helpers (invariant culture, quoted when needed).</summary>
public static class Csv
{
    public static string Line(params object?[] cells) => string.Join(",", cells.Select(Cell));

    public static string Cell(object? v)
    {
        var s = v switch
        {
            null => "",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Splits one CSV line (quotes honoured).</summary>
    public static List<string> Split(string line)
    {
        var res = new List<string>(); var sb = new StringBuilder(); var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}
