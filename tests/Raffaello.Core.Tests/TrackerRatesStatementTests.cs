using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Tests;

/// <summary>[tracker] Claim-line rates (contract / manual / imported, MISSING never 0), amounts, and the statement entry posting rules. Synthetic data.</summary>
public class TrackerRatesTests
{
    private static Contract SubA => new() { Subcontractor = "SUBA", ContractNo = Phase1Fixtures.Contract, Building = "", Status = "ACTIVE" };
    private static List<ContractItem> Items => Phase1Fixtures.ContractItems.Select(i => Phase1Fixtures.Item(i.No, i.Desc, i.Rate, i.Unit)).ToList();
    private static ClaimLine Line(string sub = "SUBA", string stage = "1ST FIX", string item = "POWER", double qty = 10, int inv = 1, double site = 1, double wir = 1) =>
        new() { Subcontractor = sub, Building = Buildings.Branded, Stage = stage, Item = item, Room = "P9-101", Qty = qty, InvoiceNo = inv, SitePct = site, WirPct = wir };

    private static RateBook Book(IEnumerable<MappingRule>? rules = null, bool withItems = true) =>
        new(new[] { SubA }, withItems ? Items : new List<ContractItem>(), Array.Empty<ContractItemBoq>(), Array.Empty<BoqItem>(), rules ?? Array.Empty<MappingRule>());

    [Fact]
    public void Contract_rate_resolves_from_the_subcontractor_contract_item()
    {
        var r = Book().Resolve(Line());
        Assert.Equal(55, r.Rate);
        Assert.Equal("1", r.ItemNo);
        Assert.StartsWith("CONTRACT", r.Source);
        var wiring = Book().Resolve(Line(stage: "2ND FIX"));
        Assert.Equal(28, wiring.Rate);
    }

    [Fact]
    public void Longer_subcontractor_name_finds_the_contract()
    {
        var book = new RateBook(new[] { new Contract { Subcontractor = "MARUF", ContractNo = "C1" } }, Array.Empty<ContractItem>(), Array.Empty<ContractItemBoq>(), Array.Empty<BoqItem>(), Array.Empty<MappingRule>());
        Assert.Equal("C1", book.ContractFor("MARUF BIN SHAHEN", Buildings.Hotel)?.ContractNo);
    }

    [Fact]
    public void Missing_rate_is_never_zero_and_says_why()
    {
        var noContract = Book().Resolve(Line(sub: "NOBODY"));
        Assert.True(noContract.IsMissing);
        Assert.Null(noContract.Rate);
        Assert.Equal(RateStatus.Missing, noContract.Status);
        Assert.Contains("no contract", noContract.Reason);
        var noItems = Book(withItems: false).Resolve(Line());
        Assert.Contains("no rate schedule", noItems.Reason);
        var amount = ClaimAmount.Of(Line(), noContract);
        Assert.Null(amount.Rate);
    }

    [Fact]
    public void Manual_rate_beats_contract_and_invoice_period_beats_manual()
    {
        var rules = new[]
        {
            new MappingRule { Kind = RateRules.Kind, ContractNo = RateRules.Scope("SUBA"), MatchKey = RateRules.Key("1ST FIX", "POWER"), Target = RateRules.Target(50, null) },
            new MappingRule { Kind = RateRules.Kind, ContractNo = RateRules.Scope("SUBA"), MatchKey = RateRules.Key("1ST FIX", "POWER", 1, 2), Target = RateRules.Target(41, 0.9) },
            new MappingRule { Kind = RateRules.Kind, ContractNo = RateRules.Scope(null), MatchKey = RateRules.Key("CEILING", "LIGHT"), Target = "33" },
        };
        var book = Book(rules);
        Assert.Equal(41, book.Resolve(Line(inv: 2)).Rate);           // old invoices: old rate
        Assert.Equal(0.9, book.Resolve(Line(inv: 2)).StagePct);
        Assert.Equal(50, book.Resolve(Line(inv: 3)).Rate);           // later: manual rate
        Assert.Equal(33, book.Resolve(Line(sub: "NOBODY", stage: "CEILING", item: "LIGHT")).Rate);   // imported default for every subcontractor
        Assert.Equal((1, 2), RateRules.Period("1ST FIX|POWER|INV 1-2"));
    }

