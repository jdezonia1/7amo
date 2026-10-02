using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed class WirRow
{
    public required Wir Wir { get; init; }
    public int Days { get; init; }
    public int Lines { get; init; }
    public double Qty { get; init; }
    public double Rework { get; init; }
    public string WirNo => Wir.WirNo;
    public string Kind => Wir.Kind;
    public string Subcontractor => Wir.Subcontractor;
    public string Building => Wir.Building;
    public string Level => Wir.Level;
    public string System => Wir.System;
    public string Stage => Wir.Stage;
    public string Description => Wir.Description;
    public DateTime SubmittedAt => Wir.SubmittedAt;
    public DateTime? ApprovedAt => Wir.ApprovedAt;
    public string Status { get; init; } = "";
    public string WirStatusText => Wir.Status;
}

public sealed class WirLineRow
{
    public required ChainRow Row { get; init; }
    public double Qty { get; init; }
    public bool IsRework { get; init; }
    public string Status => IsRework ? "WARN" : Row.Status;
    public string Key => Row.Key;
}

/// <summary>WIR / MIR register with ageing, approve / reject and the chain of each WIR line.</summary>
public sealed partial class WirViewModel : PageViewModel
{
    public WirViewModel(PageContext ctx) : base(ctx)
    {
        Chain = new ChainPanelViewModel(ctx);
        Chain.AskRequested += () => ctx.Nav.OpenAsk();
    }

    public override string Key => "Wir";
    public override string Title => "WIR / MIR";
    public override string Subtitle => "Inspection requests - EMT is rework and never counts as progress";
    protected override bool HasCharts => true;

    public ChainPanelViewModel Chain { get; }
    public ObservableCollection<WirRow> Wirs { get; } = new();
    public ObservableCollection<WirLineRow> WirLines { get; } = new();
    public string[] Kinds { get; } = { "WIR", "MIR" };
    public string[] StatusOptions { get; } = { "ALL", "OPEN", "LATE", "APPROVED", "REJECTED" };

    [ObservableProperty] private string _kind = "WIR";
    [ObservableProperty] private string _statusFilter = "ALL";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private WirRow? _selected;
    [ObservableProperty] private WirLineRow? _selectedLine;
    [ObservableProperty] private string _counts = "";

    [ObservableProperty] private ISeries[] _ageing = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _ageingX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _ageingY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _weekly = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _weeklyX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _weeklyY = Array.Empty<Axis>();

    private string? _pendingWir;

