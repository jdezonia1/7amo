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
using Raffaello.Core.Queue;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

public sealed class LedgerRoomRow
{
    public required Room Room { get; init; }
    public string Code => Room.Code;
    public string Caption => $"{Room.Level}  |  {Room.RoomType}  |  {Room.AreaType}";
    public double ProjectQty { get; init; }
    public double Claimed { get; init; }
    public double UsedPct => ProjectQty <= 0 ? 0 : Claimed / ProjectQty;
    public int OverKeys { get; init; }
    public int PendingChecks { get; init; }
    public string Status => OverKeys > 0 ? "OVER" : PendingChecks > 0 ? "DUE" : Claimed <= 0 ? "OPEN" : UsedPct >= 0.999 ? "OK" : "OPEN";
}

public sealed class BalanceRow
{
    public required RoomBalance Balance { get; init; }
    public string Stage => Balance.Stage;
    public string Item => Balance.Item;
    public double ProjectQty => Balance.ProjectQty;
    public double Claimed => Balance.Claimed;
    public double Remaining => Balance.Remaining;
    public string BySubs => string.Join("  ", Balance.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}"));
    public string Status => Balance.IsOver ? "OVER" : !Balance.HasCap ? (Balance.Claimed > 0 ? "CHECK" : "OPEN") : Balance.Remaining <= 1e-9 ? "OK" : Balance.Claimed > 0 ? "OPEN" : "DUE";
}

public sealed class ClaimRow
{
    public required ClaimLine Line { get; init; }
    public string Status => Line.IsOver ? "OVER" : HeightCheck.IsPending(Line) || LengthCheck.IsPending(Line) ? "DUE" : Line.Qty < 0 ? "REJECTED" : "OK";
    public string InvoiceLabel => Raffaello.Core.Ledger.CumulativeSplit.InvoiceLabel(Line);
    public string Checks => (Line.QtyAbove45 != 0 ? $">4.5m {Line.QtyAbove45:0.#} {Line.HeightStatus} " : "") + (Line.LengthApplies ? $"15m {Line.LengthClaimedQty:0.#} {Line.LengthStatus}" : "");
}

public sealed class PlanPolygon
{
    public string Room { get; init; } = "";
    public PointCollection Points { get; init; } = new();
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush Stroke { get; init; } = Brushes.Transparent;
    public double StrokeThickness { get; init; } = 1;
    public string Tip { get; init; } = "";
}

/// <summary>Rooms and the claim ledger: every subcontractor's claims per room x stage x item against PROJECT QTY.</summary>
public sealed partial class LedgerViewModel : PageViewModel
{
    public const double PlanWidth = 1000;

    private readonly Raffaello.Core.Documents.IDocumentStore _docs;
    public LedgerViewModel(PageContext ctx, Raffaello.Core.Documents.IDocumentStore docs) : base(ctx) { _docs = docs; }

    /// <summary>Contract-rule warnings for a posted claim (height bands, 15 m rule): shown, never blocking.</summary>
    private void ContractWarnings(ClaimLine line)
    {
        try
        {
            var contract = Project.Snapshot.Contracts.FirstOrDefault(c => c.Subcontractor == line.Subcontractor && (c.Building == line.Building || c.Building.Length == 0));
            if (contract is null) return;
            var warnings = Raffaello.Core.Documents.DocumentStoreExtensions.RuleEngine(_docs).CheckClaim(contract.ContractNo, line);
            if (warnings.Count > 0)
                Ctx.Toasts.Show("CONTRACT CHECK (WARNING)", string.Join("\n", warnings.Take(3).Select(w => $"{w.Message} [{w.Source}]")) + "\nBypass with a reason on the invoice's WARNINGS tab.", ToastKind.Warn, 10);
        }
        catch (Exception) { /* checks are advisory */ }
    }

    public override string Key => "Ledger";
    public override string Title => "ROOMS & LEDGER";
    public override string Subtitle => "Remaining = PROJECT QTY - all subcontractors, per room x stage x item (stages never summed)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;   // [phase6] the building switcher drives it

