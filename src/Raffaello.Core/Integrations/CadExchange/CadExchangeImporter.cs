using System.Globalization;
using System.Text;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Integrations.CadExchange;

public static class CadImportModes
{
    /// <summary>CAD counts replace PROJECT QTY for the keys they cover (other keys untouched).</summary>
    public const string Replace = "REPLACE";
    /// <summary>Only keys without a PROJECT QTY yet are filled.</summary>
    public const string AddMissing = "ADD_MISSING";
}

public sealed class CadImportOptions
{
    public string Building { get; set; } = "";
    /// <summary>Stages a design count applies to (a socket is one point at 1ST FIX and one at 2ND FIX).</summary>
    public List<string> Stages { get; set; } = new() { "1ST FIX", "2ND FIX" };
    public string Mode { get; set; } = CadImportModes.Replace;
    public bool CreateRooms { get; set; } = true;
    public bool ImportShapes { get; set; } = true;
    public bool ImportQuantities { get; set; } = true;
    /// <summary>Reported area vs polygon area tolerance (0.05 = 5 %).</summary>
    public double AreaTolerance { get; set; } = 0.05;
}

public sealed class CadQtyChange
{
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public double? OldQty { get; set; }
    public double NewQty { get; set; }
    /// <summary>ADD / UPDATE / SAME / KEEP (ADD_MISSING mode: existing value kept).</summary>
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class CadImportPreview
{
    public CadExchangeFile File { get; init; } = new();
    public string FileName { get; init; } = "";
    public CadImportOptions Options { get; init; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<Room> NewRooms { get; } = new();
    public List<RoomShape> Shapes { get; } = new();
    public List<CadQtyChange> QtyChanges { get; } = new();
    public bool CanCommit => Errors.Count == 0;
    public Dictionary<string, double> PointsBySystem => File.Counts.GroupBy(c => c.System.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty));
    public string Summary =>
        $"{File.Source.Drawing} ({File.Source.Application}): {File.Rooms.Count} rooms, {File.Counts.Sum(c => c.Qty):N0} points in {File.Counts.Count} counts, " +
        $"{NewRooms.Count} new rooms, {QtyChanges.Count(q => q.Action is "ADD" or "UPDATE")} PROJECT QTY changes" +
        (File.Unassigned.Count > 0 ? $", {File.Unassigned.Sum(u => u.Qty):N0} points outside rooms" : "") +
        (Errors.Count > 0 ? $" - {Errors.Count} ERROR(S)" : Warnings.Count > 0 ? $" - {Warnings.Count} warning(s)" : "");
}

/// <summary>
/// Reads a raffaello-cad-exchange file (AutoCAD add-in, Revit add-in, Dynamo script), validates it and turns it into rooms,
/// room shapes for the plan view and PROJECT QTY (room x stage x system) - previewed against what is stored before anything is
/// written.
/// </summary>
public static class CadExchangeImporter
{
    public static CadImportPreview Preview(string path, ProjectSnapshot s, CadImportOptions o)
    {
        CadExchangeFile f;
        try { f = CadExchangeJson.Read(path); }
        catch (System.Text.Json.JsonException ex)
        {
            var bad = new CadImportPreview { FileName = Path.GetFileName(path), Options = o };
            bad.Errors.Add($"Not a valid JSON file: {ex.Message}");
            return bad;
        }
        return Preview(f, Path.GetFileName(path), s, o);
    }

