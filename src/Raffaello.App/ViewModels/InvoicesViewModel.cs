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

namespace Raffaello.App.ViewModels;

public sealed class ClaimLineView
{
    public required ClaimLine Line { get; init; }
    public ChainRow Row => Line.Row;
    public string Key => Row.Key;
    public string Status => Line.Status;
}

/// <summary>Builds a payment certificate from certified chain values: min(claimed, WIR, PROJECT QTY) per line.</summary>
public sealed partial class InvoicesViewModel : PageViewModel
{
    public InvoicesViewModel(PageContext ctx) : base(ctx)
    {
        Chain = new ChainPanelViewModel(ctx);
        Chain.AskRequested += () => ctx.Nav.OpenAsk();
    }

    public override string Key => "Invoices";
    public override string Title => "INVOICES";
    public override string Subtitle => "Certificate = min(CLAIMED, DONE by WIR, PROJECT QTY) per line";
    protected override bool HasCharts => true;

    public ChainPanelViewModel Chain { get; }
    public ObservableCollection<string> Subcontractors { get; } = new();
    public ObservableCollection<Invoice> Statements { get; } = new();
    public ObservableCollection<ClaimLineView> Lines { get; } = new();

    [ObservableProperty] private string? _subcontractor;
    [ObservableProperty] private Invoice? _statement;
    [ObservableProperty] private ClaimLineView? _selectedLine;
    [ObservableProperty] private ClaimDraft? _draft;
    [ObservableProperty] private bool _onlyHeld;
    [ObservableProperty] private ISeries[] _waterfall = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _waterfallX = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _waterfallY = Array.Empty<Axis>();

    private string? _pendingKey;
    private bool _loading;

    partial void OnSubcontractorChanged(string? value) { if (!_loading) LoadStatements(); }
    partial void OnStatementChanged(Invoice? value) { if (!_loading) BuildDraft(); }
    partial void OnOnlyHeldChanged(bool value) => BuildDraft();
    partial void OnSelectedLineChanged(ClaimLineView? value) => Chain.Row = value?.Row;

    protected override void Refresh()
    {
        _loading = true;
        var keep = _pendingKey?.Split('|')[0] ?? Subcontractor;
        Subcontractors.Clear();
        foreach (var s in Project.Snapshot.Invoices.Select(i => i.Subcontractor).Distinct().OrderBy(x => x)) Subcontractors.Add(s);
        Subcontractor = Subcontractors.FirstOrDefault(s => s == keep) ?? Subcontractors.FirstOrDefault();
        _loading = false;
        LoadStatements();
    }

    private void LoadStatements()
    {
        _loading = true;
        var keepNo = _pendingKey?.Split('|').ElementAtOrDefault(1) ?? Statement?.InvoiceNo;
        _pendingKey = null;
        Statements.Clear();
        foreach (var i in Project.Snapshot.Invoices.Where(i => i.Subcontractor == Subcontractor).OrderByDescending(i => i.InvDate)) Statements.Add(i);
        Statement = Statements.FirstOrDefault(i => i.InvoiceNo == keepNo) ?? Statements.FirstOrDefault(i => i.Status == InvoiceStatus.Received) ?? Statements.FirstOrDefault();
        _loading = false;
        BuildDraft();
    }

