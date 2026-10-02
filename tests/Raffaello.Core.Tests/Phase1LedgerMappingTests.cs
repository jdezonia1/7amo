using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;
using Raffaello.Core.Statements;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Tests;

public class LedgerRuleTests
{
    private static RoomQty Q(string room, string stage, string item, double qty) => new() { Room = room, Stage = stage, Item = item, Qty = qty };
    private static ClaimLine C(string sub, string room, string stage, string item, double qty, double site = 1, double wir = 1) =>
        new() { Subcontractor = sub, Room = room, Stage = stage, Item = item, Qty = qty, SitePct = site, WirPct = wir, InvoiceNo = 1 };

    [Fact]
    public void Remaining_IsCapMinusAllSubcontractors_StagesSeparate()
    {
        var project = new[] { Q("P1", "1ST FIX", "POWER", 40), Q("P1", "2ND FIX", "POWER", 40) };
        var claims = new[] { C("A", "P1", "1ST FIX", "POWER", 25), C("B", "P1", "1ST FIX", "POWER", 10), C("A", "P1", "2ND FIX", "POWER", 5) };
        var b = LedgerRules.Balances(project, claims);
        var first = b[LedgerKeys.Key("P1", "1ST FIX", "POWER")];
        Assert.Equal(5, first.Remaining);
        Assert.Equal(25, first.BySubcontractor["A"]);
        Assert.Equal(35, b[LedgerKeys.Key("P1", "2ND FIX", "POWER")].Remaining);  // stages never summed
    }

    [Fact]
    public void OverRemaining_BlockedWithoutReason_PostedOverWithReason()
    {
        var bal = LedgerRules.Balance(new[] { Q("P1", "1ST FIX", "LIGHT", 50) }, new[] { C("A", "P1", "1ST FIX", "LIGHT", 45) }, "P1", "1ST FIX", "LIGHT");
        Assert.Equal(ClaimDecision.Accepted, LedgerRules.Check(bal, 5, null).Decision);
        var blocked = LedgerRules.Check(bal, 8, null);
        Assert.False(blocked.CanPost);
        Assert.Contains("exceeds remaining 5", blocked.Message);
        var over = LedgerRules.Check(bal, 8, "site recount by engineer");
        Assert.True(over.CanPost);
        Assert.True(over.IsOver);
        Assert.False(LedgerRules.Check(LedgerRules.Balance(Array.Empty<RoomQty>(), Array.Empty<ClaimLine>(), "X", "1ST FIX", "GAS"), 1, null).CanPost);
    }

    [Fact]
    public void Reversal_IsANegativeAppendedLine()
    {
        var original = C("A", "P1", "1ST FIX", "POWER", 12);
        original.Id = 7;
        var rev = LedgerRules.Reversal(original, "typed twice");
        Assert.Equal(-12, rev.Qty);
        Assert.Contains("#7", rev.Notes);
        Assert.Equal(0, LedgerRules.Balances(new[] { Q("P1", "1ST FIX", "POWER", 40) }, new[] { original, rev }).Values.Single().Claimed);
    }

    [Fact]
    public void HeightCheck_PendingHeld_AcceptedSplit_Partly()
    {
        var line = C("A", "P1", "1ST FIX", "POWER", 10);
        line.QtyAbove45 = 4;
        line.HeightStatus = CheckStatus.Pending;
        Assert.True(Invoiceable.Of(line).Held);
        HeightCheck.Decide(line, CheckStatus.Partly, 3, "QS", "checked on site");
        var q = Invoiceable.Of(line);
        Assert.False(q.Held);
        Assert.Equal((7.0, 3.0), (q.LowQty, q.HighQty));
        HeightCheck.Decide(line, CheckStatus.Rejected, null, "QS", "");
        Assert.Equal((10.0, 0.0), (Invoiceable.Of(line).LowQty, Invoiceable.Of(line).HighQty));
        HeightCheck.Decide(line, CheckStatus.Partly, 9, "QS", "");
        Assert.Equal(CheckStatus.Accepted, line.HeightStatus);   // partly with everything = accepted
        Assert.Equal(4, line.QtyAbove45Accepted);
    }

    [Theory]
    [InlineData(10, 1.0)]
    [InlineData(15, 1.0)]
    [InlineData(20, 1.3)]
    [InlineData(30, 2.0)]
    [InlineData(37, 2.5)]
    public void LengthRule_ProportionalWithMinimumOne(double length, double perPoint)
    {
        Assert.Equal(perPoint, LengthCheck.PerPoint(length));
    }

