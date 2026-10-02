using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Queue;

namespace Raffaello.App.ViewModels;

// =====================================================================================================
//  [phase4] ACONEX hub: WORKFLOWS / STATUS BOARD / DOWNLOADS / SCRIPT RUNNER / SETUP
// =====================================================================================================

public sealed partial class AconexHubViewModel : PageViewModel
{
    public const string TabWorkflows = "WORKFLOWS";
    public const string TabBoard = "STATUS BOARD";
    public const string TabDownloads = "DOWNLOADS";
    public const string TabScripts = "SCRIPT RUNNER";
    public const string TabSetup = "SETUP";

    private readonly DispatcherTimer _daily = new() { Interval = TimeSpan.FromMinutes(1) };

    public AconexHubViewModel(PageContext ctx, AconexAutomationService automation, AconexViewModel scripts) : base(ctx)
    {
        Automation = automation;
        Scripts = scripts;
        Lookup = new AconexLookupViewModel(this);
        Board = new AconexBoardViewModel(this);
        Downloads = new AconexDownloadsViewModel(this);
        Setup = new AconexSetupViewModel(this);
        automation.Log += line => Application.Current?.Dispatcher.BeginInvoke(() => AddLog(line));
        _daily.Tick += async (_, _) => await DailyTickAsync();
        _daily.Start();
    }

    public override string Key => "Aconex";
    public override string Title => "ACONEX";
    public override string Subtitle => "Invoice workflows, status board for management, WIR / MIR downloads (browser automation of ksa1.aconex.com)";
    public override bool ShowFilterBar => false;
    protected override bool UsesFilter => false;

    internal PageContext C => Ctx;
    public AconexAutomationService Automation { get; }
    public AconexViewModel Scripts { get; }
    public AconexLookupViewModel Lookup { get; }
    public AconexBoardViewModel Board { get; }
    public AconexDownloadsViewModel Downloads { get; }
    public AconexSetupViewModel Setup { get; }
    public string[] Tabs { get; } = { TabBoard, TabWorkflows, TabDownloads, TabScripts, TabSetup };
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private string _tab = TabBoard;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    public bool IsWorkflows => Tab == TabWorkflows;
    public bool IsBoard => Tab == TabBoard;
    public bool IsDownloads => Tab == TabDownloads;
    public bool IsScripts => Tab == TabScripts;
    public bool IsSetup => Tab == TabSetup;

    partial void OnTabChanged(string value)
    {
        foreach (var p in new[] { nameof(IsWorkflows), nameof(IsBoard), nameof(IsDownloads), nameof(IsScripts), nameof(IsSetup) }) OnPropertyChanged(p);
        if (IsActive) RefreshTab();
    }

    internal void AddLogFromAnyThread(string line) => Application.Current?.Dispatcher.BeginInvoke(() => AddLog(line));

