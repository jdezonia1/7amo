using System.Net;
using System.Net.Sockets;
using System.Text;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Integrations.AconexApi;

namespace Raffaello.Core.Tests;

/// <summary>A small mock of the Oracle Aconex REST API (HttpListener on localhost) for the API client tests.</summary>
internal sealed class MockAconexApi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    public string Url { get; }
    public List<string> Requests { get; } = new();
    public int TokenCalls;
    public int FailNextWith;
    public const string Project = "1879048400";

    public MockAconexApi()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try { Handle(ctx); } catch (Exception ex) { Write(ctx, 500, "text/plain", ex.Message); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var query = ctx.Request.Url.Query;
        lock (Requests) Requests.Add(ctx.Request.HttpMethod + " " + path + Uri.UnescapeDataString(query));
        if (path == "/oauth/token")
        {
            Interlocked.Increment(ref TokenCalls);
            var auth = ctx.Request.Headers["Authorization"] ?? "";
            var ok = auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("raffaello-client:s3cret"));
            Write(ctx, ok ? 200 : 401, "application/json", ok ? """{"access_token":"tok-123","token_type":"Bearer","expires_in":3600}""" : """{"error":"invalid_client"}""");
            return;
        }
        if (ctx.Request.Headers["Authorization"] != "Bearer tok-123") { Write(ctx, 401, "application/xml", "<Error>unauthorized</Error>"); return; }
        if (ctx.Request.Headers["X-Application-Key"] != "app-key-1") { Write(ctx, 403, "application/xml", "<Error>no application key</Error>"); return; }
        if (FailNextWith > 0) { var s = FailNextWith; FailNextWith = 0; Write(ctx, s, "application/xml", "<Error>busy</Error>"); return; }

        if (path == "/api/projects")
        {
            Write(ctx, 200, "application/xml", $"""
                <ProjectResults TotalResults="2"><SearchResults>
                  <Project><ProjectId>{Project}</ProjectId><ProjectName>RAFFLES TEST</ProjectName><ProjectShortName>RAF</ProjectShortName></Project>
                  <Project><ProjectId>999</ProjectId><ProjectName>OTHER</ProjectName></Project>
                </SearchResults></ProjectResults>
                """);
            return;
        }
        if (path == $"/api/projects/{Project}/workflows/search")
        {
            Write(ctx, 200, "application/xml", """
                <WorkflowSearch TotalResults="3"><SearchResults>
                  <Workflow WorkflowId="1"><WorkflowNumber>WF-008795</WorkflowNumber><WorkflowName>Electrical_PR_PO_Approval</WorkflowName><StepName>QS review</StepName>
                    <Assignees><Assignee><Name>Mohamed</Name><OrganizationName>MOBCO</OrganizationName></Assignee></Assignees>
                    <DateIn>2026-09-20T08:00:00.000Z</DateIn><DateDue>2026-09-22T08:00:00.000Z</DateDue><DateCompleted>2026-09-21T10:00:00.000Z</DateCompleted>
                    <StepStatus>Completed</StepStatus><StepOutcome>Approved</StepOutcome><DocumentNumber>INV-SUB-01</DocumentNumber><DocumentRevision>0</DocumentRevision></Workflow>
                  <Workflow WorkflowId="2"><WorkflowNumber>WF-008795</WorkflowNumber><WorkflowName>Electrical_PR_PO_Approval</WorkflowName><StepName>Head office</StepName>
                    <Assignees><Assignee><Name>Finance</Name><OrganizationName>MOBCO HO</OrganizationName></Assignee></Assignees>
                    <DateIn>2026-09-21T10:00:00.000Z</DateIn><DateDue>2026-09-25T10:00:00.000Z</DateDue>
                    <StepStatus>In Progress</StepStatus><StepOutcome></StepOutcome><DocumentNumber>INV-SUB-01</DocumentNumber></Workflow>
                  <Workflow WorkflowId="3"><WorkflowNumber>WF-000001</WorkflowNumber><StepName>Other workflow</StepName><StepStatus>Completed</StepStatus></Workflow>
                </SearchResults></WorkflowSearch>
                """);
            return;
        }
        if (path == $"/api/projects/{Project}/register")
        {
            var page = ctx.Request.QueryString["page_number"] ?? "1";
            var q = ctx.Request.QueryString["search_query"] ?? "";
            var docs = page == "1"
                ? """<Document DocumentId="111"><DocumentNumber>WIR-EL-000123</DocumentNumber><Revision>0</Revision><Title>WIR L2 lighting</Title><DocumentType>WIR</DocumentType><Discipline>Electrical</Discipline><Filename>WIR-EL-000123.pdf</Filename><DateRegistered>2026-09-01T00:00:00.000Z</DateRegistered></Document>"""
                : """<Document DocumentId="222"><DocumentNumber>WIR-EL-000124</DocumentNumber><Revision>A</Revision><Title>WIR L3 power</Title><DocumentType>WIR</DocumentType><Discipline>Electrical</Discipline><Filename>WIR-EL-000124.pdf</Filename></Document>""";
            if (q.Contains("NOTHING")) docs = "";
            Write(ctx, 200, "application/xml", $"""<RegisterSearch CurrentPage="{page}" PageSize="1" TotalPages="2" TotalResults="2"><SearchResults>{docs}</SearchResults></RegisterSearch>""");
            return;
        }
        if (path.StartsWith($"/api/projects/{Project}/register/", StringComparison.Ordinal) && path.EndsWith("/file", StringComparison.Ordinal))
        {
            var id = path.Split('/')[^2];
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"doc-{id}.pdf\"";
            Write(ctx, 200, "application/pdf", "%PDF-1.4 mock " + id);
            return;
        }
        Write(ctx, 404, "application/xml", "<Error>not found</Error>");
    }

    private static void Write(HttpListenerContext ctx, int status, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = b.Length;
        ctx.Response.OutputStream.Write(b);
        ctx.Response.Close();
    }

    public void Dispose() { _stop.Cancel(); try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { } }
}

