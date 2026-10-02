using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raffaello.Core.AconexWeb;

/// <summary>
/// Everything the browser automation needs to know about the Aconex web UI: URLs, CSS selectors, column header texts,
/// date formats and folders. Loaded from aconex.config.json (next to settings.json) so it can be corrected without
/// recompiling when Oracle changes the page. Defaults below are best guesses for ksa1.aconex.com and MUST be checked
/// on the first real run (see README "Aconex automation").
/// </summary>
public sealed class AconexConfig
{
    public int Version { get; set; } = 1;

    // ---------------------------------------------------------------- site
    public string BaseUrl { get; set; } = "https://ksa1.aconex.com";
    /// <summary>Page opened first; if it shows the login form the user (or the stored credential) logs in.</summary>
    public string HomePath { get; set; } = "/Logon";
    /// <summary>Optional project id appended as ?projectId= where a URL template contains {projectId}.</summary>
    public string ProjectId { get; set; } = "";

    // ---------------------------------------------------------------- browser
    /// <summary>Persistent profile (cookies, SSO session) so the user logs in once. Environment variables are expanded.</summary>
    public string ProfileDir { get; set; } = @"%LOCALAPPDATA%\Raffaello\aconex-profile";
    /// <summary>"msedge" (always installed on Windows), "chrome", or empty for Playwright's bundled Chromium.</summary>
    public string BrowserChannel { get; set; } = "msedge";
    /// <summary>Explicit browser executable; overrides the channel when set.</summary>
    public string BrowserExecutablePath { get; set; } = "";
    /// <summary>Visible window (needed for the first login / SSO / 2FA). Lookups can run headless once logged in.</summary>
    public bool Headless { get; set; }
    public int NavigationTimeoutSec { get; set; } = 60;
    /// <summary>How long to wait for the user to finish a manual login in the visible window.</summary>
    public int ManualLoginTimeoutSec { get; set; } = 300;
    public int DownloadTimeoutSec { get; set; } = 180;
    /// <summary>Pause between documents (be gentle with the server).</summary>
    public int DelayBetweenDownloadsMs { get; set; } = 750;

    public LoginConfig Login { get; set; } = new();
    public WorkflowSearchConfig Workflows { get; set; } = new();
    public DocumentRegisterConfig Documents { get; set; } = new();
    public FolderConfig Folders { get; set; } = new();

    /// <summary>Date formats used by the Aconex tables (first match wins).</summary>
    public List<string> DateFormats { get; set; } = new() { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "dd-MMM-yyyy", "yyyy-MM-dd" };

    /// <summary>Optional daily auto-refresh of the invoice status board ("HH:mm", empty = off).</summary>
    public string DailyRefreshTime { get; set; } = "";

    // ---------------------------------------------------------------- load / save

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello", "aconex.config.json");

    /// <summary>Loads the config; writes the defaults the first time so the user has a file to edit.</summary>
    public static AconexConfig Load(string? path = null, bool writeIfMissing = true)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            var cfg = JsonSerializer.Deserialize<AconexConfig>(File.ReadAllText(path), Json)
                      ?? throw new InvalidDataException($"{path} is empty.");
            return cfg;
        }
        var d = new AconexConfig();
        if (writeIfMissing) d.Save(path);
        return d;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AconexConfig FromJson(string json) => JsonSerializer.Deserialize<AconexConfig>(json, Json) ?? new AconexConfig();

    public static string Expand(string path) => string.IsNullOrWhiteSpace(path) ? path : Environment.ExpandEnvironmentVariables(path);

    public string ResolvedProfileDir
    {
        get
        {
            var p = Expand(ProfileDir);
            // %LOCALAPPDATA% is not set outside Windows - fall back to the .NET special folder
            if (p.Contains('%')) p = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "aconex-profile");
            return p;
        }
    }

    /// <summary>Absolute URL for a path / template ({projectId}, {workflowNo} are substituted).</summary>
    public string Url(string pathOrUrl, IReadOnlyDictionary<string, string>? values = null)
    {
        var s = pathOrUrl ?? "";
        s = s.Replace("{projectId}", Uri.EscapeDataString(ProjectId));
        if (values != null) foreach (var kv in values) s = s.Replace("{" + kv.Key + "}", Uri.EscapeDataString(kv.Value));
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s;
        return BaseUrl.TrimEnd('/') + "/" + s.TrimStart('/');
    }

    /// <summary>Problems that would make the automation fail before it starts.</summary>
    public List<string> Validate()
    {
        var e = new List<string>();
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)) e.Add("BaseUrl is not an absolute URL.");
        if (string.IsNullOrWhiteSpace(Workflows.SearchPath)) e.Add("Workflows.SearchPath is empty.");
        if (string.IsNullOrWhiteSpace(Workflows.ResultsTable)) e.Add("Workflows.ResultsTable is empty.");
        if (string.IsNullOrWhiteSpace(Documents.SearchPath)) e.Add("Documents.SearchPath is empty.");
        if (string.IsNullOrWhiteSpace(Documents.ResultsTable)) e.Add("Documents.ResultsTable is empty.");
        if (Workflows.Columns.StepName.Count == 0) e.Add("Workflows.Columns.StepName has no header text.");
        if (Documents.Columns.DocumentNo.Count == 0) e.Add("Documents.Columns.DocumentNo has no header text.");
        return e;
    }
}

