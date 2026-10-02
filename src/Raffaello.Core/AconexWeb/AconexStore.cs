using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.AconexWeb;

// ------------------------------------------------------------------ entities (own tables, same data file)

/// <summary>Invoice revision (SubInvoice) &lt;-&gt; Aconex workflow. One revision can be re-linked; the latest row wins.</summary>
public sealed class AconexWorkflowLink : Entity
{
    public long SubInvoiceId { get; set; }
    public string WorkflowNo { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime LinkedAt { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Result of one lookup (kept for history and for the status board).</summary>
public sealed class AconexWorkflowCheck : Entity
{
    public string WorkflowNo { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public DateTime CheckedAt { get; set; }
    public string State { get; set; } = "";
    public string CurrentStep { get; set; } = "";
    public string WithWhom { get; set; } = "";
    public DateTime? DateDue { get; set; }
    public bool IsOverdue { get; set; }
    public int DaysOverdue { get; set; }
    public string Outcome { get; set; } = "";
    public string DocumentNo { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    /// <summary>Steps as JSON (<see cref="WorkflowStep"/> list).</summary>
    public string StepsJson { get; set; } = "";
    public string PageScreenshot { get; set; } = "";
    public string TableScreenshot { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>One change between two checks of the same workflow (new step, status / outcome / assignee / due changed).</summary>
public sealed class AconexStepChange : Entity
{
    public string WorkflowNo { get; set; } = "";
    public DateTime At { get; set; }
    public string StepName { get; set; } = "";
    public string Field { get; set; } = "";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
}

/// <summary>A file attached to an invoice revision (workflow screenshots ...).</summary>
public sealed class InvoiceAttachment : Entity
{
    public long SubInvoiceId { get; set; }
    public string Kind { get; set; } = "WORKFLOW SCREENSHOT";
    public string WorkflowNo { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

/// <summary>Register of every file downloaded from the document register.</summary>
public sealed class AconexDownload : Entity
{
    public string DocumentNo { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public string DocType { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Discipline { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public bool FromZip { get; set; }
    public DateTime DownloadedAt { get; set; }
    public long JobId { get; set; }
}

public static class JobStates
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Done = "DONE";
    public const string Skipped = "SKIPPED";
    public const string Failed = "FAILED";
    public const string Paused = "PAUSED";
    public const string NotFound = "NOT FOUND";
}

/// <summary>A download run (criteria + progress) that can be resumed after a crash or a stop.</summary>
public sealed class AconexDownloadJob : Entity
{
    public DateTime CreatedAt { get; set; }
    public string CriteriaJson { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = JobStates.Pending;
    public int Total { get; set; }
    public int Done { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>One document revision in a job queue.</summary>
public sealed class AconexQueueItem : Entity
{
    public long JobId { get; set; }
    public int Order { get; set; }
    public string DocumentNo { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public string DocType { get; set; } = "";
    public string Discipline { get; set; } = "";
    public DateTime? DocDate { get; set; }
    public string State { get; set; } = JobStates.Pending;
    public int Attempts { get; set; }
    public string Message { get; set; } = "";
    public string SavedPath { get; set; } = "";
}

// ------------------------------------------------------------------ store

public interface IAconexStore
{
    string User { get; set; }
    Func<DateTime> Clock { get; set; }
    void EnsureSchema();

    // links
    AconexWorkflowLink Link(long subInvoiceId, string workflowNo, string note = "");
    List<AconexWorkflowLink> ActiveLinks();

    // checks + history
    AconexWorkflowCheck SaveCheck(WorkflowLookupResult result, long? subInvoiceId);
    List<AconexWorkflowCheck> Checks(string workflowNo);
    AconexWorkflowCheck? LatestCheck(string workflowNo);
    Dictionary<string, AconexWorkflowCheck> LatestChecks();
    List<AconexStepChange> History(string? workflowNo = null, int take = 500);
    List<InvoiceAttachment> Attachments(long subInvoiceId);
    void AddAttachment(InvoiceAttachment a);

    // downloads
    List<AconexDownload> Downloads();
    AconexDownload? FindDownload(string docNo, string revision);
    AconexDownload RegisterDownload(AconexDownload d);
    AconexDownloadJob CreateJob(DocumentQuery query, IEnumerable<DocumentHit> hits, IReadOnlySet<string> alreadyDownloaded);
    List<AconexDownloadJob> Jobs(int take = 50);
    List<AconexQueueItem> Queue(long jobId);
    void UpdateQueueItem(AconexQueueItem item);
    void UpdateJob(AconexDownloadJob job);
    AconexDownloadJob? ResumableJob();
}

/// <summary>SQLite implementation; its tables live in the same data file as the project (created on first use).</summary>
public sealed class SqliteAconexStore : SideStore, IAconexStore
{
    public SqliteAconexStore(string path, string user, string? machine = null) : base(path, user, machine) { }

    protected override IEnumerable<Type> Tables => new[]
    {
        typeof(AconexWorkflowLink), typeof(AconexWorkflowCheck), typeof(AconexStepChange), typeof(InvoiceAttachment),
        typeof(AconexDownload), typeof(AconexDownloadJob), typeof(AconexQueueItem),
    };

    protected override IEnumerable<string> Indexes => new[]
    {
        "CREATE INDEX IF NOT EXISTS IX_AconexWorkflowChecks_Wf ON AconexWorkflowChecks(WorkflowNo, CheckedAt);",
        "CREATE INDEX IF NOT EXISTS IX_AconexDownloads_Doc ON AconexDownloads(DocumentNo, Revision);",
        "CREATE INDEX IF NOT EXISTS IX_AconexQueueItems_Job ON AconexQueueItems(JobId);",
        "CREATE INDEX IF NOT EXISTS IX_AconexStepChanges_Wf ON AconexStepChanges(WorkflowNo, At);",
    };

    public AconexWorkflowLink Link(long subInvoiceId, string workflowNo, string note = "")
    {
        var wf = workflowNo.Trim().ToUpperInvariant();
        AconexWorkflowLink? created = null;
        Batch(b =>
        {
            foreach (var old in b.All<AconexWorkflowLink>("SubInvoiceId=@Id AND Active=1", new { Id = subInvoiceId }))
            {
                if (WorkflowParser.NormalizeWf(old.WorkflowNo) == WorkflowParser.NormalizeWf(wf)) { created = old; return; }
                old.Active = false;
                b.Update(old);
            }
            created = b.Insert(new AconexWorkflowLink { SubInvoiceId = subInvoiceId, WorkflowNo = wf, Note = note, LinkedAt = Clock(), Active = true });
        }, $"Invoice revision #{subInvoiceId} linked to Aconex workflow {wf}");
        return created!;
    }

    public List<AconexWorkflowLink> ActiveLinks() => All<AconexWorkflowLink>("Active=1");

    public AconexWorkflowCheck SaveCheck(WorkflowLookupResult r, long? subInvoiceId)
    {
        var check = new AconexWorkflowCheck
        {
            WorkflowNo = r.WorkflowNo.Trim().ToUpperInvariant(), WorkflowName = r.WorkflowName, CheckedAt = r.CheckedAt == default ? Clock() : r.CheckedAt,
            State = r.State, CurrentStep = r.CurrentStep, WithWhom = r.WithWhom, DateDue = r.DateDue, IsOverdue = r.IsOverdue, DaysOverdue = r.DaysOverdue,
            Outcome = r.Outcome, DocumentNo = r.DocumentNo, DocumentTitle = r.DocumentTitle, StepsJson = StepJson.Serialize(r.Steps),
            PageScreenshot = r.PageScreenshotPath, TableScreenshot = r.TableScreenshotPath, Error = r.Error,
        };
        var previous = r.State is WorkflowStates.Error ? null : LatestSuccessful(check.WorkflowNo);
        var changes = previous is null || r.State is WorkflowStates.Error ? new List<AconexStepChange>()
            : WorkflowHistory.Diff(check.WorkflowNo, StepJson.Deserialize(previous.StepsJson), r.Steps, check.CheckedAt);
        Batch(b =>
        {
            b.Insert(check);
            foreach (var ch in changes) b.Insert(ch);
            if (subInvoiceId is { } id)
                foreach (var shot in new[] { r.PageScreenshotPath, r.TableScreenshotPath }.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)))
                    b.Insert(new InvoiceAttachment { SubInvoiceId = id, WorkflowNo = check.WorkflowNo, Path = shot, Sha256 = FileHash.Sha256(shot), AddedAt = check.CheckedAt });
        }, $"Aconex {r.Summary}" + (changes.Count > 0 ? $" ({changes.Count} changes)" : ""));
        return check;
    }

    private AconexWorkflowCheck? LatestSuccessful(string wf) =>
        All<AconexWorkflowCheck>("WorkflowNo=@W AND State<>@E ORDER BY CheckedAt DESC, Id DESC LIMIT 1", new { W = wf, E = WorkflowStates.Error }).FirstOrDefault();

    public List<AconexWorkflowCheck> Checks(string workflowNo) =>
        All<AconexWorkflowCheck>("WorkflowNo=@W ORDER BY CheckedAt DESC, Id DESC", new { W = workflowNo.Trim().ToUpperInvariant() });

    public AconexWorkflowCheck? LatestCheck(string workflowNo) => Checks(workflowNo).FirstOrDefault();

    public Dictionary<string, AconexWorkflowCheck> LatestChecks() =>
        All<AconexWorkflowCheck>("Id IN (SELECT MAX(Id) FROM AconexWorkflowChecks GROUP BY WorkflowNo)")
            .ToDictionary(c => c.WorkflowNo, StringComparer.OrdinalIgnoreCase);

    public List<AconexStepChange> History(string? workflowNo = null, int take = 500) => workflowNo is null
        ? All<AconexStepChange>("1=1 ORDER BY At DESC, Id DESC LIMIT @N", new { N = take })
        : All<AconexStepChange>("WorkflowNo=@W ORDER BY At DESC, Id DESC LIMIT @N", new { W = workflowNo.Trim().ToUpperInvariant(), N = take });

    public List<InvoiceAttachment> Attachments(long subInvoiceId) => All<InvoiceAttachment>("SubInvoiceId=@Id ORDER BY AddedAt DESC", new { Id = subInvoiceId });
    public void AddAttachment(InvoiceAttachment a) => Insert(a, $"Attached {System.IO.Path.GetFileName(a.Path)} to invoice revision #{a.SubInvoiceId}");

    public List<AconexDownload> Downloads() => All<AconexDownload>("1=1 ORDER BY DownloadedAt DESC, Id DESC");

    public AconexDownload? FindDownload(string docNo, string revision) =>
        All<AconexDownload>("UPPER(DocumentNo)=@D AND UPPER(Revision)=@R ORDER BY Id DESC", new { D = docNo.Trim().ToUpperInvariant(), R = revision.Trim().ToUpperInvariant() }).FirstOrDefault();

    public AconexDownload RegisterDownload(AconexDownload d) => Insert(d, $"Downloaded {d.DocumentNo} rev {d.Revision} -> {d.FileName}");

    public AconexDownloadJob CreateJob(DocumentQuery query, IEnumerable<DocumentHit> hits, IReadOnlySet<string> alreadyDownloaded)
    {
        var list = hits.ToList();
        var job = new AconexDownloadJob
        {
            CreatedAt = Clock(), CriteriaJson = System.Text.Json.JsonSerializer.Serialize(query), Description = query.Describe(), State = JobStates.Pending, Total = list.Count,
        };
        Batch(b =>
        {
            b.Insert(job);
            var i = 0;
            foreach (var h in list)
            {
                var skip = alreadyDownloaded.Contains(h.RevisionKey);
                b.Insert(new AconexQueueItem
                {
                    JobId = job.Id, Order = ++i, DocumentNo = h.DocumentNo, Revision = h.Revision, Version = h.Version, Title = h.Title, DocType = h.Type,
                    Discipline = h.Discipline, DocDate = h.Date, State = skip ? JobStates.Skipped : JobStates.Pending, Message = skip ? "already downloaded" : "",
                });
                if (skip) job.Skipped++;
            }
            b.Update(job);
        }, $"Aconex download job: {job.Description} ({list.Count} revisions)");
        return Get<AconexDownloadJob>(job.Id)!;
    }

    public List<AconexDownloadJob> Jobs(int take = 50) => All<AconexDownloadJob>("1=1 ORDER BY Id DESC LIMIT @N", new { N = take });
    public List<AconexQueueItem> Queue(long jobId) => All<AconexQueueItem>("JobId=@J ORDER BY [Order]", new { J = jobId });
    public void UpdateQueueItem(AconexQueueItem item) => Batch(b => b.Update(item), $"Queue {item.DocumentNo} rev {item.Revision}: {item.State}");
    public void UpdateJob(AconexDownloadJob job) => Batch(b => b.Update(job), $"Download job #{job.Id}: {job.State} {job.Done}/{job.Total}");

    public AconexDownloadJob? ResumableJob() =>
        All<AconexDownloadJob>("State IN (@P,@R,@S) ORDER BY Id DESC LIMIT 1", new { P = JobStates.Pending, R = JobStates.Running, S = JobStates.Paused }).FirstOrDefault();
}

public static class StepJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Opt = new() { WriteIndented = false };
    public static string Serialize(IEnumerable<WorkflowStep> steps) => System.Text.Json.JsonSerializer.Serialize(steps.ToList(), Opt);
    public static List<WorkflowStep> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<WorkflowStep>>(json, Opt) ?? new(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }
}

public static class FileHash
{
    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
    }
}

/// <summary>What changed between two lookups of the same workflow.</summary>
public static class WorkflowHistory
{
    public static List<AconexStepChange> Diff(string wf, IReadOnlyList<WorkflowStep> before, IReadOnlyList<WorkflowStep> after, DateTime at)
    {
        var changes = new List<AconexStepChange>();
        // steps are identified by name + occurrence (a step can repeat after a rejection)
        static List<(string Key, WorkflowStep Step)> Keyed(IEnumerable<WorkflowStep> steps)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, WorkflowStep)>();
            foreach (var s in steps)
            {
                var n = seen.TryGetValue(s.StepName, out var k) ? k + 1 : 1;
                seen[s.StepName] = n;
                list.Add(($"{s.StepName.ToUpperInvariant()}#{n}", s));
            }
            return list;
        }
        var old = Keyed(before).ToDictionary(x => x.Key, x => x.Step);
        foreach (var (key, s) in Keyed(after))
        {
            AconexStepChange C(string field, string o, string n) => new() { WorkflowNo = wf, At = at, StepName = s.StepName, Field = field, OldValue = o, NewValue = n };
            if (!old.TryGetValue(key, out var o))
            {
                changes.Add(C("NEW STEP", "", $"{s.StepStatusText} - {s.AssignedTo}".Trim(' ', '-')));
                continue;
            }
            if (!string.Equals(o.StepStatus, s.StepStatus, StringComparison.OrdinalIgnoreCase)) changes.Add(C("STATUS", o.StepStatusText, s.StepStatusText));
            if (!string.Equals(o.StepOutcome, s.StepOutcome, StringComparison.OrdinalIgnoreCase)) changes.Add(C("OUTCOME", o.StepOutcome, s.StepOutcome));
            if (!string.Equals(o.AssignedTo, s.AssignedTo, StringComparison.OrdinalIgnoreCase)) changes.Add(C("ASSIGNED TO", o.AssignedTo, s.AssignedTo));
            if (o.DateDue != s.DateDue) changes.Add(C("DATE DUE", o.DateDue?.ToString("dd-MMM-yyyy") ?? "", s.DateDue?.ToString("dd-MMM-yyyy") ?? ""));
            if (o.DateCompleted != s.DateCompleted) changes.Add(C("COMPLETED", o.DateCompleted?.ToString("dd-MMM-yyyy") ?? "", s.DateCompleted?.ToString("dd-MMM-yyyy") ?? ""));
        }
        return changes;
    }
}
