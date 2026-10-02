using System.Xml.Linq;
using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Tests;

public class XlsxStreamReaderTests
{
    [Fact]
    public void ReadsSharedStringsNumbersFormulasAndSkipsEmptyRows()
    {
        var path = Path.Combine(TestData.TempDir(), "s.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("DATA");
            ws.Cell("A1").Value = "LOCATION"; ws.Cell("B1").Value = "QTY";
            ws.Cell("A2").Value = "P2-106"; ws.Cell("B2").Value = 36;
            ws.Cell("A4").Value = "P3-103"; ws.Cell("B4").FormulaA1 = "B2*2";
            ws.Cell("AA4").Value = "far";
            wb.SaveAs(path);
        }
        using var x = new XlsxStreamReader(path);
        var rows = x.ReadRows("DATA").ToList();
        Assert.Equal(new[] { 1, 2, 4 }, rows.Select(r => r.Number).ToArray());
        Assert.Equal("P2-106", rows[1].Get("A"));
        Assert.Equal(36, rows[1].Num("B"));
        Assert.Equal("B2*2", rows[2].Formula("B"));
        Assert.Equal("far", rows[2].Get("AA"));
        Assert.Equal(27, XlsxStreamReader.ColumnIndex("AA"));
        Assert.Equal("AA", XlsxStreamReader.ColumnLetter(27));
    }
}

public class TrackerImportTests
{
    [Fact]
    public void ReadsRoomsProjectQtyAndLedger()
    {
        var r = TrackerImporter.Read(Phase1Fixtures.Tracker());
        Assert.Equal(4, r.Rooms.Count);
        Assert.Equal(AreaTypes.Apartment, r.Rooms.Single(x => x.Code == "P9-101").AreaType);
        Assert.Equal(AreaTypes.Boh, r.Rooms.Single(x => x.Code == "P9-BS1").AreaType);
        Assert.Equal(AreaTypes.Foh, r.Rooms.Single(x => x.Code == "P9-GF").AreaType);
        Assert.Equal("L01", r.Rooms.Single(x => x.Code == "P9-101").Plan);
        Assert.Equal(13, r.Quantities.Count); // zeros skipped
        Assert.Equal(40, r.Quantities.Single(q => q.Room == "P9-101" && q.Stage == "1ST FIX" && q.Item == "POWER").Qty);
        Assert.Equal(4, r.Quantities.Single(q => q.Room == "P9-102" && q.Stage == "CEILING" && q.Item == "LIGHT").Qty);
        Assert.Equal(5, r.Claims.Count);
        var c = r.Claims.Single(x => x.Subcontractor == "SUBA" && x.Room == "P9-102");
        Assert.Equal(2, c.InvoiceNo);
        Assert.Equal(0.5, c.SitePct);
        Assert.Equal(15, c.QtyAfterWir);
        Assert.Equal(2, r.Subcontractors);
        Assert.Equal(3, r.Invoices);
    }

    [Fact]
    public void Commit_IsIdempotentForLedgerAndKeepsAreaEdits()
    {
        var store = TestData.NewDb();
        var file = Phase1Fixtures.Tracker();
        var first = TrackerImporter.Commit(TrackerImporter.Read(file), store);
        Assert.Equal((4, 13, 5, 0), first);
        var room = store.All<Room>().Single(x => x.Code == "P9-101");
        room.AreaType = AreaTypes.Foh;
        store.Update(room);
        var second = TrackerImporter.Commit(TrackerImporter.Read(file), store);
        Assert.Equal(0, second.claims);
        Assert.Equal(5, second.skipped);
        Assert.Equal(13, store.Count<RoomQty>());
        Assert.Equal(AreaTypes.Foh, store.All<Room>().Single(x => x.Code == "P9-101").AreaType);
    }

