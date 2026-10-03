using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using Raffaello.Core.AconexWeb;

namespace Raffaello.Automation;

/// <summary>
/// Drives Aconex in a real browser (Playwright, Chromium / Edge) with a persistent profile, so the session is kept
/// between runs. With a saved login (one per Aconex account, DPAPI vault) the login form is filled automatically -
/// at most once per refused attempt, never in a loop. MFA / SSO / CAPTCHA / expired password are always handed to
/// the user in the visible window ("finish the login, then press CONTINUE"). Every URL, selector and column header
/// comes from <see cref="AconexConfig"/> (aconex.config.json). Login values are never logged or screenshotted.
/// </summary>
public sealed class PlaywrightAconexClient : IAconexClient
{
    private readonly AconexConfig _cfg;
    private readonly ICredentialVault _vault;
    private readonly Func<DateTime> _today;
    private IPlaywright? _pw;
    private IBrowserContext? _ctx;
    private IPage? _page;
    private bool _loggedIn;
    private readonly string? _profileDir;
    private readonly string _account;
    private volatile bool _continueRequested;

    /// <param name="profileDir">Browser profile folder (one per Aconex account); null = the configured ProfileDir.</param>
    /// <param name="accountName">Account shown in messages ("MIR / WIR"); never the user name.</param>
    public PlaywrightAconexClient(AconexConfig cfg, ICredentialVault? vault = null, Func<DateTime>? today = null, string? profileDir = null, string accountName = "")
    {
        _cfg = cfg;
        _vault = vault ?? new NoCredentialVault();
        _today = today ?? (() => DateTime.Today);
        _profileDir = string.IsNullOrWhiteSpace(profileDir) ? null : profileDir;
        _account = accountName ?? "";
    }

    public event Action<string>? Log;
    /// <summary>Instruction for the user when the login must be finished in the browser; "" when that is over.</summary>
    public event Action<string>? LoginAttention;
    public IPage? Page => _page;
    public string ProfileDir => _profileDir ?? _cfg.ResolvedProfileDir;
    public string AccountName => _account.Length > 0 ? _account : "Aconex";
    /// <summary>Set after Aconex refused the saved login: no further automatic attempt (avoids locking the account).</summary>
    public bool AutoLoginBlocked { get; set; }
    /// <summary>How many times the saved login was typed by this client.</summary>
    public int AutoLoginAttempts { get; private set; }
    public AconexLoginState LastLoginState { get; private set; }

    /// <summary>The user pressed CONTINUE after finishing the login in the browser.</summary>
    public void ContinueLogin() => _continueRequested = true;

    private void Say(string s) => Log?.Invoke(s);

    // ------------------------------------------------------------------ browser

