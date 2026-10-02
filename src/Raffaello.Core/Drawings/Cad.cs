using System.Globalization;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Geometry read from a DWG / DXF file, flattened to 2D in model units.</summary>
public sealed class CadModel
{
    public string FileName { get; init; } = "";
    public string Units { get; set; } = "";
    /// <summary>Metres per drawing unit from $INSUNITS (mm = 0.001); 0 when unitless.</summary>
    public double MetresPerUnit { get; set; }
    public List<CadSegment> Segments { get; } = new();
    public List<CadCircle> Circles { get; } = new();
    public List<CadInsert> Inserts { get; } = new();
    public List<CadText> Texts { get; } = new();
    public List<CadPolygon> ClosedPolylines { get; } = new();
    /// <summary>Block definitions: name -> primitives in block coordinates (for learning geometry signatures).</summary>
    public Dictionary<string, List<CadPrimitive>> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = new();

    public RectD Bounds => RectD.Bounds(Segments.SelectMany(s => new[] { s.A, s.B }).Concat(Circles.Select(c => c.Center)).Concat(Inserts.Select(i => i.Point)).Concat(Texts.Select(t => t.Point)));

    /// <summary>Loose primitives (not inside a block reference) for exploded-symbol recognition.</summary>
    public IEnumerable<CadPrimitive> LoosePrimitives =>
        Segments.Where(s => s.InsertIndex < 0).Select(s => new CadPrimitive(CadPrimitiveKind.Line, s.A, s.B, 0, 0, 0, s.Layer, s.ArcId))
            .Concat(Circles.Where(c => c.InsertIndex < 0).Select(c => new CadPrimitive(CadPrimitiveKind.Circle, c.Center, c.Center, c.Radius, 0, 0, c.Layer, -1)));
}

public sealed record CadSegment(PointD A, PointD B, string Layer, int InsertIndex = -1, int ArcId = -1)
{
    public double Length => A.DistanceTo(B);
}
public sealed record CadCircle(PointD Center, double Radius, string Layer, int InsertIndex = -1);
public sealed record CadText(string Value, PointD Point, string Layer);
public sealed record CadPolygon(List<PointD> Points, string Layer);
public sealed record CadInsert(int Index, string Block, PointD Point, double RotationDeg, double Scale, string Layer, Dictionary<string, string> Attributes, RectD Bounds);

public enum CadPrimitiveKind { Line, Circle, Arc }

/// <summary>A primitive for signatures. Arcs are kept as one primitive (radius + sweep) so tessellation does not change the signature.</summary>
public sealed record CadPrimitive(CadPrimitiveKind Kind, PointD A, PointD B, double Radius, double SweepDeg, double Unused, string Layer, int ArcId)
{
    public RectD Bounds => Kind == CadPrimitiveKind.Circle ? new RectD(A.X - Radius, A.Y - Radius, 2 * Radius, 2 * Radius) : RectD.Bounds(new[] { A, B });
}