    [Fact]
    public void PlanDrawing_ShapesBecomeNormalisedPolygonsOnTheirPlan()
    {
        XNamespace xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XElement Anchor(long x, long y, long cx, long cy, XElement body) =>
            new(xdr + "absoluteAnchor", new XElement(xdr + "pos", new XAttribute("x", x), new XAttribute("y", y)), new XElement(xdr + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy)), body);
        var pic = new XElement(xdr + "pic",
            new XElement(xdr + "nvPicPr", new XElement(xdr + "cNvPr", new XAttribute("id", 3), new XAttribute("name", "PlanImageL01"))),
            new XElement(xdr + "blipFill", new XElement(a + "blip", new XAttribute(r + "embed", "rId1"))),
            new XElement(xdr + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(a + "ext", new XAttribute("cx", 1000), new XAttribute("cy", 500)))));
        var label = new XElement(xdr + "sp", new XElement(xdr + "nvSpPr", new XElement(xdr + "cNvPr", new XAttribute("id", 2), new XAttribute("name", "LVL_L01"), new XAttribute("descr", "Level 01"))));
        var shape = new XElement(xdr + "sp",
            new XElement(xdr + "nvSpPr", new XElement(xdr + "cNvPr", new XAttribute("id", 4), new XAttribute("name", "RM_P9-101"), new XAttribute("descr", "P9-101 | test"))),
            new XElement(xdr + "spPr", new XElement(a + "xfrm", new XElement(a + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(a + "ext", new XAttribute("cx", 200), new XAttribute("cy", 100))),
                new XElement(a + "custGeom", new XElement(a + "pathLst", new XElement(a + "path", new XAttribute("w", 200), new XAttribute("h", 100),
                    new XElement(a + "moveTo", new XElement(a + "pt", new XAttribute("x", 0), new XAttribute("y", 0))),
                    new XElement(a + "lnTo", new XElement(a + "pt", new XAttribute("x", 200), new XAttribute("y", 0))),
                    new XElement(a + "lnTo", new XElement(a + "pt", new XAttribute("x", 200), new XAttribute("y", 100))),
                    new XElement(a + "close"))))));
        var doc = new XDocument(new XElement(xdr + "wsDr", Anchor(100, 100, 1000, 500, label), Anchor(100, 200, 1000, 500, pic), Anchor(300, 300, 200, 100, shape)));
        var res = PlanDrawingParser.Parse(doc, id => id == "rId1" ? new byte[] { 1, 2, 3 } : null, Buildings.Branded);
        var plan = Assert.Single(res.Plans);
        Assert.Equal("L01", plan.Plan);
        Assert.Equal("Level 01", plan.Caption);
        Assert.Equal(3, plan.Png!.Length);
        var s = Assert.Single(res.Shapes);
        Assert.Equal("P9-101", s.Room);
        Assert.Equal("L01", s.Plan);
        Assert.Equal("0.2,0.2 0.4,0.2 0.4,0.4", s.Polygons);
        Assert.Equal(0.2, s.Left, 6);
        Assert.Equal(0.4, s.Bottom, 6);
    }
}

public class ContractImportTests
{
    [Theory]
    [InlineData("install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or wall-mounted lighting point. Rate shall include wall chasing/cutting for installation at heights below 4.5 m.", "1ST FIX", "PVC", "WALL", "LOW")]
    [InlineData("install, and hand over a PVC 1st Fix outlet for a lighting switch, power socket, or ceiling lighting point. Rate shall include wall chasing/cutting for installation at heights above 4.5 m.", "1ST FIX", "PVC", "CEILING", "HIGH")]
    [InlineData("install, and hand over an EMT 1st Fix outlet for a lighting switch, power socket, lighting point, or DALI outlet (wall-mounted or ceiling-mounted), heights below 4.5 m", "1ST FIX", "EMT", "BOTH", "LOW")]
    [InlineData("install, and hand over a flexible conduit for the 3rd Fix outlet for a lighting switch, power socket, lighting point, or DALI outlet (wall-mounted or ceiling-mounted), above 4.5 m", "3RD FIX", "FLEX", "BOTH", "HIGH")]
    [InlineData("تركيب و تسليم مخرج من النوع PVC 1st Fix مفتاح انارة أو بريزة أو مخرج إنارة سقفي طبقا لأصول الصناعة و السعر شامل التكسير بالجدران لإرتفاع فوق 4.5 متر", "1ST FIX", "PVC", "CEILING", "HIGH")]
    [InlineData("تركيب و تسليم مخرج من النوع PVC 1st Fix مفتاح انارة أو بريزة أو مخرج إنارة جداري و السعر شامل التكسير بالجدران لإرتفاع أقل 4.5 متر", "1ST FIX", "PVC", "WALL", "LOW")]
    public void Parser_ReadsStageConduitMountHeight(string desc, string stage, string conduit, string mount, string height)
    {
        var a = ContractAttributeParser.Parse(desc);
        Assert.Equal(stage, a.FixStage);
        Assert.Equal(conduit, a.Conduit);
        Assert.Equal(mount, a.Mount);
        Assert.Equal(height, a.Height);
        Assert.Contains("POWER", a.Systems);
        Assert.Contains("LIGHT", a.Systems);
    }