    internal void AddLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 1000) Log.RemoveAt(0);
    }

    protected override void Refresh() => RefreshTab();

    private void RefreshTab()
    {
        switch (Tab)
        {
            case TabWorkflows: Lookup.Refresh(); break;
            case TabDownloads: Downloads.Refresh(); break;
            case TabScripts: Scripts.Activate(null); break;
            case TabSetup: Setup.Refresh(); break;
            default: Board.Refresh(); break;
        }
    }

    protected override void NavigateTo(NavTarget target)
    {
        if (target.Key is { Length: > 0 } wf)
        {
            Tab = TabWorkflows;
            Lookup.WorkflowNo = wf;
            Lookup.ShowLatest();
        }
    }

    /// <summary>Runs browser work with a busy flag, errors as toasts. Returns default on failure.</summary>
    internal async Task<T?> RunBrowserAsync<T>(string busy, Func<Core.AconexWeb.IAconexClient, Task<T>> work, bool visible = false, CancellationToken ct = default)
    {
        if (Automation.IsBusy) { Ctx.Toasts.Show("ACONEX IS BUSY", "Another lookup or download is running.", ToastKind.Warn); return default; }
        IsBusy = true;
        BusyText = busy;
        AddLog(busy);
        try { return await Automation.RunAsync(work, visible, ct); }
        catch (OperationCanceledException) { AddLog("Stopped."); Ctx.Toasts.Show("STOPPED", busy); return default; }
        catch (AconexLoginRequiredException ex) { AddLog(ex.Message); Ctx.Toasts.Show("ACONEX LOGIN NEEDED", ex.Message, ToastKind.Warn, 10); return default; }
        catch (Exception ex)
        {
            AddLog("ERROR " + ex.Message);
            var hint = ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("channel", StringComparison.OrdinalIgnoreCase)
                ? " - check BrowserChannel (msedge) / BrowserExecutablePath in SETUP." : "";
            Ctx.Toasts.Show("ACONEX FAILED", ex.Message.Split('\n')[0] + hint, ToastKind.Error, 10);
            return default;
        }
        finally { IsBusy = false; BusyText = ""; }
    }

    // ------------------------------------------------------------------ daily auto refresh (while the app runs)

    private const string LastAutoRefreshKey = "Phase4.AconexLastAutoRefresh";

    private async Task DailyTickAsync()
    {
        if (DailySchedule.ParseTime(Automation.Config.DailyRefreshTime) is not { } at || Automation.IsBusy) return;
        DateTime? last = null;
        try
        {
            if (DateTime.TryParse(Project.Store.GetMeta(LastAutoRefreshKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var l)) last = l;
        }
        catch (Exception ex) { AddLog("auto refresh: " + ex.Message); return; }
        if (!DailySchedule.IsDue(DateTime.Now, at, last)) return;
        // claim the slot first so a second PC on the same data file does not run it too
        Project.Store.SetMeta(LastAutoRefreshKey, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        AddLog($"Daily auto refresh ({Automation.Config.DailyRefreshTime})");
        await Board.RefreshAllCoreAsync(auto: true);
    }
}

/// <summary>An invoice revision in pickers.</summary>
public sealed record InvoiceChoice(long Id, string Title, string Status, string WorkflowNo)
{
    public override string ToString() => $"{Title}  [{Status}]" + (WorkflowNo.Length > 0 ? $"  {WorkflowNo}" : "");
}

// ------------------------------------------------------------------ WORKFLOWS tab

public sealed partial class AconexLookupViewModel : ObservableObject
{
    private readonly AconexHubViewModel _hub;
    public AconexLookupViewModel(AconexHubViewModel hub) => _hub = hub;

    public ObservableCollection<InvoiceChoice> Invoices { get; } = new();
    public ObservableCollection<WorkflowStep> Steps { get; } = new();
    public ObservableCollection<AconexStepChange> History { get; } = new();
    public ObservableCollection<AconexWorkflowCheck> Checks { get; } = new();
    public ObservableCollection<InvoiceAttachment> Attachments { get; } = new();

    [ObservableProperty] private string _workflowNo = "";
    [ObservableProperty] private InvoiceChoice? _selectedInvoice;
    [ObservableProperty] private AconexWorkflowCheck? _selectedCheck;
    [ObservableProperty] private string _heading = "TYPE A WORKFLOW NUMBER (WF-...) AND PRESS LOOK UP";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _stateTag = "";
    [ObservableProperty] private string _currentStep = "";
    [ObservableProperty] private string _withWhom = "";
    [ObservableProperty] private string _due = "";
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private string _checkedAt = "";
    [ObservableProperty] private string _screenshot = "";
    [ObservableProperty] private string _error = "";

    partial void OnSelectedInvoiceChanged(InvoiceChoice? value)
    {
        if (value is null) return;
        if (value.WorkflowNo.Length > 0) WorkflowNo = value.WorkflowNo;
        LoadAttachments();
        ShowLatest();
    }

    partial void OnSelectedCheckChanged(AconexWorkflowCheck? value) { if (value != null) Show(value); }

    public void Refresh()
    {
        var keep = SelectedInvoice?.Id;
        var links = SafeLinks();
        Invoices.Clear();
        foreach (var i in _hub.Project.Snapshot.SubInvoices.OrderBy(i => i.Subcontractor).ThenBy(i => i.InvoiceNo).ThenBy(i => i.Revision))
            Invoices.Add(new InvoiceChoice(i.Id, i.Title, i.Status, links.GetValueOrDefault(i.Id) ?? i.AconexWorkflowNo ?? ""));
        if (keep is { } k) SelectedInvoice = Invoices.FirstOrDefault(i => i.Id == k);
        if (WorkflowNo.Length > 0) ShowLatest();
    }

    private Dictionary<long, string> SafeLinks()
    {
        try { return _hub.Automation.Store.ActiveLinks().GroupBy(l => l.SubInvoiceId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.LinkedAt).First().WorkflowNo); }
        catch (Exception ex) { _hub.AddLog("links: " + ex.Message); return new(); }
    }

    private void LoadAttachments()
    {
        Attachments.Clear();
        if (SelectedInvoice is null) return;
        foreach (var a in _hub.Automation.Store.Attachments(SelectedInvoice.Id)) Attachments.Add(a);
    }

    public void ShowLatest()
    {
        if (string.IsNullOrWhiteSpace(WorkflowNo)) return;
        var store = _hub.Automation.Store;
        Checks.Clear();
        foreach (var c in store.Checks(WorkflowNo)) Checks.Add(c);
        History.Clear();
        foreach (var h in store.History(WorkflowNo)) History.Add(h);
        if (Checks.Count > 0) Show(Checks[0]);
        else { Steps.Clear(); Heading = $"{WorkflowNo.ToUpperInvariant()} - NOT CHECKED YET"; State = ""; StateTag = ""; CurrentStep = WithWhom = Due = Outcome = CheckedAt = Screenshot = Error = ""; }
    }

    private void Show(AconexWorkflowCheck c)
    {
        Heading = $"{c.WorkflowNo}  {c.WorkflowName}".Trim();
        State = c.State;
        StateTag = new StatusBoardRow { State = c.State, WorkflowNo = c.WorkflowNo }.Tag;
        CurrentStep = c.CurrentStep;
        WithWhom = c.WithWhom;
        Due = c.DateDue is { } d ? d.ToString("dd-MMM-yyyy") + (c.IsOverdue ? $"  ({c.DaysOverdue} DAYS OVERDUE)" : "") : "";
        Outcome = c.Outcome;
        CheckedAt = c.CheckedAt.ToString("dd-MMM-yyyy HH:mm");
        Screenshot = File.Exists(c.TableScreenshot) ? c.TableScreenshot : File.Exists(c.PageScreenshot) ? c.PageScreenshot : "";
        Error = c.Error;
        Steps.Clear();
        foreach (var s in StepJson.Deserialize(c.StepsJson)) Steps.Add(s);
    }

    [RelayCommand]
    private async Task LookUp()
    {
        var wf = WorkflowNo.Trim().ToUpperInvariant();
        if (wf.Length == 0) { _hub.C.Toasts.Show("TYPE A WORKFLOW NUMBER", kind: ToastKind.Warn); return; }
        var inv = SelectedInvoice?.WorkflowNo is { Length: > 0 } iw && WorkflowParser.NormalizeWf(iw) == WorkflowParser.NormalizeWf(wf) ? SelectedInvoice : null;
        var cfg = _hub.Automation.Config;
        var r = await _hub.RunBrowserAsync($"Looking up {wf}...", c => new WorkflowTracker(c, _hub.Automation.Store, cfg).LookupAsync(wf, inv?.Id));
        if (r is null) return;
        WorkflowNo = wf;
        ShowLatest();
        LoadAttachments();
        _hub.C.Toasts.Show(r.State, r.Summary, r.State is WorkflowStates.Error or WorkflowStates.NotFound ? ToastKind.Warn : r.IsOverdue ? ToastKind.Warn : ToastKind.Good, 8);
        await OfferOutcomesAsync(_hub, new[] { r });
    }

    /// <summary>[phase6] Approved / rejected in Aconex: ask (never silently) whether to record it on the linked invoice revision.</summary>
    internal static async Task OfferOutcomesAsync(AconexHubViewModel hub, IEnumerable<WorkflowLookupResult> results)
    {
        foreach (var r in results)
        {
            OutcomeProposal? p;
            try { p = AconexOutcomes.Propose(r, hub.Automation.Store.ActiveLinks(), hub.Project.Snapshot.SubInvoices); }
            catch (Exception) { continue; }
            if (p is null || !hub.C.Dialogs.Confirm(p.Action == OutcomeProposal.Approve ? "Aconex: approved" : "Aconex: rejected", p.Question)) continue;
            string msg = "";
            if (await hub.C.Data.WriteAsync(x => msg = x.Workflow.ApplyAconexOutcome(p), hub.C.Toasts))
                hub.C.Toasts.Show(p.Action == OutcomeProposal.Approve ? "INVOICE APPROVED" : "INVOICE REJECTED", msg, ToastKind.Good, 8);
        }
    }

    [RelayCommand]
    private async Task LinkToInvoice()
    {
        if (SelectedInvoice is null || string.IsNullOrWhiteSpace(WorkflowNo)) { _hub.C.Toasts.Show("PICK AN INVOICE REVISION AND A WORKFLOW", kind: ToastKind.Warn); return; }
        var inv = SelectedInvoice;
        var wf = WorkflowNo.Trim().ToUpperInvariant();
        var ok = await _hub.C.Data.WriteAsync(p =>
        {
            _hub.Automation.Store.Link(inv.Id, wf);
            // keep the number on the revision too when it is still empty (the Invoices screen shows it)
            var sub = p.Store.Get<SubInvoice>(inv.Id);
            if (sub is { Locked: false } && string.IsNullOrWhiteSpace(sub.AconexWorkflowNo))
            {
                sub.AconexWorkflowNo = wf;
                p.Store.Update(sub, $"{sub.Title}: Aconex workflow {wf}");
            }
        }, _hub.C.Toasts, $"{inv.Title} linked to {wf}");
        if (ok) Refresh();
    }

    [RelayCommand] private void OpenScreenshot() { if (Screenshot.Length > 0) DialogService.OpenWithShell(Screenshot); }
    [RelayCommand] private void OpenAttachment(InvoiceAttachment? a) { if (a != null && File.Exists(a.Path)) DialogService.OpenWithShell(a.Path); }
    [RelayCommand] private void GoToInvoices() => _hub.C.Nav.Go("Invoices");

    [RelayCommand]
    private void Export()
    {
        if (Steps.Count == 0) { _hub.C.Toasts.Show("NOTHING TO EXPORT", "Look the workflow up first."); return; }
        _hub.C.Exports.Export($"ACONEX_{DocumentRegister.Safe(WorkflowNo)}", new[] { StatusBoard.StepsSheet(WorkflowNo, Steps), StatusBoard.HistorySheet(History) });
    }
}

