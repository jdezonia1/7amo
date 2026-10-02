using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Plans;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed class PlanRoomShape
{
    public string Room { get; init; } = "";
    public Geometry Geometry { get; init; } = Geometry.Empty;
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush Stroke { get; init; } = Brushes.Black;
    public double StrokeThickness { get; init; } = 1;
    public string Tip { get; init; } = "";
    public double LabelX { get; init; }
    public double LabelY { get; init; }
}

public sealed record LegendItem(string Label, Brush Swatch);

public sealed class PlanLevelTab
{
    public required PlanImage Plan { get; init; }
    public string Label => $"{Plan.Plan}  {Plan.Caption}";
    public override string ToString() => Label;
}

/// <summary>Level plans with every room drawn from the tracker PLANS shapes, coloured by status / subcontractor / % used / pending checks.</summary>
public sealed partial class PlanViewModel : PageViewModel
{
    public const double PlanWidth = 2000;
    public const string All = "ALL";

    public PlanViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Plan";
    public override string Title => "PLAN VIEW";
    public override string Subtitle => "Every room on its level plan - colour by status, subcontractor, % used or pending checks; click a room for its balance";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;   // [phase6] the building switcher drives it

    public ObservableCollection<PlanLevelTab> Levels { get; } = new();
    public ObservableCollection<PlanRoomShape> Shapes { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<string> RoomsWithoutShape { get; } = new();
    public ObservableCollection<string> StageOptions { get; } = new();
    public ObservableCollection<string> ItemOptions { get; } = new();
    public ObservableCollection<string> SubOptions { get; } = new();
    public ObservableCollection<string> InvoiceOptions { get; } = new();
    public ObservableCollection<BalanceRow> Balances { get; } = new();
    public ObservableCollection<ClaimRow> Lines { get; } = new();
    public string[] ColourModes { get; } = { "STATUS", "SUBCONTRACTOR", "% USED", "PENDING CHECKS" };

    [ObservableProperty] private PlanLevelTab? _level;
    [ObservableProperty] private string _colourMode = "STATUS";
    [ObservableProperty] private string _stage = All;
    [ObservableProperty] private string _item = All;
    [ObservableProperty] private string _sub = All;
    [ObservableProperty] private string _invoice = All;
    [ObservableProperty] private bool _showLabels = true;
    [ObservableProperty] private ImageSource? _planImage;
    [ObservableProperty] private double _planHeight = 1000;
    [ObservableProperty] private string _selectedRoom = "";
    [ObservableProperty] private string _roomTitle = "Click a room";
    [ObservableProperty] private string _roomCaption = "";
    [ObservableProperty] private string _roomChecks = "";
    [ObservableProperty] private string _countText = "";
    /// <summary>[phase6] Empty state: what to do when there is nothing to draw.</summary>
    [ObservableProperty] private string _emptyHint = "";

    private List<PlanImage>? _plans;
    private Dictionary<string, RoomPlanInfo> _info = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RoomBalance> _balances = new();

    partial void OnLevelChanged(PlanLevelTab? value) => Draw();
    partial void OnColourModeChanged(string value) => Draw();
    partial void OnStageChanged(string value) => Recompute();
    partial void OnItemChanged(string value) => Recompute();
    partial void OnSubChanged(string value) => Recompute();
    partial void OnInvoiceChanged(string value) => Recompute();
    partial void OnSelectedRoomChanged(string value) { LoadRoom(); Draw(); }

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        try { _plans = Project.LoadPlans().Where(p => p.Png != null && InBuilding(p.Building)).OrderBy(p => p.Plan).ToList(); } catch { _plans = new(); }
        var keep = Level?.Plan.Plan;
        Levels.Clear();
        foreach (var p in _plans) Levels.Add(new PlanLevelTab { Plan = p });
        Sync(StageOptions, new[] { All }.Concat(s.RoomQtys.Select(q => q.Stage).Concat(s.Claims.Select(c => c.Stage)).Distinct().OrderBy(x => x)));
        Sync(ItemOptions, new[] { All }.Concat(s.RoomQtys.Select(q => q.Item).Concat(s.Claims.Select(c => c.Item)).Where(x => x.Length > 0).Distinct().OrderBy(x => x)));
        Sync(SubOptions, new[] { All }.Concat(s.Claims.Select(c => c.Subcontractor).Distinct().OrderBy(x => x)));
        Sync(InvoiceOptions, new[] { All }.Concat(s.Claims.Select(c => c.InvoiceNo).Where(n => n > 0).Distinct().OrderBy(n => n).Select(n => n.ToString(CultureInfo.InvariantCulture))));
        RoomsWithoutShape.Clear();
        var shaped = s.RoomShapes.Select(r => r.Room).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in s.Rooms.Where(r => InBuilding(r.Building) && !shaped.Contains(r.Code)).OrderBy(r => r.Code)) RoomsWithoutShape.Add(r.Code);
        Level = Levels.FirstOrDefault(l => l.Plan.Plan == keep) ?? Levels.FirstOrDefault();
        EmptyHint = Levels.Count > 0 ? "" : s.Rooms.Count == 0
            ? "No rooms yet. Import the tracker on ROOMS & LEDGER (the PLANS sheet gives the level images and room shapes)."
            : $"No plan images for {(BuildingFilter ?? "this building")}. The tracker's PLANS sheet provides them; rooms are still listed on the right.";
        Recompute();
    }

    private PlanFilter Filter() => new(Stage == All ? null : Stage, Item == All ? null : Item, Sub == All ? null : Sub,
        int.TryParse(Invoice, out var n) ? n : null);

    private void Recompute()
    {
        var s = Project.Snapshot;
        _info = RoomStatusCalc.Compute(s, Filter());
        var f = Filter();
        _balances = LedgerRules.Balances(s.RoomQtys.Where(q => f.Matches(q.Stage, q.Item)),
            s.Claims.Where(c => f.Matches(c.Stage, c.Item) && (f.Subcontractor is null || c.Subcontractor == f.Subcontractor) && (f.UpToInvoice is null || c.InvoiceNo <= f.UpToInvoice)));
        CountText = string.Join("   ", RoomStatusKinds.All.Select(k => $"{k} {_info.Values.Count(i => i.Status == k && i.Room != null)}"));
        LoadRoom();
        Draw();
    }

    private static Brush B(string hex, byte alpha = 255)
    {
        var c = (Color)ColorConverter.ConvertFromString("#" + hex);
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private void Draw()
    {
        Shapes.Clear();
        Legend.Clear();
        var plan = Level?.Plan;
        if (plan?.Png is null) { PlanImage = null; return; }
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(plan.Png); bmp.EndInit(); bmp.Freeze();
        PlanImage = bmp;
        PlanHeight = plan.Width <= 0 ? 1000 : PlanWidth * plan.Height / plan.Width;
        var subColours = RoomStatusCalc.SubColours(_info.Values.Select(i => i.DominantSub));
        foreach (var shape in Project.Snapshot.RoomShapes.Where(r => r.Plan.Equals(plan.Plan, StringComparison.OrdinalIgnoreCase)))
        {
            var polys = RoomStatusCalc.Polygons(shape.Polygons);
            if (polys.Count == 0) continue;
            var info = _info.GetValueOrDefault(shape.Room);
            var hex = ColourMode switch
            {
                "SUBCONTRACTOR" => info?.DominantSub is { Length: > 0 } d ? subColours[d] : "A6A6A6",
                "% USED" => info is null || info.ProjectQty <= 0 ? "A6A6A6" : RoomStatusCalc.HeatColour(info.UsedPct),
                "PENDING CHECKS" => (info?.PendingChecks ?? 0) > 0 ? "E3AE12" : "FFFFFF",
                _ => RoomStatusKinds.Colour(info?.Status ?? RoomStatusKinds.NoProjectQty),
            };
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                foreach (var poly in polys)
                {
                    g.BeginFigure(new Point(poly[0].X * PlanWidth, poly[0].Y * PlanHeight), true, true);
                    g.PolyLineTo(poly.Skip(1).Select(p => new Point(p.X * PlanWidth, p.Y * PlanHeight)).ToList(), true, false);
                }
            geo.Freeze();
            var biggest = polys.OrderByDescending(p => p.Count).First();
            var (cx, cy) = RoomStatusCalc.Centroid(biggest);
            var selected = shape.Room.Equals(SelectedRoom, StringComparison.OrdinalIgnoreCase);
            Shapes.Add(new PlanRoomShape
            {
                Room = shape.Room, Geometry = geo, Fill = B(hex, selected ? (byte)200 : (byte)130), Stroke = selected ? Brushes.Black : B("202020"), StrokeThickness = selected ? 5 : 1.5,
                Tip = info?.Tip ?? shape.Room, LabelX = cx * PlanWidth, LabelY = cy * PlanHeight,
            });
        }
        switch (ColourMode)
        {
            case "SUBCONTRACTOR":
                foreach (var (sub, hex) in subColours) Legend.Add(new LegendItem(sub, B(hex)));
                Legend.Add(new LegendItem("NO CLAIMS", B("A6A6A6")));
                break;
            case "% USED":
                foreach (var p in new[] { 0, 0.25, 0.5, 0.75, 1.0 }) Legend.Add(new LegendItem($"{p:P0}", B(RoomStatusCalc.HeatColour(p))));
                break;
            case "PENDING CHECKS":
                Legend.Add(new LegendItem("HEIGHT / LENGTH CHECK PENDING", B("E3AE12")));
                Legend.Add(new LegendItem("NONE", B("FFFFFF")));
                break;
            default:
                foreach (var k in RoomStatusKinds.All) Legend.Add(new LegendItem(k, B(RoomStatusKinds.Colour(k))));
                break;
        }
    }

    private void LoadRoom()
    {
        Balances.Clear();
        Lines.Clear();
        if (SelectedRoom.Length == 0) { RoomTitle = "Click a room"; RoomCaption = ""; RoomChecks = ""; return; }
        var info = _info.GetValueOrDefault(SelectedRoom);
        var room = Project.Snapshot.Rooms.FirstOrDefault(r => r.Code.Equals(SelectedRoom, StringComparison.OrdinalIgnoreCase));
        RoomTitle = SelectedRoom;
        RoomCaption = info is null ? "" : $"{room?.Level}  |  {room?.RoomType}  |  {room?.AreaType}\nPROJECT {info.ProjectQty:N1}  CLAIMED {info.Claimed:N1}  REMAINING {info.Remaining:N1}  ({info.UsedPct:P0})  -  {info.Status}\n" +
                                              string.Join("  ", info.BySubcontractor.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N1}"));
        foreach (var b in _balances.Values.Where(b => b.Room.Equals(SelectedRoom, StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Stage).ThenBy(b => b.Item))
            Balances.Add(new BalanceRow { Balance = b });
        var lines = Project.Snapshot.Claims.Where(c => c.Room.Equals(SelectedRoom, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.InvoiceNo).ThenBy(c => c.Stage).ToList();
        foreach (var c in lines) Lines.Add(new ClaimRow { Line = c });
        var checks = lines.Where(c => c.QtyAbove45 != 0 || c.LengthApplies).ToList();
        RoomChecks = checks.Count == 0 ? "No height / length checks." :
            string.Join("\n", checks.Select(c => $"{c.Subcontractor} INV {c.InvoiceNo} {c.Stage} {c.Item}: " +
                (c.QtyAbove45 != 0 ? $">4.5 m {c.QtyAbove45:0.#} {(HeightCheck.IsPending(c) ? "PENDING" : c.HeightStatus)}  " : "") +
                (c.LengthApplies ? $"15 m {c.LengthClaimedQty:0.#} {(LengthCheck.IsPending(c) ? "PENDING" : c.LengthStatus)}" : "")));
    }

    public void SelectRoom(string room) => SelectedRoom = room;

    [RelayCommand] private void OpenInLedger() { if (SelectedRoom.Length > 0) Ctx.Nav.Go("Ledger", new NavTarget("Ledger", Key: SelectedRoom)); }
    [RelayCommand] private void AddClaim() => OpenInLedger();
    [RelayCommand] private void PickRoom(string? room) { if (room != null) SelectedRoom = room; }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is not { } room) return;
        var shape = Project.Snapshot.RoomShapes.FirstOrDefault(r => r.Room.Equals(room, StringComparison.OrdinalIgnoreCase));
        if (shape != null) Level = Levels.FirstOrDefault(l => l.Plan.Plan == shape.Plan) ?? Level;
        SelectedRoom = room;
    }

    private static void Sync(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    public override IEnumerable<Raffaello.Core.Export.ExportSheet> ExportCurrentView()
    {
        var plan = Level?.Plan.Plan;
        var rooms = Project.Snapshot.RoomShapes.Where(r => r.Plan == plan).Select(r => r.Room).Distinct().ToList();
        yield return new Raffaello.Core.Export.ExportSheet
        {
            Name = "PLAN ROOMS", Title = $"PLAN {plan} - {ColourMode}",
            Columns = new() { new("ROOM"), new("STATUS"), new("PROJECT QTY", Raffaello.Core.Export.ColumnKind.Number), new("CLAIMED", Raffaello.Core.Export.ColumnKind.Number),
                new("REMAINING", Raffaello.Core.Export.ColumnKind.Number), new("% USED", Raffaello.Core.Export.ColumnKind.Percent), new("PENDING CHECKS", Raffaello.Core.Export.ColumnKind.Integer), new("MAIN SUBCONTRACTOR") },
            Rows = rooms.Select(r => _info.GetValueOrDefault(r)).Where(i => i != null)
                .Select(i => new object?[] { i!.Code, i.Status, i.ProjectQty, i.Claimed, i.Remaining, i.UsedPct, i.PendingChecks, i.DominantSub }).ToList(),
        };
    }
}
