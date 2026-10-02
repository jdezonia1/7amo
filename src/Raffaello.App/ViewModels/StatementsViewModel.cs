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

public sealed class StatementLine
{
    public required ChainRow Row { get; init; }
    public double CumQty { get; init; }
    public double Rate { get; init; }
    public double PrevQty { get; init; }
    public double ThisPeriod => CumQty - PrevQty;
    public double Value => CumQty * Rate;
    public double ClaimedPct => Row.Qs <= 0 ? 0 : CumQty / Row.Qs;
    public string Status => CumQty > Row.Cap + 0.0001 ? "OVER" : CumQty > Row.Done + 0.0001 ? "CHECK" : "OK";
    public string Key => Row.Key;
}

/// <summary>Per-subcontractor cumulative statements: copy detection, cap, claimed % vs WIR %, certification ageing.</summary>
public sealed partial class StatementsViewModel : PageViewModel
{
    public StatementsViewModel(PageContext ctx) : base(ctx)
    {
        Chain = new ChainPanelViewModel(ctx);
        Chain.AskRequested += () => ctx.Nav.OpenAsk();
    }

    public override string Key => "Statements";
    public override string Title => "STATEMENTS";
    public override string Subtitle => "Cumulative statements per subcontractor - capped by PROJECT QTY, checked against WIR";
    protected override bool HasCharts => true;

    public ChainPanelViewModel Chain { get; }
    public ObservableCollection<SubcontractorScore> Subs { get; } = new();
    public ObservableCollection<InvoiceSummary> Invoices { get; } = new();
    public ObservableCollection<StatementLine> Lines { get; } = new();

    [ObservableProperty] private SubcontractorScore? _selectedSub;
    [ObservableProperty] private InvoiceSummary? _selectedInvoice;
    [ObservableProperty] private StatementLine? _selectedLine;
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private string _bannerStatus = "";
    [ObservableProperty] private string _invoiceTotals = "";
    [ObservableProperty] private bool _onlyFlagged;

    [ObservableProperty] private ISeries[] _scoreSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _scoreX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _scoreY = Array.Empty<Axis>();
    [ObservableProperty] private ISeries[] _historySeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _historyX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _historyY = Array.Empty<Axis>();

    private string? _pendingInvoiceKey;

    partial void OnSelectedSubChanged(SubcontractorScore? value) => LoadInvoices();
    partial void OnSelectedInvoiceChanged(InvoiceSummary? value) => LoadLines();
    partial void OnSelectedLineChanged(StatementLine? value) => Chain.Row = value?.Row;
    partial void OnOnlyFlaggedChanged(bool value) => LoadLines();

    protected override void Refresh()
    {
        var keep = SelectedSub?.Name;
        var scope = Scoped();
        var scores = StatementAnalysis.Scorecard(Project.Snapshot, scope);
        Subs.Clear();
        foreach (var s in scores) Subs.Add(s);
        SelectedSub = Subs.FirstOrDefault(s => s.Name == keep) ?? Subs.FirstOrDefault();

        ScoreSeries = new ISeries[]
        {
            ChartKit.Columns("CLAIMED %", scores.Select(s => s.ClaimedPct), ChartKit.Accent, 22),
            ChartKit.Columns("WIR %", scores.Select(s => s.WirPct), ChartKit.Yellow, 22),
        };
        ScoreX = new[] { ChartKit.XLabels(scores.Select(s => s.Name)) };
        ScoreY = new[] { ChartKit.YPercent() };
    }

