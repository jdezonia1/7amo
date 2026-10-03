using System.Globalization;
using System.Text.Json;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tracker;

/// <summary>One level of a plan package: a high-resolution background picture and the location shapes on it.</summary>
public sealed record PlanPackageLevel(string Level, string Name, string Revision, string BackgroundPath, byte[] Png, int Width, int Height, List<RoomShape> Shapes);

public sealed class PlanPackageResult
{
    public string Building { get; set; } = "";
    public string Revision { get; set; } = "";
    public List<PlanPackageLevel> Levels { get; } = new();
    public List<string> Issues { get; } = new();
    public string Summary => $"{Building} {Revision}: {Levels.Count} levels, {Levels.Sum(l => l.Shapes.Count)} shapes" + (Issues.Count > 0 ? $", {Issues.Count} issues" : "");
}

/// <summary>
/// Plans per building x level that can be swapped for a newer revision (TRACKING > PLANS). Stored in the existing tables:
/// <see cref="PlanImage"/> (Plan = "LEVEL@REV", Name = revision, Png = background) and <see cref="RoomShape"/> (polygons normalised 0..1
/// to the picture, ShapeName = kind GUESTROOM / PUBLIC / APARTMENT / TERRACE ..., Description = parent location, e.g. the public pool).
///
/// Package (RECON_TOOLS\PLANS): manifest.json { "building", "revision", "levels": [ { "level", "name", "background_png" | "folder", "shapes" } ] }
/// (levels may also be just sub-folders holding background.png + shapes.json); shapes.json { "image": { "width", "height" },
/// "shapes": [ { "location" | "name", "kind", "parent", "points" | "polygon" | "polygons" } ] } with points in image pixels or 0..1.
/// SVG backgrounds are not drawn (WPF has no SVG renderer here) - the PNG is used; tiled PNGs need one stitched background.png.
/// </summary>
public static class PlanPackage
{
    public static string PlanCode(string level, string revision) => revision.Length > 0 ? $"{level.Trim().ToUpperInvariant()}@{revision.Trim().ToUpperInvariant()}" : level.Trim().ToUpperInvariant();

    public static (string Level, string Revision) Split(string plan)
    {
        var i = (plan ?? "").IndexOf('@');
        return i < 0 ? (plan ?? "", "") : (plan![..i], plan[(i + 1)..]);
    }

