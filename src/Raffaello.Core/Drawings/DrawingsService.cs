using System.Diagnostics;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Drawings;

/// <summary>Options of one takeoff run.</summary>
public sealed class TakeoffRunOptions
{
    public double? Threshold { get; set; }
    public RectD? Region { get; set; }
    public bool Vision { get; set; }
    /// <summary>Also trace coloured lines on the raster for linear classes with a colour (scans).</summary>
    public bool RasterLines { get; set; } = true;
    public IProgress<string>? Progress { get; set; }
    public CancellationToken Cancel { get; set; }
}

public sealed record TakeoffRunResult(DwgTakeoff Takeoff, List<DwgHit> Hits, List<DwgRun> Runs, List<string> Notes);

/// <summary>
/// [drawings] Workflow of the drawings module for the app and the CLI: import sheets (PDF, image, DWG / DXF, IFC, Revit JSON),
/// build the symbol library from boxes, run takeoffs (template matching / vector lines / CAD / IFC / JSON), assign rooms
/// (calibrated tracker shapes or imported boundaries), propose PROJECT QTY changes and write the takeoff PDF + workbook.
/// </summary>
public sealed class DrawingsService
{
    public IDrawingStore Store { get; }
    public DrawingSettings Settings { get; }
    public IDrawingRasterizer Rasterizer { get; }
    public IVisionReader? Vision { get; set; }
    private readonly Func<IProjectStore>? _project;
    private readonly Dictionary<long, (ColorImage Image, int Dpi)> _pictures = new();

    public DrawingsService(IDrawingStore store, DrawingSettings settings, IDrawingRasterizer? rasterizer = null, Func<IProjectStore>? project = null)
    {
        Store = store; Settings = settings; Rasterizer = rasterizer ?? new PdfiumRasterizer(); _project = project;
    }

    public IProjectStore Project => _project?.Invoke() ?? throw new InvalidOperationException("No project data source.");

    // ------------------------------------------------------------------ sheets

