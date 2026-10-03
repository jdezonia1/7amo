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

/// <summary>A cable flag or a contract-rule warning of the statement preview (bypassable with a reason).</summary>
public sealed class PreviewWarningRow
{
    public Raffaello.Core.Cables.CableFlag? Cable { get; init; }
    public Raffaello.Core.Contracts.Rules.RuleWarning? Rule { get; init; }
    public bool IsBypassed => Cable?.IsBypassed ?? Rule?.IsBypassed ?? false;
    public string Tag => IsBypassed ? "BYPASSED" : Cable != null ? Cable.Tag : "CHECK";
    public string Source => Cable != null ? "CABLE" : "CONTRACT";
    public string Code => Cable?.Code ?? Rule?.Code ?? "";
    public string Subject => Cable?.ClaimText ?? Rule?.ContextKey ?? "";
    public string Message => Cable?.Message ?? (Rule is { } r ? $"{r.Message} [{r.Source}]" : "");
    public string Decision => Cable?.Decision is { } d ? $"{d.By} {d.At:dd-MMM-yy}: {d.Reason}" : Rule?.Bypass is { } b ? $"{b.BypassedBy} {b.BypassedAt:dd-MMM-yy}: {b.Reason}" : "";
}

/// <summary>Site statements: generate the per-room sheet for a subcontractor, read it back with duplicate detection and remaining checks.</summary>
public sealed partial class SiteStatementsViewModel : PageViewModel
{
    private readonly Services.SmartReading _reading;
    public SiteStatementsViewModel(PageContext ctx, Services.SmartReading reading) : base(ctx) { _reading = reading; }

    /// <summary>Pages of a scanned statement to read (summary + marked typical-unit drawings), e.g. "1-6".</summary>
    [ObservableProperty] private string _scanPages = "1-6";

    /// <summary>
    /// A scanned / handwritten statement (summary table + marked drawings) read offline into a DRAFT: rows (stage, unit type, systems, %,
    /// rooms) x the counts written on the typical-unit drawing -> claim lines, checked against the room caps like any statement.
    /// Handwriting read offline is weak: every line is a draft to correct before posting.
    /// </summary>
    [RelayCommand]
    private async Task ReadScannedStatement()
    {
        var path = Ctx.Dialogs.OpenFile("Scanned site statement (PDF)", "PDF|*.pdf|All files|*.*");
        if (path is null) return;
        if (string.IsNullOrWhiteSpace(Sub)) { Ctx.Toasts.Show("SUBCONTRACTOR NEEDED", "Enter the subcontractor first.", ToastKind.Warn); return; }
        var set = new HashSet<int>();
        foreach (var part in ScanPages.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var ab = part.Split('-'); if (!int.TryParse(ab[0], out var a)) continue;
            var b = ab.Length > 1 && int.TryParse(ab[1], out var bb) ? bb : a;
            for (var i = a; i <= b; i++) set.Add(i);
        }
        var inv = int.TryParse(InvoiceNo, out var n) ? n : 0;
        try
        {
            PreviewText = "READING THE SCAN (offline OCR)...";
            var opts = _reading.Options(new Progress<string>(s => PreviewText = "READING  " + s));
            var o2 = new Raffaello.Core.Documents.Smart.SmartReaderOptions { Engines = opts.Engines, Rasterizer = opts.Rasterizer, Vision = opts.Vision, Progress = opts.Progress, Pages = set.Count == 0 ? null : set.Contains };
            var doc = await Task.Run(() => Raffaello.Core.Documents.Smart.SmartReader.ReadAsync(path, o2));
            var summary = doc.Pages.Where(p => p.Kind.Type == Raffaello.Core.Documents.Smart.DocTypes.SiteStatement).ToList();
            if (summary.Count == 0 && doc.Pages.Count > 0) summary.Add(doc.Pages[0]);
            var drawings = doc.Pages.Where(p => p.Kind.Type == Raffaello.Core.Documents.Smart.DocTypes.MarkedDrawing).ToList();
            var draft = Raffaello.Core.Documents.Smart.SiteStatementExtractor.Read(summary, drawings);
            var no = StatementNo.Trim().Length > 0 ? StatementNo.Trim() : draft.StatementNo.Length > 0 ? draft.StatementNo : "SCAN-" + DateTime.Now.ToString("yyyyMMdd");
            var res = Raffaello.Core.Documents.Smart.StatementDraftConverter.ToImport(draft, Project.Snapshot, Sub.Trim(), no, inv, WorkingBuilding);
            _preview = res;
            PreviewFile = path;
            HasPreview = true;
            PreviewText = $"DRAFT FROM SCAN: {draft.Rows.Count} row(s), {draft.Drawings.Count} marked drawing(s) - {res.Summary}. Check every line against the scan before posting.";
            Preview.Clear();
            foreach (var (line, check) in res.Checks) Preview.Add(new StatementPreviewRow { Line = line, Check = check });
            Issues.Clear();
            foreach (var i in draft.Issues) Issues.Add($"{i.Level.ToString().ToUpperInvariant()}  {i.Message}");
            foreach (var i in res.Issues) Issues.Add($"{i.Level.ToString().ToUpperInvariant()}  {i.Message}");
            Raffaello.Core.Wiring.StatementPreviewChecks.RuleWarnings(res, Project.Snapshot, _reading.Documents);
            FillWarnings();
            foreach (var r in draft.Rows) Issues.Add($"ROW {r.No}: {r.Stage} {r.UnitType} {string.Join("+", r.Systems)} {(r.Pct is double pc ? pc.ToString("P0") : "")} rooms {string.Join(", ", r.Rooms)}  <- \"{r.Raw}\" (conf {r.Confidence:0.00})");
            Raffaello.Core.Documents.DocArchive.Save(_reading.Documents, doc.ToDocText(), path, "SITE STATEMENT", nameof(Raffaello.Core.Domain.SiteStatement), no);
        }
        catch (Exception ex) { PreviewText = "No statement opened"; Ctx.Toasts.Show("CANNOT READ SCAN", ex.Message, ToastKind.Error); }
    }

