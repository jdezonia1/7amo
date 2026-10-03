using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Recon;

namespace Raffaello.Core.Tests;

/// <summary>HOTEL RECON importer + closing check on tiny synthetic workbooks (layout of HOTEL_PROJECT_QTY.xlsx / HOTEL_REMAINING.xlsx, no real data).</summary>
public class HotelReconTests
{
    private static string ProjectQty()
    {
        var path = Path.Combine(TestData.TempDir(), "HOTEL_PROJECT_QTY.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("PROJECT QTY");
        ws.Cell(1, 2).Value = "PROJECT QUANTITY - TEST HOTEL";
        ws.Cell(2, 2).Value = "KEY  (stage|item - read by the tracker, do not edit)";
        var keys = new[] { "CEILING|POWER", "1ST FIX|LIGHT", "DB PANELS|GRMS PANEL", "CABLE TRAY|50 MM" };
        var units = new[] { "no", "no", "no", "m" };
        for (var i = 0; i < keys.Length; i++) { ws.Cell(2, 12 + i).Value = keys[i]; ws.Cell(4, 12 + i).Value = units[i]; }
        ws.Cell(3, 2).Value = "RATE  ->  (type rates in the yellow cells)";
        ws.Cell(4, 2).Value = "UNIT";
        ws.Cell(5, 2).Value = "LOCATION";
        ws.Cell(5, 12).Value = "CEILING";
        var hdr = new[] { "PART", "FLOOR", "LEVEL", "UNIT / AREA No.", "LOCATION", "UNIT TYPE", "QTY SOURCE", "TOTAL No.", "TRAY m" };
        for (var i = 0; i < hdr.Length; i++) ws.Cell(6, 2 + i).Value = hdr[i];
        object?[][] rows =
        {
            new object?[] { "H1", "B2", "Basement 2", null, "H1-ELEC ROOM", "Public", "PUBLIC DRAWINGS: POWER", null, null, null, 2, 5, 0, 12.5 },
            new object?[] { "H3", "L1", "Level 01", "101", "H3-L1-101", "TSK", "ROOM TYPE", null, null, null, 4, 10, 1, null },
            new object?[] { "H3", "L1", "Level 01", "101", "H3-L1-101", "TSK", "ROOM TYPE (part 2)", null, null, null, null, 1, null, null },
            new object?[] { "H1", "B2", "Basement 2", null, null, "Public", "no location", null, null, null, 99, 99, null, null },
        };
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } v) ws.Cell(7 + r, 2 + c).Value = XLCellValue.FromObject(v);
        ws.Cell(12, 2).Value = "Public areas: location = PART-ROOM NAME (e.g. H1-ELEC ROOM)";
        ws.Cell(13, 2).Value = "Rules: switches under LIGHT; twin socket = 1";
        wb.SaveAs(path);
        return path;
    }

    private static string CleanClaims()
    {
        var path = Path.Combine(TestData.TempDir(), "HOTEL_REMAINING.xlsx");
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("CHECK").Cell(1, 1).Value = "summary";
        var ws = wb.Worksheets.Add("CLEAN CLAIMS");
        ws.Cell(1, 1).Value = "CLEAN CLAIM LINES (input for the app)";
        // headers by name: QTY placed before ITEM on purpose
        var hdr = new[] { "SUBCONTRACTOR", "INV", "OLD STAGE", "STAGE", "FLOOR", "OLD LOCATION", "LOCATION", "OLD ITEM", "QTY", "ITEM", "DRAWING %", "WIR %", "REWORK", "FLAGS", "SOURCE ROW" };
        for (var i = 0; i < hdr.Length; i++) ws.Cell(3, 1 + i).Value = hdr[i];
        object?[][] rows =
        {
            new object?[] { "SubA", 1, "1ST FIX", "1ST FIX", "Level 01", "H3-L1-101", "H3-L1-101", "LIGHT", 6, "LIGHT", 1, 1, null, null, 14 },
            new object?[] { "SUBB", 2, "1ST FIX", "1ST FIX", "Level 01", "H3-L1-101", "H3-L1-101", "LIGHT", 7, "LIGHT", 1, 1, null, null, 15 },
            new object?[] { "SUBA", 1, "CEILING", "CEILING", "Basement 2", "H1-POOL", "H1-ELEC ROOM", "POWER", 0.75, "POWER", 1, 1, null, "POOL SPREAD", 16 },
            new object?[] { "SUBA", 1, "CEILING", "CEILING", "Basement 2", "H1-ELEC ROOM", "H1-ELEC ROOM", "POWER", 3, "POWER", 1, 1, "YES", null, 17 },
            new object?[] { "SUBC", 3, "1ST FIX", "CEILING", "Level 01", "H3-L1-101", "H3-L1-101", "SOCKET", 1.25, "POWER", 0.5, 0.8, null, "ASSUMED STAGE, POOL SPREAD", 18 },
            new object?[] { null, null, null, null, null, null, null, null, null, null, null, null, null, "total row", null },
        };
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } v) ws.Cell(4 + r, 1 + c).Value = XLCellValue.FromObject(v);
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reads_keys_units_and_locations_and_skips_note_rows()
    {
        var r = HotelReconImporter.Read(ProjectQty(), CleanClaims());
        Assert.DoesNotContain(r.Issues, i => i.Level == Import.IssueLevel.Error);
        Assert.Equal(new[] { "1ST FIX|LIGHT", "CABLE TRAY|50 MM", "CEILING|POWER", "DB PANELS|GRMS PANEL" },
            r.Quantities.Select(q => q.Stage + "|" + q.Item).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("m", r.Quantities.Single(q => q.Item == "50 MM").Unit);
        Assert.All(r.Quantities, q => { Assert.Equal(Buildings.Hotel, q.Building); Assert.Equal("QS SURVEY", q.Source); });
        // zero / empty cells skipped; note rows and the row without LOCATION skipped
        Assert.Equal(2, r.Rooms.Count);
        Assert.DoesNotContain(r.Quantities, q => q.Qty == 99);
        Assert.DoesNotContain(r.Quantities, q => q.Room == "H1-ELEC ROOM" && q.Item == "GRMS PANEL");
        Assert.Equal(35.5, r.TotalQty, 9);
        Assert.Contains(r.Issues, i => i.Message.Contains("H3-L1-101 is on 2 rows"));

        var elec = r.Rooms.Single(x => x.Code == "H1-ELEC ROOM");
        Assert.Equal(("H1", "Public", -2, "Basement 2"), (elec.Zone, elec.RoomType, elec.Floor, elec.Level));
        Assert.Equal("BOH", elec.AreaType);   // Public in a basement
        var guest = r.Rooms.Single(x => x.Code == "H3-L1-101");
        Assert.Equal(("101", 1, "GUESTROOM"), (guest.Unit, guest.Floor, guest.AreaType));
    }

    [Fact]
    public void Reads_claims_by_header_name_with_fractional_qty_rework_and_notes()
    {
        var r = HotelReconImporter.Read(ProjectQty(), CleanClaims());
        Assert.Equal(5, r.Claims.Count);
        Assert.All(r.Claims, c => { Assert.Equal("RECON", c.Source); Assert.Equal(Buildings.Hotel, c.Building); Assert.Equal("no", c.Unit); });
        Assert.Equal(r.Claims.Count, r.Claims.Select(c => c.SourceKey).Distinct().Count());
        Assert.Equal("SUBA", r.Claims[0].Subcontractor);
        Assert.Equal("RECON|HOTEL|row4", r.Claims[0].SourceKey);

        var pool = r.Claims.Single(c => c.Qty == 0.75);
        Assert.Equal(("H1-ELEC ROOM", "CEILING", "POWER", 1), (pool.Room, pool.Stage, pool.Item, pool.InvoiceNo));
        Assert.StartsWith("POOL SPREAD | old: H1-POOL", pool.Notes);

        var assumed = r.Claims.Single(c => c.Subcontractor == "SUBC");
        Assert.Equal((0.5, 0.8, 3), (assumed.SitePct, assumed.WirPct, assumed.InvoiceNo));
        Assert.Contains("old: 1ST FIX / SOCKET", assumed.Notes);
        Assert.Equal("GUESTROOM", assumed.AreaType);

        Assert.True(r.Claims.Single(c => c.Qty == 3).Rework);
        Assert.Equal(15, r.ClaimedQty, 9);
        Assert.Equal(3, r.ReworkQty, 9);
    }

    [Fact]
    public void Length_extra_goes_to_the_15m_check_not_to_the_room_total()
    {
        var path = Path.Combine(TestData.TempDir(), "HOTEL_REMAINING_LEN.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("CLEAN CLAIMS");
            var hdr = new[] { "SUBCONTRACTOR", "INV", "STAGE", "FLOOR", "LOCATION", "ITEM", "QTY", "DRAWING %", "WIR %", "REWORK", "FLAGS", "SOURCE ROW", "LENGTH EXTRA (15 m rule)" };
            for (var i = 0; i < hdr.Length; i++) ws.Cell(3, 1 + i).Value = hdr[i];
            object?[][] rows =
            {
                new object?[] { "SKY", 1, "2ND FIX", "Level 01", "H3-L1-101", "DALI", 19, 1, 1, null, "15M RULE EXTRA - CHECK ROUTE LENGTH", 4, 26 },
                new object?[] { "SKY", 1, "1ST FIX", "Level 01", "H3-L1-101", "LIGHT", 5, 1, 1, null, null, 5, null },
            };
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] is { } v) ws.Cell(4 + r, 1 + c).Value = XLCellValue.FromObject(v);
            wb.SaveAs(path);
        }
        var res = HotelReconImporter.Read(ProjectQty(), path);
        var dali = res.Claims.Single(c => c.Item == "DALI");
        Assert.Equal(19, dali.Qty, 9);                       // plan points count against the room total
        Assert.True(dali.LengthApplies);
        Assert.Equal(45, dali.LengthClaimedQty, 9);          // plan + 15 m extras
        Assert.Equal(CheckStatus.Pending, dali.LengthStatus);
        Assert.Contains("15 m rule", dali.Notes);
        var light = res.Claims.Single(c => c.Item == "LIGHT");
        Assert.False(light.LengthApplies);
        Assert.Equal(0, light.LengthClaimedQty, 9);
        Assert.Equal(26, res.LengthExtraQty, 9);
        Assert.Equal(24, res.ClaimedQty, 9);                 // extras are not in the claimed quantity
    }

    [Fact]
    public void Check_closes_ignores_rework_and_shows_overclaim_negative()
    {
        var c = ReconCheck.Run(HotelReconImporter.Read(ProjectQty(), CleanClaims()));
        Assert.Equal(35.5, c.ProjectTotal, 9);
        Assert.Equal(15, c.ClaimedTotal, 9);
        Assert.Equal(3, c.ReworkTotal, 9);
        Assert.Equal(20.5, c.RemainingTotal, 9);
        Assert.True(c.Closes);
        Assert.Equal(0, c.ClosingDifference, 9);

        double Rem(string room, string stage, string item) => c.Rows.Single(x => x.Room == room && x.Stage == stage && x.Item == item).Remaining;
        Assert.Equal(-2, Rem("H3-L1-101", "1ST FIX", "LIGHT"), 9);          // 11 total (two rows) - 6 - 7
        Assert.Equal(1.25, Rem("H1-ELEC ROOM", "CEILING", "POWER"), 9);      // 2 - 0.75, rework 3 not counted
        Assert.Equal(2.75, Rem("H3-L1-101", "CEILING", "POWER"), 9);
        Assert.Equal(1, c.OverKeys);
        Assert.Equal(2, c.OverQty, 9);
        var light = c.ByStageItem.Single(k => k.Stage == "1ST FIX" && k.Item == "LIGHT");
        Assert.Equal((16.0, 13.0, 1), (light.Total, light.Claimed, light.OverRooms));
    }

    [Fact]
    public void Check_counts_claims_without_a_total()
    {
        var qty = new[] { new RoomQty { Building = Buildings.Hotel, Room = "A", Stage = "1ST FIX", Item = "POWER", Qty = 10 } };
        var claims = new[]
        {
            new ClaimLine { Building = Buildings.Hotel, Room = "A", Stage = "1ST FIX", Item = "POWER", Qty = 4 },
            new ClaimLine { Building = Buildings.Hotel, Room = "B", Stage = "1ST FIX", Item = "POWER", Qty = 2.5 },
        };
        var c = ReconCheck.Run(qty, claims);
        Assert.Equal(1, c.ClaimKeysWithoutTotal);
        Assert.Equal(3.5, c.RemainingTotal, 9);
        Assert.True(c.Closes);
    }

    [Theory]
    [InlineData("B2", -2)]
    [InlineData("B1", -1)]
    [InlineData("GF", 0)]
    [InlineData("L1", 1)]
    [InlineData("L02", 2)]
    [InlineData("RF", 99)]
    public void Parses_floor_codes(string code, int floor) => Assert.Equal(floor, HotelReconImporter.ParseFloor(code));

    [Fact]
    public void Commit_replaces_hotel_qty_and_recon_lines_and_keeps_other_data()
    {
        var db = TestData.NewDb();
        db.Batch(w =>
        {
            w.Insert(new RoomQty { Building = Buildings.Hotel, Room = "OLD", Stage = "1ST FIX", Item = "POWER", Qty = 100, Source = "TRACKER" });
            w.Insert(new RoomQty { Building = Buildings.Branded, Room = "P9-101", Stage = "1ST FIX", Item = "POWER", Qty = 40 });
            w.Insert(new ClaimLine { Building = Buildings.Hotel, Room = "OLD", Stage = "1ST FIX", Item = "POWER", Qty = 5, Source = "TRACKER", SourceKey = "TRK|1" });
            w.Insert(new ClaimLine { Building = Buildings.Branded, Room = "P9-101", Stage = "1ST FIX", Item = "POWER", Qty = 7, Source = "TRACKER", SourceKey = "TRK|2" });
            w.Insert(new Room { Building = Buildings.Hotel, Code = "H3-L1-101", AreaType = "FOH" });
        }, "seed");
        var r = HotelReconImporter.Read(ProjectQty(), CleanClaims());
        var first = HotelReconImporter.Commit(r, db);
        Assert.Equal((2, 7, 5, 0), first);
        // re-import: RECON lines replaced, not doubled
        var again = HotelReconImporter.Commit(HotelReconImporter.Read(ProjectQty(), CleanClaims()), db);
        Assert.Equal(5, again.deletedClaims);

        var qty = db.All<RoomQty>();
        Assert.Equal(35.5, qty.Where(q => q.Building == Buildings.Hotel).Sum(q => q.Qty), 9);
        Assert.Single(qty, q => q.Building == Buildings.Branded);
        var claims = db.All<ClaimLine>();
        Assert.Equal(5, claims.Count(c => c.Source == "RECON"));
        Assert.Single(claims, c => c.Building == Buildings.Hotel && c.Source == "TRACKER");
        Assert.Single(claims, c => c.Building == Buildings.Branded);
        Assert.Equal("FOH", db.All<Room>().Single(x => x.Code == "H3-L1-101").AreaType);   // area type edit kept
        Assert.Equal(1, db.All<Room>().Count(x => x.Code == "H3-L1-101"));

        var all = HotelReconImporter.Commit(HotelReconImporter.Read(ProjectQty(), CleanClaims()), db, replaceAllHotelClaims: true);
        Assert.Equal(6, all.deletedClaims);
        Assert.DoesNotContain(db.All<ClaimLine>(), c => c.Building == Buildings.Hotel && c.Source != "RECON");
        Assert.Single(db.All<ClaimLine>(), c => c.Building == Buildings.Branded);
    }
}