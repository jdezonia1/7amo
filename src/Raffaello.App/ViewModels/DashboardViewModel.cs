using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using Raffaello.App.Services;
using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.App.ViewModels;

public sealed class KpiTile
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; init; } = "";
    public ISeries[] Spark { get; init; } = Array.Empty<ISeries>();
    public Axis[] SparkX { get; init; } = { ChartKit.Hidden() };
    public Axis[] SparkY { get; init; } = { ChartKit.Hidden() };
}

public sealed record ActivityItem(string When, string User, string Action, string Summary);

public sealed partial class DashboardViewModel : PageViewModel
{
    public DashboardViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Dashboard";
    public override string Title => "DASHBOARD";
    public override string Subtitle => "Every problem is a mismatch in the chain";
    protected override bool HasCharts => true;

    public ObservableCollection<KpiTile> Kpis { get; } = new();
    public ObservableCollection<QueueItem> Queue { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();
    public ObservableCollection<HeatCell> HeatCells { get; } = new();

    [ObservableProperty] private string _heroValue = "";
    [ObservableProperty] private string _heroDetail = "";
    [ObservableProperty] private string _forecastText = "";
    [ObservableProperty] private string _forecastDetail = "";
    [ObservableProperty] private string _queueCount = "";
    [ObservableProperty] private string _funnelNote = "";

    [ObservableProperty] private ISeries[] _sCurve = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _sCurveX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _sCurveY = Array.Empty<Axis>();

    [ObservableProperty] private ISeries[] _funnel = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _funnelX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _funnelY = Array.Empty<Axis>();

    [ObservableProperty] private ISeries[] _heat = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _heatX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _heatY = Array.Empty<Axis>();

    [ObservableProperty] private ISeries[] _donut = Array.Empty<ISeries>();
    [ObservableProperty] private string _donutTotal = "";

    [ObservableProperty] private ISeries[] _ageing = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _ageingX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _ageingY = Array.Empty<Axis>();

    [ObservableProperty] private ISeries[] _cash = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _cashX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _cashY = Array.Empty<Axis>();

    [ObservableProperty] private ISeries[] _systems = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _systemsX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _systemsY = Array.Empty<Axis>();

    protected override void Refresh()
    {
        var p = Project;
        var s = p.Snapshot;
        var rows = Scoped();
        var spec = Spec;
        var wirsInScope = s.Wirs.Where(w => w.Kind == "WIR" && (spec.Building is null || w.Building == spec.Building) && (spec.Level is null || w.Level == spec.Level)
                                            && (spec.System is null || w.System == spec.System) && (spec.Stage is null || w.Stage == spec.Stage)).ToList();

        // ---------------- S-curve + forecast
        var curve = ProjectAnalytics.SCurve(s, rows, p.ProjectStart, p.PlannedFinish, Today);
        var fc = ProjectAnalytics.ForecastCompletion(curve);
        var labels = curve.Select(c => c.WeekStart.ToString("dd MMM")).ToList();
        var actual = curve.Select(c => c.Actual).ToList();
        var lastIdx = actual.FindLastIndex(a => a.HasValue);
        var forecast = new double?[curve.Count];
        if (lastIdx >= 0 && fc.SlopePerWeek > 0)
            for (var i = lastIdx; i < curve.Count; i++)
                forecast[i] = Math.Min(1.0, actual[lastIdx]!.Value + fc.SlopePerWeek * (i - lastIdx));
        SCurve = new ISeries[]
        {
            ChartKit.Line("PLANNED", curve.Select(c => (double?)c.Planned), ChartKit.Graphite, dashed: true, width: 2),
            ChartKit.Line("ACTUAL", actual, ChartKit.Accent, area: true),
            ChartKit.Line("FORECAST", forecast, ChartKit.Yellow, dashed: true, width: 2),
        };
        SCurveX = new[] { ChartKit.XLabels(labels) };
        SCurveX[0].ForceStepToMin = false;
        SCurveX[0].MinStep = 4;
        SCurveY = new[] { ChartKit.YPercent() };
        var nowPt = lastIdx >= 0 ? curve[lastIdx] : null;
        var gap = nowPt is null ? 0 : nowPt.Actual!.Value - nowPt.Planned;
        ForecastText = fc.CompletionDate is DateTime d ? d.ToString("dd MMM yyyy").ToUpperInvariant() : "NOT ENOUGH DATA";
        ForecastDetail = nowPt is null ? "" :
            $"Actual {nowPt.Actual:P1} vs planned {nowPt.Planned:P1} ({(gap >= 0 ? "+" : "")}{gap * 100:N1} pts). Trend {fc.SlopePerWeek * 100:N2} pts/week over {fc.PointsUsed} weeks, R² {fc.RSquared:N2}. Planned finish {p.PlannedFinish:dd MMM yyyy}.";

        // ---------------- KPIs
        var certified = rows.Sum(r => r.CertifiedValue);
        var claimedV = rows.Sum(r => r.ClaimedValue);
        HeroValue = $"SAR {certified:N0}";
        HeroDetail = $"of SAR {claimedV:N0} claimed  |  held SAR {Math.Max(0, claimedV - rows.Sum(r => r.CertifiableQty * r.Rate)):N0}";
        var weeklyDone = ProjectAnalytics.WeeklyDone(s, rows, Today);
        var weeklyWirs = ProjectAnalytics.WeeklyWirCount(wirsInScope, Today);
        var openWirs = wirsInScope.Count(w => w.Status == WirStatus.Open);
        var lateWirs = wirsInScope.Count(w => w.Status == WirStatus.Open && (Today - w.SubmittedAt.Date).TotalDays > p.Options.WirDueDays);
        var over = rows.Count(r => r.Verdict == Verdict.Over);
        var check = rows.Count(r => r.Verdict == Verdict.Check);
        var meanDone = ChainMath.MeanStagePct(rows, t => t.DonePct);
        var meanClaim = ChainMath.MeanStagePct(rows, t => t.ClaimedPct);
        Kpis.Clear();
        Kpis.Add(new KpiTile
        {
            Label = "PROGRESS (MEAN OF STAGES)", Value = $"{meanDone:P1}", Detail = nowPt is null ? "" : $"planned {nowPt.Planned:P0}",
            Status = gap < -0.05 ? "CHECK" : "OK", Spark = new ISeries[] { ChartKit.Spark(weeklyDone, ChartKit.Accent) },
        });
        Kpis.Add(new KpiTile
        {
            Label = "OPEN WIRS", Value = $"{openWirs:N0}", Detail = $"{lateWirs} older than {p.Options.WirDueDays} days", Status = lateWirs > 0 ? "DUE" : "OK",
            Spark = new ISeries[] { ChartKit.Spark(weeklyWirs, ChartKit.Yellow) },
        });
        Kpis.Add(new KpiTile
        {
            Label = "OVER / CHECK LINES", Value = $"{over:N0} / {check:N0}", Detail = $"of {rows.Count:N0} lines in scope", Status = over > 0 ? "OVER" : check > 0 ? "CHECK" : "OK",
            Spark = new ISeries[] { ChartKit.Spark(new[] { (double)rows.Count(r => r.Verdict == Verdict.Ok), rows.Count(r => r.Verdict == Verdict.Open), rows.Count(r => r.Verdict == Verdict.Due), check, over }, ChartKit.Graphite) },
        });
        Kpis.Add(new KpiTile
        {
            Label = "CLAIMED vs WIR", Value = $"{meanClaim:P0} / {meanDone:P0}", Detail = meanClaim > meanDone ? "claims ahead of WIR - hold" : "claims within WIR",
            Status = meanClaim > meanDone + 0.005 ? "CHECK" : "OK",
            Spark = new ISeries[] { ChartKit.Spark(ProjectAnalytics.CashFlow(s.Invoices).Select(m => m.Claimed), ChartKit.Good) },
        });

        // ---------------- real workflow data (room ledger, checks, subcontractor invoices)
        if (s.Claims.Count > 0 || s.RoomQtys.Count > 0)
        {
            var bal = Raffaello.Core.Ledger.LedgerRules.Balances(s.RoomQtys, s.Claims);
            var overCap = bal.Values.Count(b => b.HasCap && b.IsOver);
            var noCap = bal.Values.Count(b => !b.HasCap && b.Claimed > 0);
            var pendingChecks = s.Claims.Count(c => Raffaello.Core.Ledger.HeightCheck.IsPending(c) || Raffaello.Core.Ledger.LengthCheck.IsPending(c));
            var used = bal.Values.Where(b => b.HasCap).Sum(b => Math.Min(b.Claimed, b.ProjectQty));
            var cap = bal.Values.Where(b => b.HasCap).Sum(b => b.ProjectQty);
            Kpis.Insert(0, new KpiTile
            {
                Label = "ROOM LEDGER (CLAIMED / PROJECT QTY)", Value = cap <= 0 ? "-" : $"{used / cap:P1}",
                Detail = $"{s.Claims.Count:N0} lines  |  {overCap} over cap  |  {noCap} without PROJECT QTY", Status = overCap > 0 ? "OVER" : noCap > 0 ? "CHECK" : "OK",
                Spark = new ISeries[] { ChartKit.Spark(s.Claims.Where(c => c.InvoiceNo > 0).GroupBy(c => c.InvoiceNo).OrderBy(g => g.Key).Select(g => g.Sum(c => c.Qty)), ChartKit.Accent) },
            });
            var openInv = s.SubInvoices.Count(i => i.Status is SubInvoiceStatus.Submitted or SubInvoiceStatus.Rejected);
            Kpis.Insert(1, new KpiTile
            {
                Label = "CHECKS PENDING / INVOICES OPEN", Value = $"{pendingChecks:N0} / {openInv:N0}",
                Detail = $"{s.SubInvoices.Count(i => i.Status == SubInvoiceStatus.Approved)} invoices approved", Status = pendingChecks > 0 ? "DUE" : openInv > 0 ? "CHECK" : "OK",
                Spark = new ISeries[] { ChartKit.Spark(s.SubInvoices.Where(i => i.Status == SubInvoiceStatus.Approved).OrderBy(i => i.InvoiceNo)
                    .Select(i => Raffaello.Core.Invoicing.InvoiceTotals.Of(i, s.SubInvoiceLines.Where(l => l.SubInvoiceId == i.Id)).CurrGross), ChartKit.Good) },
            });
            Kpis.RemoveAt(Kpis.Count - 1);
        }

        // ---------------- chain funnel (per stage, never summed)
        var stages = ChainMath.TotalsByStage(rows);
        var links = new[] { "QS", "GIVEN", "DONE", "CLAIMED", "CERTIFIED", "DELIVERED" };
        Funnel = stages.Values.Select((t, i) => (ISeries)ChartKit.Columns(t.Stage, new[] { t.Qs, t.Given, t.Done, t.Claimed, t.Certified, t.Delivered ?? 0 }, ChartKit.At(i), 18)).ToArray();
        FunnelX = new[] { ChartKit.XLabels(links) };
        FunnelY = new[] { ChartKit.YValues() };
        FunnelNote = stages.Count > 1 ? "Stages side by side - never added together" : stages.Keys.FirstOrDefault() ?? "";

        // ---------------- heatmap level x system (% given)
        var cells = ProjectAnalytics.GivenHeatmap(rows);
        var levels = cells.Select(c => c.Level).Distinct().OrderBy(ProjectAnalytics.LevelRank).ToList();
        var systems = cells.Select(c => c.System).Distinct().OrderBy(x => Array.IndexOf(Raffaello.Core.Domain.Systems.Main, x)).ToList();
        HeatCells.Clear();
        foreach (var c in cells) HeatCells.Add(c);
        Heat = new ISeries[] { ChartKit.Heat(cells.Select(c => new WeightedPoint(systems.IndexOf(c.System), levels.IndexOf(c.Level), Math.Round(c.Pct * 100)))) };
        HeatX = new[] { ChartKit.XLabels(systems) };
        HeatY = new[] { ChartKit.XLabels(levels) };

        // ---------------- donut points by system
        var pts = ProjectAnalytics.PointsBySystem(rows);
        Donut = pts.Select((v, i) => (ISeries)ChartKit.Slice(v.Name, v.Value, ChartKit.At(i))).ToArray();
        DonutTotal = $"{pts.Sum(v => v.Value):N0}";

        // ---------------- WIR ageing
        var ageing = ProjectAnalytics.WirAgeing(wirsInScope, Today);
        var colours = new[] { ChartKit.Good, ChartKit.Yellow, ChartKit.Accent, ChartKit.Sk("Ink") };
        Ageing = ageing.Select((b, i) =>
        {
            var vals = new double[4];
            vals[i] = b.Count;
            var series = ChartKit.Columns(b.Label, vals, colours[i], 34);
            series.IgnoresBarPosition = true;
            return (ISeries)series;
        }).ToArray();
        AgeingX = new[] { ChartKit.XLabels(ageing.Select(a => a.Label)) };
        AgeingY = new[] { ChartKit.YValues() };

        // ---------------- cash flow
        var cf = ProjectAnalytics.CashFlow(s.Invoices.Where(i => spec.Building is null || s.InvoiceLines.Any(l => l.InvoiceId == i.Id && p.ChainById.TryGetValue(l.LineId, out var r) && r.Building == spec.Building)));
        var cum = ChartKit.Line("CUMULATIVE CERTIFIED", cf.Select(m => (double?)m.CumulativeCertified), ChartKit.Graphite, width: 2);
        cum.ScalesYAt = 1;
        cum.GeometrySize = 7;
        cum.GeometryFill = ChartKit.Fill(ChartKit.Sk("Surface"));
        cum.GeometryStroke = ChartKit.Stroke(ChartKit.Graphite, 2);
        Cash = new ISeries[]
        {
            ChartKit.Columns("CLAIMED", cf.Select(m => m.Claimed), ChartKit.Yellow, 22),
            ChartKit.Columns("CERTIFIED", cf.Select(m => m.Certified), ChartKit.Accent, 22),
            cum,
        };
        CashX = new[] { ChartKit.XLabels(cf.Select(m => m.Month.ToString("MMM yy").ToUpperInvariant())) };
        var y2 = ChartKit.YValues(v => $"{v / 1000:N0}K");
        y2.Position = LiveChartsCore.Measure.AxisPosition.End;
        y2.SeparatorsPaint = null;
        CashY = new[] { ChartKit.YValues(v => $"{v / 1000:N0}K"), y2 };

        // ---------------- progress by system per stage
        var sysOrder = rows.Select(r => r.System).Distinct().OrderBy(x => Array.IndexOf(Raffaello.Core.Domain.Systems.Main, x)).ToList();
        Systems = Raffaello.Core.Domain.Stages.All.Where(st => rows.Any(r => r.Stage == st)).Select((st, i) => (ISeries)ChartKit.Columns(st,
            sysOrder.Select(sys => { var g = rows.Where(r => r.Stage == st && r.System == sys).ToList(); var qs = g.Sum(r => r.Qs); return qs <= 0 ? 0 : g.Sum(r => r.Done) / qs; }), ChartKit.At(i), 14)).ToArray();
        SystemsX = new[] { ChartKit.XLabels(sysOrder) };
        SystemsY = new[] { ChartKit.YPercent() };

        // ---------------- queue + activity
        var queue = spec == FilterSpec.All ? p.Queue.ToList() : NeedsTodayQueue.Build(s, rows, p.Options);
        Queue.Clear();
        foreach (var q in queue.Take(30)) Queue.Add(q);
        QueueCount = $"{queue.Count} ITEMS";
        Activity.Clear();
        foreach (var a in p.RecentActivity(25))
            Activity.Add(new(a.At.ToString("dd MMM HH:mm"), a.User, a.Action, a.Summary));
    }

    [RelayCommand]
    private void OpenQueue(QueueItem? item)
    {
        if (item != null) Ctx.Nav.Go(item.Target.Module, item.Target);
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        var p = Project;
        return ReportBuilder.WeeklyReport(p.Snapshot, Scoped(), p.Options, p.ProjectStart, p.PlannedFinish, p.Settings.EffectiveUserName);
    }
}
