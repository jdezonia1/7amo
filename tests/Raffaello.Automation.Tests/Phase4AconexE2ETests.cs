using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;

namespace Raffaello.Automation.Tests;

internal sealed class MemoryVault : ICredentialVault
{
    private (string, string)? _c;
    public MemoryVault(string? u = null, string? p = null) { if (u != null) _c = (u, p ?? ""); }
    public bool IsSupported => true;
    public bool HasCredential => _c != null;
    public void Save(string userName, string password) => _c = (userName, password);
    public (string User, string Password)? Load() => _c;
    public void Clear() => _c = null;
}

/// <summary>Real Playwright runs (headless Chromium) against <see cref="MockAconexServer"/>.</summary>
public sealed class Phase4AconexE2ETests : IDisposable
{
    private readonly MockAconexServer _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "raff-p4-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAconexStore _store;
    private static readonly DateTime Today = new(2026, 10, 2);

    public Phase4AconexE2ETests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteAconexStore(Path.Combine(_root, "data.db"), "tester", "TEST-PC");
        _store.EnsureSchema();
    }

    private AconexConfig Cfg(string profile = "profile")
    {
        var c = _server.Config(Path.Combine(_root, profile), _root);
        BrowserEnv.Apply(c);
        return c;
    }

    private PlaywrightAconexClient Client(AconexConfig cfg, ICredentialVault? vault = null) =>
        new(cfg, vault ?? new MemoryVault(MockAconexServer.UserName, MockAconexServer.Password), () => Today);

    [SkippableFact]
    public async Task Workflow_lookup_logs_in_reads_steps_and_saves_screenshots()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg();
        await using var client = Client(cfg);
        var r = await client.LookupWorkflowAsync("wf-000123", Path.Combine(_root, "shots"));

        Assert.Equal(1, _server.LoginCount);
        Assert.Equal("Electrical_Invoice_Approval", r.WorkflowName);
        Assert.Equal(5, r.Steps.Count);
        Assert.Equal("Procurement Director", r.Steps[0].StepName);
        Assert.Equal("A - Approved", r.Steps[0].StepOutcome);
        Assert.Equal(new DateTime(2026, 5, 18), r.Steps[0].DateIn);
        Assert.Equal("Mr Reviewer Two - Demo; Ms Reviewer Three - Demo; Apps Support - Demo", r.Steps[1].AssignedTo);
        Assert.Equal("DEMO-PRJ-SUB-ELE-001-2026", r.DocumentNo);
        Assert.Equal("Finance Manager", r.CurrentStep);
        Assert.Contains("Mr Reviewer Six - Demo", r.WithWhom);
        Assert.Equal(WorkflowStates.Overdue, r.State);
        Assert.True(r.IsOverdue);
        Assert.Equal(new DateTime(2026, 6, 13), r.DateDue);
        Assert.Equal((Today - new DateTime(2026, 6, 13)).Days, r.DaysOverdue);
        Assert.True(File.Exists(r.PageScreenshotPath));
        Assert.True(File.Exists(r.TableScreenshotPath));
        Assert.True(new FileInfo(r.TableScreenshotPath).Length > 1000);
        Assert.Empty(r.MissingColumns);
    }

    [SkippableFact]
    public async Task Persistent_profile_keeps_the_session_so_the_second_run_needs_no_login()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg("shared-profile");
        await using (var first = Client(cfg)) await first.LookupWorkflowAsync("WF-000456", Path.Combine(_root, "shots"));
        Assert.Equal(1, _server.LoginCount);

        // no stored credential this time: only the profile's session cookie can get us in
        await using var second = Client(cfg, new MemoryVault());
        var r = await second.LookupWorkflowAsync("WF-000456", Path.Combine(_root, "shots"));
        Assert.Equal(1, _server.LoginCount);
        Assert.Equal(WorkflowStates.InProgress, r.State);
        Assert.Equal("Procurement Director", r.CurrentStep);
    }

    [SkippableFact]
    public async Task Headless_without_session_or_credential_asks_for_a_visible_login()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        await using var client = Client(Cfg("fresh"), new MemoryVault());
        var ex = await Assert.ThrowsAsync<AconexLoginRequiredException>(() => client.EnsureLoggedInAsync());
        Assert.Contains("visible", ex.Message);
    }

    [SkippableFact]
    public async Task Unknown_workflow_is_not_found()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        await using var client = Client(Cfg());
        var r = await client.LookupWorkflowAsync("WF-999999", Path.Combine(_root, "shots"));
        Assert.Equal(WorkflowStates.NotFound, r.State);
        Assert.Empty(r.Steps);
    }

    [SkippableFact]
    public async Task Tracker_records_history_and_attaches_screenshots_to_the_invoice_revision()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg();
        await using var client = Client(cfg);
        var tracker = new WorkflowTracker(client, _store, cfg);
        var inv = new SubInvoice { Id = 42, Subcontractor = "DEMO SUB", ContractNo = "SUB-DEMO-001", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Submitted, CreatedAt = new DateTime(2026, 5, 17), SubmittedAt = new DateTime(2026, 5, 18) };
        _store.Link(inv.Id, "WF-000123");

        var first = await tracker.RefreshAllAsync(new[] { inv });
        Assert.Single(first);
        Assert.Empty(_store.History("WF-000123"));
        Assert.Equal(2, _store.Attachments(42).Count);

        // the finance manager approves, a new step appears
        var wf = _server.Workflows[0];
        wf.Steps[4] = wf.Steps[4] with { Status = "Completed", Outcome = "A - Approved", Completed = "01/10/2026" };
        wf.Steps.Add(new MockStep("Payment Release", "Mr Reviewer Seven - Demo", "01/10/2026", "05/10/2026", "05/10/2026", "", "Pending", "Pending"));
        var second = await tracker.LookupAsync("WF-000123", inv.Id);

        Assert.Equal("Payment Release", second.CurrentStep);
        Assert.Equal(WorkflowStates.InProgress, second.State);
        var history = _store.History("WF-000123");
        Assert.Contains(history, h => h.StepName == "Finance Manager" && h.Field == "STATUS" && h.NewValue == "Completed");
        Assert.Contains(history, h => h.StepName == "Finance Manager" && h.Field == "OUTCOME" && h.NewValue == "A - Approved");
        Assert.Contains(history, h => h.StepName == "Payment Release" && h.Field == "NEW STEP");
        Assert.Equal(4, _store.Attachments(42).Count);

        var board = StatusBoard.Build(new[] { inv }, _store.ActiveLinks(), _store.LatestChecks(), Today);
        var row = Assert.Single(board);
        Assert.Equal("Payment Release", row.CurrentStep);
        Assert.Equal("Mr Reviewer Seven - Demo", row.WithWhom);

        var xlsx = Path.Combine(_root, "board.xlsx");
        Raffaello.Core.Export.ExcelExporter.Export(xlsx, StatusBoard.ToSheet(board, DateTime.Now), StatusBoard.HistorySheet(history));
        var pdf = Path.Combine(_root, "board.pdf");
        PdfTables.Export(pdf, "test", new[] { StatusBoard.ToSheet(board, DateTime.Now) }, new[] { new PdfTables.PdfImage("WF-000123", second.TableScreenshotPath) });
        Assert.True(new FileInfo(xlsx).Length > 0);
        Assert.True(new FileInfo(pdf).Length > 1000);
    }

    [SkippableFact]
    public async Task Download_by_numbers_routes_wir_mir_extracts_zip_registers_sha_and_skips_known_revisions()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg();
        await using var client = Client(cfg);
        var svc = new DocumentDownloadService(client, _store, cfg);
        var numbers = DocumentRegister.ParseNumberList("DEMO-WIR-EL-000101\nDEMO-WIR-EL-000102, DEMO-MIR-EL-000202\nnot a number\nDEMO-WIR-EL-777777");
        Assert.Equal(4, numbers.Count);
        var query = new DocumentQuery { DocumentNumbers = numbers };

        var job = await svc.PlanAsync(query);
        Assert.Equal(3, job.Total);
        var done = await svc.RunAsync(job.Id);
        Assert.Equal(JobStates.Done, done.State);
        Assert.Equal(3, done.Done);

        var regs = _store.Downloads();
        Assert.Equal(4, regs.Count); // 000102 came as a ZIP bundle with 2 files
        Assert.All(regs, r => Assert.Matches("^[0-9a-f]{64}$", r.Sha256));
        Assert.All(regs, r => Assert.True(File.Exists(r.Path)));
        Assert.Equal(2, regs.Count(r => r.DocumentNo == "DEMO-WIR-EL-000102" && r.FromZip));
        var mir = Assert.Single(regs, r => r.DocumentNo == "DEMO-MIR-EL-000202");
        Assert.StartsWith(Path.Combine(_root, "MIR"), mir.Path);
        Assert.Equal("2", mir.Revision);
        Assert.Contains(Path.Combine("MIR", "2026-09"), mir.Path);
        Assert.StartsWith(Path.Combine(_root, "WIR"), regs.First(r => r.DocumentNo == "DEMO-WIR-EL-000101").Path);
        Assert.Contains("DEMO-MIR-EL-000202 rev 2", File.ReadAllText(mir.Path));
        Assert.Empty(Directory.GetFiles(_root, "*.zip", SearchOption.AllDirectories));

        // second run: everything already there
        var downloadsBefore = _server.DownloadCount;
        var again = await svc.PlanAsync(query);
        Assert.Equal(3, again.Skipped);
        var againDone = await svc.RunAsync(again.Id);
        Assert.Equal(downloadsBefore, _server.DownloadCount);
        Assert.Equal(3, againDone.Skipped);
        Assert.Equal(4, _store.Downloads().Count);
    }

    [SkippableFact]
    public async Task Download_by_date_range_and_discipline_pages_through_results_and_resumes_after_a_stop()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg();
        await using var client = Client(cfg);
        var svc = new DocumentDownloadService(client, _store, cfg);
        var query = new DocumentQuery { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), Discipline = "Electrical", DocType = "WIR" };

        var job = await svc.PlanAsync(query);
        Assert.Equal(5, job.Total); // 000101..000105, across two result pages (page size 4); ME and the August WIR are out
        var items = _store.Queue(job.Id);
        Assert.DoesNotContain(items, i => i.DocumentNo == "DEMO-WIR-EL-000099" || i.DocumentNo.Contains("-ME-"));

        // stop after the second document
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<(int Done, int Total, string Doc)>(p => { if (p.Done >= 2) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.RunAsync(job.Id, progress, ct: cts.Token));
        var paused = _store.Jobs().First(j => j.Id == job.Id);
        Assert.Equal(JobStates.Paused, paused.State);
        Assert.Equal(job.Id, _store.ResumableJob()?.Id);
        var doneSoFar = _store.Queue(job.Id).Count(i => i.State == JobStates.Done);
        Assert.InRange(doneSoFar, 1, 4);

        var resumed = await svc.RunAsync(job.Id, new SyncProgress<(int, int, string)>(_ => { }));
        Assert.Equal(JobStates.Done, resumed.State);
        Assert.Equal(5, _store.Queue(job.Id).Count(i => i.State == JobStates.Done));
        Assert.Equal(6, _store.Downloads().Count); // 000102 = 2 files
        Assert.Null(_store.ResumableJob());
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) => _a(value);
}
