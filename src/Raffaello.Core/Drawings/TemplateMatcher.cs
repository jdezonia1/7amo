namespace Raffaello.Core.Drawings;

/// <summary>[drawings] One library symbol prepared for matching.</summary>
public sealed class SymbolTemplate
{
    public long SymbolId { get; init; }
    public string Name { get; init; } = "";
    public required GrayImage Image { get; init; }
    public bool AllowRotation { get; init; } = true;
    public bool AllowMirror { get; init; } = true;
    /// <summary>Per-symbol threshold (0 = use the run's).</summary>
    public double Threshold { get; init; }

    public static SymbolTemplate From(DwgSymbol s, int sheetDpi)
    {
        if (s.TemplatePng is not { Length: > 0 }) throw new InvalidOperationException($"Symbol {s.Name} has no example image.");
        var img = GrayImage.FromPng(s.TemplatePng);
        if (s.TemplateDpi > 0 && sheetDpi > 0 && s.TemplateDpi != sheetDpi)
        {
            var k = (double)sheetDpi / s.TemplateDpi;
            img = img.Resize(Math.Max(3, (int)Math.Round(img.Width * k)), Math.Max(3, (int)Math.Round(img.Height * k)));
        }
        return new SymbolTemplate { SymbolId = s.Id, Name = s.Name, Image = img, AllowRotation = s.AllowRotation, AllowMirror = s.AllowMirror, Threshold = s.Threshold };
    }
}

public sealed class MatchOptions
{
    /// <summary>Normalised cross-correlation needed for a hit (0..1).</summary>
    public double Threshold { get; set; } = 0.70;
    /// <summary>Template scales tried (1 = same size as the example).</summary>
    public double[] Scales { get; set; } = { 1.0 };
    public bool Rotations { get; set; } = true;
    public bool Mirror { get; set; } = true;
    /// <summary>Blur radius applied to sheet and template (tolerates scan jitter and line weight).</summary>
    public int BlurRadius { get; set; } = 1;
    /// <summary>Two hits overlapping more than this share of the smaller box are the same symbol (best score wins).</summary>
    public double NmsOverlap { get; set; } = 0.35;
    /// <summary>Only search inside this region (sheet pixels), e.g. the plan area without the title block.</summary>
    public RectD? Region { get; set; }
    public int MaxHits { get; set; } = 50000;
    /// <summary>Share of the template's strokes that must be inked on the sheet (0 = off).</summary>
    public double MinStrokeRecall { get; set; } = 0.75;
    /// <summary>Share of the ink inside the box that must belong to the template (0 = off).</summary>
    public double MinStrokePrecision { get; set; } = 0.55;
    public IProgress<string>? Progress { get; set; }
    public CancellationToken Cancel { get; set; }
}

/// <summary>[drawings] A symbol occurrence found on a sheet.</summary>
public sealed record Detection(long SymbolId, string Name, double X, double Y, double W, double H, double Score, int Rotation, bool Mirrored, double Scale)
{
    public PointD Center => new(X + W / 2, Y + H / 2);
    public RectD Box => new(X, Y, W, H);
}

/// <summary>
/// [drawings] Robust template matching on drawing rasters: normalised cross-correlation of blurred ink images, every template
/// variant (rotations 0/90/180/270, mirrored, several scales), coarse-to-fine (pyramid), ink-density pruning, and non-max
/// suppression across all symbols (a twin socket beats the single socket inside it because its correlation is higher).
/// Works on exploded AutoCAD output and scans alike because it only looks at the image.
/// </summary>
public static class TemplateMatcher
{
    private sealed class Variant
    {
        public required string Name;
        public long SymbolId;
        public int Rotation;
        public bool Mirrored;
        public double Scale;
        public double Threshold;
        public required float[] Full;       // ink 0..1, blurred, full resolution
        public required bool[] Ink;         // binary ink of the oriented template
        public required bool[] InkDil;      // ... dilated by 1 px
        public int W, H;
        public required float[] Coarse;
        public int CW, CH;
        public int Factor;
    }

