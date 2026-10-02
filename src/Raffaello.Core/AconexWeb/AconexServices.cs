using System.Globalization;
using System.IO.Compression;
using Raffaello.Core.Domain;

namespace Raffaello.Core.AconexWeb;

/// <summary>Looks workflows up through an <see cref="IAconexClient"/>, keeps every check and the step-change history, attaches screenshots.</summary>
public sealed class WorkflowTracker
{
    private readonly IAconexClient _client;
    private readonly IAconexStore _store;
    private readonly AconexConfig _cfg;

    public WorkflowTracker(IAconexClient client, IAconexStore store, AconexConfig cfg) { _client = client; _store = store; _cfg = cfg; }

    public event Action<string>? Log;

    public string ScreenshotFolder => AconexConfig.Expand(_cfg.Folders.ScreenshotFolder) is { Length: > 0 } f && !f.Contains('%')
        ? f : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "aconex-screenshots");

    /// <summary>Looks one workflow up and stores the result; screenshots are attached to <paramref name="subInvoiceId"/> when given.</summary>
    public async Task<WorkflowLookupResult> LookupAsync(string workflowNo, long? subInvoiceId = null, CancellationToken ct = default)
    {
        WorkflowLookupResult r;
        try
        {
            Directory.CreateDirectory(ScreenshotFolder);
            r = await _client.LookupWorkflowAsync(workflowNo, ScreenshotFolder, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not AconexLoginRequiredException)
        {
            r = new WorkflowLookupResult { WorkflowNo = workflowNo, CheckedAt = DateTime.Now, State = WorkflowStates.Error, Error = ex.Message };
        }
        _store.SaveCheck(r, subInvoiceId);
        Log?.Invoke(r.Summary);
        return r;
    }

    /// <summary>Refreshes every linked open invoice revision (one browser session). Returns the results in board order.</summary>
    public async Task<List<WorkflowLookupResult>> RefreshAllAsync(IEnumerable<SubInvoice> invoices, IProgress<(int Done, int Total, string Wf)>? progress = null, CancellationToken ct = default)
    {
        var board = StatusBoard.Build(invoices, _store.ActiveLinks(), _store.LatestChecks(), DateTime.Today, includeClosed: false);
        var targets = board.Where(b => b.WorkflowNo.Length > 0).GroupBy(b => WorkflowParser.NormalizeWf(b.WorkflowNo)).Select(g => g.First()).ToList();
        var results = new List<WorkflowLookupResult>();
        if (targets.Count == 0) return results;
        await _client.EnsureLoggedInAsync(ct).ConfigureAwait(false);
        var i = 0;
        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, targets.Count, t.WorkflowNo));
            results.Add(await LookupAsync(t.WorkflowNo, t.SubInvoiceId, ct).ConfigureAwait(false));
            i++;
        }
        progress?.Report((i, targets.Count, ""));
        return results;
    }
}

