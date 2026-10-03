using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Raffaello.Core.AconexWeb;

namespace Raffaello.Automation.Tests;

/// <summary>
/// A small local imitation of the Aconex pages the automation uses (HttpListener, no external dependency):
/// login form + session cookie, "Search Workflows" with the grouped results table (same columns as the real page),
/// and a paged document register with checkbox selection and Download (single file, or a ZIP bundle like Aconex
/// does for several files). All data is synthetic.
/// </summary>
public sealed class MockAconexServer : IDisposable
{
    public const string UserName = "demo.user";
    public const string Password = "demo-pass";
    /// <summary>Second fake account (WORKFLOWS / INVOICE UPLOAD profile tests).</summary>
    public const string WorkflowsUser = "demo.workflows";
    public const string WorkflowsPassword = "demo-pass-wf-7781";
    /// <summary>Fake one-time code accepted by the MFA page.</summary>
    public const string MfaCode = "123456";
    private const string Cookie = "MOCKSESSION";
    private const string PendingCookie = "MOCKPENDING";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _sessions = new();
    private readonly object _lock = new();

    public string BaseUrl { get; }
    public int LoginCount { get; private set; }
    /// <summary>Password posts that were refused (a real site would lock the account after a few).</summary>
    public int FailedLoginCount { get; private set; }
    public List<string> LoggedInUsers { get; } = new();
    /// <summary>After a correct password, ask for a one-time code (MFA) before the session starts.</summary>
    public bool RequireMfa { get; set; }
    /// <summary>Static pages served at /Fixture/{name} without a login (login-state detection tests).</summary>
    public Dictionary<string, string> Fixtures { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pending = new();
    private readonly Dictionary<string, string> _users = new() { [UserName] = Password, [WorkflowsUser] = WorkflowsPassword };
    public int DownloadCount { get; private set; }
    public int PageSize { get; set; } = 4;
    public List<MockWorkflow> Workflows { get; } = new();
    public List<MockDoc> Docs { get; } = new();

    public MockAconexServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        Seed();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Config pointing the automation at this mock (selectors match the real-site defaults where possible).</summary>
    public AconexConfig Config(string profileDir, string root)
    {
        var c = new AconexConfig
        {
            BaseUrl = BaseUrl, HomePath = "/Logon", ProjectId = "1000", ProfileDir = profileDir, BrowserChannel = "", Headless = true,
            NavigationTimeoutSec = 15, ManualLoginTimeoutSec = 5, DownloadTimeoutSec = 20, DelayBetweenDownloadsMs = 0,
        };
        c.Folders.WirFolder = Path.Combine(root, "WIR");
        c.Folders.MirFolder = Path.Combine(root, "MIR");
        c.Folders.OtherFolder = Path.Combine(root, "OTHER");
        c.Folders.ScreenshotFolder = Path.Combine(root, "shots");
        return c;
    }

    // ------------------------------------------------------------------ synthetic data

    private void Seed()
    {
        var wf = new MockWorkflow { No = "WF-000123", Name = "Electrical_Invoice_Approval", DocNo = "DEMO-PRJ-SUB-ELE-001-2026", Rev = "00", Ver = "3", Title = "Demo subcontractor invoice 01", File = "DEMO-PRJ-SUB-ELE-001-2026.pdf" };
        wf.Steps.AddRange(new[]
        {
            new MockStep("Procurement Director", "Mr Reviewer One - Demo", "18/05/2026", "19/05/2026", "23/05/2026", "18/05/2026", "Completed", "A - Approved"),
            new MockStep("PMO Director", "Mr Reviewer Two - Demo\nMs Reviewer Three - Demo\nApps Support - Demo", "18/05/2026", "19/05/2026", "24/05/2026", "07/06/2026", "Completed", "A - Approved"),
            new MockStep("PO Creation", "Mr Reviewer Four - Demo", "18/05/2026", "21/05/2026", "21/05/2026", "18/05/2026", "Completed", "A - Approved"),
            new MockStep("Top Management DMD", "Apps Support - Demo\nMr Reviewer Five - Demo", "07/06/2026", "08/06/2026", "25/05/2026", "10/06/2026", "Completed", "A - Approved"),
            new MockStep("Finance Manager", "Apps Support - Demo\nMr Reviewer Six - Demo", "10/06/2026", "13/06/2026", "27/05/2026", "", "Overdue", "Pending"),
        });
        Workflows.Add(wf);
        var wf2 = new MockWorkflow { No = "WF-000456", Name = "Electrical_Invoice_Approval", DocNo = "DEMO-PRJ-SUB-ELE-002-2026", Rev = "01", Ver = "1", Title = "Demo subcontractor invoice 02 rev 1", File = "DEMO-PRJ-SUB-ELE-002-2026.pdf" };
        wf2.Steps.Add(new MockStep("Procurement Director", "Mr Reviewer One - Demo", "28/09/2026", "08/10/2026", "08/10/2026", "", "Pending", "Pending"));
        Workflows.Add(wf2);

        var id = 0;
        MockDoc D(string no, string rev, string type, string disc, string group, string date, params string[] files) =>
            new() { Id = ++id, No = no, Rev = rev, Ver = "1", Title = $"Synthetic {type} {no}", Type = type, Discipline = disc, Group = group, Date = date, Status = "Code A", Files = files.ToList() };
        Docs.AddRange(new[]
        {
            D("DEMO-WIR-EL-000101", "0", "WIR", "Electrical", "BRANDED", "01/09/2026", "DEMO-WIR-EL-000101.pdf"),
            D("DEMO-WIR-EL-000102", "1", "WIR", "Electrical", "BRANDED", "03/09/2026", "DEMO-WIR-EL-000102.pdf", "DEMO-WIR-EL-000102 attachment.pdf"),
            D("DEMO-WIR-EL-000103", "0", "WIR", "Electrical", "HOTEL", "05/09/2026", "DEMO-WIR-EL-000103.pdf"),
            D("DEMO-WIR-EL-000104", "0", "WIR", "Electrical", "HOTEL", "08/09/2026", "DEMO-WIR-EL-000104.pdf"),
            D("DEMO-WIR-EL-000105", "0", "WIR", "Electrical", "HOTEL", "12/09/2026", "DEMO-WIR-EL-000105.pdf"),
            D("DEMO-MIR-EL-000201", "0", "MIR", "Electrical", "BRANDED", "02/09/2026", "DEMO-MIR-EL-000201.pdf"),
            D("DEMO-MIR-EL-000202", "2", "MIR", "Electrical", "HOTEL", "10/09/2026", "DEMO-MIR-EL-000202.pdf"),
            D("DEMO-WIR-ME-000301", "0", "WIR", "Mechanical", "HOTEL", "04/09/2026", "DEMO-WIR-ME-000301.pdf"),
            D("DEMO-WIR-EL-000099", "0", "WIR", "Electrical", "BRANDED", "15/08/2026", "DEMO-WIR-EL-000099.pdf"),
        });
    }

    // ------------------------------------------------------------------ server loop

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening) { return; }
            catch (HttpListenerException) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            lock (_lock) Route(ctx);
        }
        catch (Exception ex)
        {
            try { Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes(ex.ToString())); } catch { /* client gone */ }
        }
    }

    /// <summary>Server-side session timeout: every browser must log in again.</summary>
    public void ExpireSessions() { lock (_lock) _sessions.Clear(); }

    private bool LoggedIn(HttpListenerRequest req) => req.Cookies[Cookie] is { } c && _sessions.Contains(c.Value);

    private void Route(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url!.AbsolutePath.TrimEnd('/');
        if (path.Equals("/Logon", StringComparison.OrdinalIgnoreCase))
        {
            if (req.HttpMethod == "POST")
            {
                using var r = new StreamReader(req.InputStream, Encoding.UTF8);
                var form = ParseQuery(r.ReadToEnd());
                var user = form.GetValueOrDefault("userName") ?? "";
                if (_users.TryGetValue(user, out var pw) && form.GetValueOrDefault("password") == pw)
                {
                    var ret = form.GetValueOrDefault("returnUrl") is { Length: > 0 } ru ? ru : "/home";
                    if (RequireMfa)
                    {
                        var pid = Guid.NewGuid().ToString("N");
                        _pending[pid] = user;
                        ctx.Response.AppendHeader("Set-Cookie", $"{PendingCookie}={pid}; Path=/; HttpOnly");
                        Redirect(ctx, "/Mfa?returnUrl=" + Uri.EscapeDataString(ret));
                        return;
                    }
                    StartSession(ctx, user);
                    Redirect(ctx, ret);
                    return;
                }
                FailedLoginCount++;
                Html(ctx, LoginPage("Invalid user name or password."));
                return;
            }
            if (LoggedIn(req)) { Redirect(ctx, "/home"); return; }
            Html(ctx, LoginPage(""));
            return;
        }
        if (path.StartsWith("/Fixture/", StringComparison.OrdinalIgnoreCase))
        {
            if (Fixtures.TryGetValue(path["/Fixture/".Length..], out var fx)) Html(ctx, fx);
            else Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("no fixture"));
            return;
        }
        if (path.Equals("/Mfa", StringComparison.OrdinalIgnoreCase))
        {
            var pending = req.Cookies[PendingCookie]?.Value;
            if (pending is null || !_pending.TryGetValue(pending, out var who)) { Redirect(ctx, "/Logon"); return; }
            if (req.HttpMethod == "POST")
            {
                using var r = new StreamReader(req.InputStream, Encoding.UTF8);
                var form = ParseQuery(r.ReadToEnd());
                if (form.GetValueOrDefault("otp") == MfaCode)
                {
                    _pending.Remove(pending);
                    StartSession(ctx, who);
                    Redirect(ctx, form.GetValueOrDefault("returnUrl") is { Length: > 0 } ru ? ru : "/home");
                    return;
                }
                Html(ctx, MfaPage("That code is not right."));
                return;
            }
            Html(ctx, MfaPage(""));
            return;
        }
        if (!LoggedIn(req))
        {
            Redirect(ctx, "/Logon?returnUrl=" + Uri.EscapeDataString(req.Url.PathAndQuery));
            return;
        }
        switch (path.ToLowerInvariant())
        {
            case "/home": case "": Html(ctx, Shell("Home", "<p>Welcome to the mock project.</p>")); return;
            case "/workflow/searchworkflows": Html(ctx, WorkflowPage(req)); return;
            // classic Aconex shows module pages inside a frame
            case "/framed/workflow": Html(ctx, Shell("Workflows", "<iframe id='frameMain' name='main' src='/Workflow/SearchWorkflows' style='width:100%;height:900px;border:0'></iframe>")); return;
            case "/documentregister/search": Html(ctx, RegisterPage(req)); return;
            case "/documentregister/download": Download(ctx); return;
            default: Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("not found")); return;
        }
    }

    private void StartSession(HttpListenerContext ctx, string user)
    {
        var sid = Guid.NewGuid().ToString("N");
        _sessions.Add(sid);
        LoginCount++;
        LoggedInUsers.Add(user);
        // persistent cookie: the browser profile keeps it between runs, like the real SSO session
        ctx.Response.AppendHeader("Set-Cookie", $"{Cookie}={sid}; Path=/; Expires={DateTime.UtcNow.AddDays(30):R}; HttpOnly");
    }

    public static string MfaPage(string error) => Page("Verify", $@"
<div class='content'><h2>Two-step verification (mock)</h2><p>Enter the verification code from your authenticator app.</p><p style='color:#c00'>{WebUtility.HtmlEncode(error)}</p>
<form id='mfaForm' method='post' action='/Mfa'>
<div>Code <input name='otp' id='otp' autocomplete='one-time-code'></div>
<input type='hidden' name='returnUrl' id='returnUrl'>
<button type='submit' id='verify'>Verify</button></form>
<script>const p = new URLSearchParams(location.search); document.getElementById('returnUrl').value = p.get('returnUrl') || '';</script></div>");

    // ------------------------------------------------------------------ pages

    public static string Page(string title, string body) => $@"<!doctype html><html><head><meta charset='utf-8'><title>{title}</title>
<style>
body {{ font-family: Arial, sans-serif; margin:0; font-size:12px; color:#222; }}
.top {{ background:#1f2a44; color:#fff; padding:8px 14px; display:flex; gap:18px; align-items:center; }}
.top b {{ letter-spacing:1px; }} .content {{ padding:12px 16px; }}
table {{ border-collapse:collapse; width:100%; }} th {{ text-align:left; color:#666; font-weight:normal; border-bottom:1px solid #ccc; padding:4px; }}
td {{ padding:4px; border-bottom:1px solid #eee; vertical-align:top; }} tr.group td {{ background:#eef2f8; font-weight:bold; }}
.overdue {{ color:#c00; }} .criteria label {{ display:inline-block; width:110px; color:#555; }} .criteria div {{ margin:3px 0; }}
</style></head><body>{body}</body></html>";

    public static string Shell(string title, string content) => Page(title, $@"
<div class='top' id='nav-bar'><b>ORACLE ACONEX (MOCK)</b><span>Home</span><span>Documents</span><span>Workflows</span><span class='user' id='user-menu'>{UserName}</span></div>
<div class='content'><h2>{title}</h2>{content}</div>");

    public static string LoginPage(string error) => Page("Log on", $@"
<div class='content'><h2>Log on to Aconex (mock)</h2><p style='color:#c00'>{WebUtility.HtmlEncode(error)}</p>
<form id='logonForm' method='post' action='/Logon'>
<div>User name <input name='userName' id='userName'></div>
<div>Password <input type='password' name='password' id='password'></div>
<input type='hidden' name='returnUrl' id='returnUrl'>
<button type='submit' id='login'>Log in</button></form>
<script>const p = new URLSearchParams(location.search); document.getElementById('returnUrl').value = p.get('returnUrl') || '';</script></div>");

    private string WorkflowPage(HttpListenerRequest req)
    {
        var q = req.QueryString;
        var no = (q["workflowNo"] ?? "").Trim();
        var sb = new StringBuilder();
        sb.Append($@"<form class='criteria' method='get' action='/Workflow/SearchWorkflows'>
<input type='hidden' name='projectId' value='{WebUtility.HtmlEncode(q["projectId"] ?? "")}'>
<div><label>Workflow Name</label><select name='wfName'><option>-- Select --</option></select></div>
<div><label>Workflow No</label><input name='workflowNo' id='workflowNo' value=''></div>
<div><label>Date Range</label><select><option>-- Select --</option></select></div>
<div><input type='checkbox'> Show my tasks only &nbsp; Group By <select><option>Workflow No</option></select> Sort by <select><option>Date Due</option></select> Show <select><option>25</option></select> per page</div>
<button type='submit' id='searchButton'>Search</button> <button type='reset'>Clear</button></form>");
        if (no.Length > 0)
        {
            var wf = Workflows.FirstOrDefault(w => WorkflowParser.NormalizeWf(w.No) == WorkflowParser.NormalizeWf(no));
            if (wf is null) sb.Append("<p class='none'>No results found</p>");
            else
            {
                sb.Append($"<p>1 - {wf.Steps.Count} of {wf.Steps.Count} results (0 selected)</p>");
                sb.Append("<table id='resultsTable' class='searchResults'><thead><tr><th></th>");
                foreach (var h in new[] { "Document No", "Document Revision", "Document Version", "Document Title", "Step Name", "Action", "Assigned To", "Date In", "Date Due &#9650;", "Original Due Date", "Date Completed", "Step Status", "Step Outcome", "File Name" })
                    sb.Append($"<th>{h}</th>");
                sb.Append("</tr></thead><tbody>");
                sb.Append($"<tr class='group'><td colspan='15'>Initiator Tools &#9662; | Workflow No.: {wf.No} Name: {wf.Name}</td></tr>");
                foreach (var s in wf.Steps)
                {
                    var status = s.Status == "Overdue" ? "<span class='overdue'>Overdue</span>" : s.Status;
                    var due = s.Status == "Overdue" ? $"<span class='overdue'>{s.Due}</span>" : s.Due;
                    sb.Append($@"<tr><td><input type='checkbox'></td><td>{wf.DocNo}</td><td>{wf.Rev}</td><td>{wf.Ver}</td><td>{WebUtility.HtmlEncode(wf.Title)}</td>
<td>{WebUtility.HtmlEncode(s.Name)}</td><td>&#10003; &#9993;</td><td>{string.Join("<br>", s.Assigned.Split('\n').Select(WebUtility.HtmlEncode))}</td>
<td>{s.In}</td><td>{due}</td><td>{s.OriginalDue}</td><td>{s.Completed}</td><td>{status}</td><td>{s.Outcome}</td><td><a href='#'>{wf.File}</a></td></tr>");
                }
                sb.Append("</tbody></table>");
            }
        }
        return Shell("Search Workflows", sb.ToString());
    }

    private string RegisterPage(HttpListenerRequest req)
    {
        var q = req.QueryString;
        var sb = new StringBuilder();
        sb.Append($@"<form class='criteria' method='get' action='/DocumentRegister/Search'>
<input type='hidden' name='search' value='1'>
<div><label>Document No</label><input name='docno' id='docNo' value=''></div>
<div><label>Date from</label><input name='dateFrom' id='dateFrom' placeholder='dd/mm/yyyy'> <label>to</label><input name='dateTo' id='dateTo' placeholder='dd/mm/yyyy'></div>
<div><label>Discipline</label><select name='discipline' id='discipline'><option value=''>Any</option><option value='EL'>Electrical</option><option value='ME'>Mechanical</option></select></div>
<div><label>Type</label><select name='docType' id='docType'><option value=''>Any</option><option value='WIR'>WIR</option><option value='MIR'>MIR</option></select></div>
<div><label>Group</label><input name='group' id='group'></div>
<button type='submit' id='searchButton'>Search</button></form>");
        if (q["search"] != "1") return Shell("Document Register", sb.ToString());

        var docs = Docs.AsEnumerable();
        if (q["docno"] is { Length: > 0 } dn) docs = docs.Where(d => d.No.Contains(dn.Trim(), StringComparison.OrdinalIgnoreCase));
        DateTime? P(string? s) => DateTime.TryParseExact(s ?? "", "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (P(q["dateFrom"]) is { } f) docs = docs.Where(d => P(d.Date) >= f);
        if (P(q["dateTo"]) is { } t) docs = docs.Where(d => P(d.Date) <= t);
        if (q["discipline"] is { Length: > 0 } disc) docs = docs.Where(d => d.Discipline.StartsWith(disc == "EL" ? "Electrical" : disc == "ME" ? "Mechanical" : disc, StringComparison.OrdinalIgnoreCase));
        if (q["docType"] is { Length: > 0 } ty) docs = docs.Where(d => d.Type.Equals(ty, StringComparison.OrdinalIgnoreCase));
        if (q["group"] is { Length: > 0 } g) docs = docs.Where(d => d.Group.Equals(g, StringComparison.OrdinalIgnoreCase));
        var list = docs.OrderBy(d => d.No).ToList();
        if (list.Count == 0) { sb.Append("<p>No results found</p>"); return Shell("Document Register", sb.ToString()); }
        var page = int.TryParse(q["page"], out var pg) ? Math.Max(1, pg) : 1;
        var pages = (list.Count + PageSize - 1) / PageSize;
        var rows = list.Skip((page - 1) * PageSize).Take(PageSize).ToList();
        sb.Append($"<p>{list.Count} results - page {page} of {pages}</p>");
        // columns deliberately in a different order from the workflow page and with extra columns: lookup is by header text
        sb.Append("<table id='docTable'><thead><tr><th><input type='checkbox'></th><th>Title</th><th>Document No &#9660;</th><th>Type</th><th>Rev</th><th>Version</th><th>Status</th><th>Discipline</th><th>Category</th><th>Date Modified</th><th>Confidential</th><th>File</th></tr></thead><tbody>");
        foreach (var d in rows)
            sb.Append($"<tr><td><input type='checkbox' name='sel' value='{d.Id}'></td><td>{WebUtility.HtmlEncode(d.Title)}</td><td><a href='#'>{d.No}</a></td><td>{d.Type}</td><td>{d.Rev}</td><td>{d.Ver}</td><td>{d.Status}</td><td>{d.Discipline}</td><td>{d.Group}</td><td>{d.Date}</td><td>No</td><td>{WebUtility.HtmlEncode(d.Files[0])}</td></tr>");
        sb.Append("</tbody></table>");
        var qs = HttpUtility(q, "page");
        sb.Append(page < pages ? $"<a class='next' href='/DocumentRegister/Search?{qs}&page={page + 1}'>Next &gt;</a>" : "<a class='next disabled'>Next &gt;</a>");
        sb.Append(@" <button type='button' id='downloadButton' onclick=""const ids=[...document.querySelectorAll('input[name=sel]:checked')].map(c=>c.value); if(ids.length) location.href='/DocumentRegister/Download?ids='+ids.join(',');"">Download</button>");
        return Shell("Document Register", sb.ToString());
    }

    private static string HttpUtility(System.Collections.Specialized.NameValueCollection q, string except) =>
        string.Join("&", q.AllKeys.Where(k => k != null && k != except).Select(k => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(q[k] ?? "")}"));

    private void Download(HttpListenerContext ctx)
    {
        var ids = (ctx.Request.QueryString["ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
        var docs = Docs.Where(d => ids.Contains(d.Id)).ToList();
        if (docs.Count == 0) { Send(ctx, 404, "text/plain", Encoding.UTF8.GetBytes("nothing selected")); return; }
        DownloadCount++;
        if (docs.Count == 1 && docs[0].Files.Count == 1)
        {
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{docs[0].Files[0]}\"");
            Send(ctx, 200, "application/pdf", FakePdf(docs[0], docs[0].Files[0]));
            return;
        }
        // several files: Aconex sends one ZIP bundle
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var d in docs)
                foreach (var f in d.Files)
                {
                    var e = zip.CreateEntry($"{d.No}/{f}");
                    using var s = e.Open();
                    s.Write(FakePdf(d, f));
                }
        ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"Documents.zip\"");
        Send(ctx, 200, "application/zip", ms.ToArray());
    }

    public static byte[] FakePdf(MockDoc d, string file) =>
        Encoding.ASCII.GetBytes($"%PDF-1.4\n% synthetic test file\n% {d.No} rev {d.Rev} {file}\n%%EOF\n");

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string> ParseQuery(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");

    private static void Redirect(HttpListenerContext ctx, string to)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = to;
        ctx.Response.Close();
    }

    private static void Html(HttpListenerContext ctx, string html) => Send(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

    private static void Send(HttpListenerContext ctx, int status, string type, byte[] body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
    }
}

public sealed class MockWorkflow
{
    public string No { get; set; } = "";
    public string Name { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Ver { get; set; } = "";
    public string Title { get; set; } = "";
    public string File { get; set; } = "";
    public List<MockStep> Steps { get; } = new();
}

public sealed record MockStep(string Name, string Assigned, string In, string Due, string OriginalDue, string Completed, string Status, string Outcome);

public sealed class MockDoc
{
    public int Id { get; set; }
    public string No { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Ver { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Discipline { get; set; } = "";
    public string Group { get; set; } = "";
    public string Date { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Files { get; set; } = new();
}
