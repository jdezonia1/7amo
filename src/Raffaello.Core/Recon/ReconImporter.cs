using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Recon;

/// <summary>What one building's recon workbook(s) hold: rooms, the 100% total (PROJECT QTY) and the cleaned past claims.</summary>
public sealed class ReconImportResult
{
    public string Building { get; init; } = Buildings.Hotel;
    public string ProjectQtyFile { get; init; } = "";
    public string ClaimsFile { get; init; } = "";
    public List<Room> Rooms { get; } = new();
    public List<RoomQty> Quantities { get; } = new();
    public List<ClaimLine> Claims { get; } = new();
    public List<ImportIssue> Issues { get; } = new();

    public double TotalQty => Quantities.Sum(q => q.Qty);
    /// <summary>Claimed against the totals: no rework, no NOT COMPARED lines (cable pulling / cable tray).</summary>
    public double ClaimedQty => Claims.Where(c => !c.Rework && !LedgerRules.IsNotCompared(c)).Sum(c => c.Qty);
    /// <summary>2nd-fix extra points kept for the 15 m route-length check (not against the project quantity).</summary>
    public double LengthExtraQty => Claims.Where(c => c.LengthApplies).Sum(c => c.LengthClaimedQty - c.Qty);
    public double ReworkQty => Claims.Where(c => c.Rework).Sum(c => c.Qty);
    public List<ClaimLine> NotCompared => Claims.Where(LedgerRules.IsNotCompared).ToList();

    public string Summary
    {
        get
        {
            var nc = NotCompared;
            var ncText = nc.Count == 0 ? "" : "; NOT COMPARED " + string.Join(", ", nc.GroupBy(c => c.Stage).OrderBy(g => g.Key)
                .Select(g => $"{g.Key} {g.Count()} lines {g.Sum(c => c.Qty):N2} {g.First().Unit}"));
            return $"{Building}: {Rooms.Count} locations, {Quantities.Count} PROJECT QTY cells ({Quantities.Select(q => q.Stage + "|" + q.Item).Distinct().Count()} stage|item keys), " +
                   $"total {TotalQty:N0}; {Claims.Count} claim lines ({Claims.Select(c => c.Subcontractor).Distinct().Count()} subcontractors), " +
                   $"claimed {ClaimedQty:N2}, rework {ReworkQty:N2}, 15 m rule extras {LengthExtraQty:N2} (length check pending){ncText}";
        }
    }
}

/// <summary>
/// RECON import for one building (HOTEL or BRANDED): the building's 100% total (sheet PROJECT QTY) and the cleaned past claims
/// (sheet CLEAN CLAIMS, plus the optional sheet NO CAP (CABLES)), so <see cref="LedgerRules.Balances"/> gives REMAINING per room x stage x item.
/// PROJECT QTY: the row whose column B starts with "KEY" holds "STAGE|ITEM" keys, the row whose column B is "UNIT" the units,
/// the row with PART / LOCATION headers is the header row; one data row per location (rows without PART or LOCATION are notes).
/// CLEAN CLAIMS: headers are read by name (SUBCONTRACTOR, INV, STAGE, FLOOR, LOCATION, ITEM, QTY, DRAWING %, WIR %, REWORK, FLAGS,
/// OLD STAGE / OLD LOCATION / OLD ITEM, SOURCE ROW, LENGTH EXTRA); the mapped STAGE / LOCATION / ITEM are used.
/// NO CAP (CABLES): SUBCONTRACTOR, INV, FLOOR, LOCATION, CABLE, QTY (m) - cable pulling (site statement) and cable tray (waits for the
/// final Revit model). They are imported as NOT COMPARED lines: kept in the ledger, never counted against a total.
/// </summary>
public static class ReconImporter
{
    public const string ClaimSource = "RECON";
    public const string QtySource = "QS SURVEY";
    public const string ProjectSheet = "PROJECT QTY";
    public const string ClaimsSheet = "CLEAN CLAIMS";
    public const string NoCapSheetPrefix = "NO CAP";
    public const string CablePulling = "CABLE PULLING";
    public const string CableTray = "CABLE TRAY";

    /// <summary>Stages that are never compared with a total (cable pulling = site statement; cable tray = final Revit model).</summary>
    public static readonly string[] NotComparedStages = { CablePulling, CableTray };

