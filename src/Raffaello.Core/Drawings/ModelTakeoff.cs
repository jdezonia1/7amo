using System.Globalization;
using System.Text.Json;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Takeoff read from a model (CAD, IFC, Revit add-in JSON): hits, runs and rooms in sheet units + metres per unit.</summary>
public sealed class ModelTakeoff
{
    public List<DwgHit> Hits { get; } = new();
    public List<DwgRun> Runs { get; } = new();
    public List<RoomPolygon> Rooms { get; } = new();
    public double MetresPerUnit { get; set; }
    public RectD Bounds { get; set; }
    /// <summary>Line work for the review picture (sheet units).</summary>
    public List<(PointD A, PointD B, Rgb Colour)> Background { get; } = new();
    /// <summary>JSON / IFC items that matched no library symbol or linear class (shown to the user, never dropped silently).</summary>
    public List<string> Unmatched { get; } = new();
    public List<string> Notes { get; } = new();

    // ------------------------------------------------------------------ CAD

    /// <summary>
    /// DWG / DXF: block references by name, exploded blocks by geometry signature, linear runs by layer, rooms from closed polylines.
    /// Sheet units = model units with y flipped (screen y down).
    /// </summary>
    public static ModelTakeoff FromCad(CadModel m, IReadOnlyList<DwgSymbol> symbols, IReadOnlyList<DwgLinearClass> classes, string roomLayers = "*ROOM*;*AREA*;*SPACE*", double signatureTolerance = 0.06)
    {
        var t = new ModelTakeoff { MetresPerUnit = m.MetresPerUnit };
        PointD F(PointD p) => new(p.X, -p.Y);
        RectD FB(RectD r) => new(r.X, -r.Bottom, r.W, r.H);
        foreach (var h in CadTakeoff.BlockHits(m, symbols).Concat(CadTakeoff.ExplodedHits(m, symbols, signatureTolerance)))
        {
            var b = FB(new RectD(h.X, h.Y, h.W, h.H));
            h.X = b.X; h.Y = b.Y;
            t.Hits.Add(h);
        }
        var tol = m.MetresPerUnit > 0 ? 0.005 / m.MetresPerUnit : 1e-3 * Math.Max(1, Math.Max(m.Bounds.W, m.Bounds.H));
        foreach (var r in LinearTakeoff.FromLayers(m.Segments.Where(s => s.InsertIndex < 0).Select(s => (s.A, s.B, s.Layer)), classes, m.MetresPerUnit, tol))
        {
            r.Points = Poly.Format(Poly.ParsePoints(r.Points).Select(F));
            t.Runs.Add(r);
        }
        foreach (var room in CadTakeoff.Rooms(m, roomLayers)) t.Rooms.Add(room with { Points = room.Points.Select(F).ToList() });
        foreach (var s in m.Segments) t.Background.Add((F(s.A), F(s.B), new Rgb(90, 90, 90)));
        foreach (var c in m.Circles)
            for (var i = 0; i < 16; i++)
            {
                double a0 = i * Math.PI / 8, a1 = (i + 1) * Math.PI / 8;
                t.Background.Add((F(c.Center + new PointD(Math.Cos(a0), Math.Sin(a0)) * c.Radius), F(c.Center + new PointD(Math.Cos(a1), Math.Sin(a1)) * c.Radius), new Rgb(90, 90, 90)));
            }
        t.Bounds = FB(m.Bounds);
        if (m.MetresPerUnit <= 0) t.Notes.Add("$INSUNITS is unitless - set the scale (metres per unit) before using lengths.");
        t.Notes.AddRange(m.Notes);
        return t;
    }

    // ------------------------------------------------------------------ IFC

