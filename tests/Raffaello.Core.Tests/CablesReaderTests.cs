using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ClosedXML.Excel;
using CSMath;
using Raffaello.Core.Cables;
using Raffaello.Core.Domain;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Raffaello.Core.Tests;

/// <summary>[cables] SLD / drawing / schedule readers on synthetic files generated in the test.</summary>
public class CablesReaderTests
{
    private const double W = 842, H = 595;

    /// <summary>A synthetic SLD page: MDB box on top, bus bar, two drops to SMDB boxes with size + breaker labels; page 2 = a printed cable schedule.</summary>
    internal static string SldPdf()
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var page = b.AddPage(W, H);
        PdfPoint P(double x, double y) => new(x, H - y);   // top-left coordinates -> PDF
        void Box(double x, double y, double w, double h) => page.DrawRectangle(P(x, y + h), w, h, 1);
        void Line(double x1, double y1, double x2, double y2) => page.DrawLine(P(x1, y1), P(x2, y2), 1);
        void Text(string t, double x, double y, double size = 8) => page.AddText(t, size, P(x, y), font);

        Box(350, 60, 140, 40); Text("MDB-BR-Z1-L00", 365, 85);
        Line(420, 100, 420, 150);                   // incomer drop to the bus
        Line(200, 150, 640, 150);                   // bus bar
        Line(250, 150, 250, 300); Line(590, 150, 590, 300);
        Text("4C x 70mm2 + 1x35 E", 256, 220); Text("250A", 256, 240);
        Text("4x95+1x50E", 596, 220); Text("L=48m", 596, 240);
        Box(190, 300, 120, 30); Text("SMDB-BR-Z1-L01-01", 196, 319);
        Box(530, 300, 120, 30); Text("SMDB-BR-Z1-L02-01", 536, 319);
        // a crossing line with no junction must not connect the two drops
        Line(150, 260, 700, 260);

        var p2 = b.AddPage(W, H);
        void T2(string t, double x, double y) => p2.AddText(t, 8, new PdfPoint(x, H - y), font);
        T2("FROM", 40, 60); T2("TO", 220, 60); T2("SIZE", 400, 60); T2("LENGTH", 520, 60);
        T2("SMDB-BR-Z1-L01-01", 40, 80); T2("LDB-BR-Z1-L01-02", 220, 80); T2("4x16", 400, 80); T2("45", 520, 80);
        T2("SMDB-BR-Z1-L01-01", 40, 100); T2("JF-LB1-04", 220, 100); T2("4x6", 400, 100); T2("62", 520, 100);
        var path = Path.Combine(TestData.TempDir(), "sld.pdf");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void Sld_pdf_vector_text_and_lines_become_runs_with_direction_size_earth_and_breaker()
    {
        var res = CableReaders.Read(SldPdf(), new CableReadOptions { Building = Buildings.Branded });
        Assert.Equal(CableSources.SldPdf, res.Kind);
        var a = Assert.Single(res.Runs, r => r.ToName == "SMDB-BR-Z1-L01-01");
        Assert.Equal("MDB-BR-Z1-L00", a.FromName);
        Assert.Equal("4X70", a.SizeKey);
        Assert.Equal("1X35", a.EarthSizeKey);
        Assert.Contains("250A", a.Breaker);
        Assert.True(a.Confidence >= 0.7, a.Confidence.ToString());
        var b = Assert.Single(res.Runs, r => r.ToName == "SMDB-BR-Z1-L02-01");
        Assert.Equal("MDB-BR-Z1-L00", b.FromName);
        Assert.Equal("4X95", b.SizeKey);
        Assert.Equal("1X50", b.EarthSizeKey);
        Assert.Equal(48, b.DesignLength);
        Assert.Equal(1, b.SourcePage);
        // page 2: printed schedule rows
        var t = Assert.Single(res.Runs, r => r.ToName == "LDB-BR-Z1-L01-02");
        Assert.Equal("SMDB-BR-Z1-L01-01", t.FromName);
        Assert.Equal("4X16", t.SizeKey);
        Assert.Equal(45, t.DesignLength);
        Assert.Equal(2, t.SourcePage);
        Assert.Contains(res.Runs, r => r.ToName == "JF-LB1-04" && r.SizeKey == "4X6" && r.DesignLength == 62);
        Assert.Equal(4, res.Runs.Count);
        // panels with their feeder
        var smdb = res.Panels.Single(p => p.Name == "SMDB-BR-Z1-L01-01");
        Assert.Equal(PanelNames.KeyOf("MDB-BR-Z1-L00"), smdb.ParentKey);
        Assert.Equal(Buildings.Branded, smdb.Building);
    }