// ------------------------------------------------------------------ STATUS BOARD tab

public sealed partial class AconexBoardViewModel : ObservableObject
{
    private readonly AconexHubViewModel _hub;
    private CancellationTokenSource? _cts;
    public AconexBoardViewModel(AconexHubViewModel hub) => _hub = hub;

    public ObservableCollection<StatusBoardRow> Rows { get; } = new();
    public ObservableCollection<AconexStepChange> History { get; } = new();

    [ObservableProperty] private bool _includeClosed;
    [ObservableProperty] private StatusBoardRow? _selected;
    [ObservableProperty] private string _openCount = "0";
    [ObservableProperty] private string _overdueCount = "0";
    [ObservableProperty] private string _rejectedCount = "0";
    [ObservableProperty] private string _notCheckedCount = "0";
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _autoRefreshTime = "";
    [ObservableProperty] private bool _pdfWithScreenshots = true;

    partial void OnIncludeClosedChanged(bool value) => Refresh();

    public void Refresh()
    {
        AutoRefreshTime = _hub.Automation.Config.DailyRefreshTime;
        List<StatusBoardRow> rows;
        try
        {
            var store = _hub.Automation.Store;
            rows = StatusBoard.Build(_hub.Project.Snapshot.SubInvoices, store.ActiveLinks(), store.LatestChecks(), DateTime.Today, IncludeClosed);
            History.Clear();
            foreach (var h in store.History(take: 200)) History.Add(h);
        }
        catch (Exception ex) { _hub.C.Toasts.Show("STATUS BOARD", ex.Message, ToastKind.Error); return; }
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OpenCount = rows.Count(r => r.InvoiceStatus != SubInvoiceStatus.Approved).ToString("N0");
        OverdueCount = rows.Count(r => r.IsOverdue).ToString("N0");
        RejectedCount = rows.Count(r => r.State == WorkflowStates.Rejected).ToString("N0");
        NotCheckedCount = rows.Count(r => r.State == WorkflowStates.NotChecked).ToString("N0");
    }