    [Fact]
    public void Amounts_apply_site_wir_and_stage_payment_percent()
    {
        var rule = new MappingRule { Kind = RateRules.Kind, ContractNo = RateRules.Scope("SUBA"), MatchKey = "1ST FIX|POWER", Target = "10;0.9" };
        var (rate, a) = Book(new[] { rule }).Price(Line(qty: 20, site: 0.5, wir: 0.8));
        Assert.Equal(10, rate.Rate);
        Assert.Equal(200, a.Amount, 6);
        Assert.Equal(100, a.AfterSite, 6);
        Assert.Equal(80, a.AfterWir, 6);
        Assert.Equal(72, a.Payable, 6);
    }

    [Fact]
    public void Recon_tools_rate_mapping_layout_is_read_per_building()
    {
        var path = Path.Combine(TestData.TempDir(), "RATE_MAPPING_HOTEL.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("MAPPING");
            ws.Cell(1, 1).Value = "HOTEL - RATE MAPPING";
            string[] h = { "STAGE", "ITEM", "AUTO SR", "AUTO CONFIDENCE", "AUTO REASON", "YOUR SR (type here)", "YOUR NOTE", "USED SR", "USED CONFIDENCE", "CONTRACT DESCRIPTION", "UNIT", "RATE (SAR)" };
            for (var i = 0; i < h.Length; i++) ws.Cell(3, i + 1).Value = h[i];
            ws.Cell(4, 1).Value = "CEILING"; ws.Cell(4, 2).Value = "DALI"; ws.Cell(4, 8).Value = 182; ws.Cell(4, 9).Value = "HIGH"; ws.Cell(4, 12).Value = 55;
            wb.SaveAs(path);
        }
        var (rows, _) = RateMappingImporter.Read(path);
        var r = Assert.Single(rows);
        Assert.Equal(("HIGH", "182", 55.0), (r.Confidence, r.ItemNo, r.Rate));
        Assert.Equal(Buildings.Hotel, RateMappingImporter.BuildingOf(path));
        var rules = RateMappingImporter.ToRules(rows, path, Buildings.Hotel);
        Assert.Equal("SUB:*@HOTEL", rules[0].ContractNo);
        var book = Book(rules, withItems: false);
        var hotel = Line(sub: "RANA", stage: "CEILING", item: "DALI"); hotel.Building = Buildings.Hotel;
        var branded = Line(sub: "ROOTS", stage: "CEILING", item: "DALI");
        Assert.Equal(55, book.Resolve(hotel).Rate);
        Assert.True(book.Resolve(branded).IsMissing);   // a HOTEL mapping does not price BRANDED lines
    }

    [Fact]
    public void Mapping_sheet_is_read_into_rate_rules()
    {
        var path = Path.Combine(TestData.TempDir(), "REMAINING_VALUE_TEST.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("MAPPING");
            ws.Cell(1, 1).Value = "stage|item -> rate";
            string[] h = { "STAGE", "ITEM", "CONTRACT ITEM", "RATE", "CONFIDENCE" };
            for (var i = 0; i < h.Length; i++) ws.Cell(3, i + 1).Value = h[i];
            ws.Cell(4, 1).Value = "1ST FIX"; ws.Cell(4, 2).Value = "POWER"; ws.Cell(4, 3).Value = "1"; ws.Cell(4, 4).Value = 55; ws.Cell(4, 5).Value = "HIGH";
            ws.Cell(5, 1).Value = "2ND FIX"; ws.Cell(5, 2).Value = "DATA"; ws.Cell(5, 4).Value = "";
            wb.SaveAs(path);
        }
        var (rows, issues) = RateMappingImporter.Read(path);
        Assert.Single(rows);
        Assert.Single(issues);   // the row without a rate stays MISSING
        var rules = RateMappingImporter.ToRules(rows, path);
        Assert.Equal("SUB:*", rules[0].ContractNo);
        Assert.Equal(55, Book(rules, withItems: false).Resolve(Line(sub: "ANY")).Rate);
    }
}

public class StatementEntryTests
{
    private static RoomBalance Bal(string room, string stage, string item, double project, double claimed) =>
        new(room, stage, item, project, claimed, new Dictionary<string, double>());