    private async Task<IPage> StartAsync()
    {
        if (_page is { IsClosed: false }) return _page;
        // single-file publish: the driver (.playwright\node + package) sits next to the exe, not next to the (bundled) dll
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH"))
            && Directory.Exists(Path.Combine(AppContext.BaseDirectory, ".playwright")))
            Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", AppContext.BaseDirectory);
        _pw ??= await Playwright.CreateAsync().ConfigureAwait(false);
        Directory.CreateDirectory(ProfileDir);
        DisableBrowserPasswordSaving(ProfileDir);
        var opt = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _cfg.Headless,
            AcceptDownloads = true,
            ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
            Locale = "en-GB",
        };
        if (_cfg.BrowserArgs.Count > 0) opt.Args = _cfg.BrowserArgs.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var exe = AconexConfig.Expand(_cfg.BrowserExecutablePath);
        if (!string.IsNullOrWhiteSpace(exe)) opt.ExecutablePath = exe;
        else if (!string.IsNullOrWhiteSpace(_cfg.BrowserChannel)) opt.Channel = _cfg.BrowserChannel;
        Say($"Starting browser ({(opt.ExecutablePath ?? opt.Channel ?? "chromium")}, profile {ProfileDir}{(_cfg.Headless ? ", headless" : "")})");
        _ctx = await _pw.Chromium.LaunchPersistentContextAsync(ProfileDir, opt).ConfigureAwait(false);
        _ctx.SetDefaultTimeout(_cfg.NavigationTimeoutSec * 1000);
        _ctx.SetDefaultNavigationTimeout(_cfg.NavigationTimeoutSec * 1000);
        _page = _ctx.Pages.FirstOrDefault() ?? await _ctx.NewPageAsync().ConfigureAwait(false);
        return _page;
    }

    /// <summary>
    /// The browser must not keep its own copy of the Aconex login: switch off its password manager and form autofill
    /// in the profile (Chromium / Edge "Default\Preferences") before it starts.
    /// </summary>
    public static void DisableBrowserPasswordSaving(string profileDir)
    {
        var path = Path.Combine(profileDir, "Default", "Preferences");
        System.Text.Json.Nodes.JsonObject root;
        try
        {
            root = File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject o ? o : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { root = new(); }
        System.Text.Json.Nodes.JsonObject Obj(string name)
        {
            if (root[name] is System.Text.Json.Nodes.JsonObject x) return x;
            var n = new System.Text.Json.Nodes.JsonObject();
            root[name] = n;
            return n;
        }
        root["credentials_enable_service"] = false;
        root["credentials_enable_autosignin"] = false;
        Obj("profile")["password_manager_enabled"] = false;
        var autofill = Obj("autofill");
        autofill["enabled"] = false;
        autofill["profile_enabled"] = false;
        autofill["credit_card_enabled"] = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* browser still runs; it just may offer to save */ }
    }

    /// <summary>Locator in the page or inside the configured frame.</summary>
    private ILocator L(string frameSelector, string selector) =>
        string.IsNullOrWhiteSpace(frameSelector) ? _page!.Locator(selector) : _page!.FrameLocator(frameSelector).Locator(selector);

    private async Task<bool> VisibleAsync(ILocator loc)
    {
        try { return await loc.First.IsVisibleAsync().ConfigureAwait(false); }
        catch (PlaywrightException) { return false; }
    }

    // ------------------------------------------------------------------ login

    public async Task EnsureLoggedInAsync(CancellationToken ct = default)
    {
        var page = await StartAsync().ConfigureAwait(false);
        if (_loggedIn && !await VisibleAsync(L("", _cfg.Login.LoginFormSelector)).ConfigureAwait(false)) return;
        if (page.Url is "about:blank" or "" || !page.Url.StartsWith(_cfg.BaseUrl, StringComparison.OrdinalIgnoreCase))
            await page.GotoAsync(_cfg.Url(_cfg.HomePath)).ConfigureAwait(false);
        await LoginIfNeededAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens <paramref name="url"/> (if given) and reports what the page shows. Used by Test login and the tests.</summary>
    public async Task<AconexLoginState> DetectLoginStateAsync(string? url = null, CancellationToken ct = default)
    {
        var page = await StartAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(url)) await page.GotoAsync(url).ConfigureAwait(false);
        return await WaitForStateAsync(TimeSpan.FromSeconds(Math.Min(10, _cfg.NavigationTimeoutSec)), s => s != AconexLoginState.Unknown, ct).ConfigureAwait(false);
    }

    private async Task LoginIfNeededAsync(CancellationToken ct)
    {
        var page = _page!;
        var shortWait = TimeSpan.FromSeconds(Math.Min(15, _cfg.NavigationTimeoutSec));
        var state = await WaitForStateAsync(shortWait, s => s != AconexLoginState.Unknown, ct).ConfigureAwait(false);
        if (state == AconexLoginState.LoggedIn) { _loggedIn = true; return; }

        var auto = _cfg.Login.AutoFillStoredCredential && !AutoLoginBlocked;
        if (auto && state == AconexLoginState.WrongPassword && _vault.HasCredential)
        {
            // an old error message is on the page: open a clean login page so a new message can be told apart
            await page.GotoAsync(_cfg.Url(_cfg.HomePath)).ConfigureAwait(false);
            state = await WaitForStateAsync(shortWait, s => s != AconexLoginState.Unknown, ct).ConfigureAwait(false);
            if (state == AconexLoginState.LoggedIn) { _loggedIn = true; return; }
        }

        if (auto && state == AconexLoginState.LoginForm && _vault.Load() is { } cred)
        {
            AutoLoginAttempts++;
            Say($"Login form shown - typing the saved login for {AccountName}.");
            await L("", _cfg.Login.UserNameInput).First.FillAsync(cred.User).ConfigureAwait(false);
            await L("", _cfg.Login.PasswordInput).First.FillAsync(cred.Password).ConfigureAwait(false);
            await L("", _cfg.Login.SubmitButton).First.ClickAsync().ConfigureAwait(false);
            await SettleAsync().ConfigureAwait(false);
            state = await WaitForStateAsync(TimeSpan.FromSeconds(_cfg.NavigationTimeoutSec),
                s => s is not (AconexLoginState.LoginForm or AconexLoginState.Unknown), ct).ConfigureAwait(false);
            if (state == AconexLoginState.LoggedIn) { _loggedIn = true; Say($"Logged in ({AccountName})."); return; }
            if (state is AconexLoginState.WrongPassword or AconexLoginState.LoginForm or AconexLoginState.AccountLocked)
            {
                AutoLoginBlocked = true;
                throw Refused(state);
            }
            // MFA / SSO / CAPTCHA / expired password / unknown page: the user finishes it below
        }
        if (state == AconexLoginState.AccountLocked) throw Refused(state);

        if (_cfg.Headless)
            throw new AconexLoginRequiredException("Aconex needs a login. Run once with the browser visible (Headless = false) and log in; the session is kept in " + ProfileDir + ".");

        await HandOverAsync(state, ct).ConfigureAwait(false);
    }

    private AconexLoginFailedException Refused(AconexLoginState state) => new(state == AconexLoginState.AccountLocked
        ? $"Aconex says the {AccountName} account is locked. Unlock it with Aconex support / your admin, then try again. The app did not retry."
        : $"Aconex did not accept the saved user name / password for {AccountName}. Correct them in Settings > Aconex and press SAVE. The app will not try again on its own (to avoid locking the account).", state);

    /// <summary>Brings the browser to the front and waits until the user has finished the login (detected, or CONTINUE pressed).</summary>
    private async Task HandOverAsync(AconexLoginState state, CancellationToken ct)
    {
        var msg = AconexLoginDetector.HandOverMessage(state, AccountName);
        Say(msg);
        _continueRequested = false;
        LoginAttention?.Invoke(msg);
        try
        {
            try { await _page!.BringToFrontAsync().ConfigureAwait(false); } catch (PlaywrightException) { }
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(_cfg.ManualLoginTimeoutSec);
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                var s = await ProbeStateAsync().ConfigureAwait(false);
                if (s == AconexLoginState.LoggedIn) { _loggedIn = true; Say($"Logged in ({AccountName}) - the session is kept in the browser profile."); return; }
                if (_continueRequested)
                {
                    _continueRequested = false;
                    // the user says it is done: accept any Aconex page that no longer asks for a login
                    if (s == AconexLoginState.Unknown && AconexLoginDetector.IsAllowedLoginHost(_page!.Url, _cfg.Login, _cfg.BaseUrl))
                    {
                        _loggedIn = true;
                        Say($"CONTINUE pressed - carrying on ({AccountName}). If this keeps happening, check Login.LoggedInSelector in aconex.config.json.");
                        return;
                    }
                    Say($"Still not logged in ({s}). Finish the login in the browser, then press CONTINUE again.");
                }
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
            throw new AconexLoginRequiredException($"Not logged in after {_cfg.ManualLoginTimeoutSec} s ({AccountName}). Check Login.LoggedInSelector in aconex.config.json if you did log in.");
        }
        finally { LoginAttention?.Invoke(""); }
    }

    private async Task<AconexLoginState> WaitForStateAsync(TimeSpan timeout, Func<AconexLoginState, bool> done, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        var state = AconexLoginState.Unknown;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            state = await ProbeStateAsync().ConfigureAwait(false);
            if (done(state) || DateTime.UtcNow >= until) return state;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Looks at the page once: configured selectors, visible text and URL (nothing is stored or logged).</summary>
    private async Task<AconexLoginState> ProbeStateAsync()
    {
        var l = _cfg.Login;
        try
        {
            var text = await _page!.EvaluateAsync<string>("() => document.body ? document.body.innerText.slice(0, 20000) : ''").ConfigureAwait(false);
            async Task<bool> Vis(string sel) => !string.IsNullOrWhiteSpace(sel) && await VisibleAsync(L("", sel)).ConfigureAwait(false);
            var probe = new AconexLoginProbe(_page.Url, text ?? "",
                await Vis(l.LoggedInSelector).ConfigureAwait(false), await Vis(l.LoginFormSelector).ConfigureAwait(false), await Vis(l.PasswordInput).ConfigureAwait(false),
                await Vis(l.MfaInputSelector).ConfigureAwait(false), await Vis(l.CaptchaSelector).ConfigureAwait(false));
            return LastLoginState = AconexLoginDetector.Classify(probe, l, _cfg.BaseUrl);
        }
        catch (PlaywrightException) { return AconexLoginState.Unknown; } // page is navigating
    }

    private async Task GotoAsync(string url, CancellationToken ct)
    {
        await EnsureLoggedInAsync(ct).ConfigureAwait(false);
        await _page!.GotoAsync(url).ConfigureAwait(false);
        // the session may have expired: log in again and come back
        if (await VisibleAsync(L("", _cfg.Login.LoginFormSelector)).ConfigureAwait(false))
        {
            _loggedIn = false;
            await LoginIfNeededAsync(ct).ConfigureAwait(false);
            await _page.GotoAsync(url).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ tables

    /// <summary>
    /// Reads a results table from the DOM (HTML table or ARIA grid). Every body row gets a data-raff-row attribute so it
    /// can be found again (checkbox / download link).
    /// </summary>
    private const string ExtractTableJs = @"(t) => {
  const txt = c => ((c.innerText !== undefined ? c.innerText : c.textContent) || '').replace(/ /g, ' ').trim();
  const link = c => { const a = c.querySelector('a[href]'); return a ? a.href : ''; };
  let headers = [], rows = [], idx = 0;
  if (t.tagName === 'TABLE') {
    if (t.tHead && t.tHead.rows.length) headers = Array.from(t.tHead.rows[t.tHead.rows.length - 1].cells).map(txt);
    const body = t.tBodies.length ? Array.from(t.tBodies).flatMap(b => Array.from(b.rows)) : Array.from(t.rows);
    for (const r of body) {
      if (r.parentElement && r.parentElement.tagName === 'THEAD') continue;
      const cells = Array.from(r.cells);
      if (!cells.length) continue;
      if (!headers.length && cells.every(c => c.tagName === 'TH')) { headers = cells.map(txt); continue; }
      const key = String(idx++);
      r.setAttribute('data-raff-row', key);
      const isGroup = cells.length === 1 && (cells[0].colSpan > 1 || headers.length > 1);
      rows.push({ Cells: cells.map(txt), Links: cells.map(link), IsGroup: isGroup, Key: key });
    }
  } else {
    headers = Array.from(t.querySelectorAll('[role=columnheader]')).map(txt);
    for (const r of Array.from(t.querySelectorAll('[role=row]'))) {
      const cells = Array.from(r.querySelectorAll('[role=gridcell],[role=cell]'));
      if (!cells.length) continue;
      const key = String(idx++);
      r.setAttribute('data-raff-row', key);
      rows.push({ Cells: cells.map(txt), Links: cells.map(link), IsGroup: cells.length === 1, Key: key });
    }
  }
  return JSON.stringify({ Headers: headers, Rows: rows });
}";

    private static readonly JsonSerializerOptions JsonOpt = new() { PropertyNameCaseInsensitive = true };

    private async Task<RawTable?> ReadTableAsync(string frame, string tableSelector, string noResultsText, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(_cfg.NavigationTimeoutSec);
        var table = L(frame, tableSelector).First;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (await VisibleAsync(table).ConfigureAwait(false))
            {
                var json = await table.EvaluateAsync<string>(ExtractTableJs).ConfigureAwait(false);
                return JsonSerializer.Deserialize<RawTable>(json, JsonOpt) ?? new RawTable();
            }
            if (!string.IsNullOrWhiteSpace(noResultsText))
            {
                var none = string.IsNullOrWhiteSpace(frame) ? _page!.GetByText(noResultsText) : _page!.FrameLocator(frame).GetByText(noResultsText);
                if (await VisibleAsync(none).ConfigureAwait(false)) return null;
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new AconexPageChangedException($"The results table '{tableSelector}' did not appear within {_cfg.NavigationTimeoutSec} s. Check the selector in aconex.config.json.");
    }

    /// <summary>Reads the table on this page and the following pages (Next button), up to maxPages.</summary>
    private async Task<List<(RawTable Table, int Page)>> ReadAllPagesAsync(string frame, string tableSelector, string noResults, string nextSelector, int maxPages, CancellationToken ct)
    {
        var pages = new List<(RawTable, int)>();
        for (var p = 1; p <= Math.Max(1, maxPages); p++)
        {
            var t = await ReadTableAsync(frame, tableSelector, noResults, ct).ConfigureAwait(false);
            if (t is null) break;
            pages.Add((t, p));
            if (string.IsNullOrWhiteSpace(nextSelector)) break;
            var next = L(frame, nextSelector).First;
            if (!await VisibleAsync(next).ConfigureAwait(false)) break;
            await ClickAndWaitForTableChangeAsync(frame, tableSelector, () => next.ClickAsync(), ct).ConfigureAwait(false);
        }
        return pages;
    }

    /// <summary>Clicks (next page / search) and waits until the results table is replaced, so a stale table is never read.</summary>
    private async Task ClickAndWaitForTableChangeAsync(string frame, string tableSelector, Func<Task> click, CancellationToken ct)
    {
        var table = L(frame, tableSelector).First;
        var before = await VisibleAsync(table).ConfigureAwait(false) ? await table.InnerTextAsync().ConfigureAwait(false) : null;
        await click().ConfigureAwait(false);
        await SettleAsync().ConfigureAwait(false);
        if (before is null) return;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(_cfg.NavigationTimeoutSec);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await VisibleAsync(table).ConfigureAwait(false)) return;
                if (await table.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2000 }).ConfigureAwait(false) != before) return;
            }
            catch (PlaywrightException) { /* navigating */ }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the page to finish loading after an action (network idle, bounded).</summary>
    private async Task SettleAsync()
    {
        try { await _page!.WaitForLoadStateAsync(LoadState.DOMContentLoaded).ConfigureAwait(false); }
        catch (PlaywrightException) { }
        try { await _page!.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 }).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (PlaywrightException) { }
    }

    private static RawTable Merge(IEnumerable<RawTable> tables)
    {
        var all = new RawTable();
        foreach (var t in tables)
        {
            if (all.Headers.Count == 0) all.Headers.AddRange(t.Headers);
            all.Rows.AddRange(t.Rows);
        }
        return all;
    }

    // ------------------------------------------------------------------ workflows

    public async Task<WorkflowLookupResult> LookupWorkflowAsync(string workflowNo, string screenshotFolder, CancellationToken ct = default)
    {
        var wf = workflowNo.Trim().ToUpperInvariant();
        var w = _cfg.Workflows;
        await GotoAsync(_cfg.Url(w.SearchPath, new Dictionary<string, string> { ["workflowNo"] = wf }), ct).ConfigureAwait(false);
        foreach (var sel in (w.BeforeSearchClicks ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await L(w.FrameSelector, sel).First.ClickAsync().ConfigureAwait(false);
        var box = L(w.FrameSelector, w.WorkflowNoInput).First;
        await box.FillAsync(wf).ConfigureAwait(false);
        var hasButton = await VisibleAsync(L(w.FrameSelector, w.SearchButton)).ConfigureAwait(false);
        await ClickAndWaitForTableChangeAsync(w.FrameSelector, w.ResultsTable,
            () => hasButton ? L(w.FrameSelector, w.SearchButton).First.ClickAsync() : box.PressAsync("Enter"), ct).ConfigureAwait(false);

        var first = await ReadTableAsync(w.FrameSelector, w.ResultsTable, w.NoResultsText, ct).ConfigureAwait(false);
        Directory.CreateDirectory(screenshotFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
        var safe = DocumentRegister.Safe(wf);
        var pageShot = Path.Combine(screenshotFolder, $"{safe}_{stamp}_page.png");
        await _page!.ScreenshotAsync(new PageScreenshotOptions { Path = pageShot, FullPage = true }).ConfigureAwait(false);
        if (first is null)
        {
            Say($"{wf}: no results");
            return new WorkflowLookupResult { WorkflowNo = wf, CheckedAt = DateTime.Now, State = WorkflowStates.NotFound, PageScreenshotPath = pageShot };
        }
        var tableShot = Path.Combine(screenshotFolder, $"{safe}_{stamp}_table.png");
        await L(w.FrameSelector, w.ResultsTable).First.ScreenshotAsync(new LocatorScreenshotOptions { Path = tableShot }).ConfigureAwait(false);

        var tables = new List<RawTable> { first };
        if (w.MaxPages > 1 && !string.IsNullOrWhiteSpace(w.NextPageButton) && await VisibleAsync(L(w.FrameSelector, w.NextPageButton)).ConfigureAwait(false))
        {
            await ClickAndWaitForTableChangeAsync(w.FrameSelector, w.ResultsTable, () => L(w.FrameSelector, w.NextPageButton).First.ClickAsync(), ct).ConfigureAwait(false);
            tables.AddRange((await ReadAllPagesAsync(w.FrameSelector, w.ResultsTable, w.NoResultsText, w.NextPageButton, w.MaxPages - 1, ct).ConfigureAwait(false)).Select(x => x.Table));
        }
        var result = WorkflowParser.Parse(wf, Merge(tables), _cfg, _today());
        result.PageScreenshotPath = pageShot;
        result.TableScreenshotPath = tableShot;
        if (result.UnknownHeaders.Count > 0) Say($"{wf}: columns not used: {string.Join(", ", result.UnknownHeaders)}");
        Say(result.Summary);
        return result;
    }

    // ------------------------------------------------------------------ document register

    public async Task<List<DocumentHit>> SearchDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        var d = _cfg.Documents;
        var hits = new List<DocumentHit>();
        var batches = query.DocumentNumbers.Count == 0 ? new List<string?> { null }
            : d.DocNoSeparator.Length > 0 ? new List<string?> { string.Join(d.DocNoSeparator, query.DocumentNumbers) }
            : query.DocumentNumbers.Select(n => (string?)n).ToList();
        foreach (var docNo in batches)
        {
            ct.ThrowIfCancellationRequested();
            await RunDocumentSearchAsync(query, docNo, ct).ConfigureAwait(false);
            foreach (var (table, page) in await ReadAllPagesAsync(d.FrameSelector, d.ResultsTable, d.NoResultsText, d.NextPageButton, d.MaxPages, ct).ConfigureAwait(false))
                hits.AddRange(DocumentRegister.ParseResults(table, _cfg, page));
        }
        // a search by number may return near matches: keep the exact numbers asked for
        if (query.DocumentNumbers.Count > 0)
        {
            var wanted = new HashSet<string>(query.DocumentNumbers, StringComparer.OrdinalIgnoreCase);
            hits = hits.Where(h => wanted.Contains(h.DocumentNo)).ToList();
        }
        Say($"Register search ({query.Describe()}): {hits.Count} rows");
        return hits;
    }

    private async Task RunDocumentSearchAsync(DocumentQuery q, string? docNo, CancellationToken ct)
    {
        var d = _cfg.Documents;
        await GotoAsync(_cfg.Url(d.SearchPath), ct).ConfigureAwait(false);
        async Task Fill(string sel, string? value)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrWhiteSpace(sel)) return;
            var loc = L(d.FrameSelector, sel).First;
            var tag = await loc.EvaluateAsync<string>("e => e.tagName").ConfigureAwait(false);
            if (tag.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                try { await loc.SelectOptionAsync(new SelectOptionValue { Label = value }).ConfigureAwait(false); }
                catch (PlaywrightException) { await loc.SelectOptionAsync(new SelectOptionValue { Value = value }).ConfigureAwait(false); }
            }
            else await loc.FillAsync(value).ConfigureAwait(false);
        }
        await Fill(d.DocNoInput, docNo);
        await Fill(d.DateFromInput, q.DateFrom?.ToString(d.DateInputFormat, CultureInfo.InvariantCulture));
        await Fill(d.DateToInput, q.DateTo?.ToString(d.DateInputFormat, CultureInfo.InvariantCulture));
        await Fill(d.DisciplineSelect, q.Discipline);
        await Fill(d.DocTypeSelect, q.DocType);
        await Fill(d.GroupInput, q.Group);
        await ClickAndWaitForTableChangeAsync(d.FrameSelector, d.ResultsTable, () => L(d.FrameSelector, d.SearchButton).First.ClickAsync(), ct).ConfigureAwait(false);
    }

    public async Task<string> DownloadAsync(DocumentHit hit, DocumentQuery query, string targetFolder, CancellationToken ct = default)
    {
        var d = _cfg.Documents;
        // find the row again with a search on its number (works for rows found by date / group too)
        await RunDocumentSearchAsync(new DocumentQuery { DocType = query.DocType, Discipline = query.Discipline }, hit.DocumentNo, ct).ConfigureAwait(false);
        string? rowKey = null;
        foreach (var (table, _) in await ReadAllPagesAsync(d.FrameSelector, d.ResultsTable, d.NoResultsText, "", 1, ct).ConfigureAwait(false))
        {
            var rows = DocumentRegister.ParseResults(table, _cfg);
            var match = rows.FirstOrDefault(r => r.DocumentNo.Equals(hit.DocumentNo, StringComparison.OrdinalIgnoreCase) && r.Revision.Equals(hit.Revision, StringComparison.OrdinalIgnoreCase))
                        ?? (hit.Revision.Length == 0 ? rows.FirstOrDefault(r => r.DocumentNo.Equals(hit.DocumentNo, StringComparison.OrdinalIgnoreCase)) : null);
            rowKey = match?.RowKey;
        }
        if (rowKey is null) throw new InvalidOperationException($"{hit.DocumentNo} rev {hit.Revision} is no longer in the register search (superseded revision?).");
        var row = L(d.FrameSelector, d.ResultsTable).First.Locator($"[data-raff-row='{rowKey}']").First;

        var timeout = _cfg.DownloadTimeoutSec * 1000f;
        IDownload download;
        if (d.DownloadMode.Equals("RowLink", StringComparison.OrdinalIgnoreCase))
        {
            download = await WaitForDownloadAnywhereAsync(() => row.Locator(d.RowDownloadLink).First.ClickAsync(), timeout, ct).ConfigureAwait(false);
        }
        else
        {
            await row.Locator(d.RowCheckbox).First.CheckAsync().ConfigureAwait(false);
            download = await WaitForDownloadAnywhereAsync(async () =>
            {
                await L(d.FrameSelector, d.DownloadButton).First.ClickAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(d.DownloadConfirmButton))
                    await L(d.FrameSelector, d.DownloadConfirmButton).First.ClickAsync().ConfigureAwait(false);
            }, timeout, ct).ConfigureAwait(false);
        }
        var name = DocumentRegister.Safe(download.SuggestedFilename);
        if (!name.Contains(hit.DocumentNo, StringComparison.OrdinalIgnoreCase))
            name = DocumentRegister.Safe($"{hit.DocumentNo}_Rev{hit.Revision}_{name}");
        Directory.CreateDirectory(targetFolder);
        var path = ZipBundle.UniquePath(Path.Combine(targetFolder, name));
        await download.SaveAsAsync(path).ConfigureAwait(false);
        var failure = await download.FailureAsync().ConfigureAwait(false);
        if (failure != null) throw new IOException($"Download of {hit.DocumentNo} failed: {failure}");
        Say($"Saved {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>Downloads may start in the page or in a popup tab - listen on every page of the context.</summary>
    private async Task<IDownload> WaitForDownloadAnywhereAsync(Func<Task> action, float timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<IDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDownload(object? s, IDownload dl) => tcs.TrySetResult(dl);
        void Hook(IPage p) => p.Download += OnDownload;
        void OnPage(object? s, IPage p) => Hook(p);
        foreach (var p in _ctx!.Pages) Hook(p);
        _ctx.Page += OnPage;
        try
        {
            await action().ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            try { return await tcs.Task.ConfigureAwait(false); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No download started within {timeoutMs / 1000:N0} s. Check Documents.DownloadButton / DownloadConfirmButton in aconex.config.json.");
            }
        }
        finally
        {
            _ctx.Page -= OnPage;
            foreach (var p in _ctx.Pages) p.Download -= OnDownload;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_ctx != null) await _ctx.CloseAsync().ConfigureAwait(false); }
        catch (PlaywrightException) { /* browser already closed by the user */ }
        _pw?.Dispose();
        _ctx = null; _page = null; _pw = null;
    }
}