    [RelayCommand] private Task RefreshAll() => RefreshAllCoreAsync(auto: false);

    internal async Task RefreshAllCoreAsync(bool auto)
    {
        _cts = new CancellationTokenSource();
        var invoices = _hub.Project.Snapshot.SubInvoices.ToList();
        var cfg = _hub.Automation.Config;
        var progress = new Progress<(int Done, int Total, string Wf)>(p => Progress = p.Wf.Length > 0 ? $"{p.Done + 1} / {p.Total}  {p.Wf}" : $"{p.Done} / {p.Total} done");
        var results = await _hub.RunBrowserAsync("Refreshing all invoice workflows...", c => new WorkflowTracker(c, _hub.Automation.Store, cfg).RefreshAllAsync(invoices, progress, _cts.Token), ct: _cts.Token);
        Progress = "";
        Refresh();
        if (results is null) return;
        var overdue = results.Count(r => r.IsOverdue);
        _hub.C.Toasts.Show(auto ? "DAILY ACONEX REFRESH" : "STATUS BOARD REFRESHED", $"{results.Count} workflows checked, {overdue} overdue, {results.Count(r => r.State == WorkflowStates.Error)} errors",
            overdue > 0 ? ToastKind.Warn : ToastKind.Good, 8);
        await AconexLookupViewModel.OfferOutcomesAsync(_hub, results);
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void SaveAutoRefresh()
    {
        var t = AutoRefreshTime.Trim();
        if (t.Length > 0 && DailySchedule.ParseTime(t) is null) { _hub.C.Toasts.Show("TIME AS HH:MM", "e.g. 08:30, or empty to switch it off", ToastKind.Warn); return; }
        _hub.Automation.Config.DailyRefreshTime = t;
        _hub.Automation.SaveConfig();
        _hub.C.Toasts.Show(t.Length == 0 ? "DAILY REFRESH OFF" : $"DAILY REFRESH AT {t}", "Runs while the app is open (one PC per day).", ToastKind.Good);
    }

    [RelayCommand]
    private void OpenRow(StatusBoardRow? row)
    {
        row ??= Selected;
        if (row is null || row.WorkflowNo.Length == 0) return;
        _hub.Tab = AconexHubViewModel.TabWorkflows;
        _hub.Lookup.SelectedInvoice = _hub.Lookup.Invoices.FirstOrDefault(i => i.Id == row.SubInvoiceId);
        _hub.Lookup.WorkflowNo = row.WorkflowNo;
        _hub.Lookup.ShowLatest();
    }

    private IEnumerable<ExportSheet> Sheets() => new[] { StatusBoard.ToSheet(Rows.ToList(), DateTime.Now), StatusBoard.HistorySheet(History) };

    [RelayCommand] private void ExportExcel() => _hub.C.Exports.Export("ACONEX_INVOICE_STATUS", Sheets());

    [RelayCommand]
    private void ExportPdf()
    {
        var path = _hub.C.Dialogs.SaveFile("Invoice status (PDF)", $"ACONEX_INVOICE_STATUS_{DateTime.Now:yyyyMMdd_HHmm}.pdf", "PDF|*.pdf");
        if (path is null) return;
        try
        {
            var imgs = PdfWithScreenshots
                ? Rows.Where(r => r.Screenshot.Length > 0).Select(r => new PdfTables.PdfImage($"{r.Invoice} - {r.WorkflowNo} ({r.LastChecked:dd-MMM-yyyy HH:mm})", r.Screenshot))
                : Enumerable.Empty<PdfTables.PdfImage>();
            PdfTables.Export(path, $"{_hub.Project.Settings.Project}  |  printed {DateTime.Now:dd-MMM-yyyy HH:mm} by {_hub.Project.Settings.EffectiveUserName}",
                new[] { StatusBoard.ToSheet(Rows.ToList(), DateTime.Now) }, imgs);
            _hub.C.Toasts.Show("PDF SAVED", Path.GetFileName(path), ToastKind.Good);
            DialogService.OpenWithShell(path);
        }
        catch (IOException ex) { _hub.C.Toasts.Show("PDF FAILED", ex.Message, ToastKind.Error); }
    }
}

// ------------------------------------------------------------------ DOWNLOADS tab

public sealed partial class AconexDownloadsViewModel : ObservableObject
{
    private readonly AconexHubViewModel _hub;
    private CancellationTokenSource? _cts;
    public AconexDownloadsViewModel(AconexHubViewModel hub) => _hub = hub;