    private static StatementDraft Draft(params StatementDraftLine[] lines) =>
        new() { Header = new StatementHeader { Subcontractor = "SUBA", InvoiceNo = 3, StatementNo = "ST-1", Building = Buildings.Hotel }, Lines = lines.ToList() };

    private static StatementDraftLine L(string stage, string item, int claimed, string option = PostOptions.WithinRemaining, string reason = "", string room = "H2-101") =>
        new() { Room = room, Stage = stage, Item = item, Claimed = claimed, Option = option, Reason = reason };

    private static Func<string, string, string, RoomBalance> Balances(double project, double claimed) => (r, s, i) => Bal(r, s, i, project, claimed);

    [Fact]
    public void Within_remaining_posts_whole_numbers_and_holds_the_excess()
    {
        var plan = StatementPlanner.Plan(Draft(L("1ST FIX", "POWER", 10)), Balances(20, 13.5));   // remaining 6.5 -> 6
        var p = Assert.Single(plan);
        Assert.Equal(6, p.Post);
        Assert.Equal(4, p.Excess);
        Assert.Equal(PlanStatus.Part, p.Status);
        var claims = StatementPlanner.ToClaims(Draft().Header, plan);
        Assert.Equal(6, claims.Single().Claim.Qty);
        Assert.Equal("ST-1", claims.Single().Claim.StatementNo);
        Assert.Null(claims.Single().OverReason);
    }

    [Fact]
    public void Two_lines_on_the_same_key_share_the_remaining()
    {
        var plan = StatementPlanner.Plan(Draft(L("1ST FIX", "POWER", 5), L("1ST FIX", "POWER", 5)), Balances(8, 0));
        Assert.Equal(new[] { 5, 3 }, plan.Select(p => p.Post));
        Assert.Equal(2, plan[1].Excess);
    }

    [Fact]
    public void Over_in_full_needs_a_reason()
    {
        var withReason = StatementPlanner.Plan(Draft(L("1ST FIX", "LIGHT", 10, PostOptions.FullWithReason, "site instruction 12")), Balances(5, 0)).Single();
        Assert.Equal(10, withReason.Post);
        Assert.True(withReason.Over);
        Assert.Equal("site instruction 12", StatementPlanner.ToClaims(Draft().Header, new[] { withReason }).Single().OverReason);
        var noReason = StatementPlanner.Plan(Draft(L("1ST FIX", "LIGHT", 10, PostOptions.FullWithReason)), Balances(5, 0)).Single();
        Assert.Equal(5, noReason.Post);
        Assert.False(noReason.Over);
    }

    [Fact]
    public void Second_fix_excess_can_go_to_the_15m_check()
    {
        var p = StatementPlanner.Plan(Draft(L("2ND FIX", "POWER", 12, PostOptions.LengthPending)), Balances(10, 2)).Single();
        Assert.True(p.LengthPending);
        Assert.Equal(8, p.Post);
        var claim = StatementPlanner.ToClaims(Draft().Header, new[] { p }).Single().Claim;
        Assert.True(claim.LengthApplies);
        Assert.Equal(12, claim.LengthClaimedQty);
        Assert.Equal(8, claim.Qty);
        // not 2nd fix -> within remaining
        var first = StatementPlanner.Plan(Draft(L("1ST FIX", "POWER", 12, PostOptions.LengthPending)), Balances(10, 2)).Single();
        Assert.False(first.LengthPending);
        Assert.Equal(8, first.Post);
    }

    [Fact]
    public void Cable_pulling_and_tray_are_not_compared()
    {
        var p = StatementPlanner.Plan(Draft(L("CABLE PULLING", "4X16", 140)), Balances(0, 0)).Single();
        Assert.True(p.NotCompared);
        Assert.Equal(140, p.Post);
        var claim = StatementPlanner.ToClaims(Draft().Header, new[] { p }).Single().Claim;
        Assert.True(LedgerRules.IsNotCompared(claim));
    }

    [Fact]
    public void No_project_qty_holds_everything_and_skip_posts_nothing()
    {
        var none = StatementPlanner.Plan(Draft(L("1ST FIX", "FIRE", 6)), Balances(0, 0)).Single();
        Assert.Equal((0, 6, PlanStatus.OnHold), (none.Post, none.Excess, none.Status));
        var skip = StatementPlanner.Plan(Draft(L("1ST FIX", "POWER", 6, PostOptions.Skip)), Balances(10, 0)).Single();
        Assert.Equal(0, skip.Post);
        Assert.Empty(StatementPlanner.ToClaims(Draft().Header, new[] { none, skip }));
    }

