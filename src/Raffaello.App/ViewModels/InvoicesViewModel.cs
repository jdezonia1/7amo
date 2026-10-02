using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Mapping;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

public sealed class StoredInvoiceRow
{
    public required SubInvoice Invoice { get; init; }
    public double CurrGross { get; init; }
    public string Title => Invoice.Title;
    public string Caption => $"{Invoice.ContractNo}  |  SAR {CurrGross:N0}{(Invoice.AconexWorkflowNo.Length > 0 ? "  |  " + Invoice.AconexWorkflowNo : "")}";
    public string Status => Invoice.Status switch { SubInvoiceStatus.Approved => "APPROVED", SubInvoiceStatus.Rejected => "REJECTED", SubInvoiceStatus.Submitted => "DUE", _ => "OPEN" };
}

public sealed class InvoiceLineRow
{
    public required SubInvoiceLine Line { get; init; }
    public bool IsItem => Line.Kind == "ITEM";
    public string ItemNo => Line.ItemNo;
    public string BoqCode => Line.BoqCode;
    public string Description => IsItem ? (Line.BoqDescription.Length > 0 ? Line.BoqDescription : Line.Description) : Line.Description;
    public string Unit => Line.Unit;
    public double? ContractQty => IsItem ? Line.ContractQty : null;
    public double? Rate => IsItem ? Line.Rate : null;
    public double? StagePct => IsItem ? Line.StagePct : null;
    public double? PrevQty => IsItem ? Line.PrevQty : null;
    public double? CurrQty => IsItem ? Line.CurrQty : null;
    public double? CumQty => IsItem ? Line.CumQty : null;
    public double? CurrAmount => IsItem ? Line.CurrAmount : null;
    public double? CumAmount => IsItem ? Line.CumAmount : null;
    public string Explanation => Line.Explanation;
}

/// <summary>Needs-confirmation group: same stage / system / band / area mapped the same way.</summary>
public sealed class ConfirmRow
{
    public required List<MappedPart> Parts { get; init; }
    public MappedPart First => Parts[0];
    public string Confidence => First.Confidence;
    public string Status => Confidence == Raffaello.Core.Mapping.Confidence.Unmapped ? "OVER" : "CHECK";
    public string Stage => First.Line.Stage;
    public string System => First.System;
    public string Band => First.Band;
    public string Area => First.Area;
    public string ItemNo => First.Item?.ItemNo ?? "-";
    public string BoqCode => First.BoqCode;
    public double Qty => Parts.Sum(p => p.Qty);
    public int Lines => Parts.Count;
    public string Explanation => First.Explanation;
}