    public ObservableCollection<AconexDownloadJob> Jobs { get; } = new();
    public ObservableCollection<AconexQueueItem> Queue { get; } = new();
    public ObservableCollection<AconexDownload> Register { get; } = new();
    public string[] DocTypes { get; } = { "", "WIR", "MIR" };

    [ObservableProperty] private string _numbersText = "";
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private string _discipline = "";
    [ObservableProperty] private string _docType = "WIR";
    [ObservableProperty] private AconexDownloadJob? _selectedJob;
    [ObservableProperty] private AconexDownload? _selectedFile;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _numbersInfo = "";
    [ObservableProperty] private string _folders = "";

    partial void OnSelectedJobChanged(AconexDownloadJob? value) => LoadQueue();
    partial void OnSearchChanged(string value) => LoadRegister();
    partial void OnNumbersTextChanged(string value)
    {
        var n = DocumentRegister.ParseNumberList(value, _hub.Automation.Config.Documents.DocNumberRegex).Count;
        NumbersInfo = n == 0 ? "" : $"{n} DOCUMENT NUMBERS";
    }

    public void Refresh()
    {
        var f = _hub.Automation.Config.Folders;
        Folders = $"WIR -> {Show(f.WirFolder)}   |   MIR -> {Show(f.MirFolder)}   |   OTHER -> {Show(f.OtherFolder)}";
        var keep = SelectedJob?.Id;
        Jobs.Clear();
        try { foreach (var j in _hub.Automation.Store.Jobs()) Jobs.Add(j); }
        catch (Exception ex) { _hub.C.Toasts.Show("DOWNLOADS", ex.Message, ToastKind.Error); return; }
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == keep) ?? Jobs.FirstOrDefault();
        LoadRegister();
    }