    public static List<Detection> Match(GrayImage sheet, IReadOnlyList<SymbolTemplate> symbols, MatchOptions? options = null)
    {
        var o = options ?? new MatchOptions();
        if (symbols.Count == 0) return new();
        var variants = new List<Variant>();
        foreach (var s in symbols) variants.AddRange(Variants(s, o));
        if (variants.Count == 0) return new();

        // ink images per pyramid factor
        var factors = variants.Select(v => v.Factor).Distinct().ToList();
        var full = InkFloat(sheet, o.BlurRadius);
        var sheetInk = sheet.Ink(170);
        var sheetInkDil = sheetInk.Dilate(2);
        var coarse = new Dictionary<int, (float[] Img, int W, int H)>();
        foreach (var f in factors)
        {
            if (f == 1) { coarse[1] = (full, sheet.Width, sheet.Height); continue; }
            var g = sheet.Downscale(f);
            coarse[f] = (InkFloat(g, Math.Max(0, o.BlurRadius - 1) + 0), g.Width, g.Height);
        }

        var raw = new List<Detection>();
        var gate = new object();
        var done = 0;
        foreach (var v in variants)
        {
            o.Cancel.ThrowIfCancellationRequested();
            var (cimg, cw, ch) = coarse[v.Factor];
            var cands = CoarseCandidates(cimg, cw, ch, v, o);
            var found = new List<Detection>();
            Parallel.ForEach(cands, new ParallelOptions { CancellationToken = o.Cancel }, c =>
            {
                var best = Refine(full, sheet.Width, sheet.Height, v, c.X * v.Factor, c.Y * v.Factor, v.Factor + 1);
                if (best.Score >= v.Threshold && StrokesAgree(sheetInk, sheetInkDil, v, (int)best.X, (int)best.Y, o))
                    lock (found) found.Add(new Detection(v.SymbolId, v.Name, best.X, best.Y, v.W, v.H, best.Score, v.Rotation, v.Mirrored, v.Scale));
            });
            lock (gate) raw.AddRange(found);
            done++;
            o.Progress?.Report($"{v.Name} rot {v.Rotation}{(v.Mirrored ? " mirrored" : "")} x{v.Scale:0.##}: {found.Count} candidates ({done}/{variants.Count})");
        }
        return Suppress(raw, o.NmsOverlap).Take(o.MaxHits).ToList();
    }

