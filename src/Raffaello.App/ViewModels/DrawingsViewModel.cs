using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;
using Raffaello.Core.Export;

namespace Raffaello.App.ViewModels;

// =====================================================================================================
//  [drawings] DRAWINGS: takeoff (counts + lengths), statement verification, revision compare
// =====================================================================================================

/// <summary>A marker on the review canvas (display pixels).</summary>
public sealed partial class HitMarker : ObservableObject
{
    public required DwgHit Hit { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Size { get; init; }
    [ObservableProperty] private Brush _stroke = Brushes.DarkRed;
    [ObservableProperty] private bool _visible = true;
    public string Label => Hit.MarkNo > 0 ? Hit.MarkNo.ToString(CultureInfo.InvariantCulture) : "";
    public string Tip => $"{Hit.SymbolName} #{Hit.MarkNo}  score {Hit.Score:0.00}  {Hit.Status}  {Hit.Room}{(Hit.Note.Length > 0 ? "\n" + Hit.Note : "")}";
}

public sealed record RunLine(PathGeometry Geometry, Brush Stroke, string Tip);
public sealed record CountRow(string Room, string Stage, string Item, double Qty, string Unit);

public sealed partial class DrawingsViewModel : PageViewModel
{
    private readonly DrawingsService _svc;
    private readonly AconexAutomationService _aconex;
    private readonly Raffaello.Core.Materials.MaterialsSettings _matSettings;
    private ColorImage? _picture;
    private double _k = 1;   // sheet units -> display pixels
    private Affine2D _sheetToDisplay = Affine2D.Identity;
    private readonly List<(PointD Sheet, PointD Plan)> _pairs = new();
    private PointD? _pendingSheetPoint;
    private PointD? _scaleA;

    public static readonly string[] Modes = { "REVIEW", "ADD MISSED", "DEFINE SYMBOL", "CALIBRATE ROOMS", "SET SCALE", "PICK LINE STYLE" };
    public static readonly string[] Systems = { "POWER", "LIGHT", "DATA", "GRMS", "AV", "EMERGENCY LIGHT", "FIRE", "BMS", "OTHER" };
    public static readonly string[] Mounts = { "W", "C" };
    public string[] ModeOptions => Modes;
    public string[] SystemOptions => Systems;
    public string[] MountOptions => Mounts;
    public string[] SourceKinds => DwgSourceKinds.All;

    public DrawingsViewModel(PageContext ctx, DrawingsService svc, AconexAutomationService aconex, Raffaello.Core.Materials.MaterialsSettings matSettings) : base(ctx)
    {
        _svc = svc; _aconex = aconex; _matSettings = matSettings;
    }

    public override string Key => "Drawings";
    public override string Title => "DRAWINGS";
    public override string Subtitle => "Symbol takeoff, lengths, statement highlights, revision compare";
    protected override bool UsesFilter => false;

    public ObservableCollection<DwgSheet> Sheets { get; } = new();
    public ObservableCollection<DwgSymbol> Symbols { get; } = new();
    public ObservableCollection<DwgLinearClass> LinearClasses { get; } = new();
    public ObservableCollection<HitMarker> Markers { get; } = new();
    public ObservableCollection<RunLine> RunLines { get; } = new();
    public ObservableCollection<CountRow> RoomTotals { get; } = new();
    public ObservableCollection<QtyProposal> Proposals { get; } = new();
    public ObservableCollection<string> PlanCodes { get; } = new();
    public ObservableCollection<DwgStatementLine> StatementLines { get; } = new();
    public ObservableCollection<DwgRevisionDelta> Deltas { get; } = new();