/// <summary>[drawings] DWG / DXF reader on ACadSharp (MIT): layers, block references with attributes, loose geometry, texts, closed polylines, units.</summary>
public static class CadReader
{
    public static CadModel Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        CadDocument doc = ext == ".dwg" ? DwgReader.Read(path) : DxfReader.Read(path);
        var m = new CadModel { FileName = Path.GetFileName(path) };
        (m.Units, m.MetresPerUnit) = UnitsOf(doc.Header.InsUnits);
        foreach (var br in doc.BlockRecords)
        {
            if (br.Name.StartsWith("*")) continue;
            var prims = new List<CadPrimitive>();
            var tmp = new CadModel();
            var arcId = 0;
            foreach (var e in br.Entities) Flatten(tmp, e, Affine2D.Identity, -1, ref arcId, 0, prims, path);
            m.Blocks[br.Name] = prims;
            var xref = br.BlockEntity?.XRefPath;
            if (!string.IsNullOrWhiteSpace(xref)) m.Notes.Add($"xref {br.Name}: {xref}");
        }
        var id = 0;
        foreach (var e in doc.Entities) Flatten(m, e, Affine2D.Identity, -1, ref id, 0, null, path);
        return m;
    }

    public static (string Name, double MetresPerUnit) UnitsOf(ACadSharp.Types.Units.UnitsType u) => u switch
    {
        ACadSharp.Types.Units.UnitsType.Millimeters => ("mm", 0.001),
        ACadSharp.Types.Units.UnitsType.Centimeters => ("cm", 0.01),
        ACadSharp.Types.Units.UnitsType.Meters => ("m", 1),
        ACadSharp.Types.Units.UnitsType.Inches => ("in", 0.0254),
        ACadSharp.Types.Units.UnitsType.Feet => ("ft", 0.3048),
        ACadSharp.Types.Units.UnitsType.Kilometers => ("km", 1000),
        _ => ("unitless", 0),
    };

    private static PointD P(CSMath.XYZ p, Affine2D t) => t.Apply(new PointD(p.X, p.Y));
    private static PointD P(CSMath.XY p, Affine2D t) => t.Apply(new PointD(p.X, p.Y));

    private static void Flatten(CadModel m, Entity e, Affine2D t, int insertIndex, ref int arcId, int depth, List<CadPrimitive>? prims, string path)
    {
        var layer = e.Layer?.Name ?? "0";
        switch (e)
        {
            case Line l:
            {
                var a = P(l.StartPoint, t); var b = P(l.EndPoint, t);
                m.Segments.Add(new CadSegment(a, b, layer, insertIndex));
                prims?.Add(new CadPrimitive(CadPrimitiveKind.Line, a, b, 0, 0, 0, layer, -1));
                break;
            }
            case LwPolyline pl:
            {
                var v = pl.Vertices.ToList();
                var pts = new List<PointD>();
                var n = pl.IsClosed ? v.Count : v.Count - 1;
                for (var i = 0; i < n; i++)
                {
                    var a = new PointD(v[i].Location.X, v[i].Location.Y);
                    var b = new PointD(v[(i + 1) % v.Count].Location.X, v[(i + 1) % v.Count].Location.Y);
                    var seg = BulgePoints(a, b, v[i].Bulge);
                    if (pts.Count == 0) pts.Add(t.Apply(seg[0]));
                    var aid = Math.Abs(v[i].Bulge) > 1e-9 ? ++arcId : -1;
                    for (var k = 1; k < seg.Count; k++)
                    {
                        var pa = t.Apply(seg[k - 1]); var pb = t.Apply(seg[k]);
                        m.Segments.Add(new CadSegment(pa, pb, layer, insertIndex, aid));
                        pts.Add(pb);
                    }
                    if (prims != null)
                    {
                        if (aid < 0) prims.Add(new CadPrimitive(CadPrimitiveKind.Line, t.Apply(a), t.Apply(b), 0, 0, 0, layer, -1));
                        else
                        {
                            var sweep = 4 * Math.Atan(Math.Abs(v[i].Bulge)) * 180 / Math.PI;
                            var chord = a.DistanceTo(b);
                            var r = chord / (2 * Math.Sin(sweep * Math.PI / 360)) * t.Scale;
                            prims.Add(new CadPrimitive(CadPrimitiveKind.Arc, t.Apply(a), t.Apply(b), r, sweep, 0, layer, aid));
                        }
                    }
                }
                if (pl.IsClosed && pts.Count >= 3) m.ClosedPolylines.Add(new CadPolygon(pts.Take(pts.Count - (pts[0].DistanceTo(pts[^1]) < 1e-9 ? 1 : 0)).ToList(), layer));
                break;
            }
            case Circle c when e is not Arc:
            {
                var center = P(c.Center, t); var r = c.Radius * t.Scale;
                m.Circles.Add(new CadCircle(center, r, layer, insertIndex));
                prims?.Add(new CadPrimitive(CadPrimitiveKind.Circle, center, center, r, 360, 0, layer, -1));
                break;
            }
            case Arc a:
            {
                double s0 = a.StartAngle, s1 = a.EndAngle;
                if (s1 < s0) s1 += 2 * Math.PI;
                var aid = ++arcId;
                const int steps = 16;
                var prev = new PointD(a.Center.X + a.Radius * Math.Cos(s0), a.Center.Y + a.Radius * Math.Sin(s0));
                for (var i = 1; i <= steps; i++)
                {
                    var ang = s0 + (s1 - s0) * i / steps;
                    var cur = new PointD(a.Center.X + a.Radius * Math.Cos(ang), a.Center.Y + a.Radius * Math.Sin(ang));
                    m.Segments.Add(new CadSegment(t.Apply(prev), t.Apply(cur), layer, insertIndex, aid));
                    prev = cur;
                }
                prims?.Add(new CadPrimitive(CadPrimitiveKind.Arc, t.Apply(new PointD(a.Center.X + a.Radius * Math.Cos(s0), a.Center.Y + a.Radius * Math.Sin(s0))), t.Apply(prev), a.Radius * t.Scale, (s1 - s0) * 180 / Math.PI, 0, layer, aid));
                break;
            }
            case TextEntity te:
                m.Texts.Add(new CadText(te.Value ?? "", P(te.InsertPoint, t), layer));
                break;
            case MText mt:
                m.Texts.Add(new CadText(CleanMText(mt.Value ?? ""), P(mt.InsertPoint, t), layer));
                break;
            case Insert ins when depth < 6:
            {
                var block = ins.Block;
                var name = block?.Name ?? "?";
                var bp = block?.BlockEntity?.BasePoint ?? CSMath.XYZ.Zero;
                var rot = ins.Rotation;
                var local = Affine2D.Similarity(1, rot * 180 / Math.PI, ins.InsertPoint.X, ins.InsertPoint.Y)
                    .After(Affine2D.ScaleOnly(ins.XScale, ins.YScale)).After(new Affine2D(1, 0, -bp.X, 0, 1, -bp.Y));
                var full = t.After(local);
                var idx = insertIndex;
                var before = (m.Segments.Count, m.Circles.Count);
                if (depth == 0 && prims is null) idx = m.Inserts.Count;
                if (block != null)
                    foreach (var child in block.Entities) Flatten(m, child, full, idx, ref arcId, depth + 1, prims, path);
                if (depth == 0 && prims is null)
                {
                    var pts = m.Segments.Skip(before.Item1).SelectMany(s => new[] { s.A, s.B })
                        .Concat(m.Circles.Skip(before.Item2).SelectMany(c => new[] { c.Center + new PointD(-c.Radius, -c.Radius), c.Center + new PointD(c.Radius, c.Radius) })).ToList();
                    var at = P(ins.InsertPoint, t);
                    var bounds = pts.Count > 0 ? RectD.Bounds(pts) : new RectD(at.X, at.Y, 0, 0);
                    var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var att in ins.Attributes) attrs[att.Tag ?? ""] = att.Value ?? "";
                    m.Inserts.Add(new CadInsert(idx, name, at, rot * 180 / Math.PI, ins.XScale, layer, attrs, bounds));
                }
                break;
            }
        }
    }

    /// <summary>Points along a polyline segment with a bulge (arc), the end points included.</summary>
    public static List<PointD> BulgePoints(PointD a, PointD b, double bulge)
    {
        if (Math.Abs(bulge) < 1e-9) return new List<PointD> { a, b };
        var theta = 4 * Math.Atan(bulge);
        var chord = a.DistanceTo(b);
        var r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
        var mid = new PointD((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var d = b - a; var nrm = new PointD(-d.Y / chord, d.X / chord);
        var h = r * Math.Cos(theta / 2);
        var c = mid + nrm * (h * Math.Sign(bulge));
        var a0 = Math.Atan2(a.Y - c.Y, a.X - c.X);
        var res = new List<PointD>();
        const int steps = 12;
        for (var i = 0; i <= steps; i++)
        {
            var ang = a0 + theta * i / steps;
            res.Add(new PointD(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang)));
        }
        res[^1] = b;
        return res;
    }

    private static string CleanMText(string s) => Regex.Replace(Regex.Replace(s, @"\\[A-Za-z][^;]*;", ""), @"[{}]|\\P", " ").Trim();
}

