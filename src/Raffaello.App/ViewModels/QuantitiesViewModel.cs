using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.App.ViewModels;

public sealed class RoomSummary
{
    public string Building { get; init; } = "";
    public string Level { get; init; } = "";
    public string Room { get; init; } = "";
    public string RoomType { get; init; } = "";
    public int Lines { get; init; }
    public double GivenPct { get; init; }
    public double DonePct { get; init; }
    public string Status { get; init; } = "OK";
    public string Caption => $"{Building} {Level}  |  {RoomType}";
}

public sealed class StageTotalView
{
    public string Stage { get; init; } = "";
    public double Qs { get; init; }
    public double Given { get; init; }
    public double Remaining { get; init; }
    public double Done { get; init; }
    public double Claimed { get; init; }
    public double DonePct { get; init; }
}

/// <summary>REMAINING = TOTAL QS - GIVEN per room / system / stage, with the chain of the selected line.</summary>
public sealed partial class QuantitiesViewModel : PageViewModel
{
    public QuantitiesViewModel(PageContext ctx) : base(ctx)
    {
        Chain = new ChainPanelViewModel(ctx);
        Chain.AskRequested += () => ctx.Nav.OpenAsk();
    }

    public override string Key => "Quantities";
    public override string Title => "QUANTITIES";
    public override string Subtitle => "REMAINING = TOTAL QS - GIVEN, per room / system / stage";
    protected override bool HasCharts => true;

    public ChainPanelViewModel Chain { get; }
    public ObservableCollection<RoomSummary> Rooms { get; } = new();
    public ObservableCollection<ChainRow> Lines { get; } = new();
    public ObservableCollection<StageTotalView> StageTotals { get; } = new();
    public string[] StatusFilters { get; } = { "ALL", "OVER", "CHECK", "DUE", "OPEN", "OK" };

    [ObservableProperty] private string _statusFilter = "ALL";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private RoomSummary? _selectedRoom;
    [ObservableProperty] private ChainRow? _selectedLine;
    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private ISeries[] _roomBars = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _roomBarsX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _roomBarsY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _stageStack = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _stageStackX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _stageStackY = Array.Empty<Axis>();

    private bool _syncing;

    partial void OnStatusFilterChanged(string value) => ApplyLineFilter();
    partial void OnSearchChanged(string value) => ApplyLineFilter();
    partial void OnSelectedLineChanged(ChainRow? value)
    {
        Chain.Row = value;
        if (value != null) Project.Settings.LastLineId = value.Id;
    }

    partial void OnSelectedRoomChanged(RoomSummary? value)
    {
        if (_syncing || value is null) return;
        Ctx.Filter.Set(value.Building, value.Level, value.Room, Spec.System, Spec.Stage);
    }

    protected override void Refresh()
    {
        var rows = Scoped();
        var roomScope = new FilterSpec(Spec.Building, Spec.Level, null, Spec.System, Spec.Stage).Apply(Project.Chain).ToList();
        _syncing = true;
        Rooms.Clear();
        foreach (var g in roomScope.GroupBy(r => (r.Building, r.Level, r.Room)).OrderBy(g => g.Key.Building)
                     .ThenBy(g => Raffaello.Core.Analytics.ProjectAnalytics.LevelRank(g.Key.Level)).ThenBy(g => g.Key.Room, StringComparer.OrdinalIgnoreCase))
        {
            Rooms.Add(new RoomSummary
            {
                Building = g.Key.Building, Level = g.Key.Level, Room = g.Key.Room, RoomType = g.First().RoomType, Lines = g.Count(),
                GivenPct = ChainMath.MeanStagePct(g, t => t.GivenPct), DonePct = ChainMath.MeanStagePct(g, t => t.DonePct),
                Status = VerdictText.Of(g.Max(r => r.Verdict)),
            });
        }
        SelectedRoom = Spec.Room is null ? null : Rooms.FirstOrDefault(r => r.Room == Spec.Room);
        _syncing = false;

        StageTotals.Clear();
        foreach (var t in ChainMath.TotalsByStage(rows).Values)
            StageTotals.Add(new StageTotalView { Stage = t.Stage, Qs = t.Qs, Given = t.Given, Remaining = t.Qs - t.Given, Done = t.Done, Claimed = t.Claimed, DonePct = t.DonePct });

        ApplyLineFilter();
        BuildCharts(rows);
    }

