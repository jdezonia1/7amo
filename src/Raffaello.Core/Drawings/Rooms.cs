using System.Globalization;
using System.Text.Json;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] A room outline in sheet units.</summary>
public sealed record RoomPolygon(string Room, List<PointD> Points, string Level = "", double CeilingHeightM = 0)
{
    public RectD Bounds => RectD.Bounds(Points);
    public double Area => Poly.Area(Points);
}

/// <summary>
/// [drawings] Assigns points (symbol hits) and runs to rooms by point-in-polygon. When rooms overlap (a cupboard inside a bedroom
/// outline) the smallest containing room wins.
/// </summary>
public sealed class RoomAssigner
{
    public const string Unassigned = "(NO ROOM)";
    private readonly List<RoomPolygon> _rooms;

    public RoomAssigner(IEnumerable<RoomPolygon> rooms) => _rooms = rooms.Where(r => r.Points.Count >= 3).OrderBy(r => r.Area).ToList();

    public IReadOnlyList<RoomPolygon> Rooms => _rooms;
    public bool IsEmpty => _rooms.Count == 0;

    public string RoomAt(PointD p)
    {
        foreach (var r in _rooms)
            if (r.Bounds.Contains(p) && Poly.Contains(r.Points, p)) return r.Room;
        return Unassigned;
    }

    public RoomPolygon? Get(string room) => _rooms.FirstOrDefault(r => r.Room.Equals(room, StringComparison.OrdinalIgnoreCase));

    /// <summary>Length of a polyline inside each room (sheet units). Pieces outside every room go to <see cref="Unassigned"/>.</summary>
    public Dictionary<string, double> SplitLength(IReadOnlyList<PointD> polyline)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < polyline.Count; i++)
        {
            var a = polyline[i - 1]; var b = polyline[i];
            var total = a.DistanceTo(b); var assigned = 0.0;
            // smallest rooms first: a piece inside a nested room is not counted again in the outer one
            var claimed = new List<RoomPolygon>();
            foreach (var r in _rooms)
            {
                var inside = Poly.LengthInside(r.Points, a, b);
                if (inside <= 1e-9) continue;
                foreach (var c in claimed) inside -= Math.Min(inside, OverlapInside(c, r, a, b));
                if (inside <= 1e-9) continue;
                res[r.Room] = res.GetValueOrDefault(r.Room) + inside;
                assigned += inside;
                claimed.Add(r);
            }
            if (total - assigned > 1e-6) res[Unassigned] = res.GetValueOrDefault(Unassigned) + (total - assigned);
        }
        return res;
    }

    private static double OverlapInside(RoomPolygon inner, RoomPolygon outer, PointD a, PointD b)
    {
        // length of a-b inside both: sample-free approximation via midpoints of the inner pieces
        var ts = new List<double> { 0, 1 };
        foreach (var poly in new[] { inner.Points, outer.Points })
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var d = b - a; var p = poly[j]; var e = poly[i] - p;
                var den = d.X * e.Y - d.Y * e.X;
                if (Math.Abs(den) < 1e-15) continue;
                var w = p - a; var t = (w.X * e.Y - w.Y * e.X) / den; var u = (w.X * d.Y - w.Y * d.X) / den;
                if (t > 0 && t < 1 && u >= 0 && u <= 1) ts.Add(t);
            }
        ts.Sort();
        double s = 0; var len = a.DistanceTo(b);
        for (var k = 0; k + 1 < ts.Count; k++)
        {
            var mid = a + (b - a) * ((ts[k] + ts[k + 1]) / 2);
            if (Poly.Contains(inner.Points, mid) && Poly.Contains(outer.Points, mid)) s += (ts[k + 1] - ts[k]) * len;
        }
        return s;
    }
}

