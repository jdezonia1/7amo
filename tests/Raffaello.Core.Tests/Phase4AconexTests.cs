using System.IO.Compression;
using System.Text;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

public class Phase4AconexTests
{
    private static readonly AconexConfig Cfg = new();
    private static readonly DateTime Today = new(2026, 10, 2);

    // A workflow table as Aconex renders it, with the columns shuffled, an extra column, sort arrows and a second workflow.
    private const string Html = @"<html><body><p>1 - 6 of 6 results</p>
<table id='resultsTable'>
<thead><tr><th></th><th>Step Status</th><th>Document No</th><th>Step Name</th><th>Assigned To</th><th>Date In</th><th>Date Due &#9650;</th>
<th>Original Due Date</th><th>Date Completed</th><th>Step Outcome</th><th>Document Revision</th><th>Document Version</th><th>Document Title</th><th>Action</th><th>File Name</th><th>Confidential</th></tr></thead>
<tbody>
<tr class='group'><td colspan='16'>Initiator Tools | Workflow No.: WF-000777 Name: Electrical_Invoice_Approval</td></tr>
<tr><td><input type=checkbox></td><td>Completed</td><td>DEMO-INV-001</td><td>Procurement Director</td><td>Mr A - Demo</td><td>18/05/2026</td><td>19/05/2026</td><td>23/05/2026</td><td>18/05/2026</td><td>A - Approved</td><td>00</td><td>3</td><td>Invoice 1</td><td></td><td><a href='/f/1'>inv.pdf</a></td><td>No</td></tr>
<tr><td></td><td>Completed</td><td>DEMO-INV-001</td><td>PMO Director</td><td>Mr B - Demo<br>Ms C - Demo</td><td>18/05/2026</td><td>19/05/2026</td><td>24/05/2026</td><td>07/06/2026</td><td>A - Approved</td><td>00</td><td>3</td><td>Invoice 1</td><td></td><td>inv.pdf</td><td>No</td></tr>
<tr><td></td><td><span class='red'>Overdue</span></td><td>DEMO-INV-001</td><td>Top Management</td><td>Mr D - Demo<br/>Apps Support - Demo</td><td>10/06/2026</td><td>13/06/2026</td><td>27/05/2026</td><td></td><td>Pending</td><td>00</td><td>3</td><td>Invoice 1</td><td></td><td>inv.pdf</td><td>No</td></tr>
<tr class='group'><td colspan='16'>Workflow No.: WF-000888 Name: Other</td></tr>
<tr><td></td><td>Pending</td><td>DEMO-INV-002</td><td>Somebody Else</td><td>Mr E</td><td>10/06/2026</td><td>13/06/2026</td><td></td><td></td><td>Pending</td><td>00</td><td>1</td><td>x</td><td></td><td>y.pdf</td><td>No</td></tr>
</tbody></table></body></html>";

    [Fact]
    public void Column_lookup_is_by_header_text_not_position()
    {
        var map = new ColumnMap(new[] { "", "Date Due ▲", "Step  Status", "Original Due Date", "STEP NAME" });
        Assert.Equal(1, map.Find(new[] { "Date Due", "Due Date" }));
        Assert.Equal(2, map.Find(new[] { "Step Status", "Status" }));
        Assert.Equal(3, map.Find(new[] { "Original Due Date" }));
        Assert.Equal(4, map.Find(new[] { "Step Name" }));
        Assert.Equal(-1, map.Find(new[] { "Assigned To" }));
    }

    [Fact]
    public void Workflow_table_parses_with_shuffled_columns_and_finds_current_step()
    {
        var table = HtmlTableParser.ParseFirst(Html, "resultsTable")!;
        var r = WorkflowParser.Parse("wf-000777", table, Cfg, Today);

        Assert.Equal("Electrical_Invoice_Approval", r.WorkflowName);
        Assert.Equal(3, r.Steps.Count); // WF-000888's row is ignored
        Assert.Equal("Mr B - Demo; Ms C - Demo", r.Steps[1].AssignedTo);
        Assert.Equal(new DateTime(2026, 6, 7), r.Steps[1].DateCompleted);
        Assert.Equal(StepStatuses.Overdue, r.Steps[2].StepStatus);
        Assert.Equal("inv.pdf", r.Steps[0].FileName);
        Assert.Equal("00", r.Steps[0].DocumentRevision);
        Assert.Equal("3", r.Steps[0].DocumentVersion);
        Assert.Equal("Top Management", r.CurrentStep);
        Assert.Equal("Mr D - Demo; Apps Support - Demo", r.WithWhom);
        Assert.Equal(WorkflowStates.Overdue, r.State);
        Assert.Equal(111, r.DaysOverdue);
        Assert.Equal("DEMO-INV-001", r.DocumentNo);
        Assert.Contains("Confidential", r.UnknownHeaders);
        Assert.Empty(r.MissingColumns);
        Assert.Contains("OVERDUE", r.Summary);
    }

