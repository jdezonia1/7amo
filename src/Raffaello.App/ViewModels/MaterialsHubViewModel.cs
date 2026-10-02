using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.App.Views;
using Raffaello.Core.Coding;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Materials;

namespace Raffaello.App.ViewModels;

public sealed class PoListRow
{
    public required MatPo Po { get; init; }
    public int Lines { get; init; }
    public double Total { get; init; }
    public int Coded { get; init; }
    public double DeliveredPct { get; init; }
    public string Caption => $"{Po.Supplier}  |  SAR {Total:N0}  |  coded {Coded}/{Lines}";
    public string Tag => Po.ToleranceConflict ? "CHECK" : Coded < Lines ? "DUE" : "OK";
}

public sealed class PoLineRowP3
{
    public required MatPoLine L { get; init; }
    public double Delivered { get; init; }
    public double Pct => L.Qty <= 0 ? 0 : Delivered / L.Qty;
    public string CodeTag => L.CodeStatus switch { CodeStatus.Auto or CodeStatus.Confirmed or CodeStatus.Manual or CodeStatus.Document => "OK", CodeStatus.Review => "CHECK", _ => "OVER" };
    public string CodeStatusText => L.CodeStatus.Length == 0 ? "ASK" : L.CodeStatus;
}

public sealed class DnListRow
{
    public required MatDn Dn { get; init; }
    public int Lines { get; init; }
    public string Status { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Caption => $"{Dn.Supplier}  |  PO {Dn.PoNo}  |  {Dn.DnDate:dd-MMM-yy}  |  {Lines} lines";
}

public sealed partial class InvoiceableRow : ObservableObject
{
    public required MatDnLine L { get; init; }
    public required MatDn Dn { get; init; }
    public string PoLine { get; init; } = "";
    [ObservableProperty] private bool _selected = true;
    public string Tag => MatchStatus.Tag(L.MatchStatus);
}

public sealed class SupplierInvoiceRow
{
    public required SubInvoice Inv { get; init; }
    public double Curr { get; init; }
    public string Title => Inv.Title;
    public string Caption => $"{Inv.ContractNo}  |  SAR {Curr:N0}{(Inv.AconexWorkflowNo.Length > 0 ? "  |  " + Inv.AconexWorkflowNo : "")}";
    public string Status => Inv.Status switch { SubInvoiceStatus.Approved => "APPROVED", SubInvoiceStatus.Rejected => "REJECTED", SubInvoiceStatus.Submitted => "DUE", _ => "OPEN" };
}

/// <summary>
/// MATERIALS hub (phase 3): purchase orders with auto-coding, delivery notes, MIRs, the PO / DN / MIR 3-way match,
/// supplier invoices from DN lines (one invoice per DN line, hard lock), MIR tracker export, DN lookup and reader settings.
/// The earlier PO-vs-DN overview stays as the OVERVIEW tab.
/// </summary>
public sealed partial class MaterialsHubViewModel : PageViewModel
{
    private readonly MaterialsService _m;
    private readonly IOcrEngine _ocr;
    private readonly IPageRenderer _renderer;

    public MaterialsHubViewModel(PageContext ctx, MaterialsService materials, MaterialsViewModel overview, IOcrEngine ocr, IPageRenderer renderer) : base(ctx)
    {
        _m = materials; _ocr = ocr; _renderer = renderer;
        Overview = overview;
        _m.ReadProgress = new Progress<string>(s => Busy = "READING  " + s);
    }

    public override string Key => "Materials";
    public override string Title => "MATERIALS";
    public override string Subtitle => "PO, DN and MIR read and checked, 3-way match, supplier invoices, DN lookup";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public MaterialsViewModel Overview { get; }
    public string[] Tabs { get; } = { "PURCHASE ORDERS", "DELIVERY NOTES", "MIR", "3-WAY MATCH", "SUPPLIER INVOICES", "DN LOOKUP", "OVERVIEW", "SETTINGS" };
    [ObservableProperty] private string _tab = "PURCHASE ORDERS";
    [ObservableProperty] private string _busy = "";
    public bool IsPoTab => Tab == "PURCHASE ORDERS";
    public bool IsDnTab => Tab == "DELIVERY NOTES";
    public bool IsMirTab => Tab == "MIR";
    public bool IsMatchTab => Tab == "3-WAY MATCH";
    public bool IsInvTab => Tab == "SUPPLIER INVOICES";
    public bool IsLookupTab => Tab == "DN LOOKUP";
    public bool IsOverviewTab => Tab == "OVERVIEW";
    public bool IsSettingsTab => Tab == "SETTINGS";

    partial void OnTabChanged(string value)
    {
        foreach (var n in new[] { nameof(IsPoTab), nameof(IsDnTab), nameof(IsMirTab), nameof(IsMatchTab), nameof(IsInvTab), nameof(IsLookupTab), nameof(IsOverviewTab), nameof(IsSettingsTab) }) OnPropertyChanged(n);
        if (value == "OVERVIEW") Overview.Activate(null); else Overview.Deactivate();
        if (value == "SETTINGS") LoadSettings();
    }

