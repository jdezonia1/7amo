using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;

namespace Raffaello.App.ViewModels.Insights;

/// <summary>[insights] Monthly payables (subcontractors, suppliers) vs receivables (owner) from remaining quantities x rates, terms and the programme.</summary>
public sealed partial class InsightsCashFlowViewModel : PageViewModel
{
    private readonly InsightsHub _hub;
    private CashFlowResult? _last;
    private int _run;

    public InsightsCashFlowViewModel(PageContext ctx, InsightsHub hub) : base(ctx) => _hub = hub;

    public override string Key => "CashFlow";
    public override string Title => "CASH-FLOW FORECAST";
    public override string Subtitle => "Remaining work x rates, payment terms (stage %, retention, LC before delivery) and the programme -> monthly payables vs owner receipts";
    public override bool ShowFilterBar => false;
    protected override bool HasCharts => true;

    public ObservableCollection<CashMonth> Months { get; } = new();
    public ObservableCollection<string> Assumptions { get; } = new();
    public ObservableCollection<InsightProgramme> Programme { get; } = new();
    public ObservableCollection<InsightPaymentTerm> Terms { get; } = new();
    public string[] Schemes { get; } = PaymentSchemes.All;
    public static readonly string[] PartyOptions = { PaymentParties.Subcontract, PaymentParties.Supplier, PaymentParties.Owner };

    [ObservableProperty] private int _delayWeeks;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _totalPayables;
    [ObservableProperty] private double _totalReceipts;
    [ObservableProperty] private double _lowestCumulative;
    [ObservableProperty] private string _lowestMonth = "";
    [ObservableProperty] private string _finishText = "";
    [ObservableProperty] private ISeries[] _chart = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _chartX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _chartY = Array.Empty<Axis>();

    partial void OnDelayWeeksChanged(int value) => _ = LoadAsync(false);

    protected override void Refresh() => _ = LoadAsync(true);

    private async Task LoadAsync(bool reloadTables)
    {
        var run = ++_run;
        StatusText = "Forecasting...";
        try
        {
            var p = Project; var building = BuildingFilter; var delay = DelayWeeks;
            var data = await Task.Run(() => _hub.LoadData());
            var mats = await Task.Run(() => _hub.LoadMaterials());
            if (reloadTables)
            {
                Programme.Clear(); foreach (var x in data.Programme.OrderBy(x => x.PlannedStart)) Programme.Add(x);
                Terms.Clear(); foreach (var x in data.Terms) Terms.Add(x);
            }
            else
            {   // unsaved edits take part in the scenario
                data = new InsightsData { Dismissals = data.Dismissals, Thresholds = data.Thresholds, Norms = data.Norms, InvoicePeriods = data.InvoicePeriods, FileHashes = data.FileHashes,
                    Programme = Programme.ToList(), Terms = Terms.ToList() };
            }
            var r = await Task.Run(() => CashFlowForecast.Build(new CashFlowInputs
            {
                Project = p.Snapshot, Materials = mats, Data = data, Today = p.Options.Today, ProjectStart = p.ProjectStart, PlannedFinish = p.PlannedFinish, DelayWeeks = delay, Building = building,
            }));
            if (run != _run) return;
            _last = r;
            Months.Clear(); foreach (var m in r.Months) Months.Add(m);
            Assumptions.Clear(); foreach (var a in r.Assumptions) Assumptions.Add(a);
            TotalPayables = r.TotalPayables; TotalReceipts = r.TotalReceipts; LowestCumulative = r.PeakNegative;
            LowestMonth = r.PeakNegativeMonth?.ToString("MMM yyyy") ?? "-";
            FinishText = r.Finish.ToString("dd MMM yyyy");
            var cum = ChartKit.Line("CUMULATIVE NET", r.Months.Select(m => (double?)m.Cumulative), ChartKit.Graphite, width: 2);
            cum.ScalesYAt = 1;
            Chart = new ISeries[]
            {
                ChartKit.Columns("SUBCONTRACTORS", r.Months.Select(m => -m.SubPayables), ChartKit.Accent, 18),
                ChartKit.Columns("SUPPLIERS", r.Months.Select(m => -m.SupplierPayables), ChartKit.Yellow, 18),
                ChartKit.Columns("OWNER RECEIPTS", r.Months.Select(m => m.OwnerReceipts), ChartKit.Good, 18),
                cum,
            };
            ChartX = new[] { ChartKit.XLabels(r.Months.Select(m => m.Label.ToUpperInvariant()), r.Months.Count > 14 ? 30 : 0) };
            var y2 = ChartKit.YValues(v => $"{v / 1_000_000:N1}M", null);
            y2.Position = LiveChartsCore.Measure.AxisPosition.End;
            y2.SeparatorsPaint = null;
            ChartY = new[] { ChartKit.YValues(v => $"{v / 1_000_000:N1}M", null), y2 };
            StatusText = $"{r.Months.Count} months  |  {r.Events.Count} payment lines  |  {building ?? "ALL BUILDINGS"}" + (delay > 0 ? $"  |  scenario: {delay} weeks delay" : "");
        }
        catch (Exception ex) { StatusText = "Forecast failed: " + ex.Message; }
    }