/// <summary>
/// [drawings] Translation / rotation / scale invariant signature of a small group of CAD primitives - recognises EXPLODED blocks
/// (the lines / arcs / circles that were a socket block) by comparing with signatures learned from block definitions.
/// </summary>
public sealed record GeometrySignature(int Lines, int Circles, int Arcs, double[] LineLengths, double[] Radii, double[] Sweeps, double[] Spread, double Size)
{
    public static GeometrySignature? Of(IReadOnlyList<CadPrimitive> prims)
    {
        if (prims.Count == 0) return null;
        var pts = prims.SelectMany(p => p.Kind == CadPrimitiveKind.Circle ? new[] { p.A } : new[] { p.A, p.B }).ToList();
        var b = RectD.Bounds(prims.SelectMany(p => { var r = p.Bounds; return new[] { new PointD(r.X, r.Y), new PointD(r.Right, r.Bottom) }; }));
        var size = Math.Sqrt(b.W * b.W + b.H * b.H);
        if (size < 1e-9) return null;
        var c = new PointD(pts.Average(p => p.X), pts.Average(p => p.Y));
        var lines = prims.Where(p => p.Kind == CadPrimitiveKind.Line).Select(p => p.A.DistanceTo(p.B) / size).OrderBy(x => x).ToArray();
        var circles = prims.Where(p => p.Kind == CadPrimitiveKind.Circle).Select(p => p.Radius / size).OrderBy(x => x).ToArray();
        var arcs = prims.Where(p => p.Kind == CadPrimitiveKind.Arc).OrderBy(p => p.Radius).ToList();
        var radii = circles.Concat(arcs.Select(a => a.Radius / size)).ToArray();
        var sweeps = arcs.Select(a => a.SweepDeg / 360).OrderBy(x => x).ToArray();
        var spread = pts.Select(p => p.DistanceTo(c) / size).OrderBy(x => x).ToArray();
        return new GeometrySignature(lines.Length, circles.Length, arcs.Count, lines, radii, sweeps, spread, size);
    }

