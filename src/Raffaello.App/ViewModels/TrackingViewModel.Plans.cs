using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>A level of a building in the plan picker (all its revisions).</summary>
public sealed record TrkPlanLevel(string Building, string Level, string Caption, List<PlanImage> Revisions)
{
    public string Display => $"{Building}  {Level}";
    public string Detail => $"{Caption}  |  {Revisions.Count} revision(s)";
    public override string ToString() => Display;
}

/// <summary>A location shape on the plan, coloured by the chosen measure.</summary>
public sealed class TrkPlanShape
{
    public string Room { get; init; } = "";
    public PointCollection Points { get; init; } = new();
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush Stroke { get; init; } = Brushes.Gray;
    public double StrokeThickness { get; init; } = 1;
    public string Tip { get; init; } = "";
}

/// <summary>Legend entry: a subcontractor colour (click the swatch to change it, untick to hide) or a status colour.</summary>
public sealed partial class TrkLegendItem : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private string _colour = "#A9A9A9";
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _visible = true;
    public bool IsSub { get; init; }
    public Brush Swatch => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colour));
    public Action? Changed { get; set; }
    partial void OnColourChanged(string value) => OnPropertyChanged(nameof(Swatch));
    partial void OnVisibleChanged(bool value) => Changed?.Invoke();
}

/// <summary>TRACKING > PLANS: plan per building x level (revisions interchangeable), shapes coloured by status / remaining / subcontractor.</summary>
public sealed partial class TrackingViewModel
{
    public const string ModeStatus = "STATUS", ModeRemaining = "REMAINING %", ModeSub = "BY SUBCONTRACTOR";
    public string[] ColourModes { get; } = { ModeStatus, ModeRemaining, ModeSub };
    public ObservableCollection<TrkPlanLevel> PlanLevels { get; } = new();
    public ObservableCollection<PlanImage> PlanRevisions { get; } = new();
    public ObservableCollection<TrkPlanShape> PlanShapes { get; } = new();
    public ObservableCollection<TrkLegendItem> PlanLegend { get; } = new();
    public ObservableCollection<string> PlanStageOptions { get; } = new();
    public ObservableCollection<string> PlanItemOptions { get; } = new();
    public ObservableCollection<string> PlanInvoiceOptions { get; } = new();
    [ObservableProperty] private TrkPlanLevel? _selectedPlanLevel;
    [ObservableProperty] private PlanImage? _selectedRevision;
    [ObservableProperty] private ImageSource? _planBitmap;
    [ObservableProperty] private double _planWidthPx = 1000;
    [ObservableProperty] private double _planHeightPx = 700;
    [ObservableProperty] private double _planZoom = 0.5;
    [ObservableProperty] private string _colourMode = ModeStatus;
    [ObservableProperty] private string _planStage = "ALL";
    [ObservableProperty] private string _planItem = "ALL";
    [ObservableProperty] private string _planInvoice = "ALL";
    [ObservableProperty] private string _planInfo = "";
    [ObservableProperty] private string _alignScaleX = "1";
    [ObservableProperty] private string _alignScaleY = "1";
    [ObservableProperty] private string _alignOffsetX = "0";
    [ObservableProperty] private string _alignOffsetY = "0";
    private List<PlanImage>? _planImages;
    private Dictionary<string, string> _subColours = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSubMode => ColourMode == ModeSub;
    public static string PlanColoursFile => Path.Combine(Raffaello.Core.Settings.AppSettings.SettingsFolder, "plan_colours.json");
    public const string PlansFolder = @"D:\RAFFLES MASTER FOLDER\RECON_TOOLS\PLANS";