/// <summary>[drawings] Room boundaries: from the tracker room shapes through a calibration, or from a CSV / JSON import.</summary>
public static class RoomBoundaries
{
    /// <summary>Tracker shapes (normalised to the plan image) mapped to the sheet with the plan -> sheet transform.</summary>
    public static List<RoomPolygon> FromTracker(IEnumerable<RoomShape> shapes, string plan, Affine2D planToSheet, string level = "")
    {
        var res = new List<RoomPolygon>();
        foreach (var s in shapes.Where(s => string.IsNullOrEmpty(plan) || s.Plan.Equals(plan, StringComparison.OrdinalIgnoreCase)))
            foreach (var part in (s.Polygons ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var pts = Poly.ParsePoints(part).Select(planToSheet.Apply).ToList();
                if (pts.Count >= 3) res.Add(new RoomPolygon(s.Room, pts, level));
            }
        return res;
    }

    /// <summary>
    /// Fits the plan (normalised) -> sheet transform from clicked pairs "sx,sy&gt;px,py;...": 2 pairs = similarity, 3+ = affine.
    /// </summary>
    public static (Affine2D PlanToSheet, double Residual) Calibrate(IReadOnlyList<(PointD Sheet, PointD Plan)> pairs)
    {
        var t = Affine2D.Fit(pairs.Select(p => p.Plan).ToList(), pairs.Select(p => p.Sheet).ToList());
        return (t, t.MaxResidual(pairs.Select(p => p.Plan).ToList(), pairs.Select(p => p.Sheet).ToList()));
    }

    public static string FormatPairs(IEnumerable<(PointD Sheet, PointD Plan)> pairs) => string.Join(";", pairs.Select(p => $"{p.Sheet}>{p.Plan}"));

    public static List<(PointD Sheet, PointD Plan)> ParsePairs(string? s)
    {
        var res = new List<(PointD, PointD)>();
        foreach (var part in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('>');
            if (ab.Length != 2) continue;
            var a = Poly.ParsePoints(ab[0]); var b = Poly.ParsePoints(ab[1]);
            if (a.Count == 1 && b.Count == 1) res.Add((a[0], b[0]));
        }
        return res;
    }

    /// <summary>
    /// CSV: ROOM, [LEVEL], POINTS ("x,y x,y ...") or X1,Y1,X2,Y2... columns; JSON: [{"room": "...", "level": "...", "points": [[x,y],...]}]
    /// or {"rooms": [...]}. Coordinates in sheet units, or 0..1 of the sheet when every value is &lt;= 1 (then scaled by the sheet size).
    /// </summary>
    public static List<RoomPolygon> Import(string path, double sheetWidth = 0, double sheetHeight = 0)
    {
        var text = File.ReadAllText(path);
        var rooms = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("[") || text.TrimStart().StartsWith("{")
            ? ParseJson(text) : ParseCsv(text);
        var normalised = rooms.Count > 0 && rooms.All(r => r.Points.All(p => p.X >= 0 && p.X <= 1.0 && p.Y >= 0 && p.Y <= 1.0));
        if (normalised && sheetWidth > 0 && sheetHeight > 0)
            rooms = rooms.Select(r => r with { Points = r.Points.Select(p => new PointD(p.X * sheetWidth, p.Y * sheetHeight)).ToList() }).ToList();
        return rooms;
    }

    public static List<RoomPolygon> ParseCsv(string text)
    {
        var res = new List<RoomPolygon>();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return res;
        var head = Csv.Split(lines[0]).Select(h => h.Trim().ToUpperInvariant()).ToList();
        var iRoom = head.FindIndex(h => h is "ROOM" or "NAME" or "LOCATION");
        var iLevel = head.FindIndex(h => h is "LEVEL" or "FLOOR");
        var iPts = head.FindIndex(h => h is "POINTS" or "POLYGON" or "VERTICES");
        var iH = head.FindIndex(h => h.StartsWith("CEILING"));
        var start = iRoom >= 0 ? 1 : 0;
        if (iRoom < 0) iRoom = 0;
        for (var k = start; k < lines.Count; k++)
        {
            var c = Csv.Split(lines[k]);
            if (c.Count <= iRoom || c[iRoom].Trim().Length == 0) continue;
            List<PointD> pts;
            if (iPts >= 0 && iPts < c.Count) pts = Poly.ParsePoints(c[iPts]);
            else
            {
                var nums = c.Where((_, i) => i != iRoom && i != iLevel && i != iH)
                    .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).Where(d => !double.IsNaN(d)).ToList();
                pts = new List<PointD>();
                for (var i = 0; i + 1 < nums.Count; i += 2) pts.Add(new PointD(nums[i], nums[i + 1]));
            }
            var h = iH >= 0 && iH < c.Count && double.TryParse(c[iH], NumberStyles.Float, CultureInfo.InvariantCulture, out var hh) ? hh : 0;
            if (pts.Count >= 3) res.Add(new RoomPolygon(c[iRoom].Trim().ToUpperInvariant(), pts, iLevel >= 0 && iLevel < c.Count ? c[iLevel].Trim() : "", h));
        }
        return res;
    }

    public static List<RoomPolygon> ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("rooms", out var r) ? r : default;
        var res = new List<RoomPolygon>();
        if (arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var e in arr.EnumerateArray())
        {
            var name = Str(e, "room") ?? Str(e, "name") ?? Str(e, "id") ?? "";
            var pts = new List<PointD>();
            var pe = e.TryGetProperty("points", out var p1) ? p1 : e.TryGetProperty("polygon", out var p2) ? p2 : default;
            if (pe.ValueKind == JsonValueKind.Array)
                foreach (var pt in pe.EnumerateArray())
                {
                    if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() >= 2) pts.Add(new PointD(pt[0].GetDouble(), pt[1].GetDouble()));
                    else if (pt.ValueKind == JsonValueKind.Object) pts.Add(new PointD(Num(pt, "x"), Num(pt, "y")));
                }
            else if (pe.ValueKind == JsonValueKind.String) pts = Poly.ParsePoints(pe.GetString());
            var level = Str(e, "level") ?? "";
            var h = e.TryGetProperty("ceiling_height_m", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetDouble() : 0;
            if (name.Length > 0 && pts.Count >= 3) res.Add(new RoomPolygon(name.Trim().ToUpperInvariant(), pts, level, h));
        }
        return res;
    }

    internal static string? Str(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.GetRawText(), _ => null };
        return null;
    }

    internal static double Num(JsonElement e, string name)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (p.Value.ValueKind == JsonValueKind.Number) return p.Value.GetDouble();
                if (p.Value.ValueKind == JsonValueKind.String && double.TryParse(p.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            }
        return 0;
    }

    public static DwgRoom ToEntity(RoomPolygon r, long sheetId, string source) =>
        new() { SheetId = sheetId, Room = r.Room, Level = r.Level, Polygon = Poly.Format(r.Points), Source = source, CeilingHeightM = r.CeilingHeightM };

    public static RoomPolygon FromEntity(DwgRoom r) => new(r.Room, Poly.ParsePoints(r.Polygon), r.Level, r.CeilingHeightM);
}
