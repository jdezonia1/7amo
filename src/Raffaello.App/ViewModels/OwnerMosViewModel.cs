using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Coding;
using Raffaello.Core.Export;
using Raffaello.Core.Materials;
using Raffaello.Core.Mos;

namespace Raffaello.App.ViewModels;

public sealed class MosValuationRow
{
    public required MosValuation V { get; init; }
    public double Cum { get; init; }
    public double Curr { get; init; }
    public string Title => V.Title;
    public string Caption => $"to {V.PeriodTo:dd-MMM-yy}  |  SAR {Cum:N0} on site  |  {Curr:N0} this period";
    public string Status => V.Status switch { MosStatus.Approved => "APPROVED", MosStatus.Rejected => "REJECTED", MosStatus.Submitted => "DUE", _ => "OPEN" };
}

/// <summary>
/// OWNER MOS: delivered materials (DN + MIR) mapped to the owner BOQ with the same matcher as auto-coding, valued at
/// qty x BOQ rate x MOS %, less installed quantities (release), cumulative valuations with revisions, Excel + PDF export.
/// </summary>
public sealed partial class OwnerMosViewModel : PageViewModel
{
    private readonly MaterialsService _m;

    public OwnerMosViewModel(PageContext ctx, MaterialsService materials) : base(ctx) => _m = materials;

    public override string Key => "OwnerMos";
    public override string Title => "OWNER MOS";
    public override string Subtitle => "Materials on site (App F): delivered (DN + MIR) less used, 75% of the PO rate, capped at 75% of the BOQ value";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public string[] Tabs { get; } = { "VALUATION", "DELIVERED LEDGER", "INSTALLED", "WARNINGS", "DIFF" };
    [ObservableProperty] private string _tab = "VALUATION";
    public bool IsValuation => Tab == "VALUATION";
    public bool IsLedger => Tab == "DELIVERED LEDGER";
    public bool IsInstalled => Tab == "INSTALLED";
    public bool IsWarnings => Tab == "WARNINGS";
    public bool IsDiff => Tab == "DIFF";
    partial void OnTabChanged(string value) { foreach (var n in new[] { nameof(IsValuation), nameof(IsLedger), nameof(IsInstalled), nameof(IsWarnings), nameof(IsDiff) }) OnPropertyChanged(n); }

