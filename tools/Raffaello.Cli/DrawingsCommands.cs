using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;

namespace Raffaello.Cli;

/// <summary>
/// [drawings] Headless drawing takeoff / statement verification / revision compare:
///   raffaello-cli takeoff FILE [--page N] [--dpi 150] [--library lib.json] [--box NAME:x,y,w,h[:SYSTEM[:W|C]]]... [--rooms rooms.csv|json]
///                 [--threshold 0.7] [--type 1BR-A] [--rev 1] [--building BRANDED] [--level L01] [--db data.db] --out DIR
///   raffaello-cli verify-statement FILE [--pages 5-8] [--library lib.json] [--clean clean.pdf --clean-page N] [--claimed claims.csv]
///                 [--rooms "P2-106,P3-103"] [--colours GREEN,YELLOW] [--stage "1ST FIX"] [--scale 50] [--dpi 150] --out DIR
///   raffaello-cli compare-revisions OLD NEW [--page N] [--new-page N] [--library lib.json] [--dpi 150] --out DIR
///   raffaello-cli drawings-demo --out DIR      (synthetic drawings with known answers: precision / recall, length errors)
/// FILE: PDF, PNG, DXF, DWG, IFC or Revit add-in JSON. Outputs contain project data: keep --out outside the repository.
/// </summary>
public static class DrawingsCommands
{
    private static readonly HashSet<string> Names = new() { "takeoff", "verify-statement", "compare-revisions", "drawings-demo" };
    public static bool Handles(string cmd) => Names.Contains(cmd);

    public static int Run(string[] args)
    {
        var o = Options(args.Skip(1).ToArray(), out var pos, out var multi);
        var sw = Stopwatch.StartNew();
        try
        {
            switch (args[0])
            {
                case "takeoff": Takeoff(pos[0], o, multi.GetValueOrDefault("box") ?? new()); break;
                case "verify-statement": Verify(pos[0], o); break;
                case "compare-revisions": Compare(pos[0], pos[1], o); break;
                case "drawings-demo": Demo(Req(o, "out")); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 1;
        }
        Console.WriteLine($"({sw.ElapsedMilliseconds:N0} ms)");
        return 0;
    }

    private static Dictionary<string, string> Options(string[] a, out List<string> positional, out Dictionary<string, List<string>> multi)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var k = a[i][2..];
                var v = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
                d[k] = v;
                if (!multi.TryGetValue(k, out var l)) multi[k] = l = new List<string>();
                l.Add(v);
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    private static string Req(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
    private static int Int(Dictionary<string, string> o, string k, int d) => o.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
    private static double Dbl(Dictionary<string, string> o, string k, double d) => o.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : d;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static List<DwgSymbol> LoadLibrary(string path) => JsonSerializer.Deserialize<List<DwgSymbol>>(File.ReadAllText(path)) ?? new();
    public static void SaveLibrary(string path, IEnumerable<DwgSymbol> lib) => File.WriteAllText(path, JsonSerializer.Serialize(lib.ToList(), Json));

    private static List<int> Pages(string? spec, int count)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(1, count).ToList();
        var res = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-');
            int a = int.Parse(ab[0], CultureInfo.InvariantCulture), b = ab.Length > 1 ? int.Parse(ab[1], CultureInfo.InvariantCulture) : a;
            for (var p = a; p <= Math.Min(b, count); p++) res.Add(p);
        }
        return res;
    }

    // ------------------------------------------------------------------ takeoff