    /// <summary>Width and height from a PNG header (0, 0 when not a PNG).</summary>
    public static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50) return (0, 0);
        int Be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        return (Be(16), Be(20));
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : null;
        return null;
    }

    private static JsonElement? Prop(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names) if (e.TryGetProperty(n, out var v)) return v;
        return null;
    }

    public static PlanPackageResult Read(string folderOrManifest, string? building = null)
    {
        var res = new PlanPackageResult();
        var manifestPath = File.Exists(folderOrManifest) ? folderOrManifest : Path.Combine(folderOrManifest, "manifest.json");
        var root = File.Exists(folderOrManifest) ? Path.GetDirectoryName(folderOrManifest)! : folderOrManifest;
        var levels = new List<(string Level, string Name, string Folder, string? Png, string? Shapes, string Rev)>();
        if (File.Exists(manifestPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var m = doc.RootElement;
            res.Building = (building ?? Str(m, "building", "Building") ?? "").Trim().ToUpperInvariant();
            res.Revision = (Str(m, "revision", "rev", "Revision") ?? "").Trim();
            if (Prop(m, "levels", "Levels") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var l in arr.EnumerateArray())
                {
                    var code = Str(l, "level", "code", "id", "Level") ?? "";
                    var folder = Str(l, "folder", "dir") ?? code;
                    levels.Add((code, Str(l, "name", "title") ?? code, Path.Combine(root, folder), Str(l, "background_png", "png", "background"), Str(l, "shapes", "shapes_json"),
                        Str(l, "revision", "rev") ?? res.Revision));
                }
        }
        else res.Building = (building ?? "").Trim().ToUpperInvariant();
        if (levels.Count == 0 && Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root).OrderBy(x => x))
                if (File.Exists(Path.Combine(d, "shapes.json")))
                    levels.Add((Path.GetFileName(d), Path.GetFileName(d), d, null, null, res.Revision));
        if (res.Building.Length == 0) res.Issues.Add("building not given (manifest 'building')");
        foreach (var (level, name, folder, png, shapes, rev) in levels)
        {
            if (level.Length == 0) { res.Issues.Add("a level without a code was skipped"); continue; }
            string Resolve(string? rel, string def) => rel is { Length: > 0 } ? (Path.IsPathRooted(rel) ? rel : File.Exists(Path.Combine(root, rel)) ? Path.Combine(root, rel) : Path.Combine(folder, rel)) : Path.Combine(folder, def);
            var pngPath = Resolve(png, "background.png");
            var shapesPath = Resolve(shapes, "shapes.json");
            if (!File.Exists(pngPath)) { res.Issues.Add($"{level}: no background picture ({Path.GetFileName(pngPath)})"); continue; }
            var bytes = File.ReadAllBytes(pngPath);
            var (w, h) = PngSize(bytes);
            var list = new List<RoomShape>();
            var code = PlanCode(level, rev);
            if (File.Exists(shapesPath))
            {
                using var sd = JsonDocument.Parse(File.ReadAllText(shapesPath));
                var s = sd.RootElement;
                if (Prop(s, "image") is { } img)
                {
                    if (int.TryParse(Str(img, "width", "w"), out var iw) && iw > 0) w = w > 0 ? w : iw;
                    if (int.TryParse(Str(img, "height", "h"), out var ih) && ih > 0) h = h > 0 ? h : ih;
                }
                var shapeArr = s.ValueKind == JsonValueKind.Array ? s : Prop(s, "shapes", "rooms", "locations") ?? default;
                if (shapeArr.ValueKind == JsonValueKind.Array)
                    foreach (var sh in shapeArr.EnumerateArray())
                    {
                        var loc = (Str(sh, "location", "name", "room", "code") ?? "").Trim().ToUpperInvariant();
                        if (loc.Length == 0) continue;
                        var polys = new List<List<(double X, double Y)>>();
                        if (Prop(sh, "polygons") is { ValueKind: JsonValueKind.Array } pa) foreach (var p in pa.EnumerateArray()) polys.Add(Points(p));
                        else if (Prop(sh, "points", "polygon", "image_points", "points_px", "pixels") is { ValueKind: JsonValueKind.Array } pts) polys.Add(Points(pts));
                        polys = polys.Where(p => p.Count >= 3).ToList();
                        if (polys.Count == 0) { res.Issues.Add($"{level} {loc}: no polygon"); continue; }
                        var norm = polys.SelectMany(p => p).Any(pt => pt.X > 1.5 || pt.Y > 1.5);
                        if (norm && (w <= 0 || h <= 0)) { res.Issues.Add($"{level}: picture size unknown - shapes skipped"); break; }
                        var np = polys.Select(p => p.Select(pt => norm ? (pt.X / w, pt.Y / h) : pt).ToList()).ToList();
                        var all = np.SelectMany(p => p).ToList();
                        list.Add(new RoomShape
                        {
                            Building = res.Building, Room = loc, Plan = code, ShapeName = (Str(sh, "kind", "type") ?? "").Trim().ToUpperInvariant(),
                            Description = (Str(sh, "parent", "pool") ?? "").Trim().ToUpperInvariant(),
                            Polygons = string.Join("|", np.Select(p => string.Join(" ", p.Select(pt => $"{pt.Item1.ToString("0.######", CultureInfo.InvariantCulture)},{pt.Item2.ToString("0.######", CultureInfo.InvariantCulture)}")))),
                            Left = all.Min(x => x.Item1), Top = all.Min(x => x.Item2), Right = all.Max(x => x.Item1), Bottom = all.Max(x => x.Item2),
                        });
                    }
            }
            else res.Issues.Add($"{level}: no shapes.json - the picture is imported without rooms");
            res.Levels.Add(new PlanPackageLevel(level.Trim().ToUpperInvariant(), name, rev, pngPath, bytes, w, h, list));
        }
        return res;
    }

    private static List<(double X, double Y)> Points(JsonElement arr)
    {
        var res = new List<(double, double)>();
        if (arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2) res.Add((p[0].GetDouble(), p[1].GetDouble()));
            else if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("x", out var x) && p.TryGetProperty("y", out var y)) res.Add((x.GetDouble(), y.GetDouble()));
        }
        return res;
    }

    /// <summary>Stores the package: each level x revision replaces the same level x revision (other revisions stay selectable).</summary>
    public static (int Plans, int Shapes) Commit(PlanPackageResult r, IProjectStore store)
    {
        var codes = r.Levels.Select(l => PlanCode(l.Level, l.Revision)).ToHashSet();
        var oldPlans = store.All<PlanImage>().Where(p => p.Building == r.Building && codes.Contains(p.Plan)).ToList();
        var oldShapes = store.All<RoomShape>().Where(s => s.Building == r.Building && codes.Contains(s.Plan)).ToList();
        int shapes = 0;
        store.Batch(w =>
        {
            foreach (var p in oldPlans) w.Delete(p);
            foreach (var s in oldShapes) w.Delete(s);
            foreach (var l in r.Levels)
            {
                w.Insert(new PlanImage { Building = r.Building, Plan = PlanCode(l.Level, l.Revision), Name = l.Revision, Caption = l.Name, Png = l.Png, Width = l.Width, Height = l.Height });
                shapes += w.InsertMany(l.Shapes);
            }
        }, $"Plan package {r.Summary}");
        return (r.Levels.Count, shapes);
    }

    /// <summary>A new revision of one level: a new picture; the shapes of <paramref name="copyShapesFrom"/> are copied (align them afterwards).</summary>
    public static string AddRevision(IProjectStore store, string building, string level, string revision, byte[] png, string caption, string? copyShapesFrom)
    {
        var code = PlanCode(level, revision);
        var (w, h) = PngSize(png);
        var copies = copyShapesFrom is null ? new List<RoomShape>() : store.All<RoomShape>().Where(s => s.Building == building && s.Plan == copyShapesFrom)
            .Select(s => new RoomShape { Building = s.Building, Room = s.Room, ShapeName = s.ShapeName, Description = s.Description, Plan = code, Polygons = s.Polygons, Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom }).ToList();
        var old = store.All<PlanImage>().Where(p => p.Building == building && p.Plan == code).ToList();
        var oldShapes = store.All<RoomShape>().Where(s => s.Building == building && s.Plan == code).ToList();
        store.Batch(wr =>
        {
            foreach (var p in old) wr.Delete(p);
            foreach (var s in oldShapes) wr.Delete(s);
            wr.Insert(new PlanImage { Building = building, Plan = code, Name = revision, Caption = caption, Png = png, Width = w, Height = h });
            wr.InsertMany(copies);
        }, $"Plan {building} {level} new revision {revision} ({copies.Count} shapes copied from {copyShapesFrom})");
        return code;
    }

    /// <summary>Moves / scales the shapes of a plan (p' = (p - 0.5) x scale + 0.5 + offset, in 0..1 picture units) - aligns copied shapes to a shifted revision.</summary>
    public static string Transform(string polygons, double scaleX, double scaleY, double offsetX, double offsetY) =>
        string.Join("|", (polygons ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(poly => string.Join(" ",
            poly.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(pt =>
            {
                var xy = pt.Split(',');
                if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return pt;
                x = (x - 0.5) * scaleX + 0.5 + offsetX;
                y = (y - 0.5) * scaleY + 0.5 + offsetY;
                return $"{x.ToString("0.######", CultureInfo.InvariantCulture)},{y.ToString("0.######", CultureInfo.InvariantCulture)}";
            }))));

    public static int Align(IProjectStore store, string building, string plan, double scaleX, double scaleY, double offsetX, double offsetY)
    {
        var shapes = store.All<RoomShape>().Where(s => s.Building == building && s.Plan == plan).ToList();
        store.Batch(w =>
        {
            foreach (var s in shapes)
            {
                s.Polygons = Transform(s.Polygons, scaleX, scaleY, offsetX, offsetY);
                s.Left = (s.Left - 0.5) * scaleX + 0.5 + offsetX; s.Right = (s.Right - 0.5) * scaleX + 0.5 + offsetX;
                s.Top = (s.Top - 0.5) * scaleY + 0.5 + offsetY; s.Bottom = (s.Bottom - 0.5) * scaleY + 0.5 + offsetY;
                w.Update(s);
            }
        }, $"Plan {building} {plan}: {shapes.Count} shapes aligned (scale {scaleX:0.###} x {scaleY:0.###}, offset {offsetX:0.####}, {offsetY:0.####})");
        return shapes.Count;
    }
}

/// <summary>Bright, clearly distinct colours per subcontractor for the plans (independent of the app theme). Fixed by name order; editable.</summary>
public static class SubPalette
{
    public static readonly string[] Colours =
    {
        "#E6194B", "#3CB44B", "#FFE119", "#4363D8", "#F58231", "#911EB4", "#42D4F4", "#F032E6", "#BFEF45", "#FABED4", "#469990", "#DCBEFF",
        "#9A6324", "#FFFAC8", "#800000", "#AAFFC3", "#808000", "#FFD8B1", "#000075", "#A9A9A9", "#00A36C", "#FF6F61", "#6A5ACD", "#FFB000", "#00CED1",
    };

    /// <summary>Colour per subcontractor: saved choice first, else the palette in alphabetical order (stable across plans and sessions).</summary>
    public static Dictionary<string, string> Assign(IEnumerable<string> subcontractors, IReadOnlyDictionary<string, string>? saved = null)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var s in subcontractors.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            res[s] = saved != null && saved.TryGetValue(s, out var c) && c.StartsWith('#') ? c : Colours[i % Colours.Length];
            i++;
        }
        return res;
    }

    public static string Next(string colour) => Colours[(Array.IndexOf(Colours, colour) + 1 + Colours.Length) % Colours.Length];
}