    [Fact]
    public void Comparison_prices_claimed_and_certified()
    {
        var rule = new MappingRule { Kind = RateRules.Kind, ContractNo = RateRules.Scope("SUBA"), MatchKey = "1ST FIX|POWER", Target = "55" };
        var book = new RateBook(Array.Empty<Contract>(), Array.Empty<ContractItem>(), Array.Empty<ContractItemBoq>(), Array.Empty<BoqItem>(), new[] { rule });
        var d = Draft(L("1ST FIX", "POWER", 10), L("1ST FIX", "LIGHT", 4));
        var rows = StatementComparison.Build(d.Header, StatementPlanner.Plan(d, Balances(6, 0)), book);
        var power = rows.Single(r => r.Item == "POWER");
        Assert.Equal((10, 6, 4), (power.Claimed, power.Certified, power.Difference));
        Assert.Equal(220, power.DifferenceAmount, 6);
        var light = rows.Single(r => r.Item == "LIGHT");
        Assert.Null(light.Rate);
        Assert.StartsWith("MISSING", light.RateStatus);
    }

    [Fact]
    public void Draft_round_trips_as_json()
    {
        var dir = Path.Combine(TestData.TempDir(), "drafts");
        var d = Draft(L("1ST FIX", "POWER", 10));
        var path = d.Save(dir);
        var back = StatementDraft.Load(path)!;
        Assert.Equal("SUBA", back.Header.Subcontractor);
        Assert.Equal(10, back.Lines.Single().Claimed);
        Assert.Contains(path, StatementDraft.List(dir));
    }
}
public class InvoiceStatusListTests
{
    private static ClaimLine C(string sub, int inv, double qty, string workType = "") => new() { Subcontractor = sub, InvoiceNo = inv, Qty = qty, Stage = "1ST FIX", Item = "POWER", Room = "H1-1", WorkType = workType };

    [Fact]
    public void Done_pending_and_differs()
    {
        var claims = new[] { C("RANA", 1, 100), C("RANA", 2, 50), C("RANA", 2, 1, LedgerRules.NotComparedWorkType) };
        var refs = new[]
        {
            new InvoiceReference("RANA", 1, 101, 90, "MATCH", "x.xlsx"),    // within 2 points -> DONE
            new InvoiceReference("RANA", 2, 80, 70, "", "x.xlsx"),          // 50 vs 80 -> DIFFERS
            new InvoiceReference("RANA", 4, 300, null, "NO DRAWING", "x.xlsx"), // no claim lines -> PENDING (not an error)
        };
        var rows = InvoiceStatusList.Build(Buildings.Hotel, claims, refs, Array.Empty<MappingRule>());
        Assert.Equal(DrawingStatus.Done, rows.Single(r => r.InvoiceNo == 1).Status);
        var two = rows.Single(r => r.InvoiceNo == 2);
        Assert.Equal(DrawingStatus.Differs, two.Status);
        Assert.Equal(50, two.DrawnQty);           // NOT COMPARED line left out
        Assert.Equal(-30, two.Diff);
        Assert.Equal(DrawingStatus.Pending, rows.Single(r => r.InvoiceNo == 4).Status);
    }

    [Fact]
    public void Manual_status_wins()
    {
        var rules = new[]
        {
            new MappingRule { Kind = DrawingStatus.RuleKind, ContractNo = InvoiceStatusList.RuleScope("MADAR", Buildings.Hotel), MatchKey = InvoiceStatusList.RuleKey(1), Target = "PENDING" },
            new MappingRule { Kind = DrawingStatus.RuleKind, ContractNo = InvoiceStatusList.RuleScope("SKY", Buildings.Hotel), MatchKey = InvoiceStatusList.RuleKey(1), Target = "DONE" },
            new MappingRule { Kind = DrawingStatus.RuleKind, ContractNo = InvoiceStatusList.RuleScope("SKY", Buildings.Branded), MatchKey = InvoiceStatusList.RuleKey(1), Target = "PENDING" },
        };
        var rows = InvoiceStatusList.Build(Buildings.Hotel, new[] { C("MADAR", 1, 10), C("SKY", 1, 10) }, new[] { new InvoiceReference("SKY", 1, 500, null, "", "") }, rules);
        Assert.Equal(DrawingStatus.Pending, rows.Single(r => r.Subcontractor == "MADAR").Status);
        Assert.Equal(DrawingStatus.Done, rows.Single(r => r.Subcontractor == "SKY").Status);   // DONE by hand although it differs; the BRANDED rule does not apply
    }