    [ObservableProperty] private DwgSheet? _selectedSheet;
    [ObservableProperty] private DwgSymbol? _selectedSymbol;
    [ObservableProperty] private DwgTakeoff? _takeoff;
    [ObservableProperty] private BitmapSource? _sheetImage;
    [ObservableProperty] private double _imageWidth = 1000;
    [ObservableProperty] private double _imageHeight = 700;
    [ObservableProperty] private string _mode = "REVIEW";
    [ObservableProperty] private double _confidenceMin = 0.70;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Import a drawing (PDF, PNG, DWG, DXF, IFC or Revit JSON), box one example of each symbol, then RUN TAKEOFF.";
    [ObservableProperty] private bool _useVision;
    // import fields
    [ObservableProperty] private string _newLevel = "";
    [ObservableProperty] private string _newSheetNo = "";
    [ObservableProperty] private string _newRevision = "0";
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private int _newPage = 1;
    // symbol fields
    [ObservableProperty] private string _symbolName = "";
    [ObservableProperty] private string _symbolSystem = "POWER";
    [ObservableProperty] private string _symbolMount = "W";
    [ObservableProperty] private string _symbolTag = "";
    [ObservableProperty] private bool _symbolIsLight;
    [ObservableProperty] private double _symbolSecondFix = 1;
    [ObservableProperty] private bool _symbolExcluded;
    // linear class fields
    [ObservableProperty] private string _className = "";
    [ObservableProperty] private string _classSize = "";
    [ObservableProperty] private string _classTargets = "1ST FIX|CABLE TRAY|1";
    [ObservableProperty] private string _scaleMetres = "1.0";
    [ObservableProperty] private string? _selectedPlan;
    // statement fields
    [ObservableProperty] private string _statementFile = "";
    [ObservableProperty] private string _statementPages = "";
    [ObservableProperty] private string _statementSub = "";
    [ObservableProperty] private string _statementInvoice = "";
    [ObservableProperty] private string _statementRooms = "";
    [ObservableProperty] private string _statementStage = "1ST FIX";
    [ObservableProperty] private string _statementSummary = "";
    // compare fields
    [ObservableProperty] private DwgSheet? _oldSheet;
    [ObservableProperty] private DwgSheet? _newSheet;
    [ObservableProperty] private BitmapSource? _compareImage;
    [ObservableProperty] private string _compareSummary = "";
    private RevisionCompareResult? _compare;

    public string TakeoffCaption => Takeoff is null ? "no takeoff yet" : $"#{Takeoff.Id} {Takeoff.Source} {Takeoff.RunAt:dd MMM HH:mm}  {Takeoff.Hits} symbols, {Takeoff.Runs} runs  ({Takeoff.Status})";
    public string ScaleCaption => SelectedSheet is null ? "" : SelectedSheet.MetresPerUnit > 0 ? $"scale {(SelectedSheet.ScaleDenominator > 0 ? "1:" + SelectedSheet.ScaleDenominator.ToString("0", CultureInfo.InvariantCulture) : "")} ({SelectedSheet.ScaleSource}), {SelectedSheet.MetresPerUnit:0.#####} m/unit" : "scale unknown - SET SCALE";

    protected override void Refresh()
    {
        var keep = SelectedSheet?.Id;
        Sheets.Clear();
        foreach (var s in _svc.Store.Sheets().Where(s => InBuilding(s.Building))) Sheets.Add(s);
        Symbols.Clear();
        foreach (var s in _svc.Store.Symbols()) Symbols.Add(s);
        LinearClasses.Clear();
        foreach (var c in _svc.Store.LinearClasses()) LinearClasses.Add(c);
        PlanCodes.Clear();
        foreach (var p in Project.Snapshot.RoomShapes.Select(r => r.Plan).Distinct().OrderBy(p => p)) PlanCodes.Add(p);
        SelectedSheet = Sheets.FirstOrDefault(s => s.Id == keep) ?? Sheets.FirstOrDefault();
    }

    partial void OnSelectedSheetChanged(DwgSheet? value)
    {
        Markers.Clear(); RunLines.Clear(); RoomTotals.Clear(); Proposals.Clear(); _pairs.Clear();
        SheetImage = null; _picture = null; Takeoff = null;
        if (value is null) return;
        try
        {
            var (img, _) = _svc.Picture(value);
            _picture = img;
            var disp = img.FitWithin(5000);
            _k = (double)disp.Width / img.Width;
            _sheetToDisplay = DwgSourceKinds.IsModel(value.SourceKind) ? ModelTransform(value) : Affine2D.ScaleOnly(_k, _k);
            SheetImage = ToBitmap(disp);
            ImageWidth = disp.Width; ImageHeight = disp.Height;
            Takeoff = _svc.Store.Takeoffs(value.Id).FirstOrDefault();
            LoadTakeoff();
        }
        catch (Exception ex) { Status = "Could not open the sheet: " + ex.Message; }
        OnPropertyChanged(nameof(ScaleCaption));
    }

    private Affine2D ModelTransform(DwgSheet s)
    {
        _svc.ModelOf(s).Render(2400, out var t);
        return Affine2D.ScaleOnly(_k, _k).After(t);
    }