    public MaterialsSnapshot S => _m.Snapshot;

    protected override void Refresh()
    {
        _m.Reload();
        FillPos();
        FillDns();
        FillMirs();
        FillMatch();
        FillInvoicePos();
        FillStoredInvoices();
        Suppliers.Clear();
        foreach (var s in DnLookup.Suppliers(S)) Suppliers.Add(s);
        RunLookup();
        if (IsOverviewTab) Overview.Activate(null);
    }

    private async Task<bool> Run(string what, Func<Task> work)
    {
        Busy = what;
        try { await work(); return true; }
        catch (DnLineLockedException ex) { Ctx.Toasts.Show("DN LINE ALREADY INVOICED", ex.Message, ToastKind.Warn, 8); return false; }
        catch (Raffaello.Core.Data.ConcurrencyException ex) { Ctx.Toasts.Show("CHANGED BY SOMEONE ELSE", ex.Message, ToastKind.Warn); await Ctx.Data.ReloadAsync(); return false; }
        catch (Exception ex) { Ctx.Toasts.Show(what + " FAILED", ex.Message, ToastKind.Error, 8); return false; }
        finally { Busy = ""; }
    }

    private void Changed() { Ctx.Data.RaiseChanged(); }

    // =================================================================== PURCHASE ORDERS

    public ObservableCollection<PoListRow> Pos { get; } = new();
    public ObservableCollection<PoLineRowP3> PoLines { get; } = new();
    public ObservableCollection<CodeCandidate> Suggestions { get; } = new();
    [ObservableProperty] private PoListRow? _selectedPo;
    [ObservableProperty] private PoLineRowP3? _selectedPoLine;
    [ObservableProperty] private CodeCandidate? _selectedSuggestion;
    [ObservableProperty] private string _poTerms = "";
    [ObservableProperty] private string _poCoding = "";
    [ObservableProperty] private string _manualBoq = "";
    [ObservableProperty] private string _manualCost = "";
    [ObservableProperty] private string _manualResource = "";

    partial void OnSelectedPoChanged(PoListRow? value)
    {
        FillPoLines();
        PublishSelection(value is null ? "" : Raffaello.Core.Wiring.SelectedRecords.Po(value.Po.PoNo, value.Po.Supplier));
    }
    partial void OnSelectedPoLineChanged(PoLineRowP3? value) => FillSuggestions();

    private void FillPos()
    {
        var keep = SelectedPo?.Po.Id;
        Pos.Clear();
        foreach (var po in S.Pos.OrderByDescending(p => p.PoDate ?? DateTime.MinValue))
        {
            var lines = S.LinesOf(po).ToList();
            var prog = _m.LastMatch.PoLines.Where(p => p.Po.Id == po.Id).ToList();
            Pos.Add(new PoListRow
            {
                Po = po, Lines = lines.Count, Total = lines.Sum(l => l.Amount), Coded = lines.Count(l => l.BoqCode.Length > 0),
                DeliveredPct = lines.Sum(l => l.Amount) <= 0 ? 0 : prog.Sum(p => p.Delivered * p.Line.Rate) / lines.Sum(l => l.Amount),
            });
        }
        SelectedPo = Pos.FirstOrDefault(p => p.Po.Id == keep) ?? Pos.FirstOrDefault();
        FillPoLines();
    }

    private void FillPoLines()
    {
        PoLines.Clear();
        var po = SelectedPo?.Po;
        if (po is null) { PoTerms = "Import a PO (PDF or Excel) to start."; PoCoding = ""; return; }
        var prog = _m.LastMatch.PoLines.Where(p => p.Po.Id == po.Id).ToDictionary(p => p.Line.Id, p => p.Delivered);
        foreach (var l in S.LinesOf(po)) PoLines.Add(new PoLineRowP3 { L = l, Delivered = prog.GetValueOrDefault(l.Id) });
        PoTerms = $"{po.PoNo}  |  {po.Supplier}  |  {po.PoDate:dd-MMM-yyyy}  |  advance {po.AdvancePct:P0}  retention {po.RetentionPct:P0}  |  tolerance header {Fmt(po.ToleranceHeaderPct)} / clause {Fmt(po.ToleranceClausePct)} -> uses ±{_m.Settings.ToleranceFor(po):P0}" +
                  $"{(po.ToleranceConflict ? "  (CONFLICT)" : "")}  |  penalty {po.PenaltyText}  |  payment {po.PaymentTerms}";
        var c = PoLines.GroupBy(r => r.CodeStatusText).Select(g => $"{g.Key} {g.Count()}");
        PoCoding = "CODING: " + string.Join("  ", c);
    }

    private static string Fmt(double? p) => p is double d ? d.ToString("P0", CultureInfo.InvariantCulture) : "-";