    public DwgSheet ImportSheet(string path, string building, string level, string sheetNo, string revision, int page = 1, int? dpi = null, string title = "", string role = "DRAWING")
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var kind = DwgSourceKinds.FromPath(path);
        var s = new DwgSheet
        {
            Building = building, Level = level, SheetNo = sheetNo.Length > 0 ? sheetNo : Path.GetFileNameWithoutExtension(path), Revision = revision, Title = title,
            SourceKind = kind, FilePath = Path.GetFullPath(path), FileName = Path.GetFileName(path), Sha256 = SheetImages.Sha256(path), Page = page,
            Dpi = dpi ?? Settings.DefaultDpi, Role = role, ImportedAt = Store.Clock(),
        };
        if (kind == DwgSourceKinds.Pdf)
        {
            var vp = VectorPdfReader.Read(path, page, s.Dpi);
            s.PixelWidth = (int)Math.Round(vp.WidthPx); s.PixelHeight = (int)Math.Round(vp.HeightPx);
            var n = vp.ScaleFromText();
            if (n > 0) { s.ScaleDenominator = n; s.MetresPerUnit = DwgSheet.MetresPerPixel(s.Dpi, n); s.ScaleSource = "TITLE BLOCK"; }
        }
        else if (kind is DwgSourceKinds.Dxf or DwgSourceKinds.Dwg)
        {
            var m = CadReader.Read(path);
            s.MetresPerUnit = m.MetresPerUnit; s.ScaleSource = m.MetresPerUnit > 0 ? "INSUNITS" : "";
        }
        else if (kind is DwgSourceKinds.Ifc or DwgSourceKinds.RevitJson) { s.MetresPerUnit = 1; s.ScaleSource = "MODEL"; }
        return Store.SaveSheet(s);
    }

    /// <summary>Scale from two clicked points and the real distance (metres).</summary>
    public DwgSheet CalibrateScale(DwgSheet s, PointD a, PointD b, double metres)
    {
        s.MetresPerUnit = LinearTakeoff.CalibrateScale(a, b, metres);
        s.ScaleSource = "CALIBRATED";
        if (DwgSourceKinds.IsRaster(s.SourceKind) && s.Dpi > 0) s.ScaleDenominator = Math.Round(s.MetresPerUnit * 1000 / (25.4 / s.Dpi));
        return Store.SaveSheet(s);
    }

    /// <summary>Raster of a PDF / image sheet (cached), or the picture of a model sheet.</summary>
    public (ColorImage Image, int Dpi) Picture(DwgSheet s)
    {
        if (_pictures.TryGetValue(s.Id, out var p)) return p;
        p = DwgSourceKinds.IsRaster(s.SourceKind) ? SheetImages.Load(s.FilePath, s.Page, s.Dpi, Rasterizer) : (ModelOf(s).Render(2400, out _), 100);
        if (_pictures.Count > 4) _pictures.Clear();
        _pictures[s.Id] = p;
        return p;
    }

    public ModelTakeoff ModelOf(DwgSheet s)
    {
        var syms = Store.Symbols().Where(x => x.Active).ToList();
        var cls = Store.LinearClasses().Where(x => x.Active).ToList();
        return s.SourceKind switch
        {
            DwgSourceKinds.Dxf or DwgSourceKinds.Dwg => ModelTakeoff.FromCad(CadReader.Read(s.FilePath), syms, cls),
            DwgSourceKinds.Ifc => ModelTakeoff.FromIfc(IfcModel.Read(s.FilePath), syms, cls),
            DwgSourceKinds.RevitJson => ModelTakeoff.FromRevitJson(File.ReadAllText(s.FilePath), syms, cls),
            _ => throw new InvalidOperationException($"{s.FileName} is not a model."),
        };
    }

    // ------------------------------------------------------------------ library

    /// <summary>Adds a symbol from a box the user drew around one example on the sheet.</summary>
    public DwgSymbol DefineSymbol(DwgSheet sheet, RectD box, DwgSymbol template)
    {
        var (img, dpi) = Picture(sheet);
        var crop = img.ToInkGray().Crop((int)box.X, (int)box.Y, Math.Max(4, (int)box.W), Math.Max(4, (int)box.H));
        template.TemplatePng = TemplateMatcher.Trim(TemplateMatcher.Isolate(crop)).ToPng();
        template.TemplateDpi = dpi;
        template.SourceSheetId = sheet.Id;
        template.SourceBox = $"{box.X:0},{box.Y:0},{box.W:0},{box.H:0}";
        if (template.Item.Length == 0) template.Item = template.System;
        if (template.ColourHex.Length == 0) template.ColourHex = Rgb.Palette(Store.Symbols().Count).Hex;
        return Store.SaveSymbol(template);
    }

    // ------------------------------------------------------------------ rooms

    public RoomAssigner RoomsFor(DwgSheet s) => new(Store.Rooms(s.Id).Select(RoomBoundaries.FromEntity));

    /// <summary>Aligns the tracker plan's room shapes to the sheet from 2-3 clicked pairs and stores them as the sheet's rooms.</summary>
    public (DwgCalibration Calibration, int Rooms) Calibrate(DwgSheet s, string plan, IReadOnlyList<(PointD Sheet, PointD Plan)> pairs)
    {
        var (t, res) = RoomBoundaries.Calibrate(pairs);
        var cal = Store.SaveCalibration(new DwgCalibration { SheetId = s.Id, Plan = plan, Pairs = RoomBoundaries.FormatPairs(pairs), Transform = t.Serialize(), Residual = res });
        var shapes = Project.All<RoomShape>().Where(r => r.Building.Equals(s.Building, StringComparison.OrdinalIgnoreCase));
        var rooms = RoomBoundaries.FromTracker(shapes, plan, t, s.Level);
        Store.ReplaceRooms(s.Id, rooms.Select(r => RoomBoundaries.ToEntity(r, s.Id, "TRACKER " + plan)).ToList(), $"Sheet {s.Label}: {rooms.Count} rooms from tracker plan {plan}");
        s.PlanCode = plan; Store.SaveSheet(s);
        return (cal, rooms.Count);
    }

    public int ImportRooms(DwgSheet s, string path)
    {
        var rooms = RoomBoundaries.Import(path, s.PixelWidth, s.PixelHeight);
        Store.ReplaceRooms(s.Id, rooms.Select(r => RoomBoundaries.ToEntity(r, s.Id, Path.GetFileName(path))).ToList(), $"Sheet {s.Label}: {rooms.Count} room boundaries imported");
        return rooms.Count;
    }

    // ------------------------------------------------------------------ takeoff

    public async Task<TakeoffRunResult> RunTakeoffAsync(DwgSheet s, TakeoffRunOptions? options = null)
    {
        var o = options ?? new TakeoffRunOptions();
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();
        var symbols = Store.Symbols().Where(x => x.Active && (x.Building.Length == 0 || x.Building.Equals(s.Building, StringComparison.OrdinalIgnoreCase))).ToList();
        var classes = Store.LinearClasses().Where(x => x.Active).ToList();
        List<DwgHit> hits; List<DwgRun> runs; string source;
        var rooms = RoomsFor(s);
        if (DwgSourceKinds.IsModel(s.SourceKind))
        {
            var m = ModelOf(s);
            hits = m.Hits; runs = m.Runs; source = s.SourceKind is DwgSourceKinds.Ifc ? DwgOrigins.Ifc : s.SourceKind is DwgSourceKinds.RevitJson ? DwgOrigins.Json : DwgOrigins.Cad;
            notes.AddRange(m.Notes); notes.AddRange(m.Unmatched.Take(50).Select(u => "unmatched: " + u));
            if (rooms.IsEmpty && m.Rooms.Count > 0)
            {
                Store.ReplaceRooms(s.Id, m.Rooms.Select(r => RoomBoundaries.ToEntity(r, s.Id, s.SourceKind)).ToList(), $"Sheet {s.Label}: {m.Rooms.Count} rooms from the model");
                rooms = new RoomAssigner(m.Rooms);
            }
            if (s.MetresPerUnit <= 0 && m.MetresPerUnit > 0) { s.MetresPerUnit = m.MetresPerUnit; Store.SaveSheet(s); }
        }
        else
        {
            var (img, dpi) = Picture(s);
            var templates = symbols.Where(x => x.TemplatePng is { Length: > 0 }).Select(x => SymbolTemplate.From(x, dpi)).ToList();
            var thr = o.Threshold ?? Settings.Threshold;
            o.Progress?.Report($"matching {templates.Count} symbols on {s.Label} ({img.Width}x{img.Height} px)");
            var found = TemplateMatcher.Match(img.ToInkGray(), templates, new MatchOptions
            {
                Threshold = thr, Scales = Settings.ParsedScales(), Rotations = Settings.Rotations, Mirror = Settings.Mirror, Region = o.Region, Progress = o.Progress, Cancel = o.Cancel,
            });
            hits = found.Select(d => new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = Math.Round(d.Score, 3), Rotation = d.Rotation, Mirrored = d.Mirrored, Origin = DwgOrigins.Template }).ToList();
            runs = new List<DwgRun>();
            source = DwgOrigins.Template;
            VectorPage? vp = null;
            if (s.SourceKind == DwgSourceKinds.Pdf)
            {
                vp = VectorPdfReader.Read(s.FilePath, s.Page, dpi);
                foreach (var h in hits) h.MountingHeightM = vp.HeightNear(h.Center, Math.Max(h.W, h.H) * 2.5);
                runs.AddRange(LinearTakeoff.FromVector(vp, classes, s.MetresPerUnit));
                if (vp.Segments.Count == 0) notes.Add("No vector lines on this page (a scan) - lengths come from coloured-line tracing or manual routes.");
            }
            if (o.RasterLines && (vp is null || vp.Segments.Count == 0))
                runs.AddRange(LinearTakeoff.FromRaster(img, classes.Where(c => string.IsNullOrWhiteSpace(c.StyleKeys)), s.MetresPerUnit));
            if (o.Vision && Vision is { IsAvailable: true })
            {
                var refs = symbols.Where(x => x.TemplatePng != null).ToDictionary(x => x.Id, x => GrayImage.FromPng(x.TemplatePng!));
                var n = await VisionHitVerifier.VerifyAsync(Vision, hits, refs, img, symbols.ToDictionary(x => x.Id), thr, Settings.VisionMargin, o.Cancel).ConfigureAwait(false);
                notes.Add($"Claude vision checked low-confidence hits: {n} rejected (restore them in the review if wrong).");
            }
            if (s.MetresPerUnit <= 0 && runs.Count > 0) notes.Add("The sheet has no scale - lengths are in pixels until the scale is set (title block or 2-point calibration).");
        }
        if (!rooms.IsEmpty) foreach (var h in hits.Where(h => h.Room.Length == 0)) h.Room = rooms.RoomAt(h.Center);
        foreach (var r in runs.Where(r => r.Room.Length == 0 && !rooms.IsEmpty))
        {
            var split = rooms.SplitLength(Poly.ParsePoints(r.Points));
            if (split.Count == 1) r.Room = split.Keys.First();
        }
        var symMap = symbols.ToDictionary(x => x.Id);
        TakeoffCounter.NumberMarks(hits, symMap);
        var t = Store.SaveTakeoff(new DwgTakeoff { SheetId = s.Id, Source = source, Threshold = o.Threshold ?? Settings.Threshold, Scales = Settings.Scales, Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), Notes = string.Join("\n", notes) }, hits, runs);
        return new TakeoffRunResult(t, hits, runs, notes);
    }

    /// <summary>PROJECT QTY proposals (counts + lengths) of a takeoff.</summary>
    public List<QtyProposal> Proposals(DwgSheet s, DwgTakeoff t)
    {
        var symbols = Store.Symbols().ToDictionary(x => x.Id);
        var classes = Store.LinearClasses().ToDictionary(x => x.Id);
        var counts = TakeoffCounter.Count(Store.Hits(t.Id), symbols, Settings);
        var lengths = TakeoffCounter.Lengths(Store.Runs(t.Id), classes, RoomsFor(s), s.MetresPerUnit);
        var current = Project.All<RoomQty>();
        var res = QtyDiff.Build(counts, current, s.Building);
        if (lengths.Count > 0) res.AddRange(QtyDiff.Build(lengths, current, s.Building, "m"));
        return res;
    }

    public QtyDiff.ApplyResult Apply(DwgSheet s, DwgTakeoff t, IReadOnlyList<QtyProposal> rows)
    {
        var r = QtyDiff.Apply(Project, Store, rows, s, t.Id);
        if (r.Updated + r.Inserted > 0) { t.Status = "APPLIED"; Store.SaveReview(t, Store.Hits(t.Id), Store.Runs(t.Id)); }
        return r;
    }

    /// <summary>Takeoff PDF (house layout) + companion workbook for one takeoff.</summary>
    public (string Pdf, string Xlsx, List<string> Notes) WriteOutputs(DwgSheet s, DwgTakeoff t, string folder, string typeLabel = "", int revision = 0, List<PointLength>? lengths = null)
    {
        Directory.CreateDirectory(folder);
        var (pic, dpi) = Picture(s);
        var symbols = Store.Symbols().ToDictionary(x => x.Id);
        var input = new TakeoffOutputInput
        {
            Sheet = s, TypeLabel = typeLabel, Revision = revision, Hits = Store.Hits(t.Id), Symbols = symbols, Settings = Settings,
            Runs = Store.Runs(t.Id), Classes = Store.LinearClasses().ToDictionary(c => c.Id), Lengths = lengths ?? new(),
            Picture = pic, Dpi = dpi, PdfPath = s.SourceKind == DwgSourceKinds.Pdf ? s.FilePath : null, PdfPage = s.Page,
            SheetToPicture = DwgSourceKinds.IsModel(s.SourceKind) ? ModelTransform(s) : Affine2D.Identity,
        };
        var stem = $"QS_TAKEOFF_{Safe(typeLabel.Length > 0 ? typeLabel : s.SheetNo)}_REV{revision:00}";
        var pdf = Path.Combine(folder, stem + ".pdf");
        var built = TakeoffPdf.Build(TakeoffOutput.Pages(input));
        File.WriteAllBytes(pdf, built.Pdf);
        var xlsx = Path.Combine(folder, stem + ".xlsx");
        TakeoffOutput.Workbook(xlsx, new[] { input }, _project is null ? null : Proposals(s, t));
        return (pdf, xlsx, built.Notes);
    }

    private Affine2D ModelTransform(DwgSheet s) { ModelOf(s).Render(2400, out var t); return t; }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
