using System.Text;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Assistant;

namespace Raffaello.Automation.Tests;

/// <summary>
/// Saved Aconex logins (two accounts) against the LOCAL mock site only - fake users / passwords, no real Aconex.
/// Login-state detection on fixture pages, one automatic attempt, MFA hand-over, separate sessions per account,
/// and no login values in logs, messages or the browser profile.
/// </summary>
public sealed class AconexSavedLoginE2ETests : IDisposable
{
    private readonly MockAconexServer _server = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "raff-logins-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretVault _secrets = new();
    private readonly List<string> _said = new();

    public AconexSavedLoginE2ETests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AconexConfig Cfg(bool visible = false)
    {
        var c = _server.Config(Path.Combine(_root, "profile"), _root);
        BrowserEnv.Apply(c);
        if (visible)
        {
            // the client believes the window is visible (so it hands the login over); the test browser still runs headless
            c.Headless = false;
            c.BrowserArgs.Add("--headless=new");
            c.ManualLoginTimeoutSec = 30;
        }
        return c;
    }

    private PlaywrightAconexClient Client(AconexConfig cfg, AconexProfile p)
    {
        var client = new PlaywrightAconexClient(cfg, new AconexAccountVault(_secrets, p), null, AconexProfiles.ProfileDir(cfg, p), AconexProfiles.DisplayName(p));
        client.Log += s => { lock (_said) _said.Add(s); };
        client.LoginAttention += s => { lock (_said) _said.Add(s); };
        return client;
    }

    private void SaveFake(AconexProfile p, string user, string pw) => new AconexAccountVault(_secrets, p).Save(user, pw);

    // ---------------------------------------------------------------- fixture pages

    [SkippableFact]
    public async Task Login_state_is_detected_on_fixture_pages()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        _server.Fixtures["login"] = MockAconexServer.LoginPage("");
        _server.Fixtures["error"] = MockAconexServer.LoginPage("Invalid user name or password.");
        _server.Fixtures["mfa"] = MockAconexServer.MfaPage("");
        _server.Fixtures["home"] = MockAconexServer.Shell("Home", "<p>Welcome</p>");
        _server.Fixtures["captcha"] = MockAconexServer.Page("Check", "<div class='content'><div class='g-recaptcha' style='width:300px;height:80px;border:1px solid #ccc'>I'm not a robot</div></div>");
        _server.Fixtures["expired"] = MockAconexServer.Page("Expired", "<div class='content'><p>Your password has expired. Choose a new one.</p><input type='password' name='newPassword'></div>");
        _server.Fixtures["locked"] = MockAconexServer.LoginPage("Your account is locked. Contact your administrator.");
        _server.Fixtures["other"] = MockAconexServer.Page("Maintenance", "<div class='content'><p>Scheduled maintenance.</p></div>");

        var cfg = Cfg();
        await using var client = Client(cfg, AconexProfile.MirWir);
        async Task<AconexLoginState> At(string name) => await client.DetectLoginStateAsync(_server.BaseUrl + "/Fixture/" + name);