    [Fact]
    public void Two_points_and_two_percent_tolerance()
    {
        Assert.False(InvoiceStatusList.Differs(102, 100));
        Assert.False(InvoiceStatusList.Differs(1019, 1000));
        Assert.True(InvoiceStatusList.Differs(1021, 1000));
        Assert.True(InvoiceStatusList.Differs(3, 0));
    }

    [Fact]
    public void Reference_summary_sheet_is_read()
    {
        var path = Path.Combine(TestData.TempDir(), "CLAIMS_vs_INVOICES.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("SUMMARY");
            ws.Cell(1, 1).Value = "HOTEL - recon";
            string[] h = { "SUBCONTRACTOR", "INV", "DRAWING-CLAIMED PTS", "INVOICED PTS (before share)", "INVOICED PTS x SHARE", "CERTIFIED PTS (certificate)", "STATUS" };
            for (var i = 0; i < h.Length; i++) ws.Cell(4, i + 1).Value = h[i];
            ws.Cell(5, 1).Value = "RANA"; ws.Cell(5, 2).Value = 3; ws.Cell(5, 3).Value = 10; ws.Cell(5, 4).Value = 12; ws.Cell(5, 5).Value = 11; ws.Cell(5, 6).Value = 9; ws.Cell(5, 7).Value = "INVOICE MORE";
            ws.Cell(6, 1).Value = "TOTAL";
            wb.SaveAs(path);
        }
        var (rows, issues) = InvoiceStatusList.ReadReference(path);
        var r = Assert.Single(rows);
        Assert.Empty(issues);
        Assert.Equal(("RANA", 3, 12.0, 9.0, "INVOICE MORE"), (r.Subcontractor, r.InvoiceNo, r.Invoiced!.Value, r.Certified!.Value, r.RefStatus));
        var dir = Path.Combine(TestData.TempDir(), "ref");
        InvoiceStatusList.SaveReference(dir, Buildings.Hotel, rows);
        Assert.Single(InvoiceStatusList.LoadReference(dir, Buildings.Hotel));
    }
}
public class StatementPostingTests
{
    [Fact]
    public void Post_statement_goes_through_the_claim_check_and_is_recorded()
    {
        var db = TestData.NewDb();
        var project = new ProjectService(new Settings.AppSettings { DataFilePath = db.Path, SeedDemoData = false }, _ => db);
        project.Initialize();
        Raffaello.Core.Tracker.TrackerImporter.Commit(Raffaello.Core.Tracker.TrackerImporter.Read(Phase1Fixtures.Tracker()), db);
        project.Reload();
        var draft = new StatementDraft
        {
            Header = new StatementHeader { Subcontractor = "SUBC", InvoiceNo = 1, StatementNo = "ST-9", Building = Buildings.Branded },
            Lines =
            {
                new StatementDraftLine { Room = "P9-BS1", Stage = "1ST FIX", Item = "POWER", Claimed = 12 },   // total 10, nothing claimed -> 10 posted, 2 on hold
                new StatementDraftLine { Room = "P9-102", Stage = "2ND FIX", Item = "LIGHT", Claimed = 5 },    // total 70 -> all posted
            },
        };
        var plan = project.Workflow.PlanStatement(draft);
        Assert.Equal(new[] { 10, 5 }, plan.Select(p => p.Post));
        var (posted, count, problems) = project.Workflow.PostStatement(draft);
        Assert.Empty(problems);
        Assert.Equal(2, count);
        var lines = project.Snapshot.Claims.Where(c => c.StatementNo == "ST-9").ToList();
        Assert.Equal(15, lines.Sum(c => c.Qty));
        Assert.All(lines, l => Assert.Equal("STATEMENT", l.Source));
        Assert.Equal(0, project.Workflow.Balance("P9-BS1", "1ST FIX", "POWER").Remaining);
        Assert.Contains(project.Snapshot.Statements, s => s.StatementNo == "ST-9" && s.Direction == "IN" && s.Lines == 2);
        Assert.True(draft.Posted);
    }
}
public class PlanPackageTests
{
    private static byte[] FakePng(int w, int h)
    {
        var b = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 0, 0, 0, 0, 0, 8, 6, 0, 0, 0, 0, 0, 0, 0 };
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private static string Package()
    {
        var root = Path.Combine(TestData.TempDir(), "PLANS_HOTEL");
        Directory.CreateDirectory(Path.Combine(root, "L01"));
        File.WriteAllText(Path.Combine(root, "manifest.json"), """{ "building": "HOTEL", "revision": "R5", "levels": [ { "level": "L01", "name": "Level 01", "folder": "L01" } ] }""");
        File.WriteAllBytes(Path.Combine(root, "L01", "background.png"), FakePng(2000, 1000));
        File.WriteAllText(Path.Combine(root, "L01", "shapes.json"), """
            { "image": { "width": 2000, "height": 1000 },
              "shapes": [
                { "location": "H2-L1-101", "kind": "GUESTROOM", "points": [[100,100],[300,100],[300,300],[100,300]] },
                { "location": "H2-L1 PUBLIC", "kind": "PUBLIC", "polygons": [ [[1000,0],[2000,0],[2000,500]], [[1000,600],[1500,600],[1500,900]] ] },
                { "location": "LOBBY", "kind": "PUBLIC", "parent": "H2-L1 PUBLIC", "points": [{"x":0.5,"y":0.5},{"x":0.6,"y":0.5},{"x":0.6,"y":0.6}] }
              ] }
            """);
        return root;
    }