    private static readonly Regex KeyPattern = new(@"^[^|()]+\|[^|()]+$", RegexOptions.Compiled);
    private static readonly Regex TraySize = new(@"^\d+\s*MM$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>HOTEL / BRANDED (any case); anything else is an error.</summary>
    public static string NormalizeBuilding(string building)
    {
        var b = (building ?? "").Trim().ToUpperInvariant();
        return b switch
        {
            Buildings.Hotel => Buildings.Hotel,
            Buildings.Branded or "BRANDED RESIDENCES" or "RESIDENCES" => Buildings.Branded,
            _ => throw new ArgumentException($"Building must be HOTEL or BRANDED, not '{building}'."),
        };
    }

    /// <summary>True when the workbook has a sheet with this name (used to read PROJECT QTY from the same REMAINING workbook).</summary>
    public static bool HasSheet(string path, string sheet)
    {
        using var x = new XlsxStreamReader(path);
        return x.Sheets.Keys.Any(k => string.Equals(k.Trim(), sheet, StringComparison.OrdinalIgnoreCase));
    }

    public static ReconImportResult Read(string building, string projectQtyPath, string cleanClaimsPath)
    {
        var res = new ReconImportResult { Building = NormalizeBuilding(building), ProjectQtyFile = projectQtyPath, ClaimsFile = cleanClaimsPath };
        var units = ReadProjectQty(projectQtyPath, res);
        ReadClaims(cleanClaimsPath, res, units);
        ReadNoCap(cleanClaimsPath, res);
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

    /// <summary>
    /// Floor code to number: B2 / BS2 = -2, B1 / BS1 = -1, GF / 0 = 0, L1 / 1 = 1, L02 = 2, RF (roof) = 99; anything else 0.
    /// </summary>
    public static int ParseFloor(string floor)
    {
        var f = (floor ?? "").Trim().ToUpperInvariant();
        if (f is "GF" or "G" or "L0" or "L00" || f.StartsWith("GROUND")) return 0;
        if (f is "RF" or "R" || f.StartsWith("ROOF")) return 99;
        var m = Regex.Match(f, @"^-?\d+$");
        if (m.Success) return int.Parse(f, CultureInfo.InvariantCulture);
        m = Regex.Match(f, @"^(B|BS|BASEMENT|LB)\s*0*(\d+)$");
        if (m.Success) return -int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        m = Regex.Match(f, @"^(L|LEVEL|LVL)\s*0*(\d+)$");
        if (m.Success) return int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return 0;
    }

    /// <summary>Plot number from the PART column: "P2" = 2, "H3" = 3; no digits = 0.</summary>
    public static int ParsePlot(string part)
    {
        var m = Regex.Match(part ?? "", @"\d+");
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    /// <summary>CABLE TRAY when the cable text is a tray width ("100 MM"), CABLE PULLING otherwise ("4X16").</summary>
    public static string CableStage(string cable) => TraySize.IsMatch((cable ?? "").Trim()) ? CableTray : CablePulling;

    public static bool IsNotComparedStage(string stage) =>
        NotComparedStages.Contains((stage ?? "").Trim().ToUpperInvariant());

    // ------------------------------------------------------------------ PROJECT QTY

    /// <summary>Reads the PROJECT QTY sheet; returns the unit per STAGE|ITEM key.</summary>
    private static Dictionary<string, string> ReadProjectQty(string path, ReconImportResult res)
    {
        var keyUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.FirstOrDefault(k => string.Equals(k.Trim(), ProjectSheet, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) { res.Issues.Add(new(0, IssueLevel.Error, $"Sheet {ProjectSheet} not found in {Path.GetFileName(path)}.")); return keyUnits; }

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
                var plot = ParsePlot(part);
                var room = new Room
                {
                    Building = res.Building, Code = loc, Plot = plot, Floor = ParseFloor(floorCode), Level = level.Length > 0 ? level : floorCode,
                    // HOTEL zone = PART (H1 ...); BRANDED zone = "PLOT n" like the tracker import
                    Zone = res.Building == Buildings.Branded && plot > 0 ? "PLOT " + plot : part,
                    RoomType = type, Unit = H(r, h, "UNIT / AREA No.", "UNIT / AREA NO", "UNIT NO"),
                };
                room.AreaType = AreaTypes.GuessFor(res.Building, type, room.Level, loc);
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
                    Building = res.Building, Room = loc, Stage = parts[0].Trim().ToUpperInvariant(), Item = parts[1].Trim().ToUpperInvariant(),
                    Unit = units.GetValueOrDefault(c, "no"), Qty = qty.Value, Source = QtySource,
                });
            }
        }
        foreach (var (c, key) in keys) keyUnits[NormKey(key)] = units.GetValueOrDefault(c, "no");
        if (keys.Count == 0) res.Issues.Add(new(2, IssueLevel.Error, "PROJECT QTY: no row with column B 'KEY ...' and STAGE|ITEM keys."));
        else if (h is null) res.Issues.Add(new(6, IssueLevel.Error, "PROJECT QTY: header row with PART and LOCATION not found."));
        foreach (var (loc, n) in rowsPerLoc.Where(kv => kv.Value > 1))
            res.Issues.Add(new(0, IssueLevel.Warning, $"PROJECT QTY: location {loc} is on {n} rows - quantities added together."));
        if (notes > 0) res.Issues.Add(new(0, IssueLevel.Warning, $"PROJECT QTY: {notes} note rows without PART or LOCATION skipped."));
        return keyUnits;
    }

