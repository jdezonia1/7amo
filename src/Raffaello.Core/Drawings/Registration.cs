using System.Numerics;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Result of aligning a moving image (scan / new revision) to a fixed one (clean drawing / old revision).</summary>
public sealed record RegistrationResult(Affine2D MovingToFixed, double Score, double Scale, double RotationDeg, int QuarterTurns)
{
    /// <summary>Correlation of the aligned ink images (0..1); below ~0.25 the alignment should be checked or calibrated by hand.</summary>
    public bool Reliable => Score >= 0.25;
}

public sealed class RegistrationOptions
{
    public double MinScale { get; set; } = 0.80;
    public double MaxScale { get; set; } = 1.25;
    public double MaxRotationDeg { get; set; } = 2.0;
    /// <summary>Also try the page turned by 90 / 180 / 270 degrees (scans fed sideways).</summary>
    public bool TryQuarterTurns { get; set; }
    public int CoarseSize { get; set; } = 256;
    public int FineSize { get; set; } = 512;
}

/// <summary>
/// [drawings] Automatic alignment of two drawings of the same sheet: ink images are reduced, then a search over scale and small
/// rotations uses FFT phase correlation for the shift and normalised correlation of the overlapped ink as the score; the best
/// candidate is refined at a finer resolution. Highlighter colours are removed first (<see cref="ColorImage.ToInkGray"/>).
/// When a page is too different (heavy handwriting, a different crop) the user clicks 2-3 point pairs instead.
/// </summary>
public static class Registration
{
    public static RegistrationResult Align(GrayImage fixedImg, GrayImage movingImg, RegistrationOptions? options = null)
    {
        var o = options ?? new RegistrationOptions();
        // common working scale: the larger side of the fixed image becomes CoarseSize
        var coarse = Search(fixedImg, movingImg, o.CoarseSize, o.MinScale, o.MaxScale, 0.03, o.MaxRotationDeg, 1.0, o.TryQuarterTurns ? new[] { 0, 1, 2, 3 } : new[] { 0 });
        var fine = Search(fixedImg, movingImg, o.FineSize, coarse.Scale * 0.97, coarse.Scale * 1.03, 0.005, 0.0, 0.25, new[] { coarse.QuarterTurns }, coarse.RotationDeg, 0.5);
        return fine.Score >= coarse.Score * 0.8 ? fine : coarse;
    }

    private static RegistrationResult Search(GrayImage fixedImg, GrayImage movingImg, int size, double sMin, double sMax, double sStep,
        double maxRot, double rotStep, int[] quarters, double rotCenter = 0, double rotSpan = -1)
    {
        var k = (double)size / Math.Max(fixedImg.Width, fixedImg.Height);
        int n = 1; while (n < size) n <<= 1;
        var fixedSmall = Reduce(fixedImg, k);
        var F = Fft2(Pad(fixedSmall, n), n);
        var fixedInk = fixedSmall;
        var span = rotSpan >= 0 ? rotSpan : maxRot;
        RegistrationResult best = new(Affine2D.Identity, -1, 1, 0, 0);
        var movingInk = Reduce(movingImg, k);
        var lockObj = new object();
        var jobs = new List<(int Q, double S, double R)>();
        foreach (var q in quarters)
            for (var s = sMin; s <= sMax + 1e-9; s *= 1 + sStep)
                for (var r = rotCenter - span; r <= rotCenter + span + 1e-9; r += Math.Max(rotStep, 1e-6))
                    jobs.Add((q, s, r));
        Parallel.ForEach(jobs, job =>
        {
            // moving (reduced) -> fixed (reduced): rotate/scale about the origin, then shift found by phase correlation
            var turned = movingInk.Orient(job.Q, false);
            var qt = QuarterTransform(job.Q, movingInk.GetLength(1), movingInk.GetLength(0));
            var rs = Affine2D.Similarity(job.S, job.R, 0, 0);
            var warped = WarpInk(turned, rs, n, n);
            var M = Fft2(warped, n);
            var (dx, dy, _) = PhaseCorrelation(F, M, n);
            var t = new Affine2D(rs.A, rs.B, rs.C + dx, rs.D, rs.E, rs.F + dy);
            var score = Overlap(fixedInk, turned, t, n);
            lock (lockObj)
            {
                if (score > best.Score)
                {
                    // full-resolution transform: moving -> reduced moving -> quarter turn -> t -> reduced fixed -> fixed
                    var full = Affine2D.ScaleOnly(1 / k, 1 / k).After(t).After(qt).After(Affine2D.ScaleOnly(k, k));
                    best = new RegistrationResult(full, score, job.S, job.R, job.Q);
                }
            }
        });
        return best;
    }

