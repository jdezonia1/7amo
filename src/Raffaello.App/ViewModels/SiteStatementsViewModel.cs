using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Ledger;
using Raffaello.Core.Statements;

namespace Raffaello.App.ViewModels;

public sealed partial class ToggleOption : ObservableObject
{
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _isOn = true;
}

public sealed class StatementPreviewRow
{
    public required ClaimLine Line { get; init; }
    public required ClaimCheck Check { get; init; }
    public string Status => Check.Decision switch { ClaimDecision.Blocked => "OVER", ClaimDecision.AcceptedOver => "CHECK", _ => "OK" };
    public string Checks => (Line.QtyAbove45 != 0 ? $">4.5m {Line.QtyAbove45:0.#}  " : "") + (Line.LengthApplies ? $"15m {Line.LengthClaimedQty:0.#}" : "");
}

/// <summary>Site statements: generate the per-room sheet for a subcontractor, read it back with duplicate detection and remaining checks.</summary>
public sealed partial class SiteStatementsViewModel : PageViewModel
{
    public SiteStatementsViewModel(PageContext ctx) : base(ctx) { }

    public override string Key => "SiteStatements";
    public override string Title => "SITE STATEMENTS";
    public override string Subtitle => "Issue a statement sheet per subcontractor, import it back into the room ledger (duplicates and over-remaining are caught)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    public ObservableCollection<string> Subcontractors { get; } = new();
    public ObservableCollection<ToggleOption> Stages { get; } = new();
    public ObservableCollection<ToggleOption> Systems { get; } = new();
    public ObservableCollection<SiteStatement> History { get; } = new();
    public ObservableCollection<StatementPreviewRow> Preview { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();

    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _statementNo = "";
    [ObservableProperty] private string _invoiceNo = "1";
    [ObservableProperty] private string _overReason = "";
    [ObservableProperty] private string _previewText = "No statement opened";
    [ObservableProperty] private string _previewFile = "";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private SiteStatement? _selectedHistory;

    private StatementImportResult? _preview;

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        var subs = s.Claims.Select(c => c.Subcontractor).Concat(s.Contracts.Select(c => c.Subcontractor)).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
        if (!Subcontractors.SequenceEqual(subs)) { Subcontractors.Clear(); foreach (var x in subs) Subcontractors.Add(x); }
        if (Sub.Length == 0) Sub = Subcontractors.FirstOrDefault() ?? "";
        if (Stages.Count == 0)
        {
            var known = s.RoomQtys.Select(q => q.Stage).Distinct().ToList();
            foreach (var st in SiteStatementService.DefaultStages.Concat(known).Distinct()) Stages.Add(new ToggleOption { Name = st, IsOn = SiteStatementService.DefaultStages.Contains(st) });
            var items = s.RoomQtys.Select(q => q.Item).Distinct().ToList();
            foreach (var sy in SiteStatementService.DefaultSystems.Concat(items).Distinct()) Systems.Add(new ToggleOption { Name = sy, IsOn = SiteStatementService.DefaultSystems.Contains(sy) });
        }
        History.Clear();
        foreach (var h in s.Statements.OrderByDescending(x => x.At)) History.Add(h);
        if (StatementNo.Length == 0) StatementNo = NextNo();
    }

    private string NextNo() => $"ST-{Sub}-{Project.Snapshot.Statements.Count(x => x.Subcontractor == Sub && x.Direction == "OUT") + 1:00}";

    partial void OnSubChanged(string value) => StatementNo = NextNo();