    [Fact]
    public void Missing_step_name_column_says_which_config_key_to_fix()
    {
        var t = new RawTable { Headers = { "Foo", "Bar" }, Rows = { new RawRow { Cells = { "1", "2" } } } };
        var ex = Assert.Throws<AconexPageChangedException>(() => WorkflowParser.Parse("WF-1", t, Cfg, Today));
        Assert.Contains("Workflows.Columns.StepName", ex.Message);
    }

    private static WorkflowLookupResult Analyze(params WorkflowStep[] steps)
    {
        var r = new WorkflowLookupResult { WorkflowNo = "WF-1", Steps = steps.ToList() };
        WorkflowParser.Analyze(r, Cfg, Today);
        return r;
    }

    private static WorkflowStep S(int o, string name, string status, string outcome, DateTime? due = null, DateTime? done = null, DateTime? dateIn = null) =>
        new() { Order = o, StepName = name, StepStatus = WorkflowParser.NormalizeStatus(status), StepStatusText = status, StepOutcome = outcome, DateDue = due, DateCompleted = done, DateIn = dateIn, AssignedTo = name + " person" };

    [Fact]
    public void Analyzer_reports_approved_rejected_in_progress_and_overdue()
    {
        var approved = Analyze(S(1, "A", "Completed", "A - Approved", done: Today.AddDays(-3)), S(2, "B", "Completed", "B - Approved with comments", done: Today.AddDays(-1)));
        Assert.Equal(WorkflowStates.Approved, approved.State);
        Assert.Equal("(complete)", approved.CurrentStep);

        var rejected = Analyze(S(1, "A", "Completed", "C - Revise and Resubmit", done: Today.AddDays(-3)));
        Assert.Equal(WorkflowStates.Rejected, rejected.State);
        Assert.Equal("C - Revise and Resubmit", rejected.Outcome);

        var running = Analyze(S(1, "A", "Completed", "A - Approved", done: Today.AddDays(-3)), S(2, "B", "Pending", "", due: Today.AddDays(2), dateIn: Today.AddDays(-1)));
        Assert.Equal(WorkflowStates.InProgress, running.State);
        Assert.Equal("B", running.CurrentStep);
        Assert.Equal("B person", running.WithWhom);
        Assert.False(running.IsOverdue);

        var late = Analyze(S(1, "B", "Pending", "", due: Today.AddDays(-4)));
        Assert.Equal(WorkflowStates.Overdue, late.State);
        Assert.Equal(4, late.DaysOverdue);

        Assert.Equal(WorkflowStates.NotFound, Analyze().State);
    }

    [Fact]
    public void Dates_are_read_day_first_with_optional_time_and_zone()
    {
        Assert.Equal(new DateTime(2026, 5, 18), AconexDates.Parse("18/05/2026", Cfg.DateFormats));
        Assert.Equal(new DateTime(2026, 5, 18, 10, 22, 0), AconexDates.Parse("18/05/2026 10:22 AST", Cfg.DateFormats));
        Assert.Equal(new DateTime(2026, 5, 13), AconexDates.Parse("13-May-2026", Cfg.DateFormats));
        Assert.Equal(new DateTime(2026, 5, 13), AconexDates.Parse("Due 13/05/2026 (late)", Cfg.DateFormats));
        Assert.Null(AconexDates.Parse("", Cfg.DateFormats));
        Assert.Null(AconexDates.Parse("n/a", Cfg.DateFormats));
    }