    public ObservableCollection<LedgerRoomRow> Rooms { get; } = new();
    public ObservableCollection<BalanceRow> Balances { get; } = new();
    public ObservableCollection<ClaimRow> Lines { get; } = new();
    public ObservableCollection<string> Subcontractors { get; } = new();
    public ObservableCollection<string> StageOptions { get; } = new();
    public ObservableCollection<string> ItemOptions { get; } = new();
    public ObservableCollection<PlanPolygon> Polygons { get; } = new();
    public string[] AreaTypeOptions => AreaTypes.All;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private LedgerRoomRow? _selectedRoom;
    [ObservableProperty] private ClaimRow? _selectedLine;
    [ObservableProperty] private BalanceRow? _selectedBalance;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _banner = "";

    // entry
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _invoiceNo = "1";
    [ObservableProperty] private string _stage = "1ST FIX";
    [ObservableProperty] private string _item = "POWER";
    [ObservableProperty] private string _qty = "";
    [ObservableProperty] private string _sitePct = "100";
    [ObservableProperty] private string _wirPct = "100";
    [ObservableProperty] private string _wirNo = "";
    [ObservableProperty] private string _qtyAbove45 = "";
    [ObservableProperty] private string _lengthClaimed = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _overReason = "";
    [ObservableProperty] private string _entryCheck = "";
    [ObservableProperty] private string _entryStatus = "";

    // room
    [ObservableProperty] private string _areaType = "";
    [ObservableProperty] private string _highAreaNote = "";

    // plan
    [ObservableProperty] private ImageSource? _planImage;
    [ObservableProperty] private double _planHeight = 600;
    [ObservableProperty] private string _planCaption = "";

    private List<PlanImage>? _plans;
    private Dictionary<string, RoomBalance> _balances = new();
    private string? _pendingRoom;

    partial void OnSearchChanged(string value) => FillRooms();
    partial void OnSelectedRoomChanged(LedgerRoomRow? value)
    {
        LoadRoom();
        PublishSelection(value is null ? "" : Raffaello.Core.Wiring.SelectedRecords.Room(value.Room.Code, value.Room.Building, value.Room.RoomType));
    }