    partial void OnTakeoffChanged(DwgTakeoff? value) => OnPropertyChanged(nameof(TakeoffCaption));
    partial void OnConfidenceMinChanged(double value) { foreach (var m in Markers) m.Visible = m.Hit.Score >= value || m.Hit.Origin != DwgOrigins.Template; RebuildTotals(); }

    private static BitmapSource ToBitmap(ColorImage img)
    {
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Rgb24, null, img.Data, img.Width * 3);
        bmp.Freeze();
        return bmp;
    }

    private List<DwgHit> _hits = new();
    private List<DwgRun> _runs = new();

    private void LoadTakeoff()
    {
        Markers.Clear(); RunLines.Clear();
        _hits = Takeoff is null ? new() : _svc.Store.Hits(Takeoff.Id);
        _runs = Takeoff is null ? new() : _svc.Store.Runs(Takeoff.Id);
        var symbols = _svc.Store.Symbols().ToDictionary(s => s.Id);
        foreach (var h in _hits) Markers.Add(Marker(h, symbols));
        var classes = _svc.Store.LinearClasses().ToDictionary(c => c.Id);
        foreach (var r in _runs)
        {
            var pts = Poly.ParsePoints(r.Points).Select(_sheetToDisplay.Apply).ToList();
            if (pts.Count < 2) continue;
            var fig = new PathFigure { StartPoint = new System.Windows.Point(pts[0].X, pts[0].Y) };
            foreach (var p in pts.Skip(1)) fig.Segments.Add(new LineSegment(new System.Windows.Point(p.X, p.Y), true));
            var col = classes.TryGetValue(r.ClassId, out var c) && c.ColourHex.Length > 0 ? c.ColourHex : "#E3AE12";
            RunLines.Add(new RunLine(new PathGeometry(new[] { fig }), Brush(col), $"{r.ClassName} {r.LengthM:0.00} m {r.Room}"));
        }
        RebuildTotals();
    }

    private HitMarker Marker(DwgHit h, IReadOnlyDictionary<long, DwgSymbol> symbols)
    {
        var c = _sheetToDisplay.Apply(h.Center);
        var size = Math.Max(10, Math.Max(h.W, h.H) * _sheetToDisplay.Scale * 1.3);
        var m = new HitMarker { Hit = h, X = c.X - size / 2, Y = c.Y - size / 2, Size = size, Visible = h.Score >= ConfidenceMin || h.Origin != DwgOrigins.Template };
        m.Stroke = StrokeOf(h, symbols);
        return m;
    }

    private Brush StrokeOf(DwgHit h, IReadOnlyDictionary<long, DwgSymbol> symbols)
    {
        if (!DwgHitStatus.Counts(h.Status)) return Brushes.Gray;
        if (h.Status == DwgHitStatus.Added) return Brushes.DodgerBlue;
        return symbols.TryGetValue(h.SymbolId, out var s) && s.ColourHex.Length > 0 ? Brush(s.ColourHex) : Brush("#8B0000");
    }

    private static Brush Brush(string hex)
    {
        try { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; } catch (Exception) { return Brushes.DarkRed; }
    }

    private IEnumerable<DwgHit> CountedHits => _hits.Where(h => DwgHitStatus.Counts(h.Status) && (h.Score >= ConfidenceMin || h.Origin != DwgOrigins.Template));

    private void RebuildTotals()
    {
        RoomTotals.Clear();
        if (SelectedSheet is null) return;
        var symbols = _svc.Store.Symbols().ToDictionary(s => s.Id);
        foreach (var (k, v) in TakeoffCounter.Count(CountedHits, symbols, _svc.Settings).OrderBy(k => k.Key.Room).ThenBy(k => k.Key.Stage))
            RoomTotals.Add(new CountRow(k.Room, k.Stage, k.Item, v, "no"));
        foreach (var (k, v) in TakeoffCounter.Lengths(_runs, _svc.Store.LinearClasses().ToDictionary(c => c.Id), _svc.RoomsFor(SelectedSheet), SelectedSheet.MetresPerUnit))
            RoomTotals.Add(new CountRow(k.Room, k.Stage, k.Item, Math.Round(v, 2), "m"));
    }

    // ------------------------------------------------------------------ canvas clicks (sheet units come from the view)

    /// <summary>Display pixel -> sheet units.</summary>
    public PointD ToSheet(double x, double y) => _sheetToDisplay.Inverse().Apply(new PointD(x, y));

