using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;

namespace Raffaello.App.ViewModels.Insights;

/// <summary>[insights] The same / similar item across subcontract schedules, POs and the owner BOQ: min / max / average, outliers, margin.</summary>
public sealed partial class InsightsRatesViewModel : PageViewModel
{
    private readonly InsightsHub _hub;
    private List<BenchmarkGroup> _all = new();
    private int _run;

    public InsightsRatesViewModel(PageContext ctx, InsightsHub hub) : base(ctx) => _hub = hub;

    public override string Key => "RateBenchmark";
    public override string Title => "RATE BENCHMARK";
    public override string Subtitle => "Same item across subcontract contracts, POs and the owner BOQ - min / max / average, outliers, margin (owner rate - cost rate)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;
    protected override bool HasCharts => true;

    public string[] Views { get; } = { "COMPARABLE", "OUTLIERS", "WITH OWNER RATE", "ALL GROUPS" };
    public ObservableCollection<BenchmarkGroup> Groups { get; } = new();
    public ObservableCollection<BenchmarkEntry> Entries { get; } = new();

    [ObservableProperty] private string _view = "COMPARABLE";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private BenchmarkGroup? _selected;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private ISeries[] _chart = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _chartX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _chartY = Array.Empty<Axis>();

    partial void OnViewChanged(string value) => Apply();
    partial void OnSearchChanged(string value) => Apply();
    partial void OnSelectedChanged(BenchmarkGroup? value)
    {
        Entries.Clear();
        if (value is null) { Chart = Array.Empty<ISeries>(); return; }
        var list = value.Entries.OrderBy(e => e.Source).ThenBy(e => e.Rate).ToList();
        foreach (var e in list) Entries.Add(e);
        var cost = list.Where(e => e.Source != RateSources.Owner).ToList();
        var series = new List<ISeries> { ChartKit.Columns("RATE", cost.Select(e => e.Rate), ChartKit.Accent, 30) };
        series.Add(ChartKit.Line("MEDIAN", cost.Select(_ => (double?)value.Median), ChartKit.Graphite, dashed: true, width: 2));
        if (value.OwnerRate is double o) series.Add(ChartKit.Line("OWNER RATE", cost.Select(_ => (double?)o), ChartKit.Good, width: 2));
        Chart = series.ToArray();
        ChartX = new[] { ChartKit.XLabels(cost.Select(e => Short(e.Party) + " " + e.Ref), cost.Count > 5 ? 30 : 0) };
        ChartY = new[] { ChartKit.YValues(v => v.ToString("N0")) };
    }

    private static string Short(string s) => s.Length > 22 ? s[..22] : s;

    protected override void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var run = ++_run;
        StatusText = "Benchmarking...";
        try
        {
            var p = Project;
            var mats = await Task.Run(() => _hub.LoadMaterials());
            var groups = await Task.Run(() => RateBenchmark.Build(p.Snapshot, mats));
            if (run != _run) return;
            _all = groups;
            var cmp = RateBenchmark.Comparable(groups).ToList();
            StatusText = $"{groups.Count} item groups  |  {cmp.Count} comparable  |  {cmp.Sum(g => g.Outliers)} outlier rates  |  {cmp.Count(g => g.OwnerRate.HasValue)} with owner rate";
            Apply();
        }
        catch (Exception ex) { StatusText = "Benchmark failed: " + ex.Message; }
    }

    private void Apply()
    {
        IEnumerable<BenchmarkGroup> q = View switch
        {
            "OUTLIERS" => _all.Where(g => g.Outliers > 0),
            "WITH OWNER RATE" => _all.Where(g => g.OwnerRate.HasValue),
            "ALL GROUPS" => _all,
            _ => RateBenchmark.Comparable(_all),
        };
        if (!string.IsNullOrWhiteSpace(Search))
            q = q.Where(g => g.Label.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || g.Entries.Any(e => e.Party.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || e.Ref.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase)));
        var keep = Selected?.Key;
        Groups.Clear();
        foreach (var g in q) Groups.Add(g);
        Selected = Groups.FirstOrDefault(g => g.Key == keep) ?? Groups.FirstOrDefault();
    }

    public override IEnumerable<ExportSheet> ExportCurrentView() => RateBenchmark.Sheets(_all, $"{DateTime.Today:dd MMM yyyy}");

    [RelayCommand] private void Export() => Ctx.Exports.Export("RAFFAELLO_RATE_BENCHMARK", ExportCurrentView());
}