    /// <summary>Pixel transform of <see cref="GrayImage.Orient"/> (clockwise quarter turns, no mirror).</summary>
    private static Affine2D QuarterTransform(int q, int w, int h) => (((q % 4) + 4) % 4) switch
    {
        0 => Affine2D.Identity,
        1 => new Affine2D(0, -1, h - 1, 1, 0, 0),
        2 => new Affine2D(-1, 0, w - 1, 0, -1, h - 1),
        _ => new Affine2D(0, 1, 0, -1, 0, w - 1),
    };

    /// <summary>Reduced ink image (0 = paper, 1 = ink), blurred a little.</summary>
    private static float[,] Reduce(GrayImage g, double k)
    {
        var w = Math.Max(8, (int)Math.Round(g.Width * k)); var h = Math.Max(8, (int)Math.Round(g.Height * k));
        var small = g.Resize(w, h).BoxBlur(1);
        var a = new float[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) a[y, x] = (255 - small[x, y]) / 255f;
        return a;
    }

    private static GrayImageView Orient(this float[,] a, int q, bool _) => new(a, q);

    /// <summary>A float ink image seen after q clockwise quarter turns.</summary>
    private readonly struct GrayImageView
    {
        private readonly float[,] _a; private readonly int _q;
        public GrayImageView(float[,] a, int q) { _a = a; _q = ((q % 4) + 4) % 4; }
        public int Width => _q % 2 == 0 ? _a.GetLength(1) : _a.GetLength(0);
        public int Height => _q % 2 == 0 ? _a.GetLength(0) : _a.GetLength(1);
        public float this[int x, int y]
        {
            get
            {
                int w0 = _a.GetLength(1), h0 = _a.GetLength(0);
                int sx, sy;
                switch (_q)
                {
                    case 0: sx = x; sy = y; break;
                    case 1: sx = y; sy = h0 - 1 - x; break;
                    case 2: sx = w0 - 1 - x; sy = h0 - 1 - y; break;
                    default: sx = w0 - 1 - y; sy = x; break;
                }
                return sx < 0 || sy < 0 || sx >= w0 || sy >= h0 ? 0 : _a[sy, sx];
            }
        }
        public float Sample(double x, double y)
        {
            var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
            float fx = (float)(x - x0), fy = (float)(y - y0);
            return (this[x0, y0] * (1 - fx) + this[x0 + 1, y0] * fx) * (1 - fy) + (this[x0, y0 + 1] * (1 - fx) + this[x0 + 1, y0 + 1] * fx) * fy;
        }
    }

    private static Complex[,] Pad(float[,] a, int n)
    {
        var c = new Complex[n, n];
        for (var y = 0; y < Math.Min(n, a.GetLength(0)); y++)
            for (var x = 0; x < Math.Min(n, a.GetLength(1)); x++) c[y, x] = a[y, x];
        return c;
    }

    private static Complex[,] WarpInk(GrayImageView src, Affine2D t, int w, int h)
    {
        var inv = t.Inverse();
        var c = new Complex[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                c[y, x] = src.Sample(p.X, p.Y);
            }
        return c;
    }