        Assert.Equal(AconexLoginState.LoginForm, await At("login"));
        Assert.Equal(AconexLoginState.WrongPassword, await At("error"));
        Assert.Equal(AconexLoginState.Mfa, await At("mfa"));
        Assert.Equal(AconexLoginState.LoggedIn, await At("home"));
        Assert.Equal(AconexLoginState.Captcha, await At("captcha"));
        Assert.Equal(AconexLoginState.PasswordExpired, await At("expired"));
        Assert.Equal(AconexLoginState.AccountLocked, await At("locked"));
        Assert.Equal(AconexLoginState.Unknown, await At("other"));
    }

    // ---------------------------------------------------------------- automatic login

    [SkippableFact]
    public async Task Saved_login_is_typed_once_and_the_task_continues()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        SaveFake(AconexProfile.Workflows, MockAconexServer.WorkflowsUser, MockAconexServer.WorkflowsPassword);
        await using var client = Client(Cfg(), AconexProfile.Workflows);
        var r = await client.LookupWorkflowAsync("WF-000123", Path.Combine(_root, "shots"));

        Assert.Equal(5, r.Steps.Count);
        Assert.Equal(1, client.AutoLoginAttempts);
        Assert.Equal(new[] { MockAconexServer.WorkflowsUser }, _server.LoggedInUsers);
        Assert.Equal(0, _server.FailedLoginCount);
    }

    [SkippableFact]
    public async Task Wrong_password_gives_a_clear_message_and_is_never_retried()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        SaveFake(AconexProfile.MirWir, MockAconexServer.UserName, "not-the-fake-password-42");
        await using var client = Client(Cfg(), AconexProfile.MirWir);

        var ex = await Assert.ThrowsAsync<AconexLoginFailedException>(() => client.EnsureLoggedInAsync());
        Assert.Contains("did not accept", ex.Message);
        Assert.Contains("MIR / WIR", ex.Message);
        Assert.Equal(AconexLoginState.WrongPassword, ex.State);
        Assert.True(client.AutoLoginBlocked);

        // a second run in the same session must not type the password again (account lockout)
        await Assert.ThrowsAsync<AconexLoginRequiredException>(() => client.EnsureLoggedInAsync());
        await Assert.ThrowsAsync<AconexLoginRequiredException>(() => client.LookupWorkflowAsync("WF-000123", Path.Combine(_root, "shots")));
        Assert.Equal(1, _server.FailedLoginCount);
        Assert.Equal(1, client.AutoLoginAttempts);
        Assert.Equal(0, _server.LoginCount);
    }

    [SkippableFact]
    public async Task Manual_mode_does_not_type_the_saved_login()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        SaveFake(AconexProfile.MirWir, MockAconexServer.UserName, MockAconexServer.Password);
        var cfg = Cfg();
        cfg.Login.AutoFillStoredCredential = false;
        await using var client = Client(cfg, AconexProfile.MirWir);
        await Assert.ThrowsAsync<AconexLoginRequiredException>(() => client.EnsureLoggedInAsync());
        Assert.Equal(0, client.AutoLoginAttempts);
        Assert.Equal(0, _server.LoginCount + _server.FailedLoginCount);
    }

    // ---------------------------------------------------------------- MFA hand-over

    [SkippableFact]
    public async Task Mfa_page_is_handed_to_the_user_and_the_run_resumes_when_the_login_is_done()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        _server.RequireMfa = true;
        SaveFake(AconexProfile.MirWir, MockAconexServer.UserName, MockAconexServer.Password);
        await using var client = Client(Cfg(visible: true), AconexProfile.MirWir);
        var asked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LoginAttention += s => { if (s.Length > 0) asked.TrySetResult(s); };

        var login = client.EnsureLoggedInAsync();
        var msg = await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("CONTINUE", msg);
        Assert.Contains("verification code", msg);
        Assert.False(login.IsCompleted);

        // the "user" types the fake one-time code in the browser; the app notices the login by itself
        await client.Page!.FillAsync("#otp", MockAconexServer.MfaCode);
        await client.Page.ClickAsync("#verify");
        await login.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _server.LoginCount);
        Assert.Equal(1, client.AutoLoginAttempts);
        Assert.Equal(AconexLoginState.LoggedIn, client.LastLoginState);
    }

    [SkippableFact]
    public async Task Continue_button_resumes_when_the_logged_in_marker_is_not_recognised()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        _server.RequireMfa = true;
        SaveFake(AconexProfile.Workflows, MockAconexServer.WorkflowsUser, MockAconexServer.WorkflowsPassword);
        var cfg = Cfg(visible: true);
        cfg.Login.LoggedInSelector = "#a-marker-this-site-does-not-have";
        await using var client = Client(cfg, AconexProfile.Workflows);
        var asked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.LoginAttention += s => { if (s.Length > 0) asked.TrySetResult(s); };

        var login = client.EnsureLoggedInAsync();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await client.Page!.FillAsync("#otp", MockAconexServer.MfaCode);
        await client.Page.ClickAsync("#verify");
        await client.Page.WaitForSelectorAsync("#nav-bar");
        Assert.False(login.IsCompleted);
        client.ContinueLogin();
        await login.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { MockAconexServer.WorkflowsUser }, _server.LoggedInUsers);
    }

    [SkippableFact]
    public async Task Cancel_stops_the_wait_for_the_user()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        _server.RequireMfa = true;
        SaveFake(AconexProfile.MirWir, MockAconexServer.UserName, MockAconexServer.Password);
        await using var client = Client(Cfg(visible: true), AconexProfile.MirWir);
        using var cts = new CancellationTokenSource();
        client.LoginAttention += s => { if (s.Length > 0) cts.CancelAfter(500); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EnsureLoggedInAsync(cts.Token));
    }

    // ---------------------------------------------------------------- two accounts

    [SkippableFact]
    public async Task Each_account_has_its_own_browser_session()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        SaveFake(AconexProfile.MirWir, MockAconexServer.UserName, MockAconexServer.Password);
        SaveFake(AconexProfile.Workflows, MockAconexServer.WorkflowsUser, MockAconexServer.WorkflowsPassword);
        var cfg = Cfg();
        await using (var a = Client(cfg, AconexProfile.MirWir))
        await using (var b = Client(cfg, AconexProfile.Workflows))
        {
            await a.EnsureLoggedInAsync();
            await b.EnsureLoggedInAsync();   // both open at the same time
        }
        Assert.Equal(new[] { MockAconexServer.UserName, MockAconexServer.WorkflowsUser }, _server.LoggedInUsers);
        Assert.NotEqual(AconexProfiles.ProfileDir(cfg, AconexProfile.MirWir), AconexProfiles.ProfileDir(cfg, AconexProfile.Workflows));

        // next run: each profile folder still holds its own session, no new login
        new AconexAccountVault(_secrets, AconexProfile.MirWir).Clear();
        new AconexAccountVault(_secrets, AconexProfile.Workflows).Clear();
        await using (var a = Client(cfg, AconexProfile.MirWir)) await a.EnsureLoggedInAsync();
        await using (var b = Client(cfg, AconexProfile.Workflows)) await b.EnsureLoggedInAsync();
        Assert.Equal(2, _server.LoginCount);
    }

    // ---------------------------------------------------------------- nothing leaks

    [SkippableFact]
    public async Task Login_values_never_appear_in_logs_messages_or_the_browser_profile()
    {
        Skip.If(BrowserEnv.SkipReason != null, BrowserEnv.SkipReason);
        var cfg = Cfg();
        SaveFake(AconexProfile.Workflows, MockAconexServer.WorkflowsUser, MockAconexServer.WorkflowsPassword);
        await using (var ok = Client(cfg, AconexProfile.Workflows)) await ok.LookupWorkflowAsync("WF-000123", Path.Combine(_root, "shots"));

        const string wrong = "fake-wrong-pass-9917";
        SaveFake(AconexProfile.MirWir, "fake.mirwir.user", wrong);
        var messages = new List<string>();
        await using (var bad = Client(cfg, AconexProfile.MirWir))
        {
            try { await bad.EnsureLoggedInAsync(); }
            catch (AconexLoginFailedException ex) { messages.Add(ex.ToString()); }
        }
        Assert.Single(messages);
        lock (_said) messages.AddRange(_said);
        Assert.NotEmpty(_said);
        foreach (var secret in new[] { MockAconexServer.WorkflowsPassword, MockAconexServer.WorkflowsUser, wrong, "fake.mirwir.user" })
            foreach (var m in messages) Assert.DoesNotContain(secret, m);

        // the browser was told not to keep its own copy (password manager / autofill off)
        foreach (var p in AconexProfiles.All)
        {
            var prefs = File.ReadAllText(Path.Combine(AconexProfiles.ProfileDir(cfg, p), "Default", "Preferences"));
            Assert.Contains("\"credentials_enable_service\":false", prefs);
        }
        // no file in the profiles, screenshots or config holds the passwords in plain text
        var cfgFile = Path.Combine(_root, "aconex.config.json");
        cfg.Save(cfgFile);
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(f); } catch (IOException) { continue; } // file still locked by the browser
            foreach (var secret in new[] { MockAconexServer.WorkflowsPassword, wrong })
            {
                Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(secret)) < 0, $"{secret} found in {f}");
                Assert.True(IndexOf(bytes, Encoding.Unicode.GetBytes(secret)) < 0, $"{secret} found in {f}");
            }
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}