    /// <summary>0 = identical shape; counts must agree, then the largest difference of the normalised measures.</summary>
    public double Distance(GeometrySignature o)
    {
        if (Lines != o.Lines || Circles != o.Circles || Arcs != o.Arcs) return double.MaxValue;
        static double Max(double[] a, double[] b) => a.Length != b.Length ? double.MaxValue : a.Select((v, i) => Math.Abs(v - b[i])).DefaultIfEmpty(0).Max();
        return new[] { Max(LineLengths, o.LineLengths), Max(Radii, o.Radii), Max(Sweeps, o.Sweeps), Max(Spread, o.Spread) }.Max();
    }

    public string Serialize() => string.Join("|", new[]
    {
        $"N:{Lines},{Circles},{Arcs}", "L:" + Join(LineLengths), "R:" + Join(Radii), "W:" + Join(Sweeps), "S:" + Join(Spread),
        "Z:" + Size.ToString("0.####", CultureInfo.InvariantCulture),
    });

    private static string Join(double[] a) => string.Join(",", a.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));

    public static GeometrySignature? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split('|').Select(p => p.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        double[] A(string k) => parts.TryGetValue(k, out var v) && v.Length > 0 ? v.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray() : Array.Empty<double>();
        var n = A("N");
        if (n.Length != 3) return null;
        return new GeometrySignature((int)n[0], (int)n[1], (int)n[2], A("L"), A("R"), A("W"), A("S"), A("Z").FirstOrDefault());
    }
}

/// <summary>[drawings] Counting from CAD: block references by name and exploded symbols by geometry signature; rooms from closed polylines + texts.</summary>
public static class CadTakeoff
{
    /// <summary>Hits for block references whose name matches a symbol's BlockNames.</summary>
    public static List<DwgHit> BlockHits(CadModel m, IEnumerable<DwgSymbol> symbols)
    {
        var res = new List<DwgHit>();
        var list = symbols.Where(s => s.Active && !string.IsNullOrWhiteSpace(s.BlockNames)).ToList();
        foreach (var ins in m.Inserts)
        {
            var s = list.FirstOrDefault(x => LinearTakeoff.Like(ins.Block, x.BlockNames));
            if (s is null) continue;
            var b = ins.Bounds.W > 0 ? ins.Bounds : new RectD(ins.Point.X - 1, ins.Point.Y - 1, 2, 2);
            res.Add(new DwgHit
            {
                SymbolId = s.Id, SymbolName = s.Name, X = b.X, Y = b.Y, W = b.W, H = b.H, Score = 1, Rotation = (int)Math.Round(ins.RotationDeg),
                Origin = DwgOrigins.Block, Attributes = string.Join("; ", ins.Attributes.Select(kv => $"{kv.Key}={kv.Value}")),
                MountingHeightM = HeightFromAttributes(ins.Attributes),
            });
        }
        return res;
    }