/// <summary>Subcontractor invoices: build from the ledger in the template layout, revisions, Aconex submission, approval lock, diff, Excel/PDF.</summary>
public sealed partial class InvoicesViewModel : PageViewModel
{
    public InvoicesViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "Invoices";
    public override string Title => "INVOICES";
    public override string Subtitle => "Built from the room ledger: claims up to this invoice no, previous = last approved invoice, approved invoices are locked";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<string> ContractNos { get; } = new();
    public ObservableCollection<string> Subcontractors { get; } = new();
    public ObservableCollection<StoredInvoiceRow> Stored { get; } = new();
    public ObservableCollection<InvoiceLineRow> Lines { get; } = new();
    public ObservableCollection<ConfirmRow> Confirmations { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<RevisionDiff> Diff { get; } = new();
    public ObservableCollection<ContractItem> ItemChoices { get; } = new();
    public ObservableCollection<ContractItemBoq> BoqChoices { get; } = new();
    public string[] Tabs { get; } = { "LINES", "NEEDS CONFIRMATION", "WARNINGS", "DIFF" };

    [ObservableProperty] private string _contractNo = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _invoiceNo = "1";
    [ObservableProperty] private string _tab = "LINES";
    [ObservableProperty] private bool _onlyMoved = true;
    [ObservableProperty] private StoredInvoiceRow? _selectedStored;
    [ObservableProperty] private ConfirmRow? _selectedConfirm;
    [ObservableProperty] private ContractItem? _learnItem;
    [ObservableProperty] private ContractItemBoq? _learnBoq;
    [ObservableProperty] private string _aconexNo = "";
    [ObservableProperty] private string _rejectReason = "";
    [ObservableProperty] private string _retentionPct = "10";

    [ObservableProperty] private string _heading = "No invoice built";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _mappingText = "";
    [ObservableProperty] private InvoiceTotals? _totals;
    [ObservableProperty] private bool _hasBuild;
    [ObservableProperty] private bool _isLocked;

    private InvoiceBuild? _build;
    private bool _buildIsStored;

    public bool IsLines => Tab == "LINES";
    public bool IsConfirm => Tab == "NEEDS CONFIRMATION";
    public bool IsWarnings => Tab == "WARNINGS";
    public bool IsDiff => Tab == "DIFF";

    partial void OnTabChanged(string value) { OnPropertyChanged(nameof(IsLines)); OnPropertyChanged(nameof(IsConfirm)); OnPropertyChanged(nameof(IsWarnings)); OnPropertyChanged(nameof(IsDiff)); }
    partial void OnOnlyMovedChanged(bool value) => FillLines();
    partial void OnContractNoChanged(string value) => FillItemChoices();
    partial void OnSelectedStoredChanged(StoredInvoiceRow? value) { if (value != null) ShowStored(value.Invoice); }
    partial void OnSelectedConfirmChanged(ConfirmRow? value) => LearnItem = value?.First.Item is { } it ? ItemChoices.FirstOrDefault(i => i.ItemNo == it.ItemNo) : null;
    partial void OnLearnItemChanged(ContractItem? value)
    {
        BoqChoices.Clear();
        if (value is null) return;
        foreach (var l in Project.Snapshot.ItemBoqs.Where(l => l.ContractNo == ContractNo && l.ItemNo == value.ItemNo).OrderBy(l => l.Order)) BoqChoices.Add(l);
        LearnBoq = BoqChoices.FirstOrDefault(b => b.BoqCode == SelectedConfirm?.BoqCode) ?? BoqChoices.FirstOrDefault();
    }

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        Sync(ContractNos, s.ContractItems.Select(i => i.ContractNo).Concat(s.TemplateRows.Select(t => t.ContractNo)).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        Sync(Subcontractors, s.Claims.Select(c => c.Subcontractor).Concat(s.SubInvoices.Select(i => i.Subcontractor)).Where(x => x.Length > 0).Distinct().OrderBy(x => x));
        if (ContractNo.Length == 0) ContractNo = ContractNos.FirstOrDefault() ?? "";
        if (Sub.Length == 0) Sub = s.Contracts.FirstOrDefault(c => c.ContractNo == ContractNo)?.Subcontractor is { Length: > 0 } cs ? cs : Subcontractors.FirstOrDefault() ?? "";
        var keep = SelectedStored?.Invoice.Id;
        Stored.Clear();
        foreach (var inv in s.SubInvoices.OrderBy(i => i.Subcontractor).ThenByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision))
        {
            var lines = s.SubInvoiceLines.Where(l => l.SubInvoiceId == inv.Id);
            Stored.Add(new StoredInvoiceRow { Invoice = inv, CurrGross = InvoiceTotals.Of(inv, lines).CurrGross });
        }
        FillItemChoices();
        if (_buildIsStored && keep is { } id)
        {
            var row = Stored.FirstOrDefault(r => r.Invoice.Id == id);
            if (row != null) SelectedStored = row;
        }
    }

    private void FillItemChoices()
    {
        ItemChoices.Clear();
        foreach (var i in Project.Snapshot.ContractItems.Where(i => i.ContractNo == ContractNo).OrderBy(i => i.Order)) ItemChoices.Add(i);
    }

