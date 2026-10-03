using Raffaello.Core.AconexWeb;
using Raffaello.Core.Assistant;

namespace Raffaello.Core.Tests;

/// <summary>Two saved Aconex logins: vault round trip, account per task, login-page detection. Fake values only.</summary>
public sealed class AconexSavedLoginTests
{
    private const string FakeUser = "fake.user.mirwir";
    private const string FakePassword = "fake-Pa55-word-001";
    private static readonly LoginConfig Login = new();
    private const string Base = "https://ksa1.aconex.com";

    [Fact]
    public void Each_account_round_trips_through_the_vault_independently()
    {
        var secrets = new MemorySecretVault();
        var mir = new AconexAccountVault(secrets, AconexProfile.MirWir);
        var wf = new AconexAccountVault(secrets, AconexProfile.Workflows);
        Assert.False(mir.HasCredential);
        Assert.Equal("NOT SAVED", mir.Status);

        mir.Save(" " + FakeUser + " ", FakePassword);
        wf.Save("fake.user.workflows", "fake-other-pass");
        Assert.True(mir.HasCredential);
        Assert.Equal((FakeUser, FakePassword), mir.Load());
        Assert.Equal(("fake.user.workflows", "fake-other-pass"), wf.Load());

        // the status shown in the UI never carries the user name or the password
        Assert.DoesNotContain(FakeUser, mir.Status);
        Assert.DoesNotContain(FakePassword, mir.Status);
        Assert.StartsWith("SAVED", mir.Status);

        mir.Clear();
        Assert.Null(mir.Load());
        Assert.NotNull(wf.Load());
        Assert.Throws<ArgumentException>(() => mir.Save(FakeUser, ""));
    }

    [Fact]
    public void Dpapi_vault_keeps_the_login_encrypted_on_disk()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows only
        var folder = Path.Combine(Path.GetTempPath(), "raff-aconex-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var acc = new AconexAccountVault(new DpapiSecretVault(folder), AconexProfile.Workflows);
            acc.Save(FakeUser, FakePassword);
            Assert.Equal((FakeUser, FakePassword), new AconexAccountVault(new DpapiSecretVault(folder), AconexProfile.Workflows).Load());
            foreach (var f in Directory.GetFiles(folder))
            {
                var raw = File.ReadAllBytes(f);
                Assert.DoesNotContain(FakePassword, System.Text.Encoding.UTF8.GetString(raw));
                Assert.DoesNotContain(FakePassword, System.Text.Encoding.Unicode.GetString(raw));
            }
            acc.Clear();
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(AconexTask.WirMirRegisterSync, AconexProfile.MirWir)]
    [InlineData(AconexTask.WirMirDownload, AconexProfile.MirWir)]
    [InlineData(AconexTask.WirMirLookupByNumber, AconexProfile.MirWir)]
    [InlineData(AconexTask.WorkflowBoard, AconexProfile.Workflows)]
    [InlineData(AconexTask.WorkflowLookup, AconexProfile.Workflows)]
    [InlineData(AconexTask.WorkflowDownloads, AconexProfile.Workflows)]
    [InlineData(AconexTask.InvoicePackageUpload, AconexProfile.Workflows)]
    public void Each_task_picks_its_account(AconexTask task, AconexProfile expected)
    {
        Assert.Equal(expected, AconexProfiles.ForTask(task));
        var other = expected == AconexProfile.MirWir ? AconexProfile.Workflows : AconexProfile.MirWir;
        Assert.Equal(other, AconexProfiles.ForTask(task, other)); // override for one run
    }

    [Fact]
    public void Accounts_use_separate_browser_profiles_and_secret_names()
    {
        var cfg = new AconexConfig { ProfileDir = Path.Combine(Path.GetTempPath(), "aconex-profile") };
        Assert.NotEqual(AconexProfiles.ProfileDir(cfg, AconexProfile.MirWir), AconexProfiles.ProfileDir(cfg, AconexProfile.Workflows));
        Assert.EndsWith("-mirwir", AconexProfiles.ProfileDir(cfg, AconexProfile.MirWir));
        Assert.NotEqual(AconexProfiles.PasswordSecretName(AconexProfile.MirWir), AconexProfiles.PasswordSecretName(AconexProfile.Workflows));
        Assert.Equal("MIR / WIR", AconexProfiles.DisplayName(AconexProfile.MirWir));
        Assert.Equal("WORKFLOWS / INVOICE UPLOAD", AconexProfiles.DisplayName(AconexProfile.Workflows));
    }