    private static string Show(string p) => string.IsNullOrWhiteSpace(p) ? "(not set - SETUP)" : p;

    private void LoadQueue()
    {
        Queue.Clear();
        if (SelectedJob is null) return;
        foreach (var q in _hub.Automation.Store.Queue(SelectedJob.Id)) Queue.Add(q);
    }

    private void LoadRegister()
    {
        Register.Clear();
        var all = _hub.Automation.Store.Downloads().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Search))
            all = all.Where(d => d.DocumentNo.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || d.FileName.Contains(Search, StringComparison.OrdinalIgnoreCase));
        foreach (var d in all.Take(2000)) Register.Add(d);
    }

    [RelayCommand]
    private void LoadNumbersFromExcel()
    {
        var f = _hub.C.Dialogs.OpenFile("Excel / CSV with document numbers");
        if (f is null) return;
        try
        {
            var nums = DocumentRegister.ReadNumbersFromFile(f, _hub.Automation.Config.Documents.DocNumberRegex);
            NumbersText = string.Join(Environment.NewLine, nums);
            _hub.C.Toasts.Show("NUMBERS LOADED", $"{nums.Count} from {Path.GetFileName(f)}", ToastKind.Good);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { _hub.C.Toasts.Show("COULD NOT READ", ex.Message, ToastKind.Error); }
    }

    private DocumentQuery Query() => new()
    {
        DocumentNumbers = DocumentRegister.ParseNumberList(NumbersText, _hub.Automation.Config.Documents.DocNumberRegex),
        DateFrom = DateFrom, DateTo = DateTo, Group = Group.Trim(), Discipline = Discipline.Trim(), DocType = DocType.Trim(),
    };

    [RelayCommand]
    private async Task Plan()
    {
        var q = Query();
        if (q.IsEmpty) { _hub.C.Toasts.Show("NOTHING TO SEARCH", "Paste numbers or set a date range / group / discipline / type.", ToastKind.Warn); return; }
        var cfg = _hub.Automation.Config;
        var job = await _hub.RunBrowserAsync($"Searching the register ({q.Describe()})...", async c =>
        {
            var svc = new DocumentDownloadService(c, _hub.Automation.Store, cfg);
            svc.Log += _hub.AddLogFromAnyThread;
            return await svc.PlanAsync(q);
        });
        if (job is null) return;
        Refresh();
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == job.Id);
        _hub.C.Toasts.Show("QUEUE READY", $"{job.Total} revisions, {job.Skipped} already downloaded. Press DOWNLOAD.", ToastKind.Good, 8);
    }

    [RelayCommand]
    private async Task Run()
    {
        var job = SelectedJob ?? _hub.Automation.Store.ResumableJob();
        if (job is null) { _hub.C.Toasts.Show("NO QUEUE", "SEARCH first to build a download queue.", ToastKind.Warn); return; }
        _cts = new CancellationTokenSource();
        var cfg = _hub.Automation.Config;
        ProgressMax = Math.Max(1, job.Total);
        var progress = new Progress<(int Done, int Total, string Doc)>(p =>
        {
            ProgressMax = Math.Max(1, p.Total);
            ProgressValue = p.Done;
            ProgressText = p.Doc.Length > 0 ? $"{p.Done} / {p.Total}  {p.Doc}" : $"{p.Done} / {p.Total}";
            LoadQueue();
        });
        var done = await _hub.RunBrowserAsync($"Downloading job #{job.Id}...", async c =>
        {
            var svc = new DocumentDownloadService(c, _hub.Automation.Store, cfg);
            svc.Log += _hub.AddLogFromAnyThread;
            return await svc.RunAsync(job.Id, progress, ct: _cts.Token);
        }, ct: _cts.Token);
        Refresh();
        if (done != null)
            _hub.C.Toasts.Show($"JOB #{done.Id} {done.State}", $"{done.Done} downloaded, {done.Skipped} skipped, {done.Failed} failed", done.Failed > 0 ? ToastKind.Warn : ToastKind.Good, 8);
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand] private void OpenFile() { if (SelectedFile is { } f && File.Exists(f.Path)) DialogService.OpenWithShell(f.Path); }
    [RelayCommand] private void OpenFolder() { if (SelectedFile is { } f && Path.GetDirectoryName(f.Path) is { } d && Directory.Exists(d)) DialogService.OpenWithShell(d); }

    [RelayCommand]
    private void ExportRegister() => _hub.C.Exports.Export("ACONEX_DOWNLOADS", new[]
    {
        new ExportSheet
        {
            Name = "DOWNLOADS", Title = "ACONEX DOWNLOAD REGISTER",
            Columns = new() { new("DOCUMENT NO", Width: 26), new("REV"), new("TYPE"), new("TITLE", Width: 50), new("DOC DATE", ColumnKind.Date), new("FILE", Width: 40), new("PATH", Width: 60), new("SHA-256", Width: 30), new("DOWNLOADED", ColumnKind.Date) },
            Rows = Register.Select(d => new object?[] { d.DocumentNo, d.Revision, d.Kind, d.Title, d.DocDate, d.FileName, d.Path, d.Sha256, d.DownloadedAt }).ToList(),
        },
    });
}