public sealed class LoginConfig
{
    /// <summary>Visible when the user is NOT logged in (login form).</summary>
    public string LoginFormSelector { get; set; } = "form#logonForm, input[name='userName'], input#userName";
    /// <summary>Visible only when logged in (top bar / user menu).</summary>
    public string LoggedInSelector { get; set; } = "#nav-bar, .navBar, [data-automation-id='user-menu'], #user-menu";
    public string UserNameInput { get; set; } = "input[name='userName'], input#userName";
    public string PasswordInput { get; set; } = "input[name='password'], input#password";
    public string SubmitButton { get; set; } = "button[type='submit'], #login, input[type='submit']";
    /// <summary>Fill the DPAPI-stored credential automatically (if one is stored). SSO / 2FA always stay manual.</summary>
    public bool AutoFillStoredCredential { get; set; } = true;
}

public sealed class WorkflowSearchConfig
{
    /// <summary>Search Workflows page. {workflowNo} may be used if the site accepts it in the query string.</summary>
    public string SearchPath { get; set; } = "/Workflow/SearchWorkflows?projectId={projectId}";
    /// <summary>Optional iframe holding the page content (Aconex classic pages live in frame 'main').</summary>
    public string FrameSelector { get; set; } = "";
    public string WorkflowNoInput { get; set; } = "input[name='workflowNo'], input#workflowNo, input[placeholder*='Workflow No']";
    /// <summary>Optional: open the advanced search / add the 'Workflow No' criterion before typing.</summary>
    public string BeforeSearchClicks { get; set; } = "";
    public string SearchButton { get; set; } = "button:has-text('Search'), #searchButton, input[value='Search']";
    public string ResultsTable { get; set; } = "table#resultsTable, table.searchResults, table[role='grid']";
    /// <summary>Text shown when nothing matches.</summary>
    public string NoResultsText { get; set; } = "No results";
    public string NextPageButton { get; set; } = "a.next:not(.disabled), button[aria-label='Next page']:not([disabled])";
    /// <summary>Group header rows look like "Workflow No.: WF-008795 Name: Electrical_PR_PO_Approval".</summary>
    public string GroupRowRegex { get; set; } = @"Workflow\s*No\.?\s*:\s*(?<no>[A-Z]{1,5}-?\d+)\s*(?:Name\s*:\s*(?<name>.+))?";
    public WorkflowColumns Columns { get; set; } = new();
    /// <summary>Step outcomes that mean the document was rejected / must be resubmitted (case-insensitive 'contains').</summary>
    public List<string> RejectedOutcomes { get; set; } = new() { "reject", "c -", "d -", "revise", "resubmit", "not approved" };
    public List<string> ApprovedOutcomes { get; set; } = new() { "approved", "a -", "b -", "accepted" };
    public int MaxPages { get; set; } = 10;
}

/// <summary>Header texts per logical column (any of them; matched case/space-insensitively, sort arrows ignored).</summary>
public sealed class WorkflowColumns
{
    public List<string> DocumentNo { get; set; } = new() { "Document No", "Document Number", "Doc No" };
    public List<string> DocumentRevision { get; set; } = new() { "Document Revision", "Revision", "Rev" };
    public List<string> DocumentVersion { get; set; } = new() { "Document Version", "Version", "Ver" };
    public List<string> DocumentTitle { get; set; } = new() { "Document Title", "Title" };
    public List<string> StepName { get; set; } = new() { "Step Name", "Step" };
    public List<string> Action { get; set; } = new() { "Action" };
    public List<string> AssignedTo { get; set; } = new() { "Assigned To", "Reviewer", "Assignee" };
    public List<string> DateIn { get; set; } = new() { "Date In", "Date Received" };
    public List<string> DateDue { get; set; } = new() { "Date Due", "Due Date" };
    public List<string> OriginalDueDate { get; set; } = new() { "Original Due Date", "Original Due" };
    public List<string> DateCompleted { get; set; } = new() { "Date Completed", "Completed" };
    public List<string> StepStatus { get; set; } = new() { "Step Status", "Status" };
    public List<string> StepOutcome { get; set; } = new() { "Step Outcome", "Outcome", "Review Outcome" };
    public List<string> FileName { get; set; } = new() { "File Name", "Filename", "File" };
    public List<string> WorkflowNo { get; set; } = new() { "Workflow No", "Workflow Number" };
    public List<string> WorkflowName { get; set; } = new() { "Workflow Name" };
}

