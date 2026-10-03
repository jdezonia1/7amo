using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Recon;

public sealed class HotelReconResult
{
    public string ProjectQtyFile { get; init; } = "";
    public string ClaimsFile { get; init; } = "";
    public List<Room> Rooms { get; } = new();
    public List<RoomQty> Quantities { get; } = new();
    public List<ClaimLine> Claims { get; } = new();
    public List<ImportIssue> Issues { get; } = new();

    public double TotalQty => Quantities.Sum(q => q.Qty);
    public double ClaimedQty => Claims.Where(c => !c.Rework).Sum(c => c.Qty);
    public double ReworkQty => Claims.Where(c => c.Rework).Sum(c => c.Qty);

    public string Summary =>
        $"{Rooms.Count} locations, {Quantities.Count} PROJECT QTY cells ({Quantities.Select(q => q.Stage + "|" + q.Item).Distinct().Count()} stage|item keys), " +
        $"total {TotalQty:N0}; {Claims.Count} claim lines ({Claims.Select(c => c.Subcontractor).Distinct().Count()} subcontractors), " +
        $"claimed {ClaimedQty:N2}, rework {ReworkQty:N2}";
}

/// <summary>
/// HOTEL RECON: loads the new hotel 100% total (HOTEL_PROJECT_QTY.xlsx, sheet PROJECT QTY) and the cleaned past claims
/// (HOTEL_REMAINING.xlsx, sheet CLEAN CLAIMS), so <see cref="Ledger.LedgerRules.Balances"/> gives REMAINING per room x stage x item.
/// PROJECT QTY: the row whose column B starts with "KEY" holds "STAGE|ITEM" keys, the row whose column B is "UNIT" the units,
/// the row with PART / LOCATION headers is the header row; one data row per location (rows without PART or LOCATION are notes).
/// CLEAN CLAIMS: headers are read by name (SUBCONTRACTOR, INV, STAGE, FLOOR, LOCATION, ITEM, QTY, DRAWING %, WIR %, REWORK, FLAGS,
/// OLD STAGE / OLD LOCATION / OLD ITEM, SOURCE ROW); the mapped STAGE / LOCATION / ITEM are used.
/// </summary>
public static class HotelReconImporter
{
    public const string ClaimSource = "RECON";
    public const string QtySource = "QS SURVEY";
    public const string ProjectSheet = "PROJECT QTY";
    public const string ClaimsSheet = "CLEAN CLAIMS";

    private static readonly Regex KeyPattern = new(@"^[^|()]+\|[^|()]+$", RegexOptions.Compiled);