    partial void OnSelectedPlanLevelChanged(TrkPlanLevel? value)
    {
        Fill(PlanRevisions, value?.Revisions ?? new List<PlanImage>());
        SelectedRevision = PlanRevisions.FirstOrDefault();
    }
    partial void OnSelectedRevisionChanged(PlanImage? value) { LoadPlanPicture(); RenderPlan(); }
    partial void OnColourModeChanged(string value) { OnPropertyChanged(nameof(IsSubMode)); if (value != null) RenderPlan(); }
    partial void OnPlanStageChanged(string value) { if (value != null) RenderPlan(); }
    partial void OnPlanItemChanged(string value) { if (value != null) RenderPlan(); }
    partial void OnPlanInvoiceChanged(string value) { if (value != null) RenderPlan(); }

    private void BuildPlanViewer()
    {
        try { _planImages = Project.LoadPlans(); } catch (Exception) { _planImages = new(); }
        var keep = SelectedPlanLevel?.Display;
        var levels = _planImages.Where(p => InBuilding(p.Building))
            .GroupBy(p => (p.Building, Level: PlanPackage.Split(p.Plan).Level))
            .Select(g => new TrkPlanLevel(g.Key.Building, g.Key.Level, g.OrderByDescending(p => p.Id).First().Caption, g.OrderByDescending(p => p.Id).ToList()))
            .OrderBy(l => l.Building).ThenBy(l => l.Level).ToList();
        Fill(PlanLevels, levels);
        Sync(PlanStageOptions, new[] { "ALL" }.Concat(StageOptions));
        Sync(PlanItemOptions, new[] { "ALL" }.Concat(ItemOptions));
        Sync(PlanInvoiceOptions, new[] { "ALL" }.Concat(_claims.Select(c => c.InvoiceNo).Distinct().OrderBy(n => n).Select(n => n.ToString())));
        _subColours = SubPalette.Assign(Project.Snapshot.Claims.Select(c => c.Subcontractor), LoadSavedColours());
        SelectedPlanLevel = PlanLevels.FirstOrDefault(l => l.Display == keep) ?? PlanLevels.FirstOrDefault();
        if (PlanLevels.Count == 0)
        {
            PlanShapes.Clear(); PlanBitmap = null;
            PlanInfo = $"No plans for {BuildingFilter ?? "these buildings"} yet - IMPORT PLAN PACKAGE (RECON_TOOLS\\PLANS) or import the tracker on Rooms & ledger.";
        }
    }

