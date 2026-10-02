using System.Globalization;
using System.Xml.Linq;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Tracker;

public sealed class TrackerImportResult
{
    public string FileName { get; init; } = "";
    public string Building { get; init; } = Buildings.Branded;
    public List<Room> Rooms { get; } = new();
    public List<RoomQty> Quantities { get; } = new();
    public List<ClaimLine> Claims { get; } = new();
    public List<PlanImage> Plans { get; } = new();
    public List<RoomShape> Shapes { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public int Subcontractors => Claims.Select(c => c.Subcontractor).Distinct().Count();
    public int Invoices => Claims.Select(c => (c.Subcontractor, c.InvoiceNo)).Distinct().Count();

    public string Summary =>
        $"{Rooms.Count} rooms, {Quantities.Count} PROJECT QTY cells ({Quantities.Select(q => q.Stage + "|" + q.Item).Distinct().Count()} stage|item keys), " +
        $"{Claims.Count} ledger lines ({Subcontractors} subcontractors, {Invoices} invoices), {Plans.Count} plan images, {Shapes.Count} room shapes";
}

/// <summary>
/// Reads the BRANDED_MEP_TRACKER (v19): ROOMS, PROJECT QTY (row 2 keys "STAGE|ITEM", row 6 headers), LEDGER (row 2 headers)
/// and the PLANS drawing (level images + room shapes named RM_&lt;room&gt;). Streams the sheets, so the 37 MB formula sheets are skipped.
/// </summary>
public static class TrackerImporter
{
    public static TrackerImportResult Read(string path, string building = Buildings.Branded, bool includePlans = true)
    {
        using var x = new XlsxStreamReader(path);
        var result = new TrackerImportResult { FileName = path, Building = building };
        if (x.HasSheet("ROOMS")) ReadRooms(x, result); else result.Issues.Add(new(0, IssueLevel.Error, "Sheet ROOMS not found."));
        if (x.HasSheet("PROJECT QTY")) ReadProjectQty(x, result); else result.Issues.Add(new(0, IssueLevel.Error, "Sheet PROJECT QTY not found."));
        if (x.HasSheet("LEDGER")) ReadLedger(x, result); else result.Issues.Add(new(0, IssueLevel.Warning, "Sheet LEDGER not found."));
        if (includePlans && x.HasSheet("PLANS")) ReadPlans(x, result);
        return result;
    }

    private static Dictionary<string, int> Headers(XlsxRow row) =>
        row.Values.Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .GroupBy(v => TableReader.NormalizeHeader(v.Value)).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

    private static string H(XlsxRow r, Dictionary<string, int> h, params string[] names)
    {
        foreach (var n in names)
            if (h.TryGetValue(TableReader.NormalizeHeader(n), out var c)) { var v = r.Get(c).Trim(); if (v.Length > 0) return v; }
        return "";
    }