    [RelayCommand] private Task Recalculate() => LoadAsync(false);

    [RelayCommand]
    private void DefaultProgramme()
    {
        var p = Project;
        foreach (var st in new[] { "1ST FIX", "2ND FIX", "FINAL FIX" })
        {
            var (a, b) = ProgrammeModel.DefaultPhase(st);
            var span = (p.PlannedFinish - p.ProjectStart).TotalDays;
            Programme.Add(new InsightProgramme { Building = BuildingFilter ?? "", Area = "ALL", Stage = st, PlannedStart = p.ProjectStart.AddDays(span * a).Date, PlannedFinish = p.ProjectStart.AddDays(span * b).Date, Note = "default spread - edit" });
        }
    }

    [RelayCommand]
    private void DefaultTerms()
    {
        foreach (var party in PartyOptions)
            if (!Terms.Any(t => t.Party == party && t.Ref == "*"))
            {
                var t = CashFlowForecast.Term(new InsightsData(), party, "*");
                Terms.Add(new InsightPaymentTerm { Party = party, Ref = "*", Name = t.Name, Scheme = t.Scheme, RetentionPct = t.RetentionPct, PayDays = t.PayDays, HandoverMonths = t.HandoverMonths, LcDaysBeforeDelivery = t.LcDaysBeforeDelivery });
            }
    }

    [RelayCommand]
    private async Task Save()
    {
        try
        {
            var prog = Programme.Where(x => x.PlannedFinish > x.PlannedStart).ToList();
            var terms = Terms.Where(x => x.Party.Length > 0).ToList();
            await Task.Run(() =>
            {
                _hub.Store.ReplaceAll(prog, $"Programme saved ({prog.Count} windows)");
                _hub.Store.ReplaceAll(terms, $"Payment terms saved ({terms.Count})");
            });
            Ctx.Toasts.Show("PROGRAMME AND TERMS SAVED", $"{prog.Count} programme windows, {terms.Count} payment terms.", ToastKind.Good);
            await Ctx.Data.ReloadAsync();
        }
        catch (Exception ex) { Ctx.Toasts.Show("COULD NOT SAVE", ex.Message, ToastKind.Error); }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView() =>
        _last is null ? Array.Empty<ExportSheet>() : CashFlowForecast.Sheets(_last, $"{BuildingFilter ?? "ALL BUILDINGS"}  |  {DateTime.Today:dd MMM yyyy}" + (DelayWeeks > 0 ? $"  |  scenario {DelayWeeks} weeks delay" : ""));

    [RelayCommand] private void Export() => Ctx.Exports.Export("RAFFAELLO_CASH_FLOW", ExportCurrentView());
}