// ------------------------------------------------------------------ SETUP tab

public sealed partial class AconexSetupViewModel : ObservableObject
{
    private readonly AconexHubViewModel _hub;
    public AconexSetupViewModel(AconexHubViewModel hub) => _hub = hub;

    [ObservableProperty] private string _configPath = "";
    [ObservableProperty] private string _validation = "";
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _projectId = "";
    [ObservableProperty] private bool _headless;
    [ObservableProperty] private string _browserChannel = "";
    [ObservableProperty] private string _browserExecutablePath = "";
    [ObservableProperty] private string _profileDir = "";
    [ObservableProperty] private string _wirFolder = "";
    [ObservableProperty] private string _mirFolder = "";
    [ObservableProperty] private string _otherFolder = "";
    [ObservableProperty] private string _screenshotFolder = "";
    [ObservableProperty] private string _subFolderTemplate = "";
    [ObservableProperty] private string _credUser = "";
    [ObservableProperty] private string _credStatus = "";

    public void Refresh()
    {
        var a = _hub.Automation;
        var c = a.Config;
        ConfigPath = a.ConfigPath;
        BaseUrl = c.BaseUrl; ProjectId = c.ProjectId; Headless = c.Headless; BrowserChannel = c.BrowserChannel; BrowserExecutablePath = c.BrowserExecutablePath;
        ProfileDir = c.ResolvedProfileDir;
        WirFolder = c.Folders.WirFolder; MirFolder = c.Folders.MirFolder; OtherFolder = c.Folders.OtherFolder; ScreenshotFolder = c.Folders.ScreenshotFolder; SubFolderTemplate = c.Folders.SubFolderTemplate;
        var problems = c.Validate();
        Validation = a.ConfigError.Length > 0 ? "CONFIG ERROR: " + a.ConfigError : problems.Count == 0 ? "CONFIG OK - selectors and columns still need checking on the real site (README)." : string.Join("  |  ", problems);
        CredStatus = !a.Vault.IsSupported ? "Password storage needs Windows (DPAPI). Log in by hand in the browser window."
            : a.Vault.HasCredential ? "A password is stored (encrypted with DPAPI for your Windows user)." : "No password stored - you log in by hand once; the browser profile keeps the session.";
        if (a.Vault.Load() is { } cred) CredUser = cred.User;
    }