    private void FillSuggestions()
    {
        Suggestions.Clear();
        var l = SelectedPoLine?.L;
        if (l is null) return;
        var po = SelectedPo?.Po;
        var sug = _m.Coder().Suggest(new CodingRequest(po?.Supplier ?? "", l.ItemCode, l.Description, l.Unit, po?.Id));
        foreach (var c in sug.Top) Suggestions.Add(c);
        if (l.BoqCode.Length > 0 && !Suggestions.Any(c => c.BoqCode == l.BoqCode))
            Suggestions.Insert(0, new CodeCandidate(l.BoqCode, l.CostCode, l.BudgetResourceCode, l.ResourceCode, l.Description, l.Unit, "CURRENT", l.CodeScore, l.CodeSource));
        SelectedSuggestion = Suggestions.FirstOrDefault();
        ManualBoq = l.BoqCode; ManualCost = l.CostCode; ManualResource = l.BudgetResourceCode;
    }

    [RelayCommand]
    private async Task ImportPo()
    {
        var path = Ctx.Dialogs.OpenFile("Purchase order (PDF or Excel)", "PO|*.pdf;*.xlsx;*.xlsm;*.csv|All files|*.*");
        if (path is null) return;
        ExtractionResult<PoDocument>? res = null;
        if (!await Run("READING PO", async () => res = await _m.ReadPoAsync(path, _ocr))) return;
        _m.CodePoLines(res!.Value);
        if (!MaterialsReviewWindow.Show(new MaterialsReviewViewModel(path, res, _renderer))) return;
        _m.CodePoLines(res.Value);
        if (await Run("SAVING PO", () => Task.Run(() => { _m.CommitPo(res.Value); _m.ArchiveRead(res.Text, path, "PO", "MatPo", res.Value.Header.PoNo); })))
        {
            Ctx.Toasts.Show("PO SAVED", $"{res.Value.Header.PoNo}: {res.Value.Lines.Count} lines, coding {CodingSummary(res.Value)}", ToastKind.Good);
            Changed();
        }
    }

    private static string CodingSummary(PoDocument d) => string.Join(", ", d.Lines.GroupBy(l => l.CodeStatus.Length == 0 ? "ASK" : l.CodeStatus).Select(g => $"{g.Key} {g.Count()}"));

    [RelayCommand]
    private async Task Recode()
    {
        if (SelectedPo is null) return;
        var po = SelectedPo.Po;
        CodingReport? r = null;
        if (await Run("AUTO-CODING", () => Task.Run(() => r = _m.RecodePo(po)))) { Ctx.Toasts.Show("AUTO-CODED", r!.ToString(), ToastKind.Good); Changed(); }
    }

    [RelayCommand]
    private async Task ConfirmSuggestion()
    {
        var l = SelectedPoLine?.L; var c = SelectedSuggestion;
        if (l is null || c is null) return;
        if (await Run("CONFIRMING CODE", () => Task.Run(() => _m.ConfirmCode(l, c)))) { Ctx.Toasts.Show("CODE CONFIRMED AND LEARNED", $"line {l.LineNo} -> {c.BoqCode}", ToastKind.Good); Changed(); }
    }

    [RelayCommand]
    private async Task ConfirmTyped()
    {
        var l = SelectedPoLine?.L;
        if (l is null || ManualBoq.Trim().Length == 0) return;
        var c = new CodeCandidate(ManualBoq.Trim().ToUpperInvariant(), ManualCost.Trim(), ManualResource.Trim(), "", l.Description, l.Unit, "MANUAL", 1, "typed");
        if (await Run("SAVING CODE", () => Task.Run(() => _m.ConfirmCode(l, c, manual: true)))) { Ctx.Toasts.Show("CODE SAVED AND LEARNED", $"line {l.LineNo} -> {c.BoqCode}", ToastKind.Good); Changed(); }
    }

    // =================================================================== DELIVERY NOTES

    public ObservableCollection<DnListRow> Dns { get; } = new();
    public ObservableCollection<MatchRow> DnLines { get; } = new();
    public ObservableCollection<MatPoLine> PinChoices { get; } = new();
    [ObservableProperty] private DnListRow? _selectedDn;
    [ObservableProperty] private MatchRow? _selectedDnLine;
    [ObservableProperty] private MatPoLine? _pinTarget;

    partial void OnSelectedDnChanged(DnListRow? value)
    {
        FillDnLines();
        PublishSelection(value is null ? "" : Raffaello.Core.Wiring.SelectedRecords.Dn(value.Dn.DnNo, value.Dn.Supplier, value.Dn.PoNo));
    }
    partial void OnSelectedDnLineChanged(MatchRow? value) => PinTarget = value?.PoLine is { } pl ? PinChoices.FirstOrDefault(p => p.Id == pl.Id) : null;