    private void LoadInvoices()
    {
        Invoices.Clear();
        if (SelectedSub is null) { Lines.Clear(); return; }
        var list = StatementAnalysis.Invoices(Project.Snapshot, Project.ChainById, SelectedSub.Name);
        foreach (var i in list.OrderByDescending(i => i.Invoice.InvDate)) Invoices.Add(i);
        SelectedInvoice = (_pendingInvoiceKey is { } k ? Invoices.FirstOrDefault(i => $"{i.Invoice.Subcontractor}|{i.Invoice.InvoiceNo}" == k) : null) ?? Invoices.FirstOrDefault();
        _pendingInvoiceKey = null;

        var ordered = list.OrderBy(i => i.Invoice.InvDate).ToList();
        double cum = 0;
        var cert = ordered.Select(i => { cum = Math.Max(cum, i.Invoice.CertifiedAmount); return (double?)cum; }).ToList();
        var certLine = ChartKit.Line("CERTIFIED TO DATE", cert, ChartKit.Graphite, width: 2);
        certLine.GeometrySize = 8;
        certLine.GeometryFill = ChartKit.Fill(ChartKit.Sk("Surface"));
        certLine.GeometryStroke = ChartKit.Stroke(ChartKit.Graphite, 2);
        HistorySeries = new ISeries[]
        {
            ChartKit.Columns("CLAIMED TO DATE", ordered.Select(i => i.ClaimedValue), ChartKit.Accent, 30),
            ChartKit.Columns("THIS PERIOD", ordered.Select(i => i.ThisPeriod), ChartKit.Yellow, 30),
            certLine,
        };
        HistoryX = new[] { ChartKit.XLabels(ordered.Select(i => i.Invoice.InvoiceNo)) };
        HistoryY = new[] { ChartKit.YValues(v => $"{v / 1000:N0}K") };
    }