/// <summary>One line of the invoice status board ("where is invoice X").</summary>
public sealed record StatusBoardRow
{
    public long SubInvoiceId { get; init; }
    public string Invoice { get; init; } = "";
    public string Subcontractor { get; init; } = "";
    public string ContractNo { get; init; } = "";
    public int InvoiceNo { get; init; }
    public int Revision { get; init; }
    public string InvoiceStatus { get; init; } = "";
    public string WorkflowNo { get; init; } = "";
    public string WorkflowName { get; init; } = "";
    public string State { get; init; } = WorkflowStates.NotChecked;
    public string CurrentStep { get; init; } = "";
    public string WithWhom { get; init; } = "";
    public DateTime? DateDue { get; init; }
    public bool IsOverdue { get; init; }
    public int DaysOverdue { get; init; }
    public string Outcome { get; init; } = "";
    public DateTime? LastChecked { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public int DaysInWorkflow { get; init; }
    public string Error { get; init; } = "";
    public string Screenshot { get; init; } = "";

    /// <summary>Tag text for the UI (OVER / CHECK / OK / OPEN / ERROR / REJECTED).</summary>
    public string Tag => State switch
    {
        WorkflowStates.Overdue => "OVER",
        WorkflowStates.Rejected => "REJECTED",
        WorkflowStates.Approved => "APPROVED",
        WorkflowStates.Error => "ERROR",
        WorkflowStates.NotFound => "CHECK",
        WorkflowStates.NotChecked => WorkflowNo.Length == 0 ? "NO WF" : "OPEN",
        _ => "OPEN",
    };
}

public static class StatusBoard
{
    /// <summary>
    /// Open invoice revisions (not approved, or approved but includeClosed) with their workflow: the active link wins over the
    /// workflow number typed on the revision; the latest check gives where it is.
    /// </summary>
    public static List<StatusBoardRow> Build(IEnumerable<SubInvoice> invoices, IEnumerable<AconexWorkflowLink> links,
        IReadOnlyDictionary<string, AconexWorkflowCheck> latest, DateTime today, bool includeClosed = false)
    {
        var linkBy = links.Where(l => l.Active).GroupBy(l => l.SubInvoiceId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.LinkedAt).ThenByDescending(l => l.Id).First());
        var latestNorm = latest.Values.GroupBy(c => WorkflowParser.NormalizeWf(c.WorkflowNo)).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CheckedAt).First());
        var rows = new List<StatusBoardRow>();
        foreach (var inv in invoices)
        {
            if (!includeClosed && inv.Status == SubInvoiceStatus.Approved) continue;
            var wf = linkBy.TryGetValue(inv.Id, out var l) ? l.WorkflowNo : (inv.AconexWorkflowNo ?? "").Trim();
            if (!includeClosed && inv.Status == SubInvoiceStatus.Draft && wf.Length == 0) continue;
            latestNorm.TryGetValue(WorkflowParser.NormalizeWf(wf), out var c);
            var start = inv.SubmittedAt ?? inv.CreatedAt;
            rows.Add(new StatusBoardRow
            {
                SubInvoiceId = inv.Id, Invoice = inv.Title, Subcontractor = inv.Subcontractor, ContractNo = inv.ContractNo, InvoiceNo = inv.InvoiceNo, Revision = inv.Revision,
                InvoiceStatus = inv.Status, WorkflowNo = wf, WorkflowName = c?.WorkflowName ?? "",
                State = wf.Length == 0 ? WorkflowStates.NotChecked : c?.State ?? WorkflowStates.NotChecked,
                CurrentStep = c?.CurrentStep ?? "", WithWhom = c?.WithWhom ?? "", DateDue = c?.DateDue,
                IsOverdue = c is { } cc && (cc.IsOverdue || cc.DateDue is { } due && due.Date < today.Date && cc.State is WorkflowStates.InProgress or WorkflowStates.Overdue),
                DaysOverdue = c?.DateDue is { } d && c.State is WorkflowStates.InProgress or WorkflowStates.Overdue ? Math.Max(0, (int)(today.Date - d.Date).TotalDays) : 0,
                Outcome = c?.Outcome ?? "", LastChecked = c?.CheckedAt, SubmittedAt = inv.SubmittedAt,
                DaysInWorkflow = start == default ? 0 : Math.Max(0, (int)(today.Date - start.Date).TotalDays),
                Error = c?.Error ?? "", Screenshot = c?.TableScreenshot is { Length: > 0 } ts ? ts : c?.PageScreenshot ?? "",
            });
        }
        return rows.OrderByDescending(r => r.IsOverdue).ThenByDescending(r => r.DaysOverdue).ThenBy(r => r.Subcontractor).ThenBy(r => r.InvoiceNo).ThenBy(r => r.Revision).ToList();
    }

    public static Export.ExportSheet ToSheet(IReadOnlyList<StatusBoardRow> rows, DateTime asOf) => new()
    {
        Name = "INVOICE STATUS", Title = "INVOICE STATUS - ACONEX WORKFLOWS", Subtitle = $"As of {asOf:dd-MMM-yyyy HH:mm}  |  {rows.Count} invoice revisions  |  {rows.Count(r => r.IsOverdue)} overdue",
        Columns = new()
        {
            new("INVOICE", Width: 34), new("SUBCONTRACTOR", Width: 22), new("STATUS"), new("WORKFLOW NO"), new("WORKFLOW STATE"), new("CURRENT STEP", Width: 26),
            new("WITH", Width: 34), new("DATE DUE", Export.ColumnKind.Date), new("DAYS OVERDUE", Export.ColumnKind.Integer), new("OUTCOME", Width: 18),
            new("DAYS IN WORKFLOW", Export.ColumnKind.Integer), new("LAST CHECKED", Export.ColumnKind.Date),
        },
        Rows = rows.Select(r => new object?[]
        {
            r.Invoice, r.Subcontractor, r.InvoiceStatus, r.WorkflowNo, r.State, r.CurrentStep, r.WithWhom, r.DateDue, r.IsOverdue ? r.DaysOverdue : null, r.Outcome,
            r.DaysInWorkflow, r.LastChecked,
        }).ToList(),
    };

    public static Export.ExportSheet HistorySheet(IEnumerable<AconexStepChange> changes) => new()
    {
        Name = "STEP CHANGES", Title = "ACONEX WORKFLOW STEP CHANGES",
        Columns = new() { new("WHEN", Export.ColumnKind.Date), new("WORKFLOW NO"), new("STEP", Width: 26), new("FIELD"), new("FROM", Width: 30), new("TO", Width: 30) },
        Rows = changes.Select(c => new object?[] { c.At, c.WorkflowNo, c.StepName, c.Field, c.OldValue, c.NewValue }).ToList(),
    };

    public static Export.ExportSheet StepsSheet(string wf, IEnumerable<WorkflowStep> steps) => new()
    {
        Name = Truncate("WF " + wf, 31), Title = $"WORKFLOW {wf}",
        Columns = new()
        {
            new("DOCUMENT NO", Width: 30), new("REV"), new("VER"), new("STEP NAME", Width: 26), new("ASSIGNED TO", Width: 34), new("DATE IN", Export.ColumnKind.Date),
            new("DATE DUE", Export.ColumnKind.Date), new("ORIGINAL DUE", Export.ColumnKind.Date), new("DATE COMPLETED", Export.ColumnKind.Date), new("STEP STATUS"), new("STEP OUTCOME"), new("FILE NAME", Width: 30),
        },
        Rows = steps.Select(s => new object?[] { s.DocumentNo, s.DocumentRevision, s.DocumentVersion, s.StepName, s.AssignedTo, s.DateIn, s.DateDue, s.OriginalDueDate, s.DateCompleted, s.StepStatusText, s.StepOutcome, s.FileName }).ToList(),
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>Daily auto-refresh at a set time (while the app runs).</summary>
public static class DailySchedule
{
    public static TimeSpan? ParseTime(string? hhmm) =>
        TimeSpan.TryParseExact((hhmm ?? "").Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    /// <summary>True when the scheduled time today has passed and the last run was before it.</summary>
    public static bool IsDue(DateTime now, TimeSpan at, DateTime? lastRun)
    {
        var slot = now.Date + at;
        return now >= slot && (lastRun is null || lastRun < slot);
    }

    public static DateTime NextRun(DateTime now, TimeSpan at, DateTime? lastRun) =>
        IsDue(now, at, lastRun) ? now : (now.Date + at > now ? now.Date + at : now.Date.AddDays(1) + at);
}

/// <summary>Processes a download job: searches, skips known revisions, downloads into WIR/MIR folders, extracts ZIP bundles, registers with SHA-256.</summary>
public sealed class DocumentDownloadService
{
    private readonly IAconexClient _client;
    private readonly IAconexStore _store;
    private readonly AconexConfig _cfg;

    public DocumentDownloadService(IAconexClient client, IAconexStore store, AconexConfig cfg) { _client = client; _store = store; _cfg = cfg; }

    public event Action<string>? Log;

    /// <summary>Revision keys already in the register whose file still exists.</summary>
    public HashSet<string> KnownRevisions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in _store.Downloads())
            if (File.Exists(d.Path)) set.Add(DocumentRegister.RevisionKey(d.DocumentNo, d.Revision));
        return set;
    }

    /// <summary>Searches the register and creates a queued job (already-downloaded revisions are marked SKIPPED).</summary>
    public async Task<AconexDownloadJob> PlanAsync(DocumentQuery query, CancellationToken ct = default)
    {
        await _client.EnsureLoggedInAsync(ct).ConfigureAwait(false);
        var hits = await _client.SearchDocumentsAsync(query, ct).ConfigureAwait(false);
        // keep every revision returned, de-duplicated
        var unique = hits.GroupBy(h => h.RevisionKey).Select(g => g.First()).ToList();
        var missing = query.DocumentNumbers.Where(n => !unique.Any(h => h.DocumentNo.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        var job = _store.CreateJob(query, unique, KnownRevisions());
        Log?.Invoke($"Job #{job.Id}: {unique.Count} revisions found, {job.Skipped} already downloaded" + (missing.Count > 0 ? $", NOT FOUND: {string.Join(", ", missing)}" : ""));
        return job;
    }

    /// <summary>Runs (or resumes) a job. Items left PENDING / RUNNING / FAILED (with attempts left) are processed; DONE / SKIPPED are not.</summary>
    public async Task<AconexDownloadJob> RunAsync(long jobId, IProgress<(int Done, int Total, string Doc)>? progress = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        var job = _store.Jobs(int.MaxValue).First(j => j.Id == jobId);
        var query = System.Text.Json.JsonSerializer.Deserialize<DocumentQuery>(job.CriteriaJson) ?? new DocumentQuery();
        job.State = JobStates.Running;
        _store.UpdateJob(job);
        var items = _store.Queue(jobId);
        var known = KnownRevisions();
        var processed = items.Count(i => i.State is JobStates.Done or JobStates.Skipped);
        try
        {
            await _client.EnsureLoggedInAsync(ct).ConfigureAwait(false);
            foreach (var item in items.Where(i => i.State is JobStates.Pending or JobStates.Running || i.State == JobStates.Failed && i.Attempts < maxAttempts))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((processed, items.Count, item.DocumentNo));
                var key = DocumentRegister.RevisionKey(item.DocumentNo, item.Revision);
                if (known.Contains(key))
                {
                    item.State = JobStates.Skipped; item.Message = "already downloaded";
                    _store.UpdateQueueItem(item);
                    processed++;
                    continue;
                }
                item.State = JobStates.Running; item.Attempts++;
                _store.UpdateQueueItem(item);
                try
                {
                    var hit = new DocumentHit
                    {
                        DocumentNo = item.DocumentNo, Revision = item.Revision, Version = item.Version, Title = item.Title, Type = item.DocType,
                        Discipline = item.Discipline, Date = item.DocDate,
                    };
                    var folder = DocumentRegister.TargetFolder(hit, _cfg.Folders);
                    Directory.CreateDirectory(folder);
                    var saved = await _client.DownloadAsync(hit, query, folder, ct).ConfigureAwait(false);
                    var files = Register(hit, saved, job.Id);
                    item.State = JobStates.Done;
                    item.SavedPath = string.Join("; ", files.Select(f => f.Path));
                    item.Message = files.Any(f => f.FromZip) ? $"ZIP: {files.Count} files" : "";
                    known.Add(key);
                    Log?.Invoke($"{item.DocumentNo} rev {item.Revision}: {files.Count} file(s) -> {folder}");
                }
                catch (OperationCanceledException) { item.State = JobStates.Pending; item.Attempts--; _store.UpdateQueueItem(item); throw; }
                catch (AconexLoginRequiredException) { item.State = JobStates.Pending; item.Attempts--; _store.UpdateQueueItem(item); throw; }
                catch (Exception ex)
                {
                    item.State = JobStates.Failed; item.Message = ex.Message;
                    Log?.Invoke($"FAILED {item.DocumentNo} rev {item.Revision}: {ex.Message}");
                }
                _store.UpdateQueueItem(item);
                processed++;
                if (_cfg.DelayBetweenDownloadsMs > 0) await Task.Delay(_cfg.DelayBetweenDownloadsMs, ct).ConfigureAwait(false);
            }
            items = _store.Queue(jobId);
            job = _store.Jobs(int.MaxValue).First(j => j.Id == jobId);
            job.Done = items.Count(i => i.State == JobStates.Done);
            job.Skipped = items.Count(i => i.State == JobStates.Skipped);
            job.Failed = items.Count(i => i.State == JobStates.Failed);
            job.State = job.Failed > 0 ? JobStates.Failed : JobStates.Done;
            job.FinishedAt = DateTime.Now;
            _store.UpdateJob(job);
            progress?.Report((items.Count, items.Count, ""));
            return job;
        }
        catch (Exception ex) when (ex is OperationCanceledException or AconexLoginRequiredException)
        {
            items = _store.Queue(jobId);
            job = _store.Jobs(int.MaxValue).First(j => j.Id == jobId);
            job.Done = items.Count(i => i.State == JobStates.Done);
            job.Skipped = items.Count(i => i.State == JobStates.Skipped);
            job.State = JobStates.Paused;
            _store.UpdateJob(job);
            throw;
        }
    }

    /// <summary>Registers a downloaded file; a ZIP bundle is extracted next to it and each file is registered (the zip is removed).</summary>
    public List<AconexDownload> Register(DocumentHit hit, string savedPath, long jobId)
    {
        var result = new List<AconexDownload>();
        var files = new List<DownloadedFile>();
        if (ZipBundle.IsZip(savedPath)) files.AddRange(ZipBundle.Extract(savedPath, Path.GetDirectoryName(savedPath)!, deleteZip: true));
        else files.Add(new DownloadedFile(savedPath, Path.GetFileName(savedPath), false));
        foreach (var f in files)
        {
            var fi = new FileInfo(f.Path);
            result.Add(_store.RegisterDownload(new AconexDownload
            {
                DocumentNo = hit.DocumentNo, Revision = hit.Revision, Version = hit.Version, Title = hit.Title, DocType = hit.Type, Kind = DocumentRegister.Kind(hit, _cfg.Folders),
                Discipline = hit.Discipline, DocDate = hit.Date, Path = f.Path, FileName = fi.Name, SizeBytes = fi.Length, Sha256 = FileHash.Sha256(f.Path),
                FromZip = f.FromZip, DownloadedAt = DateTime.Now, JobId = jobId,
            }));
        }
        return result;
    }
}

public static class ZipBundle
{
    public static bool IsZip(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[4];
        return fs.Read(sig) == 4 && sig[0] == 0x50 && sig[1] == 0x4B && sig[2] == 0x03 && sig[3] == 0x04;
    }

    /// <summary>Extracts every file (flattened, names made unique, path traversal refused).</summary>
    public static List<DownloadedFile> Extract(string zipPath, string folder, bool deleteZip)
    {
        var list = new List<DownloadedFile>();
        Directory.CreateDirectory(folder);
        using (var z = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in z.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // directory
                var name = DocumentRegister.Safe(Path.GetFileName(e.FullName.Replace('\\', '/')));
                var target = UniquePath(Path.Combine(folder, name));
                if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;
                e.ExtractToFile(target, overwrite: false);
                list.Add(new DownloadedFile(target, e.FullName, true));
            }
        }
        if (deleteZip && list.Count > 0) File.Delete(zipPath);
        return list;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