    private void ApplyLineFilter()
    {
        var keep = SelectedLine?.Id;
        var q = Scoped().AsEnumerable();
        if (StatusFilter != "ALL") q = q.Where(r => r.Status == StatusFilter);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(r => r.Key.Contains(s, StringComparison.OrdinalIgnoreCase) || r.ItemCode.Contains(s, StringComparison.OrdinalIgnoreCase)
                             || r.Subcontractors.Contains(s, StringComparison.OrdinalIgnoreCase) || r.RoomType.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        var list = q.OrderByDescending(r => r.Verdict).ThenBy(r => r.Building).ThenBy(r => Raffaello.Core.Analytics.ProjectAnalytics.LevelRank(r.Level))
            .ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.System).ThenBy(r => Array.IndexOf(Stages.All, r.Stage)).ToList();
        Lines.Clear();
        foreach (var r in list) Lines.Add(r);
        CountText = $"{list.Count:N0} LINES  |  OVER {list.Count(r => r.Verdict == Verdict.Over)}  |  CHECK {list.Count(r => r.Verdict == Verdict.Check)}  |  DUE {list.Count(r => r.Verdict == Verdict.Due)}";
        SelectedLine = keep is long id ? Lines.FirstOrDefault(r => r.Id == id) ?? Lines.FirstOrDefault() : Lines.FirstOrDefault();
    }

    private void BuildCharts(List<ChainRow> rows)
    {
        var rooms = rows.GroupBy(r => r.Room).Select(g => (Room: g.Key, Qs: g.GroupBy(x => x.Stage).Max(st => st.Sum(x => x.Qs)),
                Given: ChainMath.MeanStagePct(g, t => t.GivenPct), Done: ChainMath.MeanStagePct(g, t => t.DonePct)))
            .OrderByDescending(x => x.Qs).Take(16).OrderBy(x => x.Room, StringComparer.OrdinalIgnoreCase).ToList();
        RoomBars = new ISeries[]
        {
            ChartKit.Columns("GIVEN %", rooms.Select(r => r.Given), ChartKit.Graphite, 12),
            ChartKit.Columns("DONE %", rooms.Select(r => r.Done), ChartKit.Accent, 12),
        };
        RoomBarsX = new[] { ChartKit.XLabels(rooms.Select(r => r.Room), rooms.Count > 10 ? 45 : 0) };
        RoomBarsY = new[] { ChartKit.YPercent() };

        var totals = ChainMath.TotalsByStage(rows).Values.ToList();
        StageStack = new ISeries[]
        {
            new StackedColumnSeries<double> { Name = "DONE", Values = totals.Select(t => Math.Min(t.Done, t.Qs)).ToArray(), Fill = ChartKit.Fill(ChartKit.Accent), Rx = 4, Ry = 4, MaxBarWidth = 46 },
            new StackedColumnSeries<double> { Name = "GIVEN, NOT DONE", Values = totals.Select(t => Math.Max(0, Math.Min(t.Given, t.Qs) - t.Done)).ToArray(), Fill = ChartKit.Fill(ChartKit.Yellow), Rx = 4, Ry = 4, MaxBarWidth = 46 },
            new StackedColumnSeries<double> { Name = "REMAINING", Values = totals.Select(t => Math.Max(0, t.Qs - t.Given)).ToArray(), Fill = ChartKit.Fill(ChartKit.Graphite), Rx = 4, Ry = 4, MaxBarWidth = 46 },
        };
        StageStackX = new[] { ChartKit.XLabels(totals.Select(t => t.Stage)) };
        StageStackY = new[] { ChartKit.YValues() };
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.LineId is long id && Project.ChainById.TryGetValue(id, out var row))
        {
            StatusFilter = "ALL";
            Search = "";
            Ctx.Filter.Set(row.Building, row.Level, row.Room, null, null);
            SelectedLine = Lines.FirstOrDefault(r => r.Id == id);
        }
        else if (target.Key is "OVER" or "CHECK" or "DUE" or "OPEN")
        {
            StatusFilter = target.Key;
        }
        if (target.LineId.HasValue && target.Key is "OVER" or "CHECK" or "DUE") StatusFilter = "ALL";
    }

    [RelayCommand] private void ClearRoom() => Ctx.Filter.Set(Spec.Building, Spec.Level, null, Spec.System, Spec.Stage);
    [RelayCommand] private void SetStatus(string? s) => StatusFilter = s ?? "ALL";

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return ReportBuilder.ChainSheet("QUANTITIES", Lines, $"QUANTITIES - {Spec.Describe()}");
        yield return new ExportSheet
        {
            Name = "BY STAGE", Title = "TOTALS BY STAGE (NEVER SUMMED)",
            Columns = new() { new("STAGE"), new("QS", ColumnKind.Integer), new("GIVEN", ColumnKind.Integer), new("REMAINING", ColumnKind.Integer), new("DONE", ColumnKind.Integer), new("CLAIMED", ColumnKind.Integer), new("DONE %", ColumnKind.Percent) },
            Rows = StageTotals.Select(t => new object?[] { t.Stage, t.Qs, t.Given, t.Remaining, t.Done, t.Claimed, t.DonePct }).ToList(),
        };
    }
}