    partial void OnKindChanged(string value) => Refresh();
    partial void OnStatusFilterChanged(string value) => Refresh();
    partial void OnSearchChanged(string value) => Refresh();
    partial void OnSelectedChanged(WirRow? value) => LoadLines();
    partial void OnSelectedLineChanged(WirLineRow? value) => Chain.Row = value?.Row;

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        var spec = Spec;
        var due = Project.Options.WirDueDays;
        var lineAgg = s.WirLines.GroupBy(l => l.WirId).ToDictionary(g => g.Key, g => (n: g.Count(), q: g.Where(x => !x.IsRework).Sum(x => x.Qty), rw: g.Where(x => x.IsRework).Sum(x => x.Qty)));
        var all = s.Wirs.Where(w => w.Kind == Kind && (spec.Building is null || w.Building == spec.Building) && (spec.Level is null || w.Level == spec.Level)
                                    && (spec.System is null || w.System == spec.System) && (spec.Stage is null || w.Stage == spec.Stage))
            .Select(w =>
            {
                var days = (int)(Today - w.SubmittedAt.Date).TotalDays;
                var agg = lineAgg.GetValueOrDefault(w.Id);
                var status = w.Status switch
                {
                    WirStatus.Approved => "APPROVED",
                    WirStatus.Rejected => "REJECTED",
                    _ => days > due ? "DUE" : "OPEN",
                };
                return new WirRow { Wir = w, Days = w.Status == WirStatus.Open ? days : (int)((w.ApprovedAt ?? w.SubmittedAt) - w.SubmittedAt).TotalDays, Lines = agg.n, Qty = agg.q, Rework = agg.rw, Status = status };
            }).ToList();
        var q = all.AsEnumerable();
        q = StatusFilter switch
        {
            "OPEN" => q.Where(w => w.Wir.Status == WirStatus.Open),
            "LATE" => q.Where(w => w.Status == "DUE"),
            "APPROVED" => q.Where(w => w.Wir.Status == WirStatus.Approved),
            "REJECTED" => q.Where(w => w.Wir.Status == WirStatus.Rejected),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(Search))
            q = q.Where(w => w.WirNo.Contains(Search, StringComparison.OrdinalIgnoreCase) || w.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) || w.Subcontractor.Contains(Search, StringComparison.OrdinalIgnoreCase));
        var keep = _pendingWir ?? Selected?.WirNo;
        _pendingWir = null;
        Wirs.Clear();
        foreach (var w in q.OrderByDescending(w => w.Status == "DUE").ThenByDescending(w => w.Wir.Status == WirStatus.Open).ThenByDescending(w => w.SubmittedAt)) Wirs.Add(w);
        Selected = Wirs.FirstOrDefault(w => w.WirNo == keep) ?? Wirs.FirstOrDefault();
        Counts = $"{all.Count:N0} {Kind}  |  OPEN {all.Count(w => w.Wir.Status == WirStatus.Open)}  |  LATE {all.Count(w => w.Status == "DUE")}  |  APPROVED {all.Count(w => w.Wir.Status == WirStatus.Approved)}  |  REJECTED {all.Count(w => w.Wir.Status == WirStatus.Rejected)}";

        var buckets = ProjectAnalytics.WirAgeing(all.Select(w => w.Wir), Today);
        var colours = new[] { ChartKit.Good, ChartKit.Yellow, ChartKit.Accent, ChartKit.Sk("Ink") };
        Ageing = buckets.Select((b, i) => { var v = new double[4]; v[i] = b.Count; var c = ChartKit.Columns(b.Label, v, colours[i], 34); c.IgnoresBarPosition = true; return (ISeries)c; }).ToArray();
        AgeingX = new[] { ChartKit.XLabels(buckets.Select(b => b.Label)) };
        AgeingY = new[] { ChartKit.YValues() };

        var weeks = 16;
        var submitted = ProjectAnalytics.WeeklyWirCount(all.Select(w => w.Wir), Today, weeks);
        var start = ProjectAnalytics.WeekStart(Today).AddDays(-7 * (weeks - 1));
        var approved = new double[weeks];
        foreach (var w in all.Where(w => w.Wir.ApprovedAt.HasValue && w.Wir.Status == WirStatus.Approved))
        {
            var i = (int)Math.Floor((w.Wir.ApprovedAt!.Value.Date - start).TotalDays / 7.0);
            if (i >= 0 && i < weeks) approved[i]++;
        }
        Weekly = new ISeries[] { ChartKit.Columns("SUBMITTED", submitted, ChartKit.Yellow, 12), ChartKit.Columns("APPROVED", approved, ChartKit.Accent, 12) };
        WeeklyX = new[] { ChartKit.XLabels(Enumerable.Range(0, weeks).Select(i => start.AddDays(7 * i).ToString("dd MMM")), 45) };
        WeeklyY = new[] { ChartKit.YValues() };
    }

    private void LoadLines()
    {
        WirLines.Clear();
        if (Selected is null) return;
        foreach (var l in Project.Snapshot.WirLines.Where(l => l.WirId == Selected.Wir.Id))
            if (Project.ChainById.TryGetValue(l.LineId, out var r)) WirLines.Add(new WirLineRow { Row = r, Qty = l.Qty, IsRework = l.IsRework });
        SelectedLine = WirLines.FirstOrDefault();
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { } no)
        {
            StatusFilter = "ALL";
            Kind = no.StartsWith("MIR") ? "MIR" : "WIR";
            _pendingWir = no;
            Refresh();
        }
    }

    private async Task SetApproval(bool approve)
    {
        if (Selected is null || Selected.Wir.Status != WirStatus.Open) { Ctx.Toasts.Show("ONLY OPEN WIRS", "Select an open WIR.", ToastKind.Warn); return; }
        var wir = Selected.Wir;
        _pendingWir = wir.WirNo;
        await Ctx.Data.WriteAsync(p => p.ApproveWir(wir, approve), Ctx.Toasts, $"{wir.WirNo} {(approve ? "APPROVED" : "REJECTED")}");
    }

    [RelayCommand] private Task Approve() => SetApproval(true);
    [RelayCommand] private Task Reject() => SetApproval(false);
    [RelayCommand] private void Import() => Ctx.Nav.OpenImport("WIR");

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = $"{Kind} REGISTER", Title = $"{Kind} REGISTER - {Spec.Describe()}",
            Columns = new() { new($"{Kind} NO"), new("SUBCONTRACTOR"), new("BUILDING"), new("LEVEL"), new("SYSTEM"), new("STAGE"), new("DESCRIPTION", Width: 40), new("SUBMITTED", ColumnKind.Date), new("APPROVED", ColumnKind.Date), new("DAYS", ColumnKind.Integer), new("LINES", ColumnKind.Integer), new("QTY", ColumnKind.Integer), new("REWORK", ColumnKind.Integer), new("STATUS") },
            Rows = Wirs.Select(w => new object?[] { w.WirNo, w.Subcontractor, w.Building, w.Level, w.System, w.Stage, w.Description, w.SubmittedAt, w.ApprovedAt, w.Days, w.Lines, w.Qty, w.Rework, w.Status }).ToList(),
        };
    }
}