    public override string Key => "SiteStatements";
    public override string Title => "SITE STATEMENTS";
    public override string Subtitle => "Issue a statement sheet per subcontractor, import it back into the room ledger (duplicates and over-remaining are caught)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => true;   // [phase6] the building switcher drives it

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

    // ---- warnings of the preview (cable flags + contract rules), bypassed with a reason right here
    public ObservableCollection<PreviewWarningRow> Warnings { get; } = new();
    [ObservableProperty] private PreviewWarningRow? _selectedWarning;
    [ObservableProperty] private string _bypassReason = "";
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string _warningsText = "";

    private void FillWarnings()
    {
        Warnings.Clear();
        if (_preview != null)
        {
            foreach (var f in _preview.CableFlags) Warnings.Add(new PreviewWarningRow { Cable = f });
            foreach (var w in _preview.RuleWarnings) Warnings.Add(new PreviewWarningRow { Rule = w });
        }
        HasWarnings = Warnings.Count > 0;
        var open = Warnings.Count(w => !w.IsBypassed);
        WarningsText = $"WARNINGS  {open} open, {Warnings.Count - open} bypassed  -  cable flags and contract rules never block; bypass them with a reason (recorded with your name)";
    }

    private async Task BypassRows(List<PreviewWarningRow> rows)
    {
        if (_preview is null || rows.Count == 0) return;
        var reason = BypassReason.Trim();
        if (reason.Length < 3) { Ctx.Toasts.Show("REASON NEEDED", "Type a short reason for the bypass (who agreed, why).", ToastKind.Warn); return; }
        var res = _preview;
        var docs = _reading.Documents;
        try
        {
            var n = await Task.Run(() =>
            {
                var c = Raffaello.Core.Wiring.StatementPreviewChecks.BypassCableFlags(Project.Store, rows.Where(r => r.Cable != null).Select(r => r.Cable!), reason);
                c += Raffaello.Core.Wiring.StatementPreviewChecks.BypassRuleWarnings(docs, rows.Where(r => r.Rule != null).Select(r => r.Rule!), reason, DateTime.Now);
                Raffaello.Core.Wiring.StatementPreviewChecks.Refresh(res, Project.Store, Project.Snapshot, docs);
                return c;
            });
            BypassReason = "";
            FillWarnings();
            Ctx.Toasts.Show("BYPASS RECORDED", $"{n} warning(s): {reason}", ToastKind.Good);
        }
        catch (Exception ex) { Ctx.Toasts.Show("BYPASS NOT SAVED", ex.Message, ToastKind.Error); }
    }

    [RelayCommand]
    private Task BypassWarning()
    {
        if (SelectedWarning is not { } w) { Ctx.Toasts.Show("PICK A WARNING FIRST", kind: ToastKind.Warn); return Task.CompletedTask; }
        if (w.IsBypassed) { Ctx.Toasts.Show("ALREADY BYPASSED", w.Decision); return Task.CompletedTask; }
        return BypassRows(new List<PreviewWarningRow> { w });
    }

    [RelayCommand]
    private Task BypassAllWarnings()
    {
        var open = Warnings.Where(w => !w.IsBypassed).ToList();
        if (open.Count == 0) return Task.CompletedTask;
        if (BypassReason.Trim().Length >= 3 && !Ctx.Dialogs.Confirm("Bypass warnings", $"Let {open.Count} warning(s) through with the reason:\n\n{BypassReason.Trim()}")) return Task.CompletedTask;
        return BypassRows(open);
    }

    protected override void Refresh()
    {
        var s = Project.Snapshot;
        var subs = s.Claims.Select(c => c.Subcontractor).Concat(s.Contracts.Select(c => c.Subcontractor)).Where(x => x.Length > 0).Distinct().OrderBy(x => x).ToList();
        // Clearing the list makes the bound ComboBox write Sub = null; keep the user's pick across the refill.
        var keepSub = Sub;
        if (!Subcontractors.SequenceEqual(subs)) { Subcontractors.Clear(); foreach (var x in subs) Subcontractors.Add(x); }
        Sub = !string.IsNullOrEmpty(keepSub) && Subcontractors.Contains(keepSub) ? keepSub : Subcontractors.FirstOrDefault() ?? "";
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
        if (await Ctx.Data.WriteAsync(p => rooms = p.Workflow.GenerateStatement(path, sub, no, WorkingBuilding, stages, systems), Ctx.Toasts))
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
            var res = await Task.Run(() => Project.Workflow.PreviewStatement(path, inv, WorkingBuilding));
            _preview = res;
            PreviewFile = path;
            HasPreview = true;
            PreviewText = res.Summary;
            Preview.Clear();
            foreach (var (line, check) in res.Checks) Preview.Add(new StatementPreviewRow { Line = line, Check = check });
            Issues.Clear();
            foreach (var i in res.Issues) Issues.Add($"{i.Level.ToString().ToUpperInvariant()}  row {i.Row}: {i.Message}");
            FillWarnings();
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
            FillWarnings();
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