    private void FillDns()
    {
        var keep = SelectedDn?.Dn.Id;
        Dns.Clear();
        foreach (var dn in S.Dns.OrderByDescending(d => d.DnDate ?? DateTime.MinValue))
        {
            var rows = _m.LastMatch.Rows.Where(r => r.Dn.Id == dn.Id).ToList();
            var worst = rows.OrderByDescending(r => MatchStatus.Severity(r.Status)).FirstOrDefault()?.Status ?? "";
            Dns.Add(new DnListRow { Dn = dn, Lines = rows.Count, Status = worst, Tag = MatchStatus.Tag(worst) });
        }
        SelectedDn = Dns.FirstOrDefault(d => d.Dn.Id == keep) ?? Dns.FirstOrDefault();
        FillDnLines();
    }

    private void FillDnLines()
    {
        DnLines.Clear(); PinChoices.Clear();
        var dn = SelectedDn?.Dn;
        if (dn is null) return;
        foreach (var r in _m.LastMatch.Rows.Where(r => r.Dn.Id == dn.Id)) DnLines.Add(r);
        var po = S.FindPo(dn.PoNo);
        if (po != null) foreach (var l in S.LinesOf(po)) PinChoices.Add(l);
    }

    [RelayCommand]
    private async Task ImportDn()
    {
        var path = Ctx.Dialogs.OpenFile("Delivery note (PDF, photo or Excel)", "DN|*.pdf;*.jpg;*.jpeg;*.png;*.xlsx;*.xlsm;*.csv|All files|*.*");
        if (path is null) return;
        ExtractionResult<DnDocument>? res = null;
        if (!await Run("READING DN", async () => res = await _m.ReadDnAsync(path, _ocr))) return;
        if (!MaterialsReviewWindow.Show(new MaterialsReviewViewModel(path, res!, _renderer))) return;
        if (await Run("SAVING DN", () => Task.Run(() => { _m.CommitDn(res!.Value); _m.ArchiveRead(res.Text, path, "DN", "MatDn", res.Value.Header.DnNo); })))
        {
            var rows = _m.LastMatch.Rows.Where(r => r.Dn.DnNo == res!.Value.Header.DnNo).ToList();
            Ctx.Toasts.Show("DN SAVED AND MATCHED", $"{res!.Value.Header.DnNo}: {string.Join(", ", rows.GroupBy(r => r.Status).Select(g => $"{g.Key} {g.Count()}"))}", ToastKind.Good, 6);
            Changed();
        }
    }

    [RelayCommand]
    private async Task PinLine()
    {
        var row = SelectedDnLine;
        if (row is null) return;
        var target = PinTarget;
        if (await Run("PINNING LINE", () => Task.Run(() => { ThreeWayMatcher.PinManual(_m.Store, row.Line, target); _m.RunMatch(); })))
        { Ctx.Toasts.Show(target is null ? "MANUAL LINK REMOVED" : "DN LINE PINNED", target is null ? "" : $"-> PO line {target.LineNo}", ToastKind.Good); Changed(); }
    }

    // =================================================================== MIR

    public ObservableCollection<MatMir> Mirs { get; } = new();
    public ObservableCollection<MatMirDn> MirDns { get; } = new();
    public ObservableCollection<MatMirEvidence> MirEvidence { get; } = new();
    public string[] MirStatuses { get; } = { "OPEN", "APPROVED", "APPROVED WITH COMMENTS", "REVISE AND RESUBMIT", "REJECTED" };
    [ObservableProperty] private MatMir? _selectedMir;
    [ObservableProperty] private string _mirStatus = "OPEN";
    [ObservableProperty] private string _newMirDn = "";
    [ObservableProperty] private string _newMirPo = "";
    [ObservableProperty] private bool _mirReadDnPhotos = true;
    [ObservableProperty] private bool _mirReadCerts;
    [ObservableProperty] private bool _mirReadLabels;

    partial void OnSelectedMirChanged(MatMir? value) => FillMirDetail();

    private void FillMirs()
    {
        var keep = SelectedMir?.Id;
        Mirs.Clear();
        foreach (var m in S.Mirs.OrderByDescending(m => m.MirDate ?? DateTime.MinValue)) Mirs.Add(m);
        SelectedMir = Mirs.FirstOrDefault(m => m.Id == keep) ?? Mirs.FirstOrDefault();
        FillMirDetail();
    }

    private void FillMirDetail()
    {
        MirDns.Clear(); MirEvidence.Clear();
        var m = SelectedMir;
        if (m is null) return;
        MirStatus = m.Status;
        foreach (var d in S.MirDns.Where(x => x.MirId == m.Id)) MirDns.Add(d);
        foreach (var e in S.MirEvidence.Where(x => x.MirId == m.Id).OrderBy(e => e.Page)) MirEvidence.Add(e);
        NewMirPo = MirDns.Select(d => d.PoNo).FirstOrDefault(p => p.Length > 0) ?? "";
    }

    public string VisionStatus => _m.Vision().Status;