    [Fact]
    public void Register_rows_numbers_and_folders()
    {
        var html = @"<table id='docTable'><tr><th></th><th>Title</th><th>Document No ▼</th><th>Type</th><th>Rev</th><th>Discipline</th><th>Date Modified</th><th>Category</th></tr>
<tr><td><input type=checkbox></td><td>Cable tray L2</td><td><a href='#'>DEMO-WIR-EL-000101</a></td><td>WIR</td><td>1</td><td>Electrical</td><td>03/09/2026</td><td>HOTEL</td></tr>
<tr><td></td><td>Cables delivery</td><td>DEMO-MIR-EL-000201</td><td>Material Inspection Request</td><td>0</td><td>Electrical</td><td>12/08/2026</td><td>BRANDED</td></tr></table>";
        var hits = DocumentRegister.ParseResults(HtmlTableParser.ParseFirst(html)!, Cfg);
        Assert.Equal(2, hits.Count);
        Assert.Equal("DEMO-WIR-EL-000101", hits[0].DocumentNo);
        Assert.Equal("1", hits[0].Revision);
        Assert.Equal("HOTEL", hits[0].Group);
        Assert.Equal(new DateTime(2026, 9, 3), hits[0].Date);
        Assert.Equal("DEMO-WIR-EL-000101|1", hits[0].RevisionKey);

        var f = new FolderConfig { WirFolder = "/share/WIR", MirFolder = "/share/MIR", OtherFolder = "/share/OTHER", SubFolderTemplate = "{yyyy-MM}" };
        Assert.Equal("WIR", DocumentRegister.Kind(hits[0], f));
        Assert.Equal("MIR", DocumentRegister.Kind(hits[1], f));
        Assert.Equal(Path.Combine("/share/WIR", "2026-09"), DocumentRegister.TargetFolder(hits[0], f));
        Assert.Equal(Path.Combine("/share/MIR", "2026-08"), DocumentRegister.TargetFolder(hits[1], f));
        Assert.Equal("OTHER", DocumentRegister.Kind(hits[0] with { Type = "Shop Drawing", DocumentNo = "DEMO-SDW-1" }, f));
        Assert.Throws<InvalidOperationException>(() => DocumentRegister.TargetFolder(hits[0], new FolderConfig()));

        var nums = DocumentRegister.ParseNumberList("demo-wir-el-000101\r\n  DEMO-WIR-EL-000101 ; DEMO-MIR-EL-000201\tjunk\nRef: DEMO-WIR-EL-000102 (rev 1)");
        Assert.Equal(new[] { "DEMO-WIR-EL-000101", "DEMO-MIR-EL-000201", "DEMO-WIR-EL-000102" }, nums);
    }