    private static double HeightFromAttributes(Dictionary<string, string> attrs)
    {
        foreach (var kv in attrs)
        {
            var m = Regex.Match(kv.Key + "=" + kv.Value, @"H\w*\s*=\s*(\d{2,5})", RegexOptions.IgnoreCase);
            if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
        }
        return 0;
    }

    /// <summary>Signature learned from a block definition (for symbols that are sometimes exploded).</summary>
    public static GeometrySignature? LearnFromBlock(CadModel m, string block) =>
        m.Blocks.TryGetValue(block, out var prims) ? GeometrySignature.Of(prims) : null;

    /// <summary>
    /// Exploded symbols: loose primitives smaller than the symbol are grouped (touching / overlapping bounding boxes), each group's
    /// signature is compared with the library. A group of 2-3x the symbol size is split no further - review it in the UI.
    /// </summary>
    public static List<DwgHit> ExplodedHits(CadModel m, IEnumerable<DwgSymbol> symbols, double tolerance = 0.06, double insertScale = 1)
    {
        var sigs = symbols.Where(s => s.Active && !string.IsNullOrWhiteSpace(s.GeometrySignature))
            .Select(s => (Symbol: s, Sig: GeometrySignature.Parse(s.GeometrySignature)!)).Where(x => x.Sig != null).ToList();
        if (sigs.Count == 0) return new();
        var maxSize = sigs.Max(x => x.Sig.Size) * insertScale * 1.6;
        // arcs tessellated from one CAD arc are one primitive
        var prims = new List<CadPrimitive>();
        var loose = m.LoosePrimitives.ToList();
        prims.AddRange(loose.Where(p => p.ArcId < 0));
        foreach (var g in loose.Where(p => p.ArcId >= 0).GroupBy(p => p.ArcId))
        {
            var list = g.ToList();
            prims.Add(list.Count == 1 ? list[0] : ArcFromPolyline(list, list[0].Layer));
        }
        prims = prims.Where(p => Math.Max(p.Bounds.W, p.Bounds.H) <= maxSize).ToList();
        var groups = MergeNested(Group(prims, maxSize * 0.04 + 1e-9), maxSize);
        var res = new List<DwgHit>();
        foreach (var g in groups)
        {
            var sig = GeometrySignature.Of(g);
            if (sig is null || sig.Size > maxSize) continue;
            var best = sigs.Select(x => (x.Symbol, x.Sig, D: x.Sig.Distance(sig))).Where(x => x.D <= tolerance && sig.Size >= x.Sig.Size * 0.5 && sig.Size <= x.Sig.Size * 2 * insertScale).OrderBy(x => x.D).FirstOrDefault();
            if (best.Symbol is null) continue;
            var b = RectD.Bounds(g.SelectMany(p => { var r = p.Bounds; return new[] { new PointD(r.X, r.Y), new PointD(r.Right, r.Bottom) }; }));
            res.Add(new DwgHit { SymbolId = best.Symbol.Id, SymbolName = best.Symbol.Name, X = b.X, Y = b.Y, W = b.W, H = b.H, Score = Math.Round(1 - best.D, 3), Origin = DwgOrigins.Geometry });
        }
        return res;
    }

    private static CadPrimitive ArcFromPolyline(List<CadPrimitive> segs, string layer)
    {
        // circle through first, middle and last points
        var a = segs[0].A; var b = segs[segs.Count / 2].A; var c = segs[^1].B;
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return new CadPrimitive(CadPrimitiveKind.Line, a, c, 0, 0, 0, layer, -1);
        var ux = ((a.X * a.X + a.Y * a.Y) * (b.Y - c.Y) + (b.X * b.X + b.Y * b.Y) * (c.Y - a.Y) + (c.X * c.X + c.Y * c.Y) * (a.Y - b.Y)) / d;
        var uy = ((a.X * a.X + a.Y * a.Y) * (c.X - b.X) + (b.X * b.X + b.Y * b.Y) * (a.X - c.X) + (c.X * c.X + c.Y * c.Y) * (b.X - a.X)) / d;
        var center = new PointD(ux, uy); var r = center.DistanceTo(a);
        var len = segs.Sum(s => s.A.DistanceTo(s.B));
        var closed = a.DistanceTo(c) < r * 0.05;
        if (closed) return new CadPrimitive(CadPrimitiveKind.Circle, center, center, r, 360, 0, layer, -1);
        return new CadPrimitive(CadPrimitiveKind.Arc, a, c, r, len / r * 180 / Math.PI, 0, layer, segs[0].ArcId);
    }

