using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Ledger;
using Raffaello.Core.Queue;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>
/// TRACKING: the Excel MEP tracker (HOTEL_MEP_TRACKER / BRANDED_MEP_TRACKER) mirrored from the app data. Same sheet names, same columns:
/// DASHBOARD, ENTRY, PLANS, ROOM, CONTROL, PROJECT QTY, LEDGER, ROOMS, SUMMARY, CAP, CABLES (+ RATES). Balances come from
/// <see cref="LedgerRules"/> (remaining = PROJECT QTY - all subcontractors, stages never summed), rates from <see cref="RateBook"/>,
/// posting from the normal claim check. The building switcher in the header chooses HOTEL / BRANDED.
/// </summary>
public sealed partial class TrackingViewModel : PageViewModel, ITabbedPage
{
    public TrackingViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Tracking";
    public override string Title => "TRACKING";
    public override string Subtitle => "";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;

    public const string TDashboard = "DASHBOARD", TEntry = "ENTRY", TPlans = "PLANS", TRoom = "ROOM", TControl = "CONTROL", TProject = "PROJECT QTY",
        TLedger = "LEDGER", TRooms = "ROOMS", TSummary = "SUMMARY", TCap = "CAP", TCables = "CABLES", TRates = "RATES", TInvoices = "INVOICES";
    public string[] Tabs { get; } = { TDashboard, TEntry, TPlans, TRoom, TControl, TProject, TLedger, TRooms, TSummary, TCap, TCables, TRates, TInvoices };
    [ObservableProperty] private string _tab = TDashboard;
    public bool IsDashboard => Tab == TDashboard;
    public bool IsEntry => Tab == TEntry;
    public bool IsPlans => Tab == TPlans;
    public bool IsRoom => Tab == TRoom;
    public bool IsControl => Tab == TControl;
    public bool IsProject => Tab == TProject;
    public bool IsLedger => Tab == TLedger;
    public bool IsRooms => Tab == TRooms;
    public bool IsSummary => Tab == TSummary;
    public bool IsCap => Tab == TCap;
    public bool IsCables => Tab == TCables;
    public bool IsRates => Tab == TRates;
    public bool IsInvoices => Tab == TInvoices;

    partial void OnTabChanged(string value)
    {
        foreach (var n in new[] { nameof(IsDashboard), nameof(IsEntry), nameof(IsPlans), nameof(IsRoom), nameof(IsControl), nameof(IsProject), nameof(IsLedger), nameof(IsRooms), nameof(IsSummary), nameof(IsCap), nameof(IsCables), nameof(IsRates), nameof(IsInvoices) })
            OnPropertyChanged(n);
        if (IsActive) EnsureTab();
        TabSwitched?.Invoke(value);
    }

    /// <summary>The shell updates the breadcrumb (Home &gt; area &gt; item) when the tab changes inside TRACKING.</summary>
    public event Action<string>? TabSwitched;

    // ------------------------------------------------------------------ shared data (rebuilt on refresh)
    private List<ClaimLine> _claims = new();
    private List<RoomQty> _caps = new();
    private Dictionary<string, RoomBalance> _bal = new();
    private Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private RateBook _rates = RateBook.From(new Raffaello.Core.Data.ProjectSnapshot());
    private List<TrkLedgerRow> _ledgerAll = new();
    private readonly HashSet<string> _built = new();

    [ObservableProperty] private string _banner = "";
    public ObservableCollection<string> Subcontractors { get; } = new();
    public ObservableCollection<string> StageOptions { get; } = new();
    public ObservableCollection<string> ItemOptions { get; } = new();
    public ObservableCollection<string> LocationCodes { get; } = new();

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        _claims = s.Claims.Where(c => InBuilding(c.Building)).ToList();
        _caps = s.RoomQtys.Where(q => InBuilding(q.Building)).ToList();
        _bal = LedgerRules.Balances(_caps, _claims);
        _rooms = s.Rooms.Where(r => InBuilding(r.Building)).GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _rates = Project.Workflow.Rates();
        _ledgerAll = _claims.Select(c =>
        {
            var (r, a) = _rates.Price(c);
            return new TrkLedgerRow { Line = c, RateInfo = r, Amounts = a, Balance = _bal.GetValueOrDefault(c.Key) };
        }).ToList();
        Sync(Subcontractors, _claims.Select(c => c.Subcontractor).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        Sync(StageOptions, _caps.Select(q => q.Stage).Concat(_claims.Select(c => c.Stage)).Where(x => x.Length > 0).Distinct().OrderBy(TrkStatus.StageRank));
        Sync(ItemOptions, _caps.Select(q => q.Item).Concat(_claims.Select(c => c.Item)).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        Sync(LocationCodes, AllLocations().Select(l => l.Code));
        var missing = _ledgerAll.Count(l => l.RateInfo.IsMissing);
        Banner = _caps.Count == 0 && _claims.Count == 0
            ? $"No tracker data for {BuildingFilter ?? "this building"} yet - import the RECON / tracker on ROOMS & LEDGER."
            : $"{BuildingFilter ?? "ALL"}  |  {_caps.Select(q => q.Room).Distinct().Count():N0} LOCATIONS  |  {_caps.Count:N0} PROJECT QTY KEYS  |  {_claims.Count:N0} CLAIM LINES  |  " +
              $"{_claims.Select(c => c.Subcontractor).Distinct().Count()} SUBCONTRACTORS  |  RATE MISSING ON {missing:N0} LINES";
        OnPropertyChanged(nameof(Subtitle));
        _built.Clear();
        EnsureTab();
    }

    private void EnsureTab()
    {
        if (!_built.Add(Tab)) return;
        try
        {
            switch (Tab)
            {
                case TDashboard: BuildDashboard(); break;
                case TEntry: BuildEntry(); break;
                case TPlans: BuildPlans(); BuildPlanViewer(); break;
                case TRoom: BuildRoom(); break;
                case TControl: BuildControl(); break;
                case TProject: BuildProjectQty(); break;
                case TLedger: BuildLedger(); break;
                case TRooms: BuildRooms(); break;
                case TSummary: BuildSummary(); break;
                case TCap: BuildCap(); break;
                case TCables: BuildCables(); break;
                case TRates: BuildRates(); break;
                case TInvoices: BuildInvoices(); break;
            }
        }
        catch (Exception ex) { _built.Remove(Tab); Ctx.Toasts.Show($"{Tab} could not be built", ex.Message, ToastKind.Error); }
    }

    private static void Sync(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private string PartOf(string location)
    {
        if (_rooms.TryGetValue(location, out var r) && r.Plot > 0) return $"Plot {r.Plot}";
        var dash = location.IndexOf('-');
        return dash > 0 ? location[..dash] : location;
    }

    private static DataColumn Col(DataTable t, string caption, Type? type = null)
    {
        var c = t.Columns.Add("c" + t.Columns.Count, type ?? typeof(string));
        c.Caption = caption;
        return c;
    }

    private static double? Num(string s) => double.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { Length: > 0 } k)
        {
            if (Tabs.Contains(k)) Tab = k;
            else { RoomLocation = k; Tab = TRoom; }
        }
    }
}