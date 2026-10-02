using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Raffaello.App.Services;
using Raffaello.Core.Analytics;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.App.ViewModels;

public sealed class PoLineRow
{
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = "";
    public string Description { get; init; } = "";
    public string Unit { get; init; } = "";
    public double? PoQty { get; init; }
    public double? Rate { get; init; }
    public double?[] Dn { get; init; } = Array.Empty<double?>();
    public double? Delivered { get; init; }
    public double? Remaining { get; init; }
    public double? Value { get; init; }
    public double Pct { get; init; }
    public string Status { get; init; } = "";
    public bool IsTotal { get; init; }
}

public sealed record DnColumn(int Index, string Header, string Detail);
public sealed record Alarm(string Status, string Title, string Detail);

/// <summary>PO vs delivery notes: one column per DN, value per DN (SUMPRODUCT), exceeds-PO alarms and the PO total check.</summary>
public sealed partial class MaterialsViewModel : PageViewModel
{
    public MaterialsViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Materials";
    public override string Title => "MATERIALS";
    public override string Subtitle => "DELIVERED vs PO - one column per delivery note, PCS converted to M";
    protected override bool UsesFilter => false;
    public override bool ShowFilterBar => false;
    protected override bool HasCharts => true;

    public ObservableCollection<PoProgress> Pos { get; } = new();
    public ObservableCollection<PoLineRow> Rows { get; } = new();
    public ObservableCollection<Alarm> Alarms { get; } = new();
    public List<DnColumn> DnColumns { get; private set; } = new();

    public event Action? ColumnsChanged;

    [ObservableProperty] private PoProgress? _selectedPo;
    [ObservableProperty] private string _totalCheck = "";
    [ObservableProperty] private string _totalCheckStatus = "";
    [ObservableProperty] private string _poSummary = "";

    [ObservableProperty] private ISeries[] _lineSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _lineX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _lineY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _cumSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _cumX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _cumY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _timeline = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _timelineX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _timelineY = Array.Empty<Axis>();

    private string? _pendingPo;

    partial void OnSelectedPoChanged(PoProgress? value) => LoadPo();

    protected override void Refresh()
    {
        var keep = _pendingPo ?? SelectedPo?.Po.PoNo;
        _pendingPo = null;
        var all = MaterialAnalysis.All(Project.Snapshot);
        Pos.Clear();
        foreach (var p in all) Pos.Add(p);
        Alarms.Clear();
        foreach (var p in all)
        {
            foreach (var l in p.Lines.Where(l => l.IsOver))
                Alarms.Add(new("OVER", $"{p.Po.PoNo} L{l.Line.LineNo} delivered > PO", $"{l.Line.Description}: {l.Delivered:N0} / {l.Line.Qty:N0} {l.Line.Unit}"));
            if (!p.TotalCheck.Matches)
                Alarms.Add(new("CHECK", $"{p.Po.PoNo} total does not add up", $"lines {p.TotalCheck.LinesTotal:N2} vs stated {p.TotalCheck.StatedTotal:N2}"));
        }
        if (Alarms.Count == 0) Alarms.Add(new("OK", "No material alarms", "All deliveries within PO, all PO totals add up."));
        SelectedPo = Pos.FirstOrDefault(p => p.Po.PoNo == keep) ?? Pos.FirstOrDefault();

        // cumulative deliveries per PO (value)
        CumSeries = all.Select((p, i) =>
        {
            var pts = MaterialAnalysis.CumulativeDeliveries(p).Select(x => new DateTimePoint(x.Date, x.Cumulative / Math.Max(1, p.PoValue))).ToArray();
            return (ISeries)new StepLineSeries<DateTimePoint> { Name = p.Po.PoNo, Values = pts, Stroke = ChartKit.Stroke(ChartKit.At(i), 2), Fill = null, GeometrySize = 6, GeometryStroke = ChartKit.Stroke(ChartKit.At(i), 2), GeometryFill = ChartKit.Fill(ChartKit.Sk("Surface")) };
        }).ToArray();
        CumX = new[] { new DateTimeAxis(TimeSpan.FromDays(7), d => d.ToString("dd MMM")) { LabelsPaint = new SolidColorPaint(ChartKit.Muted), TextSize = 11, SeparatorsPaint = null } };
        CumY = new[] { ChartKit.YPercent(1.1) };

        // DN timeline: each DN as a bubble on its PO lane, sized by value
        var lanes = all.Select(p => p.Po.PoNo).ToList();
        Timeline = all.Select((p, i) => (ISeries)new ScatterSeries<WeightedPoint>
        {
            Name = p.Po.PoNo,
            Values = p.Dns.Select(d => new WeightedPoint(d.DnDate.ToOADate(), i, p.DnValue(d.Id))).ToArray(),
            Fill = ChartKit.Fill(ChartKit.At(i).WithAlpha(200)),
            Stroke = null,
            MinGeometrySize = 8,
            GeometrySize = 30,
        }).ToArray();
        TimelineX = new[] { new Axis { Labeler = v => DateTime.FromOADate(Math.Max(1, v)).ToString("dd MMM"), LabelsPaint = new SolidColorPaint(ChartKit.Muted), TextSize = 11, SeparatorsPaint = null } };
        TimelineY = new[] { new Axis { Labels = lanes.ToArray(), LabelsPaint = new SolidColorPaint(ChartKit.Muted), TextSize = 11, MinStep = 1, ForceStepToMin = true, MinLimit = -0.6, MaxLimit = lanes.Count - 0.4, SeparatorsPaint = new SolidColorPaint(ChartKit.LineColor.WithAlpha(90), 1) } };
    }