    private static string NormKey(string key)
    {
        var p = key.Split('|', 2);
        return p.Length == 2 ? p[0].Trim().ToUpperInvariant() + "|" + p[1].Trim().ToUpperInvariant() : key.Trim().ToUpperInvariant();
    }

    // ------------------------------------------------------------------ CLEAN CLAIMS

    private static void ReadClaims(string path, ReconImportResult res, Dictionary<string, string> keyUnits)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.FirstOrDefault(k => string.Equals(k.Trim(), ClaimsSheet, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) { res.Issues.Add(new(0, IssueLevel.Error, $"Sheet {ClaimsSheet} not found in {Path.GetFileName(path)}.")); return; }
        var rooms = res.Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        var unknown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? h = null;
        var label = res.Building == Buildings.Hotel ? "hotel" : "branded";
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
            var notCompared = IsNotComparedStage(stage);
            if (!notCompared && loc.Length > 0 && !rooms.ContainsKey(loc)) unknown[loc] = unknown.GetValueOrDefault(loc) + 1;

            var notes = new List<string>();
            if (notCompared) notes.Add(NotComparedNote(stage));
            var flags = H(r, h, "FLAGS");
            if (flags.Length > 0) notes.Add(flags);
            var old = new List<string>();
            void Old(string col, string now) { var o = H(r, h, col); if (o.Length > 0 && !o.Equals(now, StringComparison.OrdinalIgnoreCase)) old.Add(o); }
            Old("OLD STAGE", stage); Old("OLD LOCATION", loc); Old("OLD ITEM", item);
            if (old.Count > 0) notes.Add("old: " + string.Join(" / ", old));
            var srcRow = H(r, h, "SOURCE ROW");
            if (srcRow.Length > 0) notes.Add("src row " + srcRow);

            // 15 m rule (contract): 2nd-fix quantity claimed above the total is kept as length extras -> the app's LENGTH check
            // (QTY = plan points against the room total; LengthClaimedQty = plan + extras; PENDING until route lengths are checked)
            var lenExtra = notCompared ? 0 : Num(H(r, h, "LENGTH EXTRA (15 m rule)", "LENGTH EXTRA")) ?? 0;
            if (lenExtra > 0) notes.Add($"15 m rule: +{lenExtra:0.##} extra points above the project quantity - check route lengths");

            var unit = H(r, h, "UNIT") is { Length: > 0 } u ? u
                : keyUnits.TryGetValue(stage + "|" + item, out var ku) ? ku
                : notCompared ? "m" : "no";
            res.Claims.Add(new ClaimLine
            {
                LengthApplies = lenExtra > 0, LengthClaimedQty = lenExtra > 0 ? qty.Value + lenExtra : 0,
                LengthStatus = lenExtra > 0 ? CheckStatus.Pending : CheckStatus.None,
                LengthNote = lenExtra > 0 ? $"Imported ({label} recon): 2nd-fix claim above the project quantity - treated as 15 m rule extras. Check route lengths." : "",
                Building = res.Building, Subcontractor = sub.Trim().ToUpperInvariant(), InvoiceNo = Int(H(r, h, "INV", "INVOICE")),
                Stage = stage, Floor = H(r, h, "FLOOR"), Room = loc, Item = item, Unit = unit, Qty = qty.Value,
                SitePct = Num(H(r, h, "DRAWING %", "SITE %")) ?? 1, WirPct = Num(H(r, h, "WIR %")) ?? 1,
                Rework = H(r, h, "REWORK").Trim().Equals("YES", StringComparison.OrdinalIgnoreCase),
                WorkType = notCompared ? LedgerRules.NotComparedWorkType : "",
                Notes = string.Join(" | ", notes), AreaType = rooms.TryGetValue(loc, out var room) ? room.AreaType : "",
                Source = ClaimSource, SourceKey = $"RECON|{res.Building}|row{r.Number}", EnteredAt = DateTime.Now,
            });
        }
        if (h is null) res.Issues.Add(new(3, IssueLevel.Error, "CLEAN CLAIMS: header row (SUBCONTRACTOR, STAGE, LOCATION, ITEM, QTY) not found."));
        foreach (var (loc, n) in unknown)
            res.Issues.Add(new(0, IssueLevel.Warning, $"CLEAN CLAIMS: location {loc} ({n} lines) is not in PROJECT QTY."));
    }

    private static string NotComparedNote(string stage) => stage.Trim().ToUpperInvariant() == CableTray
        ? "NOT COMPARED - cable tray: not compared until the final Revit model"
        : "NOT COMPARED - cable pulling: paid by site statement";

    // ------------------------------------------------------------------ NO CAP (CABLES)

    private static void ReadNoCap(string path, ReconImportResult res)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.FirstOrDefault(k => k.Trim().StartsWith(NoCapSheetPrefix, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return;   // optional (the hotel workbook has none)
        var rooms = res.Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int>? h = null;
        int lines = 0;
        foreach (var r in x.ReadRows(sheet))
        {
            if (h is null)
            {
                var hh = Headers(r);
                if (hh.ContainsKey("SUBCONTRACTOR") && hh.ContainsKey("LOCATION") && (hh.ContainsKey("CABLE") || hh.ContainsKey("ITEM"))) h = hh;
                continue;
            }
            var sub = H(r, h, "SUBCONTRACTOR");
            if (sub.Length == 0) continue;
            var qty = Num(H(r, h, "QTY (m)", "QTY (M)", "QTY M", "QTY"));
            if (qty is null) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"{sheet} row {r.Number}: no QTY - skipped.")); continue; }
            var cable = H(r, h, "CABLE", "ITEM").ToUpperInvariant();
            var stage = H(r, h, "STAGE").ToUpperInvariant() is { Length: > 0 } st ? st : CableStage(cable);
            var loc = H(r, h, "LOCATION");
            res.Claims.Add(new ClaimLine
            {
                Building = res.Building, Subcontractor = sub.Trim().ToUpperInvariant(), InvoiceNo = Int(H(r, h, "INV", "INVOICE")),
                Stage = stage, Floor = H(r, h, "FLOOR"), Room = loc, Item = cable, Unit = "m", Qty = qty.Value,
                SitePct = Num(H(r, h, "DRAWING %", "SITE %")) ?? 1, WirPct = Num(H(r, h, "WIR %")) ?? 1,
                WorkType = LedgerRules.NotComparedWorkType, Notes = NotComparedNote(stage),
                AreaType = rooms.TryGetValue(loc, out var room) ? room.AreaType : "",
                Source = ClaimSource, SourceKey = $"RECON|{res.Building}|NOCAP|row{r.Number}", EnteredAt = DateTime.Now,
            });
            lines++;
        }
        if (h is null) res.Issues.Add(new(3, IssueLevel.Warning, $"{sheet}: header row (SUBCONTRACTOR, LOCATION, CABLE, QTY) not found - cable lines not read."));
        else if (lines > 0) res.Issues.Add(new(0, IssueLevel.Warning, $"{sheet}: {lines} cable lines read as NOT COMPARED (cable pulling / cable tray) - kept in the ledger, not counted against any total."));
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Writes the recon in one batch for <see cref="ReconImportResult.Building"/> only: upserts that building's rooms by code (other rooms
    /// kept, AreaType edits kept), REPLACES every PROJECT QTY cell of the building, deletes the building's claim lines with Source RECON
    /// and inserts the new ones. The other building, and claim lines from other sources (TRACKER, MANUAL ...), are kept unless
    /// <paramref name="replaceAllBuildingClaims"/> is true, in which case every claim line of the building is deleted first.
    /// </summary>
    public static (int rooms, int qty, int claims, int deletedClaims) Commit(ReconImportResult res, IProjectStore store, bool replaceAllBuildingClaims = false)
    {
        var b = res.Building;
        var existingRooms = store.All<Room>().Where(r => r.Building == b)
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var oldQty = store.All<RoomQty>().Where(q => q.Building == b).ToList();
        var oldClaims = store.All<ClaimLine>().Where(c => c.Building == b && (replaceAllBuildingClaims || c.Source == ClaimSource)).ToList();
        int rooms = 0;
        store.Batch(w =>
        {
            foreach (var r in res.Rooms)
            {
                if (existingRooms.TryGetValue(r.Code, out var old))
                {
                    old.Floor = r.Floor; old.Level = r.Level; old.Zone = r.Zone; old.RoomType = r.RoomType;
                    if (r.Plot != 0) old.Plot = r.Plot;
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
        }, $"{b} recon {Path.GetFileName(res.ProjectQtyFile)} + {Path.GetFileName(res.ClaimsFile)}: {rooms} locations, {res.Quantities.Count} PROJECT QTY, " +
           $"{res.Claims.Count} RECON claim lines ({res.NotCompared.Count} not compared; {oldClaims.Count} old {b} lines removed)");
        return (rooms, res.Quantities.Count, res.Claims.Count, oldClaims.Count);
    }
}