    private void BuildDraft()
    {
        Lines.Clear();
        if (Subcontractor is null || Statement is null) { Draft = null; return; }
        var d = ClaimBuilder.Build(Project.Snapshot, Project.ChainById, Subcontractor, Statement);
        Draft = d;
        var spec = Spec;
        foreach (var l in d.Lines.Where(l => spec.Matches(l.Row)).Where(l => !OnlyHeld || l.HeldQty > 0).OrderByDescending(l => l.Status == "OVER").ThenByDescending(l => l.Status == "CHECK"))
            Lines.Add(new ClaimLineView { Line = l });
        SelectedLine = Lines.FirstOrDefault();

        var steps = new (string Label, double Value)[]
        {
            ("CLAIMED", d.ClaimedValue), ("HELD", -d.HeldValue), ("CERT TO DATE", d.GrossToDate), ("PREVIOUS", -d.PreviousGross),
            ("THIS PERIOD", d.ThisPeriodGross), ("RETENTION", -d.Retention), ("ADVANCE", -d.AdvanceRecovery), ("NET", d.NetPayable),
        };
        Waterfall = new ISeries[]
        {
            ChartKit.Columns("AMOUNT", steps.Select(s => Math.Abs(s.Value)), ChartKit.Accent, 34),
        };
        ((ColumnSeries<double>)Waterfall[0]).Values = steps.Select(s => s.Value).ToArray();
        WaterfallX = new[] { ChartKit.XLabels(steps.Select(s => s.Label)) };
        WaterfallY = new[] { ChartKit.YValues(v => $"{v / 1000:N0}K", null) };
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { } k && k.Contains('|')) { _pendingKey = k; Refresh(); }
    }

    [RelayCommand]
    private async Task Certify()
    {
        if (Draft?.Statement is null) return;
        var st = Draft.Statement;
        if (st.Status == InvoiceStatus.Certified) { Ctx.Toasts.Show("ALREADY CERTIFIED", st.InvoiceNo, ToastKind.Warn); return; }
        var copy = Raffaello.Core.Rules.InvoiceCopyDetector.Detect(Project.Snapshot.Invoices, Project.Snapshot.InvoiceLines).FirstOrDefault(c => c.Invoice.Id == st.Id);
        if (copy != null && !Ctx.Dialogs.Confirm("Copy statement", copy.Message + "\n\nCertify anyway?")) return;
        if (!Ctx.Dialogs.Confirm("Certify", $"Certify {st.Subcontractor} {st.InvoiceNo}?\n\nCertified to date SAR {Draft.GrossToDate:N2}\nThis period SAR {Draft.ThisPeriodGross:N2}\nHeld (claimed above WIR / cap) SAR {Draft.HeldValue:N2}\nNet payable SAR {Draft.NetPayable:N2}")) return;
        var draft = Draft;
        _pendingKey = $"{st.Subcontractor}|{st.InvoiceNo}";
        await Ctx.Data.WriteAsync(p => p.Certify(draft), Ctx.Toasts, $"{st.InvoiceNo} CERTIFIED");
    }

    [RelayCommand]
    private void ExportClaim()
    {
        var sheets = ExportCurrentView().ToList();
        if (sheets.Count > 0) Ctx.Exports.Export($"CERTIFICATE_{Subcontractor}_{Statement?.InvoiceNo}", sheets);
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        var d = Draft;
        if (d?.Statement is null) yield break;
        yield return new ExportSheet
        {
            Name = "CERTIFICATE", Title = $"PAYMENT CERTIFICATE - {d.Subcontractor} {d.Statement.InvoiceNo}",
            Subtitle = $"Contract {d.Contract?.ContractNo}  |  statement {d.Statement.InvDate:dd-MMM-yyyy}  |  prepared {DateTime.Today:dd-MMM-yyyy}",
            Columns = new() { new("ITEM", Width: 40), new("AMOUNT (SAR)", ColumnKind.Money) },
            Rows = new()
            {
                new object?[] { "CLAIMED TO DATE (STATEMENT)", d.ClaimedValue },
                new object?[] { "HELD - CLAIMED ABOVE WIR OR PROJECT QTY", -d.HeldValue },
                new object?[] { "CERTIFIED TO DATE", d.GrossToDate },
                new object?[] { "LESS PREVIOUSLY CERTIFIED", -d.PreviousGross },
                new object?[] { "THIS CERTIFICATE (GROSS)", d.ThisPeriodGross },
                new object?[] { $"LESS RETENTION {d.RetentionPct:P0}", -d.Retention },
                new object?[] { "LESS ADVANCE RECOVERY", -d.AdvanceRecovery },
            },
            TotalRow = new object?[] { "NET PAYABLE", d.NetPayable },
        };
        yield return new ExportSheet
        {
            Name = "LINES", Title = "CERTIFIED QUANTITIES BY LINE",
            Columns = new() { new("BUILDING"), new("LEVEL"), new("ROOM"), new("SYSTEM"), new("STAGE"), new("ITEM"), new("CLAIMED", ColumnKind.Integer), new("DONE (WIR)", ColumnKind.Integer), new("CAP", ColumnKind.Integer), new("CERTIFIABLE", ColumnKind.Integer), new("PREVIOUS", ColumnKind.Integer), new("THIS PERIOD", ColumnKind.Integer), new("HELD", ColumnKind.Integer), new("RATE", ColumnKind.Money), new("VALUE TO DATE", ColumnKind.Money), new("THIS PERIOD VALUE", ColumnKind.Money), new("STATUS") },
            Rows = d.Lines.Select(l => new object?[] { l.Row.Building, l.Row.Level, l.Row.Room, l.Row.System, l.Row.Stage, l.Row.ItemCode, l.Claimed, l.Done, l.Cap, l.Certifiable, l.PreviousCertified, l.ThisPeriod, l.HeldQty, l.Rate, l.ValueToDate, l.ValueThisPeriod, l.Status }).ToList(),
            TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, null, null, null, null, null, null, null, d.GrossToDate, d.ThisPeriodGross, null },
        };
    }
}