    [Fact]
    public void Number_list_is_read_from_an_excel_column()
    {
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "nums.xlsx");
        Raffaello.Core.Export.ExcelExporter.Export(path, new Raffaello.Core.Export.ExportSheet
        {
            Name = "X", Columns = new() { new("DOCUMENT NO"), new("TITLE") },
            Rows = new() { new object?[] { "DEMO-WIR-EL-000101", "a" }, new object?[] { "DEMO-WIR-EL-000102", "b" } },
        });
        Assert.Equal(new[] { "DEMO-WIR-EL-000101", "DEMO-WIR-EL-000102" }, DocumentRegister.ReadNumbersFromFile(path));
    }

    [Fact]
    public void History_diff_lists_status_outcome_assignee_and_new_steps()
    {
        var before = new List<WorkflowStep> { S(1, "A", "Completed", "A - Approved"), S(2, "B", "Pending", "") };
        var after = new List<WorkflowStep> { S(1, "A", "Completed", "A - Approved"), S(2, "B", "Completed", "A - Approved", done: Today) with { AssignedTo = "someone new" }, S(3, "C", "Pending", "") };
        var d = WorkflowHistory.Diff("WF-1", before, after, Today);
        Assert.Contains(d, c => c.StepName == "B" && c.Field == "STATUS" && c.OldValue == "Pending" && c.NewValue == "Completed");
        Assert.Contains(d, c => c.StepName == "B" && c.Field == "OUTCOME");
        Assert.Contains(d, c => c.StepName == "B" && c.Field == "ASSIGNED TO" && c.NewValue == "someone new");
        Assert.Contains(d, c => c.StepName == "B" && c.Field == "COMPLETED");
        Assert.Contains(d, c => c.StepName == "C" && c.Field == "NEW STEP");
        Assert.DoesNotContain(d, c => c.StepName == "A");
    }

    [Fact]
    public void Status_board_uses_links_hides_approved_and_sorts_overdue_first()
    {
        var invs = new[]
        {
            new SubInvoice { Id = 1, Subcontractor = "SUB A", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Submitted, AconexWorkflowNo = "WF-OLD", SubmittedAt = Today.AddDays(-10) },
            new SubInvoice { Id = 2, Subcontractor = "SUB B", InvoiceNo = 2, Revision = 1, Status = SubInvoiceStatus.Submitted, AconexWorkflowNo = "WF-000002", SubmittedAt = Today.AddDays(-30) },
            new SubInvoice { Id = 3, Subcontractor = "SUB C", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Approved, AconexWorkflowNo = "WF-000003" },
            new SubInvoice { Id = 4, Subcontractor = "SUB D", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Draft },
        };
        var links = new[] { new AconexWorkflowLink { Id = 1, SubInvoiceId = 1, WorkflowNo = "WF-000001", Active = true, LinkedAt = Today } };
        var checks = new Dictionary<string, AconexWorkflowCheck>
        {
            ["WF-000001"] = new() { WorkflowNo = "WF-000001", State = WorkflowStates.InProgress, CurrentStep = "PMO", WithWhom = "X", DateDue = Today.AddDays(3), CheckedAt = Today },
            ["WF-000002"] = new() { WorkflowNo = "WF-000002", State = WorkflowStates.Overdue, CurrentStep = "CFO", WithWhom = "Y", DateDue = Today.AddDays(-5), IsOverdue = true, CheckedAt = Today },
        };
        var board = StatusBoard.Build(invs, links, checks, Today);
        Assert.Equal(2, board.Count);
        Assert.Equal("WF-000002", board[0].WorkflowNo);
        Assert.True(board[0].IsOverdue);
        Assert.Equal(5, board[0].DaysOverdue);
        Assert.Equal("OVER", board[0].Tag);
        Assert.Equal("WF-000001", board[1].WorkflowNo); // link wins over the typed number
        Assert.Equal("PMO", board[1].CurrentStep);
        Assert.Equal(10, board[1].DaysInWorkflow);
        Assert.Equal(4, StatusBoard.Build(invs, links, checks, Today, includeClosed: true).Count);
        var sheet = StatusBoard.ToSheet(board, Today);
        Assert.Equal(2, sheet.Rows.Count);
    }

    [Fact]
    public void Daily_schedule_fires_once_per_day_after_the_time()
    {
        var at = DailySchedule.ParseTime("08:30")!.Value;
        Assert.Null(DailySchedule.ParseTime("25:00"));
        Assert.Null(DailySchedule.ParseTime(""));
        var day = new DateTime(2026, 10, 2);
        Assert.False(DailySchedule.IsDue(day.AddHours(8), at, null));
        Assert.True(DailySchedule.IsDue(day.AddHours(9), at, null));
        Assert.True(DailySchedule.IsDue(day.AddHours(9), at, day.AddDays(-1).AddHours(9)));
        Assert.False(DailySchedule.IsDue(day.AddHours(10), at, day.AddHours(9)));
        Assert.Equal(day.AddDays(1).Add(at), DailySchedule.NextRun(day.AddHours(10), at, day.AddHours(9)));
        Assert.Equal(day.Add(at), DailySchedule.NextRun(day.AddHours(7), at, null));
    }

    [Fact]
    public void Config_round_trips_writes_defaults_and_validates()
    {
        var dir = TestData.TempDir();
        var path = Path.Combine(dir, "aconex.config.json");
        var c = AconexConfig.Load(path);
        Assert.True(File.Exists(path));
        Assert.Empty(c.Validate());
        var json = File.ReadAllText(path).Replace("\"BaseUrl\": \"https://ksa1.aconex.com\"", "// edited by hand\n  \"BaseUrl\": \"https://example.test\"");
        File.WriteAllText(path, json);
        var c2 = AconexConfig.Load(path);
        Assert.Equal("https://example.test", c2.BaseUrl);
        Assert.Equal(c.Workflows.Columns.StepName, c2.Workflows.Columns.StepName);
        c2.ProjectId = "12 34";
        Assert.Equal("https://example.test/Workflow/SearchWorkflows?projectId=12%2034", c2.Url(c2.Workflows.SearchPath));
        Assert.Equal("https://other/x", c2.Url("https://other/x"));
        c2.BaseUrl = "nope";
        c2.Workflows.Columns.StepName.Clear();
        Assert.Equal(2, c2.Validate().Count);
        Assert.False(string.IsNullOrWhiteSpace(new AconexConfig().ResolvedProfileDir));
        Assert.DoesNotContain("%", new AconexConfig().ResolvedProfileDir);
    }

    private static SqliteAconexStore NewStore(out Raffaello.Core.Data.Db db)
    {
        db = TestData.NewDb();
        var s = new SqliteAconexStore(db.Path, "tester", "TESTPC");
        s.EnsureSchema();
        return s;
    }

    [Fact]
    public void Store_lives_in_the_project_file_links_checks_history_and_attachments()
    {
        var s = NewStore(out var db);
        db.Insert(new Subcontractor { Name = "X" });
        s.EnsureSchema(); // idempotent
        Assert.Equal(1, db.Count<Subcontractor>());

        var l1 = s.Link(7, "wf-000001");
        var l2 = s.Link(7, "WF-000001");
        Assert.Equal(l1.Id, l2.Id);
        s.Link(7, "WF-000009");
        Assert.Equal("WF-000009", Assert.Single(s.ActiveLinks()).WorkflowNo);

        var shot = Path.Combine(TestData.TempDir(), "s.png");
        File.WriteAllBytes(shot, new byte[] { 1, 2, 3 });
        var r1 = new WorkflowLookupResult { WorkflowNo = "WF-000009", State = WorkflowStates.InProgress, CheckedAt = Today.AddDays(-1), Steps = { S(1, "A", "Pending", "") }, TableScreenshotPath = shot };
        s.SaveCheck(r1, 7);
        var r2 = new WorkflowLookupResult { WorkflowNo = "WF-000009", State = WorkflowStates.Approved, CheckedAt = Today, Steps = { S(1, "A", "Completed", "A - Approved") } };
        s.SaveCheck(r2, 7);
        s.SaveCheck(new WorkflowLookupResult { WorkflowNo = "WF-000009", State = WorkflowStates.Error, Error = "timeout", CheckedAt = Today.AddHours(1) }, null);

        Assert.Equal(3, s.Checks("WF-000009").Count);
        Assert.Equal(WorkflowStates.Error, s.LatestCheck("wf-000009")!.State);
        Assert.Equal(2, s.History("WF-000009").Count); // status + outcome
        var att = Assert.Single(s.Attachments(7));
        Assert.Equal(64, att.Sha256.Length);
        Assert.Single(s.LatestChecks());
        Assert.Contains(db.RecentAudit(50), a => a.Summary.Contains("WF-000009"));
    }

    [Fact]
    public void Jobs_skip_known_revisions_and_can_be_resumed()
    {
        var s = NewStore(out _);
        var file = Path.Combine(TestData.TempDir(), "a.pdf");
        File.WriteAllText(file, "x");
        s.RegisterDownload(new AconexDownload { DocumentNo = "DOC-1", Revision = "0", Path = file, Sha256 = "x", DownloadedAt = Today });
        Assert.NotNull(s.FindDownload("doc-1", "0"));
        var hits = new[] { new DocumentHit { DocumentNo = "DOC-1", Revision = "0" }, new DocumentHit { DocumentNo = "DOC-1", Revision = "1" }, new DocumentHit { DocumentNo = "DOC-2", Revision = "0" } };
        var job = s.CreateJob(new DocumentQuery { DocumentNumbers = { "DOC-1", "DOC-2" } }, hits, new HashSet<string> { "DOC-1|0" });
        Assert.Equal(3, job.Total);
        Assert.Equal(1, job.Skipped);
        var q = s.Queue(job.Id);
        Assert.Equal(new[] { JobStates.Skipped, JobStates.Pending, JobStates.Pending }, q.Select(i => i.State));
        Assert.Equal(job.Id, s.ResumableJob()!.Id);
        job.State = JobStates.Done;
        s.UpdateJob(job);
        Assert.Null(s.ResumableJob());
    }

    private sealed class FakeClient : IAconexClient
    {
        public List<DocumentHit> Hits { get; } = new();
        public int FailFirstN { get; set; }
        public int Downloads { get; private set; }
        public event Action<string>? Log;
        public Task EnsureLoggedInAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkflowLookupResult> LookupWorkflowAsync(string workflowNo, string screenshotFolder, CancellationToken ct = default) =>
            throw new InvalidOperationException("page broke");
        public Task<List<DocumentHit>> SearchDocumentsAsync(DocumentQuery query, CancellationToken ct = default) => Task.FromResult(Hits.ToList());
        public Task<string> DownloadAsync(DocumentHit hit, DocumentQuery query, string targetFolder, CancellationToken ct = default)
        {
            Downloads++;
            if (FailFirstN-- > 0) throw new IOException("network hiccup");
            Directory.CreateDirectory(targetFolder);
            var p = Path.Combine(targetFolder, hit.DocumentNo + ".pdf");
            File.WriteAllText(p, hit.DocumentNo);
            Log?.Invoke(p);
            return Task.FromResult(p);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Download_service_retries_failed_items_on_resume()
    {
        var s = NewStore(out _);
        var root = TestData.TempDir();
        var cfg = new AconexConfig { DelayBetweenDownloadsMs = 0 };
        cfg.Folders.WirFolder = Path.Combine(root, "WIR");
        cfg.Folders.OtherFolder = Path.Combine(root, "OTHER");
        var client = new FakeClient { FailFirstN = 1 };
        client.Hits.Add(new DocumentHit { DocumentNo = "X-WIR-1", Revision = "0", Type = "WIR", Date = Today });
        client.Hits.Add(new DocumentHit { DocumentNo = "X-SD-2", Revision = "0", Type = "Shop Drawing", Date = Today });
        var svc = new DocumentDownloadService(client, s, cfg);
        var job = await svc.PlanAsync(new DocumentQuery { DocType = "WIR" });
        var r1 = await svc.RunAsync(job.Id);
        Assert.Equal(JobStates.Failed, r1.State);
        Assert.Equal(1, r1.Failed);
        var r2 = await svc.RunAsync(job.Id);
        Assert.Equal(JobStates.Done, r2.State);
        Assert.Equal(2, r2.Done);
        Assert.Equal(3, client.Downloads);
        Assert.Contains(s.Downloads(), d => d.Kind == "OTHER" && d.Path.StartsWith(Path.Combine(root, "OTHER")));
    }

    [Fact]
    public async Task Tracker_turns_page_errors_into_error_checks()
    {
        var s = NewStore(out _);
        var cfg = new AconexConfig();
        cfg.Folders.ScreenshotFolder = TestData.TempDir();
        var t = new WorkflowTracker(new FakeClient(), s, cfg);
        var r = await t.LookupAsync("WF-1");
        Assert.Equal(WorkflowStates.Error, r.State);
        Assert.Equal("page broke", s.LatestCheck("WF-1")!.Error);
    }

    [Fact]
    public void Zip_bundles_are_flattened_and_unique()
    {
        var dir = TestData.TempDir();
        var zip = Path.Combine(dir, "Documents.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var n in new[] { "A/doc.pdf", "B/doc.pdf", "C/" })
            {
                var e = z.CreateEntry(n);
                if (!n.EndsWith('/')) { using var st = e.Open(); st.Write(Encoding.ASCII.GetBytes(n)); }
            }
        }
        Assert.True(ZipBundle.IsZip(zip));
        var files = ZipBundle.Extract(zip, Path.Combine(dir, "out"), deleteZip: true);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => Path.GetFileName(f.Path) == "doc (2).pdf");
        Assert.False(File.Exists(zip));
        Assert.False(ZipBundle.IsZip(files[0].Path));
    }

    [Fact]
    public void Pdf_tables_print_sheets_and_images()
    {
        var path = Path.Combine(TestData.TempDir(), "t.pdf");
        PdfTables.Export(path, "footer", new[] { StatusBoard.HistorySheet(new[] { new AconexStepChange { WorkflowNo = "WF-1", At = Today, StepName = "A", Field = "STATUS", OldValue = "Pending", NewValue = "Completed" } }) });
        Assert.StartsWith("%PDF", File.ReadAllText(path)[..4]);
    }
}