    [RelayCommand]
    private void Save()
    {
        var c = _hub.Automation.Config;
        c.BaseUrl = BaseUrl.Trim().TrimEnd('/'); c.ProjectId = ProjectId.Trim(); c.Headless = Headless; c.BrowserChannel = BrowserChannel.Trim(); c.BrowserExecutablePath = BrowserExecutablePath.Trim();
        c.Folders.WirFolder = WirFolder.Trim(); c.Folders.MirFolder = MirFolder.Trim(); c.Folders.OtherFolder = OtherFolder.Trim(); c.Folders.ScreenshotFolder = ScreenshotFolder.Trim();
        c.Folders.SubFolderTemplate = SubFolderTemplate.Trim();
        try
        {
            _hub.Automation.SaveConfig();
            _ = _hub.Automation.CloseBrowserAsync(); // next run uses the new settings
            _hub.C.Toasts.Show("ACONEX SETUP SAVED", ConfigPath, ToastKind.Good);
        }
        catch (IOException ex) { _hub.C.Toasts.Show("SAVE FAILED", ex.Message, ToastKind.Error); }
        Refresh();
    }

    [RelayCommand]
    private void Reload()
    {
        var msg = _hub.Automation.ReloadConfig();
        _ = _hub.Automation.CloseBrowserAsync();
        Refresh();
        _hub.C.Toasts.Show(msg.Length == 0 ? "CONFIG RELOADED" : "CONFIG PROBLEM", msg, msg.Length == 0 ? ToastKind.Good : ToastKind.Warn, 8);
    }

    [RelayCommand] private void OpenConfig() { if (!File.Exists(ConfigPath)) _hub.Automation.SaveConfig(); DialogService.OpenWithShell(ConfigPath); }

    [RelayCommand] private void BrowseWir() { if (_hub.C.Dialogs.PickFolder("WIR download folder (shared partition)") is { } f) WirFolder = f; }
    [RelayCommand] private void BrowseMir() { if (_hub.C.Dialogs.PickFolder("MIR download folder (shared partition)") is { } f) MirFolder = f; }
    [RelayCommand] private void BrowseOther() { if (_hub.C.Dialogs.PickFolder("Other documents folder") is { } f) OtherFolder = f; }
    [RelayCommand] private void BrowseShots() { if (_hub.C.Dialogs.PickFolder("Workflow screenshots folder") is { } f) ScreenshotFolder = f; }

    /// <summary>Called by the view with the PasswordBox content (never bound, never logged).</summary>
    public void SaveCredential(string password)
    {
        if (string.IsNullOrWhiteSpace(CredUser) || string.IsNullOrEmpty(password)) { _hub.C.Toasts.Show("USER AND PASSWORD", "Both are needed.", ToastKind.Warn); return; }
        try
        {
            _hub.Automation.Vault.Save(CredUser.Trim(), password);
            _hub.C.Toasts.Show("PASSWORD STORED", "Encrypted with Windows DPAPI for your user only.", ToastKind.Good);
        }
        catch (PlatformNotSupportedException ex) { _hub.C.Toasts.Show("NOT SUPPORTED", ex.Message, ToastKind.Warn); }
        Refresh();
    }

    [RelayCommand] private void ClearCredential() { _hub.Automation.Vault.Clear(); Refresh(); _hub.C.Toasts.Show("STORED PASSWORD REMOVED", kind: ToastKind.Good); }

    [RelayCommand]
    private async Task TestLogin()
    {
        var ok = await _hub.RunBrowserAsync("Opening Aconex - log in in the browser window if asked...", async c => { await c.EnsureLoggedInAsync(); return true; }, visible: true);
        if (ok) _hub.C.Toasts.Show("LOGGED IN", "The session is kept in " + ProfileDir, ToastKind.Good, 8);
    }

    [RelayCommand] private async Task CloseBrowser() { await _hub.Automation.CloseBrowserAsync(); _hub.C.Toasts.Show("BROWSER CLOSED"); }
}