    [RelayCommand]
    private async Task ImportMir()
    {
        var path = Ctx.Dialogs.OpenFile("MIR bundle (PDF)", "MIR|*.pdf|All files|*.*");
        if (path is null) return;
        var vision = _m.Vision();
        if (vision.IsAvailable && (MirReadDnPhotos || MirReadCerts || MirReadLabels)
            && !Ctx.Dialogs.Confirm("CLOUD READING", "Photo / scan pages of the MIR will be sent to Claude to read (DN photos" + (MirReadCerts ? ", certificates" : "") + (MirReadLabels ? ", drum labels" : "") + "). Continue?"))
            return;
        ExtractionResult<MirDocument>? res = null;
        var progress = new Progress<string>(s => Busy = s);
        if (!await Run("READING MIR", async () => res = await _m.ReadMirAsync(path, _ocr, new MirReadOptions { ReadDnPhotos = MirReadDnPhotos, ReadCertificates = MirReadCerts, ReadLabels = MirReadLabels, Progress = progress }))) return;
        if (!MaterialsReviewWindow.Show(new MaterialsReviewViewModel(path, res!, _renderer))) return;
        if (await Run("SAVING MIR", () => Task.Run(() => { _m.CommitMir(res!.Value); _m.ArchiveRead(res.Text, path, "MIR", "MatMir", res.Value.Header.MirNo); })))
        {
            Ctx.Toasts.Show("MIR SAVED", $"{res!.Value.Header.MirNo}: {res.Value.Dns.Count} DN ref(s), {res.Value.Evidence.Count} evidence rows", ToastKind.Good);
            Changed();
        }
    }

    [RelayCommand]
    private async Task AddMirDn()
    {
        var m = SelectedMir;
        if (m is null || NewMirDn.Trim().Length == 0) return;
        var (dn, po) = (NewMirDn.Trim(), NewMirPo.Trim());
        if (await Run("ADDING DN", () => Task.Run(() => _m.AddDnToMir(m, dn, po)))) { NewMirDn = ""; Changed(); }
    }

    [RelayCommand]
    private async Task SetMirStatus()
    {
        var m = SelectedMir;
        if (m is null) return;
        var st = MirStatus;
        if (await Run("SAVING MIR STATUS", () => Task.Run(() => _m.SetMirStatus(m, st)))) Changed();
    }

    // =================================================================== 3-WAY MATCH

    public ObservableCollection<MatchRow> MatchRows { get; } = new();
    public string[] MatchFilters { get; } = { "ALL", MatchStatus.Matched, MatchStatus.QtyMismatch, MatchStatus.OverPo, MatchStatus.DnWithoutMir, MatchStatus.NotOnPo };
    [ObservableProperty] private string _matchFilter = "ALL";
    [ObservableProperty] private string _matchSummary = "";
    [ObservableProperty] private int _matchedCount;
    [ObservableProperty] private int _problemCount;
    partial void OnMatchFilterChanged(string value) => FillMatch();

    private void FillMatch()
    {
        MatchRows.Clear();
        foreach (var r in _m.LastMatch.Rows.Where(r => MatchFilter == "ALL" || r.Status == MatchFilter).OrderByDescending(r => MatchStatus.Severity(r.Status)).ThenBy(r => r.Dn.DnNo)) MatchRows.Add(r);
        MatchSummary = _m.LastMatch.Summary;
        MatchedCount = _m.LastMatch.Count(MatchStatus.Matched);
        ProblemCount = _m.LastMatch.Rows.Count - MatchedCount;
    }

    [RelayCommand]
    private async Task RunMatch()
    {
        if (await Run("MATCHING", () => Task.Run(_m.RunMatch))) { Ctx.Toasts.Show("3-WAY MATCH", _m.LastMatch.Summary, ToastKind.Good, 6); Changed(); }
    }

    [RelayCommand]
    private async Task ExportTracker()
    {
        var po = InvoicePo ?? SelectedPo?.Po;
        if (po is null) { Ctx.Toasts.Show("PICK A PO FIRST", kind: ToastKind.Warn); return; }
        var path = Ctx.Dialogs.SaveFile("MIR tracker", SupplierInvoices.Safe($"{po.PoNo}-MIR-TRACKER_{DateTime.Now:yyyyMMdd}") + ".xlsx");
        if (path is null) return;
        if (await Run("EXPORTING TRACKER", () => Task.Run(() => MirTrackerExporter.Export(path, S, _m.Settings, po))))
        { Ctx.Toasts.Show("MIR TRACKER EXPORTED", Path.GetFileName(path), ToastKind.Good); DialogService.OpenWithShell(path); }
    }

    // =================================================================== SUPPLIER INVOICES

    public ObservableCollection<MatPo> InvoicePos { get; } = new();
    public ObservableCollection<InvoiceableRow> Invoiceable { get; } = new();
    public ObservableCollection<InvoiceLineRow> InvoiceLines { get; } = new();
    public ObservableCollection<string> InvoiceWarnings { get; } = new();
    public ObservableCollection<SupplierInvoiceRow> StoredInvoices { get; } = new();
    [ObservableProperty] private MatPo? _invoicePo;
    [ObservableProperty] private string _invoiceNo = "1";
    [ObservableProperty] private string _invoiceHeading = "";
    [ObservableProperty] private InvoiceTotals? _invoiceTotals;
    [ObservableProperty] private SupplierInvoiceRow? _selectedStoredInvoice;
    [ObservableProperty] private string _aconexNo = "";
    [ObservableProperty] private string _rejectReason = "";
    private SupplierInvoiceBuild? _build;

