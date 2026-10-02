namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] Synthetic electrical drawings with known answers (no company data): rooms with walls, conduit lines, room labels,
/// symbols at random rotations / mirrored, plus simulated subcontractor markups (scanned, skewed, green highlighter) and revisions.
/// Used by the tests and by <c>raffaello-cli drawings-demo</c> to measure precision / recall.
/// </summary>
public static class Synthetic
{
    public static readonly string[] SymbolTypes = { "SOCKET", "TWIN SOCKET", "LIGHT", "DOWNLIGHT", "DATA", "GRMS CP-4", "SWITCH 1G", "SWITCH 2G", "ISOLATOR" };

    /// <summary>Default PROJECT QTY mapping of the synthetic symbols (house rules: twin socket = 1, switches under LIGHT).</summary>
    public static string TargetsOf(string type) => type switch
    {
        "SOCKET" => "1ST FIX|POWER|1;2ND FIX|POWER|1",
        "TWIN SOCKET" => "1ST FIX|POWER|1;2ND FIX|POWER|1",
        "LIGHT" or "DOWNLIGHT" => "1ST FIX|LIGHT|1;2ND FIX|LIGHT|1",
        "DATA" => "1ST FIX|DATA|1;2ND FIX|DATA|1",
        "GRMS CP-4" => "1ST FIX|GRMS|1;2ND FIX|GRMS|1",
        "SWITCH 1G" or "SWITCH 2G" => "1ST FIX|LIGHT|1;2ND FIX|LIGHT|1",
        "ISOLATOR" => "1ST FIX|POWER|1",
        _ => "",
    };

    public static string SystemOf(string type) => type switch
    {
        "SOCKET" or "TWIN SOCKET" or "ISOLATOR" => "POWER",
        "DATA" => "DATA",
        "GRMS CP-4" => "GRMS",
        _ => "LIGHT",
    };

    public sealed record SymbolPlacement(string Type, double Cx, double Cy, int Quarter, bool Mirror, string Room);
    public sealed record RoomBox(string Name, double X, double Y, double W, double H)
    {
        public List<PointD> Polygon => new() { new(X, Y), new(X + W, Y), new(X + W, Y + H), new(X, Y + H) };
    }

    public sealed class Scene
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public double SymbolSize { get; init; }
        public List<RoomBox> Rooms { get; init; } = new();
        public List<(PointD A, PointD B, double Thickness, Rgb Colour)> Lines { get; init; } = new();
        public List<SymbolPlacement> Symbols { get; init; } = new();
        public List<(double X, double Y, string Text)> Labels { get; init; } = new();
        /// <summary>Conduit runs (polylines, sheet px) - for the length tests.</summary>
        public List<List<PointD>> Conduits { get; init; } = new();
        public int Dpi { get; init; } = 150;
        public double ScaleDenominator { get; init; } = 50;

        public Scene Clone() => new()
        {
            Width = Width, Height = Height, SymbolSize = SymbolSize, Dpi = Dpi, ScaleDenominator = ScaleDenominator,
            Rooms = Rooms.ToList(), Lines = Lines.ToList(), Symbols = Symbols.ToList(), Labels = Labels.ToList(), Conduits = Conduits.Select(c => c.ToList()).ToList(),
        };