    [Fact]
    public void LengthCheck_QuickAndGroups_OnlyRevisedInvoiced_PlanCounts()
    {
        Assert.Equal(133.3, LengthCheck.FromTotal(40, 2000));
        Assert.Equal(40, LengthCheck.FromTotal(40, 300));               // never below plan
        Assert.Equal(10 * 1.3 + 5 * 2.3, LengthCheck.FromGroups("10x20; 5×35m"), 6);   // per-point rounding
        var line = C("A", "P1", "2ND FIX", "DATA", 40, site: 1, wir: 0.5);
        line.LengthApplies = true; line.LengthClaimedQty = 150; line.LengthStatus = CheckStatus.Pending;
        Assert.True(Invoiceable.Of(line).Held);
        line.RouteLengthTotal = 2000;
        LengthCheck.Recalculate(line);
        Assert.Equal(133.3, line.LengthRevisedQty);
        Assert.Contains("plan qty 40", line.LengthNote);
        line.LengthStatus = CheckStatus.Revised;
        Assert.Equal(133.3 * 0.5, Invoiceable.Of(line).Total, 6);
        line.LengthRevisedOverride = 120;
        Assert.Equal(60, Invoiceable.Of(line).Total, 6);
        Assert.Equal(30, LengthCheck.RejectedExtra(line), 6);
        line.LengthStatus = CheckStatus.Rejected;
        Assert.Equal(20, Invoiceable.Of(line).Total, 6);
        Assert.Equal(40, LedgerRules.Balances(new[] { Q("P1", "2ND FIX", "DATA", 40) }, new[] { line }).Values.Single().Claimed); // plan qty only
    }

    [Fact]
    public void Invoiceable_IsQtyTimesSiteTimesWir()
    {
        Assert.Equal(17.64, Invoiceable.Of(C("A", "P4", "2ND FIX", "LIGHT", 63, 0.4, 0.7)).Total, 6);
    }
}