    partial void OnInvoicePoChanged(MatPo? value)
    {
        if (value != null) InvoiceNo = SupplierInvoices.NextInvoiceNo(S, value).ToString(CultureInfo.InvariantCulture);
        FillInvoiceable();
    }

    partial void OnInvoiceNoChanged(string value) => FillInvoiceable();
    partial void OnSelectedStoredInvoiceChanged(SupplierInvoiceRow? value) { if (value != null) ShowStoredInvoice(value.Inv); }

    private int InvNo => int.TryParse(InvoiceNo, out var n) ? n : 0;

    private void FillInvoicePos()
    {
        var keep = InvoicePo?.Id;
        InvoicePos.Clear();
        foreach (var p in S.Pos.OrderBy(p => p.Supplier).ThenBy(p => p.PoNo)) InvoicePos.Add(p);
        InvoicePo = InvoicePos.FirstOrDefault(p => p.Id == keep) ?? InvoicePos.FirstOrDefault();
        FillInvoiceable();
    }

    private void FillInvoiceable()
    {
        Invoiceable.Clear();
        var po = InvoicePo;
        if (po is null || InvNo <= 0) return;
        var poLines = S.LinesOf(po).ToDictionary(l => l.Id);
        var dns = S.Dns.ToDictionary(d => d.Id);
        foreach (var l in SupplierInvoices.Invoiceable(S, po, InvNo))
            Invoiceable.Add(new InvoiceableRow { L = l, Dn = dns[l.DnId], PoLine = l.PoLineId is long id && poLines.TryGetValue(id, out var pl) ? $"{pl.LineNo:00} {pl.Description}" : "-" });
    }

    private void FillStoredInvoices()
    {
        StoredInvoices.Clear();
        foreach (var inv in _m.SupplierInvoiceHeaders().OrderByDescending(i => i.CreatedAt))
            StoredInvoices.Add(new SupplierInvoiceRow { Inv = inv, Curr = InvoiceTotals.Of(inv, Project.Snapshot.SubInvoiceLines.Where(l => l.SubInvoiceId == inv.Id)).CurrGross });
    }

    [RelayCommand]
    private void BuildInvoice()
    {
        var po = InvoicePo;
        if (po is null || InvNo <= 0) { Ctx.Toasts.Show("PICK A PO AND AN INVOICE NO", kind: ToastKind.Warn); return; }
        var existing = Project.Snapshot.SubInvoices.Where(i => i.ContractNo == po.PoNo && i.Subcontractor == po.Supplier && i.InvoiceNo == InvNo).OrderBy(i => i.Revision).LastOrDefault();
        if (existing is { Locked: true }) { Ctx.Toasts.Show($"{existing.Title} IS APPROVED AND LOCKED", "Use the next invoice number.", ToastKind.Warn); return; }
        var rev = existing is null ? 0 : existing.Status == SubInvoiceStatus.Draft ? existing.Revision : existing.Revision + 1;
        var ids = Invoiceable.Where(r => r.Selected).Select(r => r.L.Id).ToList();
        _build = _m.BuildInvoice(po, InvNo, ids, rev);
        ShowBuild(_build.Build);
    }

    private void ShowBuild(InvoiceBuild b)
    {
        InvoiceLines.Clear();
        foreach (var l in b.Lines.Where(l => l.Kind != "ITEM" || Math.Abs(l.CumQty) > 1e-9 || Math.Abs(l.CurrQty) > 1e-9)) InvoiceLines.Add(new InvoiceLineRow { Line = l });
        InvoiceWarnings.Clear();
        foreach (var w in b.Warnings) InvoiceWarnings.Add(w);
        InvoiceTotals = b.Totals;
        InvoiceHeading = $"{b.Header.Title}  |  {b.Header.Status}  |  this period SAR {b.Totals.CurrGross:N2}  |  incl. VAT SAR {b.Totals.NetInclVatCurr:N2}";
    }

    private void ShowStoredInvoice(SubInvoice inv)
    {
        var po = S.FindPo(inv.ContractNo);
        if (po is null) return;
        _build = new SupplierInvoiceBuild { Build = Project.Workflow.Stored(inv), Po = po, CurrentLines = S.DnLines.Where(l => _m.LockedLines(inv).Contains(l.Id)).ToList() };
        AconexNo = inv.AconexWorkflowNo;
        ShowBuild(_build.Build);
    }

    [RelayCommand]
    private async Task SaveInvoice()
    {
        var b = _build;
        if (b is null || b.Build.Header.Id != 0 && b.Build.Header.Locked) return;
        SubInvoice? h = null;
        if (await Run("SAVING INVOICE", () => Task.Run(() => h = _m.SaveInvoice(b))))
        { Ctx.Toasts.Show("SUPPLIER INVOICE SAVED", $"{h!.Title}: {b.CurrentLines.Count} DN lines locked to this invoice", ToastKind.Good); Changed(); }
    }

