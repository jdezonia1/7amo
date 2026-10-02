using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;
using Raffaello.Core.Variations;
using Xunit.Abstractions;

namespace Raffaello.Core.Tests;

/// <summary>[drawings] Statement verification, revision compare, store, PROJECT QTY diff and the service end to end.</summary>
public sealed class DrawingsWorkflowTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raffaello-dwgwf-" + Guid.NewGuid().ToString("N"));
    public DrawingsWorkflowTests(ITestOutputHelper o) { _out = o; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static (Synthetic.Scene Scene, GrayImage Sheet, List<DwgSymbol> Lib, Dictionary<long, DwgSymbol> Map, List<DwgHit> Hits) Clean(int seed)
    {
        var scene = Synthetic.Generate(seed, width: 1600, height: 1100, roomsX: 3, roomsY: 2, symbolsPerRoom: 10);
        var sheet = Synthetic.Render(scene).ToGray();
        var lib = Synthetic.Library(scene, sheet);
        var hits = TemplateMatcher.Match(sheet, lib.Select(s => SymbolTemplate.From(s, scene.Dpi)).ToList())
            .Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
        var rooms = new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon)));
        foreach (var h in hits) h.Room = rooms.RoomAt(h.Center);
        return (scene, sheet, lib, lib.ToDictionary(s => s.Id), hits);
    }

    [Fact]
    public void Statement_highlights_are_counted_per_room_and_compared_with_the_claim()
    {
        var (scene, sheet, lib, map, hits) = Clean(31);
        var scan = Synthetic.Markup(scene, 8, share: 0.55, scale: 0.97, rotationDeg: 0.5, shiftX: 25, shiftY: -15);
        var settings = new DrawingSettings();
        var rooms = new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon)));
        var page = StatementVerifier.VerifyPage(new StatementPageInput
        {
            Page = scan.Page, Clean = sheet, CleanHits = hits, Symbols = map, Rooms = rooms, Settings = settings, Stage = "1ST FIX",
            MetresPerPx = scene.MetresPerPixel / 0.97,
        });
        _out.WriteLine($"align {page.AlignScore:0.00}; highlighted {page.Symbols.Count(s => s.Highlighted)} of {page.Symbols.Count}, truth {scan.Highlighted.Count}; unmatched spots {page.UnmatchedSpots.Count}; runs {page.RunLengthM:0.0} m");
        // precision / recall of "highlighted" against the markup truth
        var found = page.Symbols.Where(s => s.Highlighted).Select(s => (s.Hit.SymbolName, s.Hit.Center)).ToList();
        // truth = highlighted symbols that the clean takeoff found (the others show up as unmatched highlight spots)
        var inClean = scan.Highlighted.Where(p => hits.Any(h => h.SymbolName == p.Type && h.Center.DistanceTo(new PointD(p.Cx, p.Cy)) < 12)).ToList();
        Assert.True(page.UnmatchedSpots.Count >= scan.Highlighted.Count - inClean.Count - 1);
        var sc = Synthetic.Evaluate(inClean, found, scene.SymbolSize * 0.5);
        int tp = sc.Sum(x => x.TruePositives), fp = sc.Sum(x => x.FalsePositives), fn = sc.Sum(x => x.FalseNegatives);
        _out.WriteLine($"highlighted symbols TP {tp} FP {fp} FN {fn}");
        Assert.True(tp >= 0.95 * inClean.Count && fp <= 0.05 * inClean.Count + 1, $"TP {tp} FP {fp} FN {fn}");
        // claim: room R01 claims 3 more POWER than highlighted, R02 claims exactly
        var r01 = page.Highlighted.GetValueOrDefault(("R01", "1ST FIX", "POWER"));
        var r02 = page.Highlighted.GetValueOrDefault(("R02", "1ST FIX", "LIGHT"));
        var claimed = new List<ClaimedQty> { new("R01", "1ST FIX", "POWER", r01 + 3), new("R02", "1ST FIX", "LIGHT", r02) };
        var lines = StatementVerifier.Compare(new[] { page }, claimed);
        var l1 = lines.Single(l => l.Room == "R01" && l.Item == "POWER");
        Assert.Equal(3, l1.Diff, 3);
        Assert.True(l1.Flag is "OVER CLAIM" or "ABOVE DRAWING");
        Assert.Equal("OK", lines.Single(l => l.Room == "R02" && l.Item == "LIGHT").Flag);
        Assert.Contains(lines, l => l.Flag == "NOT CLAIMED");
        // highlighted runs measured (the note in the margin is a stroke too, so allow extra)
        Assert.True(page.RunLengthM >= scan.HighlightedRunPx * scene.MetresPerPixel * 0.8);
        // overlay page in the house layout
        var tp1 = StatementVerifier.Page(page, map, lines, "STATEMENT CHECK - TEST - p1", "statement.pdf", null, 1, scan.Page, 150);
        var pdf = TakeoffPdf.Build(new[] { tp1 }).Pdf;
        Assert.True(pdf.Length > 1000);
    }

    [Fact]
    public void Typical_unit_page_applies_to_each_listed_room()
    {
        var (scene, sheet, _, map, hits) = Clean(32);
        var scan = Synthetic.Markup(scene, 3, share: 0.5, scale: 1, rotationDeg: 0, shiftX: 0, shiftY: 0);
        var page = StatementVerifier.VerifyPage(new StatementPageInput
        {
            Page = scan.Page, Clean = sheet, CleanHits = hits, PageToClean = Affine2D.Identity, Symbols = map, Settings = new DrawingSettings(), RoomList = new() { "P2-106", "P3-103", "P4-04" }, Stage = "2ND FIX",
        });
        var a = page.Highlighted.Where(k => k.Key.Room == "P2-106").Sum(k => k.Value);
        Assert.True(a > 0);
        Assert.Equal(a, page.Highlighted.Where(k => k.Key.Room == "P4-04").Sum(k => k.Value));
    }

    [Fact]
    public void Revision_compare_finds_added_and_removed_symbols_and_drafts_a_variation()
    {
        var (scene, sheet, lib, map, oldHits) = Clean(41);
        var (rev, removed, added) = Synthetic.Revise(scene, 9, remove: 5, add: 6);
        var newImg = Synthetic.Warp(Synthetic.Render(rev), Affine2D.Similarity(1.0, 0.0, 12, -8), rev.Width + 20, rev.Height + 20).ToGray();
        var newHits = TemplateMatcher.Match(newImg, lib.Select(s => SymbolTemplate.From(s, 150)).ToList())
            .Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
        var rooms = new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon)));
        var res = RevisionComparer.Compare(sheet, newImg, oldHits, newHits, map, new DrawingSettings(), rooms);
        _out.WriteLine($"align {res.AlignScore:0.00}; +{res.AddedHits.Count} (truth {added.Count}) -{res.RemovedHits.Count} (truth {removed.Count}); regions +{res.Added.Count} -{res.Removed.Count}; rooms {string.Join(",", res.AffectedRooms)}");
        var sa = Synthetic.Evaluate(added, res.AddedHits.Select(h => (h.SymbolName, h.Center)), scene.SymbolSize * 0.6);
        var sr = Synthetic.Evaluate(removed, res.RemovedHits.Select(h => (h.SymbolName, h.Center)), scene.SymbolSize * 0.6);
        Assert.True(sa.Sum(x => x.TruePositives) >= added.Count - 1, "added symbols");
        Assert.True(sr.Sum(x => x.TruePositives) >= removed.Count - 1, "removed symbols");
        Assert.True(sa.Sum(x => x.FalsePositives) + sr.Sum(x => x.FalsePositives) <= 2);
        Assert.NotEmpty(res.Added);   // incl. the new partition wall
        foreach (var room in added.Select(a => a.Room).Concat(removed.Select(r => r.Room)).Distinct()) Assert.Contains(room, res.AffectedRooms);

        var db = Path.Combine(_dir, "v.db");
        var vs = new SqliteVariationStore(db, "qs");
        vs.EnsureSchema();
        var v = RevisionComparer.CreateVariationDraft(vs, res.Deltas, new DwgSheet { SheetNo = "E-101", Revision = "A", FileName = "a.pdf" }, new DwgSheet { SheetNo = "E-101", Revision = "B", FileName = "b.pdf", Building = Buildings.Branded });
        Assert.Equal(VariationStatus.Draft, v.Status);
        var lines = vs.Lines(v.Id);
        Assert.NotEmpty(lines);
        Assert.Equal(res.Deltas.Where(d => d.Delta > 0).Sum(d => d.Delta), lines.Where(l => l.Kind == VariationLineKinds.Addition).Sum(l => l.Qty), 3);
        Assert.Equal(-res.Deltas.Where(d => d.Delta < 0).Sum(d => d.Delta), lines.Where(l => l.Kind == VariationLineKinds.Omission).Sum(l => l.Qty), 3);
    }

    [Fact]
    public void Store_roundtrip_and_project_qty_diff_is_applied_only_where_accepted()
    {
        var file = Path.Combine(_dir, "data.db");
        var db = new Db(file, "mohamed", "PC");
        db.EnsureSchema();
        db.Insert(new RoomQty { Building = Buildings.Branded, Room = "R01", Stage = "1ST FIX", Item = "POWER", Qty = 5 });
        db.Insert(new RoomQty { Building = Buildings.Branded, Room = "R02", Stage = "1ST FIX", Item = "POWER", Qty = 7 });
        var store = new DrawingStoreSelector(() => db);
        var sheet = store.SaveSheet(new DwgSheet { Building = Buildings.Branded, SheetNo = "E-101", Revision = "B", FileName = "e101.pdf" });
        var sym = store.SaveSymbol(new DwgSymbol { Name = "socket", System = "POWER", Item = "POWER" });
        Assert.Throws<InvalidOperationException>(() => store.SaveSymbol(new DwgSymbol { Name = "SOCKET", System = "POWER" }));
        var hits = Enumerable.Range(0, 9).Select(i => new DwgHit { SymbolId = sym.Id, SymbolName = "SOCKET", X = i * 30, Y = 0, W = 20, H = 20, Room = i < 6 ? "R01" : "R03" }).ToList();
        var t = store.SaveTakeoff(new DwgTakeoff { SheetId = sheet.Id }, hits, new List<DwgRun>());
        Assert.Equal(9, store.Hits(t.Id).Count);
        // review: remove one false positive in R01, add one missed in R02
        var h = store.Hits(t.Id);
        h[0].Status = DwgHitStatus.Removed;
        h.Add(new DwgHit { SymbolId = sym.Id, SymbolName = "SOCKET", X = 500, Y = 0, W = 20, H = 20, Room = "R02", Status = DwgHitStatus.Added, Origin = DwgOrigins.Manual });
        store.SaveReview(t, h, new List<DwgRun>());
        Assert.Equal(9, store.Takeoffs(sheet.Id)[0].Hits);
        var counts = TakeoffCounter.Count(store.Hits(t.Id), store.Symbols().ToDictionary(s => s.Id), new DrawingSettings());
        var rows = QtyDiff.Build(counts, db.All<RoomQty>(), Buildings.Branded);
        var r01 = rows.Single(r => r.Room == "R01" && r.Stage == "1ST FIX");
        Assert.Equal(5, r01.Current); Assert.Equal(5, r01.Proposed); Assert.Equal("SAME", r01.Status);
        var r02 = rows.Single(r => r.Room == "R02" && r.Stage == "1ST FIX");
        Assert.Equal(1, r02.Proposed); Assert.Equal("CHANGE", r02.Status);
        var r03 = rows.Single(r => r.Room == "R03" && r.Stage == "1ST FIX");
        Assert.Equal("NEW", r03.Status);
        // nothing ticked -> nothing changes
        QtyDiff.Apply(db, store, rows, sheet, t.Id);
        Assert.Equal(7, db.All<RoomQty>().Single(q => q.Room == "R02" && q.Stage == "1ST FIX").Qty);
        r03.Accept = true;
        var applied = QtyDiff.Apply(db, store, rows, sheet, t.Id);
        Assert.Equal(1, applied.Inserted);
        Assert.Equal(7, db.All<RoomQty>().Single(q => q.Room == "R02" && q.Stage == "1ST FIX").Qty);
        Assert.Equal(3, db.All<RoomQty>().Single(q => q.Room == "R03" && q.Stage == "1ST FIX").Qty);
        Assert.Contains(store.Decisions(), d => d.Room == "R03" && d.Decision == "ACCEPTED");
        Assert.Contains(store.Decisions(), d => d.Room == "R02" && d.Decision == "REJECTED");
        // a data reset clears the drawing tables too
        db.ClearAll();
        Assert.Empty(store.Sheets());
    }

    [Fact]
    public async Task Service_runs_a_pdf_takeoff_with_calibrated_tracker_rooms_and_writes_outputs()
    {
        var scene = Synthetic.Generate(51, width: 1600, height: 1100, roomsX: 3, roomsY: 2, symbolsPerRoom: 10);
        var pdf = Path.Combine(_dir, "E-101.pdf");
        var trays = SyntheticFiles.Trays(scene);
        File.WriteAllBytes(pdf, SyntheticFiles.VectorPdf(scene, trays));
        var db = new Db(Path.Combine(_dir, "svc.db"), "qs", "PC");
        db.EnsureSchema();
        // tracker plan shapes are normalised to a plan image with a different frame: u = 0.1 + x/W*0.8
        PointD ToPlan(PointD p) => new(0.1 + p.X / scene.Width * 0.8, 0.05 + p.Y / scene.Height * 0.9);
        foreach (var r in scene.Rooms)
            db.Insert(new RoomShape { Building = Buildings.Branded, Room = r.Name, Plan = "L01", Polygons = Poly.Format(r.Polygon.Select(ToPlan)) });
        var store = new DrawingStoreSelector(() => db);
        var svc = new DrawingsService(store, new DrawingSettings(), project: () => db);
        var sheet = svc.ImportSheet(pdf, Buildings.Branded, "L01", "E-101", "B");
        Assert.Equal(50, sheet.ScaleDenominator);
        var (img, _) = svc.Picture(sheet);
        foreach (var type in Synthetic.SymbolTypes)
        {
            var inst = scene.Symbols.FirstOrDefault(p => p.Type == type && p.Quarter == 0 && !p.Mirror);
            if (inst is null) continue;
            svc.DefineSymbol(sheet, new RectD(inst.Cx - 15, inst.Cy - 15, 30, 30), new DwgSymbol { Name = type, System = Synthetic.SystemOf(type), Mount = type.Contains("LIGHT") ? "C" : "W", IsLightFitting = type.Contains("LIGHT") });
        }
        var vp = VectorPdfReader.Read(pdf, 1, sheet.Dpi);
        store.SaveLinearClass(new DwgLinearClass { Name = "TRAY 300", System = "TRAY", Size = "300", Targets = "1ST FIX|CABLE TRAY 300|1", StyleKeys = vp.Styles().First(s => s.Style.Colour == SyntheticFiles.TrayMagenta).Style.Key });
        // 3 clicked pairs: room corners on the sheet and on the plan
        var pairs = new[] { scene.Rooms[0].Polygon[0], scene.Rooms[^1].Polygon[2], scene.Rooms[2].Polygon[1] }.Select(p => (p, ToPlan(p))).ToList();
        var (cal, n) = svc.Calibrate(sheet, "L01", pairs);
        Assert.Equal(scene.Rooms.Count, n);
        Assert.True(cal.Residual < 0.5);
        var run = await svc.RunTakeoffAsync(sheet);
        var known = svc.Store.Symbols().Select(x => x.Name).ToHashSet();
        var scores = Synthetic.Evaluate(scene.Symbols.Where(p => known.Contains(p.Type)), run.Hits.Select(h => (h.SymbolName, h.Center)), 12);
        _out.WriteLine($"service takeoff: {run.Hits.Count} hits, TP {scores.Sum(s => s.TruePositives)} FP {scores.Sum(s => s.FalsePositives)} FN {scores.Sum(s => s.FalseNegatives)}; runs {run.Runs.Count}");
        Assert.True(scores.Sum(s => s.TruePositives) >= scene.Symbols.Count(p => known.Contains(p.Type)) * 0.9);
        var wrongRoom = run.Hits.Count(h => scene.Symbols.Any(p => p.Type == h.SymbolName && new PointD(p.Cx, p.Cy).DistanceTo(h.Center) < 12 && p.Room != h.Room));
        Assert.Equal(0, wrongRoom);
        var props = svc.Proposals(sheet, run.Takeoff);
        Assert.Contains(props, p => p.Item == "CABLE TRAY 300" && p.Unit == "m" && p.Proposed > 0);
        Assert.All(props.Where(p => p.Unit == "no"), p => Assert.Equal("NEW", p.Status));
        var (outPdf, xlsx, _) = svc.WriteOutputs(sheet, run.Takeoff, Path.Combine(_dir, "out"), "1BR-A", 1);
        Assert.True(File.Exists(outPdf) && File.Exists(xlsx));
    }
}