    [RelayCommand]
    private async Task Generate()
    {
        if (Sub.Length == 0 || StatementNo.Length == 0) { Ctx.Toasts.Show("SUBCONTRACTOR AND STATEMENT NO NEEDED", kind: ToastKind.Warn); return; }
        var path = Ctx.Dialogs.SaveFile("Save site statement", $"{StatementNo}.xlsx");
        if (path is null) return;
        var (sub, no) = (Sub, StatementNo.Trim());
        var stages = Stages.Where(x => x.IsOn).Select(x => x.Name).ToList();
        var systems = Systems.Where(x => x.IsOn).Select(x => x.Name).ToList();
        var rooms = 0;
        if (await Ctx.Data.WriteAsync(p => rooms = p.Workflow.GenerateStatement(path, sub, no, Buildings.Branded, stages, systems), Ctx.Toasts))
        {
            Ctx.Toasts.Show("STATEMENT ISSUED", $"{no}: {rooms} rooms, {stages.Count} stages x {systems.Count} systems", ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
    }

    [RelayCommand]
    private async Task Open()
    {
        var path = Ctx.Dialogs.OpenFile("Filled site statement");
        if (path is null) return;
        var inv = int.TryParse(InvoiceNo, out var n) ? n : 0;
        try
        {
            var res = await Task.Run(() => Project.Workflow.PreviewStatement(path, inv, Buildings.Branded));
            _preview = res;
            PreviewFile = path;
            HasPreview = true;
            PreviewText = res.Summary;
            Preview.Clear();
            foreach (var (line, check) in res.Checks) Preview.Add(new StatementPreviewRow { Line = line, Check = check });
            Issues.Clear();
            foreach (var i in res.Issues) Issues.Add($"{i.Level.ToString().ToUpperInvariant()}  row {i.Row}: {i.Message}");
            if (res.IsDuplicate) Ctx.Toasts.Show("DUPLICATE STATEMENT", "This statement was already imported - it will not be posted again.", ToastKind.Warn, 8);
        }
        catch (Exception ex) { Ctx.Toasts.Show("CANNOT READ STATEMENT", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private async Task Commit()
    {
        if (_preview is null) return;
        if (_preview.IsDuplicate) { Ctx.Toasts.Show("DUPLICATE - NOT IMPORTED", kind: ToastKind.Warn); return; }
        var reason = string.IsNullOrWhiteSpace(OverReason) ? null : OverReason.Trim();
        if (_preview.Blocked > 0 && reason is null &&
            !Ctx.Dialogs.Confirm("Over remaining", $"{_preview.Blocked} line(s) are above the remaining quantity and will be SKIPPED (no over reason given).\n\nContinue?")) return;
        var (res, path) = (_preview, PreviewFile);
        var posted = 0;
        if (await Ctx.Data.WriteAsync(p => posted = p.Workflow.CommitStatement(res, path, reason), Ctx.Toasts))
        {
            Ctx.Toasts.Show("STATEMENT POSTED", $"{posted} claim lines into the ledger", ToastKind.Good);
            _preview = null; HasPreview = false; Preview.Clear(); Issues.Clear(); PreviewText = "No statement opened"; OverReason = "";
        }
    }

    /// <summary>Attaches the signed / stamped scan of a statement (it goes into the invoice package, 06_Site_statements).</summary>
    [RelayCommand]
    private async Task AttachScan()
    {
        if (SelectedHistory is not { } st) { Ctx.Toasts.Show("PICK A STATEMENT IN HISTORY", kind: ToastKind.Warn); return; }
        var file = Ctx.Dialogs.OpenFile($"{st.StatementNo}: scan or file", "PDF / image / Excel|*.pdf;*.jpg;*.jpeg;*.png;*.xlsx|All files|*.*");
        if (file is null) return;
        await Ctx.Data.WriteAsync(p => p.Workflow.AddAttachment(AttachmentKinds.Statement, st.StatementNo, AttachmentKinds.Scan, file), Ctx.Toasts, "STATEMENT FILE ATTACHED");
    }

    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        yield return new ExportSheet
        {
            Name = "STATEMENT PREVIEW", Title = PreviewText,
            Columns = new() { new("ROOM"), new("STAGE"), new("ITEM"), new("QTY", ColumnKind.Number), new("SITE %", ColumnKind.Percent), new("WIR %", ColumnKind.Percent), new("CHECKS"), new("REMAINING", ColumnKind.Number), new("MESSAGE", Width: 60), new("STATUS") },
            Rows = Preview.Select(p => new object?[] { p.Line.Room, p.Line.Stage, p.Line.Item, p.Line.Qty, p.Line.SitePct, p.Line.WirPct, p.Checks, p.Check.Remaining, p.Check.Message, p.Status }).ToList(),
        };
    }
}