    /// <summary>NCC of fixed vs warped moving over the fixed image area.</summary>
    private static double Overlap(float[,] fixedInk, GrayImageView moving, Affine2D t, int n)
    {
        var inv = t.Inverse();
        int h = fixedInk.GetLength(0), w = fixedInk.GetLength(1);
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0; var cnt = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                double a = fixedInk[y, x], b = moving.Sample(p.X, p.Y);
                sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b; cnt++;
            }
        var cov = sab - sa * sb / cnt; var va = saa - sa * sa / cnt; var vb = sbb - sb * sb / cnt;
        return va <= 1e-9 || vb <= 1e-9 ? 0 : cov / Math.Sqrt(va * vb);
    }

    /// <summary>Shift (dx, dy) that moves M onto F, with sub-pixel peak refinement.</summary>
    private static (double Dx, double Dy, double Peak) PhaseCorrelation(Complex[,] F, Complex[,] M, int n)
    {
        var R = new Complex[n, n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var c = F[y, x] * Complex.Conjugate(M[y, x]);
                var mag = c.Magnitude;
                R[y, x] = mag < 1e-12 ? Complex.Zero : c / mag;
            }
        var r = Fft2(R, n, inverse: true);
        double best = double.MinValue; int bx = 0, by = 0;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (r[y, x].Real > best) { best = r[y, x].Real; bx = x; by = y; }
        double Sub(double l, double c0, double rr) { var d = l - 2 * c0 + rr; return Math.Abs(d) < 1e-12 ? 0 : 0.5 * (l - rr) / d; }
        var ox = Sub(r[by, (bx - 1 + n) % n].Real, best, r[by, (bx + 1) % n].Real);
        var oy = Sub(r[(by - 1 + n) % n, bx].Real, best, r[(by + 1) % n, bx].Real);
        double dx = bx + ox, dy = by + oy;
        if (dx > n / 2.0) dx -= n;
        if (dy > n / 2.0) dy -= n;
        return (dx, dy, best);
    }

    private static Complex[,] Fft2(Complex[,] a, int n, bool inverse = false)
    {
        var o = (Complex[,])a.Clone();
        var row = new Complex[n];
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++) row[x] = o[y, x];
            Fft(row, inverse);
            for (var x = 0; x < n; x++) o[y, x] = row[x];
        }
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++) row[y] = o[y, x];
            Fft(row, inverse);
            for (var y = 0; y < n; y++) o[y, x] = row[y];
        }
        return o;
    }

    private static Complex[,] Fft2(float[,] a, int n) => Fft2(Pad(a, n), n);

    /// <summary>In-place iterative radix-2 FFT (inverse scaled by 1/n).</summary>
    private static void Fft(Complex[] a, bool inverse)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var j = 0; j < len / 2; j++)
                {
                    var u = a[i + j]; var v = a[i + j + len / 2] * w;
                    a[i + j] = u + v; a[i + j + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
        if (inverse) for (var i = 0; i < n; i++) a[i] /= n;
    }

    /// <summary>Warps a grey image into the fixed frame (white outside).</summary>
    public static GrayImage Warp(GrayImage moving, Affine2D movingToFixed, int w, int h)
    {
        var inv = movingToFixed.Inverse();
        var o = new GrayImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                o.Data[y * w + x] = (byte)Math.Clamp(Math.Round(moving.Sample(p.X, p.Y)), 0, 255);
            }
        });
        return o;
    }

    /// <summary>Warps a mask into the fixed frame (nearest neighbour).</summary>
    public static BitMask Warp(BitMask moving, Affine2D movingToFixed, int w, int h)
    {
        var inv = movingToFixed.Inverse();
        var o = new BitMask(w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                o.Bits[y * w + x] = moving[(int)Math.Round(p.X), (int)Math.Round(p.Y)];
            }
        });
        return o;
    }
}