    public static CadImportPreview Preview(CadExchangeFile f, string fileName, ProjectSnapshot s, CadImportOptions o)
    {
        var p = new CadImportPreview { File = f, FileName = fileName, Options = o };
        Validate(f, o, p.Errors, p.Warnings);
        if (p.Errors.Count > 0) return p;
        var building = (o.Building.Length > 0 ? o.Building : f.Building).Trim().ToUpperInvariant();
        if (building.Length == 0) { p.Errors.Add("Building is not set (file and import options are both empty)."); return p; }

        var known = s.Rooms.Where(r => r.Building.Equals(building, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Code.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var levels = f.Levels.GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var r in f.Rooms)
        {
            if (known.ContainsKey(r.Id.Trim())) continue;
            if (!o.CreateRooms) { p.Warnings.Add($"{r.Id} is not a room of {building} (creating rooms is off) - its counts are skipped."); continue; }
            var level = levels.TryGetValue(r.Level, out var lv) ? lv : null;
            p.NewRooms.Add(new Room
            {
                Building = building, Code = r.Id.Trim(), Level = r.Level, RoomType = r.Type, AreaType = r.AreaType.ToUpperInvariant(),
                Plan = level?.Plan is { Length: > 0 } plan ? plan : r.Level,
            });
        }
        var rooms = known.Keys.Concat(p.NewRooms.Select(r => r.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (o.ImportShapes)
            foreach (var g in f.Rooms.GroupBy(r => r.Level, StringComparer.OrdinalIgnoreCase))
            {
                var level = levels.TryGetValue(g.Key, out var lv) ? lv : null;
                var frame = level?.Frame;
                var all = g.SelectMany(r => CadExchangeJson.Ring(r.Polygon)).ToList();
                var (x0, y0, x1, y1) = frame is null ? CadGeometry.Bounds(all) : (frame.MinX, frame.MinY, frame.MaxX, frame.MaxY);
                if (frame is null) { var mx = (x1 - x0) * 0.02; var my = (y1 - y0) * 0.02; x0 -= mx; x1 += mx; y0 -= my; y1 += my; }
                double w = Math.Max(1e-9, x1 - x0), h = Math.Max(1e-9, y1 - y0);
                var plan = level?.Plan is { Length: > 0 } pl ? pl : g.Key;
                foreach (var r in g.Where(r => rooms.Contains(r.Id.Trim())))
                {
                    var ring = CadExchangeJson.Ring(r.Polygon).Select(pt => new P2((pt.X - x0) / w, (y1 - pt.Y) / h)).ToList();
                    var b = CadGeometry.Bounds(ring);
                    p.Shapes.Add(new RoomShape
                    {
                        Building = building, Room = r.Id.Trim(), ShapeName = $"RM_{r.Id.Trim()}-{plan}", Plan = plan,
                        Polygons = string.Join(" ", ring.Select(pt => pt.X.ToString("0.#####", CultureInfo.InvariantCulture) + "," + pt.Y.ToString("0.#####", CultureInfo.InvariantCulture))),
                        Left = b.MinX, Top = b.MinY, Right = b.MaxX, Bottom = b.MaxY,
                        Description = $"CAD {f.Source.Drawing} {r.Name}".Trim(),
                    });
                }
            }

        if (o.ImportQuantities)
        {
            var existing = s.RoomQtys.Where(q => q.Building.Equals(building, StringComparison.OrdinalIgnoreCase))
                .GroupBy(q => LedgerKeys.Key(q.Room, q.Stage, q.Item)).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
            var wanted = new Dictionary<string, (string Room, string Stage, string Item, double Qty, List<string> Detail)>();
            foreach (var c in f.Counts)
            {
                if (!rooms.Contains(c.Room.Trim())) { p.Warnings.Add($"Count {c.System} {c.Item} x{c.Qty:0.##} for unknown room {c.Room} skipped."); continue; }
                var stages = c.Stage.Trim().Length > 0 ? new List<string> { c.Stage.Trim().ToUpperInvariant() } : o.Stages.Select(x => x.Trim().ToUpperInvariant()).ToList();
                foreach (var st in stages)
                {
                    var key = LedgerKeys.Key(c.Room, st, c.System);
                    if (!wanted.TryGetValue(key, out var cur)) cur = (c.Room.Trim(), st, c.System.Trim().ToUpperInvariant(), 0, new List<string>());
                    cur.Qty += c.Qty;
                    cur.Detail.Add($"{c.Item} {c.Qty:0.##}{(c.Method is { Length: > 0 } m && m != "BLOCK" ? " (" + m + ")" : "")}");
                    wanted[key] = cur;
                }
            }
            foreach (var kv in wanted.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var (room, stage, item, qty, detail) = kv.Value;
                double? old = existing.TryGetValue(kv.Key, out var e) ? e : null;
                var action = old is null ? "ADD" : Math.Abs(old.Value - qty) < 1e-9 ? "SAME" : o.Mode == CadImportModes.AddMissing ? "KEEP" : "UPDATE";
                p.QtyChanges.Add(new CadQtyChange { Room = room, Stage = stage, Item = item, OldQty = old, NewQty = Math.Round(qty, 4), Action = action, Detail = string.Join("; ", detail) });
            }
        }
        foreach (var u in f.Unassigned.Where(u => u.Qty > 0))
            p.Warnings.Add($"{u.Qty:0.##} x {u.System} {u.Item} on {u.Level} are outside every room ({u.Reason}).");
        return p;
    }

    public static void Validate(CadExchangeFile f, CadImportOptions o, List<string> errors, List<string> warnings)
    {
        if (!string.Equals(f.Format, CadExchangeFile.FormatName, StringComparison.Ordinal)) errors.Add($"format must be \"{CadExchangeFile.FormatName}\" (found \"{f.Format}\").");
        if (f.Version < 1 || f.Version > CadExchangeFile.CurrentVersion) errors.Add($"version {f.Version} is not supported (this Raffaello reads 1..{CadExchangeFile.CurrentVersion}).");
        if (f.Rooms.Count == 0 && f.Counts.Count == 0) errors.Add("The file has no rooms and no counts.");
        var levelIds = f.Levels.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var areaFactor = (f.Source.Units ?? "mm").ToLowerInvariant() switch { "m" => 1.0, "ft" or "feet" => 0.09290304, "cm" => 1e-4, _ => 1e-6 };
        foreach (var r in f.Rooms)
        {
            var id = r.Id?.Trim() ?? "";
            if (id.Length == 0) { errors.Add("A room has no id."); continue; }
            if (!ids.Add(id)) errors.Add($"Room {id} appears twice.");
            if (string.IsNullOrWhiteSpace(r.Level)) errors.Add($"Room {id} has no level.");
            else if (levelIds.Count > 0 && !levelIds.Contains(r.Level)) warnings.Add($"Room {id}: level {r.Level} is not in the levels list.");
            var ring = CadExchangeJson.Ring(r.Polygon);
            if (ring.Count < 3) { errors.Add($"Room {id}: the polygon needs at least 3 distinct points."); continue; }
            if (ring.Any(pt => double.IsNaN(pt.X) || double.IsNaN(pt.Y) || double.IsInfinity(pt.X) || double.IsInfinity(pt.Y))) { errors.Add($"Room {id}: polygon has invalid numbers."); continue; }
            var area = CadGeometry.Area(ring);
            if (area < 1e-9) { errors.Add($"Room {id}: the polygon has no area."); continue; }
            var net = area - r.Holes.Select(CadExchangeJson.Ring).Where(h => h.Count >= 3).Sum(CadGeometry.Area);
            if (r.Area is double reported && reported > 0)
            {
                var m2 = net * areaFactor;
                if (Math.Abs(m2 - reported) / reported > o.AreaTolerance)
                    warnings.Add($"Room {id}: polygon area {m2:0.##} m2 differs from the reported {reported:0.##} m2 (check units = {f.Source.Units} or the boundary).");
            }
        }
        var n = 0;
        foreach (var c in f.Counts)
        {
            n++;
            if (string.IsNullOrWhiteSpace(c.Room)) errors.Add($"Count #{n} has no room.");
            if (string.IsNullOrWhiteSpace(c.System)) errors.Add($"Count #{n} ({c.Room}) has no system.");
            if (string.IsNullOrWhiteSpace(c.Item)) errors.Add($"Count #{n} ({c.Room}) has no item.");
            if (double.IsNaN(c.Qty) || double.IsInfinity(c.Qty) || c.Qty < 0) errors.Add($"Count #{n} ({c.Room} {c.Item}) has an invalid qty {c.Qty}.");
            if (ids.Count > 0 && !ids.Contains(c.Room?.Trim() ?? "")) warnings.Add($"Count #{n} refers to room {c.Room} which is not in the rooms list.");
        }
        if (errors.Count > 20) { var more = errors.Count - 20; errors.RemoveRange(20, more); errors.Add($"... and {more} more errors."); }
    }

    /// <summary>Writes the preview: new rooms, replaced room shapes (same room + plan), PROJECT QTY changes. One audited batch.</summary>
    public static string Commit(CadImportPreview p, IProjectStore store)
    {
        if (!p.CanCommit) throw new InvalidOperationException("The CAD file has errors - fix them first: " + string.Join("; ", p.Errors.Take(3)));
        var building = p.NewRooms.FirstOrDefault()?.Building ?? p.Shapes.FirstOrDefault()?.Building ?? (p.Options.Building.Length > 0 ? p.Options.Building : p.File.Building).ToUpperInvariant();
        var source = $"CAD:{p.File.Source.Drawing}".Trim();
        if (source.Length > 120) source = source[..120];
        var shapes = store.All<RoomShape>();
        var qtys = store.All<RoomQty>().Where(q => q.Building.Equals(building, StringComparison.OrdinalIgnoreCase)).ToList();
        int added = 0, updated = 0;
        store.Batch(w =>
        {
            foreach (var r in p.NewRooms) w.Insert(r);
            foreach (var sh in p.Shapes)
            {
                foreach (var old in shapes.Where(x => x.Room.Equals(sh.Room, StringComparison.OrdinalIgnoreCase) && x.Plan.Equals(sh.Plan, StringComparison.OrdinalIgnoreCase)
                                                      && x.Building.Equals(sh.Building, StringComparison.OrdinalIgnoreCase)))
                    w.Delete(old);
                w.Insert(sh);
            }
            foreach (var q in p.QtyChanges)
            {
                if (q.Action == "ADD")
                {
                    w.Insert(new RoomQty { Building = building, Room = q.Room, Stage = q.Stage, Item = q.Item, Qty = q.NewQty, Unit = "no", Source = source });
                    added++;
                }
                else if (q.Action == "UPDATE")
                {
                    var rows = qtys.Where(x => LedgerKeys.Key(x.Room, x.Stage, x.Item) == LedgerKeys.Key(q.Room, q.Stage, q.Item)).ToList();
                    var first = rows[0];
                    first.Qty = q.NewQty;
                    first.Source = source;
                    w.Update(first);
                    foreach (var extra in rows.Skip(1)) w.Delete(extra);
                    updated++;
                }
            }
        }, $"CAD import {p.FileName}: {p.NewRooms.Count} rooms, {p.Shapes.Count} shapes, PROJECT QTY {added} added / {updated} changed");
        return $"{p.NewRooms.Count} rooms added, {p.Shapes.Count} room shapes, PROJECT QTY {added} added / {updated} changed";
    }

    /// <summary>The JSON schema (draft 2020-12) of the exchange file - also in docs/cad-exchange.schema.json.</summary>
    public static string JsonSchema()
    {
        using var s = typeof(CadExchangeImporter).Assembly.GetManifestResourceStream("Raffaello.Core.Integrations.CadExchange.cad-exchange.schema.json");
        if (s is null) return "";
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
