namespace Raffaello.Core.AconexWeb;

/// <summary>A table as read from a page: header texts and the cell texts of every body row.</summary>
public sealed class RawTable
{
    public List<string> Headers { get; set; } = new();
    public List<RawRow> Rows { get; set; } = new();
}

public sealed class RawRow
{
    public List<string> Cells { get; set; } = new();
    /// <summary>Href of the first link in each cell ("" when none).</summary>
    public List<string> Links { get; set; } = new();
    /// <summary>A grouping row (one wide cell), e.g. "Workflow No.: WF-008795 Name: ...".</summary>
    public bool IsGroup { get; set; }
    /// <summary>Stable key the automation uses to find the row again (row index on its page, or a data attribute).</summary>
    public string Key { get; set; } = "";
}

public static class StepStatuses
{
    public const string Completed = "COMPLETED";
    public const string Pending = "PENDING";
    public const string Overdue = "OVERDUE";
    public const string Terminated = "TERMINATED";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Overall state of a workflow, as shown on the status board.</summary>
public static class WorkflowStates
{
    public const string InProgress = "IN PROGRESS";
    public const string Overdue = "OVERDUE";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string NotFound = "NOT FOUND";
    public const string Error = "ERROR";
    public const string NotChecked = "NOT CHECKED";
}

public sealed record WorkflowStep
{
    public int Order { get; init; }
    public string DocumentNo { get; init; } = "";
    public string DocumentRevision { get; init; } = "";
    public string DocumentVersion { get; init; } = "";
    public string DocumentTitle { get; init; } = "";
    public string StepName { get; init; } = "";
    public string Action { get; init; } = "";
    /// <summary>One or more people, '; ' separated.</summary>
    public string AssignedTo { get; init; } = "";
    public DateTime? DateIn { get; init; }
    public DateTime? DateDue { get; init; }
    public DateTime? OriginalDueDate { get; init; }
    public DateTime? DateCompleted { get; init; }
    /// <summary>Normalised: COMPLETED / PENDING / OVERDUE / TERMINATED / UNKNOWN.</summary>
    public string StepStatus { get; init; } = StepStatuses.Unknown;
    public string StepStatusText { get; init; } = "";
    public string StepOutcome { get; init; } = "";
    public string FileName { get; init; } = "";

    public bool IsOpen => StepStatus is StepStatuses.Pending or StepStatuses.Overdue or StepStatuses.Unknown && DateCompleted is null;
}

/// <summary>Result of one workflow lookup.</summary>
public sealed class WorkflowLookupResult
{
    public string WorkflowNo { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public List<WorkflowStep> Steps { get; set; } = new();
    public DateTime CheckedAt { get; set; }
    /// <summary>Overall state (<see cref="WorkflowStates"/>).</summary>
    public string State { get; set; } = WorkflowStates.NotChecked;
    public string CurrentStep { get; set; } = "";
    public string WithWhom { get; set; } = "";
    public DateTime? DateDue { get; set; }
    public bool IsOverdue { get; set; }
    public int DaysOverdue { get; set; }
    public string Outcome { get; set; } = "";
    public string DocumentNo { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public string PageScreenshotPath { get; set; } = "";
    public string TableScreenshotPath { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>Header texts that could not be matched to a known column (for diagnosing a changed page).</summary>
    public List<string> UnknownHeaders { get; set; } = new();
    public List<string> MissingColumns { get; set; } = new();

    public string Summary => State switch
    {
        WorkflowStates.NotFound => $"{WorkflowNo}: not found",
        WorkflowStates.Error => $"{WorkflowNo}: {Error}",
        WorkflowStates.Approved or WorkflowStates.Rejected => $"{WorkflowNo}: {State} ({Outcome})",
        _ => $"{WorkflowNo}: at {CurrentStep} with {WithWhom}" + (DateDue is { } d ? $", due {d:dd-MMM-yyyy}" : "") + (IsOverdue ? $" - OVERDUE {DaysOverdue} d" : ""),
    };
}

/// <summary>One row of the document register search.</summary>
public sealed record DocumentHit
{
    public string DocumentNo { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Version { get; init; } = "";
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public string Discipline { get; init; } = "";
    public string Group { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Status { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>How to find the row again on the results page.</summary>
    public string RowKey { get; init; } = "";
    public int Page { get; init; } = 1;

    public string RevisionKey => DocumentRegister.RevisionKey(DocumentNo, Revision);
}

/// <summary>What to search for in the document register.</summary>
public sealed record DocumentQuery
{
    public List<string> DocumentNumbers { get; init; } = new();
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }
    public string Group { get; init; } = "";
    public string Discipline { get; init; } = "";
    public string DocType { get; init; } = "";

    public bool IsEmpty => DocumentNumbers.Count == 0 && DateFrom is null && DateTo is null && Group.Length == 0 && Discipline.Length == 0 && DocType.Length == 0;

    public string Describe()
    {
        var p = new List<string>();
        if (DocumentNumbers.Count > 0) p.Add($"{DocumentNumbers.Count} numbers");
        if (DateFrom != null || DateTo != null) p.Add($"{DateFrom:dd-MMM-yy} .. {DateTo:dd-MMM-yy}");
        if (Group.Length > 0) p.Add("group " + Group);
        if (Discipline.Length > 0) p.Add("discipline " + Discipline);
        if (DocType.Length > 0) p.Add("type " + DocType);
        return p.Count == 0 ? "everything" : string.Join(", ", p);
    }
}

/// <summary>A file saved by a download (one per file; a ZIP bundle gives several).</summary>
public sealed record DownloadedFile(string Path, string OriginalName, bool FromZip);

public sealed class AconexLoginRequiredException : Exception
{
    public AconexLoginRequiredException(string message) : base(message) { }
}

public sealed class AconexPageChangedException : Exception
{
    public AconexPageChangedException(string message) : base(message) { }
}

/// <summary>
/// The browser side. The real implementation drives Aconex with Playwright (Raffaello.Automation);
/// tests use a mock site or a fake.
/// </summary>
public interface IAconexClient : IAsyncDisposable
{
    /// <summary>Opens the browser and makes sure the session is logged in (may wait for the user).</summary>
    Task EnsureLoggedInAsync(CancellationToken ct = default);

    /// <summary>Looks a workflow up; saves a full-page and a table screenshot into <paramref name="screenshotFolder"/>.</summary>
    Task<WorkflowLookupResult> LookupWorkflowAsync(string workflowNo, string screenshotFolder, CancellationToken ct = default);

    Task<List<DocumentHit>> SearchDocumentsAsync(DocumentQuery query, CancellationToken ct = default);

    /// <summary>Downloads one register row into <paramref name="targetFolder"/>; a ZIP bundle is returned as the zip path (the caller extracts).</summary>
    Task<string> DownloadAsync(DocumentHit hit, DocumentQuery query, string targetFolder, CancellationToken ct = default);

    event Action<string>? Log;
}

/// <summary>Optional stored login. The Windows implementation encrypts with DPAPI (current user); nothing is ever stored in plain text.</summary>
public interface ICredentialVault
{
    bool IsSupported { get; }
    bool HasCredential { get; }
    void Save(string userName, string password);
    (string User, string Password)? Load();
    void Clear();
}

/// <summary>No stored credential (non-Windows, or the user prefers to type it every time).</summary>
public sealed class NoCredentialVault : ICredentialVault
{
    public bool IsSupported => false;
    public bool HasCredential => false;
    public void Save(string userName, string password) => throw new PlatformNotSupportedException("Storing the Aconex password needs Windows (DPAPI).");
    public (string User, string Password)? Load() => null;
    public void Clear() { }
}
