using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Linear takeoff: trays / conduits / cables measured from vector PDF lines, CAD layers or traced on scans.</summary>
public static class LinearTakeoff
{
    /// <summary>Wildcard match (* and ?) ignoring case, several patterns separated by ';'.</summary>
    public static bool Like(string value, string patterns)
    {
        foreach (var p in (patterns ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rx = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(value ?? "", rx, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    private static bool StyleMatches(DwgLinearClass c, string styleKey) =>
        (c.StyleKeys ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(k => k.Equals(styleKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Vector PDF: every class with style keys collects its segments, chained into polylines.</summary>
    public static List<DwgRun> FromVector(VectorPage page, IEnumerable<DwgLinearClass> classes, double metresPerPx, double minLengthPx = 2)
    {
        var res = new List<DwgRun>();
        foreach (var c in classes.Where(c => c.Active && !string.IsNullOrWhiteSpace(c.StyleKeys)))
        {
            var segs = page.Segments.Where(s => StyleMatches(c, s.Style.Key)).Select(s => (s.A, s.B)).ToList();
            foreach (var line in Polylines.Chain(segs, tol: Math.Max(0.5, page.Dpi / 150.0)))
            {
                var len = Poly.PolylineLength(line);
                if (len < minLengthPx) continue;
                res.Add(NewRun(c, line, len, metresPerPx, DwgOrigins.Vector, c.StyleKeys));
            }
        }
        return res;
    }

    /// <summary>CAD: segments per layer (already in model units), classes by layer pattern.</summary>
    public static List<DwgRun> FromLayers(IEnumerable<(PointD A, PointD B, string Layer)> segments, IEnumerable<DwgLinearClass> classes, double metresPerUnit, double tol)
    {
        var segs = segments.ToList();
        var res = new List<DwgRun>();
        foreach (var c in classes.Where(c => c.Active && !string.IsNullOrWhiteSpace(c.Layers)))
        {
            var mine = segs.Where(s => Like(s.Layer, c.Layers)).Select(s => (s.A, s.B)).ToList();
            foreach (var line in Polylines.Chain(mine, tol))
                res.Add(NewRun(c, line, Poly.PolylineLength(line), metresPerUnit, DwgOrigins.Cad, c.Layers));
        }
        return res;
    }

    /// <summary>Scans: pixels near the class colour, thinned and traced into polylines (no vector data needed).</summary>
    public static List<DwgRun> FromRaster(ColorImage img, IEnumerable<DwgLinearClass> classes, double metresPerPx, int minLengthPx = 20)
    {
        var res = new List<DwgRun>();
        foreach (var c in classes.Where(c => c.Active && !string.IsNullOrWhiteSpace(c.ColourHex)))
        {
            var mask = HighlightDetector.NearColour(img, Rgb.Parse(c.ColourHex, Rgb.Black), c.ColourTolerance).Close(4);   // bridges dash gaps
            foreach (var comp in mask.Components(minLengthPx))
            {
                var skel = Skeleton.Thin(comp.LocalMask());
                foreach (var line in Polylines.TraceSkeleton(skel, comp.X - 1, comp.Y - 1, 1.0))
                {
                    var len = Poly.PolylineLength(line);
                    if (len >= minLengthPx) res.Add(NewRun(c, line, len, metresPerPx, DwgOrigins.Raster, c.ColourHex));
                }
            }
        }
        return res;
    }

    public static DwgRun NewRun(DwgLinearClass c, List<PointD> line, double lengthUnits, double metresPerUnit, string origin, string style) => new()
    {
        ClassId = c.Id, ClassName = c.Name, Points = Poly.Format(line), LengthUnits = lengthUnits,
        LengthM = metresPerUnit > 0 ? Math.Round(lengthUnits * metresPerUnit, 3) : 0, Origin = origin, StyleKey = style,
    };

    /// <summary>Metres per unit from two clicked points of a known dimension.</summary>
    public static double CalibrateScale(PointD a, PointD b, double knownMetres) =>
        a.DistanceTo(b) < 1e-9 || knownMetres <= 0 ? throw new InvalidOperationException("Click two different points and type the real distance.") : knownMetres / a.DistanceTo(b);
}

/// <summary>
/// [drawings] Route graph (containment / cable routes) for shortest-path lengths: nodes merged within a tolerance, T-junctions split,
/// devices and the DB snapped onto the nearest route.
/// </summary>
public sealed class RouteNetwork
{
    private readonly List<PointD> _nodes = new();
    private readonly List<List<(int To, double W)>> _adj = new();
    private readonly List<(int A, int B)> _edges = new();
    private readonly double _tol;

    public RouteNetwork(IEnumerable<(PointD A, PointD B)> segments, double tol = 1.5)
    {
        _tol = tol;
        var segs = segments.Where(s => s.A.DistanceTo(s.B) > 1e-9).ToList();
        // split segments where another segment's end point lies on them (T-junctions) or where they cross
        var cuts = segs.Select(_ => new List<double> { 0, 1 }).ToList();
        for (var i = 0; i < segs.Count; i++)
            for (var j = 0; j < segs.Count; j++)
            {
                if (i == j) continue;
                foreach (var p in new[] { segs[j].A, segs[j].B })
                {
                    var (d, _, t) = Poly.ToSegment(p, segs[i].A, segs[i].B);
                    if (d <= tol && t > 1e-6 && t < 1 - 1e-6) cuts[i].Add(t);
                }
                if (j > i && Cross(segs[i], segs[j]) is { } x) { cuts[i].Add(x.T); cuts[j].Add(x.U); }
            }
        for (var i = 0; i < segs.Count; i++)
        {
            var ts = cuts[i].Distinct().OrderBy(t => t).ToList();
            for (var k = 0; k + 1 < ts.Count; k++)
            {
                var a = segs[i].A + (segs[i].B - segs[i].A) * ts[k];
                var b = segs[i].A + (segs[i].B - segs[i].A) * ts[k + 1];
                AddEdge(Node(a), Node(b));
            }
        }
    }

    private static (double T, double U)? Cross((PointD A, PointD B) s1, (PointD A, PointD B) s2)
    {
        var d = s1.B - s1.A; var e = s2.B - s2.A;
        var den = d.X * e.Y - d.Y * e.X;
        if (Math.Abs(den) < 1e-12) return null;
        var w = s2.A - s1.A;
        var t = (w.X * e.Y - w.Y * e.X) / den; var u = (w.X * d.Y - w.Y * d.X) / den;
        return t > 1e-6 && t < 1 - 1e-6 && u > 1e-6 && u < 1 - 1e-6 ? (t, u) : null;
    }

    public int NodeCount => _nodes.Count;
    public double TotalLength => _edges.Sum(e => _nodes[e.A].DistanceTo(_nodes[e.B]));

    private int Node(PointD p)
    {
        for (var i = 0; i < _nodes.Count; i++) if (_nodes[i].DistanceTo(p) <= _tol) return i;
        _nodes.Add(p); _adj.Add(new());
        return _nodes.Count - 1;
    }

    private void AddEdge(int a, int b)
    {
        if (a == b) return;
        var w = _nodes[a].DistanceTo(_nodes[b]);
        _adj[a].Add((b, w)); _adj[b].Add((a, w));
        _edges.Add((a, b));
    }

    /// <summary>Nearest point on the network: (distance, edge, closest point).</summary>
    public (double Distance, int Edge, PointD Point) Snap(PointD p)
    {
        var best = (double.MaxValue, -1, p);
        for (var i = 0; i < _edges.Count; i++)
        {
            var (d, c, _) = Poly.ToSegment(p, _nodes[_edges[i].A], _nodes[_edges[i].B]);
            if (d < best.Item1) best = (d, i, c);
        }
        return best;
    }

    /// <summary>Shortest route length between two points snapped onto the network (sheet units), null when not connected or too far.</summary>
    public (double Length, double SnapFrom, double SnapTo, List<PointD> Path)? Route(PointD from, PointD to, double maxSnap)
    {
        if (_edges.Count == 0) return null;
        var sf = Snap(from); var st = Snap(to);
        if (sf.Edge < 0 || st.Edge < 0 || sf.Distance > maxSnap || st.Distance > maxSnap) return null;
        if (sf.Edge == st.Edge)
            return (sf.Point.DistanceTo(st.Point), sf.Distance, st.Distance, new List<PointD> { sf.Point, st.Point });
        // virtual source / target nodes on their edges
        var n = _nodes.Count;
        var dist = new double[n + 2]; Array.Fill(dist, double.MaxValue);
        var prev = new int[n + 2]; Array.Fill(prev, -1);
        var src = n; var dst = n + 1;
        List<(int To, double W)> Neigh(int v)
        {
            var l = v < n ? _adj[v].ToList() : new List<(int, double)>();
            var (ea, eb) = _edges[sf.Edge];
            if (v == src) { l.Add((ea, sf.Point.DistanceTo(_nodes[ea]))); l.Add((eb, sf.Point.DistanceTo(_nodes[eb]))); }
            var (ta, tb) = _edges[st.Edge];
            if (v == ta) l.Add((dst, st.Point.DistanceTo(_nodes[ta])));
            if (v == tb) l.Add((dst, st.Point.DistanceTo(_nodes[tb])));
            return l;
        }
        var pq = new PriorityQueue<int, double>();
        dist[src] = 0; pq.Enqueue(src, 0);
        while (pq.TryDequeue(out var v, out var dv))
        {
            if (dv > dist[v]) continue;
            if (v == dst) break;
            foreach (var (nb, w) in Neigh(v))
                if (dv + w < dist[nb]) { dist[nb] = dv + w; prev[nb] = v; pq.Enqueue(nb, dist[nb]); }
        }
        if (dist[dst] == double.MaxValue) return null;
        var path = new List<PointD>();
        for (var v = dst; v >= 0; v = prev[v]) path.Add(v == src ? sf.Point : v == dst ? st.Point : _nodes[v]);
        path.Reverse();
        return (dist[dst], sf.Distance, st.Distance, path);
    }
}

/// <summary>[drawings] Length of one cable run (DB -> point) with its breakdown.</summary>
public sealed class PointLength
{
    public int MarkNo { get; init; }
    public long HitId { get; init; }
    public string Symbol { get; init; } = "";
    public string System { get; init; } = "";
    public string Item { get; init; } = "";
    public string Room { get; init; } = "";
    public string RoomType { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string CableSize { get; init; } = "";
    public double HorizontalM { get; init; }
    public double RiseM { get; init; }
    public double DropM { get; init; }
    public double RiserM { get; init; }
    public double TerminationM { get; init; }
    public double SparePct { get; init; }
    public double TotalM { get; init; }
    public bool Traceable { get; init; } = true;
    public string Note { get; init; } = "";
    public List<PointD> Path { get; init; } = new();
    /// <summary>15 m rule: points this run is worth (max(1, L/15), 1 decimal).</summary>
    public double PointsEquivalent => LengthCheck.PerPoint(TotalM);
}

public sealed record LengthStats(string Group, int Count, double Min, double Avg, double Max, double Total);

/// <summary>[drawings] Inputs for cable lengths of one sheet.</summary>
public sealed class CableLengthInput
{
    public required RouteNetwork Network { get; init; }
    /// <summary>DB / panel position (sheet units).</summary>
    public PointD Panel { get; init; }
    public string PanelName { get; init; } = "DB";
    public double MetresPerUnit { get; init; }
    public string Building { get; init; } = "";
    public string Level { get; init; } = "";
    /// <summary>Levels between the DB and the points (riser runs), 0 = same level.</summary>
    public int LevelsCrossed { get; init; }
    /// <summary>Room -> room type (for heights and averages).</summary>
    public Dictionary<string, string> RoomTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Room -> ceiling height override from the room boundaries (m).</summary>
    public Dictionary<string, double> CeilingHeights { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Largest distance from a symbol to its route (sheet units) - further points are "not traceable".</summary>
    public double MaxSnap { get; init; } = 60;
    /// <summary>Cable size per item / system ("POWER" -> "2.5 mm2").</summary>
    public Dictionary<string, string> CableSizes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// [drawings] Cable length per point = horizontal route (shortest path on the measured routes) + rise / drop from the containment
/// to the device (ceiling height + half the void - mounting height; H= on the drawing wins) + drop at the DB end + riser (slab to slab
/// x levels crossed) + 2 x termination allowance, plus SPARE %. Averages per item / room type / system feed the 15 m checks, the
/// material reconciliation and estimates for rooms not yet measured.
/// </summary>
public static class CableLengths
{
    public static List<PointLength> Compute(CableLengthInput input, IEnumerable<DwgHit> hits, IReadOnlyDictionary<long, DwgSymbol> symbols, DrawingSettings settings)
    {
        var res = new List<PointLength>();
        foreach (var h in hits.Where(h => DwgHitStatus.Counts(h.Status) && symbols.ContainsKey(h.SymbolId)))
        {
            var s = symbols[h.SymbolId];
            if (s.Excluded) continue;
            var roomType = input.RoomTypes.GetValueOrDefault(h.Room, "");
            var hgt = settings.HeightsFor(input.Building, input.Level, roomType);
            var ceiling = input.CeilingHeights.TryGetValue(h.Room, out var ch) && ch > 0 ? ch : hgt.FloorToCeilingM;
            var containment = ceiling + hgt.CeilingVoidM / 2;
            var mount = TakeoffCounter.MountOf(h, s, settings) == "C" ? ceiling : settings.MountingHeightFor(s, h.MountingHeightM);
            var size = input.CableSizes.GetValueOrDefault(s.Item.Length > 0 ? s.Item : s.System, input.CableSizes.GetValueOrDefault(s.System, ""));
            var route = input.Network.Route(input.Panel, h.Center, input.MaxSnap);
            if (route is null)
            {
                res.Add(new PointLength { MarkNo = h.MarkNo, HitId = h.Id, Symbol = s.Name, System = TakeoffCounter.SystemOf(s), Item = s.Item, Room = h.Room, RoomType = roomType,
                    From = input.PanelName, To = $"{s.Name} #{h.MarkNo}", CableSize = size, Traceable = false, Note = "no measured route reaches this point" });
                continue;
            }
            var (len, snapFrom, snapTo, path) = route.Value;
            var horizontal = (len + snapFrom + snapTo) * input.MetresPerUnit;
            var rise = Math.Max(0, containment - mount);
            var drop = Math.Max(0, containment - settings.PanelEntryHeightM);
            var riser = Math.Max(0, input.LevelsCrossed) * hgt.SlabToSlabM;
            var term = 2 * settings.TerminationAllowanceM;
            var total = (horizontal + rise + drop + riser + term) * (1 + settings.SparePct);
            res.Add(new PointLength
            {
                MarkNo = h.MarkNo, HitId = h.Id, Symbol = s.Name, System = TakeoffCounter.SystemOf(s), Item = s.Item.Length > 0 ? s.Item : s.System, Room = h.Room, RoomType = roomType,
                From = input.PanelName, To = $"{TakeoffCounter.TagOf(s)} #{h.MarkNo}", CableSize = size,
                HorizontalM = Math.Round(horizontal, 2), RiseM = Math.Round(rise, 2), DropM = Math.Round(drop, 2), RiserM = Math.Round(riser, 2),
                TerminationM = Math.Round(term, 2), SparePct = settings.SparePct, TotalM = Math.Round(total, 2), Path = path,
            });
        }
        return res;
    }

    public static List<LengthStats> Averages(IEnumerable<PointLength> lengths, Func<PointLength, string> group) =>
        lengths.Where(l => l.Traceable).GroupBy(group).OrderBy(g => g.Key)
            .Select(g => new LengthStats(g.Key, g.Count(), Math.Round(g.Min(x => x.TotalM), 2), Math.Round(g.Average(x => x.TotalM), 2), Math.Round(g.Max(x => x.TotalM), 2), Math.Round(g.Sum(x => x.TotalM), 2)))
            .ToList();

    /// <summary>"n x L" groups for the 15 m length check (LengthCheck.FromGroups), lengths rounded to 0.5 m.</summary>
    public static string LengthGroups(IEnumerable<PointLength> lengths) =>
        string.Join(";", lengths.Where(l => l.Traceable).GroupBy(l => Math.Round(l.TotalM * 2) / 2).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()}x{g.Key.ToString("0.#", CultureInfo.InvariantCulture)}"));

    /// <summary>Estimate for a room not yet measured: typical points x average length of its room type.</summary>
    public static double EstimateRoom(IEnumerable<PointLength> measured, string roomType, string system, int points)
    {
        var same = measured.Where(l => l.Traceable && l.RoomType.Equals(roomType, StringComparison.OrdinalIgnoreCase) && l.System.Equals(system, StringComparison.OrdinalIgnoreCase)).ToList();
        return same.Count == 0 ? 0 : Math.Round(same.Average(l => l.TotalM) * points, 2);
    }
}
