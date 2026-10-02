using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;
using Raffaello.Core.Tracker;
using Xunit;

namespace Raffaello.Core.Tests;

/// <summary>Mohamed's answers to the phase-1 questions: panels + site %, wall default, gas meters, DATA RACK, cumulative invoices.</summary>
public class FollowUpTests
{
    private static readonly (string No, string Desc)[] Metering =
    {
        ("267", "installation, and handover of PVC 1st Fix outlet for Metering (ceiling type). Price includes wall chasing works for height less than 4.5 m."),
        ("268", "installation, and handover of PVC 1st Fix outlet for Metering (wall type). Price includes wall chasing works for height less than 4.5 m."),
        ("271", "installation, and handover of EMT 1st Fix outlet for Metering. Price includes wall chasing works for height less than 4.5 m."),
    };

    private static MappingContext Ctx(MappingOptions? options = null)
    {
        var items = Phase1Fixtures.ContractItems.Select(i => Phase1Fixtures.Item(i.No, i.Desc, i.Rate, i.Unit))
            .Concat(Metering.Select(m => Phase1Fixtures.Item(m.No, m.Desc))).ToList();
        var links = Phase1Fixtures.ContractItems.SelectMany(i => i.Codes.Select((c, n) => new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = i.No, BoqCode = c, Order = n })).ToList();
        links.Add(new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = "308", BoqCode = "B6-01-01-00-6-26-AL-2" });
        links.AddRange(Metering.Select(m => new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = m.No, BoqCode = "B6-01-01-00-6-22-X-18", BoqDescription = "BMS system" }));
        var boq = Phase1Fixtures.BoqDescriptions.Select(kv => new BoqItem { ItemCode = kv.Key, Description = kv.Value });
        return new MappingContext(Phase1Fixtures.Contract, items, links, boq, Array.Empty<MappingRule>()) { Options = options ?? MappingOptions.Default };
    }

    private static ClaimLine L(string stage, string item, double qty, double site = 1, int inv = 1, string room = "P9-101", string sub = "SUBA") =>
        new() { Subcontractor = sub, InvoiceNo = inv, Room = room, Stage = stage, Item = item, Qty = qty, SitePct = site, AreaType = AreaTypes.Apartment, SourceKey = $"{sub}|{room}|{stage}|{item}|{inv}|{qty}" };

    [Fact]
    public void Panels_HonourSitePercent()
    {
        var p = new MappingEngine().Map(new[] { L("DB PANELS", "PANEL 42", 2, site: 0.7) }, Ctx()).Parts.Single();
        Assert.Equal("308", p.Item?.ItemNo);
        Assert.Equal(1.4, p.Qty, 6);
    }

    [Theory]
    [InlineData("DATA", "183")]
    [InlineData("AV", "183")]
    public void FirstFixData_DefaultsToTheWallItem_AndIsOneSettingAway(string item, string wall)
    {
        Assert.Equal(Mounts.Wall, MappingOptions.Default.Data1stFixMount);
        Assert.Equal(Mounts.Wall, MappingOptions.Default.Grms1stFixMount);
        var p = new MappingEngine().Map(new[] { L("1ST FIX", item, 3) }, Ctx()).Parts.Single();
        Assert.Equal(wall, p.Item?.ItemNo);
        Assert.False(Confidence.NeedsConfirmation(p.Confidence));
        var ceiling = new MappingEngine().Map(new[] { L("1ST FIX", "DATA", 3) }, Ctx(new MappingOptions { Data1stFixMount = Mounts.Ceiling })).Parts.Single();
        Assert.Equal("182", ceiling.Item?.ItemNo);
    }

    [Fact]
    public void Parser_CeilingType_IsNotConfusedByWallChasingWorks()
    {
        Assert.Equal(Mounts.Ceiling, ContractAttributeParser.Parse(Metering[0].Desc, "No.").Mount);
        Assert.Equal(Mounts.Wall, ContractAttributeParser.Parse(Metering[1].Desc, "No.").Mount);
    }

    [Theory]
    [InlineData("1ST FIX", "268")]
    [InlineData("EMT", "271")]
    public void Gas_MapsToMeteringItems(string stage, string expected)
    {
        var p = new MappingEngine().Map(new[] { L(stage, "GAS", 5) }, Ctx()).Parts.Single();
        Assert.Equal("METERING", p.System);
        Assert.Equal(expected, p.Item?.ItemNo);
        Assert.Equal("B6-01-01-00-6-22-X-18", p.BoqCode);
    }

    [Fact]
    public void Gas_PrefersAnItemThatSaysGas()
    {
        var ctx = Ctx();
        var gas = Phase1Fixtures.Item("269", "installation, and handover of PVC 1st Fix outlet for gas Metering (wall type), height less than 4.5 m.");
        var all = ctx.Items.Append(gas).ToList();
        var links = ctx.LinksByItem.Values.SelectMany(x => x).Append(new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = "269", BoqCode = "B6-01-01-00-6-22-X-18" });
        var ctx2 = new MappingContext(Phase1Fixtures.Contract, all, links, Array.Empty<BoqItem>(), Array.Empty<MappingRule>());
        Assert.Equal("269", new MappingEngine().Map(new[] { L("1ST FIX", "GAS", 1) }, ctx2).Parts.Single().Item?.ItemNo);
    }

    [Fact]
    public void DataRack_IsALengthExtraOnData2ndFix_NotAgainstTheCap()
    {
        var rack = L("2ND FIX", "DATA RACK", 293, inv: 2, room: "ALL");
        rack.SitePct = 0.9; rack.WirPct = 0.8;
        Assert.True(LengthExtras.ConvertDataRack(rack, alreadyInvoiced: true));
        Assert.Equal("DATA", rack.Item);
        Assert.Equal(0, rack.Qty);
        Assert.Equal(293, rack.LengthClaimedQty);
        Assert.Equal(CheckStatus.Accepted, rack.LengthStatus);
        Assert.False(LengthCheck.IsPending(rack));
        // never counts against PROJECT QTY
        var data = L("2ND FIX", "DATA", 10, room: "ALL");
        var bal = LedgerRules.Balances(new[] { new RoomQty { Room = "ALL", Stage = "2ND FIX", Item = "DATA", Qty = 10 } }, new[] { data, rack }).Values.Single();
        Assert.Equal(10, bal.Claimed);
        Assert.False(bal.IsOver);
        // invoiced at the accepted claim on the same row as DATA 2nd fix
        Assert.Equal(293 * 0.9 * 0.8, Invoiceable.Of(rack).Total, 6);
        var parts = new MappingEngine().Map(new[] { data, rack }, Ctx()).Parts;
        Assert.Equal(parts[0].RowKey, parts[1].RowKey);
        // a new DATA RACK entry (not yet invoiced) waits in the LENGTH check
        var fresh = L("2ND FIX", "DATA RACK", 12);
        LengthExtras.ConvertDataRack(fresh, alreadyInvoiced: false);
        Assert.True(LengthCheck.IsPending(fresh));
    }

    [Fact]
    public void CumulativeText_IsDetected()
    {
        Assert.True(TrackerImporter.IsCumulativeText("SUBX INV-9 (cumulative) - plot 9 Ground Floor total 100"));
        Assert.True(TrackerImporter.IsCumulativeText("INV 9 CUM"));
        Assert.False(TrackerImporter.IsCumulativeText("accumulator room"));
    }

    [Fact]
    public void CumulativeInvoice_ReplacesEarlierLinesOfTheSameSubAndKey()
    {
        var early = L("1ST FIX", "POWER", 6, inv: 2, sub: "CP");
        var other = L("1ST FIX", "POWER", 4, inv: 1, sub: "SUBA");
        var cum = L("1ST FIX", "POWER", 9, inv: 3, sub: "CP"); cum.IsCumulative = true;
        var later = L("1ST FIX", "POWER", 1, inv: 4, sub: "CP");
        var eff = LedgerRules.Effective(new[] { early, other, cum, later }).ToList();
        Assert.DoesNotContain(early, eff);
        Assert.Equal(14, eff.Sum(c => c.Qty));
        Assert.Equal(14, LedgerRules.Balances(Array.Empty<RoomQty>(), new[] { early, other, cum, later }).Values.Single().Claimed);
        Assert.Contains("1-3 CUM", CumulativeSplit.InvoiceLabel(cum));
        Assert.Contains("awaiting invoice files", CumulativeSplit.Banner(new[] { cum }));
    }

    private static List<ClaimLine> CumulativeBlock()
    {
        var lines = new List<ClaimLine>();
        long id = 1;
        foreach (var (room, q) in new[] { ("P2-1", 38.0), ("P2-2", 43.0), ("P2-3", 43.0), ("P2-4", 7.0) })
        {
            var c = L("1ST FIX", "POWER", q, inv: 3, room: room, sub: "CP"); c.IsCumulative = true; c.Id = id++; lines.Add(c);
        }
        var l = L("1ST FIX", "LIGHT", 10.5, inv: 3, room: "P2-1", sub: "CP"); l.IsCumulative = true; l.Id = id; lines.Add(l);
        return lines;
    }

    [Fact]
    public void Split_ReconcilesExactlyPerLineAndPerInvoice()
    {
        var block = CumulativeBlock();
        var cums = new[]
        {
            new InvoiceCum(1, new Dictionary<string, double> { ["1|P"] = 31, ["2|L"] = 2.5 }),
            new InvoiceCum(2, new Dictionary<string, double> { ["1|P"] = 77, ["2|L"] = 2.5 }),
            new InvoiceCum(3, new Dictionary<string, double> { ["1|P"] = 131, ["2|L"] = 10.5 }),
        };
        var r = CumulativeSplit.Split(block, "CP", cums, c => c.Item == "POWER" ? "1|P" : "2|L");
        Assert.Empty(r.NotSplit);
        Assert.Equal(5, r.Replaced.Count);
        foreach (var orig in block)
            Assert.Equal(orig.Qty, r.NewLines.Where(n => n.SourceKey.Contains($"|{orig.Id}|INV")).Sum(n => n.Qty), 9);
        var power = r.NewLines.Where(n => n.Item == "POWER").GroupBy(n => n.InvoiceNo).ToDictionary(g => g.Key, g => g.Sum(n => n.Qty));
        Assert.Equal(31, power[1], 9); Assert.Equal(46, power[2], 9); Assert.Equal(54, power[3], 9);
        Assert.All(r.NewLines.Where(n => n.Item == "POWER"), n => Assert.Equal(Math.Round(n.Qty), n.Qty));   // whole points stay whole
        var light = r.NewLines.Where(n => n.Item == "LIGHT").GroupBy(n => n.InvoiceNo).ToDictionary(g => g.Key, g => g.Sum(n => n.Qty));
        Assert.Equal(2.5, light[1], 9); Assert.False(light.ContainsKey(2)); Assert.Equal(8, light[3], 9);
        Assert.All(r.Rows, row => Assert.Equal("OK", row.Status));

        // after the split the cumulative block counts as the separate invoices, never on top of them
        foreach (var c in r.Replaced) c.ReplacedBySplit = true;
        var ledger = block.Concat(r.NewLines).ToList();
        Assert.Equal(141.5, LedgerRules.Effective(ledger).Sum(c => c.Qty), 9);
        Assert.Equal(31 + 2.5, LedgerRules.Effective(ledger.Where(c => c.InvoiceNo <= 1)).Sum(c => c.Qty), 9);
        Assert.Empty(CumulativeSplit.Pending(ledger));
    }

    [Fact]
    public void Split_IsDeterministic_AndFlagsMissingFilesAndMismatches()
    {
        var cums = new[] { new InvoiceCum(3, new Dictionary<string, double> { ["1|P"] = 120 }), new InvoiceCum(1, new Dictionary<string, double> { ["1|P"] = 40 }) };
        var a = CumulativeSplit.Split(CumulativeBlock(), "CP", cums, c => c.Item == "POWER" ? "1|P" : null);
        var b = CumulativeSplit.Split(CumulativeBlock(), "CP", cums, c => c.Item == "POWER" ? "1|P" : null);
        Assert.Equal(a.NewLines.Select(n => (n.Room, n.InvoiceNo, n.Qty)), b.NewLines.Select(n => (n.Room, n.InvoiceNo, n.Qty)));
        Assert.Contains(a.Issues, i => i.Message.Contains("INV 2 file missing"));
        Assert.Contains(a.Issues, i => i.Message.Contains("ledger lines hold 131"));
        Assert.Single(a.NotSplit);   // the LIGHT line has no row
        Assert.Equal(131, a.NewLines.Sum(n => n.Qty), 9);
    }

    [Fact]
    public void Split_RefusesWithoutTheCumulativeInvoiceFile()
    {
        var r = CumulativeSplit.Split(CumulativeBlock(), "CP", new[] { new InvoiceCum(1, new Dictionary<string, double> { ["1|P"] = 1 }) }, _ => "1|P");
        Assert.Empty(r.NewLines);
        Assert.Contains(r.Issues, i => i.Message.Contains("INV 3"));
    }

    [Fact]
    public void PastInvoiceFile_ReadsNumberAndExecutedQuantities()
    {
        var path = Path.Combine(TestData.TempDir(), "CP_INV_02.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("CP INV 2");
            ws.Cell("L2").Value = "Vendor Name "; ws.Cell("M2").Value = "CP";
            ws.Cell("L6").Value = "Subcon.Inv.No"; ws.Cell("M6").Value = "02";
            ws.Cell("B15").Value = 1; ws.Cell("C15").Value = "B6-01-01-00-6-26-V-5"; ws.Cell("F15").Value = "outlet"; ws.Cell("H15").Value = 100; ws.Cell("I15").Value = 55; ws.Cell("J15").Value = 0.9;
            ws.Cell("O15").Value = 31; ws.Cell("P15").Value = 46; ws.Cell("Q15").Value = 77;
            ws.Cell("B16").Value = 1; ws.Cell("C16").Value = "B6-01-01-00-6-26-V-5"; ws.Cell("F16").Value = "outlet"; ws.Cell("Q16").Value = 3; ws.Cell("P16").Value = 1;
            ws.Cell("C17").Value = " Subcontract Value";
            wb.SaveAs(path);
        }
        var f = PastInvoiceImporter.Read(path, Phase1Fixtures.Contract);
        Assert.Equal(2, f.InvoiceNo);
        Assert.Equal("header M6", f.InvoiceNoSource);
        Assert.Equal(80, f.CumByRow()["1|B6-01-01-00-6-26-V-5"], 9);
        Assert.Contains(f.Issues, i => i.Message.Contains("<> cum"));   // row 16: 0 + 1 <> 3
    }
}