    /// <summary>Joins groups whose boxes overlap (a "T" drawn inside a square, a dot inside a ring) while the result stays symbol-sized.</summary>
    internal static List<List<CadPrimitive>> MergeNested(List<List<CadPrimitive>> groups, double maxSize)
    {
        static RectD Box(List<CadPrimitive> g) => RectD.Bounds(g.SelectMany(p => { var r = p.Bounds; return new[] { new PointD(r.X, r.Y), new PointD(r.Right, r.Bottom) }; }));
        var list = groups.Select(g => (Prims: g, Box: Box(g))).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            list.Sort((a, b) => (b.Box.W * b.Box.H).CompareTo(a.Box.W * a.Box.H));
            for (var i = 0; i < list.Count && !changed; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = list[i].Box; var b = list[j].Box;
                    // the smaller box mostly inside the bigger one
                    var inter = a.IntersectionArea(new RectD(b.X, b.Y, Math.Max(b.W, 1e-9), Math.Max(b.H, 1e-9)));
                    var inside = b.Center.X >= a.X && b.Center.X <= a.Right && b.Center.Y >= a.Y && b.Center.Y <= a.Bottom;
                    if (!inside && inter <= 0) continue;
                    var merged = list[i].Prims.Concat(list[j].Prims).ToList();
                    var mb = Box(merged);
                    if (Math.Sqrt(mb.W * mb.W + mb.H * mb.H) > maxSize || !inside) continue;
                    list[i] = (merged, mb); list.RemoveAt(j); changed = true; break;
                }
        }
        return list.Select(x => x.Prims).ToList();
    }

    /// <summary>Union-find on bounding boxes expanded by the gap.</summary>
    internal static List<List<CadPrimitive>> Group(List<CadPrimitive> prims, double gap)
    {
        var parent = Enumerable.Range(0, prims.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var cell = Math.Max(gap * 10, 1e-6);
        var grid = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < prims.Count; i++)
        {
            var b = prims[i].Bounds;
            for (var gx = (long)Math.Floor((b.X - gap) / cell); gx <= (long)Math.Floor((b.Right + gap) / cell); gx++)
                for (var gy = (long)Math.Floor((b.Y - gap) / cell); gy <= (long)Math.Floor((b.Bottom + gap) / cell); gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = new List<int>();
                    foreach (var j in l)
                    {
                        var o = prims[j].Bounds;
                        if (b.X - gap <= o.Right && o.X <= b.Right + gap && b.Y - gap <= o.Bottom && o.Y <= b.Bottom + gap) parent[Find(i)] = Find(j);
                    }
                    l.Add(i);
                }
        }
        return Enumerable.Range(0, prims.Count).GroupBy(Find).Select(g => g.Select(i => prims[i]).ToList()).ToList();
    }

    /// <summary>Rooms: closed polylines on room layers, named by the text inside nearest the centre.</summary>
    public static List<RoomPolygon> Rooms(CadModel m, string roomLayers = "*ROOM*;*AREA*;*SPACE*", string textLayers = "")
    {
        var res = new List<RoomPolygon>();
        foreach (var poly in m.ClosedPolylines.Where(p => LinearTakeoff.Like(p.Layer, roomLayers)))
        {
            var c = new PointD(poly.Points.Average(p => p.X), poly.Points.Average(p => p.Y));
            var name = m.Texts.Where(t => (textLayers.Length == 0 || LinearTakeoff.Like(t.Layer, textLayers)) && t.Value.Trim().Length > 0 && Poly.Contains(poly.Points, t.Point))
                .OrderBy(t => t.Point.DistanceTo(c)).Select(t => t.Value.Trim().ToUpperInvariant()).FirstOrDefault();
            if (name != null) res.Add(new RoomPolygon(name, poly.Points));
        }
        return res;
    }
}