    [Fact]
    public void Read_runs_saved_to_the_register_turn_provisional_claims_into_matched_ones()
    {
        var (_, svc) = CablesServiceTests.NewService();
        svc.Import(new[] { CablesServiceTests.Claim("SUBA", 1, "MDB BR-Z1-L00", "SMDB BR-Z1-L01-01", "4x70", 60, building: Buildings.Branded) }, null, "INV 1");
        Assert.Contains(svc.Flags(), f => f.Code == CableFlagCodes.UnknownRun);
        var res = CableReaders.Read(SldPdf(), new CableReadOptions { Building = Buildings.Branded });
        var m = svc.SaveRegister(res.Runs, res.Panels, "SLD");
        Assert.Equal(1, m.ProvisionalUpgraded);
        Assert.Equal(3, m.RunsAdded);
        Assert.DoesNotContain(svc.Flags(), f => f.Code == CableFlagCodes.UnknownRun);
        var tree = CableReports.Tree(svc.Load(), CableReports.Progress(svc.Load()));
        var root = tree.First();
        Assert.Equal("MDB-BR-Z1-L00", root.Panel.Name);
        Assert.Equal(2, root.Children.Count);
    }

    [Fact]
    public void Analyzer_keeps_crossing_lines_apart_and_joins_t_junctions()
    {
        var segs = new List<SldSegment> { new(0, 0, 10, 0), new(5, 0, 5, 10), new(0, 5, 10, 5) };   // T at (5,0); the third line crosses the second
        var comp = SldAnalyzer.Components(segs, 0.2);
        Assert.Equal(comp[0], comp[1]);
        Assert.NotEqual(comp[0], comp[2]);
    }

    [Fact]
    public void Analyzer_reads_cad_attribute_tags_and_fed_from_text()
    {
        var page = new SldPage { Width = 500, Height = 500 };
        var name = new SldText("DB 7", 100, 100, 30, 3);   // only recognisable because the attribute tag says it is the panel name
        page.Texts.Add(name);
        page.AttributeTags[name] = "PANEL_NAME";
        page.Texts.Add(new SldText("FED FROM SMDB-HT-Z1-L01-01", 100, 110, 80, 3));
        var r = SldAnalyzer.Analyze(page);
        Assert.Contains(r.Nodes, n => n.Name == "DB 7");
        Assert.Contains(r.FedFrom, f => f.ParentName == "SMDB-HT-Z1-L01-01");
    }