        public double MetresPerPixel => DwgSheet.MetresPerPixel(Dpi, ScaleDenominator);
    }

    /// <summary>Paints one symbol (vector definition in a unit box, mirrored then turned clockwise by quarter turns).</summary>
    public static void DrawSymbol(ColorImage img, string type, double cx, double cy, double size, int quarter, bool mirror, Rgb ink, double stroke = 1.6)
    {
        var h = size / 2;
        PointD T(double u, double v)
        {
            if (mirror) u = -u;
            for (var q = 0; q < ((quarter % 4) + 4) % 4; q++) (u, v) = (-v, u);
            return new PointD(cx + u * h, cy + v * h);
        }
        void L(double u0, double v0, double u1, double v1) { var a = T(u0, v0); var b = T(u1, v1); img.Line(a.X, a.Y, b.X, b.Y, ink, stroke); }
        void Arc(double uc, double vc, double r, double a0, double a1)
        {
            const int n = 24;
            for (var i = 0; i < n; i++)
            {
                double t0 = a0 + (a1 - a0) * i / n, t1 = a0 + (a1 - a0) * (i + 1) / n;
                L(uc + r * Math.Cos(t0), vc + r * Math.Sin(t0), uc + r * Math.Cos(t1), vc + r * Math.Sin(t1));
            }
        }
        void Dot(double u, double v, double r) { var p = T(u, v); img.Disc(p.X, p.Y, r * h, ink); }
        switch (type)
        {
            case "SOCKET":
                Arc(0, 0.25, 0.65, Math.PI, 2 * Math.PI); L(-0.65, 0.25, 0.65, 0.25); L(0, -0.4, 0, -0.95); break;
            case "TWIN SOCKET":
                Arc(0, 0.25, 0.65, Math.PI, 2 * Math.PI); L(-0.65, 0.25, 0.65, 0.25); L(-0.3, -0.33, -0.3, -0.95); L(0.3, -0.33, 0.3, -0.95); break;
            case "LIGHT":
                Arc(0, 0, 0.7, 0, 2 * Math.PI); L(-0.5, -0.5, 0.5, 0.5); L(-0.5, 0.5, 0.5, -0.5); break;
            case "DOWNLIGHT":
                Arc(0, 0, 0.7, 0, 2 * Math.PI); Dot(0, 0, 0.32); break;
            case "DATA":
                L(0, -0.8, 0.75, 0.6); L(0.75, 0.6, -0.75, 0.6); L(-0.75, 0.6, 0, -0.8); L(0, -0.05, 0, 0.6); break;
            case "GRMS CP-4":
                L(-0.7, -0.7, 0.7, -0.7); L(0.7, -0.7, 0.7, 0.7); L(0.7, 0.7, -0.7, 0.7); L(-0.7, 0.7, -0.7, -0.7); L(-0.4, -0.35, 0.4, -0.35); L(0, -0.35, 0, 0.45); break;
            case "SWITCH 1G":
                Dot(-0.4, 0.4, 0.28); L(-0.4, 0.4, 0.55, -0.55); L(0.55, -0.55, 0.85, -0.25); break;
            case "SWITCH 2G":
                Dot(-0.4, 0.4, 0.28); L(-0.4, 0.4, 0.55, -0.55); L(0.55, -0.55, 0.85, -0.25); L(0.15, -0.15, 0.45, 0.15); break;
            case "ISOLATOR":
                L(-0.8, -0.4, 0.8, -0.4); L(0.8, -0.4, 0.8, 0.4); L(0.8, 0.4, -0.8, 0.4); L(-0.8, 0.4, -0.8, -0.4); L(-0.8, 0.4, 0.8, -0.4); break;
            default:
                Arc(0, 0, 0.6, 0, 2 * Math.PI); break;
        }
    }

    /// <summary>A plan of rooms in a grid with symbols, conduits and labels. Deterministic for a seed.</summary>
    public static Scene Generate(int seed, int width = 2400, int height = 1700, int roomsX = 4, int roomsY = 3, int symbolsPerRoom = 14, double symbolSize = 24)
    {
        var rnd = new Random(seed);
        var scene = new Scene { Width = width, Height = height, SymbolSize = symbolSize };
        double mx = 60, my = 60, rw = (width - 2 * mx) / roomsX, rh = (height - 2 * my) / roomsY;
        var black = Rgb.Black; var blue = new Rgb(30, 60, 200);
        var n = 0;
        for (var j = 0; j < roomsY; j++)
            for (var i = 0; i < roomsX; i++)
            {
                var room = new RoomBox($"R{++n:00}", mx + i * rw, my + j * rh, rw, rh);
                scene.Rooms.Add(room);
                scene.Labels.Add((room.X + 14, room.Y + 14, room.Name));
            }
        foreach (var r in scene.Rooms)
            foreach (var (a, b) in Edges(r.Polygon)) scene.Lines.Add((a, b, 5, new Rgb(40, 40, 40)));
        foreach (var r in scene.Rooms)
        {
            var placed = new List<PointD>();
            var tries = 0;
            while (placed.Count < symbolsPerRoom && tries++ < 2000)
            {
                var p = new PointD(r.X + symbolSize * 1.6 + rnd.NextDouble() * (r.W - symbolSize * 3.2), r.Y + symbolSize * 2.4 + rnd.NextDouble() * (r.H - symbolSize * 4));
                if (placed.Any(q => q.DistanceTo(p) < symbolSize * 2.0)) continue;
                placed.Add(p);
                var type = SymbolTypes[rnd.Next(SymbolTypes.Length)];
                scene.Symbols.Add(new SymbolPlacement(type, p.X, p.Y, rnd.Next(4), rnd.Next(3) == 0, r.Name));
            }
            // conduits: from a room corner box along L-shaped routes to a few symbols (stopping at the symbol edge)
            var start = new PointD(r.X + 10, r.Y + r.H - 10);
            foreach (var s in scene.Symbols.Where(s => s.Room == r.Name).Take(5))
            {
                var end = new PointD(s.Cx - symbolSize * 0.75, s.Cy);
                var mid = new PointD(start.X + 6 + rnd.Next(10), end.Y);
                var poly = new List<PointD> { start, new(mid.X, start.Y), mid, end };
                scene.Conduits.Add(poly);
                for (var k = 1; k < poly.Count; k++) scene.Lines.Add((poly[k - 1], poly[k], 1.4, blue));
            }
            // a few random lines through the room (dimension lines, furniture)
            for (var k = 0; k < 3; k++)
            {
                var y = r.Y + rnd.NextDouble() * r.H;
                scene.Lines.Add((new PointD(r.X + 5, y), new PointD(r.X + r.W * (0.3 + rnd.NextDouble() * 0.6), y), 1, black));
            }
            for (int k = 0, t = 0; k < 4 && t < 200; t++)
            {
                var lx = r.X + 20 + rnd.NextDouble() * (r.W - 80); var ly = r.Y + 30 + rnd.NextDouble() * (r.H - 60);
                if (placed.Any(q => Math.Abs(q.X - (lx + 10)) < symbolSize * 1.6 && Math.Abs(q.Y - (ly + 5)) < symbolSize * 1.3)) continue;
                scene.Labels.Add((lx, ly, rnd.Next(100, 999).ToString()));
                k++;
            }
        }
        return scene;
    }

    private static IEnumerable<(PointD, PointD)> Edges(List<PointD> poly)
    {
        for (var i = 0; i < poly.Count; i++) yield return (poly[i], poly[(i + 1) % poly.Count]);
    }

    public static ColorImage Render(Scene s)
    {
        var img = new ColorImage(s.Width, s.Height);
        foreach (var (a, b, t, c) in s.Lines) img.Line(a.X, a.Y, b.X, b.Y, c, t);
        foreach (var (x, y, text) in s.Labels) img.Text(x, y, text, Rgb.Black, 2);
        foreach (var p in s.Symbols) DrawSymbol(img, p.Type, p.Cx, p.Cy, s.SymbolSize, p.Quarter, p.Mirror, Rgb.Black);
        return img;
    }

    /// <summary>One clean example of each symbol type, cut from the sheet like the user would (box around an un-rotated instance).</summary>
    public static Dictionary<string, GrayImage> Examples(Scene s, GrayImage sheet)
    {
        var res = new Dictionary<string, GrayImage>();
        var box = (int)Math.Ceiling(s.SymbolSize * 1.25);
        foreach (var t in SymbolTypes)
        {
            var inst = s.Symbols.FirstOrDefault(p => p.Type == t && p.Quarter == 0 && !p.Mirror);
            if (inst != null) { res[t] = sheet.Crop((int)Math.Round(inst.Cx - box / 2.0), (int)Math.Round(inst.Cy - box / 2.0), box, box); continue; }
            var c = new ColorImage(box, box);
            DrawSymbol(c, t, box / 2.0, box / 2.0, s.SymbolSize, 0, false, Rgb.Black);
            res[t] = c.ToGray();
        }
        return res;
    }

    public static List<DwgSymbol> Library(Scene s, GrayImage sheet)
    {
        var i = 0;
        return Examples(s, sheet).Select(kv => new DwgSymbol
        {
            Id = ++i, Name = kv.Key, System = SystemOf(kv.Key), Item = SystemOf(kv.Key), Tag = kv.Key, TemplatePng = kv.Value.ToPng(), TemplateDpi = s.Dpi,
            Mount = kv.Key is "LIGHT" or "DOWNLIGHT" ? "C" : "W", IsLightFitting = kv.Key is "LIGHT" or "DOWNLIGHT",
            ColourHex = Rgb.Palette(i - 1).Hex,
        }).ToList();
    }

    public sealed record ScanResult(ColorImage Page, Affine2D SheetToPage, List<SymbolPlacement> Highlighted, double HighlightedRunPx, List<List<PointD>> HighlightedRuns);

    /// <summary>
    /// Simulates a subcontractor's marked-up statement: green highlighter discs on a share of the symbols, highlighted conduit runs,
    /// a highlighted note in the margin, then printed + scanned (scale, small rotation, shift, noise).
    /// </summary>
    public static ScanResult Markup(Scene s, int seed, double share = 0.6, double scale = 0.96, double rotationDeg = 0.7, double shiftX = 30, double shiftY = -20, int runs = 3)
    {
        var rnd = new Random(seed);
        var img = Render(s);
        var hl = new List<SymbolPlacement>();
        foreach (var p in s.Symbols)
        {
            if (rnd.NextDouble() >= share) continue;
            hl.Add(p);
            img.HighlightDisc(p.Cx + (rnd.NextDouble() - 0.5) * 4, p.Cy + (rnd.NextDouble() - 0.5) * 4, s.SymbolSize * 0.75, Rgb.HighlightGreen);
        }
        var runList = new List<List<PointD>>();
        double runPx = 0;
        foreach (var c in s.Conduits.Take(runs))
        {
            for (var k = 1; k < c.Count; k++) img.Highlight(c[k - 1].X, c[k - 1].Y, c[k].X, c[k].Y, Rgb.HighlightGreen, 7);
            runList.Add(c);
            runPx += Poly.PolylineLength(c);
        }
        img.Highlight(s.Width * 0.75, 20, s.Width * 0.9, 20, Rgb.HighlightGreen, 14);   // a highlighted note in the margin
        var t = Affine2D.Similarity(scale, rotationDeg, shiftX, shiftY);
        var page = Warp(img, t, (int)(s.Width * scale + 60), (int)(s.Height * scale + 60));
        Noise(page, rnd, 0.002);
        return new ScanResult(page, t, hl, runPx, runList);
    }

    /// <summary>Resamples the image so that page(x) = img(t^-1 x) (bilinear, white outside).</summary>
    public static ColorImage Warp(ColorImage img, Affine2D srcToDst, int w, int h)
    {
        var inv = srcToDst.Inverse();
        var o = new ColorImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var p = inv.Apply(new PointD(x, y));
                int x0 = (int)Math.Floor(p.X), y0 = (int)Math.Floor(p.Y);
                double fx = p.X - x0, fy = p.Y - y0;
                var a = img.Get(x0, y0); var b = img.Get(x0 + 1, y0); var c = img.Get(x0, y0 + 1); var d = img.Get(x0 + 1, y0 + 1);
                byte Mix(byte p00, byte p10, byte p01, byte p11) => (byte)Math.Round((p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy);
                var k = (y * w + x) * 3;
                o.Data[k] = Mix(a.R, b.R, c.R, d.R); o.Data[k + 1] = Mix(a.G, b.G, c.G, d.G); o.Data[k + 2] = Mix(a.B, b.B, c.B, d.B);
            }
        });
        return o;
    }

    public static void Noise(ColorImage img, Random rnd, double share)
    {
        var n = (int)(img.Width * img.Height * share);
        for (var i = 0; i < n; i++)
        {
            var x = rnd.Next(img.Width); var y = rnd.Next(img.Height);
            var v = (byte)(rnd.Next(2) == 0 ? 60 : 255);
            img.Set(x, y, new Rgb(v, v, v));
        }
    }

    /// <summary>A revision: removes some symbols, adds others (new sockets / data) and a new partition wall.</summary>
    public static (Scene Revised, List<SymbolPlacement> Removed, List<SymbolPlacement> Added) Revise(Scene s, int seed, int remove = 6, int add = 8)
    {
        var rnd = new Random(seed);
        var r = s.Clone();
        var removed = r.Symbols.OrderBy(_ => rnd.Next()).Take(remove).ToList();
        foreach (var x in removed) r.Symbols.Remove(x);
        var added = new List<SymbolPlacement>();
        var tries = 0;
        while (added.Count < add && tries++ < 5000)
        {
            var room = r.Rooms[rnd.Next(r.Rooms.Count)];
            var p = new PointD(room.X + s.SymbolSize * 1.6 + rnd.NextDouble() * (room.W - s.SymbolSize * 3.2), room.Y + s.SymbolSize * 2.4 + rnd.NextDouble() * (room.H - s.SymbolSize * 4));
            if (r.Symbols.Any(q => new PointD(q.Cx, q.Cy).DistanceTo(p) < s.SymbolSize * 2.4)) continue;
            if (r.Conduits.Any(c => Enumerable.Range(1, c.Count - 1).Any(k => Poly.ToSegment(p, c[k - 1], c[k]).Distance < s.SymbolSize * 1.2))) continue;
            if (r.Labels.Any(l => Math.Abs(l.X + 10 - p.X) < s.SymbolSize * 2 && Math.Abs(l.Y + 5 - p.Y) < s.SymbolSize * 1.5)) continue;
            var type = new[] { "SOCKET", "DATA", "TWIN SOCKET", "LIGHT" }[rnd.Next(4)];
            var sp = new SymbolPlacement(type, p.X, p.Y, rnd.Next(4), false, room.Name);
            r.Symbols.Add(sp); added.Add(sp);
        }
        var wallRoom = r.Rooms[0];
        r.Lines.Add((new PointD(wallRoom.X + wallRoom.W * 0.5, wallRoom.Y + 4), new PointD(wallRoom.X + wallRoom.W * 0.5, wallRoom.Y + wallRoom.H * 0.3), 5, new Rgb(40, 40, 40)));
        return (r, removed, added);
    }

    // ------------------------------------------------------------------ evaluation

    public sealed record TypeScore(string Type, int TruePositives, int FalsePositives, int FalseNegatives)
    {
        public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);
        public double Recall => TruePositives + FalseNegatives == 0 ? 1 : (double)TruePositives / (TruePositives + FalseNegatives);
    }

    /// <summary>Greedy matching of detections to the truth (same type, centre within tolerance) -> precision / recall per type.</summary>
    public static List<TypeScore> Evaluate(IEnumerable<SymbolPlacement> truth, IEnumerable<(string Type, PointD Center)> found, double tolerance)
    {
        var t = truth.ToList(); var f = found.ToList();
        var usedT = new bool[t.Count];
        var tp = new Dictionary<string, int>(); var fp = new Dictionary<string, int>();
        foreach (var d in f)
        {
            var best = -1; var bestD = double.MaxValue;
            for (var i = 0; i < t.Count; i++)
            {
                if (usedT[i] || t[i].Type != d.Type) continue;
                var dist = new PointD(t[i].Cx, t[i].Cy).DistanceTo(d.Center);
                if (dist <= tolerance && dist < bestD) { best = i; bestD = dist; }
            }
            if (best >= 0) { usedT[best] = true; tp[d.Type] = tp.GetValueOrDefault(d.Type) + 1; }
            else fp[d.Type] = fp.GetValueOrDefault(d.Type) + 1;
        }
        var types = t.Select(x => x.Type).Concat(f.Select(x => x.Type)).Distinct().OrderBy(x => Array.IndexOf(SymbolTypes, x)).ToList();
        return types.Select(ty => new TypeScore(ty, tp.GetValueOrDefault(ty), fp.GetValueOrDefault(ty),
            t.Where((x, i) => x.Type == ty && !usedT[i]).Count())).ToList();
    }
}