    private static void Takeoff(string file, Dictionary<string, string> o, List<string> boxes)
    {
        var outDir = Req(o, "out");
        Directory.CreateDirectory(outDir);
        var settings = new DrawingSettings { Threshold = Dbl(o, "threshold", 0.70) };
        var dbPath = o.GetValueOrDefault("db", Path.Combine(outDir, "drawings.db"));
        var db = new Db(dbPath, "cli"); db.EnsureSchema();
        var store = new DrawingStoreSelector(() => db);
        var svc = new DrawingsService(store, settings, project: () => db);
        if (o.TryGetValue("library", out var libPath))
            foreach (var s in LoadLibrary(libPath)) { if (store.Symbols().Any(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))) continue; s.Id = 0; store.SaveSymbol(s); }
        var sheet = svc.ImportSheet(file, o.GetValueOrDefault("building", Buildings.Branded), o.GetValueOrDefault("level", ""), o.GetValueOrDefault("sheet", ""),
            o.GetValueOrDefault("revision", "0"), Int(o, "page", 1), Int(o, "dpi", settings.DefaultDpi), o.GetValueOrDefault("type", ""));
        Console.WriteLine($"Sheet {sheet.Label}: {sheet.SourceKind} {sheet.PixelWidth}x{sheet.PixelHeight} px, scale {(sheet.ScaleDenominator > 0 ? "1:" + sheet.ScaleDenominator : "?")} ({sheet.ScaleSource}), m/unit {sheet.MetresPerUnit:0.#####}");
        foreach (var b in boxes)
        {
            // NAME:x,y,w,h[:SYSTEM[:W|C]]
            var p = b.Split(':');
            var r = p[1].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var sym = svc.DefineSymbol(sheet, new RectD(r[0], r[1], r[2], r[3]), new DwgSymbol { Name = p[0], System = p.Length > 2 ? p[2].ToUpperInvariant() : "POWER", Mount = p.Length > 3 ? p[3].ToUpperInvariant() : "W" });
            Console.WriteLine($"  symbol {sym.Name} ({sym.System}, {sym.Mount}) from box {sym.SourceBox}");
        }
        if (boxes.Count > 0) SaveLibrary(Path.Combine(outDir, "library.json"), store.Symbols());
        if (o.TryGetValue("rooms", out var rooms)) Console.WriteLine($"  rooms imported: {svc.ImportRooms(sheet, rooms)}");
        var run = svc.RunTakeoffAsync(sheet, new TakeoffRunOptions { Progress = new Progress<string>(_ => { }) }).GetAwaiter().GetResult();
        Console.WriteLine($"Takeoff: {run.Hits.Count} symbols, {run.Runs.Count} runs ({run.Runs.Sum(r => r.LengthM):0.0} m) in {run.Takeoff.Seconds:0.0} s");
        foreach (var g in run.Hits.GroupBy(h => h.SymbolName).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,-24} {g.Count(),5}  (score min {g.Min(h => h.Score):0.00})");
        foreach (var n in run.Notes) Console.WriteLine("  note: " + n);
        var csv = new StringBuilder(Csv.Line("MARK", "SYMBOL", "ROOM", "X", "Y", "W", "H", "SCORE", "ROTATION", "MIRRORED", "H_M", "ORIGIN") + "\n");
        foreach (var h in run.Hits) csv.AppendLine(Csv.Line(h.MarkNo, h.SymbolName, h.Room, h.X, h.Y, h.W, h.H, h.Score, h.Rotation, h.Mirrored, h.MountingHeightM, h.Origin));
        File.WriteAllText(Path.Combine(outDir, "hits.csv"), csv.ToString());
        var props = svc.Proposals(sheet, run.Takeoff);
        var pc = new StringBuilder(Csv.Line("ROOM", "STAGE", "ITEM", "UNIT", "CURRENT", "PROPOSED", "DIFF", "STATUS") + "\n");
        foreach (var p in props) pc.AppendLine(Csv.Line(p.Room, p.Stage, p.Item, p.Unit, p.Current, p.Proposed, p.Diff, p.Status));
        File.WriteAllText(Path.Combine(outDir, "proposed_project_qty.csv"), pc.ToString());
        var (pdf, xlsx, notes) = svc.WriteOutputs(sheet, run.Takeoff, outDir, o.GetValueOrDefault("type", ""), Int(o, "rev", 0));
        foreach (var n in notes) Console.WriteLine("  note: " + n);
        Console.WriteLine($"Wrote {pdf}\n      {xlsx}\n      hits.csv, proposed_project_qty.csv ({props.Count} rows, nothing applied)");
    }

    // ------------------------------------------------------------------ statement

    private static void Verify(string file, Dictionary<string, string> o)
    {
        var outDir = Req(o, "out");
        Directory.CreateDirectory(outDir);
        var settings = new DrawingSettings();
        var dpi = Int(o, "dpi", 150);
        var raster = new PdfiumRasterizer();
        var lib = o.TryGetValue("library", out var lp) ? LoadLibrary(lp) : new List<DwgSymbol>();
        for (var i = 0; i < lib.Count; i++) if (lib[i].Id == 0) lib[i].Id = i + 1;
        var map = lib.ToDictionary(s => s.Id);
        var templates = lib.Where(s => s.TemplatePng != null).Select(s => SymbolTemplate.From(s, dpi)).ToList();
        GrayImage? clean = null; List<DwgHit>? cleanHits = null;
        if (o.TryGetValue("clean", out var cleanPath))
        {
            var (ci, _) = SheetImages.Load(cleanPath, Int(o, "clean-page", 1), dpi, raster);
            clean = ci.ToGray();
            cleanHits = TemplateMatcher.Match(clean, templates).Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
            Console.WriteLine($"Clean drawing: {cleanHits.Count} symbols");
        }
        var claimed = o.TryGetValue("claimed", out var cp) ? StatementVerifier.FromCsv(cp) : new List<ClaimedQty>();
        var roomList = o.TryGetValue("rooms", out var rl) ? rl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new List<string>();
        var scale = Dbl(o, "scale", 0);
        var colours = HighlightColour.ParseList(o.GetValueOrDefault("colours", settings.HighlightColours));
        var pages = Pages(o.GetValueOrDefault("pages"), file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? raster.PageCount(file) : 1);
        var results = new List<StatementPageResult>();
        var pics = new Dictionary<int, ColorImage>();
        var report = new StringBuilder(Csv.Line("PAGE", "HIGHLIGHT_SHARE", "SPOTS", "STROKES", "STROKE_LENGTH_PX", "STROKE_LENGTH_M", "SYMBOLS", "HIGHLIGHTED_SYMBOLS", "UNMATCHED_SPOTS", "ALIGN") + "\n");
        foreach (var p in pages)
        {
            var (img, pdpi) = SheetImages.Load(file, p, dpi, raster);
            var r = StatementVerifier.VerifyPage(new StatementPageInput
            {
                Page = img, PageNo = p, Dpi = pdpi, Clean = clean, CleanHits = cleanHits, Templates = clean is null ? templates : null, Symbols = map, Settings = settings,
                RoomList = roomList, Highlight = new HighlightOptions { Colours = colours, MinArea = Math.Max(20, pdpi * pdpi / 900) }, Stage = o.GetValueOrDefault("stage", ""),
                MetresPerPx = scale > 0 ? DwgSheet.MetresPerPixel(pdpi, scale) : 0,
            });
            results.Add(r);
            pics[p] = img;
            report.AppendLine(Csv.Line(p, Math.Round(r.Highlights.CoveredShare, 4), r.Highlights.Spots.Count(), r.Highlights.Strokes.Count(), Math.Round(r.RunLengthPx), r.RunLengthM,
                r.Symbols.Count, r.Symbols.Count(s => s.Highlighted), r.UnmatchedSpots.Count, Math.Round(r.AlignScore, 3)));
            Console.WriteLine($"p{p,3}: highlight {r.Highlights.CoveredShare:P2} of page, {r.Highlights.Spots.Count()} spots, {r.Highlights.Strokes.Count()} strokes ({r.RunLengthPx:0} px{(r.RunLengthM > 0 ? $" = {r.RunLengthM:0.0} m" : "")}), " +
                              $"symbols {r.Symbols.Count(s => s.Highlighted)}/{r.Symbols.Count} highlighted, {r.UnmatchedSpots.Count} spots without a symbol");
        }
        File.WriteAllText(Path.Combine(outDir, "pages.csv"), report.ToString());
        var lines = StatementVerifier.Compare(results, claimed);
        var lc = new StringBuilder(Csv.Line("PAGE", "ROOM", "STAGE", "ITEM", "CLAIMED", "HIGHLIGHTED", "DRAWING_TOTAL", "DIFF", "FLAG") + "\n");
        foreach (var l in lines) lc.AppendLine(Csv.Line(l.Page, l.Room, l.Stage, l.Item, l.Claimed, l.Highlighted, l.DrawingTotal, l.Diff, l.Flag));
        File.WriteAllText(Path.Combine(outDir, "claimed_vs_highlighted.csv"), lc.ToString());
        Console.WriteLine($"Comparison: {lines.Count} rows, {lines.Count(StatementVerifier.IsFlag)} flagged");
        var specs = results.Select(r => StatementVerifier.Page(r, map, lines, $"STATEMENT CHECK - {Path.GetFileNameWithoutExtension(file)} - p{r.PageNo}", Path.GetFileName(file),
            file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? file : null, r.PageNo, pics[r.PageNo], r.PageNo > 0 ? dpi : dpi)).ToList();
        var built = TakeoffPdf.Build(specs);
        File.WriteAllBytes(Path.Combine(outDir, "statement_check.pdf"), built.Pdf);
        // small overlay PNGs of the first pages for a quick look
        foreach (var r in results.Take(6))
        {
            var ov = pics[r.PageNo].Clone();
            ov.Tint(r.Highlights.Mask, new Rgb(255, 0, 255), 0.35);
            foreach (var b in r.Highlights.Blobs) ov.Rect(b.X, b.Y, b.W, b.H, b.Kind == HighlightKinds.Stroke ? new Rgb(0xC0, 0, 0) : new Rgb(0, 0x44, 0xDD), 2);
            File.WriteAllBytes(Path.Combine(outDir, $"overlay_p{r.PageNo:000}.png"), ov.FitWithin(2000).ToPng());
        }
        Console.WriteLine($"Wrote statement_check.pdf, claimed_vs_highlighted.csv, pages.csv, overlay_p*.png to {outDir}");
    }

    // ------------------------------------------------------------------ revisions

    private static void Compare(string oldFile, string newFile, Dictionary<string, string> o)
    {
        var outDir = Req(o, "out");
        Directory.CreateDirectory(outDir);
        var dpi = Int(o, "dpi", 150);
        var settings = new DrawingSettings();
        var raster = new PdfiumRasterizer();
        var (oi, _) = SheetImages.Load(oldFile, Int(o, "page", 1), dpi, raster);
        var (ni, _) = SheetImages.Load(newFile, Int(o, "new-page", Int(o, "page", 1)), dpi, raster);
        var lib = o.TryGetValue("library", out var lp) ? LoadLibrary(lp) : new List<DwgSymbol>();
        for (var i = 0; i < lib.Count; i++) if (lib[i].Id == 0) lib[i].Id = i + 1;
        var templates = lib.Where(s => s.TemplatePng != null).Select(s => SymbolTemplate.From(s, dpi)).ToList();
        List<DwgHit> Hits(GrayImage g) => templates.Count == 0 ? new() : TemplateMatcher.Match(g, templates).Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
        var og = oi.ToGray(); var ng = ni.ToGray();
        var res = RevisionComparer.Compare(og, ng, Hits(og), Hits(ng), lib.ToDictionary(s => s.Id), settings);
        Console.WriteLine($"Aligned (score {res.AlignScore:0.00}); changed areas +{res.Added.Count} / -{res.Removed.Count}; symbols +{res.AddedHits.Count} / -{res.RemovedHits.Count}");
        var sb = new StringBuilder(Csv.Line("ROOM", "STAGE", "ITEM", "OLD", "NEW", "DELTA") + "\n");
        foreach (var d in res.Deltas) { sb.AppendLine(Csv.Line(d.Room, d.Stage, d.Item, d.OldQty, d.NewQty, d.Delta)); Console.WriteLine($"  {d.Room,-12} {d.Stage,-9} {d.Item,-10} {d.OldQty,5} -> {d.NewQty,5} ({d.Delta:+0.##;-0.##})"); }
        File.WriteAllText(Path.Combine(outDir, "deltas.csv"), sb.ToString());
        File.WriteAllBytes(Path.Combine(outDir, "compare_overlay.png"), res.Overlay!.FitWithin(4000).ToPng());
        Console.WriteLine($"Wrote compare_overlay.png, deltas.csv to {outDir}");
    }

    // ------------------------------------------------------------------ synthetic demo

    private static void Demo(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        void W(string s) { Console.WriteLine(s); sb.AppendLine(s); }
        var all = new Dictionary<string, (int Tp, int Fp, int Fn)>();
        W("== 1. Template matching on synthetic raster sheets (rotations / mirrors, conduits, labels, noise), 3 seeds");
        foreach (var seed in new[] { 7, 21, 33 })
        {
            var scene = Synthetic.Generate(seed);
            var sheet = Synthetic.Render(scene); Synthetic.Noise(sheet, new Random(seed), 0.001);
            var g = sheet.ToGray();
            var lib = Synthetic.Library(scene, g);
            var hits = TemplateMatcher.Match(g, lib.Select(s => SymbolTemplate.From(s, scene.Dpi)).ToList());
            foreach (var s in Synthetic.Evaluate(scene.Symbols, hits.Select(h => (h.Name, h.Center)), scene.SymbolSize * 0.5))
            {
                var v = all.GetValueOrDefault(s.Type); all[s.Type] = (v.Tp + s.TruePositives, v.Fp + s.FalsePositives, v.Fn + s.FalseNegatives);
            }
            if (seed == 7)
            {
                var ov = sheet.Clone();
                foreach (var h in hits) ov.Circle(h.Center.X, h.Center.Y, h.W * 0.62, TakeoffPdf.DarkRed, 2);
                File.WriteAllBytes(Path.Combine(outDir, "demo_matches.png"), ov.ToPng());
            }
        }
        W($"{"SYMBOL",-12} {"TP",4} {"FP",4} {"FN",4} {"PRECISION",10} {"RECALL",8}");
        foreach (var (t, v) in all) W($"{t,-12} {v.Tp,4} {v.Fp,4} {v.Fn,4} {(double)v.Tp / Math.Max(1, v.Tp + v.Fp),10:P1} {(double)v.Tp / Math.Max(1, v.Tp + v.Fn),8:P1}");
        var T = all.Values.Aggregate((a, b) => (a.Tp + b.Tp, a.Fp + b.Fp, a.Fn + b.Fn));
        W($"{"ALL",-12} {T.Tp,4} {T.Fp,4} {T.Fn,4} {(double)T.Tp / (T.Tp + T.Fp),10:P1} {(double)T.Tp / (T.Tp + T.Fn),8:P1}");

        W("\n== 2. Vector PDF: rasterised with PDFium + matched; dashed tray lines measured from the vector paths");
        {
            var scene = Synthetic.Generate(21);
            var trays = SyntheticFiles.Trays(scene);
            var pdf = Path.Combine(outDir, "demo_sheet.pdf");
            File.WriteAllBytes(pdf, SyntheticFiles.VectorPdf(scene, trays, new Dictionary<int, int> { [0] = 1350 }));
            var (img, dpi) = new PdfiumRasterizer().Render(pdf, 1, scene.Dpi);
            var g = img.ToGray();
            var lib = Synthetic.Library(scene, g);
            var hits = TemplateMatcher.Match(g, lib.Select(s => SymbolTemplate.From(s, dpi)).ToList());
            var sc = Synthetic.Evaluate(scene.Symbols, hits.Select(h => (h.Name, h.Center)), 12);
            int tp = sc.Sum(s => s.TruePositives), fp = sc.Sum(s => s.FalsePositives), fn = sc.Sum(s => s.FalseNegatives);
            W($"PDF raster: TP {tp} FP {fp} FN {fn}  precision {(double)tp / (tp + fp):P1} recall {(double)tp / (tp + fn):P1}");
            var vp = VectorPdfReader.Read(pdf, 1, dpi);
            var mpp = DwgSheet.MetresPerPixel(dpi, vp.ScaleFromText());
            var cls = new DwgLinearClass { Id = 1, Name = "TRAY 300", Targets = "1ST FIX|CABLE TRAY 300|1", StyleKeys = vp.Styles().First(s => s.Style.Colour == SyntheticFiles.TrayMagenta).Style.Key };
            var runs = LinearTakeoff.FromVector(vp, new[] { cls }, mpp);
            var truth = trays.Sum(t => Poly.PolylineLength(t)) * mpp; var got = runs.Sum(r => r.LengthM);
            W($"scale read from title block 1:{vp.ScaleFromText()}; tray truth {truth:0.000} m, measured {got:0.000} m, error {(got - truth) / truth:P3}");
            // raster fallback on the same sheet: trace the magenta tray colour
            var rr = LinearTakeoff.FromRaster(img, new[] { new DwgLinearClass { Id = 2, Name = "TRAY (raster)", ColourHex = "#FF00FF", ColourTolerance = 90 } }, mpp, 40);
            var rgot = rr.Sum(r => r.LengthM);
            W($"raster colour tracing of the dashed tray: {rgot:0.000} m, error {(rgot - truth) / truth:P2} (dashes leave gaps; vector is preferred)");
            // takeoff PDF in the house layout
            var symbols = lib.ToDictionary(s => s.Id);
            var dh = hits.Select(h => new DwgHit { SymbolId = h.SymbolId, SymbolName = h.Name, X = h.X, Y = h.Y, W = h.W, H = h.H, Score = h.Score }).ToList();
            var rooms = new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon)));
            foreach (var h in dh) h.Room = rooms.RoomAt(h.Center);
            var settings = new DrawingSettings();
            foreach (var r in runs) r.Room = "";
            var net = new RouteNetwork(scene.Conduits.SelectMany(c => c.Zip(c.Skip(1))).Concat(trays.SelectMany(c => c.Zip(c.Skip(1)))), 2);
            var lengths = CableLengths.Compute(new CableLengthInput { Network = net, Panel = scene.Conduits[0][0], MetresPerUnit = mpp, MaxSnap = 30, PanelName = "DB-01" },
                dh.Where(h => rooms.RoomAt(h.Center) == "R01").ToList(), symbols, settings);
            TakeoffCounter.NumberMarks(dh, symbols);
            var input = new TakeoffOutputInput
            {
                Sheet = new DwgSheet { SheetNo = "DEMO-101", FileName = "demo_sheet.pdf", Title = "SYNTHETIC 1BR-A" }, TypeLabel = "SYNTHETIC 1BR-A", Revision = 1,
                Hits = dh, Symbols = symbols, Settings = settings, Runs = runs, Classes = new Dictionary<long, DwgLinearClass> { [1] = cls }, Lengths = lengths,
                Picture = img, Dpi = dpi, PdfPath = pdf, PdfPage = 1,
            };
            File.WriteAllBytes(Path.Combine(outDir, "demo_takeoff.pdf"), TakeoffPdf.Build(TakeoffOutput.Pages(input)).Pdf);
            TakeoffOutput.Workbook(Path.Combine(outDir, "demo_takeoff.xlsx"), new[] { input });
            var traced = lengths.Where(l => l.Traceable).ToList();
            W($"cable lengths R01: {traced.Count} of {lengths.Count} points traceable, avg {(traced.Count > 0 ? traced.Average(l => l.TotalM) : 0):0.00} m per point; 15 m groups {CableLengths.LengthGroups(lengths)}");
        }

        W("\n== 3. DXF / DWG: blocks, exploded blocks (geometry signature), layers, rooms");
        foreach (var dwg in new[] { false, true })
        {
            var scene = Synthetic.Generate(9);
            var path = Path.Combine(outDir, dwg ? "demo_plan.dwg" : "demo_plan.dxf");
            var truth = SyntheticFiles.Dxf(scene, path, SyntheticFiles.Trays(scene), dwg);
            var m = CadReader.Read(path);
            var lib = Synthetic.SymbolTypes.Select((t, i) => new DwgSymbol { Id = i + 1, Name = t, System = Synthetic.SystemOf(t), BlockNames = SyntheticFiles.BlockOf(t) }).ToList();
            foreach (var s in lib) s.GeometrySignature = CadTakeoff.LearnFromBlock(m, s.BlockNames)?.Serialize() ?? "";
            var classes = new[] { new DwgLinearClass { Id = 1, Name = "TRAY 300", Layers = "E-TRAY-*", Targets = "1ST FIX|CABLE TRAY 300|1" }, new DwgLinearClass { Id = 2, Name = "CONDUIT 20", Layers = "E-CONDUIT-*", Targets = "1ST FIX|CONDUIT 20|1" } };
            var t = ModelTakeoff.FromCad(m, lib, classes, "A-ROOM");
            var blocks = t.Hits.Count(h => h.Origin == DwgOrigins.Block);
            var geo = t.Hits.Where(h => h.Origin == DwgOrigins.Geometry).Select(h => (h.SymbolName, new PointD(h.Center.X / truth.MillimetresPerPx, scene.Height + h.Center.Y / truth.MillimetresPerPx))).ToList();
            var sc = Synthetic.Evaluate(truth.Exploded, geo, 15);
            int tp = sc.Sum(s => s.TruePositives), fp = sc.Sum(s => s.FalsePositives), fn = sc.Sum(s => s.FalseNegatives);
            double tray = t.Runs.Where(r => r.ClassId == 1).Sum(r => r.LengthM), con = t.Runs.Where(r => r.ClassId == 2).Sum(r => r.LengthM);
            W($"{(dwg ? "DWG" : "DXF")}: blocks {blocks}/{truth.Inserted.Count}; exploded TP {tp} FP {fp} FN {fn}; rooms {t.Rooms.Count}/{scene.Rooms.Count}; " +
              $"tray {tray:0.000}/{truth.TrayM:0.000} m ({(tray - truth.TrayM) / truth.TrayM:P3}); conduit {con:0.000}/{truth.ConduitM:0.000} m ({(con - truth.ConduitM) / truth.ConduitM:P3})");
        }

        W("\n== 4. IFC (Revit export) and Revit add-in JSON");
        {
            var scene = Synthetic.Generate(4);
            var path = Path.Combine(outDir, "demo_model.ifc");
            var truth = SyntheticFiles.Ifc(scene, path);
            var m = IfcModel.Read(path);
            var lib = new List<DwgSymbol>
            {
                new() { Id = 1, Name = "POWER OUTLET", System = "POWER", IfcClasses = "IfcOutlet:POWEROUTLET" }, new() { Id = 2, Name = "DATA OUTLET", System = "DATA", IfcClasses = "IfcOutlet:DATAOUTLET" },
                new() { Id = 3, Name = "LIGHT", System = "LIGHT", IfcClasses = "IfcLightFixture" }, new() { Id = 4, Name = "SWITCH", System = "LIGHT", IfcClasses = "IfcSwitchingDevice" },
                new() { Id = 5, Name = "OTHER", System = "POWER", IfcClasses = "IfcFlowTerminal" },
            };
            var classes = new[] { new DwgLinearClass { Id = 1, Name = "TRAY 300", IfcClasses = "IfcCableCarrierSegment:*300*" }, new DwgLinearClass { Id = 2, Name = "CABLE", IfcClasses = "IfcCableSegment" } };
            var t = ModelTakeoff.FromIfc(m, lib, classes);
            var expected = truth.CountsBySpace.Sum(r => r.Value.Values.Sum());
            var inRoom = t.Hits.Count(h => truth.CountsBySpace.ContainsKey(h.Room));
            W($"IFC: {m.Spaces.Count} spaces, {t.Hits.Count}/{expected} devices ({inRoom} assigned to a space - half by containment, half by footprint), " +
              $"tray {t.Runs.Where(r => r.ClassId == 1).Sum(r => r.LengthM):0.000}/{truth.TrayM:0.000} m, cable {t.Runs.Where(r => r.ClassId == 2).Sum(r => r.LengthM):0.000}/{truth.CableM:0.000} m");
            var json = ModelTakeoff.FromRevitJson(SyntheticFiles.RevitJson(scene), Synthetic.SymbolTypes.Select((x, i) => new DwgSymbol { Id = i + 1, Name = x, System = Synthetic.SystemOf(x) }).ToList(),
                new[] { new DwgLinearClass { Id = 9, Name = "CABLE TRAY 300", System = "TRAY", Size = "300" } });
            W($"Revit JSON: {json.Hits.Count}/{scene.Symbols.Count} points, {json.Rooms.Count} rooms, {json.Runs.Sum(r => r.LengthM):0.00} m tray, unmatched {json.Unmatched.Count}");
        }

        W("\n== 5. Marked-up statement (scanned, skewed, green highlighter) vs clean drawing");
        {
            var scene = Synthetic.Generate(31);
            var clean = Synthetic.Render(scene).ToGray();
            var lib = Synthetic.Library(scene, clean);
            var map = lib.ToDictionary(s => s.Id);
            var hits = TemplateMatcher.Match(clean, lib.Select(s => SymbolTemplate.From(s, 150)).ToList()).Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
            var scan = Synthetic.Markup(scene, 8, share: 0.55, scale: 0.97, rotationDeg: 0.6, shiftX: 25, shiftY: -15);
            var rooms = new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon)));
            var page = StatementVerifier.VerifyPage(new StatementPageInput { Page = scan.Page, Clean = clean, CleanHits = hits, Symbols = map, Rooms = rooms, Settings = new DrawingSettings(), Stage = "1ST FIX", MetresPerPx = scene.MetresPerPixel / 0.97 });
            var sc = Synthetic.Evaluate(scan.Highlighted, page.Symbols.Where(s => s.Highlighted).Select(s => (s.Hit.SymbolName, s.Hit.Center)), 12);
            int tp = sc.Sum(s => s.TruePositives), fp = sc.Sum(s => s.FalsePositives), fn = sc.Sum(s => s.FalseNegatives);
            W($"alignment score {page.AlignScore:0.00}; highlighted symbols TP {tp} FP {fp} FN {fn} (precision {(double)tp / Math.Max(1, tp + fp):P1}, recall {(double)tp / Math.Max(1, tp + fn):P1}); " +
              $"{page.UnmatchedSpots.Count} spots without a symbol; highlighted runs {page.RunLengthM:0.00} m vs truth {scan.HighlightedRunPx * scene.MetresPerPixel:0.00} m (+ margin note)");
            var claimed = page.Highlighted.Select(k => new ClaimedQty(k.Key.Room, k.Key.Stage, k.Key.Item, k.Value + (k.Key.Room == "R01" ? 3 : 0))).ToList();
            var lines = StatementVerifier.Compare(new[] { page }, claimed);
            W($"comparison: {lines.Count} rows, flagged {lines.Count(StatementVerifier.IsFlag)} ({string.Join(", ", lines.Where(StatementVerifier.IsFlag).Select(l => $"{l.Room} {l.Item} {l.Flag} {l.Diff:+0.#;-0.#}"))})");
            File.WriteAllBytes(Path.Combine(outDir, "demo_statement_check.pdf"), TakeoffPdf.Build(new[] { StatementVerifier.Page(page, map, lines, "STATEMENT CHECK - SYNTHETIC - p1", "synthetic_statement.pdf", null, 1, scan.Page, 150) }).Pdf);
        }

        W("\n== 6. Revision compare");
        {
            var scene = Synthetic.Generate(41);
            var oldG = Synthetic.Render(scene).ToGray();
            var lib = Synthetic.Library(scene, oldG);
            var (rev, removed, added) = Synthetic.Revise(scene, 9, 5, 6);
            var newG = Synthetic.Warp(Synthetic.Render(rev), Affine2D.Similarity(1, 0.3, 12, -8), rev.Width + 20, rev.Height + 20).ToGray();
            List<DwgHit> H(GrayImage g) => TemplateMatcher.Match(g, lib.Select(s => SymbolTemplate.From(s, 150)).ToList()).Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score }).ToList();
            var res = RevisionComparer.Compare(oldG, newG, H(oldG), H(newG), lib.ToDictionary(s => s.Id), new DrawingSettings(), new RoomAssigner(scene.Rooms.Select(r => new RoomPolygon(r.Name, r.Polygon))));
            var sa = Synthetic.Evaluate(added, res.AddedHits.Select(h => (h.SymbolName, h.Center)), 15);
            var sr = Synthetic.Evaluate(removed, res.RemovedHits.Select(h => (h.SymbolName, h.Center)), 15);
            W($"alignment {res.AlignScore:0.00}; added symbols {sa.Sum(s => s.TruePositives)}/{added.Count} (FP {sa.Sum(s => s.FalsePositives)}), removed {sr.Sum(s => s.TruePositives)}/{removed.Count} (FP {sr.Sum(s => s.FalsePositives)}); rooms {string.Join(",", res.AffectedRooms)}");
            File.WriteAllBytes(Path.Combine(outDir, "demo_compare.png"), res.Overlay!.ToPng());
        }
        File.WriteAllText(Path.Combine(outDir, "demo_report.txt"), sb.ToString());
    }
}
