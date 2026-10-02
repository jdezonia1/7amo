using Raffaello.Core.Drawings;
using Xunit.Abstractions;

namespace Raffaello.Core.Tests;

/// <summary>[drawings] Template matching, registration and highlight detection on synthetic drawings with known answers.</summary>
public sealed class DrawingsMatcherTests
{
    private readonly ITestOutputHelper _out;
    public DrawingsMatcherTests(ITestOutputHelper output) => _out = output;

    internal static (Synthetic.Scene Scene, GrayImage Sheet, List<SymbolTemplate> Templates, List<DwgSymbol> Library) Fixture(int seed = 7, int roomsX = 3, int roomsY = 2)
    {
        var scene = Synthetic.Generate(seed, width: 1700, height: 1150, roomsX: roomsX, roomsY: roomsY, symbolsPerRoom: 14);
        var sheet = Synthetic.Render(scene).ToGray();
        var lib = Synthetic.Library(scene, sheet);
        var templates = lib.Select(s => SymbolTemplate.From(s, scene.Dpi)).ToList();
        return (scene, sheet, templates, lib);
    }

    [Fact]
    public void Finds_every_symbol_type_with_rotations_and_mirrors()
    {
        var (scene, sheet, templates, _) = Fixture();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = TemplateMatcher.Match(sheet, templates, new MatchOptions { Threshold = 0.70 });
        var dbg = Environment.GetEnvironmentVariable("RAFFAELLO_DWG_DEBUG");
        if (!string.IsNullOrEmpty(dbg))
        {
            foreach (var t in templates) File.WriteAllBytes(Path.Combine(dbg, $"tpl_{t.Name.Replace(' ', '_')}.png"), TemplateMatcher.Trim(t.Image).ToPng());
            var ov = sheet.ToColor();
            foreach (var h in hits) { ov.Rect(h.X, h.Y, h.W, h.H, new Rgb(255, 0, 0), 2); ov.Text(h.X, h.Y - 12, h.Name[..Math.Min(5, h.Name.Length)], new Rgb(255, 0, 0), 2); }
            foreach (var s in scene.Symbols) ov.Text(s.Cx - 10, s.Cy + 16, s.Type[..Math.Min(5, s.Type.Length)], new Rgb(0, 0, 255), 2);
            File.WriteAllBytes(Path.Combine(dbg, "hits.png"), ov.ToPng());
            foreach (var h in hits.Where(h => !scene.Symbols.Any(s => s.Type == h.Name && new PointD(s.Cx, s.Cy).DistanceTo(h.Center) < 12)))
            {
                var near = scene.Symbols.OrderBy(s => new PointD(s.Cx, s.Cy).DistanceTo(h.Center)).First();
                _out.WriteLine($"FP {h.Name} {h.Center} {h.Score:0.00} r{h.Rotation} m{h.Mirrored} nearest {near.Type} at {near.Cx:0},{near.Cy:0} q{near.Quarter} m{near.Mirror}");
            }
            var only = TemplateMatcher.Match(sheet, templates.Where(t => t.Name == "DOWNLIGHT").ToList(), new MatchOptions { Threshold = 0.5, MinStrokePrecision = 0, MinStrokeRecall = 0 });
            foreach (var h in only) _out.WriteLine($"DL-only {h.Center} {h.Score:0.00}");
            foreach (var s in scene.Symbols.Where(s => !hits.Any(h => s.Type == h.Name && new PointD(s.Cx, s.Cy).DistanceTo(h.Center) < 12)))
                _out.WriteLine($"FN {s.Type} {s.Cx:0},{s.Cy:0} q{s.Quarter} m{s.Mirror} near hits: {string.Join("; ", hits.Where(h => new PointD(s.Cx, s.Cy).DistanceTo(h.Center) < 30).Select(h => $"{h.Name} {h.Score:0.00}"))}");
        }
        _out.WriteLine($"{hits.Count} hits in {sw.ElapsedMilliseconds} ms, truth {scene.Symbols.Count}");
        var scores = Synthetic.Evaluate(scene.Symbols, hits.Select(h => (h.Name, h.Center)), scene.SymbolSize * 0.5);
        foreach (var s in scores) _out.WriteLine($"{s.Type,-12} TP {s.TruePositives,3} FP {s.FalsePositives,3} FN {s.FalseNegatives,3}  P {s.Precision:P0} R {s.Recall:P0}");
        var tp = scores.Sum(s => s.TruePositives); var fp = scores.Sum(s => s.FalsePositives); var fn = scores.Sum(s => s.FalseNegatives);
        Assert.True(tp / (double)(tp + fp) >= 0.95, $"precision {tp}/{tp + fp}");
        Assert.True(tp / (double)(tp + fn) >= 0.95, $"recall {tp}/{tp + fn}");
    }

