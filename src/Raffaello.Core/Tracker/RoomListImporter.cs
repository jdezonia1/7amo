using System.Globalization;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Tracker;

public sealed class RoomListImportResult
{
    public string FileName { get; init; } = "";
    public string Building { get; init; } = "";
    public string SheetName { get; set; } = "";
    public int HeaderRow { get; set; }
    public Dictionary<string, string> ColumnsFound { get; } = new();
    public List<Room> Rooms { get; } = new();
    public List<RoomQty> Quantities { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public string Summary => $"{Building} room list '{SheetName}' (headers row {HeaderRow}): {Rooms.Count} rooms, {Quantities.Count} PROJECT QTY cells; " +
                             $"area types {string.Join(", ", Rooms.GroupBy(r => r.AreaType).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))}";
}

/// <summary>
/// [phase6] Room list for a building (e.g. the HOTEL) found by header text, wherever the columns are: ROOM / ROOM NO / KEY /
/// LOCATION, LEVEL / FLOOR, TYPE / ROOM TYPE, ZONE, AREA TYPE, PLAN, PLOT. Columns titled "STAGE|ITEM" (as in the tracker's
/// PROJECT QTY sheet, e.g. "1ST FIX|POWER") carry PROJECT QTY. Rooms get an area type guess (editable on the Ledger page).
/// </summary>
public static class RoomListImporter
{
    public static readonly string[] RoomHeaders = { "ROOM", "ROOM NO", "ROOM NO.", "ROOM NUMBER", "ROOM CODE", "KEY", "KEY NO", "LOCATION", "SPACE", "SPACE NO", "UNIT NO", "UNIT" };
    public static readonly string[] LevelHeaders = { "LEVEL", "FLOOR", "FLOOR LEVEL", "STOREY" };
    public static readonly string[] TypeHeaders = { "ROOM TYPE", "TYPE", "UNIT TYPE", "KEY TYPE", "SPACE TYPE", "DESCRIPTION", "ROOM NAME" };
    public static readonly string[] ZoneHeaders = { "ZONE", "WING", "BLOCK" };
    public static readonly string[] AreaHeaders = { "AREA TYPE", "AREA" };
    public static readonly string[] PlanHeaders = { "PLAN", "DRAWING", "PLAN CODE" };
    public static readonly string[] PlotHeaders = { "PLOT", "BUILDING NO" };