    [Fact]
    public void Package_is_read_normalised_and_stored_per_revision()
    {
        var r = PlanPackage.Read(Package());
        Assert.Equal("HOTEL", r.Building);
        var l = Assert.Single(r.Levels);
        Assert.Equal((2000, 1000), (l.Width, l.Height));
        Assert.Equal(3, l.Shapes.Count);
        var room = l.Shapes[0];
        Assert.Equal("L01@R5", room.Plan);
        Assert.Equal("0.05,0.1 0.15,0.1 0.15,0.3 0.05,0.3", room.Polygons);
        Assert.Equal(2, l.Shapes[1].Polygons.Split('|').Length);
        Assert.Equal("H2-L1 PUBLIC", l.Shapes[2].Description);

        var db = TestData.NewDb();
        Assert.Equal((1, 3), PlanPackage.Commit(r, db));
        PlanPackage.Commit(r, db);   // the same revision again replaces it
        Assert.Single(db.All<PlanImage>());
        var code = PlanPackage.AddRevision(db, "HOTEL", "L01", "R6", FakePng(2100, 1000), "Level 01 rev 6", "L01@R5");
        Assert.Equal("L01@R6", code);
        Assert.Equal(2, db.All<PlanImage>().Count);
        Assert.Equal(3, db.All<RoomShape>().Count(s => s.Plan == "L01@R6"));
        PlanPackage.Align(db, "HOTEL", "L01@R6", 2, 1, 0.1, 0);
        var moved = db.All<RoomShape>().First(s => s.Plan == "L01@R6" && s.Room == "H2-L1-101");
        Assert.StartsWith("-0.3,0.1 ", moved.Polygons);   // (0.05-0.5)*2+0.5+0.1
        Assert.Equal(("L01", "R6"), PlanPackage.Split(code));
    }

    [Fact]
    public void Sub_colours_are_stable_and_editable()
    {
        var a = SubPalette.Assign(new[] { "ROOTS", "ELAF", "ABRAG" });
        Assert.Equal(SubPalette.Colours[0], a["ABRAG"]);
        Assert.Equal(SubPalette.Colours[2], a["ROOTS"]);
        var b = SubPalette.Assign(new[] { "ROOTS", "ELAF", "ABRAG" }, new Dictionary<string, string> { ["ELAF"] = "#123456" });
        Assert.Equal("#123456", b["ELAF"]);
        Assert.Equal(SubPalette.Colours[1], SubPalette.Next(SubPalette.Colours[0]));
    }
}