    [RelayCommand]
    private async Task ExportInvoice()
    {
        var b = _build;
        if (b is null) return;
        var folder = Ctx.Dialogs.PickFolder("Folder for the invoice package");
        if (folder is null) return;
        string? zip = null;
        if (await Run("EXPORTING", () => Task.Run(() => zip = _m.ExportInvoice(folder, b, Project.Workflow.HeaderInfo()))))
        { Ctx.Toasts.Show("PACKAGE EXPORTED", Path.GetFileName(zip!), ToastKind.Good); DialogService.OpenWithShell(folder); }
    }

    [RelayCommand]
    private async Task SubmitInvoice()
    {
        var inv = SelectedStoredInvoice?.Inv;
        if (inv is null) return;
        var no = AconexNo.Trim();
        if (await Run("SUBMITTING", () => Task.Run(() => Project.Workflow.Submit(inv, no)))) Changed();
    }

    [RelayCommand]
    private async Task RejectInvoice()
    {
        var inv = SelectedStoredInvoice?.Inv;
        if (inv is null || RejectReason.Trim().Length == 0) { Ctx.Toasts.Show("TYPE THE REJECTION REASON", kind: ToastKind.Warn); return; }
        var reason = RejectReason.Trim();
        if (await Run("REJECTING", () => Task.Run(() => Project.Workflow.Reject(inv, reason)))) { RejectReason = ""; Changed(); }
    }

    [RelayCommand]
    private async Task ApproveInvoice()
    {
        var inv = SelectedStoredInvoice?.Inv;
        if (inv is null || !Ctx.Dialogs.Confirm("APPROVE", $"Approve and lock {inv.Title}?")) return;
        if (await Run("APPROVING", () => Task.Run(() => Project.Workflow.Approve(inv)))) Changed();
    }

    /// <summary>Next revision of a rejected invoice: same DN lines (locked to this invoice number), rebuilt.</summary>
    [RelayCommand]
    private void NewRevision()
    {
        var inv = SelectedStoredInvoice?.Inv;
        var po = inv is null ? null : S.FindPo(inv.ContractNo);
        if (inv is null || po is null) return;
        var rev = Project.Workflow.NextRevision(inv.ContractNo, inv.Subcontractor, inv.InvoiceNo);
        _build = _m.BuildInvoice(po, inv.InvoiceNo, _m.LockedLines(inv), rev);
        ShowBuild(_build.Build);
        Ctx.Toasts.Show($"REV {rev} BUILT", "Check it, then SAVE DRAFT.", ToastKind.Info);
    }

    // =================================================================== DN LOOKUP

    public ObservableCollection<string> Suppliers { get; } = new();
    public ObservableCollection<DnLookupRow> LookupRows { get; } = new();
    [ObservableProperty] private string _lookupSupplier = "";
    [ObservableProperty] private string _lookupQuery = "";
    partial void OnLookupSupplierChanged(string value) => RunLookup();
    partial void OnLookupQueryChanged(string value) => RunLookup();

    [RelayCommand]
    private void RunLookup()
    {
        LookupRows.Clear();
        foreach (var r in _m.Lookup(LookupSupplier, LookupQuery).Take(500)) LookupRows.Add(r);
    }

    // =================================================================== SETTINGS

    [ObservableProperty] private bool _cloudReading;
    [ObservableProperty] private string _visionEffort = "medium";
    [ObservableProperty] private string _toleranceMode = "CLAUSE";
    [ObservableProperty] private string _customTolerance = "5";
    [ObservableProperty] private string _pipeLength = "6";
    [ObservableProperty] private string _mosPct = "75";
    [ObservableProperty] private bool _mosRequiresMir = true;
    [ObservableProperty] private string _autoHigh = "85";
    [ObservableProperty] private string _autoMedium = "60";
    [ObservableProperty] private bool _boqAi;
    [ObservableProperty] private string _mosTemplate = "";
    [ObservableProperty] private string _readerStatus = "";
    public string[] Efforts { get; } = { "low", "medium", "high" };
    public string[] ToleranceModes { get; } = { "CLAUSE", "HEADER", "CUSTOM" };

    private void LoadSettings()
    {
        var s = _m.Settings;
        CloudReading = s.CloudReading; VisionEffort = s.VisionEffort; ToleranceMode = s.ToleranceMode;
        CustomTolerance = (s.CustomTolerancePct * 100).ToString("0.##", CultureInfo.InvariantCulture); PipeLength = s.PipeLengthM.ToString("0.##", CultureInfo.InvariantCulture);
        MosPct = (s.MosPct * 100).ToString("0.##", CultureInfo.InvariantCulture); MosRequiresMir = s.MosRequiresMir;
        AutoHigh = (s.AutoCodeHigh * 100).ToString("0", CultureInfo.InvariantCulture); AutoMedium = (s.AutoCodeMedium * 100).ToString("0", CultureInfo.InvariantCulture);
        BoqAi = s.BoqAiFallback; MosTemplate = s.MosTemplatePath;
        ReaderStatus = $"Text layer: PdfPig  |  OCR: {_ocr.Name}{(_ocr.IsAvailable ? "" : " - not available")}  |  Cloud reading: {_m.Vision().Status}";
    }