public class MappingTests
{
    private static MappingContext Ctx(IEnumerable<MappingRule>? rules = null)
    {
        var items = Phase1Fixtures.ContractItems.Select(i => Phase1Fixtures.Item(i.No, i.Desc, i.Rate, i.Unit)).ToList();
        var links = Phase1Fixtures.ContractItems.SelectMany(i => i.Codes.Select((c, n) => new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = i.No, BoqCode = c, Order = n })).ToList();
        links.Add(new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = "308", BoqCode = "B6-01-01-00-6-26-AL-2" });
        var boq = Phase1Fixtures.BoqDescriptions.Select(kv => new BoqItem { ItemCode = kv.Key, Description = kv.Value });
        return new MappingContext(Phase1Fixtures.Contract, items, links, boq, rules ?? Array.Empty<MappingRule>());
    }

    private static ClaimLine L(string stage, string item, double qty, string area = AreaTypes.Apartment) =>
        new() { Subcontractor = "SUBA", InvoiceNo = 1, Room = "P9-101", Stage = stage, Item = item, Qty = qty, AreaType = area };

    [Theory]
    [InlineData("1ST FIX", "POWER", "1", "small power points")]
    [InlineData("1ST FIX", "LIGHT", "1", "lighting points, to apartment - number")]
    [InlineData("1ST FIX", "EMERGENCY LIGHT", "1", "to emergency lighting point")]
    [InlineData("CEILING", "LIGHT", "3", "lighting points, to apartment - number")]
    [InlineData("2ND FIX", "POWER", "11", "small power points")]
    [InlineData("1ST FIX", "DATA", "183", "final sub-circuit for IT / Telecoms devices")]
    [InlineData("1ST FIX", "AV", "183", "allowance for IPTV system")]
    [InlineData("1ST FIX", "TERRACE LIGHT", "1", "lighting points, to balcony")]
    public void Maps_StageSystemArea_ToItemAndBoqRow(string stage, string item, string expectedItem, string expectedBoq)
    {
        var r = new MappingEngine().Map(new[] { L(stage, item, 10) }, Ctx());
        var p = Assert.Single(r.Parts);
        Assert.Equal(expectedItem, p.Item?.ItemNo);
        Assert.Equal(expectedBoq, p.BoqDescription);
        Assert.False(Confidence.NeedsConfirmation(p.Confidence), p.Explanation);
        Assert.Contains("x site", p.Explanation);
    }

    [Theory]
    [InlineData(AreaTypes.Boh, "lighting points, to BOH")]
    [InlineData(AreaTypes.Foh, "lighting points, to FOH - number")]
    [InlineData(AreaTypes.Balcony, "lighting points, to balcony")]
    public void LightingRow_FollowsRoomAreaType(string area, string expected)
    {
        var p = new MappingEngine().Map(new[] { L("1ST FIX", "LIGHT", 5, area) }, Ctx()).Parts.Single();
        Assert.Equal(expected, p.BoqDescription);
    }

    [Fact]
    public void AcceptedHeight_GoesToTheHighItem()
    {
        var line = L("1ST FIX", "POWER", 10);
        line.QtyAbove45 = 4; HeightCheck.Decide(line, CheckStatus.Accepted, null, "QS", "");
        var parts = new MappingEngine().Map(new[] { line }, Ctx()).Parts;
        Assert.Equal(6, parts.Single(p => p.Item!.ItemNo == "1").Qty);
        Assert.Equal(4, parts.Single(p => p.Item!.ItemNo == "2").Qty);
    }

    [Fact]
    public void Unmapped_AndPanelsAndLearnedOverrides()
    {
        var r = new MappingEngine().Map(new[] { L("2ND FIX", "GRMS", 3), L("DB PANELS", "PANEL 42", 2), L("1ST FIX", "GAS", 5) }, Ctx());
        Assert.Equal(Confidence.Unmapped, r.Parts.Single(p => p.Line.Item == "GRMS").Confidence);
        Assert.Equal("308", r.Parts.Single(p => p.Line.Item == "PANEL 42").Item?.ItemNo);
        Assert.True(r.CoveragePct < 1);
        var rules = new[]
        {
            MappingEngine.Learn("ITEM", Phase1Fixtures.Contract, DefaultItemResolver.RuleKey("1ST FIX", "DATA", HeightBands.Low), "182"),
            MappingEngine.Learn("BOQ", Phase1Fixtures.Contract, DefaultBoqResolver.RuleKey("1", "LIGHT", AreaTypes.Apartment), "B6-01-01-00-6-26-AS-6"),
        };
        var learned = new MappingEngine().Map(new[] { L("1ST FIX", "DATA", 1), L("1ST FIX", "LIGHT", 1) }, Ctx(rules)).Parts;
        Assert.Equal("182", learned.Single(p => p.Line.Item == "DATA").Item?.ItemNo);
        Assert.Equal("lighting points, to BOH", learned.Single(p => p.Line.Item == "LIGHT").BoqDescription);
        Assert.Contains("learned rule", learned.Single(p => p.Line.Item == "DATA").Explanation);
        Assert.Contains("learned rule", learned.Single(p => p.Line.Item == "LIGHT").Explanation);
        Assert.All(learned, p => Assert.False(Confidence.NeedsConfirmation(p.Confidence)));
    }

    [Fact]
    public void PendingChecks_AreHeldOutOfMapping()
    {
        var line = L("1ST FIX", "POWER", 10);
        line.QtyAbove45 = 2; line.HeightStatus = CheckStatus.Pending;
        var r = new MappingEngine().Map(new[] { line }, Ctx());
        Assert.Empty(r.Parts);
        Assert.Single(r.Held);
    }

    [Theory]
    [InlineData("50 MM", "TRAY 50-300")]
    [InlineData("300 MM", "TRAY 50-300")]
    [InlineData("450 MM", "TRAY 400-800")]
    [InlineData("900 MM", "TRAY 900+")]
    public void TrayBands(string item, string band) => Assert.Equal(band, DefaultItemResolver.TrayBand(item));
}

public class InvoiceTests
{
    private static (IProjectStore store, ProjectSnapshot snap) Setup()
    {
        var store = TestData.NewDb();
        TrackerImporter.Commit(TrackerImporter.Read(Phase1Fixtures.Tracker()), store);
        ContractLinkImporter.Commit(ContractLinkImporter.Read(Phase1Fixtures.LinkWorkbook(), Phase1Fixtures.Contract, "SUBA"), store);
        var file = Phase1Fixtures.InvoiceWorkbook();
        EPromiseImporter.Commit(EPromiseImporter.Read(file), store);
        InvoiceTemplateImporter.Commit(InvoiceTemplateImporter.Read(file, Phase1Fixtures.Contract), store, Phase1Fixtures.Contract);
        return (store, ProjectSnapshot.Load(store));
    }