    public void OnCanvasClick(double x, double y, HitMarker? marker)
    {
        if (SelectedSheet is null) return;
        var p = ToSheet(x, y);
        switch (Mode)
        {
            case "REVIEW" when marker != null:
                marker.Hit.Status = DwgHitStatus.Counts(marker.Hit.Status) ? DwgHitStatus.Removed : DwgHitStatus.Confirmed;
                marker.Stroke = StrokeOf(marker.Hit, _svc.Store.Symbols().ToDictionary(s => s.Id));
                RebuildTotals();
                Status = $"{marker.Hit.SymbolName} #{marker.Hit.MarkNo} -> {marker.Hit.Status} (SAVE REVIEW to keep)";
                break;
            case "ADD MISSED":
                if (SelectedSymbol is null) { Status = "Pick the symbol to add in the library list first."; return; }
                var size = _hits.Where(h => h.SymbolId == SelectedSymbol.Id).Select(h => Math.Max(h.W, h.H)).DefaultIfEmpty(20 / Math.Max(_k, 1e-6)).Average();
                var hit = new DwgHit { SymbolId = SelectedSymbol.Id, SymbolName = SelectedSymbol.Name, X = p.X - size / 2, Y = p.Y - size / 2, W = size, H = size, Score = 1, Status = DwgHitStatus.Added, Origin = DwgOrigins.Manual,
                    Room = _svc.RoomsFor(SelectedSheet).RoomAt(p) };
                _hits.Add(hit);
                Markers.Add(Marker(hit, _svc.Store.Symbols().ToDictionary(s => s.Id)));
                RebuildTotals();
                Status = $"Added {hit.SymbolName} in {hit.Room} (SAVE REVIEW to keep)";
                break;
            case "CALIBRATE ROOMS":
                _pendingSheetPoint = p;
                Status = $"Sheet point {p} - now click the same corner on the tracker plan (right panel).";
                break;
            case "SET SCALE":
                if (_scaleA is null) { _scaleA = p; Status = "Click the second end of the known dimension."; }
                else
                {
                    if (!double.TryParse(ScaleMetres, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) || m <= 0) { Status = "Type the real distance in metres first."; _scaleA = null; return; }
                    SelectedSheet = _svc.CalibrateScale(SelectedSheet, _scaleA.Value, p, m);
                    _scaleA = null;
                    OnPropertyChanged(nameof(ScaleCaption));
                    Status = $"Scale set: {SelectedSheet?.MetresPerUnit:0.#####} m per unit.";
                }
                break;
            case "PICK LINE STYLE":
                if (SelectedSheet.SourceKind != DwgSourceKinds.Pdf) { Status = "Line styles come from vector PDFs; for DWG / DXF use layers, for scans a colour."; return; }
                var vp = VectorPdfReader.Read(SelectedSheet.FilePath, SelectedSheet.Page, SelectedSheet.Dpi);
                var (seg, d, _) = vp.Nearest(p);
                if (seg is null || d > 10) { Status = "No vector line near the click."; return; }
                var name = ClassName.Trim().Length > 0 ? ClassName.Trim() : $"LINE {seg.Style.Colour.Hex}";
                var cls = _svc.Store.LinearClasses().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                          ?? new DwgLinearClass { Name = name, System = "TRAY", Size = ClassSize, Targets = ClassTargets, ColourHex = seg.Style.Colour.Hex };
                cls.StyleKeys = string.Join(";", (cls.StyleKeys + ";" + seg.Style.Key).Split(';', StringSplitOptions.RemoveEmptyEntries).Distinct());
                _svc.Store.SaveLinearClass(cls);
                Status = $"{cls.Name} = lines {seg.Style} (all such lines on the page are measured at the next takeoff).";
                Ctx.Data.RaiseChanged();
                break;
        }
    }