public sealed class DocumentRegisterConfig
{
    public string SearchPath { get; set; } = "/DocumentRegister/Search?projectId={projectId}";
    public string FrameSelector { get; set; } = "";
    /// <summary>Document number box. Several numbers are searched one by one (or joined with <see cref="DocNoSeparator"/> if the site supports OR).</summary>
    public string DocNoInput { get; set; } = "input[name='docno'], input#docNo";
    public string DocNoSeparator { get; set; } = "";
    public string DateFromInput { get; set; } = "input[name='dateFrom'], input#dateFrom";
    public string DateToInput { get; set; } = "input[name='dateTo'], input#dateTo";
    /// <summary>.NET format typed into the date boxes.</summary>
    public string DateInputFormat { get; set; } = "dd/MM/yyyy";
    public string DisciplineSelect { get; set; } = "select[name='discipline'], select#discipline";
    public string DocTypeSelect { get; set; } = "select[name='docType'], select#docType";
    public string GroupInput { get; set; } = "input[name='group'], input#group, select#group";
    public string SearchButton { get; set; } = "button:has-text('Search'), #searchButton";
    public string ResultsTable { get; set; } = "table#docTable, table.searchResults, table[role='grid']";
    public string NoResultsText { get; set; } = "No results";
    public string NextPageButton { get; set; } = "a.next:not(.disabled), button[aria-label='Next page']:not([disabled])";
    /// <summary>Checkbox in a result row (scoped to the row).</summary>
    public string RowCheckbox { get; set; } = "input[type='checkbox']";
    /// <summary>"SelectAndDownload": tick the row and press <see cref="DownloadButton"/>; "RowLink": click <see cref="RowDownloadLink"/> in the row.</summary>
    public string DownloadMode { get; set; } = "SelectAndDownload";
    public string DownloadButton { get; set; } = "button:has-text('Download'), #downloadButton";
    /// <summary>Optional confirmation in the download dialog ("Download" / "Download files only").</summary>
    public string DownloadConfirmButton { get; set; } = "";
    public string RowDownloadLink { get; set; } = "a.download, a[title='Download']";
    public DocumentColumns Columns { get; set; } = new();
    /// <summary>Document number pattern used to read numbers out of pasted text / Excel / file names.</summary>
    public string DocNumberRegex { get; set; } = @"[A-Z0-9]{2,}(?:-[A-Z0-9]+){2,}";
    public int MaxPages { get; set; } = 20;
}

public sealed class DocumentColumns
{
    public List<string> DocumentNo { get; set; } = new() { "Document No", "Document Number", "Doc No" };
    public List<string> Revision { get; set; } = new() { "Revision", "Rev" };
    public List<string> Version { get; set; } = new() { "Version", "Ver" };
    public List<string> Title { get; set; } = new() { "Title", "Document Title" };
    public List<string> Type { get; set; } = new() { "Type", "Document Type" };
    public List<string> Discipline { get; set; } = new() { "Discipline" };
    public List<string> Date { get; set; } = new() { "Date Modified", "Date", "Date Created", "Revision Date" };
    public List<string> Status { get; set; } = new() { "Status", "Review Status" };
    public List<string> FileName { get; set; } = new() { "File Name", "Filename", "File" };
    public List<string> Group { get; set; } = new() { "Group", "Category", "Package" };
}

public sealed class FolderConfig
{
    /// <summary>WIR downloads (shared partition). Sub-folders per year-month are created below.</summary>
    public string WirFolder { get; set; } = "";
    public string MirFolder { get; set; } = "";
    /// <summary>Anything that is neither WIR nor MIR.</summary>
    public string OtherFolder { get; set; } = "";
    /// <summary>Workflow screenshots (attached to invoice revisions).</summary>
    public string ScreenshotFolder { get; set; } = @"%LOCALAPPDATA%\Raffaello\aconex-screenshots";
    /// <summary>"{type}/{yyyy-MM}" style sub-folder template, or empty for flat folders.</summary>
    public string SubFolderTemplate { get; set; } = "{yyyy-MM}";
    /// <summary>Document types counted as WIR / MIR (matched against the Type column or the number).</summary>
    public List<string> WirTypes { get; set; } = new() { "WIR", "Work Inspection Request", "Inspection Request" };
    public List<string> MirTypes { get; set; } = new() { "MIR", "Material Inspection Request", "Material Inspection" };
}
