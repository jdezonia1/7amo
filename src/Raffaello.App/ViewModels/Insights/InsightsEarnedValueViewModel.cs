using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;

namespace Raffaello.App.ViewModels.Insights;

public sealed record WarningRow(string Tag, string Subject, string Message, string Action);

/// <summary>[insights] Planned vs actual per stage / system (ledger S-curve), productivity per subcontractor, forecast finish per area, early warnings.</summary>
public sealed partial class InsightsEarnedValueViewModel : PageViewModel
{
    private readonly InsightsHub _hub;
    private EvResult? _last;
    private int _run;

    public InsightsEarnedValueViewModel(PageContext ctx, InsightsHub hub) : base(ctx) => _hub = hub;

    public override string Key => "EarnedValue";
    public override string Title => "EARNED VALUE & PRODUCTIVITY";
    public override string Subtitle => "Planned vs actual per stage and system, points per week per subcontractor, forecast finish per area, early warnings";
    public override bool ShowFilterBar => false;
    protected override bool HasCharts => true;

    public ObservableCollection<EvRow> Rows { get; } = new();
    public ObservableCollection<ProductivityRow> Productivity { get; } = new();
    public ObservableCollection<AreaForecast> Areas { get; } = new();
    public ObservableCollection<WarningRow> Warnings { get; } = new();
    public ObservableCollection<InsightInvoicePeriod> Periods { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _spi;
    [ObservableProperty] private string _actualText = "";
    [ObservableProperty] private string _plannedText = "";
    [ObservableProperty] private ISeries[] _curve = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _curveX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _curveY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _prodChart = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _prodX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _prodY = Array.Empty<Axis>();

    protected override void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var run = ++_run;
        StatusText = "Calculating...";
        try
        {
            var p = Project; var building = BuildingFilter;
            var data = await Task.Run(() => _hub.LoadData());
            var r = await Task.Run(() => EarnedValue.Build(new EvInputs { Project = p.Snapshot, Data = data, Today = p.Options.Today, ProjectStart = p.ProjectStart, PlannedFinish = p.PlannedFinish, Building = building }));
            if (run != _run) return;
            _last = r;
            Rows.Clear(); foreach (var x in r.Rows.Where(x => x.PlanPoints > 0).OrderBy(x => x.Spi)) Rows.Add(x);
            Productivity.Clear(); foreach (var x in r.Productivity) Productivity.Add(x);
            Areas.Clear(); foreach (var x in r.Areas.OrderByDescending(a => a.SlipDays ?? double.MinValue)) Areas.Add(x);
            Warnings.Clear(); foreach (var w in r.Warnings.OrderByDescending(w => w.Severity)) Warnings.Add(new WarningRow(w.Severity.ToString().ToUpperInvariant(), w.Subject, w.Message, w.Action));
            Notes.Clear(); foreach (var n in r.Notes) Notes.Add(n);
            Periods.Clear();
            foreach (var x in data.InvoicePeriods.OrderBy(x => x.Subcontractor).ThenBy(x => x.InvoiceNo)) Periods.Add(x);
            Spi = r.Spi;
            var now = r.Curve.LastOrDefault(c => c.Actual.HasValue);
            ActualText = now?.Actual?.ToString("P1") ?? "-";
            PlannedText = now?.Planned.ToString("P1") ?? "-";

            Curve = new ISeries[]
            {
                ChartKit.Line("PLANNED", r.Curve.Select(c => (double?)c.Planned), ChartKit.Graphite, dashed: true, width: 2),
                ChartKit.Line("ACTUAL (LEDGER)", r.Curve.Select(c => c.Actual), ChartKit.Accent, width: 3, area: true),
            };
            CurveX = new[] { ChartKit.XLabels(r.Curve.Select(c => c.WeekStart.ToString("dd MMM").ToUpperInvariant()), 45) };
            CurveX[0].MinStep = Math.Max(1, r.Curve.Count / 12);
            CurveY = new[] { ChartKit.YPercent() };

            var subs = r.Productivity.Take(8).ToList();
            var maxPeriods = subs.Select(s => s.Periods.Count).DefaultIfEmpty().Max();
            var dated = subs.Any(s => s.Dated);
            ProdChart = Enumerable.Range(0, Math.Min(maxPeriods, 6)).Select(k => (ISeries)ChartKit.Columns($"INVOICE {k + 1}",
                subs.Select(s => k < s.Periods.Count ? (dated ? s.Periods[k].PointsPerWeek ?? 0 : s.Periods[k].Points) : 0), ChartKit.At(k), 14)).ToArray();
            ProdX = new[] { ChartKit.XLabels(subs.Select(s => s.Subcontractor), subs.Count > 5 ? 20 : 0) };
            ProdY = new[] { ChartKit.YValues(v => v.ToString("N0")) };
            StatusText = (dated ? "points per week per invoice period" : "points per invoice (enter invoice period dates for points per week)") + $"  |  {building ?? "ALL BUILDINGS"}";
        }
        catch (Exception ex) { StatusText = "Earned value failed: " + ex.Message; }
    }

    [RelayCommand]
    private void AddMissingPeriods()
    {
        foreach (var g in Project.Snapshot.Claims.Where(c => c.InvoiceNo > 0).Select(c => (Sub: c.Subcontractor.Trim(), c.InvoiceNo)).Distinct().OrderBy(x => x.Sub).ThenBy(x => x.InvoiceNo))
            if (!Periods.Any(p => p.Subcontractor.Equals(g.Sub, StringComparison.OrdinalIgnoreCase) && p.InvoiceNo == g.InvoiceNo))
                Periods.Add(new InsightInvoicePeriod { Subcontractor = g.Sub, InvoiceNo = g.InvoiceNo, PeriodEnd = DateTime.Today, Note = "enter the end of the period" });
    }

    [RelayCommand]
    private async Task SavePeriods()
    {
        try
        {
            var rows = Periods.Where(p => p.Subcontractor.Trim().Length > 0 && p.InvoiceNo > 0 && p.PeriodEnd != default).ToList();
            await Task.Run(() => _hub.Store.ReplaceAll(rows, $"Invoice period dates saved ({rows.Count})"));
            Ctx.Toasts.Show("INVOICE PERIODS SAVED", $"{rows.Count} invoice periods dated.", ToastKind.Good);
            await Ctx.Data.ReloadAsync();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT SAVE", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView() =>
        _last is null ? Array.Empty<ExportSheet>() : EarnedValue.Sheets(_last, $"{BuildingFilter ?? "ALL BUILDINGS"}  |  {DateTime.Today:dd MMM yyyy}");

    [RelayCommand] private void Export() => Ctx.Exports.Export("RAFFAELLO_EARNED_VALUE", ExportCurrentView());
}