    private static AconexLoginProbe Probe(string text = "", string url = Base + "/Logon", bool loggedIn = false, bool form = false, bool pwd = false, bool mfa = false, bool captcha = false)
        => new(url, text, loggedIn, form, pwd, mfa, captcha);

    [Fact]
    public void Login_pages_are_classified()
    {
        AconexLoginState C(AconexLoginProbe p) => AconexLoginDetector.Classify(p, Login, Base);
        Assert.Equal(AconexLoginState.LoginForm, C(Probe("Log on to Aconex  Forgot your password?", form: true, pwd: true)));
        Assert.Equal(AconexLoginState.WrongPassword, C(Probe("Invalid user name or password.", form: true, pwd: true)));
        Assert.Equal(AconexLoginState.LoggedIn, C(Probe("Documents  Workflows", url: Base + "/home", loggedIn: true)));
        Assert.Equal(AconexLoginState.Mfa, C(Probe("Enter the verification code", mfa: true)));
        Assert.Equal(AconexLoginState.Mfa, C(Probe("Two-step verification: enter the code from your authenticator")));
        // MFA words in a help line on the normal login form do not stop the automatic login
        Assert.Equal(AconexLoginState.LoginForm, C(Probe("Log on. Having trouble with your authenticator? Contact support.", form: true, pwd: true)));
        Assert.Equal(AconexLoginState.Captcha, C(Probe("Please confirm: I'm not a robot", form: true, pwd: true)));
        Assert.Equal(AconexLoginState.Captcha, C(Probe("", captcha: true)));
        Assert.Equal(AconexLoginState.PasswordExpired, C(Probe("Your password has expired")));
        Assert.Equal(AconexLoginState.AccountLocked, C(Probe("Your account is locked", form: true, pwd: true)));
        Assert.Equal(AconexLoginState.Unknown, C(Probe("Scheduled maintenance")));
        // a logged-in marker next to a login form is not a login
        Assert.Equal(AconexLoginState.LoginForm, C(Probe("", loggedIn: true, form: true, pwd: true)));
    }

    [Fact]
    public void Password_is_never_typed_into_another_site()
    {
        var sso = Probe("Sign in", url: "https://login.example-idp.test/authorize", form: true, pwd: true);
        Assert.Equal(AconexLoginState.Sso, AconexLoginDetector.Classify(sso, Login, Base));
        var cfg = new LoginConfig { AllowedLoginHosts = { "login.example-idp.test" } };
        Assert.Equal(AconexLoginState.LoginForm, AconexLoginDetector.Classify(sso, cfg, Base));
        Assert.True(AconexLoginDetector.IsAllowedLoginHost("about:blank", Login, Base));
        Assert.False(AconexLoginDetector.IsAllowedLoginHost("https://evil.example.test/Logon", Login, Base));
    }

    [Fact]
    public void Hand_over_messages_ask_for_continue()
    {
        foreach (var s in new[] { AconexLoginState.Mfa, AconexLoginState.Sso, AconexLoginState.Captcha, AconexLoginState.PasswordExpired, AconexLoginState.Unknown })
        {
            var m = AconexLoginDetector.HandOverMessage(s, "MIR / WIR");
            Assert.Contains("CONTINUE", m);
            Assert.Contains("MIR / WIR", m);
        }
    }

    [Fact]
    public void Config_and_settings_files_never_hold_the_login()
    {
        var secrets = new MemorySecretVault();
        new AconexAccountVault(secrets, AconexProfile.MirWir).Save(FakeUser, FakePassword);
        var json = new AconexConfig().ToJson() + System.Text.Json.JsonSerializer.Serialize(new AssistantSettings());
        Assert.DoesNotContain(FakePassword, json);
        Assert.DoesNotContain(FakeUser, json);
        // older config files without the new fields still load with the defaults
        var old = AconexConfig.FromJson("{\"BaseUrl\":\"https://ksa1.aconex.com\",\"Login\":{\"AutoFillStoredCredential\":true}}");
        Assert.NotEmpty(old.Login.WrongPasswordTexts);
        Assert.NotEmpty(old.Login.MfaInputSelector);
        Assert.Empty(old.Login.AllowedLoginHosts);
    }
}