    [Fact]
    public void Parser_SystemsCategoriesAndSizes()
    {
        var wiring = ContractAttributeParser.Parse("Wire pulling and termination for 2nd Fix works for a lighting switch, power socket, below 4.5 m. If longer than 15 m an extra point is counted.");
        Assert.True(wiring.Pulling);
        Assert.Equal("WIRING", wiring.Category);
        Assert.True(ContractAttributeParser.Parse("سحب سلك 2nd Fix مفتاح انارة و في حاله طول النقطه اكثر من 15 متر").Pulling);
        var homerun = ContractAttributeParser.Parse("Wire pulling and termination for 2nd Fix homerun circuit wiring (Homerun for Circuit) for a lighting switch");
        Assert.True(homerun.Homerun);
        var data = ContractAttributeParser.Parse("install, and hand over a PVC 1st Fix outlet for data, telephone, TV, or CCTV system, wall-mounted");
        Assert.Contains("DATA", data.Systems); Assert.Contains("AV", data.Systems); Assert.Contains("CCTV", data.Systems);
        Assert.DoesNotContain("LIGHT", data.Systems);
        var fa = ContractAttributeParser.Parse("a PVC 1st Fix outlet for fire alarm, speaker, A/V, or BGM system, ceiling-mounted");
        Assert.Contains("FIRE", fa.Systems); Assert.Contains("EVACUATION", fa.Systems);
        var facade = ContractAttributeParser.Parse("a PVC 1st Fix outlet for a lighting switch or external façade lighting outlet");
        Assert.Equal(new[] { "FACADE LIGHT" }, facade.Systems);
        var panel = ContractAttributeParser.Parse("Installation, connection, testing, and handover of electrical panel (42 ways) – labor only");
        Assert.Equal(("PANEL", "PANEL 42"), (panel.Category, panel.SizeKey));
        Assert.Equal("PANEL 6-12", ContractAttributeParser.Parse("handover of electrical panel (6–12 ways) – labor only").SizeKey);
        var cable = ContractAttributeParser.Parse("Pull and hand over cable 16 mm² (4C or 3×16) including required tagging/labeling.");
        Assert.Equal(("CABLE", "4X16"), (cable.Category, cable.SizeKey));
        Assert.Equal("1X6", ContractAttributeParser.Parse("Pull and hand over cable 6 mm² (1C) including required tagging").SizeKey);
        Assert.Equal("CABLE TERMINATION", ContractAttributeParser.Parse("install, test, and commission cable 300 mm² (4C or 3×300) including required tagging").Category);
        Assert.Equal("TRAY 50-300", ContractAttributeParser.Parse("cable supports (cable tray, cable trunking, or cable ladders) in sizes ranging from 5 cm to 30 cm").SizeKey);
    }