    [Fact]
    public void Registration_recovers_scan_scale_rotation_and_shift()
    {
        var scene = Synthetic.Generate(3, width: 1600, height: 1100, roomsX: 3, roomsY: 2, symbolsPerRoom: 10);
        var clean = Synthetic.Render(scene).ToGray();
        var scan = Synthetic.Markup(scene, 5, scale: 0.95, rotationDeg: 0.8, shiftX: 40, shiftY: -25);
        var r = Registration.Align(clean, scan.Page.ToInkGray());
        var truth = scan.SheetToPage.Inverse();
        _out.WriteLine($"score {r.Score:0.000} scale {r.Scale:0.0000} rot {r.RotationDeg:0.00}");
        foreach (var p in new[] { new PointD(100, 100), new PointD(1500, 1000), new PointD(800, 550) })
        {
            var pagePt = scan.SheetToPage.Apply(p);
            var back = r.MovingToFixed.Apply(pagePt);
            _out.WriteLine($"{p} -> {back} (truth {truth.Apply(pagePt)})");
            Assert.True(back.DistanceTo(p) < 4, $"{p} mapped to {back}");
        }
        Assert.True(r.Reliable);
    }

    [Fact]
    public void Highlight_spots_and_runs_are_found_and_measured()
    {
        var scene = Synthetic.Generate(11, width: 1600, height: 1100, roomsX: 3, roomsY: 2, symbolsPerRoom: 8);
        var scan = Synthetic.Markup(scene, 2, share: 0.5, scale: 1, rotationDeg: 0, shiftX: 0, shiftY: 0, runs: 2);
        var res = HighlightDetector.Detect(scan.Page, new HighlightOptions { MinArea = 60 });
        var strokes = res.Strokes.ToList();
        var dbg = Environment.GetEnvironmentVariable("RAFFAELLO_DWG_DEBUG");
        if (!string.IsNullOrEmpty(dbg))
        {
            var ov = scan.Page.Clone();
            foreach (var b in res.Blobs) { ov.Rect(b.X, b.Y, b.W, b.H, b.Kind == HighlightKinds.Stroke ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255), 1); foreach (var p in b.Skeleton) ov.Set((int)p.X, (int)p.Y, new Rgb(255, 0, 255)); }
            foreach (var r in scan.HighlightedRuns) ov.Polyline(r, new Rgb(255, 128, 0), 1);
            File.WriteAllBytes(Path.Combine(dbg, "hl.png"), ov.ToPng());
        }
        _out.WriteLine($"spots {res.Spots.Count()} strokes {strokes.Count} lengths {string.Join(", ", strokes.Select(s => s.LengthPx.ToString("0")))} truth runs {scan.HighlightedRunPx:0}");
        Assert.InRange(res.Spots.Count(), scan.Highlighted.Count - 3, scan.Highlighted.Count + 1);
        // the two highlighted conduit runs + the margin note
        var runLen = strokes.Where(s => s.Y > 40).Sum(s => s.LengthPx);
        Assert.InRange(runLen, scan.HighlightedRunPx * 0.85, scan.HighlightedRunPx * 1.15);
    }
}