    public static HotelReconResult Read(string projectQtyPath, string cleanClaimsPath)
    {
        var res = new HotelReconResult { ProjectQtyFile = projectQtyPath, ClaimsFile = cleanClaimsPath };
        ReadProjectQty(projectQtyPath, res);
        ReadClaims(cleanClaimsPath, res);
        return res;
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, int> Headers(XlsxRow row) =>
        row.Values.Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .GroupBy(v => TableReader.NormalizeHeader(v.Value)).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    private static string H(XlsxRow r, Dictionary<string, int> h, params string[] names)
    {
        foreach (var n in names)
            if (h.TryGetValue(TableReader.NormalizeHeader(n), out var c)) { var v = r.Get(c).Trim(); if (v.Length > 0) return v; }
        return "";
    }

    private static double? Num(string s) =>
        double.TryParse(s.Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int Int(string s) => Num(s) is double d ? (int)Math.Round(d) : 0;

    /// <summary>Floor code to number: B2 = -2, B1 = -1, GF = 0, L1 = 1, L02 = 2, RF (roof) = 99; anything else 0.</summary>
    public static int ParseFloor(string floor)
    {
        var f = (floor ?? "").Trim().ToUpperInvariant();
        if (f is "GF" or "G" or "L0" or "L00" || f.StartsWith("GROUND")) return 0;
        if (f is "RF" or "R" || f.StartsWith("ROOF")) return 99;
        var m = Regex.Match(f, @"^(B|BASEMENT|LB)\s*0*(\d+)$");
        if (m.Success) return -int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(f, @"^(L|LEVEL)\s*0*(\d+)$");
        if (m.Success) return int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return 0;
    }

    // ------------------------------------------------------------------ PROJECT QTY

    private static void ReadProjectQty(string path, HotelReconResult res)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.FirstOrDefault(k => string.Equals(k.Trim(), ProjectSheet, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) { res.Issues.Add(new(0, IssueLevel.Error, $"Sheet {ProjectSheet} not found in {Path.GetFileName(path)}.")); return; }

        Dictionary<int, string> keys = new();
        Dictionary<int, string> units = new();
        Dictionary<string, int>? h = null;
        var rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
        var rowsPerLoc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int notes = 0;
        foreach (var r in x.ReadRows(sheet))
        {
            if (h is null)
            {
                var b = r.Get(2).Trim();
                if (keys.Count == 0 && b.StartsWith("KEY", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (c, v) in r.Values)
                        if (c != 2 && KeyPattern.IsMatch(v.Trim())) keys[c] = v.Trim();
                    continue;
                }
                if (keys.Count > 0 && units.Count == 0 && b.Equals("UNIT", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (c, v) in r.Values) if (keys.ContainsKey(c) && v.Trim().Length > 0) units[c] = v.Trim();
                    continue;
                }
                var hh = Headers(r);
                if (hh.ContainsKey("PART") && hh.ContainsKey("LOCATION")) h = hh;
                continue;
            }
            var part = H(r, h, "PART");
            var loc = H(r, h, "LOCATION");
            if (part.Length == 0 || loc.Length == 0) { if (!r.IsEmpty) notes++; continue; }
            rowsPerLoc[loc] = rowsPerLoc.GetValueOrDefault(loc) + 1;
            if (!rooms.ContainsKey(loc))
            {
                var floorCode = H(r, h, "FLOOR");
                var level = H(r, h, "LEVEL");
                var type = H(r, h, "UNIT TYPE");
                var room = new Room
                {
                    Building = Buildings.Hotel, Code = loc, Floor = ParseFloor(floorCode), Level = level.Length > 0 ? level : floorCode,
                    Zone = part, RoomType = type, Unit = H(r, h, "UNIT / AREA No.", "UNIT / AREA NO", "UNIT NO"),
                };
                room.AreaType = AreaTypes.GuessFor(Buildings.Hotel, type, room.Level, loc);
                rooms[loc] = room;
                res.Rooms.Add(room);
            }
            foreach (var (c, key) in keys)
            {
                var text = r.Get(c).Trim();
                if (text.Length == 0) continue;
                var qty = Num(text);
                if (qty is null) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"PROJECT QTY row {r.Number}: '{text}' under {key} is not a number - skipped.")); continue; }
                if (Math.Abs(qty.Value) < 1e-9) continue;
                var parts = key.Split('|', 2);
                res.Quantities.Add(new RoomQty
                {
                    Building = Buildings.Hotel, Room = loc, Stage = parts[0].Trim().ToUpperInvariant(), Item = parts[1].Trim().ToUpperInvariant(),
                    Unit = units.GetValueOrDefault(c, "no"), Qty = qty.Value, Source = QtySource,
                });
            }
        }
        if (keys.Count == 0) res.Issues.Add(new(2, IssueLevel.Error, "PROJECT QTY: no row with column B 'KEY ...' and STAGE|ITEM keys."));
        else if (h is null) res.Issues.Add(new(6, IssueLevel.Error, "PROJECT QTY: header row with PART and LOCATION not found."));
        foreach (var (loc, n) in rowsPerLoc.Where(kv => kv.Value > 1))
            res.Issues.Add(new(0, IssueLevel.Warning, $"PROJECT QTY: location {loc} is on {n} rows - quantities added together."));
        if (notes > 0) res.Issues.Add(new(0, IssueLevel.Warning, $"PROJECT QTY: {notes} note rows without PART or LOCATION skipped."));
    }

    // ------------------------------------------------------------------ CLEAN CLAIMS

    private static void ReadClaims(string path, HotelReconResult res)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.FirstOrDefault(k => string.Equals(k.Trim(), ClaimsSheet, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) { res.Issues.Add(new(0, IssueLevel.Error, $"Sheet {ClaimsSheet} not found in {Path.GetFileName(path)}.")); return; }
        var rooms = res.Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? h = null;
        foreach (var r in x.ReadRows(sheet))
        {
            if (h is null)
            {
                var hh = Headers(r);
                if (new[] { "SUBCONTRACTOR", "STAGE", "LOCATION", "ITEM", "QTY" }.All(hh.ContainsKey)) h = hh;
                continue;
            }
            var sub = H(r, h, "SUBCONTRACTOR");
            if (sub.Length == 0) continue;
            var qty = Num(H(r, h, "QTY"));
            if (qty is null) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"CLEAN CLAIMS row {r.Number}: no QTY - skipped.")); continue; }
            var stage = H(r, h, "STAGE").ToUpperInvariant();
            var loc = H(r, h, "LOCATION");
            var item = H(r, h, "ITEM").ToUpperInvariant();
            if (stage.Length == 0 || loc.Length == 0 || item.Length == 0)
                res.Issues.Add(new(r.Number, IssueLevel.Warning, $"CLEAN CLAIMS row {r.Number}: STAGE / LOCATION / ITEM empty ({sub} {stage} {loc} {item})."));
            if (loc.Length > 0 && !rooms.ContainsKey(loc)) unknown[loc] = unknown.GetValueOrDefault(loc) + 1;

            var notes = new List<string>();
            var flags = H(r, h, "FLAGS");
            if (flags.Length > 0) notes.Add(flags);
            var old = new List<string>();
            void Old(string col, string now) { var o = H(r, h, col); if (o.Length > 0 && !o.Equals(now, StringComparison.OrdinalIgnoreCase)) old.Add(o); }
            Old("OLD STAGE", stage); Old("OLD LOCATION", loc); Old("OLD ITEM", item);
            if (old.Count > 0) notes.Add("old: " + string.Join(" / ", old));
            var srcRow = H(r, h, "SOURCE ROW");
            if (srcRow.Length > 0) notes.Add("src row " + srcRow);

            res.Claims.Add(new ClaimLine
            {
                Building = Buildings.Hotel, Subcontractor = sub.Trim().ToUpperInvariant(), InvoiceNo = Int(H(r, h, "INV", "INVOICE")),
                Stage = stage, Floor = H(r, h, "FLOOR"), Room = loc, Item = item, Unit = "no", Qty = qty.Value,
                SitePct = Num(H(r, h, "DRAWING %", "SITE %")) ?? 1, WirPct = Num(H(r, h, "WIR %")) ?? 1,
                Rework = H(r, h, "REWORK").Trim().Equals("YES", StringComparison.OrdinalIgnoreCase),
                Notes = string.Join(" | ", notes), AreaType = rooms.TryGetValue(loc, out var room) ? room.AreaType : "",
                Source = ClaimSource, SourceKey = $"RECON|row{r.Number}", EnteredAt = DateTime.Now,
            });
        }
        if (h is null) res.Issues.Add(new(3, IssueLevel.Error, "CLEAN CLAIMS: header row (SUBCONTRACTOR, STAGE, LOCATION, ITEM, QTY) not found."));
        foreach (var (loc, n) in unknown)
            res.Issues.Add(new(0, IssueLevel.Warning, $"CLEAN CLAIMS: location {loc} ({n} lines) is not in PROJECT QTY."));
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Writes the recon in one batch: upserts HOTEL rooms by code (other rooms kept, AreaType edits kept), REPLACES every HOTEL
    /// PROJECT QTY cell, deletes the HOTEL claim lines with Source RECON and inserts the new ones. Branded data and HOTEL claim
    /// lines from other sources (TRACKER, MANUAL ...) are kept unless <paramref name="replaceAllHotelClaims"/> is true, in which
    /// case every HOTEL claim line is deleted first (the recon becomes the only hotel ledger).
    /// </summary>
    public static (int rooms, int qty, int claims, int deletedClaims) Commit(HotelReconResult res, IProjectStore store, bool replaceAllHotelClaims = false)
    {
        var existingRooms = store.All<Room>().Where(r => r.Building == Buildings.Hotel)
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var oldQty = store.All<RoomQty>().Where(q => q.Building == Buildings.Hotel).ToList();
        var oldClaims = store.All<ClaimLine>().Where(c => c.Building == Buildings.Hotel && (replaceAllHotelClaims || c.Source == ClaimSource)).ToList();
        int rooms = 0;
        store.Batch(w =>
        {
            foreach (var r in res.Rooms)
            {
                if (existingRooms.TryGetValue(r.Code, out var old))
                {
                    old.Floor = r.Floor; old.Level = r.Level; old.Zone = r.Zone; old.RoomType = r.RoomType;
                    if (r.Unit.Length > 0) old.Unit = r.Unit;
                    if (string.IsNullOrEmpty(old.AreaType)) old.AreaType = r.AreaType;
                    w.Update(old);
                }
                else w.Insert(r);
                rooms++;
            }
            foreach (var q in oldQty) w.Delete(q);
            w.InsertMany(res.Quantities);
            foreach (var c in oldClaims) w.Delete(c);
            w.InsertMany(res.Claims);
        }, $"Hotel recon {Path.GetFileName(res.ProjectQtyFile)} + {Path.GetFileName(res.ClaimsFile)}: {rooms} locations, {res.Quantities.Count} PROJECT QTY, " +
           $"{res.Claims.Count} RECON claim lines ({oldClaims.Count} old hotel lines removed)");
        return (rooms, res.Quantities.Count, res.Claims.Count, oldClaims.Count);
    }
}