    [Fact]
    public void Cable_schedule_columns_are_found_by_header_anywhere_and_remembered()
    {
        var path = Path.Combine(TestData.TempDir(), "schedule.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("CABLE SCHEDULE");
            ws.Cell(1, 1).Value = "PROJECT X - CABLE SCHEDULE";
            var h = new[] { "CABLE TAG", "SOURCE", "DESTINATION", "NO. OF CORES", "MM²", "TYPE", "LENGTH (M)", "CB RATING", "EARTH" };
            for (var i = 0; i < h.Length; i++) ws.Cell(3, i + 1).Value = h[i];
            object[][] rows =
            {
                new object[] { "C-101", "EMDB-HT-Z1-LB2", "ESMDB-HT-Z1-LB2-KT-01", 4, 120, "XLPE/SWA/PVC", 135, "250A", 70 },
                new object[] { "C-102", "SMDB HT-Z1-LB2- KT 03", "ROLL IN ELECTRIC COMBI OVEN", 4, 70, "LSOH", 86, "125A", "1x35" },
                new object[] { "", "", "", "", "", "", "", "", "" },
            };
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(4 + r, c + 1).Value = XLCellValue.FromObject(rows[r][c]);
            wb.SaveAs(path);
        }
        var res = CableReaders.Read(path, new CableReadOptions { Building = Buildings.Hotel });
        Assert.Equal(2, res.Runs.Count);
        var a = res.Runs[0];
        Assert.Equal(("C-101", "4X120", 135.0, "250A", "1X70"), (a.Ref, a.SizeKey, a.DesignLength!.Value, a.Breaker, a.EarthSizeKey));
        Assert.Equal(Buildings.Hotel, a.Building);
        Assert.Equal("LSOH", res.Runs[1].Insulation);
        Assert.Equal(PanelNames.KeyOf("SMDB-HT-Z1-LB2-KT-03"), res.Runs[1].FromKey);
        Assert.Equal("SOURCE", res.Mapping["FROM"]);
        Assert.Equal("LENGTH (M)", res.Mapping["LENGTH"]);

        // a remembered profile for the same header layout wins over detection (here: the user said LENGTH is the CB column - nonsense, but remembered)
        var profile = new CableImportProfile { Signature = res.HeaderSignature, Mapping = "FROM=SOURCE;TO=DESTINATION;CORES=NO. OF CORES;MM2=MM²;LENGTH=CB RATING", LastUsed = DateTime.Now };
        var again = CableReaders.Read(path, new CableReadOptions { Profiles = new[] { profile } });
        Assert.Equal(250, again.Runs[0].DesignLength);
    }

    [Fact]
    public void Csv_schedule_is_read_too()
    {
        var path = Path.Combine(TestData.TempDir(), "schedule.csv");
        File.WriteAllText(path, "FROM,TO,CABLE SIZE,LENGTH\nMDB-BR-Z1-L00,SMDB-BR-Z1-L01-01,\"4C x 70mm2 + 1x35 E\",52\n");
        var r = Assert.Single(CableReaders.Read(path).Runs);
        Assert.Equal(("4X70", "1X35", 52.0), (r.SizeKey, r.EarthSizeKey, r.DesignLength!.Value));
    }

    private static CadDocument CadSld()
    {
        var doc = new CadDocument();
        void Box(double x, double y, double w, double h) =>
            doc.Entities.Add(new LwPolyline(new IVector[] { new XY(x, y), new XY(x + w, y), new XY(x + w, y + h), new XY(x, y + h) }) { IsClosed = true });
        // CAD y is up: the source panel is on top
        Box(0, 100, 60, 10); doc.Entities.Add(new TextEntity { Value = "SMDB-HT-Z1-LB2-KT-01", InsertPoint = new XYZ(2, 103, 0), Height = 2.5 });
        doc.Entities.Add(new Line(new XYZ(30, 100, 0), new XYZ(30, 40, 0)));
        doc.Entities.Add(new TextEntity { Value = "4x16+1x16E", InsertPoint = new XYZ(32, 70, 0), Height = 2.5 });
        Box(0, 30, 60, 10); doc.Entities.Add(new TextEntity { Value = "EDB-HT-Z1-LB2-KT-02", InsertPoint = new XYZ(2, 33, 0), Height = 2.5 });
        return doc;
    }

    [Fact]
    public void Dxf_and_dwg_drawings_are_read_with_acadsharp()
    {
        var dir = TestData.TempDir();
        var dxf = Path.Combine(dir, "sld.dxf");
        DxfWriter.Write(dxf, CadSld(), false, null, null);
        var r = CableReaders.Read(dxf, new CableReadOptions { Building = Buildings.Hotel });
        var run = Assert.Single(r.Runs);
        Assert.Equal(("SMDB-HT-Z1-LB2-KT-01", "EDB-HT-Z1-LB2-KT-02", "4X16", "1X16"), (run.FromName, run.ToName, run.SizeKey, run.EarthSizeKey));
        Assert.Equal(CableSources.Drawing, run.SourceKind);

        var dwg = Path.Combine(dir, "sld.dwg");
        DwgWriter.Write(dwg, CadSld(), null, null);
        var r2 = CableReaders.Read(dwg);
        Assert.Contains(r2.Runs, x => x.ToName == "EDB-HT-Z1-LB2-KT-02" && x.SizeKey == "4X16");
    }
}