    [Fact]
    public void Build_CumPrevCurr_FromLedgerAndApprovedPrevious()
    {
        var (store, s) = Setup();
        var inv1 = InvoiceBuilder.Build(s, Phase1Fixtures.Contract, "SUBA", 1);
        var power = inv1.Lines.Single(l => l.ItemNo == "1" && l.BoqCode == "B6-01-01-00-6-26-V-5");
        Assert.Equal(30, power.CumQty);          // SUBA INV 1 1ST FIX POWER 30 x 1 x 1
        var light = inv1.Lines.Single(l => l.ItemNo == "1" && l.BoqCode == "B6-01-01-00-6-26-AW-6");
        Assert.Equal(40, light.CumQty);          // 50 x 0.8 WIR, apartment row
        Assert.Contains("P9-101 1ST FIX LIGHT", light.Explanation);
        Assert.Equal(0, inv1.Totals.PrevGross);
        Assert.Equal((30 + 40) * 55 * 0.9, inv1.Totals.CurrGross, 6);

        var saved = InvoiceWorkflow.SaveDraft(store, inv1);
        InvoiceWorkflow.Submit(store, saved, "WF-0001");
        InvoiceWorkflow.Approve(store, saved);
        Assert.Throws<InvalidOperationException>(() => InvoiceWorkflow.SaveDraft(store, inv1));   // approved = locked

        var inv2 = InvoiceBuilder.Build(ProjectSnapshot.Load(store), Phase1Fixtures.Contract, "SUBA", 2);
        var p2 = inv2.Lines.Single(l => l.ItemNo == "1" && l.BoqCode == "B6-01-01-00-6-26-V-5");
        Assert.Equal((30.0, 30.0, 0.0), (p2.PrevQty, p2.CumQty, p2.CurrQty));
        var wiring = inv2.Lines.Single(l => l.ItemNo == "11" && l.BoqCode == "B6-01-01-00-6-26-V-5");
        Assert.Equal((0.0, 15.0), (wiring.PrevQty, wiring.CurrQty));   // 60 x 0.5 x 0.5
        Assert.Equal(15 * 28 * 0.9 + 20 * 55 * 0.9, inv2.Totals.CurrGross, 6);  // + BS1 LIGHT 20 on the BOH row
        Assert.Equal(-0.1 * inv2.Totals.CurrGross, inv2.Totals.CurrRetention, 6);
        Assert.Equal(inv2.Totals.NetCurr * 0.15, inv2.Totals.VatCurr, 6);
    }

    [Fact]
    public void Revisions_DiffAndNumbering()
    {
        var (store, s) = Setup();
        var r0 = InvoiceBuilder.Build(s, Phase1Fixtures.Contract, "SUBA", 1);
        var saved = InvoiceWorkflow.SaveDraft(store, r0);
        InvoiceWorkflow.Submit(store, saved, "WF-1");
        InvoiceWorkflow.Reject(store, saved, "lighting on wrong BOQ row");
        var snap = ProjectSnapshot.Load(store);
        Assert.Equal(1, InvoiceWorkflow.NextRevision(snap.SubInvoices, Phase1Fixtures.Contract, "SUBA", 1));
        Assert.Equal("lighting on wrong BOQ row", snap.SubInvoices.Single().RejectionReason);
        // learned BOQ rule moves the lighting to BOH in rev 1
        store.Insert(MappingEngine.Learn("BOQ", Phase1Fixtures.Contract, DefaultBoqResolver.RuleKey("1", "LIGHT", AreaTypes.Apartment), "B6-01-01-00-6-26-AS-6"));
        var r1 = InvoiceBuilder.Build(ProjectSnapshot.Load(store), Phase1Fixtures.Contract, "SUBA", 1, revision: 1);
        var diff = InvoiceBuilder.Diff(r0.Lines, r1.Lines);
        Assert.Equal(2, diff.Count);
        Assert.Contains(diff, d => d.BoqCode == "B6-01-01-00-6-26-AW-6" && d.QtyChange == -40);
        Assert.Contains(diff, d => d.BoqCode == "B6-01-01-00-6-26-AS-6" && d.QtyChange == 40);
    }

