using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Recon;

namespace Raffaello.Core.Tests;

/// <summary>BRANDED RECON (one workbook: PROJECT QTY + CLEAN CLAIMS + NO CAP (CABLES)), NOT COMPARED lines and whole-number points. Synthetic data only.</summary>
public class BrandedReconTests
{
    private static void Put(IXLWorksheet ws, int firstRow, int firstCol, object?[][] rows)
    {
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } v) ws.Cell(firstRow + r, firstCol + c).Value = XLCellValue.FromObject(v);
    }

    private static string Workbook(bool fractional = false)
    {
        var path = Path.Combine(TestData.TempDir(), "BRANDED_REMAINING.xlsx");
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("CHECK").Cell(1, 1).Value = "summary";

        var cc = wb.Worksheets.Add("CLEAN CLAIMS");
        cc.Cell(1, 1).Value = "CLEAN CLAIM LINES (input for the app)";
        var hdr = new[] { "SUBCONTRACTOR", "INV", "OLD STAGE", "STAGE", "FLOOR", "OLD LOCATION", "LOCATION", "OLD ITEM", "ITEM", "QTY", "DRAWING %", "WIR %", "REWORK", "FLAGS", "SOURCE ROW", "LENGTH EXTRA (15 m rule)" };
        for (var i = 0; i < hdr.Length; i++) cc.Cell(3, 1 + i).Value = hdr[i];
        Put(cc, 4, 1, new[]
        {
            new object?[] { "ROOTS", 1, "1ST FIX", "1ST FIX", "Level 01", "P2-101", "P2-101", "LIGHT", "LIGHT", 30, 1, 1, null, null, 3, null },
            new object?[] { "ROOTS", 1, "1ST FIX", "1ST FIX", "Level 01", "P2-101", "P2-101", "TERRACE LIGHT", "TERRACE LIGHT", 2, 1, 1, null, null, 4, null },
            new object?[] { "AWRAD", 2, "1ST FIX", "1ST FIX", "Level 01", "P2-101", "P2-101", "LIGHT", "LIGHT", fractional ? 12.5 : 15, 1, 1, null, null, 5, null },
            new object?[] { "AWRAD", 2, "CEILING", "CEILING", "Ground Floor", "P2-GF", "P2-GF", "POWER", "POWER", 4, 1, 1, null, null, 6, null },
            new object?[] { "AWRAD", 2, "CABLE TRAY", "CABLE TRAY", "Ground Floor", "P2-GF", "P2-GF", "50 MM", "50 MM", 33.5, 1, 1, null, null, 7, null },
        });

        var nc = wb.Worksheets.Add("NO CAP (CABLES)");
        nc.Cell(1, 1).Value = "NOT COMPARED: CABLE PULLING (site statement) and CABLE TRAY (waiting for the final Revit model)";
        var nh = new[] { "SUBCONTRACTOR", "INV", "FLOOR", "LOCATION", "CABLE", "QTY (m)" };
        for (var i = 0; i < nh.Length; i++) nc.Cell(3, 1 + i).Value = nh[i];
        Put(nc, 4, 1, new[]
        {
            new object?[] { "ROOTS", 3, "BASEMENT 1", "EMCC-BR-Z2-LB1-FANS", "4X16", 162.4 },
            new object?[] { "CONCRETE PLUS", 9, "Ground Floor", "P2-GF", "100 MM", 47.64 },
            new object?[] { "CONCRETE PLUS", 9, "Ground Floor", "P2-GF", "450mm", 10 },
        });

        var pq = wb.Worksheets.Add("PROJECT QTY");
        pq.Cell(1, 2).Value = "PROJECT QUANTITY - BRANDED";
        pq.Cell(2, 2).Value = "KEY  (stage|item)";
        var keys = new[] { "1ST FIX|LIGHT", "1ST FIX|TERRACE LIGHT", "1ST FIX|TERRACE POWER", "CEILING|POWER" };
        for (var i = 0; i < keys.Length; i++) { pq.Cell(2, 11 + i).Value = keys[i]; pq.Cell(4, 11 + i).Value = "no"; }
        pq.Cell(2, 11 + keys.Length).Value = "KEY  (stage|item - read by the tracker, do not edit)";
        pq.Cell(4, 2).Value = "UNIT";
        var ph = new[] { "PART", "FLOOR", "LEVEL", "UNIT / AREA No.", "LOCATION", "UNIT TYPE", "QTY SOURCE", "TOTAL No." };
        for (var i = 0; i < ph.Length; i++) pq.Cell(6, 2 + i).Value = ph[i];
        Put(pq, 7, 2, new[]
        {
            new object?[] { "P2", "1", "Level 01", null, "P2-101", "2BR", "BRANDED TRACKER", null, null, 40, 3, fractional ? 1.5 : 1, null },
            new object?[] { "P2", "0", "Ground Floor", null, "P2-GF", "Public", "BRANDED TRACKER", null, null, null, null, null, 20 },
            new object?[] { "P2", "BS1", "Basement 1", null, "P2-BS1", "Public", "BRANDED TRACKER", null, null, 9, null, null, null },
            new object?[] { "P3", "RF", "Roof", null, "P3-RF", "Public", "BRANDED TRACKER", null, null, null, null, null, 6 },
            new object?[] { "P1", "0", "Ground Floor", null, "P1-1", "(V-1)", "BRANDED TRACKER", null, null, 50, null, null, null },
        });
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reads_branded_rooms_with_plot_floor_and_area_type()
    {
        var f = Workbook();
        var r = ReconImporter.Read("branded", f, f);
        Assert.Equal(Buildings.Branded, r.Building);
        Assert.DoesNotContain(r.Issues, i => i.Level == Import.IssueLevel.Error);
        Room Room(string code) => r.Rooms.Single(x => x.Code == code);
        Assert.Equal((2, 1, "APARTMENT", "PLOT 2"), (Room("P2-101").Plot, Room("P2-101").Floor, Room("P2-101").AreaType, Room("P2-101").Zone));
        Assert.Equal((2, 0, "FOH"), (Room("P2-GF").Plot, Room("P2-GF").Floor, Room("P2-GF").AreaType));
        Assert.Equal((2, -1, "BOH"), (Room("P2-BS1").Plot, Room("P2-BS1").Floor, Room("P2-BS1").AreaType));
        Assert.Equal((3, 99, "BOH"), (Room("P3-RF").Plot, Room("P3-RF").Floor, Room("P3-RF").AreaType));
        Assert.Equal((1, "APARTMENT"), (Room("P1-1").Plot, Room("P1-1").AreaType));
        Assert.All(r.Rooms, x => Assert.Equal(Buildings.Branded, x.Building));
        Assert.All(r.Quantities, q => Assert.Equal(Buildings.Branded, q.Building));
        // TERRACE LIGHT and TERRACE POWER are separate keys; the tracker's own KEY column is not a key
        Assert.Equal(new[] { "1ST FIX|LIGHT", "1ST FIX|TERRACE LIGHT", "1ST FIX|TERRACE POWER", "CEILING|POWER" },
            r.Quantities.Select(q => q.Stage + "|" + q.Item).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(129, r.TotalQty, 9);
    }

    [Fact]
    public void Cable_lines_are_imported_as_not_compared()
    {
        var f = Workbook();
        var r = ReconImporter.Read(Buildings.Branded, f, f);
        var nc = r.NotCompared;
        Assert.Equal(4, nc.Count);   // 3 NO CAP lines + the CABLE TRAY line in CLEAN CLAIMS
        Assert.All(nc, c => { Assert.Equal("m", c.Unit); Assert.Equal(LedgerRules.NotComparedWorkType, c.WorkType); Assert.Equal("RECON", c.Source); Assert.StartsWith("NOT COMPARED", c.Notes); });
        Assert.Equal(ReconImporter.CablePulling, nc.Single(c => c.Item == "4X16").Stage);
        Assert.Equal(ReconImporter.CableTray, nc.Single(c => c.Item == "100 MM").Stage);
        Assert.Equal(ReconImporter.CableTray, nc.Single(c => c.Item == "450MM").Stage);
        Assert.Contains("Revit", nc.Single(c => c.Item == "100 MM").Notes);
        Assert.Equal(r.Claims.Count, r.Claims.Select(c => c.SourceKey).Distinct().Count());
        // a panel location in NO CAP is not reported as a missing room
        Assert.DoesNotContain(r.Issues, i => i.Message.Contains("EMCC"));
        Assert.Equal(51, r.ClaimedQty, 9);   // 30 + 2 + 15 + 4; metres not added to points
    }

    [Fact]
    public void Not_compared_lines_never_count_against_a_total_or_show_over()
    {
        var f = Workbook();
        var c = ReconCheck.Run(ReconImporter.Read(Buildings.Branded, f, f));
        Assert.Equal(129, c.ProjectTotal, 9);
        Assert.Equal(51, c.ClaimedTotal, 9);
        Assert.Equal(78, c.RemainingTotal, 9);
        Assert.True(c.Closes);
        Assert.Equal(4, c.NotComparedLines);
        Assert.Equal(162.4 + 47.64 + 10 + 33.5, c.NotComparedTotal, 9);
        Assert.Equal(0, c.ClaimKeysWithoutTotal);   // cable keys have no total but are not "claimed with no total"
        Assert.Equal(1, c.OverKeys);                // P2-101 1ST FIX LIGHT 45 of 40
        Assert.DoesNotContain(c.Rows, x => x.Stage is ReconImporter.CablePulling or ReconImporter.CableTray);

        // the ledger rules (app OVER / NO CAP counters) ignore them too, even on a key that has a cap
        var qty = new[] { new RoomQty { Room = "A", Stage = "CABLE TRAY", Item = "50 MM", Unit = "m", Qty = 10 } };
        var lines = new[]
        {
            new ClaimLine { Room = "A", Stage = "CABLE TRAY", Item = "50 MM", Unit = "m", Qty = 25, WorkType = LedgerRules.NotComparedWorkType },
            new ClaimLine { Room = "B", Stage = "CABLE PULLING", Item = "4X16", Unit = "m", Qty = 99, WorkType = "not compared" },
        };
        var bal = LedgerRules.Balances(qty, lines);
        Assert.DoesNotContain(bal.Values, b => b.IsOver);
        Assert.False(bal.ContainsKey(LedgerKeys.Key("B", "CABLE PULLING", "4X16")));
        Assert.Equal(0, LedgerRules.Balance(qty, lines, "A", "CABLE TRAY", "50 MM").Claimed, 9);
    }

    [Fact]
    public void Fractional_point_quantities_are_listed_metres_are_not()
    {
        var f = Workbook(fractional: true);
        var c = ReconCheck.Run(ReconImporter.Read(Buildings.Branded, f, f));
        Assert.Equal(2, c.FractionalPoints.Count);
        Assert.Contains(c.FractionalPoints, p => p.Where == "CLAIM" && p.Qty == 12.5 && p.Who == "AWRAD INV 2");
        Assert.Contains(c.FractionalPoints, p => p.Where == "PROJECT QTY" && p.Item == "TERRACE POWER" && p.Qty == 1.5);
        var w = Assert.Single(c.Warnings);
        Assert.Contains("2 point quantities", w);
        Assert.Contains("P2-101 1ST FIX|LIGHT 12.5", w);

        var whole = ReconCheck.Run(ReconImporter.Read(Buildings.Branded, Workbook(), Workbook()));
        Assert.Empty(whole.FractionalPoints);   // 33.5 m of tray and 162.4 m of cable are fine
        Assert.Empty(whole.Warnings);
    }

    [Fact]
    public void Commit_replaces_only_the_buildings_own_qty_and_recon_lines()
    {
        var db = TestData.NewDb();
        db.Batch(w =>
        {
            w.Insert(new RoomQty { Building = Buildings.Hotel, Room = "H3-L1-101", Stage = "1ST FIX", Item = "LIGHT", Qty = 11, Source = "QS SURVEY" });
            w.Insert(new ClaimLine { Building = Buildings.Hotel, Room = "H3-L1-101", Stage = "1ST FIX", Item = "LIGHT", Qty = 6, Source = "RECON", SourceKey = "RECON|HOTEL|row4" });
            w.Insert(new RoomQty { Building = Buildings.Branded, Room = "OLD", Stage = "1ST FIX", Item = "LIGHT", Qty = 100, Source = "TRACKER" });
            w.Insert(new ClaimLine { Building = Buildings.Branded, Room = "OLD", Stage = "1ST FIX", Item = "LIGHT", Qty = 5, Source = "TRACKER", SourceKey = "TRK|1" });
            w.Insert(new Room { Building = Buildings.Branded, Code = "P2-101", AreaType = "BALCONY" });
        }, "seed");
        var f = Workbook();
        var first = ReconImporter.Commit(ReconImporter.Read(Buildings.Branded, f, f), db);
        Assert.Equal((5, 7, 8, 0), first);
        var again = ReconImporter.Commit(ReconImporter.Read(Buildings.Branded, f, f), db);
        Assert.Equal(8, again.deletedClaims);   // RECON lines replaced, not doubled

        var qty = db.All<RoomQty>();
        Assert.Equal(129, qty.Where(q => q.Building == Buildings.Branded).Sum(q => q.Qty), 9);
        Assert.Single(qty, q => q.Building == Buildings.Hotel);
        var claims = db.All<ClaimLine>();
        Assert.Equal(8, claims.Count(c => c.Building == Buildings.Branded && c.Source == "RECON"));
        Assert.Equal(4, claims.Count(c => c.Building == Buildings.Branded && LedgerRules.IsNotCompared(c)));
        Assert.Single(claims, c => c.Building == Buildings.Branded && c.Source == "TRACKER");
        Assert.Single(claims, c => c.Building == Buildings.Hotel && c.Source == "RECON");   // hotel recon untouched
        var p2 = db.All<Room>().Single(x => x.Code == "P2-101");
        Assert.Equal(("BALCONY", 2, 1), (p2.AreaType, p2.Plot, p2.Floor));   // area type edit kept, plot / floor set

        var all = ReconImporter.Commit(ReconImporter.Read(Buildings.Branded, f, f), db, replaceAllBuildingClaims: true);
        Assert.Equal(9, all.deletedClaims);
        Assert.DoesNotContain(db.All<ClaimLine>(), c => c.Building == Buildings.Branded && c.Source != "RECON");
        Assert.Single(db.All<ClaimLine>(), c => c.Building == Buildings.Hotel);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("BS1", -1)]
    [InlineData("LVL1", 1)]
    [InlineData("Roof", 99)]
    public void Parses_branded_floor_codes(string code, int floor) => Assert.Equal(floor, ReconImporter.ParseFloor(code));

    [Theory]
    [InlineData("P2", 2)]
    [InlineData("H3", 3)]
    [InlineData("RF", 0)]
    public void Parses_plot_from_part(string part, int plot) => Assert.Equal(plot, ReconImporter.ParsePlot(part));

    [Fact]
    public void Unknown_building_is_refused()
    {
        var f = Workbook();
        Assert.Throws<ArgumentException>(() => ReconImporter.Read("VILLAS", f, f));
        Assert.Throws<ArgumentException>(() => HotelReconImporter.Commit(ReconImporter.Read(Buildings.Branded, f, f), TestData.NewDb()));
    }
}