    /// <summary>Plan panel click (normalised 0..1 plan coordinates) - completes a calibration pair.</summary>
    public void OnPlanClick(double u, double v)
    {
        if (SelectedSheet is null || _pendingSheetPoint is null || SelectedPlan is null) { Status = "Choose the tracker plan, then click a corner on the drawing first."; return; }
        _pairs.Add((_pendingSheetPoint.Value, new PointD(u, v)));
        _pendingSheetPoint = null;
        if (_pairs.Count < 2) { Status = "1 pair - click a second (and ideally a third) corner on the drawing, then on the plan."; return; }
        try
        {
            var (cal, n) = _svc.Calibrate(SelectedSheet, SelectedPlan, _pairs.ToList());
            Status = $"{_pairs.Count} pairs, residual {cal.Residual:0.0} units: {n} rooms aligned to the drawing. Add a third pair to refine, or RUN TAKEOFF.";
            RebuildTotals();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public void OnBoxDrawn(double x0, double y0, double x1, double y1)
    {
        if (SelectedSheet is null || Mode != "DEFINE SYMBOL") return;
        if (SymbolName.Trim().Length == 0) { Status = "Type the symbol name (SOCKET, TWIN SOCKET, DOWNLIGHT ...) before drawing the box."; return; }
        var a = ToSheet(Math.Min(x0, x1), Math.Min(y0, y1)); var b = ToSheet(Math.Max(x0, x1), Math.Max(y0, y1));
        try
        {
            var s = _svc.DefineSymbol(SelectedSheet, new RectD(a.X, a.Y, b.X - a.X, b.Y - a.Y), new DwgSymbol
            {
                Name = SymbolName, System = SymbolSystem, Item = SymbolSystem, Tag = SymbolTag, Mount = SymbolMount, IsLightFitting = SymbolIsLight,
                SecondFixFactor = SymbolSecondFix, Excluded = SymbolExcluded, Building = "",
            });
            Status = $"Symbol {s.Name} ({s.System}, {(s.Mount == "C" ? "ceiling" : "wall")}) added to the library.";
            SymbolName = "";
            Ctx.Data.RaiseChanged();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void ImportSheet()
    {
        var path = Ctx.Dialogs.OpenFile("Drawing (PDF, PNG, DWG, DXF, IFC, Revit JSON)", "Drawings|*.pdf;*.png;*.dwg;*.dxf;*.ifc;*.json|All files|*.*");
        if (path is null) return;
        try
        {
            var s = _svc.ImportSheet(path, WorkingBuilding, NewLevel, NewSheetNo, NewRevision, Math.Max(1, NewPage), title: NewTitle);
            Ctx.Toasts.Show("SHEET IMPORTED", $"{s.Label} - {s.SourceKind}, {ScaleText(s)}");
            Ctx.Data.RaiseChanged();
            SelectedSheet = Sheets.FirstOrDefault(x => x.Id == s.Id);
        }
        catch (Exception ex) { Ctx.Toasts.Show("IMPORT FAILED", ex.Message, ToastKind.Error); }
    }

    private static string ScaleText(DwgSheet s) => s.MetresPerUnit > 0 ? $"scale {s.ScaleSource}" : "scale unknown";

    [RelayCommand]
    private async Task RunTakeoff()
    {
        if (SelectedSheet is null) return;
        IsBusy = true;
        var sheet = SelectedSheet;
        try
        {
            if (UseVision) _svc.Vision = new ClaudeVisionReader(_matSettings.CloudReading, Project.Settings.AnthropicApiKey, Project.Settings.AnthropicModel);
            var progress = new Progress<string>(s => Status = s);
            var res = await Task.Run(() => _svc.RunTakeoffAsync(sheet, new TakeoffRunOptions { Threshold = ConfidenceMin, Vision = UseVision, Progress = progress }));
            Takeoff = res.Takeoff;
            LoadTakeoff();
            Status = $"{res.Hits.Count} symbols, {res.Runs.Count} runs in {res.Takeoff.Seconds:0.0} s. {string.Join(" ", res.Notes.Take(3))}";
        }
        catch (Exception ex) { Ctx.Toasts.Show("TAKEOFF FAILED", ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void SaveReview()
    {
        if (Takeoff is null) return;
        foreach (var h in _hits.Where(h => h.Origin == DwgOrigins.Template && h.Score < ConfidenceMin && DwgHitStatus.Counts(h.Status))) h.Status = DwgHitStatus.Removed;
        TakeoffCounter.NumberMarks(_hits, _svc.Store.Symbols().ToDictionary(s => s.Id));
        try { _svc.Store.SaveReview(Takeoff, _hits, _runs); Takeoff = _svc.Store.Takeoffs(Takeoff.SheetId).FirstOrDefault(); LoadTakeoff(); Status = "Review saved."; }
        catch (Exception ex) { Ctx.Toasts.Show("SAVE FAILED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void ProposeQty()
    {
        if (SelectedSheet is null || Takeoff is null) return;
        Proposals.Clear();
        foreach (var p in _svc.Proposals(SelectedSheet, Takeoff).Where(p => p.Status != "SAME")) Proposals.Add(p);
        Status = $"{Proposals.Count} PROJECT QTY differences - tick the ones to apply, then APPLY TICKED.";
    }

    [RelayCommand]
    private void ApplyQty()
    {
        if (SelectedSheet is null || Takeoff is null || Proposals.Count == 0) return;
        var n = Proposals.Count(p => p.Accept);
        if (n == 0) { Status = "Nothing ticked - nothing changed."; return; }
        if (!Ctx.Dialogs.Confirm("PROJECT QTY", $"Apply {n} ticked PROJECT QTY changes from {SelectedSheet.Label}? The others are recorded as not applied.")) return;
        try
        {
            var r = _svc.Apply(SelectedSheet, Takeoff, Proposals.ToList());
            Ctx.Toasts.Show("PROJECT QTY UPDATED", r.Summary);
            Ctx.Project.Reload();
            Ctx.Data.RaiseChanged();
            ProposeQty();
        }
        catch (Exception ex) { Ctx.Toasts.Show("NOT APPLIED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void TickAll() { foreach (var p in Proposals.ToList()) p.Accept = p.Room != RoomAssigner.Unassigned; var copy = Proposals.ToList(); Proposals.Clear(); foreach (var p in copy) Proposals.Add(p); }

    [RelayCommand]
    private void WriteTakeoffPdf()
    {
        if (SelectedSheet is null || Takeoff is null) return;
        var folder = Ctx.Dialogs.PickFolder("Folder for the takeoff PDF + workbook");
        if (folder is null) return;
        try
        {
            var (pdf, xlsx, notes) = _svc.WriteOutputs(SelectedSheet, Takeoff, folder, SelectedSheet.Title, _svc.Store.Takeoffs(SelectedSheet.Id).Count);
            Ctx.Toasts.Show("TAKEOFF WRITTEN", $"{Path.GetFileName(pdf)} + {Path.GetFileName(xlsx)} {string.Join(" ", notes)}");
            DialogService.OpenWithShell(pdf);
        }
        catch (Exception ex) { Ctx.Toasts.Show("EXPORT FAILED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void ImportRooms()
    {
        if (SelectedSheet is null) return;
        var path = Ctx.Dialogs.OpenFile("Room boundaries (CSV / JSON)", "Rooms|*.csv;*.json|All files|*.*");
        if (path is null) return;
        try { Status = $"{_svc.ImportRooms(SelectedSheet, path)} rooms imported."; RebuildTotals(); }
        catch (Exception ex) { Ctx.Toasts.Show("ROOMS NOT IMPORTED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private void DeleteSymbol()
    {
        if (SelectedSymbol is null || !Ctx.Dialogs.Confirm("Library", $"Remove {SelectedSymbol.Name} from the library?")) return;
        _svc.Store.DeleteSymbol(SelectedSymbol);
        Ctx.Data.RaiseChanged();
    }

    [RelayCommand]
    private void DeleteSheet()
    {
        if (SelectedSheet is null || !Ctx.Dialogs.Confirm("Drawings", $"Delete {SelectedSheet.Label} and its takeoffs? (PROJECT QTY is not touched)")) return;
        _svc.Store.DeleteSheet(SelectedSheet);
        Ctx.Data.RaiseChanged();
    }

    // ------------------------------------------------------------------ statement verification

    [RelayCommand]
    private void BrowseStatement()
    {
        var p = Ctx.Dialogs.OpenFile("Marked-up statement PDF", "PDF|*.pdf|PNG|*.png");
        if (p != null) StatementFile = p;
    }

    [RelayCommand]
    private async Task VerifyStatement()
    {
        if (!File.Exists(StatementFile)) { Status = "Pick the statement PDF first."; return; }
        IsBusy = true;
        try
        {
            var clean = SelectedSheet;
            var symbols = _svc.Store.Symbols().ToDictionary(s => s.Id);
            var cleanHits = Takeoff is null ? null : _svc.Store.Hits(Takeoff.Id);
            var (cleanImg, dpi) = clean is not null && DwgSourceKinds.IsRaster(clean.SourceKind) ? _svc.Picture(clean) : (null, _svc.Settings.DefaultDpi);
            var rooms = clean is null ? null : _svc.RoomsFor(clean);
            var claimed = StatementSub.Trim().Length == 0 ? new List<ClaimedQty>()
                : StatementVerifier.FromLedger(Project.Snapshot.Claims, StatementSub.Trim(), int.TryParse(StatementInvoice, out var inv) ? inv : null);
            var file = StatementFile; var pagesSpec = StatementPages; var roomList = StatementRooms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); var stage = StatementStage;
            var results = await Task.Run(() =>
            {
                var count = file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? _svc.Rasterizer.PageCount(file) : 1;
                var pages = ParsePages(pagesSpec, count);
                var list = new List<(StatementPageResult R, ColorImage Img)>();
                foreach (var p in pages)
                {
                    var (img, pdpi) = SheetImages.Load(file, p, dpi, _svc.Rasterizer);
                    list.Add((StatementVerifier.VerifyPage(new StatementPageInput
                    {
                        Page = img, PageNo = p, Dpi = pdpi, Clean = cleanImg?.ToGray(), CleanHits = cleanImg is null ? null : cleanHits,
                        Templates = cleanImg is null ? symbols.Values.Where(s => s.TemplatePng != null).Select(s => SymbolTemplate.From(s, pdpi)).ToList() : null,
                        Symbols = symbols, Rooms = rooms, RoomList = roomList, Settings = _svc.Settings, Stage = stage,
                        Highlight = new HighlightOptions { Colours = HighlightColour.ParseList(_svc.Settings.HighlightColours) },
                        MetresPerPx = clean?.MetresPerUnit ?? 0,
                    }), img));
                }
                return list;
            });
            var lines = StatementVerifier.Compare(results.Select(r => r.R), claimed);
            StatementLines.Clear();
            foreach (var l in lines) StatementLines.Add(l);
            var check = _svc.Store.SaveStatementCheck(new DwgStatementCheck
            {
                Subcontractor = StatementSub, StatementNo = Path.GetFileNameWithoutExtension(file), InvoiceNo = int.TryParse(StatementInvoice, out var i2) ? i2 : 0, FilePath = file,
                FileName = Path.GetFileName(file), Sha256 = SheetImages.Sha256(file), Pages = pagesSpec, SheetId = clean?.Id ?? 0, Stage = stage, Colours = _svc.Settings.HighlightColours,
                HighlightSpots = results.Sum(r => r.R.Highlights.Spots.Count()), HighlightedSymbols = results.Sum(r => r.R.Symbols.Count(s => s.Highlighted)),
                UnmatchedSpots = results.Sum(r => r.R.UnmatchedSpots.Count), HighlightedRunsM = results.Sum(r => r.R.RunLengthM), Flags = lines.Count(StatementVerifier.IsFlag),
            }, lines);
            StatementSummary = $"{results.Count} pages: {check.HighlightedSymbols} highlighted symbols, {check.HighlightSpots} highlight spots ({check.UnmatchedSpots} without a library symbol), runs {check.HighlightedRunsM:0.0} m, {check.Flags} differences flagged.";
            var folder = Path.Combine(Path.GetDirectoryName(file) ?? ".", "RAFFAELLO_CHECK");
            Directory.CreateDirectory(folder);
            var pdfOut = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + "_CHECK.pdf");
            File.WriteAllBytes(pdfOut, TakeoffPdf.Build(results.Select(r => StatementVerifier.Page(r.R, symbols, lines, $"STATEMENT CHECK - {StatementSub} - p{r.R.PageNo}", Path.GetFileName(file),
                file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? file : null, r.R.PageNo, r.Img, dpi)).ToList()).Pdf);
            Ctx.Toasts.Show("STATEMENT CHECKED", $"{check.Flags} differences - {Path.GetFileName(pdfOut)}");
        }
        catch (Exception ex) { Ctx.Toasts.Show("CHECK FAILED", ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    private static List<int> ParsePages(string spec, int count)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(1, count).ToList();
        var res = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-');
            if (!int.TryParse(ab[0], out var a)) continue;
            var b = ab.Length > 1 && int.TryParse(ab[1], out var bb) ? bb : a;
            for (var p = Math.Max(1, a); p <= Math.Min(b, count); p++) res.Add(p);
        }
        return res;
    }

    // ------------------------------------------------------------------ revision compare

    [RelayCommand]
    private async Task CompareRevisions()
    {
        if (OldSheet is null || NewSheet is null || OldSheet.Id == NewSheet.Id) { Status = "Pick two different revisions."; return; }
        IsBusy = true;
        try
        {
            var oldS = OldSheet; var newS = NewSheet;
            var symbols = _svc.Store.Symbols().ToDictionary(s => s.Id);
            var oldT = _svc.Store.Takeoffs(oldS.Id).FirstOrDefault(); var newT = _svc.Store.Takeoffs(newS.Id).FirstOrDefault();
            if (oldT is null || newT is null) { Status = "Run a takeoff on both revisions first (the symbol counts are compared)."; return; }
            var res = await Task.Run(() => RevisionComparer.Compare(_svc.Picture(oldS).Image.ToGray(), _svc.Picture(newS).Image.ToGray(), _svc.Store.Hits(oldT.Id), _svc.Store.Hits(newT.Id), symbols, _svc.Settings, _svc.RoomsFor(oldS)));
            _compare = res;
            Deltas.Clear();
            foreach (var d in res.Deltas) Deltas.Add(d);
            CompareImage = res.Overlay is null ? null : ToBitmap(res.Overlay.FitWithin(4000));
            var cmp = _svc.Store.SaveCompare(new DwgRevisionCompare
            {
                OldSheetId = oldS.Id, NewSheetId = newS.Id, AddedRegions = res.Added.Count, RemovedRegions = res.Removed.Count, AddedSymbols = res.AddedHits.Count, RemovedSymbols = res.RemovedHits.Count,
                Transform = res.NewToOld.Serialize(), AlignScore = res.AlignScore, Summary = $"rooms {string.Join(", ", res.AffectedRooms)}",
            }, res.Deltas);
            CompareSummary = $"Aligned (score {res.AlignScore:0.00}). Changed areas +{res.Added.Count} / -{res.Removed.Count}; symbols +{res.AddedHits.Count} / -{res.RemovedHits.Count}; affected rooms: {string.Join(", ", res.AffectedRooms)}";
            _lastCompare = cmp;
        }
        catch (Exception ex) { Ctx.Toasts.Show("COMPARE FAILED", ex.Message, ToastKind.Error); }
        finally { IsBusy = false; }
    }

    private DwgRevisionCompare? _lastCompare;

    [RelayCommand]
    private void CompareToProposals()
    {
        if (_compare is null || NewSheet is null) return;
        Proposals.Clear();
        foreach (var p in RevisionComparer.Proposals(_compare, Project.Snapshot.RoomQtys, NewSheet.Building)) Proposals.Add(p);
        Status = $"{Proposals.Count} PROJECT QTY differences from the revision - tick and APPLY TICKED (TAKEOFF tab).";
        SelectedSheet = NewSheet;
    }

    [RelayCommand]
    private void CreateVariation()
    {
        if (_compare is null || OldSheet is null || NewSheet is null || _lastCompare is null) return;
        try
        {
            var v = RevisionComparer.CreateVariationDraft(_aconex.Variations, _compare.Deltas, OldSheet, NewSheet);
            _lastCompare.VariationId = v.Id;
            _svc.Store.UpdateCompare(_lastCompare);
            Ctx.Toasts.Show("VARIATION DRAFT", $"{v.Number} created with {_compare.Deltas.Count} changes - price it in VARIATIONS / EI.");
            Ctx.Data.RaiseChanged();
        }
        catch (Exception ex) { Ctx.Toasts.Show("NOT CREATED", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (RoomTotals.Count == 0) yield break;
        yield return new ExportSheet
        {
            Name = "TAKEOFF", Title = SelectedSheet?.Label,
            Columns = new() { new ExportColumn("ROOM"), new ExportColumn("STAGE"), new ExportColumn("ITEM"), new ExportColumn("QTY", ColumnKind.Number), new ExportColumn("UNIT") },
            Rows = RoomTotals.Select(r => new object?[] { r.Room, r.Stage, r.Item, r.Qty, r.Unit }).ToList(),
        };
    }

    /// <summary>The tracker plan image for the calibration panel.</summary>
    public BitmapSource? PlanImageFor(string? plan)
    {
        var p = Project.LoadPlans().FirstOrDefault(x => x.Plan.Equals(plan ?? "", StringComparison.OrdinalIgnoreCase));
        if (p?.Png is null) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(p.Png); bmp.EndInit(); bmp.Freeze();
        return bmp;
    }

    [ObservableProperty] private BitmapSource? _planImage;
    partial void OnSelectedPlanChanged(string? value) => PlanImage = PlanImageFor(value);
}