    private static void Sync(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    private int Inv => int.TryParse(InvoiceNo, out var n) ? n : 0;
    private static double? D(string s) => double.TryParse((s ?? "").Replace(",", "").Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    [RelayCommand]
    private async Task Build()
    {
        if (ContractNo.Length == 0 || Sub.Length == 0 || Inv <= 0) { Ctx.Toasts.Show("CONTRACT, SUBCONTRACTOR AND INVOICE NO NEEDED", kind: ToastKind.Warn); return; }
        var all = Project.Snapshot.SubInvoices.Where(i => i.ContractNo == ContractNo && i.Subcontractor == Sub && i.InvoiceNo == Inv).OrderBy(i => i.Revision).ToList();
        var last = all.LastOrDefault();
        if (last is { Locked: true }) { Ctx.Toasts.Show($"{last.Title} IS APPROVED AND LOCKED", "Build the next invoice number instead.", ToastKind.Warn); ShowStored(last); return; }
        var revision = last is null ? 0 : last.Status is SubInvoiceStatus.Draft ? last.Revision : last.Revision + 1;
        var (c, sub, inv) = (ContractNo, Sub, Inv);
        try
        {
            var build = await Task.Run(() => Project.Workflow.BuildInvoice(c, sub, inv, revision));
            build.Header.RetentionPct = (D(RetentionPct) ?? 10) / 100.0;
            Show(build, stored: false);
            var before = all.LastOrDefault(i => i.Revision < revision) ?? (revision == last?.Revision ? last : null);
            FillDiff(before);
            Tab = Confirmations.Count > 0 ? "NEEDS CONFIRMATION" : "LINES";
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT BUILD INVOICE", ex.Message, ToastKind.Error); }
    }

    private void ShowStored(SubInvoice inv)
    {
        var build = Project.Workflow.Stored(inv);
        Show(build, stored: true);
        ContractNo = inv.ContractNo; Sub = inv.Subcontractor; InvoiceNo = inv.InvoiceNo.ToString(CultureInfo.InvariantCulture);
        RetentionPct = (inv.RetentionPct * 100).ToString("0.##", CultureInfo.InvariantCulture);
        AconexNo = inv.AconexWorkflowNo;
        var before = Project.Snapshot.SubInvoices.Where(i => i.ContractNo == inv.ContractNo && i.Subcontractor == inv.Subcontractor && i.InvoiceNo == inv.InvoiceNo && i.Revision < inv.Revision)
            .OrderBy(i => i.Revision).LastOrDefault();
        FillDiff(before);
    }

    private void Show(InvoiceBuild build, bool stored)
    {
        _build = build;
        _buildIsStored = stored;
        HasBuild = true;
        IsLocked = build.Header.Locked;
        var h = build.Header;
        Heading = $"{h.Title}  |  {h.ContractNo}";
        StatusText = stored
            ? $"{h.Status}{(h.AconexWorkflowNo.Length > 0 ? "  |  ACONEX " + h.AconexWorkflowNo : "")}{(h.RejectionReason.Length > 0 && h.Status == SubInvoiceStatus.Rejected ? "  |  " + h.RejectionReason : "")}{(h.Locked ? "  |  LOCKED" : "")}"
            : $"PREVIEW (not saved){(build.PreviousApproved is { } p ? "  |  previous = " + p.Title : "  |  no approved invoice before")}";
        Totals = build.Totals;
        var m = build.Mapping;
        MappingText = stored ? "Stored revision - rebuild to see the mapping" :
            $"MAPPED {m.CoveragePct:P1}  |  AUTO {m.AutoPct:P1}  |  {m.Parts.Count} parts  |  {m.NeedsConfirmation.Count()} need confirmation  |  {m.Held.Count} held by pending checks";
        Confirmations.Clear();
        foreach (var g in m.NeedsConfirmation.GroupBy(p => (p.Line.Stage, p.System, p.Band, p.Area, p.Item?.ItemNo, p.BoqCode, p.Confidence)).OrderByDescending(g => g.Sum(p => Math.Abs(p.Qty))))
            Confirmations.Add(new ConfirmRow { Parts = g.ToList() });
        Warnings.Clear();
        foreach (var w in build.Warnings) Warnings.Add(w);
        foreach (var hq in m.Held.GroupBy(x => x.HeldReason)) Warnings.Add($"HELD: {hq.Count()} line(s) - {hq.Key}");
        FillLines();
    }

    private void FillLines()
    {
        Lines.Clear();
        if (_build is null) return;
        var rows = OnlyMoved ? InvoicePdfExporter.FilteredRows(_build.Lines) : _build.Lines;
        foreach (var l in rows) Lines.Add(new InvoiceLineRow { Line = l });
    }

    private void FillDiff(SubInvoice? before)
    {
        Diff.Clear();
        if (_build is null || before is null || before.Id == _build.Header.Id) return;
        foreach (var d in InvoiceBuilder.Diff(Project.Workflow.LinesOf(before), _build.Lines)) Diff.Add(d);
    }

    [RelayCommand]
    private async Task Learn()
    {
        if (SelectedConfirm is null || LearnItem is null) { Ctx.Toasts.Show("PICK A GROUP AND THE CONTRACT ITEM", kind: ToastKind.Warn); return; }
        var part = SelectedConfirm.First;
        var (contract, item, boq) = (ContractNo, LearnItem.ItemNo, LearnBoq?.BoqCode);
        var itemKey = part.ItemRuleKey;
        var boqKey = part.BoqRuleKey(item);
        var ok = await Ctx.Data.WriteAsync(p =>
        {
            if (part.Item?.ItemNo != item || part.Confidence != Confidence.Learned) p.Workflow.Learn("ITEM", contract, itemKey, item, "confirmed on invoice screen");
            if (boq != null) p.Workflow.Learn("BOQ", contract, boqKey, boq, "confirmed on invoice screen");
        }, Ctx.Toasts, "RULE LEARNED");
        if (ok && !_buildIsStored) await Build();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (_build is null) return;
        if (_buildIsStored) { Ctx.Toasts.Show("ALREADY SAVED", "Build again to refresh it from the ledger.", ToastKind.Info); return; }
        var build = _build;
        build.Header.RetentionPct = (D(RetentionPct) ?? 10) / 100.0;
        SubInvoice? saved = null;
        if (await Ctx.Data.WriteAsync(p => saved = p.Workflow.SaveInvoice(build), Ctx.Toasts, $"{build.Header.Title} SAVED") && saved != null)
            SelectStored(saved.Id);
    }

    private void SelectStored(long id)
    {
        var row = Stored.FirstOrDefault(r => r.Invoice.Id == id);
        if (row != null) SelectedStored = row;
    }

    private SubInvoice? StoredHeader() => _buildIsStored ? _build?.Header : null;

    [RelayCommand]
    private async Task Submit()
    {
        if (StoredHeader() is not { } inv) { Ctx.Toasts.Show("SAVE THE INVOICE FIRST", kind: ToastKind.Warn); return; }
        if (string.IsNullOrWhiteSpace(AconexNo)) { Ctx.Toasts.Show("ACONEX WORKFLOW NO NEEDED", kind: ToastKind.Warn); return; }
        var no = AconexNo.Trim();
        await Ctx.Data.WriteAsync(p => p.Workflow.Submit(inv, no), Ctx.Toasts, $"{inv.Title} SUBMITTED");
    }

    [RelayCommand]
    private async Task Reject()
    {
        if (StoredHeader() is not { } inv) { Ctx.Toasts.Show("SAVE THE INVOICE FIRST", kind: ToastKind.Warn); return; }
        if (string.IsNullOrWhiteSpace(RejectReason)) { Ctx.Toasts.Show("REJECTION REASON NEEDED", kind: ToastKind.Warn); return; }
        var reason = RejectReason.Trim();
        if (await Ctx.Data.WriteAsync(p => p.Workflow.Reject(inv, reason), Ctx.Toasts, $"{inv.Title} REJECTED")) RejectReason = "";
    }

    [RelayCommand]
    private async Task Approve()
    {
        if (StoredHeader() is not { } inv) { Ctx.Toasts.Show("SAVE THE INVOICE FIRST", kind: ToastKind.Warn); return; }
        if (!Ctx.Dialogs.Confirm("Approve invoice", $"Approve {inv.Title}?\n\nIt becomes the 'previous' for the next invoice and is locked (no more edits).")) return;
        await Ctx.Data.WriteAsync(p => p.Workflow.Approve(inv), Ctx.Toasts, $"{inv.Title} APPROVED AND LOCKED");
    }

    [RelayCommand]
    private async Task NewRevision()
    {
        if (StoredHeader() is not { } inv) return;
        if (inv.Locked) { Ctx.Toasts.Show("APPROVED INVOICES ARE LOCKED", "Build the next invoice number.", ToastKind.Warn); return; }
        if (inv.Status != SubInvoiceStatus.Rejected && !Ctx.Dialogs.Confirm("New revision", $"{inv.Title} is {inv.Status}. Build Rev {inv.Revision + 1} from the ledger anyway?")) return;
        var (c, sub, n) = (inv.ContractNo, inv.Subcontractor, inv.InvoiceNo);
        var rev = Project.Workflow.NextRevision(c, sub, n);
        var build = await Task.Run(() => Project.Workflow.BuildInvoice(c, sub, n, rev));
        build.Header.RetentionPct = inv.RetentionPct;
        Show(build, stored: false);
        FillDiff(inv);
        Tab = "DIFF";
    }

    [RelayCommand]
    private void ExportExcel()
    {
        if (_build is null) return;
        var path = Ctx.Dialogs.SaveFile("Export invoice", $"{Safe(_build.Header.Title)}.xlsx");
        if (path is null) return;
        try
        {
            InvoiceExcelExporter.Export(path, _build, Project.Workflow.HeaderInfo());
            Ctx.Toasts.Show("INVOICE EXPORTED", Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { Ctx.Toasts.Show("EXPORT FAILED", ex.Message + " (is the file open in Excel?)", ToastKind.Error); }
    }

    [RelayCommand]
    private void ExportPdf()
    {
        if (_build is null) return;
        var path = Ctx.Dialogs.SaveFile("Export invoice PDF (rows with quantities only)", $"{Safe(_build.Header.Title)}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            InvoicePdfExporter.Export(path, _build, Project.Workflow.HeaderInfo());
            Ctx.Toasts.Show("PDF EXPORTED", Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (Exception ex) { Ctx.Toasts.Show("PDF FAILED", ex.Message, ToastKind.Error); }
    }

    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is not { } key) return;
        var parts = key.Split('|');
        // "contract|sub|inv" (queue) or "sub|inv"
        var (contract, sub, inv) = parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : parts.Length == 2 ? ("", parts[0], parts[1]) : ("", "", "");
        var row = Stored.Where(r => r.Invoice.Subcontractor == sub && r.Invoice.InvoiceNo.ToString(CultureInfo.InvariantCulture) == inv && (contract.Length == 0 || r.Invoice.ContractNo == contract))
            .OrderByDescending(r => r.Invoice.Revision).FirstOrDefault();
        if (row != null) SelectedStored = row;
        else if (sub.Length > 0) { Sub = sub; InvoiceNo = inv; if (contract.Length > 0) ContractNo = contract; }
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "INVOICE LINES", Title = Heading,
            Columns = new() { new("ITEM"), new("BOQ CODE"), new("DESCRIPTION", Width: 60), new("UNIT"), new("CONTRACT QTY", ColumnKind.Number), new("RATE", ColumnKind.Money), new("STAGE %", ColumnKind.Percent),
                new("PREV QTY", ColumnKind.Number), new("CURR QTY", ColumnKind.Number), new("CUM QTY", ColumnKind.Number), new("CURR AMOUNT", ColumnKind.Money), new("CUM AMOUNT", ColumnKind.Money) },
            Rows = Lines.Select(l => new object?[] { l.ItemNo, l.BoqCode, l.Description, l.Unit, l.ContractQty, l.Rate, l.StagePct, l.PrevQty, l.CurrQty, l.CumQty, l.CurrAmount, l.CumAmount }).ToList(),
        };
        if (Confirmations.Count > 0)
            yield return new ExportSheet
            {
                Name = "NEEDS CONFIRMATION", Title = "MAPPING TO CONFIRM",
                Columns = new() { new("CONFIDENCE"), new("STAGE"), new("SYSTEM"), new("BAND"), new("AREA"), new("ITEM"), new("BOQ CODE"), new("QTY", ColumnKind.Number), new("LINES", ColumnKind.Integer), new("EXPLANATION", Width: 90) },
                Rows = Confirmations.Select(c => new object?[] { c.Confidence, c.Stage, c.System, c.Band, c.Area, c.ItemNo, c.BoqCode, c.Qty, c.Lines, c.Explanation }).ToList(),
            };
    }
}