    private void LoadPo()
    {
        Rows.Clear();
        var p = SelectedPo;
        if (p is null) { DnColumns = new(); ColumnsChanged?.Invoke(); return; }
        DnColumns = p.Dns.Select((d, i) => new DnColumn(i, $"{d.DnNo}", $"{d.DnDate:dd MMM}{(string.IsNullOrEmpty(d.MirNo) ? "" : "  " + d.MirNo)}")).ToList();
        foreach (var l in p.Lines)
            Rows.Add(new PoLineRow
            {
                LineNo = l.Line.LineNo, ItemCode = l.Line.ItemCode, Description = l.Line.Description, Unit = l.Line.Unit, PoQty = l.Line.Qty, Rate = l.Line.Rate,
                Dn = p.Dns.Select(d => l.ByDn.TryGetValue(d.Id, out var q) ? q : (double?)null).ToArray(),
                Delivered = l.Delivered, Remaining = l.Remaining, Value = l.DeliveredValue, Pct = l.DeliveredPct, Status = l.Status,
            });
        Rows.Add(new PoLineRow
        {
            IsTotal = true, Description = "VALUE (SAR) - SUMPRODUCT", Unit = "", Rate = null, PoQty = null,
            Dn = p.Dns.Select(d => (double?)p.DnValue(d.Id)).ToArray(), Value = p.DeliveredValue, Pct = p.DeliveredPct, Status = p.OverCount > 0 ? "OVER" : "",
        });
        ColumnsChanged?.Invoke();
        TotalCheck = p.TotalCheck.Matches
            ? $"PO TOTAL CHECK OK  |  lines SAR {p.TotalCheck.LinesTotal:N2} = stated SAR {p.TotalCheck.StatedTotal:N2}"
            : $"PO TOTAL CHECK FAILED  |  lines SAR {p.TotalCheck.LinesTotal:N2} vs stated SAR {p.TotalCheck.StatedTotal:N2}  (diff {p.TotalCheck.Difference:N2})";
        TotalCheckStatus = p.TotalCheck.Matches ? "OK" : "CHECK";
        PoSummary = $"{p.Po.Supplier}  |  {p.Po.Description}  |  PO {p.Po.PoDate:dd MMM yyyy}  |  {p.Dns.Count} DNs  |  delivered {p.DeliveredPct:P0} of SAR {p.PoValue:N0}";

        LineSeries = new ISeries[]
        {
            ChartKit.Rows("PO", p.Lines.Select(_ => 1.0), ChartKit.Graphite),
            ChartKit.Rows("DELIVERED", p.Lines.Select(l => l.DeliveredPct), ChartKit.Accent),
        };
        ((RowSeries<double>)LineSeries[0]).IgnoresBarPosition = true;
        ((RowSeries<double>)LineSeries[1]).IgnoresBarPosition = true;
        ((RowSeries<double>)LineSeries[1]).MaxBarWidth = 10;
        LineX = new[] { ChartKit.YPercent(Math.Max(1.1, p.Lines.Select(l => l.DeliveredPct).DefaultIfEmpty(0).Max() + 0.05)) };
        LineY = new[] { ChartKit.XLabels(p.Lines.Select(l => $"L{l.Line.LineNo} {l.Line.ItemCode}")) };
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { } po)
        {
            var match = Pos.FirstOrDefault(p => p.Po.PoNo == po);
            if (match != null) SelectedPo = match; else _pendingPo = po;
        }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (SelectedPo != null) yield return ReportBuilder.PoProgressSheet(SelectedPo);
        foreach (var p in Pos.Where(p => p != SelectedPo)) yield return ReportBuilder.PoProgressSheet(p);
    }
}