    partial void OnSelectedLineChanged(ClaimRow? value) =>
        PublishSelection(value is not null ? Raffaello.Core.Wiring.SelectedRecords.LedgerLine(value.Line)
            : SelectedRoom is { } r ? Raffaello.Core.Wiring.SelectedRecords.Room(r.Room.Code, r.Room.Building, r.Room.RoomType) : "");
    partial void OnSelectedBalanceChanged(BalanceRow? value) { if (value != null) { Stage = value.Stage; Item = value.Item; } }
    partial void OnStageChanged(string value) => UpdateEntryCheck();
    partial void OnItemChanged(string value) => UpdateEntryCheck();
    partial void OnQtyChanged(string value) => UpdateEntryCheck();
    partial void OnOverReasonChanged(string value) => UpdateEntryCheck();

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        _balances = LedgerRules.Balances(s.RoomQtys, s.Claims);
        Sync(Subcontractors, s.Claims.Select(c => c.Subcontractor).Concat(s.Contracts.Select(c => c.Subcontractor)).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        Sync(StageOptions, s.RoomQtys.Select(q => q.Stage).Concat(s.Claims.Select(c => c.Stage)).Where(x => x.Length > 0).Distinct().OrderBy(StageRank));
        Sync(ItemOptions, s.RoomQtys.Select(q => q.Item).Concat(s.Claims.Select(c => c.Item)).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        var claims = s.Claims;
        Banner = Raffaello.Core.Ledger.CumulativeSplit.Banner(claims);
        Summary = s.RoomQtys.Count == 0
            ? "No tracker imported yet - IMPORT TRACKER reads ROOMS, PROJECT QTY, LEDGER and the PLANS shapes."
            : $"{s.Rooms.Count(r => r.Plan.Length > 0 || r.Plot > 0)} ROOMS  |  {s.RoomQtys.Count:N0} PROJECT QTY  |  {claims.Count:N0} CLAIM LINES  |  {claims.Select(c => c.Subcontractor).Distinct().Count()} SUBCONTRACTORS  |  " +
              $"OVER CAP {_balances.Values.Count(b => b.HasCap && b.IsOver)}  |  NO CAP {_balances.Values.Count(b => !b.HasCap && b.Claimed > 0)}";
        FillRooms();
    }

    private static int StageRank(string s) => Array.IndexOf(new[] { "CEILING", "1ST FIX", "EMT", "FLEXIBLE", "2ND FIX", "3RD FIX", "DB PANELS", "CABLE PULLING", "CABLE TRAY" }, s) is var i && i < 0 ? 99 : i;

    private void FillRooms()
    {
        var keep = _pendingRoom ?? SelectedRoom?.Code;
        _pendingRoom = null;
        var s = Project.Snapshot;
        var byRoom = _balances.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var pending = s.Claims.Where(c => HeightCheck.IsPending(c) || LengthCheck.IsPending(c)).GroupBy(c => c.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rooms = s.Rooms.Where(r => InBuilding(r.Building) && (r.Plot > 0 || r.Plan.Length > 0 || byRoom.ContainsKey(r.Code))).ToList();
        // ledger locations that are not rooms (stairs, panels ...) still need a row
        foreach (var extra in byRoom.Keys.Where(k => s.Rooms.All(r => !r.Code.Equals(k, StringComparison.OrdinalIgnoreCase)) && s.Claims.Any(c => c.Room == k && InBuilding(c.Building))))
            rooms.Add(new Room { Code = extra, Building = WorkingBuilding, RoomType = "NOT IN ROOMS", Level = s.Claims.FirstOrDefault(c => c.Room == extra)?.Floor ?? "" });
        var q = rooms.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search))
            q = q.Where(r => r.Code.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.RoomType.Contains(Search, StringComparison.OrdinalIgnoreCase) || r.Level.Contains(Search, StringComparison.OrdinalIgnoreCase));
        Rooms.Clear();
        foreach (var r in q.OrderBy(r => r.Plot).ThenBy(r => r.Floor).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase))
        {
            var b = byRoom.GetValueOrDefault(r.Code) ?? new List<RoomBalance>();
            Rooms.Add(new LedgerRoomRow
            {
                Room = r, ProjectQty = b.Sum(x => x.ProjectQty), Claimed = b.Sum(x => x.Claimed), OverKeys = b.Count(x => x.HasCap && x.IsOver),
                PendingChecks = pending.GetValueOrDefault(r.Code),
            });
        }
        SelectedRoom = Rooms.FirstOrDefault(r => r.Code == keep) ?? Rooms.FirstOrDefault();
    }

    private void LoadRoom()
    {
        Balances.Clear();
        Lines.Clear();
        var room = SelectedRoom;
        if (room is null) { PlanImage = null; Polygons.Clear(); return; }
        foreach (var b in _balances.Values.Where(b => b.Room.Equals(room.Code, StringComparison.OrdinalIgnoreCase)).OrderBy(b => StageRank(b.Stage)).ThenBy(b => b.Item))
            Balances.Add(new BalanceRow { Balance = b });
        foreach (var c in Project.Snapshot.Claims.Where(c => c.Room.Equals(room.Code, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.InvoiceNo).ThenBy(c => c.Stage).ThenBy(c => c.Item))
            Lines.Add(new ClaimRow { Line = c });
        AreaType = room.Room.AreaType;
        HighAreaNote = room.Room.HighAreaNote;
        UpdateEntryCheck();
        LoadPlan(room);
    }

    private void LoadPlan(LedgerRoomRow room)
    {
        Polygons.Clear();
        try { _plans ??= Project.LoadPlans(); } catch { _plans = new(); }
        var shape = Project.Snapshot.RoomShapes.FirstOrDefault(s => s.Room.Equals(room.Code, StringComparison.OrdinalIgnoreCase));
        var planCode = shape?.Plan ?? room.Room.Plan;
        var plan = _plans.FirstOrDefault(p => p.Plan.Equals(planCode, StringComparison.OrdinalIgnoreCase));
        if (plan?.Png is null) { PlanImage = null; PlanCaption = "No plan for this room"; return; }
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(plan.Png);
        bmp.EndInit();
        bmp.Freeze();
        PlanImage = bmp;
        PlanHeight = plan.Width <= 0 ? 600 : PlanWidth * plan.Height / plan.Width;
        PlanCaption = $"{plan.Plan}  |  {plan.Caption}";
        var rows = Rooms.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var sh in Project.Snapshot.RoomShapes.Where(s => s.Plan == plan.Plan))
        {
            rows.TryGetValue(sh.Room, out var rr);
            var selected = sh.Room.Equals(room.Code, StringComparison.OrdinalIgnoreCase);
            var status = rr?.Status ?? "OPEN";
            var colour = (Color)ColorConverter.ConvertFromString(status switch { "OVER" => "#8E1B22", "DUE" => "#E3AE12", "OK" => "#2E7D4F", _ => "#A6A6A6" });
            foreach (var poly in sh.Polygons.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var pts = new PointCollection();
                foreach (var p in poly.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var xy = p.Split(',');
                    if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                        pts.Add(new Point(x * PlanWidth, y * PlanHeight));
                }
                pts.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)150 : (byte)70, colour.R, colour.G, colour.B)); fill.Freeze();
                var stroke = new SolidColorBrush(selected ? Colors.Black : colour); stroke.Freeze();
                Polygons.Add(new PlanPolygon { Room = sh.Room, Points = pts, Fill = fill, Stroke = stroke, StrokeThickness = selected ? 3 : 1, Tip = $"{sh.Room}  {status}  {rr?.Claimed:0.#} / {rr?.ProjectQty:0.#}" });
            }
        }
    }

    public void SelectRoom(string code)
    {
        var r = Rooms.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (r != null) SelectedRoom = r;
    }

    private static double? D(string s) => double.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private void UpdateEntryCheck()
    {
        if (SelectedRoom is null) { EntryCheck = ""; EntryStatus = ""; return; }
        var bal = Project.Workflow.Balance(SelectedRoom.Code, Stage, Item);
        var qty = D(Qty) ?? 0;
        var subs = string.Join(", ", bal.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}"));
        EntryCheck = $"PROJECT {bal.ProjectQty:0.##}  |  CLAIMED {bal.Claimed:0.##}{(subs.Length > 0 ? " (" + subs + ")" : "")}  |  REMAINING {bal.Remaining:0.##}" +
                     (qty != 0 ? $"  |  AFTER THIS {bal.Remaining - qty:0.##}" : "");
        if (qty == 0) { EntryStatus = bal.HasCap ? "OPEN" : "CHECK"; return; }
        var check = LedgerRules.Check(bal, qty, OverReason);
        EntryStatus = check.Decision switch { ClaimDecision.Accepted => "OK", ClaimDecision.AcceptedOver => "OVER", _ => "ERROR" };
    }

    [RelayCommand]
    private async Task AddClaim()
    {
        if (SelectedRoom is null) return;
        var qty = D(Qty);
        if (qty is null || qty == 0 || string.IsNullOrWhiteSpace(Sub)) { Ctx.Toasts.Show("SUBCONTRACTOR AND QTY NEEDED", kind: ToastKind.Warn); return; }
        var above = D(QtyAbove45) ?? 0;
        var length = D(LengthClaimed) ?? 0;
        var line = new ClaimLine
        {
            Building = SelectedRoom.Room.Building, Subcontractor = Sub.Trim().ToUpperInvariant(), InvoiceNo = int.TryParse(InvoiceNo, out var n) ? n : 0,
            Stage = Stage.Trim().ToUpperInvariant(), Item = Item.Trim().ToUpperInvariant(), Floor = SelectedRoom.Room.Level, Room = SelectedRoom.Code, Qty = qty.Value,
            SitePct = (D(SitePct) ?? 100) / 100.0, WirPct = (D(WirPct) ?? 100) / 100.0, WirNo = WirNo.Trim(), Notes = Notes.Trim(), Source = "MANUAL",
            QtyAbove45 = above, LengthApplies = length > qty.Value, LengthClaimedQty = length,
        };
        ClaimCheck? result = null;
        var ok = await Ctx.Data.WriteAsync(p => result = p.Workflow.AddClaim(line, string.IsNullOrWhiteSpace(OverReason) ? null : OverReason), Ctx.Toasts);
        if (!ok || result is null) return;
        if (!result.CanPost) { Ctx.Toasts.Show("CLAIM BLOCKED", result.Message, ToastKind.Warn, 8); return; }
        Ctx.Toasts.Show(result.IsOver ? "POSTED - OVER" : "CLAIM POSTED", result.Message, result.IsOver ? ToastKind.Warn : ToastKind.Good);
        ContractWarnings(line);
        // [cables] a CABLE PULLING line (LOCATION = FROM panel, NOTES = TO) goes into the cable register and is checked for duplicate FROM-TO
        if (Raffaello.Core.Cables.CableStages.IsLedgerCableStage(line.Stage))
        {
            var cable = await Task.Run(() => Raffaello.Core.Cables.CableHooks.LedgerPosted(Project.Store, line));
            if (cable.Length > 0) Ctx.Toasts.Show("CABLE FLAGS", cable, ToastKind.Warn, 10);
        }
        _pendingRoom = SelectedRoom.Code;
        Qty = ""; QtyAbove45 = ""; LengthClaimed = ""; OverReason = ""; Notes = "";
    }

    [RelayCommand]
    private async Task Reverse()
    {
        if (SelectedLine is null) return;
        var reason = string.IsNullOrWhiteSpace(Notes) ? "correction" : Notes.Trim();
        if (!Ctx.Dialogs.Confirm("Reverse claim", $"Add a reversal line for {SelectedLine.Line.Subcontractor} {SelectedLine.Line.Stage} {SelectedLine.Line.Item} {SelectedLine.Line.Qty:0.##}?\n\nReason: {reason}\n(The ledger is append-only: the original line stays.)")) return;
        var line = SelectedLine.Line;
        _pendingRoom = SelectedRoom?.Code;
        await Ctx.Data.WriteAsync(p => p.Workflow.Reverse(line, reason), Ctx.Toasts, "REVERSAL POSTED");
    }

    [RelayCommand]
    private async Task SaveRoom()
    {
        if (SelectedRoom is null || SelectedRoom.Room.Id == 0) return;
        var room = SelectedRoom.Room;
        _pendingRoom = room.Code;
        await Ctx.Data.WriteAsync(p => p.Workflow.SetAreaType(room, AreaType, HighAreaNote.Trim()), Ctx.Toasts, $"{room.Code} SAVED");
    }

    [RelayCommand]
    private async Task ImportTracker()
    {
        var file = Ctx.Dialogs.OpenFile("Tracker workbook (v19)", "Tracker|*.xlsm;*.xlsx|All files|*.*");
        if (file is null) return;
        TrackerImportResult? preview = null;
        try { preview = await Task.Run(() => Project.Workflow.PreviewTracker(file, WorkingBuilding)); }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ TRACKER", ex.Message, ToastKind.Error); return; }
        var issues = string.Join("\n", preview.Issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"row \d+", "row #")).Take(8).Select(g => $"- {g.Key} (x{g.Count()})"));
        if (!Ctx.Dialogs.Confirm("Import tracker", $"{preview.Summary}\n\nArea types: {string.Join(", ", preview.Rooms.GroupBy(r => r.AreaType).Select(g => $"{g.Key} {g.Count()}"))}\n\n{issues}\n\nImport? Ledger lines already imported are skipped; PROJECT QTY is replaced.")) return;
        string msg = "";
        _plans = null;
        await Ctx.Data.WriteAsync(p => msg = p.Workflow.CommitTracker(preview), Ctx.Toasts);
        Ctx.Toasts.Show("TRACKER IMPORTED", msg, ToastKind.Good, 8);
    }

    /// <summary>[phase6] Room list of the building chosen in the switcher (HOTEL ...), columns found by header text.</summary>
    [RelayCommand]
    private async Task ImportRoomList()
    {
        var file = Ctx.Dialogs.OpenFile($"{WorkingBuilding} room list (Excel)");
        if (file is null) return;
        Raffaello.Core.Tracker.RoomListImportResult? r;
        try { r = await Task.Run(() => Project.Workflow.PreviewRoomList(file, WorkingBuilding)); }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ ROOM LIST", ex.Message, ToastKind.Error); return; }
        if (r.Rooms.Count == 0) { Ctx.Toasts.Show("NO ROOMS FOUND", string.Join("\n", r.Issues.Select(i => i.Message)), ToastKind.Warn, 10); return; }
        var cols = string.Join(", ", r.ColumnsFound.Select(kv => $"{kv.Key} = '{kv.Value}'"));
        if (!Ctx.Dialogs.Confirm("Import room list", $"{r.Summary}\n\nColumns: {cols}\n\n{string.Join("\n", r.Issues.Take(6).Select(i => "- " + i.Message))}\n\nImport into {WorkingBuilding}? Area types already set are kept.")) return;
        string msg = "";
        if (await Ctx.Data.WriteAsync(p => msg = p.Workflow.CommitRoomList(r), Ctx.Toasts)) Ctx.Toasts.Show("ROOM LIST IMPORTED", msg, ToastKind.Good, 8);
    }

    [RelayCommand] private void PlanRoom(string? room) { if (room != null) SelectRoom(room); }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { } room) { _pendingRoom = room; FillRooms(); }
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
        yield return new Raffaello.Core.Export.ExportSheet
        {
            Name = "LEDGER", Title = $"LEDGER - {SelectedRoom?.Code}",
            Columns = new() { new("SUBCONTRACTOR"), new("INVOICE", Raffaello.Core.Export.ColumnKind.Integer), new("STAGE"), new("ROOM"), new("ITEM"), new("QTY", Raffaello.Core.Export.ColumnKind.Number),
                new("SITE %", Raffaello.Core.Export.ColumnKind.Percent), new("WIR %", Raffaello.Core.Export.ColumnKind.Percent), new("QTY AFTER WIR", Raffaello.Core.Export.ColumnKind.Number), new("CHECKS"), new("NOTES", Width: 40), new("STATUS") },
            Rows = Lines.Select(l => new object?[] { l.Line.Subcontractor, l.Line.InvoiceNo, l.Line.Stage, l.Line.Room, l.Line.Item, l.Line.Qty, l.Line.SitePct, l.Line.WirPct, l.Line.QtyAfterWir, l.Checks, l.Line.Notes, l.Status }).ToList(),
        };
        yield return new Raffaello.Core.Export.ExportSheet
        {
            Name = "ROOM BALANCE", Title = $"ROOM BALANCE - {SelectedRoom?.Code}",
            Columns = new() { new("STAGE"), new("ITEM"), new("PROJECT QTY", Raffaello.Core.Export.ColumnKind.Number), new("CLAIMED", Raffaello.Core.Export.ColumnKind.Number), new("REMAINING", Raffaello.Core.Export.ColumnKind.Number), new("BY SUBCONTRACTOR", Width: 50), new("STATUS") },
            Rows = Balances.Select(b => new object?[] { b.Stage, b.Item, b.ProjectQty, b.Claimed, b.Remaining, b.BySubs, b.Status }).ToList(),
        };
    }
}