/// <summary>[trust] Aconex REST API client behind IAconexClient, against a mock server.</summary>
public sealed class TrustAconexApiTests
{
    private static AconexApiConfig Config(MockAconexApi m) => new()
    {
        Enabled = true, BaseUrl = m.Url, ProjectId = MockAconexApi.Project, AuthMode = AconexAuthModes.OAuthClientCredentials,
        TokenUrl = m.Url + "oauth/token", ClientId = "raffaello-client", ApplicationKey = "app-key-1", DelayBetweenCallsMs = 0, PageSize = 1,
    };

    [Fact]
    public async Task Signs_in_lists_projects_and_looks_up_a_workflow()
    {
        using var m = new MockAconexApi();
        Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", "s3cret");
        try
        {
            await using var c = new AconexApiClient(Config(m)) { Today = () => new DateTime(2026, 9, 23) };
            await c.EnsureLoggedInAsync();
            Assert.Equal(2, (await c.ProjectsAsync()).Count);
            var folder = TestData.TempDir();
            var r = await c.LookupWorkflowAsync("wf-008795", folder);
            Assert.Equal("Electrical_PR_PO_Approval", r.WorkflowName);
            Assert.Equal(2, r.Steps.Count);   // the other workflow's row is ignored
            Assert.Equal(WorkflowStates.InProgress, r.State);
            Assert.Equal("Head office", r.CurrentStep);
            Assert.Equal("Finance - MOBCO HO", r.WithWhom);
            Assert.Equal("INV-SUB-01", r.DocumentNo);
            Assert.True(File.Exists(r.TableScreenshotPath));
            Assert.True(new FileInfo(r.TableScreenshotPath).Length > 1000);   // PNG evidence card
            Assert.EndsWith(".xml", r.PageScreenshotPath);
            Assert.Equal(1, m.TokenCalls);   // token reused
            Assert.Contains(m.Requests, q => q.Contains("workflow_number:\"wf-008795\""));
        }
        finally { Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", null); }
    }

    [Fact]
    public async Task Searches_the_register_across_pages_and_downloads_by_document_id()
    {
        using var m = new MockAconexApi();
        Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", "s3cret");
        try
        {
            await using var c = new AconexApiClient(Config(m));
            var q = new DocumentQuery { DocumentNumbers = new() { "WIR-EL-000123", "WIR-EL-000124" }, Discipline = "Electrical" };
            var hits = await c.SearchDocumentsAsync(q);
            Assert.Equal(2, hits.Count);
            Assert.Equal("api:111", hits[0].RowKey);
            Assert.Equal("WIR", hits[0].Type);
            Assert.Contains(m.Requests, r => r.Contains("(docno:\"WIR-EL-000123\" OR docno:\"WIR-EL-000124\") AND discipline:\"Electrical\""));

            var folder = TestData.TempDir();
            var path = await c.DownloadAsync(hits[1], q, folder);
            Assert.Equal("doc-222.pdf", Path.GetFileName(path));
            Assert.StartsWith("%PDF", File.ReadAllText(path));

            // a queue item planned earlier has no row key: the client finds the id again by number
            var fresh = new AconexApiClient(Config(m));
            var p2 = await fresh.DownloadAsync(new DocumentHit { DocumentNo = "WIR-EL-000123", Revision = "0" }, q, folder);
            Assert.Contains("doc-111", p2);
            await fresh.DisposeAsync();
        }
        finally { Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", null); }
    }

    [Fact]
    public async Task Works_with_the_existing_download_service_and_retries_when_busy()
    {
        using var m = new MockAconexApi();
        Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", "s3cret");
        try
        {
            var db = TestData.NewDb();
            var store = new SqliteAconexStore(db.Path, "tester");
            store.EnsureSchema();
            var folder = TestData.TempDir();
            var web = new AconexConfig { DelayBetweenDownloadsMs = 0 };
            web.Folders.WirFolder = folder; web.Folders.MirFolder = folder; web.Folders.OtherFolder = folder;
            var cfg = Config(m);
            cfg.MaxRetries = 3;
            await using var c = new AconexApiClient(cfg, web);
            var svc = new DocumentDownloadService(c, store, web);
            var job = await svc.PlanAsync(new DocumentQuery { DocumentNumbers = new() { "WIR-EL-000123" } });
            m.FailNextWith = 503;
            var done = await svc.RunAsync(job.Id);
            Assert.Equal(JobStates.Done, done.State);
            Assert.True(done.Done >= 1);
            Assert.NotEmpty(store.Downloads());
        }
        finally { Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", null); }
    }

    [Fact]
    public async Task Wrong_credentials_ask_for_login_and_empty_searches_are_refused()
    {
        using var m = new MockAconexApi();
        Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", "wrong");
        try
        {
            await using var c = new AconexApiClient(Config(m));
            await Assert.ThrowsAsync<AconexLoginRequiredException>(() => c.EnsureLoggedInAsync());
            Assert.Throws<ArgumentException>(() => AconexApiClient.BuildQueries(new DocumentQuery(), new AconexApiFields()));
            var dates = AconexApiClient.BuildQueries(new DocumentQuery { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), DocType = "WIR" }, new AconexApiFields());
            Assert.Equal("registered:[20260901 TO 20260930] AND doctype:\"WIR\"", Assert.Single(dates));
        }
        finally { Environment.SetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET", null); }
    }

    [Fact]
    public void Config_validation_and_defaults()
    {
        var c = new AconexApiConfig();
        Assert.False(c.Enabled);
        Assert.Contains(c.Validate(), p => p.Contains("ProjectId"));
        var path = Path.Combine(TestData.TempDir(), "aconex-api.json");
        c.ProjectId = "1"; c.ClientId = "x"; c.Save(path);
        var back = AconexApiConfig.Load(path);
        Assert.Empty(back.Validate());
        Assert.Equal("/api/projects/{projectId}/register", back.Paths.RegisterSearch);
        Assert.False(AconexApiConfig.LoadSafe(Path.Combine(TestData.TempDir(), "missing.json")).Enabled);
    }
}