    private static Dictionary<string, string> LoadSavedColours()
    {
        try { return File.Exists(PlanColoursFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PlanColoursFile)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    private void LoadPlanPicture()
    {
        var plan = SelectedRevision;
        if (plan?.Png is not { Length: > 0 } png) { PlanBitmap = null; return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            PlanBitmap = bmp;
            PlanWidthPx = plan.Width > 0 ? plan.Width : bmp.PixelWidth;
            PlanHeightPx = plan.Height > 0 ? plan.Height : bmp.PixelHeight;
            PlanZoom = Math.Clamp(1100.0 / Math.Max(1, PlanWidthPx), 0.05, 2);
        }
        catch (Exception ex) { PlanBitmap = null; PlanInfo = "Cannot show the picture: " + ex.Message; }
    }

    private static Color C(string hex, byte alpha = 255)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = alpha;
        return c;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private void RenderPlan()
    {
        PlanShapes.Clear();
        var plan = SelectedRevision;
        if (plan is null) return;
        var shapes = Project.Snapshot.RoomShapes.Where(s => s.Plan == plan.Plan && (s.Building == plan.Building || s.Building.Length == 0)).ToList();
        bool KeyOk(string stage, string item) => (PlanStage is null or "ALL" || stage == PlanStage) && (PlanItem is null or "ALL" || item == PlanItem);
        var inv = PlanInvoice is { } pi && int.TryParse(pi, out var n) ? n : (int?)null;
        var balByRoom = _bal.Values.Where(b => KeyOk(b.Stage, b.Item)).GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var claimsByRoom = _claims.Where(c => !c.Rework && !LedgerRules.IsNotCompared(c) && KeyOk(c.Stage, c.Item) && (inv is null || c.InvoiceNo == inv))
            .GroupBy(c => c.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var visible = PlanLegend.Where(l => l.IsSub && !l.Visible).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stateCounts = new Dictionary<string, int>();
        var w = PlanWidthPx; var h = PlanHeightPx;
        foreach (var s in shapes)
        {
            var code = balByRoom.ContainsKey(s.Room) || claimsByRoom.ContainsKey(s.Room) || s.Description.Length == 0 ? s.Room : s.Description;
            var bs = balByRoom.GetValueOrDefault(code) ?? new List<RoomBalance>();
            var cl = claimsByRoom.GetValueOrDefault(code) ?? new List<ClaimLine>();
            var cap = bs.Where(b => b.HasCap).Sum(b => b.ProjectQty);
            var claimed = bs.Sum(b => b.Claimed);
            var over = bs.Any(b => b.HasCap && b.IsOver);
            var pending = cl.Any(LengthCheck.IsPending);
            var bySub = cl.GroupBy(c => c.Subcontractor).Select(g => (Sub: g.Key, Qty: g.Sum(c => c.Qty))).Where(x => x.Qty > 0).OrderByDescending(x => x.Qty).ToList();
            Brush fill;
            var stroke = Frozen(new SolidColorBrush(C("#707070")));
            double thick = 1;
            string state;
            switch (ColourMode)
            {
                case ModeSub:
                {
                    var shown = bySub.Where(x => !visible.Contains(x.Sub)).ToList();
                    foreach (var x in shown) subCounts[x.Sub] = subCounts.GetValueOrDefault(x.Sub) + 1;
                    state = shown.Count == 0 ? "-" : string.Join(" + ", shown.Select(x => x.Sub));
                    if (shown.Count == 0) fill = Brushes.Transparent;
                    else if (shown.Count == 1) fill = Frozen(new SolidColorBrush(C(_subColours.GetValueOrDefault(shown[0].Sub, "#A9A9A9"), 160)));
                    else
                    {
                        var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(10 * shown.Count, 10 * shown.Count), SpreadMethod = GradientSpreadMethod.Repeat };
                        for (var i = 0; i < shown.Count; i++)
                        {
                            var col = C(_subColours.GetValueOrDefault(shown[i].Sub, "#A9A9A9"), 170);
                            g.GradientStops.Add(new GradientStop(col, (double)i / shown.Count));
                            g.GradientStops.Add(new GradientStop(col, (double)(i + 1) / shown.Count));
                        }
                        fill = Frozen(g);
                    }
                    break;
                }
                case ModeRemaining:
                {
                    var frac = cap <= 0 ? -1 : Math.Clamp((cap - claimed) / cap, 0, 1);
                    state = frac < 0 ? "no total" : $"{frac:P0} remaining";
                    fill = frac < 0 ? Brushes.Transparent : Frozen(new SolidColorBrush(Color.FromArgb(150, (byte)(60 + 170 * frac), (byte)(180 - 140 * frac), 75)));
                    break;
                }
                default:
                {
                    state = over ? TrkStatus.OverCap : pending ? "15 M PENDING" : TrkStatus.Of(cap, claimed, false);
                    fill = state switch
                    {
                        TrkStatus.OverCap => Frozen(new SolidColorBrush(C("#E6194B", 150))),
                        "15 M PENDING" => Frozen(new SolidColorBrush(C("#911EB4", 140))),
                        TrkStatus.Complete => Frozen(new SolidColorBrush(C("#3CB44B", 140))),
                        TrkStatus.InProgress => Frozen(new SolidColorBrush(C("#FFB000", 140))),
                        TrkStatus.NotStarted => Frozen(new SolidColorBrush(C("#A9A9A9", 90))),
                        TrkStatus.NoCap => Frozen(new SolidColorBrush(C("#42D4F4", 120))),
                        _ => Brushes.Transparent,
                    };
                    stateCounts[state] = stateCounts.GetValueOrDefault(state) + 1;
                    break;
                }
            }
            if (over) { stroke = Frozen(new SolidColorBrush(C("#000000"))); thick = 4; }
            var tip = $"{s.Room}{(code != s.Room ? $"  (in {code})" : "")}  {s.ShapeName}\nTOTAL {cap:#,0.##}  |  CLAIMED {claimed:#,0.##}  |  REMAINING {cap - claimed:#,0.##}{(over ? "  |  OVER" : "")}{(pending ? "  |  15 m pending" : "")}" +
                      (bySub.Count > 0 ? "\n" + string.Join("\n", bySub.Select(x => $"{x.Sub}: {x.Qty:#,0.##}")) : "\nnot claimed") + "\nClick = ROOM";
            foreach (var poly in s.Polygons.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var pts = new PointCollection();
                foreach (var pt in poly.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var xy = pt.Split(',');
                    if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                        pts.Add(new Point(x * w, y * h));
                }
                pts.Freeze();
                PlanShapes.Add(new TrkPlanShape { Room = code, Points = pts, Fill = fill, Stroke = stroke, StrokeThickness = thick, Tip = tip });
            }
        }
        // legend
        if (ColourMode == ModeSub)
        {
            var names = _subColours.Keys.Where(k => _claims.Any(c => c.Subcontractor.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (!PlanLegend.All(l => l.IsSub) || PlanLegend.Count != names.Count || PlanLegend.Any(l => !names.Contains(l.Name)))
            {
                PlanLegend.Clear();
                foreach (var nm in names) PlanLegend.Add(new TrkLegendItem { Name = nm, Colour = _subColours[nm], IsSub = true, Changed = RenderPlan });
            }
            foreach (var l in PlanLegend) l.Count = subCounts.GetValueOrDefault(l.Name);
        }
        else
        {
            PlanLegend.Clear();
            if (ColourMode == ModeStatus)
                foreach (var (name, col) in new[] { (TrkStatus.OverCap, "#E6194B"), ("15 M PENDING", "#911EB4"), (TrkStatus.Complete, "#3CB44B"), (TrkStatus.InProgress, "#FFB000"), (TrkStatus.NotStarted, "#A9A9A9"), (TrkStatus.NoCap, "#42D4F4") })
                    PlanLegend.Add(new TrkLegendItem { Name = name, Colour = col, Count = stateCounts.GetValueOrDefault(name) });
            else
            {
                PlanLegend.Add(new TrkLegendItem { Name = "0 % remaining (done)", Colour = "#3CB44B" });
                PlanLegend.Add(new TrkLegendItem { Name = "100 % remaining", Colour = "#E62B4B" });
            }
        }
        PlanInfo = $"{plan.Building}  {plan.Plan}  |  {plan.Caption}  |  {shapes.Count} shapes  |  {PlanWidthPx:0} x {PlanHeightPx:0} px  |  {ColourMode}" +
                   (PlanStage is "ALL" && PlanItem is "ALL" ? "" : $"  |  {PlanStage} | {PlanItem}");
    }

    [RelayCommand]
    private void CycleColour(TrkLegendItem? item)
    {
        if (item is null || !item.IsSub) return;
        item.Colour = SubPalette.Next(item.Colour);
        _subColours[item.Name] = item.Colour;
        try
        {
            var saved = LoadSavedColours();
            saved[item.Name] = item.Colour;
            Directory.CreateDirectory(Path.GetDirectoryName(PlanColoursFile)!);
            File.WriteAllText(PlanColoursFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
        RenderPlan();
    }

    [RelayCommand] private void ShapeClick(string? room) { if (!string.IsNullOrEmpty(room)) OpenRoom(room); }
    [RelayCommand] private void ZoomIn() => PlanZoom = Math.Min(4, PlanZoom * 1.25);
    [RelayCommand] private void ZoomOut() => PlanZoom = Math.Max(0.03, PlanZoom / 1.25);

    [RelayCommand]
    private async Task ImportPlanPackage()
    {
        var file = Ctx.Dialogs.OpenFile("Plan package - pick its manifest.json (RECON_TOOLS\\PLANS\\<building>)", "Plan package|manifest.json;*.json|All files|*.*");
        if (file is null) return;
        PlanPackageResult r;
        try { r = await Task.Run(() => PlanPackage.Read(file, BuildingFilter)); }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ PLAN PACKAGE", ex.Message, ToastKind.Error); return; }
        if (r.Levels.Count == 0) { Ctx.Toasts.Show("NO PLANS IN THE PACKAGE", string.Join("\n", r.Issues.Take(5)), ToastKind.Warn, 10); return; }
        if (!Ctx.Dialogs.Confirm("Import plan package", $"{r.Summary}\n\n{string.Join("\n", r.Levels.Select(l => $"{l.Level} {l.Name}: {l.Width} x {l.Height} px, {l.Shapes.Count} shapes"))}\n\n{string.Join("\n", r.Issues.Take(6).Select(i => "- " + i))}\n\nImport? The same level + revision is replaced; other revisions stay selectable.")) return;
        (int Plans, int Shapes) res = default;
        _planImages = null;
        await Ctx.Data.WriteAsync(p => res = PlanPackage.Commit(r, p.Store), Ctx.Toasts);
        Ctx.Toasts.Show("PLAN PACKAGE IMPORTED", $"{res.Plans} plans, {res.Shapes} shapes", ToastKind.Good, 6);
    }

    /// <summary>A new revision picture for the selected level (PNG / JPG); the current revision's shapes are copied - align them if the picture moved.</summary>
    [RelayCommand]
    private async Task ReplacePlan()
    {
        if (SelectedPlanLevel is not { } level) { Ctx.Toasts.Show("PICK A PLAN FIRST", kind: ToastKind.Info); return; }
        var file = Ctx.Dialogs.OpenFile($"New picture for {level.Display} (PNG / JPG, high resolution)", "Pictures|*.png;*.jpg;*.jpeg|All files|*.*");
        if (file is null) return;
        byte[] png;
        try
        {
            var frame = BitmapDecoder.Create(new Uri(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(frame);
            using var ms = new MemoryStream();
            enc.Save(ms);
            png = ms.ToArray();
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ THE PICTURE", ex.Message, ToastKind.Error); return; }
        var rev = "REV-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
        var from = SelectedRevision?.Plan;
        if (!Ctx.Dialogs.Confirm("Replace plan", $"{level.Display}: new revision {rev} from {Path.GetFileName(file)}.\nThe {SelectedRevision?.Name} shapes are copied - use ALIGN if the new picture is shifted or scaled.\nOlder revisions stay in the revision list.")) return;
        _planImages = null;
        await Ctx.Data.WriteAsync(p => PlanPackage.AddRevision(p.Store, level.Building, level.Level, rev, png, $"{level.Caption} ({Path.GetFileName(file)})", from), Ctx.Toasts, "NEW PLAN REVISION SAVED");
    }

    [RelayCommand]
    private async Task AlignShapes()
    {
        if (SelectedRevision is not { } plan) return;
        double D(string s, double def) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        var (sx, sy, ox, oy) = (D(AlignScaleX, 1), D(AlignScaleY, 1), D(AlignOffsetX, 0), D(AlignOffsetY, 0));
        if (sx <= 0 || sy <= 0) return;
        if (!Ctx.Dialogs.Confirm("Align shapes", $"{plan.Building} {plan.Plan}: scale {sx} x {sy}, offset {ox}, {oy} (fractions of the picture).\nAll shapes of this revision move. Continue?")) return;
        var n = 0;
        await Ctx.Data.WriteAsync(p => n = PlanPackage.Align(p.Store, plan.Building, plan.Plan, sx, sy, ox, oy), Ctx.Toasts);
        Ctx.Toasts.Show("SHAPES ALIGNED", $"{n} shapes", ToastKind.Good, 4);
        AlignScaleX = AlignScaleY = "1"; AlignOffsetX = AlignOffsetY = "0";
    }
}