    private static double? Num(string s) => double.TryParse((s ?? "").Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    [RelayCommand]
    private void SaveSettings()
    {
        var s = _m.Settings;
        s.CloudReading = CloudReading; s.VisionEffort = VisionEffort; s.ToleranceMode = ToleranceMode;
        s.CustomTolerancePct = (Num(CustomTolerance) ?? 5) / 100; s.PipeLengthM = Num(PipeLength) is double p && p > 0 ? p : 6;
        s.MosPct = (Num(MosPct) ?? 75) / 100; s.MosRequiresMir = MosRequiresMir;
        s.AutoCodeHigh = (Num(AutoHigh) ?? 85) / 100; s.AutoCodeMedium = (Num(AutoMedium) ?? 60) / 100;
        s.BoqAiFallback = BoqAi; s.MosTemplatePath = MosTemplate.Trim();
        try { s.Save(); } catch (Exception ex) { Ctx.Toasts.Show("SETTINGS NOT SAVED", ex.Message, ToastKind.Error); return; }
        LoadSettings();
        Ctx.Toasts.Show("MATERIALS SETTINGS SAVED", "", ToastKind.Good);
        Changed();
    }

    [RelayCommand]
    private void BrowseTemplate()
    {
        var p = Ctx.Dialogs.OpenFile("MOS template workbook", "Excel|*.xlsx;*.xlsm");
        if (p != null) MosTemplate = p;
    }

    // =================================================================== export current view

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        if (IsMatchTab)
            yield return new ExportSheet
            {
                Name = "3-WAY MATCH", Title = "PO / DN / MIR MATCH", Subtitle = _m.LastMatch.Summary,
                Columns = new() { new("STATUS"), new("DN"), new("DN DATE", ColumnKind.Date), new("ITEM"), new("DESCRIPTION", Width: 40), new("BATCH"), new("DN QTY", ColumnKind.Number), new("DN UNIT"), new("QTY", ColumnKind.Number), new("UNIT"), new("PO LINE", Width: 36), new("CUM", ColumnKind.Number), new("PO QTY", ColumnKind.Number), new("MIR"), new("BATCH CHECK"), new("NOTES", Width: 60) },
                Rows = MatchRows.Select(r => new object?[] { r.Status, r.Dn.DnNo, r.Dn.DnDate, r.Line.ItemNo, r.Line.Description, r.Line.Batch, r.Line.RawQty, r.Line.RawUnit, r.Qty, r.Unit, r.PoLineText, r.CumQty, r.PoQty, r.MirNos, r.BatchCheck, r.NoteText }).ToList(),
            };
        else if (IsLookupTab)
            yield return new ExportSheet
            {
                Name = "DN LOOKUP", Title = "DN LOOKUP",
                Columns = new() { new("SUPPLIER"), new("DN"), new("DN DATE", ColumnKind.Date), new("PO"), new("LINES", ColumnKind.Integer), new("QTY", ColumnKind.Number), new("INVOICE"), new("STATUS"), new("ACONEX"), new("MIR"), new("MIR STATUS"), new("MATCH", Width: 40) },
                Rows = LookupRows.Select(r => new object?[] { r.Supplier, r.DnNo, r.DnDate, r.PoNo, r.Lines, r.Qty, r.Invoice, r.InvoiceStatus, r.AconexNo, r.MirNo, r.MirStatus, r.Match }).ToList(),
            };
        else if (IsPoTab && SelectedPo != null)
            yield return new ExportSheet
            {
                Name = "PO LINES", Title = $"PO {SelectedPo.Po.PoNo} - {SelectedPo.Po.Supplier}", Subtitle = PoTerms,
                Columns = new() { new("NO", ColumnKind.Integer), new("DESCRIPTION", Width: 40), new("UNIT"), new("QTY", ColumnKind.Number), new("RATE", ColumnKind.Number), new("AMOUNT", ColumnKind.Money), new("DELIVERED", ColumnKind.Number), new("BOQ CODE"), new("COST CODE"), new("BUDGET RESOURCE"), new("CODE STATUS"), new("SCORE", ColumnKind.Percent), new("CODE SOURCE", Width: 60) },
                Rows = PoLines.Select(r => new object?[] { r.L.LineNo, r.L.Description, r.L.Unit, r.L.Qty, r.L.Rate, r.L.Amount, r.Delivered, r.L.BoqCode, r.L.CostCode, r.L.BudgetResourceCode, r.CodeStatusText, r.L.CodeScore, r.L.CodeSource }).ToList(),
            };
        else if (IsOverviewTab)
            foreach (var s in Overview.ExportCurrentView()) yield return s;
    }
}