    [Fact]
    public void HeightPairs_InferredFromRatesWhenNotStated()
    {
        var a = new ContractItem { ItemNo = "40", Description = "install a widget outlet", Rate = 55, HeightBand = HeightBands.Any };
        var b = new ContractItem { ItemNo = "41", Description = "install a widget outlet", Rate = 57, HeightBand = HeightBands.Any };
        Assert.Equal(2, ContractAttributeParser.InferHeightPairs(new List<ContractItem> { a, b }));
        Assert.Equal(HeightBands.Low, a.HeightBand);
        Assert.Equal(HeightBands.High, b.HeightBand);
        Assert.Contains("inferred", b.ParseNotes);
    }

    [Fact]
    public void LinkWorkbook_ItemsSectionsAndCodes()
    {
        var r = ContractLinkImporter.Read(Phase1Fixtures.LinkWorkbook(withDescriptions: true), Phase1Fixtures.Contract, "suba");
        Assert.Equal(7, r.Items.Count);
        Assert.Equal(new[] { "Lighting and Power", "Panels" }, r.Sections.ToArray());
        Assert.Equal(Phase1Fixtures.ContractItems.Sum(i => i.Codes.Length), r.Links.Count);
        Assert.Equal(1, r.ItemsWithoutCodes);
        Assert.Equal("SUBA", r.Contract.Subcontractor);
        Assert.Equal(Buildings.Branded, r.Contract.Building);
        Assert.Equal("small power points", r.Links.First(l => l.ItemNo == "1").BoqDescription);
        var i2 = r.Items.Single(i => i.ItemNo == "2");
        Assert.Equal((HeightBands.High, 57.0), (i2.HeightBand, i2.Rate));
        Assert.True(r.Items.Single(i => i.ItemNo == "11").Is2ndFixPulling);
        var store = TestData.NewDb();
        ContractLinkImporter.Commit(r, store);
        Assert.Equal(7, store.Count<ContractItem>());
        ContractLinkImporter.Commit(ContractLinkImporter.Read(Phase1Fixtures.LinkWorkbook(), Phase1Fixtures.Contract, "SUBA"), store); // re-import replaces
        Assert.Equal(7, store.Count<ContractItem>());
        Assert.Single(store.All<Contract>());
    }

    [Fact]
    public void EPromiseAndTemplate_Import()
    {
        var file = Phase1Fixtures.InvoiceWorkbook();
        var ep = EPromiseImporter.Read(file);
        Assert.Equal(10, ep.Items.Count);
        Assert.Equal(1, ep.Duplicates);
        Assert.Equal("small power points", ep.Items.Single(i => i.ItemCode == "B6-01-01-00-6-26-V-5").Description);
        Assert.Equal("160100", ep.Items[0].CostCode);

        var t = InvoiceTemplateImporter.Read(file, Phase1Fixtures.Contract);
        Assert.Equal("TEST INV 1", t.SheetName);
        Assert.Equal("SUBA", t.VendorName);
        Assert.Equal("NOTE", t.Rows[0].Kind);
        Assert.Contains(t.Rows, r => r.Kind == "SECTION" && r.Description == "Panels");
        Assert.Equal(Phase1Fixtures.ContractItems.Sum(i => Math.Max(1, i.Codes.Length)), t.ItemRows);
        Assert.Equal(0.7, t.Rows.First(r => r.ItemNo == "308").StagePct);

        var store = TestData.NewDb();
        ContractLinkImporter.Commit(ContractLinkImporter.Read(Phase1Fixtures.LinkWorkbook(), Phase1Fixtures.Contract, "SUBA"), store);
        EPromiseImporter.Commit(ep, store);
        InvoiceTemplateImporter.Commit(t, store, Phase1Fixtures.Contract);
        // item 308 has no link-table code: the template code becomes a TEMPLATE link
        Assert.Contains(store.All<ContractItemBoq>(), l => l.ItemNo == "308" && l.Source == "TEMPLATE" && l.BoqCode == "B6-01-01-00-6-26-AL-2");
        Assert.Equal(0.7, store.All<ContractItem>().Single(i => i.ItemNo == "308").StagePct);
    }
}