    private static double? HN(XlsxRow r, Dictionary<string, int> h, params string[] names)
    {
        var s = H(r, h, names);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;

    private static void ReadRooms(XlsxStreamReader x, TrackerImportResult res)
    {
        Dictionary<string, int>? h = null;
        foreach (var r in x.ReadRows("ROOMS"))
        {
            if (h is null) { h = Headers(r); continue; }
            var loc = H(r, h, "LOCATION");
            if (loc.Length == 0) continue;
            var unitType = H(r, h, "UNIT TYPE");
            var level = H(r, h, "LEVEL");
            res.Rooms.Add(new Room
            {
                Building = res.Building, Code = loc, Plot = I(H(r, h, "PLOT")), Floor = I(H(r, h, "FLOOR")), Level = level,
                Unit = H(r, h, "UNIT"), RoomType = unitType, DwgUnitType = H(r, h, "DWG UNIT TYPE"), Plan = H(r, h, "PLAN"),
                AreaType = AreaTypes.Guess(unitType, level, loc), Zone = H(r, h, "PLOT") is { Length: > 0 } p ? "PLOT " + p : "",
            });
        }
    }

    private static void ReadProjectQty(XlsxStreamReader x, TrackerImportResult res)
    {
        Dictionary<int, string> keys = new();
        Dictionary<int, string> units = new();
        Dictionary<string, int>? h = null;
        var known = res.Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var r in x.ReadRows("PROJECT QTY"))
        {
            if (r.Number == 2)
            {
                foreach (var (c, v) in r.Values)
                    if (System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^[A-Za-z0-9 .\-/]+\|[A-Za-z0-9 .\-/]+$")) keys[c] = v.Trim();
                continue;
            }
            if (r.Number == 4) { foreach (var (c, v) in r.Values) units[c] = v.Trim(); continue; }
            if (r.Number == 6) { h = Headers(r); continue; }
            if (h is null || r.Number < 7) continue;
            var loc = H(r, h, "LOCATION");
            if (loc.Length == 0) continue;
            if (!known.ContainsKey(loc))
            {
                var room = new Room
                {
                    Building = res.Building, Code = loc, Plot = I(H(r, h, "PLOT")), Floor = I(H(r, h, "FLOOR")), Level = H(r, h, "LEVEL"),
                    Unit = H(r, h, "UNIT NO"), RoomType = H(r, h, "UNIT TYPE"),
                };
                room.AreaType = AreaTypes.Guess(room.RoomType, room.Level, loc);
                res.Rooms.Add(room);
                known[loc] = room;
                res.Issues.Add(new(r.Number, IssueLevel.Warning, $"{loc} is in PROJECT QTY but not in ROOMS - added."));
            }
            var source = H(r, h, "QTY SOURCE");
            foreach (var (c, key) in keys)
            {
                var qty = r.Number_(c);
                if (qty is null || Math.Abs(qty.Value) < 1e-9) continue;
                var parts = key.Split('|', 2);
                res.Quantities.Add(new RoomQty
                {
                    Building = res.Building, Room = loc, Stage = parts[0].Trim().ToUpperInvariant(), Item = parts[1].Trim().ToUpperInvariant(),
                    Unit = units.GetValueOrDefault(c, "no"), Qty = qty.Value, Source = source,
                });
            }
        }
        if (keys.Count == 0) res.Issues.Add(new(2, IssueLevel.Error, "PROJECT QTY row 2 has no STAGE|ITEM keys."));
    }

    /// <summary>"INV-9 (cumulative)", "CUM", "تراكمي" in the notes or invoice cell mark a cumulative invoice line.</summary>
    public static bool IsCumulativeText(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text ?? "", @"\bcumulative\b|\bcum\.?\b|تراكمي", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void ReadLedger(XlsxStreamReader x, TrackerImportResult res)
    {
        Dictionary<string, int>? h = null;
        var rooms = res.Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var r in x.ReadRows("LEDGER"))
        {
            if (r.Number == 2) { h = Headers(r); continue; }
            if (h is null || r.Number < 3) continue;
            var sub = H(r, h, "SUBCONTRACTOR");
            if (sub.Length == 0) continue;
            var qty = HN(r, h, "QTY");
            var loc = H(r, h, "LOCATION");
            var stage = H(r, h, "STAGE").ToUpperInvariant();
            var item = H(r, h, "ITEM").ToUpperInvariant();
            if (qty is null) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"LEDGER row {r.Number}: no QTY - skipped.")); continue; }
            if (item.Length == 0) res.Issues.Add(new(r.Number, IssueLevel.Warning, $"LEDGER row {r.Number}: ITEM empty ({sub} {loc} {stage})."));
            if (loc.Length > 0 && !rooms.ContainsKey(loc)) res.Issues.Add(new(r.Number, IssueLevel.Warning, $"LEDGER row {r.Number}: location {loc} not in ROOMS."));
            var invoiceText = H(r, h, "INVOICE");
            var line = new ClaimLine
            {
                Building = res.Building, Subcontractor = sub.Trim().ToUpperInvariant(), InvoiceNo = I(invoiceText), Stage = stage, Floor = H(r, h, "FLOOR"),
                Room = loc, Item = item, Unit = H(r, h, "UNIT") is { Length: > 0 } u ? u : "no", Qty = qty.Value,
                SitePct = HN(r, h, "SITE %") ?? 1, WirPct = HN(r, h, "WIR %") ?? 1, Notes = H(r, h, "NOTES"),
                WirNo = H(r, h, "WIR NO", "WIR NO.", "WIR NUMBER", "WIR REF", "WIR"),   // [phase6] optional column
                Rework = H(r, h, "REWORK?").StartsWith("Y", StringComparison.OrdinalIgnoreCase), WorkType = H(r, h, "WORK TYPE"),
                AreaType = rooms.TryGetValue(loc, out var room) ? room.AreaType : "", Source = "TRACKER",
                SourceKey = string.Join('|', "TRK", r.Number, sub, invoiceText, stage, loc, item, qty.Value.ToString(CultureInfo.InvariantCulture)),
                EnteredAt = DateTime.Now,
                IsCumulative = IsCumulativeText(H(r, h, "NOTES")) || IsCumulativeText(invoiceText),
            };
            var above = HN(r, h, "QTY ABOVE 4.5", "QTY CLAIMED ABOVE 4.5 M", "QTY >4.5");
            if (above is > 0)
            {
                line.QtyAbove45 = above.Value;
                line.HeightStatus = H(r, h, "CHECK STATUS", "HEIGHT STATUS") is { Length: > 0 } st ? st.ToUpperInvariant() : CheckStatus.Pending;
                line.QtyAbove45Accepted = HN(r, h, "QTY ACCEPTED ABOVE 4.5", "QTY ACCEPTED >4.5") ?? 0;
            }
            if (LengthExtras.ConvertDataRack(line, alreadyInvoiced: line.InvoiceNo > 0))
                res.Issues.Add(new(r.Number, IssueLevel.Warning, $"LEDGER row {r.Number}: DATA RACK {line.LengthClaimedQty:0.##} ({sub} {loc}) read as 15 m extra on DATA 2ND FIX, not against PROJECT QTY."));
            res.Claims.Add(line);
        }
        if (h is null) res.Issues.Add(new(2, IssueLevel.Error, "LEDGER row 2 headers not found."));
    }

    // ------------------------------------------------------------------ plans

    private static void ReadPlans(XlsxStreamReader x, TrackerImportResult res)
    {
        var part = x.DrawingPartOf("PLANS");
        if (part is null) { res.Issues.Add(new(0, IssueLevel.Warning, "PLANS sheet has no drawing.")); return; }
        var doc = x.LoadXml(part);
        var rels = x.RelationshipsOf(part);
        var parsed = PlanDrawingParser.Parse(doc, id => rels.TryGetValue(id, out var p) ? x.ReadBytes(p) : null, res.Building);
        res.Plans.AddRange(parsed.Plans);
        var codes = new HashSet<string>(res.Rooms.Select(r => r.Code), StringComparer.OrdinalIgnoreCase);
        foreach (var s in parsed.Shapes)
        {
            if (!codes.Contains(s.Room)) res.Issues.Add(new(0, IssueLevel.Warning, $"Plan shape {s.ShapeName}: room {s.Room} not in ROOMS."));
            res.Shapes.Add(s);
        }
        res.Issues.AddRange(parsed.Issues);
    }

    // ------------------------------------------------------------------ commit

    /// <summary>
    /// Upserts rooms (by building + code, keeping AreaType edits), replaces PROJECT QTY for the building, appends ledger lines
    /// whose SourceKey is new (re-importing the same tracker adds nothing), and replaces plans and shapes.
    /// </summary>
    public static (int rooms, int qty, int claims, int skipped) Commit(TrackerImportResult res, IProjectStore store, bool replaceQty = true)
    {
        var existingRooms = store.All<Room>().Where(r => r.Building == res.Building).GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existingKeys = store.All<ClaimLine>().Select(c => c.SourceKey).Where(k => k.Length > 0).ToHashSet();
        var oldQty = replaceQty ? store.All<RoomQty>().Where(q => q.Building == res.Building).ToList() : new();
        var oldShapes = store.All<RoomShape>().Where(s => s.Building == res.Building).ToList();
        var oldPlans = store.All<PlanImage>().Where(p => p.Building == res.Building).ToList();
        int rooms = 0, claims = 0, skipped = 0;
        store.Batch(w =>
        {
            foreach (var r in res.Rooms)
            {
                if (existingRooms.TryGetValue(r.Code, out var old))
                {
                    old.Plot = r.Plot; old.Floor = r.Floor; old.Level = r.Level; old.Unit = r.Unit; old.RoomType = r.RoomType;
                    old.DwgUnitType = r.DwgUnitType; old.Plan = r.Plan;
                    if (string.IsNullOrEmpty(old.AreaType)) old.AreaType = r.AreaType;
                    w.Update(old);
                }
                else w.Insert(r);
                rooms++;
            }
            foreach (var q in oldQty) w.Delete(q);
            w.InsertMany(res.Quantities);
            foreach (var c in res.Claims)
            {
                if (existingKeys.Contains(c.SourceKey)) { skipped++; continue; }
                w.Insert(c);
                claims++;
            }
            foreach (var s in oldShapes) w.Delete(s);
            foreach (var p in oldPlans) w.Delete(p);
            w.InsertMany(res.Shapes);
            w.InsertMany(res.Plans);
        }, $"Tracker import {Path.GetFileName(res.FileName)}: {rooms} rooms, {res.Quantities.Count} PROJECT QTY, {claims} new ledger lines ({skipped} already imported)");
        return (rooms, res.Quantities.Count, claims, skipped);
    }
}