    /// <summary>IFC: elements by class (counts per space), carriers / cables by class with their Length quantity. Units: metres.</summary>
    public static ModelTakeoff FromIfc(IfcModel m, IReadOnlyList<DwgSymbol> symbols, IReadOnlyList<DwgLinearClass> classes)
    {
        var t = new ModelTakeoff { MetresPerUnit = 1 };
        PointD F(PointD p) => new(p.X, -p.Y);
        foreach (var s in m.Spaces.Where(s => s.Footprint.Count >= 3)) t.Rooms.Add(new RoomPolygon(s.Name.Trim().ToUpperInvariant(), s.Footprint.Select(F).ToList(), s.Storey));
        foreach (var e in m.Elements)
        {
            var lin = classes.FirstOrDefault(c => c.Active && !string.IsNullOrWhiteSpace(c.IfcClasses) && IfcModel.Matches(e, c.IfcClasses));
            if (lin != null)
            {
                var p = F(e.Position ?? new PointD(0, 0));
                t.Runs.Add(new DwgRun { ClassId = lin.Id, ClassName = lin.Name, Points = Poly.Format(new[] { p }), LengthUnits = e.LengthM, LengthM = Math.Round(e.LengthM, 3),
                    Room = e.Space.Trim().ToUpperInvariant(), Origin = DwgOrigins.Ifc, Note = $"{e.IfcClass} #{e.Id} {e.Name} {e.Size}".Trim() });
                continue;
            }
            var sym = symbols.FirstOrDefault(s => s.Active && !string.IsNullOrWhiteSpace(s.IfcClasses) && IfcModel.Matches(e, s.IfcClasses));
            if (sym != null)
            {
                var p = F(e.Position ?? new PointD(0, 0));
                t.Hits.Add(new DwgHit { SymbolId = sym.Id, SymbolName = sym.Name, X = p.X - 0.15, Y = p.Y - 0.15, W = 0.3, H = 0.3, Score = 1, Origin = DwgOrigins.Ifc,
                    Room = e.Space.Trim().ToUpperInvariant(), Attributes = $"{e.IfcClass} #{e.Id} {e.Name} {e.ObjectType} {e.PredefinedType}".Trim() });
            }
            else t.Unmatched.Add($"{e.IfcClass} #{e.Id} {e.Name} ({e.ObjectType} {e.PredefinedType})".Trim());
        }
        var pts = t.Rooms.SelectMany(r => r.Points).Concat(t.Hits.Select(h => h.Center)).ToList();
        t.Bounds = pts.Count > 0 ? RectD.Bounds(pts) : new RectD(0, 0, 1, 1);
        foreach (var r in t.Rooms) for (var i = 0; i < r.Points.Count; i++) t.Background.Add((r.Points[i], r.Points[(i + 1) % r.Points.Count], new Rgb(90, 90, 90)));
        return t;
    }

    // ------------------------------------------------------------------ Revit add-in JSON

    public sealed record JsonCount(string Room, string System, string Item, double Qty, string Mount);
    public sealed record JsonLinear(string Room, string Level, string System, string Item, string Size, double LengthM);