    public ObservableCollection<MosValuationRow> Valuations { get; } = new();
    public ObservableCollection<MosLine> Lines { get; } = new();
    public ObservableCollection<DeliveredRow> Ledger { get; } = new();
    public ObservableCollection<MosInstalled> Installed { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Diff { get; } = new();
    public ObservableCollection<CodeCandidate> Suggestions { get; } = new();

    [ObservableProperty] private MosValuationRow? _selected;
    [ObservableProperty] private string _no = "1";
    [ObservableProperty] private string _periodTo = DateTime.Today.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _heading = "No valuation built";
    [ObservableProperty] private string _totals = "";
    [ObservableProperty] private string _aconexNo = "";
    [ObservableProperty] private string _rejectReason = "";
    [ObservableProperty] private DeliveredRow? _selectedLedger;
    [ObservableProperty] private CodeCandidate? _selectedSuggestion;
    [ObservableProperty] private string _typedCode = "";
    [ObservableProperty] private string _newInstalledCode = "";
    [ObservableProperty] private string _newInstalledQty = "";
    [ObservableProperty] private string _newInstalledNote = "";
    private MosBuild? _build;

    partial void OnSelectedChanged(MosValuationRow? value) { if (value != null) Show(MosService.Stored(_m.Snapshot, value.V)); }
    partial void OnSelectedLedgerChanged(DeliveredRow? value)
    {
        Suggestions.Clear();
        if (value is null) return;
        var s = _m.Coder().Suggest(new CodingRequest(value.Dn.Supplier, value.Line.ItemCode, value.Line.Description, value.Line.Unit));
        foreach (var c in s.Top) Suggestions.Add(c);
        SelectedSuggestion = Suggestions.FirstOrDefault();
        TypedCode = value.BoqCode;
    }

    protected override void Refresh()
    {
        _m.Reload();
        var s = _m.Snapshot;
        Valuations.Clear();
        foreach (var v in s.MosValuations.OrderByDescending(v => v.No).ThenByDescending(v => v.Revision))
        {
            var b = MosService.Stored(s, v);
            Valuations.Add(new MosValuationRow { V = v, Cum = b.CumAmount, Curr = b.CurrAmount });
        }
        if (_build is null || _build.Header.Id == 0) No = MosService.NextNo(s).ToString(CultureInfo.InvariantCulture);
        Ledger.Clear();
        foreach (var r in MosService.Ledger(s, _m.Settings, _m.Coder())) Ledger.Add(r);
        Installed.Clear();
        foreach (var i in s.MosInstalled.OrderByDescending(i => i.AsOf)) Installed.Add(i);
    }

    private int N => int.TryParse(No, out var n) ? n : 0;

    [RelayCommand]
    private void Build()
    {
        if (N <= 0) return;
        var to = Raffaello.Core.Documents.TextScan.ParseDate(PeriodTo) ?? DateTime.Today;
        var existing = _m.Snapshot.MosValuations.Where(v => v.No == N).OrderBy(v => v.Revision).LastOrDefault();
        if (existing is { Locked: true }) { Ctx.Toasts.Show($"{existing.Title} IS APPROVED AND LOCKED", "Use the next number.", ToastKind.Warn); return; }
        var rev = existing is null ? 0 : existing.Status == MosStatus.Draft ? existing.Revision : existing.Revision + 1;
        var b = MosService.Build(Project.Snapshot, _m.Snapshot, _m.Settings, N, rev, to, _m.Coder());
        Show(b);
        if (existing != null) FillDiff(MosService.Stored(_m.Snapshot, existing), b);
    }

    private void Show(MosBuild b)
    {
        _build = b;
        Lines.Clear();
        foreach (var l in b.Lines) Lines.Add(l);
        Warnings.Clear();
        foreach (var w in b.Warnings) Warnings.Add(w);
        Heading = $"{b.Header.Title}  |  {b.Header.Status}  |  period to {b.Header.PeriodTo:dd-MMM-yyyy}  |  MOS {b.Header.MosPct:P0}";
        Totals = $"ON SITE SAR {b.CumAmount:N2}   PREVIOUS {b.PrevAmount:N2}   THIS PERIOD {b.CurrAmount:N2}   RELEASED {b.Released:N2}   (carried to IPC line 1.6 - VAT is applied on the IPC)";
        AconexNo = b.Header.AconexNo;
        No = b.Header.No.ToString(CultureInfo.InvariantCulture);
    }

    private void FillDiff(MosBuild before, MosBuild after)
    {
        Diff.Clear();
        foreach (var (code, a, c) in MosService.Diff(before, after)) Diff.Add($"{code}: SAR {a:N2} -> {c:N2} ({c - a:+#,##0.00;-#,##0.00})");
    }

    private async Task<bool> Run(string what, Action work)
    {
        try { await Task.Run(work); _m.Reload(); Ctx.Data.RaiseChanged(); return true; }
        catch (Raffaello.Core.Data.ConcurrencyException ex) { Ctx.Toasts.Show("CHANGED BY SOMEONE ELSE", ex.Message, ToastKind.Warn); return false; }
        catch (Exception ex) { Ctx.Toasts.Show(what + " FAILED", ex.Message, ToastKind.Error); return false; }
    }

    [RelayCommand]
    private async Task Save()
    {
        var b = _build;
        if (b is null) return;
        if (await Run("SAVE", () => MosService.SaveDraft(_m.Store, _m.Snapshot, b))) Ctx.Toasts.Show("MOS SAVED", b.Header.Title, ToastKind.Good);
    }

    [RelayCommand]
    private async Task Export()
    {
        var b = _build;
        if (b is null) return;
        var folder = Ctx.Dialogs.PickFolder("Folder for the MOS valuation");
        if (folder is null) return;
        var name = $"MOS-{b.Header.No:00}-Rev{b.Header.Revision}";
        var info = Project.Workflow.HeaderInfo();
        var tpl = _m.Settings.MosTemplatePath;
        if (await Run("EXPORT", () =>
        {
            MosExporter.ExportExcel(Path.Combine(folder, name + ".xlsx"), b, info, tpl);
            MosExporter.ExportPdf(Path.Combine(folder, name + ".pdf"), b, info);
        }))
        { Ctx.Toasts.Show("MOS EXPORTED", name + " (.xlsx + .pdf)", ToastKind.Good); DialogService.OpenWithShell(folder); }
    }

    private MosValuation? Current => Selected?.V ?? (_build?.Header.Id > 0 ? _build.Header : null);

    [RelayCommand] private async Task Submit() { var v = Current; if (v != null) await Run("SUBMIT", () => MosService.Submit(_m.Store, v, AconexNo.Trim())); }
    [RelayCommand] private async Task Reject() { var v = Current; if (v != null && RejectReason.Trim().Length > 0) await Run("REJECT", () => MosService.Reject(_m.Store, v, RejectReason.Trim())); }
    [RelayCommand]
    private async Task Approve()
    {
        var v = Current;
        if (v != null && Ctx.Dialogs.Confirm("APPROVE", $"Approve and lock {v.Title}?")) await Run("APPROVE", () => MosService.Approve(_m.Store, v));
    }

    [RelayCommand]
    private void NewRevision()
    {
        var v = Current;
        if (v is null) return;
        No = v.No.ToString(CultureInfo.InvariantCulture);
        PeriodTo = v.PeriodTo.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture);
        Selected = null;
        Build();
    }