    private void LoadLines()
    {
        Lines.Clear();
        var inv = SelectedInvoice;
        if (inv is null) { Banner = ""; InvoiceTotals = ""; return; }
        var s = Project.Snapshot;
        var prevInv = Invoices.Where(i => i.Invoice.InvDate < inv.Invoice.InvDate && i.Invoice.Status != InvoiceStatus.Rejected).OrderByDescending(i => i.Invoice.InvDate).FirstOrDefault();
        var prev = prevInv is null ? new Dictionary<long, double>() : s.InvoiceLines.Where(l => l.InvoiceId == prevInv.Invoice.Id).GroupBy(l => l.LineId).ToDictionary(g => g.Key, g => g.Sum(x => x.CumQty));
        var spec = Spec;
        var lines = s.InvoiceLines.Where(l => l.InvoiceId == inv.Invoice.Id)
            .Select(l => Project.ChainById.TryGetValue(l.LineId, out var r) ? new StatementLine { Row = r, CumQty = l.CumQty, Rate = l.Rate, PrevQty = prev.GetValueOrDefault(l.LineId) } : null)
            .Where(l => l != null && spec.Matches(l.Row)).Select(l => l!)
            .Where(l => !OnlyFlagged || l.Status != "OK")
            .OrderByDescending(l => l.Status == "OVER").ThenByDescending(l => l.Status == "CHECK").ThenBy(l => l.Row.Level).ThenBy(l => l.Row.Room).ToList();
        foreach (var l in lines) Lines.Add(l);
        SelectedLine = Lines.FirstOrDefault();
        if (inv.CopyOf != null) { Banner = inv.CopyOf.Message + ". Do not certify - reject or ask for a corrected statement."; BannerStatus = "COPY"; }
        else if (inv.Invoice.Status == InvoiceStatus.Redo) { Banner = $"{inv.Invoice.InvoiceNo} marked REDO: {inv.Invoice.Notes}"; BannerStatus = "REDO"; }
        else if (inv.OverLines + inv.CheckLines > 0) { Banner = $"{inv.OverLines} lines above PROJECT QTY (OVER), {inv.CheckLines} lines claimed above WIR (CHECK). Certify only min(claimed, WIR, cap)."; BannerStatus = inv.OverLines > 0 ? "OVER" : "CHECK"; }
        else { Banner = "Statement consistent with WIR and PROJECT QTY."; BannerStatus = "OK"; }
        InvoiceTotals = $"CLAIMED TO DATE SAR {inv.ClaimedValue:N2}  |  THIS PERIOD SAR {inv.ThisPeriod:N2}  |  CERTIFIED SAR {inv.Invoice.CertifiedAmount:N2}";
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { } key && key.Contains('|'))
        {
            var sub = key.Split('|')[0];
            _pendingInvoiceKey = key;
            var match = Subs.FirstOrDefault(s => s.Name == sub);
            if (match == SelectedSub) LoadInvoices(); else SelectedSub = match;
        }
    }

    private async Task SetStatus(string status, string? note = null)
    {
        if (SelectedInvoice is null) return;
        var inv = SelectedInvoice.Invoice;
        _pendingInvoiceKey = $"{inv.Subcontractor}|{inv.InvoiceNo}";
        await Ctx.Data.WriteAsync(p => p.SetInvoiceStatus(inv, status, note), Ctx.Toasts, $"{inv.InvoiceNo} {status}");
    }

    [RelayCommand] private Task Reject() => SetStatus(InvoiceStatus.Rejected, SelectedInvoice?.CopyOf?.Message);
    [RelayCommand] private Task MarkRedo() => SetStatus(InvoiceStatus.Redo);
    [RelayCommand] private Task MarkReceived() => SetStatus(InvoiceStatus.Received);

    [RelayCommand]
    private void Certify()
    {
        if (SelectedInvoice is null) return;
        Ctx.Nav.Go("Invoices", new NavTarget("Invoices", Key: $"{SelectedInvoice.Invoice.Subcontractor}|{SelectedInvoice.Invoice.InvoiceNo}"));
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        var inv = SelectedInvoice;
        if (inv is null) yield break;
        yield return new ExportSheet
        {
            Name = inv.Invoice.InvoiceNo, Title = $"{inv.Invoice.Subcontractor} {inv.Invoice.InvoiceNo} - {inv.Invoice.InvDate:dd-MMM-yyyy} - {inv.Invoice.Status}",
            Subtitle = Banner,
            Columns = new() { new("BUILDING"), new("LEVEL"), new("ROOM"), new("SYSTEM"), new("STAGE"), new("QS", ColumnKind.Integer), new("PROJECT QTY", ColumnKind.Integer),
                new("DONE (WIR)", ColumnKind.Integer), new("PREVIOUS", ColumnKind.Integer), new("CUM CLAIMED", ColumnKind.Integer), new("THIS PERIOD", ColumnKind.Integer),
                new("RATE", ColumnKind.Money), new("VALUE", ColumnKind.Money), new("CLAIMED %", ColumnKind.Percent), new("WIR %", ColumnKind.Percent), new("STATUS") },
            Rows = Lines.Select(l => new object?[] { l.Row.Building, l.Row.Level, l.Row.Room, l.Row.System, l.Row.Stage, l.Row.Qs, l.Row.ProjectQty, l.Row.Done, l.PrevQty, l.CumQty, l.ThisPeriod, l.Rate, l.Value, l.ClaimedPct, l.Row.WirPct, l.Status }).ToList(),
            TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, null, null, null, null, null, Lines.Sum(l => l.Value), null, null, null },
        };
        yield return new ExportSheet
        {
            Name = "SCORECARD", Title = "SUBCONTRACTOR SCORECARD",
            Columns = new() { new("SUBCONTRACTOR"), new("QS VALUE", ColumnKind.Money), new("CLAIMED %", ColumnKind.Percent), new("WIR %", ColumnKind.Percent), new("CERTIFIED", ColumnKind.Money), new("OVER", ColumnKind.Integer), new("CHECK", ColumnKind.Integer), new("AVG CERT DAYS", ColumnKind.Number), new("OPEN WIRS", ColumnKind.Integer) },
            Rows = Subs.Select(s => new object?[] { s.Name, s.QsValue, s.ClaimedPct, s.WirPct, s.CertifiedValue, s.OverCount, s.CheckCount, s.AvgCertDays, s.OpenWirs }).ToList(),
        };
    }
}