    /// <summary>
    /// The Revit add-in export: {"units":"m","rooms":[{"id","level","polygon":[[x,y],..]}],"counts":[{"room","system","item","qty"}],
    /// "linear":[{"room"|"level","system","item","size","length_m"}]}. Counts and lengths come in ready; they are turned into hits / runs
    /// of the matching library symbol / linear class (by name or tag), so the same review, stage rules and PROJECT QTY diff apply.
    /// </summary>
    public static ModelTakeoff FromRevitJson(string json, IReadOnlyList<DwgSymbol> symbols, IReadOnlyList<DwgLinearClass> classes)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var t = new ModelTakeoff { MetresPerUnit = 1 };
        var unitScale = (RoomBoundaries.Str(root, "units") ?? "m").ToLowerInvariant() switch { "mm" => 0.001, "cm" => 0.01, "ft" => 0.3048, _ => 1.0 };
        if (root.TryGetProperty("rooms", out var rooms))
            foreach (var r in RoomBoundaries.ParseJson(rooms.GetRawText()))
                t.Rooms.Add(r with { Points = r.Points.Select(p => new PointD(p.X * unitScale, -p.Y * unitScale)).ToList() });
        var centre = t.Rooms.ToDictionary(r => r.Room, r => new PointD(r.Points.Average(p => p.X), r.Points.Average(p => p.Y)), StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Array)
            foreach (var c in counts.EnumerateArray())
            {
                var jc = new JsonCount((RoomBoundaries.Str(c, "room") ?? "").Trim().ToUpperInvariant(), (RoomBoundaries.Str(c, "system") ?? "").Trim().ToUpperInvariant(),
                    (RoomBoundaries.Str(c, "item") ?? "").Trim().ToUpperInvariant(), RoomBoundaries.Num(c, "qty"), (RoomBoundaries.Str(c, "mount") ?? "").Trim().ToUpperInvariant());
                var sym = symbols.FirstOrDefault(s => s.Name.Equals(jc.Item, StringComparison.OrdinalIgnoreCase) || s.Tag.Equals(jc.Item, StringComparison.OrdinalIgnoreCase))
                          ?? symbols.FirstOrDefault(s => s.BlockNames.Length > 0 && LinearTakeoff.Like(jc.Item, s.BlockNames));
                if (sym is null) { t.Unmatched.Add($"count {jc.Room} {jc.System} {jc.Item} x{jc.Qty}"); continue; }
                var at = centre.GetValueOrDefault(jc.Room);
                var n = (int)Math.Round(jc.Qty);
                for (var i = 0; i < n; i++)
                    t.Hits.Add(new DwgHit { SymbolId = sym.Id, SymbolName = sym.Name, X = at.X - 0.15 + 0.35 * (i % 8), Y = at.Y - 0.15 + 0.35 * (i / 8), W = 0.3, H = 0.3, Score = 1,
                        Room = jc.Room, Origin = DwgOrigins.Json, Attributes = $"{jc.System} {jc.Item}", MountingHeightM = jc.Mount == "C" ? 99 : 0 });
            }
        if (root.TryGetProperty("linear", out var lin) && lin.ValueKind == JsonValueKind.Array)
            foreach (var l in lin.EnumerateArray())
            {
                var jl = new JsonLinear((RoomBoundaries.Str(l, "room") ?? "").Trim().ToUpperInvariant(), RoomBoundaries.Str(l, "level") ?? "", (RoomBoundaries.Str(l, "system") ?? "").Trim().ToUpperInvariant(),
                    (RoomBoundaries.Str(l, "item") ?? "").Trim().ToUpperInvariant(), (RoomBoundaries.Str(l, "size") ?? "").Trim(), RoomBoundaries.Num(l, "length_m"));
                var cls = classes.FirstOrDefault(c => c.Name.Equals($"{jl.Item} {jl.Size}".Trim(), StringComparison.OrdinalIgnoreCase))
                          ?? classes.FirstOrDefault(c => c.System.Equals(jl.Item, StringComparison.OrdinalIgnoreCase) && c.Size.Equals(jl.Size, StringComparison.OrdinalIgnoreCase))
                          ?? classes.FirstOrDefault(c => c.Name.Equals(jl.Item, StringComparison.OrdinalIgnoreCase));
                if (cls is null) { t.Unmatched.Add($"linear {jl.Room}{jl.Level} {jl.System} {jl.Item} {jl.Size} {jl.LengthM:0.##} m"); continue; }
                var at = centre.GetValueOrDefault(jl.Room);
                t.Runs.Add(new DwgRun { ClassId = cls.Id, ClassName = cls.Name, Points = Poly.Format(new[] { at }), LengthUnits = jl.LengthM, LengthM = Math.Round(jl.LengthM, 3),
                    Room = jl.Room, Origin = DwgOrigins.Json, Note = $"{jl.System} {jl.Item} {jl.Size} {(jl.Room.Length == 0 ? "level " + jl.Level : "")}".Trim() });
            }
        var pts = t.Rooms.SelectMany(r => r.Points).Concat(t.Hits.Select(h => h.Center)).ToList();
        t.Bounds = pts.Count > 0 ? RectD.Bounds(pts) : new RectD(0, 0, 1, 1);
        foreach (var r in t.Rooms) for (var i = 0; i < r.Points.Count; i++) t.Background.Add((r.Points[i], r.Points[(i + 1) % r.Points.Count], new Rgb(90, 90, 90)));
        return t;
    }

    /// <summary>Picture of a model takeoff (background line work + room outlines) for the review screen and the takeoff PDF.</summary>
    public ColorImage Render(int maxSide, out Affine2D sheetToImage)
    {
        var b = Bounds.W > 0 && Bounds.H > 0 ? Bounds : new RectD(0, 0, 1, 1);
        var pad = Math.Max(b.W, b.H) * 0.03;
        var k = (maxSide - 2) / (Math.Max(b.W, b.H) + 2 * pad);
        int w = Math.Max(16, (int)Math.Ceiling((b.W + 2 * pad) * k)), h = Math.Max(16, (int)Math.Ceiling((b.H + 2 * pad) * k));
        sheetToImage = new Affine2D(k, 0, (pad - b.X) * k, 0, k, (pad - b.Y) * k);
        var img = new ColorImage(w, h);
        foreach (var (a, c, col) in Background)
        {
            var pa = sheetToImage.Apply(a); var pc = sheetToImage.Apply(c);
            img.Line(pa.X, pa.Y, pc.X, pc.Y, col, 1);
        }
        return img;
    }

    public static string FormatM(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