    [Fact]
    public void ExcelExport_TemplateLayoutWithCorrectFooterAndBackup()
    {
        var (_, s) = Setup();
        var b = InvoiceBuilder.Build(s, Phase1Fixtures.Contract, "SUBA", 2);
        var path = Path.Combine(TestData.TempDir(), "inv.xlsx");
        InvoiceExcelExporter.Export(path, b, new InvoiceHeaderInfo { SignatureNames = new[] { "QS" } });
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet(1);
        var last = InvoiceExcelExporter.FirstLineRow + b.Lines.Count - 1;
        var footer = last + 1;
        Assert.Equal(" Subcontract Value", ws.Cell(footer, 3).GetString());
        Assert.Equal($"SUM(K14:K{last})", ws.Cell(footer, 11).FormulaA1);
        Assert.Equal($"SUM(T14:T{last})", ws.Cell(footer, 20).FormulaA1);
        Assert.Equal(0.1, ws.Cell("U6").GetDouble());
        Assert.Equal($"S{footer + 3}*-$U$6", ws.Cell(footer + 4, 19).FormulaA1);
        Assert.Equal("FFA6A6A6", ws.Cell("C11").Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.Equal("SUBA", ws.Cell("M2").GetString());
        Assert.True(wb.Worksheets.Contains("QTY BACKUP"));
        Assert.True(wb.Worksheet("QTY BACKUP").LastRowUsed()!.RowNumber() > 1);
    }

    [Fact]
    public void Pdf_PrintsOnlyRowsWithQuantity()
    {
        var (_, s) = Setup();
        var b = InvoiceBuilder.Build(s, Phase1Fixtures.Contract, "SUBA", 2);
        var rows = InvoicePdfExporter.FilteredRows(b.Lines);
        Assert.All(rows.Where(r => r.Kind == "ITEM"), r => Assert.True(r.CumQty != 0 || r.CurrQty != 0));
        Assert.DoesNotContain(rows, r => r.Kind == "SECTION" && r.Description == "Panels");   // no panel qty
        var path = Path.Combine(TestData.TempDir(), "inv.pdf");
        InvoicePdfExporter.Export(path, b, a3: false);
        Assert.True(new FileInfo(path).Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
    }
}

public class SiteStatementTests
{
    [Fact]
    public void Generate_FillIn_Import_DuplicateDetected()
    {
        var store = TestData.NewDb();
        TrackerImporter.Commit(TrackerImporter.Read(Phase1Fixtures.Tracker()), store);
        var s = ProjectSnapshot.Load(store);
        var rooms = SiteStatementService.ScopeRooms(s, "SUBA", Buildings.Branded);
        Assert.Equal(new[] { "P9-BS1", "P9-101", "P9-102" }, rooms.Select(r => r.Code).ToArray());
        var path = Path.Combine(TestData.TempDir(), "statement.xlsx");
        SiteStatementService.Generate(path, "SUBA", "ST-001", rooms, stages: new[] { "1ST FIX", "2ND FIX" }, systems: new[] { "POWER", "LIGHT", "DATA" },
            balances: LedgerRules.Balances(s.RoomQtys, s.Claims));
        using (var wb = new XLWorkbook(path))
        {
            var ws = wb.Worksheet("STATEMENT");
            Assert.True(ws.IsProtected);
            Assert.True(ws.Column(1).IsHidden);
            var row = ws.RowsUsed().First(r => r.Cell(1).GetString() == "P9-102|1ST FIX");
            Assert.False(row.Cell(6).Style.Protection.Locked);
            row.Cell(6).Value = 5;           // POWER
            row.Cell(7).Value = 100;         // LIGHT (cap 70 -> over)
            row.Cell(9).Value = 0.5;         // SITE %
            row.Cell(13).Value = 4;          // >4.5 M QTY
            wb.Save();
        }
        var res = SiteStatementService.Read(path, s, invoiceNo: 3);
        Assert.Equal(("SUBA", "ST-001"), (res.Subcontractor, res.StatementNo));
        Assert.Equal(2, res.Claims.Count);
        var power = res.Claims.Single(c => c.Item == "POWER");
        Assert.Equal((0.5, 3, CheckStatus.Pending, 4.0), (power.SitePct, power.InvoiceNo, power.HeightStatus, power.QtyAbove45));
        Assert.Equal(1, res.Blocked);
        Assert.Equal(1, SiteStatementService.Commit(res, store, path));     // over line skipped without a reason
        var again = SiteStatementService.Read(path, ProjectSnapshot.Load(store), invoiceNo: 3);
        Assert.True(again.IsDuplicate);
        Assert.Throws<InvalidOperationException>(() => SiteStatementService.Commit(again, store, path));
    }
}