    /// <summary>Greedy non-max suppression across symbols (grid-accelerated).</summary>
    public static List<Detection> Suppress(IEnumerable<Detection> raw, double overlap)
    {
        var sorted = raw.OrderByDescending(d => d.Score).ToList();
        if (sorted.Count == 0) return sorted;
        var cell = Math.Max(8, sorted.Max(d => Math.Max(d.W, d.H)));
        var grid = new Dictionary<(int, int), List<Detection>>();
        var kept = new List<Detection>();
        foreach (var d in sorted)
        {
            var cx = (int)(d.Center.X / cell); var cy = (int)(d.Center.Y / cell);
            var clash = false;
            for (var gx = cx - 1; gx <= cx + 1 && !clash; gx++)
                for (var gy = cy - 1; gy <= cy + 1 && !clash; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var list)) continue;
                    foreach (var k in list)
                    {
                        var share = d.Box.IntersectionArea(k.Box) / Math.Max(1e-9, Math.Min(d.Box.Area, k.Box.Area));
                        // same place, or a clearly weaker partial match leaning on a stronger symbol (a rotated switch on another switch)
                        if (share > overlap || (share > 0.08 && d.Score < k.Score - 0.05)) { clash = true; break; }
                    }
                }
            if (clash) continue;
            kept.Add(d);
            if (!grid.TryGetValue((cx, cy), out var l)) grid[(cx, cy)] = l = new List<Detection>();
            l.Add(d);
        }
        return kept;
    }

    /// <summary>Ink 0..1 (1 = black), blurred.</summary>
    internal static float[] InkFloat(GrayImage g, int blur)
    {
        var src = blur > 0 ? g.BoxBlur(blur) : g;
        var f = new float[src.Data.Length];
        for (var i = 0; i < f.Length; i++) f[i] = (255 - src.Data[i]) / 255f;
        return f;
    }

    /// <summary>Trims white margins (keeps 2 px) so a loosely drawn box around the example does not matter.</summary>
    public static GrayImage Trim(GrayImage t, byte inkThreshold = 200)
    {
        int x0 = t.Width, y0 = t.Height, x1 = -1, y1 = -1;
        for (var y = 0; y < t.Height; y++)
            for (var x = 0; x < t.Width; x++)
                if (t[x, y] < inkThreshold) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return t;
        x0 = Math.Max(0, x0 - 2); y0 = Math.Max(0, y0 - 2); x1 = Math.Min(t.Width - 1, x1 + 2); y1 = Math.Min(t.Height - 1, y1 + 2);
        return t.Crop(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>
    /// Keeps only the ink that belongs to the symbol in the middle of the user's box: strokes that touch the box edge (a conduit or
    /// wall running through) and marks away from the centre (a room number, a leader) are painted white.
    /// </summary>
    public static GrayImage Isolate(GrayImage t, byte inkThreshold = 200)
    {
        var ink = t.Ink(inkThreshold);
        var merged = ink.Dilate(1);
        var comps = merged.Components(1);
        if (comps.Count <= 1) return t;
        double cx0 = t.Width * 0.3, cx1 = t.Width * 0.7, cy0 = t.Height * 0.3, cy1 = t.Height * 0.7;
        var keep = new BitMask(t.Width, t.Height);
        var centre = comps.Where(c => c.X <= cx1 && c.X + c.W >= cx0 && c.Y <= cy1 && c.Y + c.H >= cy0).ToList();
        if (centre.Count == 0) return t;
        foreach (var c in centre)
        {
            var touchesEdge = c.X <= 0 || c.Y <= 0 || c.X + c.W >= t.Width || c.Y + c.H >= t.Height;
            // a stroke through the whole box is a wall / conduit, unless it is the only thing in the middle
            if (touchesEdge && centre.Count > 1 && (c.W >= t.Width - 1 || c.H >= t.Height - 1)) continue;
            foreach (var p in c.Pixels) keep.Bits[p] = true;
        }
        var o = t.Clone();
        for (var i = 0; i < o.Data.Length; i++) if (!keep.Bits[i] || !ink.Bits[i]) o.Data[i] = keep.Bits[i] ? o.Data[i] : (byte)255;
        // strokes of a kept component that run out of the box (a conduit ending at the symbol) are cut at a margin
        return o;
    }

    private static IEnumerable<Variant> Variants(SymbolTemplate s, MatchOptions o)
    {
        var baseImg = Trim(Isolate(s.Image));
        var seen = new List<(int W, int H, float[] Ink)>();
        var thr = s.Threshold > 0 ? s.Threshold : o.Threshold;
        foreach (var scale in o.Scales.DefaultIfEmpty(1.0))
        {
            var img = Math.Abs(scale - 1) < 1e-6 ? baseImg : baseImg.Resize(Math.Max(3, (int)Math.Round(baseImg.Width * scale)), Math.Max(3, (int)Math.Round(baseImg.Height * scale)));
            var mirrors = o.Mirror && s.AllowMirror ? new[] { false, true } : new[] { false };
            var rots = o.Rotations && s.AllowRotation ? new[] { 0, 1, 2, 3 } : new[] { 0 };
            foreach (var m in mirrors)
                foreach (var r in rots)
                {
                    var oriented = img.Orient(r, m);
                    var ink = InkFloat(oriented, o.BlurRadius);
                    var bin = oriented.Ink(170);
                    if (seen.Any(x => x.W == oriented.Width && x.H == oriented.Height && MeanAbsDiff(x.Ink, ink) < 0.01)) continue;
                    seen.Add((oriented.Width, oriented.Height, ink));
                    var f = Math.Clamp(Math.Min(oriented.Width, oriented.Height) / 10, 1, 4);
                    var cimg = f == 1 ? oriented : oriented.Resize(Math.Max(2, oriented.Width / f), Math.Max(2, oriented.Height / f));
                    yield return new Variant
                    {
                        Name = s.Name, SymbolId = s.SymbolId, Rotation = r * 90, Mirrored = m, Scale = scale, Threshold = thr,
                        Full = ink, W = oriented.Width, H = oriented.Height, Ink = bin.Bits, InkDil = bin.Dilate(2).Bits,
                        Coarse = f == 1 ? ink : InkFloat(cimg, Math.Max(0, o.BlurRadius - 1)), CW = cimg.Width, CH = cimg.Height, Factor = f,
                    };
                }
        }
    }

    private static double MeanAbsDiff(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    private readonly record struct Cand(int X, int Y, float Score);

    /// <summary>NCC over the coarse image, tile by tile; local maxima above (threshold - 0.15).</summary>
    private static List<Cand> CoarseCandidates(float[] img, int w, int h, Variant v, MatchOptions o)
    {
        int tw = v.CW, th = v.CH, n = tw * th;
        if (tw > w || th > h) return new();
        double st = 0, st2 = 0;
        var sparse = new List<(int Off, float T)>();
        for (var y = 0; y < th; y++)
            for (var x = 0; x < tw; x++)
            {
                var t = v.Coarse[y * tw + x];
                st += t; st2 += t * t;
                if (t > 0.02f) sparse.Add((y * w + x, t));
            }
        var varT = st2 - st * st / n;
        if (varT < 1e-6 || sparse.Count == 0) return new();
        var offs = sparse.Select(s => s.Off).ToArray();
        var vals = sparse.Select(s => s.T).ToArray();
        var cthr = (float)Math.Max(0.3, v.Threshold - 0.15);

        // search window (region) in coarse pixels
        int rx0 = 0, ry0 = 0, rx1 = w - tw, ry1 = h - th;
        if (o.Region is { } reg)
        {
            rx0 = Math.Max(0, (int)(reg.X / v.Factor)); ry0 = Math.Max(0, (int)(reg.Y / v.Factor));
            rx1 = Math.Min(w - tw, (int)(reg.Right / v.Factor) - tw); ry1 = Math.Min(h - th, (int)(reg.Bottom / v.Factor) - th);
        }
        if (rx1 < rx0 || ry1 < ry0) return new();
        const int tile = 256;
        var tiles = new List<(int X, int Y)>();
        for (var ty = ry0; ty <= ry1; ty += tile)
            for (var tx = rx0; tx <= rx1; tx += tile) tiles.Add((tx, ty));
        var res = new List<Cand>();
        Parallel.ForEach(tiles, new ParallelOptions { CancellationToken = o.Cancel }, t =>
        {
            int pw = Math.Min(tile, rx1 - t.X + 1), ph = Math.Min(tile, ry1 - t.Y + 1);
            // integral images of the patch that the tile's windows cover
            int iw = pw + tw - 1, ih = ph + th - 1;
            var S = new double[(iw + 1) * (ih + 1)];
            var S2 = new double[(iw + 1) * (ih + 1)];
            for (var y = 0; y < ih; y++)
            {
                double rs = 0, rs2 = 0;
                var srow = (t.Y + y) * w + t.X;
                for (var x = 0; x < iw; x++)
                {
                    var val = img[srow + x];
                    rs += val; rs2 += val * val;
                    S[(y + 1) * (iw + 1) + x + 1] = S[y * (iw + 1) + x + 1] + rs;
                    S2[(y + 1) * (iw + 1) + x + 1] = S2[y * (iw + 1) + x + 1] + rs2;
                }
            }
            var score = new float[pw * ph];
            for (var y = 0; y < ph; y++)
                for (var x = 0; x < pw; x++)
                {
                    int a = y * (iw + 1) + x, b = a + tw, c = (y + th) * (iw + 1) + x, d = c + tw;
                    var si = S[d] - S[b] - S[c] + S[a];
                    if (si < 0.3 * st || si > 3.0 * st) { score[y * pw + x] = -1; continue; }
                    var varI = S2[d] - S2[b] - S2[c] + S2[a] - si * si / n;
                    if (varI < 1e-6) { score[y * pw + x] = -1; continue; }
                    var basep = (t.Y + y) * w + t.X + x;
                    double dot = 0;
                    for (var k = 0; k < offs.Length; k++) dot += img[basep + offs[k]] * vals[k];
                    score[y * pw + x] = (float)((dot - si * st / n) / Math.Sqrt(varI * varT));
                }
            var local = new List<Cand>();
            for (var y = 0; y < ph; y++)
                for (var x = 0; x < pw; x++)
                {
                    var s = score[y * pw + x];
                    if (s < cthr) continue;
                    var isMax = true;
                    for (var dy = -1; dy <= 1 && isMax; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                            var ns = score[ny * pw + nx];
                            if (ns > s || (ns == s && (dy < 0 || (dy == 0 && dx < 0)))) { isMax = false; break; }
                        }
                    if (isMax) local.Add(new Cand(t.X + x, t.Y + y, s));
                }
            lock (res) res.AddRange(local);
        });
        return res;
    }

    /// <summary>
    /// Second check on a hit: most template strokes must be inked on the sheet (recall) and most ink inside the box must belong to
    /// the template (precision, a 2 px border is ignored so conduits running into the symbol do not count). Rejects partial matches
    /// on text glyphs and line corners that blurred correlation alone accepts.
    /// </summary>
    private static bool StrokesAgree(BitMask ink, BitMask inkDil, Variant v, int x, int y, MatchOptions o)
    {
        if (o.MinStrokeRecall <= 0 && o.MinStrokePrecision <= 0) return true;
        int tIn = 0, tHit = 0, iIn = 0, iHit = 0;
        for (var j = 0; j < v.H; j++)
            for (var i = 0; i < v.W; i++)
            {
                var k = j * v.W + i;
                if (v.Ink[k]) { tIn++; if (inkDil[x + i, y + j]) tHit++; }
                if (i < 2 || j < 2 || i >= v.W - 2 || j >= v.H - 2) continue;
                if (ink[x + i, y + j]) { iIn++; if (v.InkDil[k]) iHit++; }
            }
        if (tIn == 0) return false;
        var recall = (double)tHit / tIn;
        var precision = iIn == 0 ? 0 : (double)iHit / iIn;
        return recall >= o.MinStrokeRecall && precision >= o.MinStrokePrecision;
    }

    /// <summary>Exact NCC at full resolution in a small window around a coarse candidate.</summary>
    private static (double X, double Y, double Score) Refine(float[] img, int w, int h, Variant v, int cx, int cy, int radius)
    {
        int tw = v.W, th = v.H, n = tw * th;
        double st = 0, st2 = 0;
        for (var i = 0; i < v.Full.Length; i++) { st += v.Full[i]; st2 += v.Full[i] * v.Full[i]; }
        var varT = st2 - st * st / n;
        (double X, double Y, double Score) best = (cx, cy, -1);
        for (var y = cy - radius; y <= cy + radius; y++)
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (x < 0 || y < 0 || x + tw > w || y + th > h) continue;
                double si = 0, si2 = 0, dot = 0;
                for (var j = 0; j < th; j++)
                {
                    var row = (y + j) * w + x; var trow = j * tw;
                    for (var i = 0; i < tw; i++)
                    {
                        var a = img[row + i];
                        si += a; si2 += a * a; dot += a * v.Full[trow + i];
                    }
                }
                var varI = si2 - si * si / n;
                if (varI < 1e-6 || varT < 1e-6) continue;
                var s = (dot - si * st / n) / Math.Sqrt(varI * varT);
                if (s > best.Score) best = (x, y, s);
            }
        return best;
    }

    /// <summary>NCC of a template against one location (used by the review UI to score a manually added hit).</summary>
    public static double ScoreAt(GrayImage sheet, GrayImage template, int x, int y, int blur = 1)
    {
        var t = InkFloat(template, blur);
        var patch = InkFloat(sheet.Crop(x, y, template.Width, template.Height), blur);
        double st = 0, st2 = 0, si = 0, si2 = 0, dot = 0; var n = t.Length;
        for (var i = 0; i < n; i++) { st += t[i]; st2 += t[i] * t[i]; si += patch[i]; si2 += patch[i] * patch[i]; dot += t[i] * patch[i]; }
        var d = Math.Sqrt((st2 - st * st / n) * (si2 - si * si / n));
        return d < 1e-9 ? 0 : (dot - si * st / n) / d;
    }
}
