using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed record PaletteEntry(string Group, string Title, string Detail, string Shortcut, Action Run);

/// <summary>Ctrl+K: jump to any module, room, line, WIR, statement or PO; run any action.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly DataService _data;
    private readonly FilterState _filter;
    private List<PaletteEntry> _static = new();
    private Func<IEnumerable<PaletteEntry>>? _actions;

    public CommandPaletteViewModel(DataService data, FilterState filter) { _data = data; _filter = filter; }

    public ObservableCollection<PaletteEntry> Results { get; } = new();
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteEntry? _selected;

    public void Configure(INavigator nav, Func<IEnumerable<PaletteEntry>> actions)
    {
        _actions = actions;
        _static = new()
        {
            new("GO TO", "Welcome", "Start screen", "", () => nav.Go("Welcome")),
            new("GO TO", "Dashboard", "Needs you today + charts", "Ctrl+1", () => nav.Go("Dashboard")),
            new("GO TO", "Rooms & Ledger", "Claims per room x stage x item, remaining, plan", "Ctrl+2", () => nav.Go("Ledger")),
            new("GO TO", "Plan view", "Rooms on the level plans by status / subcontractor / % used", "", () => nav.Go("Plan")),
            new("GO TO", "Checks", "HEIGHT CHECK (>4.5 m) and LENGTH CHECK (15 m)", "Ctrl+3", () => nav.Go("Checks")),
            new("GO TO", "Quantities", "REMAINING = QS - GIVEN", "Ctrl+4", () => nav.Go("Quantities")),
            new("GO TO", "Statements", "Subcontractor statements", "Ctrl+5", () => nav.Go("Statements")),
            new("GO TO", "Materials", "PO vs DN", "Ctrl+6", () => nav.Go("Materials")),
            new("GO TO", "Aconex", "Download queue", "", () => nav.Go("Aconex")),
            new("GO TO", "WIR / MIR", "Inspection requests", "", () => nav.Go("Wir")),
            new("GO TO", "Contracts & BOQ", "Contract items, attributes, BOQ links, imports", "Ctrl+7", () => nav.Go("Contracts")),
            new("GO TO", "Invoices", "Build, revise, approve, export the subcontractor invoice", "Ctrl+8", () => nav.Go("Invoices")),
            new("GO TO", "Site statements", "Issue / import site statements", "Ctrl+9", () => nav.Go("SiteStatements")),
            new("GO TO", "Reports", "Weekly report, print", "Ctrl+0", () => nav.Go("Reports")),
            new("GO TO", "Settings", "Theme, data file, API key", "", () => nav.Go("Settings")),
            // [phase3] begin
            new("GO TO", "Owner MOS", "Materials on site valuation for the owner", "", () => nav.Go("OwnerMos")),
            new("GO TO", "BOQ", "Owner BOQ by system and category", "", () => nav.Go("Boq")),
            new("GO TO", "DN lookup", "Supplier + DN / PO / batch -> PO, invoice, MIR", "", () => nav.Go("Materials")),
            // [phase3] end
            new("FILTER", "Clear filters", "Show all buildings and stages", "", () => _filter.Clear()),
            new("FILTER", "Hotel only", "", "", () => _filter.Set(building: "HOTEL")),
            new("FILTER", "Branded only", "", "", () => _filter.Set(building: "BRANDED")),
            new("FILTER", "OVER lines", "Quantities filtered to OVER", "", () => nav.Go("Quantities", new NavTarget("Quantities", Key: "OVER"))),
            new("FILTER", "CHECK lines", "Quantities filtered to CHECK", "", () => nav.Go("Quantities", new NavTarget("Quantities", Key: "CHECK"))),
        };
        foreach (var st in Raffaello.Core.Domain.Stages.All) { var s = st; _static.Add(new("FILTER", $"Stage {s}", "", "", () => _filter.Set(_filter.Spec.Building, _filter.Spec.Level, _filter.Spec.Room, _filter.Spec.System, s))); }
    }

    public void Open()
    {
        Query = "";
        IsOpen = true;
        Update();
    }

    partial void OnQueryChanged(string value) => Update();

    private IEnumerable<PaletteEntry> Dynamic(INavigator? nav)
    {
        var p = _data.Project;
        foreach (var r in p.Snapshot.Rooms)
        {
            var room = r;
            yield return new("ROOM", $"{room.Building} {room.Level} {room.Code}", room.RoomType, "", () => { _filter.Set(room.Building, room.Level, room.Code); });
        }
        foreach (var w in p.Snapshot.Wirs)
            yield return new(w.Kind, w.WirNo, $"{w.Subcontractor} {w.Description} - {w.Status}", "", () => GoTo("Wir", new NavTarget("Wir", Key: w.WirNo)));
        foreach (var i in p.Snapshot.Invoices)
            yield return new("STATEMENT", $"{i.Subcontractor} {i.InvoiceNo}", $"{i.InvDate:dd MMM yyyy} - {i.Status}", "", () => GoTo("Statements", new NavTarget("Statements", Key: $"{i.Subcontractor}|{i.InvoiceNo}")));
        foreach (var po in p.Snapshot.PurchaseOrders)
            yield return new("PO", po.PoNo, $"{po.Supplier} - {po.Description}", "", () => GoTo("Materials", new NavTarget("Materials", Key: po.PoNo)));
        foreach (var r in p.Chain)
        {
            var id = r.Id;
            yield return new("LINE", r.Key, $"{r.Status} - {r.Reason}", "", () => GoTo("Quantities", new NavTarget("Quantities", id)));
        }
    }

    private INavigator? _nav;
    public void SetNavigator(INavigator nav) => _nav = nav;
    private void GoTo(string module, NavTarget t) => _nav?.Go(module, t);

    private void Update()
    {
        var q = Query.Trim();
        var source = _static.Concat(_actions?.Invoke() ?? Enumerable.Empty<PaletteEntry>());
        IEnumerable<PaletteEntry> hits;
        if (q.Length == 0) hits = source.Take(14);
        else
        {
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Match(PaletteEntry e) => terms.All(t => e.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Detail.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Group.Contains(t, StringComparison.OrdinalIgnoreCase));
            hits = source.Where(Match).Take(12).Concat(Dynamic(_nav).Where(Match).Take(40));
        }
        Results.Clear();
        foreach (var h in hits) Results.Add(h);
        Selected = Results.FirstOrDefault();
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? 0 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    [RelayCommand]
    public void Execute(PaletteEntry? entry)
    {
        entry ??= Selected;
        if (entry is null) return;
        IsOpen = false;
        entry.Run();
    }

    [RelayCommand] private void Close() => IsOpen = false;
}
