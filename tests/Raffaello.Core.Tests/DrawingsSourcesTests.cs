using Raffaello.Core.Drawings;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace Raffaello.Core.Tests;

/// <summary>[drawings] PDF (raster + vector), DXF / DWG, IFC and Revit JSON sources with known answers; takeoff PDF golden checks.</summary>
public sealed class DrawingsSourcesTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raffaello-dwg-" + Guid.NewGuid().ToString("N"));
    public DrawingsSourcesTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static Synthetic.Scene Scene(int seed = 21) => Synthetic.Generate(seed, width: 1600, height: 1100, roomsX: 3, roomsY: 2, symbolsPerRoom: 10);

    [Fact]
    public void Vector_pdf_is_rasterised_matched_and_its_lines_measured()
    {
        var scene = Scene();
        var trays = SyntheticFiles.Trays(scene);
        var notes = new Dictionary<int, int> { [0] = 1350, [3] = 3200 };
        var pdf = Path.Combine(_dir, "sheet.pdf");
        File.WriteAllBytes(pdf, SyntheticFiles.VectorPdf(scene, trays, notes));

        var (img, dpi) = new PdfiumRasterizer().Render(pdf, 1, scene.Dpi);
        Assert.Equal(scene.Dpi, dpi);
        Assert.InRange(img.Width, scene.Width - 2, scene.Width + 2);
        var gray = img.ToGray();
        var lib = Synthetic.Library(scene, gray);   // examples boxed on the rendered sheet itself, as the user does
        var hits = TemplateMatcher.Match(gray, lib.Select(s => SymbolTemplate.From(s, dpi)).ToList(), new MatchOptions());
        var scores = Synthetic.Evaluate(scene.Symbols, hits.Select(h => (h.Name, h.Center)), scene.SymbolSize * 0.5);
        int tp = scores.Sum(s => s.TruePositives), fp = scores.Sum(s => s.FalsePositives), fn = scores.Sum(s => s.FalseNegatives);
        _out.WriteLine($"PDF raster: TP {tp} FP {fp} FN {fn}");
        foreach (var sc in scores) _out.WriteLine($"  {sc.Type,-12} TP {sc.TruePositives} FP {sc.FalsePositives} FN {sc.FalseNegatives}");
        if (Environment.GetEnvironmentVariable("RAFFAELLO_DWG_DEBUG") is { Length: > 1 } dbg)
            foreach (var s in lib) File.WriteAllBytes(Path.Combine(dbg, $"pdf_{s.Name.Replace(' ', '_')}.png"), TemplateMatcher.Trim(TemplateMatcher.Isolate(GrayImage.FromPng(s.TemplatePng!))).ToPng());
        Assert.True(tp >= 0.9 * scene.Symbols.Count && fp <= 0.1 * scene.Symbols.Count, $"TP {tp} FP {fp} FN {fn}");

        var vp = VectorPdfReader.Read(pdf, 1, dpi);
        Assert.Equal(50, vp.ScaleFromText());
        var mpp = DwgSheet.MetresPerPixel(dpi, vp.ScaleFromText());
        var trayStyle = vp.Styles().First(s => s.Style.Colour == SyntheticFiles.TrayMagenta).Style;
        Assert.NotEqual("", trayStyle.Dash);
        var cls = new DwgLinearClass { Id = 1, Name = "TRAY 300", System = "TRAY", Size = "300", Targets = "1ST FIX|CABLE TRAY 300|1", StyleKeys = trayStyle.Key };
        var runs = LinearTakeoff.FromVector(vp, new[] { cls }, mpp);
        var truth = trays.Sum(t => Poly.PolylineLength(t)) * mpp;
        var measured = runs.Sum(r => r.LengthM);
        _out.WriteLine($"tray truth {truth:0.00} m measured {measured:0.00} m ({(measured - truth) / truth:P2})");
        Assert.InRange(measured, truth * 0.99, truth * 1.01);
        // H= notes next to symbols
        var s0 = scene.Symbols[0];
        Assert.Equal(1.35, vp.HeightNear(new PointD(s0.Cx, s0.Cy), scene.SymbolSize * 2), 3);
    }

    [Fact]
    public void Takeoff_pdf_keeps_the_vector_page_and_adds_panel_table_and_numbered_circles()
    {
        var scene = Scene(5);
        var pdf = Path.Combine(_dir, "unit.pdf");
        File.WriteAllBytes(pdf, SyntheticFiles.VectorPdf(scene));
        var lib = Synthetic.Library(scene, Synthetic.Render(scene).ToGray());
        var symbols = lib.ToDictionary(s => s.Id);
        // truth hits (as a reviewed takeoff would have them)
        var hits = scene.Symbols.Select(p => { var s = lib.First(x => x.Name == p.Type); return new DwgHit { SymbolId = s.Id, SymbolName = s.Name, X = p.Cx - 12, Y = p.Cy - 12, W = 24, H = 24, Score = 1, Room = p.Room }; }).ToList();
        var settings = new DrawingSettings();
        var input = new TakeoffOutputInput
        {
            Sheet = new DwgSheet { SheetNo = "E-101", FileName = "unit.pdf", Title = "1BR-A" }, TypeLabel = "1BR-A", Revision = 1,
            Hits = hits, Symbols = symbols, Settings = settings, Picture = Synthetic.Render(scene), Dpi = scene.Dpi, PdfPath = pdf, PdfPage = 1,
        };
        var pages = TakeoffOutput.Pages(input);
        var built = TakeoffPdf.Build(pages);
        var outPdf = Path.Combine(_dir, "takeoff.pdf");
        File.WriteAllBytes(outPdf, built.Pdf);
        var systems = hits.Select(h => TakeoffCounter.SystemOf(symbols[h.SymbolId])).Distinct().ToList();
        using var doc = PdfDocument.Open(outPdf);
        Assert.Equal(systems.Count, doc.NumberOfPages);
        using var src = PdfDocument.Open(pdf);
        var srcPaths = src.GetPage(1).Paths.Count;
        foreach (var page in doc.GetPages())
        {
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            Assert.Contains("QS TAKEOFF - 1BR-A -", text);
            Assert.Contains("TOTAL THIS SHEET", text);
            Assert.Contains("[REV 01]", text);
            Assert.Contains("unit.pdf", text);
            var system = systems.First(s => text.Contains($"- {s} "));
            var expected = hits.Count(h => TakeoffCounter.SystemOf(symbols[h.SymbolId]) == system);
            Assert.Contains($"{expected} marks on this sheet", text);
            // the original drawing is still vector and the page is wider than the original
            Assert.True(page.Width > src.GetPage(1).Width + 400);
            Assert.True(page.Paths.Count >= srcPaths);
            // one circle per mark: closed 4-curve subpaths stroked dark red / amber
            var circles = page.Paths.Count(p => p.IsStroked && !p.IsFilled && p.Count == 1 && p[0].Commands.OfType<UglyToad.PdfPig.Core.PdfSubpath.CubicBezierCurve>().Count() == 4
                                                && (Rgb(p.StrokeColor) == TakeoffPdf.DarkRed || Rgb(p.StrokeColor) == TakeoffPdf.Amber));
            Assert.Equal(expected, circles);
            // the totals printed equal the counting rules (CEIL = all items of the system)
            var table = TakeoffCounter.Tables(hits, symbols, settings).First(t => t.System == system);
            Assert.Equal(expected, (int)table.Totals["CEIL"]);
        }
        var xlsx = Path.Combine(_dir, "takeoff.xlsx");
        TakeoffOutput.Workbook(xlsx, new[] { input });
        using var wb = new ClosedXML.Excel.XLWorkbook(xlsx);
        Assert.Equal(hits.Count, wb.Worksheet("DETAIL").RowsUsed().Count() - 1);
        Assert.True(wb.Worksheets.Contains("SUMMARY"));
    }

    private static Raffaello.Core.Drawings.Rgb Rgb(UglyToad.PdfPig.Graphics.Colors.IColor? c)
    {
        if (c is null) return Raffaello.Core.Drawings.Rgb.Black;
        var (r, g, b) = c.ToRGBValues();
        return new Raffaello.Core.Drawings.Rgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cad_blocks_exploded_blocks_layers_and_rooms(bool dwg)
    {
        var scene = Scene(9);
        var trays = SyntheticFiles.Trays(scene);
        var path = Path.Combine(_dir, dwg ? "plan.dwg" : "plan.dxf");
        var truth = SyntheticFiles.Dxf(scene, path, trays, dwg);
        var model = CadReader.Read(path);
        Assert.Equal("mm", model.Units);
        Assert.Equal(0.001, model.MetresPerUnit);
        var lib = Synthetic.SymbolTypes.Select((t, i) => new DwgSymbol { Id = i + 1, Name = t, System = Synthetic.SystemOf(t), Item = Synthetic.SystemOf(t), BlockNames = SyntheticFiles.BlockOf(t) }).ToList();
        foreach (var s in lib) s.GeometrySignature = CadTakeoff.LearnFromBlock(model, s.BlockNames)!.Serialize();
        var classes = new[]
        {
            new DwgLinearClass { Id = 1, Name = "TRAY 300", System = "TRAY", Layers = "E-TRAY-*", Targets = "1ST FIX|CABLE TRAY 300|1" },
            new DwgLinearClass { Id = 2, Name = "CONDUIT 20", System = "CONDUIT", Layers = "E-CONDUIT-20", Targets = "1ST FIX|CONDUIT 20|1" },
        };
        var t = ModelTakeoff.FromCad(model, lib, classes, "A-ROOM");
        var blocks = t.Hits.Where(h => h.Origin == DwgOrigins.Block).ToList();
        var geo = t.Hits.Where(h => h.Origin == DwgOrigins.Geometry).ToList();
        Assert.Equal(truth.Inserted.Count, blocks.Count);
        Assert.True(blocks.All(b => b.SymbolName == lib.First(s => s.Id == b.SymbolId).Name));
        // exploded: recognised by geometry signature (rotation / mirror invariant)
        var mmPerPx = truth.MillimetresPerPx;
        var found = geo.Select(h => (h.SymbolName, new PointD(h.Center.X / mmPerPx, scene.Height + h.Center.Y / mmPerPx))).ToList();
        var scores = Synthetic.Evaluate(truth.Exploded, found, scene.SymbolSize * 0.6);
        int tp = scores.Sum(s => s.TruePositives), fp = scores.Sum(s => s.FalsePositives), fn = scores.Sum(s => s.FalseNegatives);
        _out.WriteLine($"{(dwg ? "DWG" : "DXF")} blocks {blocks.Count}/{truth.Inserted.Count}; exploded TP {tp} FP {fp} FN {fn}");
        foreach (var s in scores) _out.WriteLine($"  {s.Type,-12} P {s.Precision:P0} R {s.Recall:P0}");
        if (Environment.GetEnvironmentVariable("RAFFAELLO_DWG_DEBUG") is { Length: > 0 })
            foreach (var p in truth.Exploded.Where(p => p.Type is "DOWNLIGHT" or "GRMS CP-4").Take(2))
            {
                var c = new PointD(p.Cx * mmPerPx, (scene.Height - p.Cy) * mmPerPx); var r = scene.SymbolSize * mmPerPx;
                var prims = model.LoosePrimitives.Where(q => q.Bounds.Center.DistanceTo(c) < r).ToList();
                var sig = GeometrySignature.Of(prims)!;
                var learned = GeometrySignature.Parse(lib.First(s => s.Name == p.Type).GeometrySignature)!;
                _out.WriteLine($"{p.Type}: {prims.Count} prims sig {sig.Serialize()}\n  learned {learned.Serialize()}\n  dist {sig.Distance(learned)}");
                var maxSize = lib.Max(s => GeometrySignature.Parse(s.GeometrySignature)!.Size) * 1.6;
                var all = model.LoosePrimitives.Where(q => Math.Max(q.Bounds.W, q.Bounds.H) <= maxSize).ToList();
                foreach (var g in CadTakeoff.Group(all, maxSize * 0.04).Where(g => g.Any(q => prims.Contains(q))))
                    _out.WriteLine($"  group {g.Count} prims, layers {string.Join(",", g.Select(q => q.Layer).Distinct())} size {GeometrySignature.Of(g)!.Size:0}");
            }
        Assert.True(tp >= 0.9 * truth.Exploded.Count, $"exploded recall {tp}/{truth.Exploded.Count}");
        Assert.True(fp <= 0.1 * truth.Exploded.Count + 1, $"exploded false positives {fp}");
        // linear by layer
        var tray = t.Runs.Where(r => r.ClassId == 1).Sum(r => r.LengthM);
        var conduit = t.Runs.Where(r => r.ClassId == 2).Sum(r => r.LengthM);
        _out.WriteLine($"tray {tray:0.00}/{truth.TrayM:0.00} m, conduit {conduit:0.00}/{truth.ConduitM:0.00} m");
        Assert.InRange(tray, truth.TrayM * 0.999, truth.TrayM * 1.001);
        Assert.InRange(conduit, truth.ConduitM * 0.99, truth.ConduitM * 1.01);
        // rooms from closed polylines + texts, hits assigned by point in polygon
        Assert.Equal(scene.Rooms.Count, t.Rooms.Count);
        var rooms = new RoomAssigner(t.Rooms);
        foreach (var h in t.Hits) h.Room = rooms.RoomAt(h.Center);
        var expectRoom = truth.Inserted.Concat(truth.Exploded).GroupBy(p => p.Room).ToDictionary(g => g.Key, g => g.Count());
        var gotRoom = t.Hits.GroupBy(h => h.Room).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (room, n) in expectRoom) Assert.InRange(gotRoom.GetValueOrDefault(room), n - 2, n + 1);
    }

    [Fact]
    public void Ifc_counts_per_space_and_carrier_lengths()
    {
        var scene = Scene(4);
        var path = Path.Combine(_dir, "model.ifc");
        var truth = SyntheticFiles.Ifc(scene, path);
        var m = IfcModel.Read(path);
        Assert.Equal(0.001, m.LengthUnitM);
        Assert.Equal(scene.Rooms.Count, m.Spaces.Count);
        var lib = new List<DwgSymbol>
        {
            new() { Id = 1, Name = "POWER OUTLET", System = "POWER", IfcClasses = "IfcOutlet:POWEROUTLET" },
            new() { Id = 2, Name = "DATA OUTLET", System = "DATA", IfcClasses = "IfcOutlet:DATAOUTLET" },
            new() { Id = 3, Name = "LIGHT FIXTURE", System = "LIGHT", Mount = "C", IsLightFitting = true, IfcClasses = "IfcLightFixture" },
            new() { Id = 4, Name = "SWITCH", System = "LIGHT", IfcClasses = "IfcSwitchingDevice" },
            new() { Id = 5, Name = "OTHER", System = "POWER", IfcClasses = "IfcFlowTerminal" },
        };
        var classes = new[]
        {
            new DwgLinearClass { Id = 1, Name = "TRAY 300", System = "TRAY", IfcClasses = "IfcCableCarrierSegment:*300*", Targets = "1ST FIX|CABLE TRAY 300|1" },
            new DwgLinearClass { Id = 2, Name = "CABLE 4C16", System = "CABLE", IfcClasses = "IfcCableSegment", Targets = "2ND FIX|CABLE 4C16|1" },
        };
        var t = ModelTakeoff.FromIfc(m, lib, classes);
        Assert.Empty(t.Unmatched);
        foreach (var (room, types) in truth.CountsBySpace)
        {
            int Count(params string[] ty) => ty.Sum(x => types.GetValueOrDefault(x));
            Assert.Equal(Count("SOCKET", "TWIN SOCKET"), t.Hits.Count(h => h.Room == room && h.SymbolId == 1));
            Assert.Equal(Count("LIGHT", "DOWNLIGHT"), t.Hits.Count(h => h.Room == room && h.SymbolId == 3));
            Assert.Equal(Count("SWITCH 1G", "SWITCH 2G"), t.Hits.Count(h => h.Room == room && h.SymbolId == 4));
        }
        Assert.Equal(truth.TrayM, t.Runs.Where(r => r.ClassId == 1).Sum(r => r.LengthM), 3);
        Assert.Equal(truth.CableM, t.Runs.Where(r => r.ClassId == 2).Sum(r => r.LengthM), 3);
        var lengths = TakeoffCounter.Lengths(t.Runs, classes.ToDictionary(c => c.Id), new RoomAssigner(t.Rooms), 1);
        Assert.Equal(truth.TrayM, lengths.Where(k => k.Key.Item == "CABLE TRAY 300").Sum(k => k.Value), 3);
        Assert.DoesNotContain(lengths.Keys, k => k.Room == RoomAssigner.Unassigned);
    }

    [Fact]
    public void Revit_json_counts_and_lengths_flow_into_the_same_rules()
    {
        var scene = Scene(6);
        var lib = Synthetic.SymbolTypes.Select((t, i) => new DwgSymbol { Id = i + 1, Name = t, System = Synthetic.SystemOf(t), Item = Synthetic.SystemOf(t), Mount = t.Contains("LIGHT") ? "C" : "W" }).ToList();
        var classes = new[] { new DwgLinearClass { Id = 7, Name = "CABLE TRAY 300", System = "TRAY", Size = "300", Targets = "1ST FIX|CABLE TRAY 300|1" } };
        var t = ModelTakeoff.FromRevitJson(SyntheticFiles.RevitJson(scene), lib, classes);
        Assert.Empty(t.Unmatched);
        Assert.Equal(scene.Symbols.Count, t.Hits.Count);
        var counts = TakeoffCounter.Count(t.Hits, lib.ToDictionary(s => s.Id), new DrawingSettings());
        foreach (var room in scene.Rooms)
        {
            var power = scene.Symbols.Count(p => p.Room == room.Name && Synthetic.SystemOf(p.Type) == "POWER");
            Assert.Equal(power, counts.GetValueOrDefault((room.Name, "2ND FIX", "POWER")));
        }
        var m = TakeoffCounter.Lengths(t.Runs, classes.ToDictionary(c => c.Id), new RoomAssigner(t.Rooms), 1);
        Assert.Equal(scene.Rooms.Sum(r => r.W * scene.MetresPerPixel * 0.8), m.Values.Sum(), 2);
    }

    [Fact]
    public void Cable_lengths_with_heights_drops_riser_termination_and_spare()
    {
        // L-shaped route 0,0 -> 10,0 -> 10,8 (1 unit = 1 m); DB at 0,0; socket at 10,8 (H=0.45), light at 10,3 (ceiling)
        var net = new RouteNetwork(new[] { (new PointD(0, 0), new PointD(10, 0)), (new PointD(10, 0), new PointD(10, 8)) }, 0.01);
        var socket = new DwgSymbol { Id = 1, Name = "SOCKET", System = "POWER", Item = "POWER", Mount = "W" };
        var light = new DwgSymbol { Id = 2, Name = "LIGHT", System = "LIGHT", Item = "LIGHT", Mount = "C", IsLightFitting = true };
        var hits = new List<DwgHit>
        {
            new() { Id = 1, SymbolId = 1, X = 9.9, Y = 7.9, W = 0.2, H = 0.2, MarkNo = 1, Room = "R1" },
            new() { Id = 2, SymbolId = 2, X = 9.9, Y = 2.9, W = 0.2, H = 0.2, MarkNo = 2, Room = "R1" },
            new() { Id = 3, SymbolId = 1, X = 40, Y = 40, W = 0.2, H = 0.2, MarkNo = 3, Room = "R2" },
        };
        var settings = new DrawingSettings { SparePct = 0.10, TerminationAllowanceM = 0.5, PanelEntryHeightM = 2.0, Heights = { new HeightRow { FloorToCeilingM = 3.0, CeilingVoidM = 0.6, SlabToSlabM = 4.0 } } };
        settings.Heights.RemoveAt(0);
        var input = new CableLengthInput { Network = net, Panel = new PointD(0, 0), MetresPerUnit = 1, LevelsCrossed = 1, MaxSnap = 1 };
        var res = CableLengths.Compute(input, hits, new Dictionary<long, DwgSymbol> { [1] = socket, [2] = light }, settings);
        var s = res.First(r => r.MarkNo == 1);
        Assert.Equal(18, s.HorizontalM, 2);          // 10 + 8
        Assert.Equal(3.3 - 0.45, s.RiseM, 2);        // containment 3.0 + 0.3 - mounting 0.45
        Assert.Equal(1.3, s.DropM, 2);               // 3.3 - DB entry 2.0
        Assert.Equal(4.0, s.RiserM, 2);
        Assert.Equal(1.0, s.TerminationM, 2);
        Assert.Equal((18 + 2.85 + 1.3 + 4 + 1) * 1.1, s.TotalM, 2);
        var l = res.First(r => r.MarkNo == 2);
        Assert.Equal(13, l.HorizontalM, 2);
        Assert.Equal(0.3, l.RiseM, 2);               // drop from the void to the ceiling fitting
        Assert.False(res.First(r => r.MarkNo == 3).Traceable);
        var avg = CableLengths.Averages(res, r => r.System);
        Assert.Equal(2, avg.Count);
        Assert.Equal(Math.Round(s.TotalM / 15, 1), s.PointsEquivalent);
        Assert.Contains("x", CableLengths.LengthGroups(res));
    }
}