    public static RoomListImportResult Read(string path, string building, string? sheetName = null)
    {
        using var x = new XlsxStreamReader(path);
        var res = new RoomListImportResult { FileName = path, Building = building };
        var sheets = sheetName != null ? new[] { sheetName }
            : x.Sheets.Keys.OrderBy(k => k.Contains("ROOM", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        foreach (var sheet in sheets)
        {
            Dictionary<string, int>? cols = null;
            var qtyCols = new Dictionary<int, (string Stage, string Item)>();
            foreach (var r in x.ReadRows(sheet))
            {
                if (cols is null)
                {
                    if (r.Number > 20) break;
                    var map = r.Values.Where(v => v.Value.Trim().Length > 0).ToDictionary(v => TableReader.NormalizeHeader(v.Value), v => v.Key);
                    if (!RoomHeaders.Any(h => map.ContainsKey(TableReader.NormalizeHeader(h)))) continue;
                    cols = map;
                    res.SheetName = sheet; res.HeaderRow = r.Number;
                    foreach (var (c, k) in r.Values)
                    {
                        var parts = k.Split('|');
                        if (parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0) qtyCols[c] = (parts[0].Trim().ToUpperInvariant(), parts[1].Trim().ToUpperInvariant());
                    }
                    void Note(string name, string[] aliases) { var f = aliases.FirstOrDefault(a => map.ContainsKey(TableReader.NormalizeHeader(a))); if (f != null) res.ColumnsFound[name] = f; }
                    Note("ROOM", RoomHeaders); Note("LEVEL", LevelHeaders); Note("TYPE", TypeHeaders); Note("ZONE", ZoneHeaders); Note("AREA TYPE", AreaHeaders); Note("PLAN", PlanHeaders); Note("PLOT", PlotHeaders);
                    continue;
                }
                string Get(string[] aliases) { foreach (var a in aliases) if (cols.TryGetValue(TableReader.NormalizeHeader(a), out var c)) { var v = r.Get(c).Trim(); if (v.Length > 0) return v; } return ""; }
                var code = Get(RoomHeaders);
                if (code.Length == 0) continue;
                if (res.Rooms.Any(o => o.Code.Equals(code, StringComparison.OrdinalIgnoreCase))) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"Row {r.Number}: room {code} listed twice - first kept.")); continue; }
                var level = Get(LevelHeaders);
                var type = Get(TypeHeaders);
                var area = Get(AreaHeaders).ToUpperInvariant();
                var room = new Room
                {
                    Building = building, Code = code.ToUpperInvariant(), Level = level, RoomType = type, Zone = Get(ZoneHeaders), Plan = Get(PlanHeaders),
                    Plot = int.TryParse(Get(PlotHeaders), NumberStyles.Integer, CultureInfo.InvariantCulture, out var plot) ? plot : 0,
                    Floor = LevelNumber(level),
                    AreaType = AreaTypes.All.Contains(area) ? area : AreaTypes.GuessFor(building, type, level, code),
                };
                res.Rooms.Add(room);
                foreach (var (c, (stage, item)) in qtyCols)
                    if (r.Number_(c) is { } q && q != 0) res.Quantities.Add(new RoomQty { Building = building, Room = room.Code, Stage = stage, Item = item, Qty = q, Source = "ROOM LIST" });
            }
            if (cols != null) break;
        }
        if (res.Rooms.Count == 0) res.Issues.Add(new(0, IssueLevel.Error, $"No room column found (looked for: {string.Join(", ", RoomHeaders)}) in the first 20 rows."));
        return res;
    }

    /// <summary>"Level 03" / "L3" / "GF" / "B1" / "Roof" -> 3 / 3 / 0 / -1 / 99.</summary>
    public static int LevelNumber(string level)
    {
        var l = (level ?? "").Trim().ToUpperInvariant();
        if (l is "GF" or "G" || l.StartsWith("GROUND")) return 0;
        if (l.StartsWith("ROOF") || l == "RF") return 99;
        var digits = new string(l.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var n)) return 0;
        return l.StartsWith("B") ? -n : n;
    }

    /// <summary>Adds new rooms, updates level / type / zone / plan of known ones (area types already set are kept), replaces their PROJECT QTY.</summary>
    public static (int Added, int Updated, int Qty) Commit(RoomListImportResult r, IProjectStore store)
    {
        var existing = store.All<Room>().Where(x => x.Building == r.Building).GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var codes = r.Rooms.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldQty = r.Quantities.Count == 0 ? new List<RoomQty>() : store.All<RoomQty>().Where(q => q.Building == r.Building && codes.Contains(q.Room)).ToList();
        int added = 0, updated = 0;
        store.Batch(w =>
        {
            foreach (var room in r.Rooms)
            {
                if (existing.TryGetValue(room.Code, out var old))
                {
                    old.Level = room.Level; old.RoomType = room.RoomType; old.Zone = room.Zone; old.Floor = room.Floor;
                    if (room.Plan.Length > 0) old.Plan = room.Plan;
                    if (room.Plot != 0) old.Plot = room.Plot;
                    if (string.IsNullOrEmpty(old.AreaType)) old.AreaType = room.AreaType;
                    w.Update(old); updated++;
                }
                else { w.Insert(room); added++; }
            }
            foreach (var q in oldQty) w.Delete(q);
            w.InsertMany(r.Quantities);
        }, $"{r.Building} room list: {added} rooms added, {updated} updated, {r.Quantities.Count} PROJECT QTY");
        return (added, updated, r.Quantities.Count);
    }
}