    /// <summary>Owner BOQ code for a delivered DN line: stored on the DN line (CONFIRMED) and learned.</summary>
    [RelayCommand]
    private async Task SetCode()
    {
        var row = SelectedLedger;
        if (row is null) return;
        var chosen = TypedCode.Trim().Length > 0 && TypedCode.Trim() != SelectedSuggestion?.BoqCode
            ? new CodeCandidate(TypedCode.Trim().ToUpperInvariant(), "", "", "", row.Line.Description, row.Line.Unit, "MANUAL", 1, "typed")
            : SelectedSuggestion;
        if (chosen is null) return;
        var line = _m.Snapshot.DnLines.FirstOrDefault(l => l.Id == row.Line.Id);
        if (line is null) return;
        if (await Run("SET CODE", () =>
        {
            line.BoqCode = chosen.BoqCode; line.CodeStatus = CodeStatus.Confirmed; line.CodeScore = chosen.Score; line.CodeSource = $"owner BOQ confirmed by {_m.Store.User} ({chosen.Source})";
            _m.Store.Update(line, $"DN line {line.ItemNo}: owner BOQ {chosen.BoqCode}");
            AutoCoder.Learn(_m.Store, _m.Snapshot, new CodingRequest(row.Dn.Supplier, line.ItemCode, line.Description, line.Unit), chosen, _m.Store.User);
        }))
            Ctx.Toasts.Show("OWNER BOQ CODE SET", $"{line.Description} -> {chosen.BoqCode}", ToastKind.Good);
    }

    [RelayCommand]
    private async Task AddInstalled()
    {
        if (NewInstalledCode.Trim().Length == 0 || !double.TryParse(NewInstalledQty, NumberStyles.Float, CultureInfo.InvariantCulture, out var q)) { Ctx.Toasts.Show("BOQ CODE AND QTY NEEDED", kind: ToastKind.Warn); return; }
        var e = new MosInstalled { BoqCode = NewInstalledCode.Trim().ToUpperInvariant(), Qty = q, AsOf = Raffaello.Core.Documents.TextScan.ParseDate(PeriodTo) ?? DateTime.Today, Note = NewInstalledNote.Trim() };
        if (await Run("ADD INSTALLED", () => _m.Store.Insert(e, $"MOS installed {e.BoqCode}: {e.Qty:N2} as of {e.AsOf:dd-MMM-yyyy}")))
        { NewInstalledQty = ""; NewInstalledNote = ""; }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (_build is null) yield break;
        yield return new ExportSheet
        {
            Name = "MOS", Title = Heading, Subtitle = Totals,
            Columns = new() { new("BOQ CODE"), new("DESCRIPTION", Width: 50), new("UNIT"), new("RATE", ColumnKind.Money), new("DELIVERED", ColumnKind.Number), new("INSTALLED", ColumnKind.Number), new("ON SITE", ColumnKind.Number), new("MOS %", ColumnKind.Percent), new("CUM", ColumnKind.Money), new("PREV", ColumnKind.Money), new("CURR", ColumnKind.Money), new("SOURCES", Width: 50) },
            Rows = _build.Lines.Select(l => new object?[] { l.BoqCode, l.BoqDescription, l.Unit, l.BoqRate, l.DeliveredQty, l.InstalledQty, l.OnSiteQty, l.MosPct, l.CumAmount, l.PrevAmount, l.CurrAmount, l.Sources }).ToList(),
        };
    }
}
