using Raffaello.Core.Assistant;

namespace Raffaello.Core.AconexWeb;

// =====================================================================================================
//  Two saved Aconex logins: one account for MIR / WIR, one for WORKFLOWS / INVOICE UPLOAD.
//  Each account has its own encrypted login (DPAPI vault) and its own browser profile (cookies), so both can stay
//  logged in at the same time. Passwords never go to settings files, the database, logs or screenshots.
// =====================================================================================================

/// <summary>The two Aconex user accounts.</summary>
public enum AconexProfile
{
    /// <summary>MIR and WIR: register sync, downloads, lookup by number.</summary>
    MirWir,
    /// <summary>Workflows (board, lookup, downloads) and invoice package upload.</summary>
    Workflows,
}

/// <summary>Every Aconex task the app runs; each picks its account automatically (<see cref="AconexProfiles.ForTask"/>).</summary>
public enum AconexTask
{
    WirMirRegisterSync,
    WirMirDownload,
    WirMirLookupByNumber,
    WorkflowBoard,
    WorkflowLookup,
    WorkflowDownloads,
    InvoicePackageUpload,
}

public static class AconexProfiles
{
    public static readonly AconexProfile[] All = { AconexProfile.MirWir, AconexProfile.Workflows };

    public static string DisplayName(AconexProfile p) => p == AconexProfile.MirWir ? "MIR / WIR" : "WORKFLOWS / INVOICE UPLOAD";

    /// <summary>Short key used in secret names and browser-profile folder names.</summary>
    public static string Key(AconexProfile p) => p == AconexProfile.MirWir ? "mirwir" : "workflows";

    /// <summary>Which account a task uses. <paramref name="overrideProfile"/> (chosen by the user for a run) wins.</summary>
    public static AconexProfile ForTask(AconexTask task, AconexProfile? overrideProfile = null)
    {
        if (overrideProfile is { } o) return o;
        return task switch
        {
            AconexTask.WirMirRegisterSync or AconexTask.WirMirDownload or AconexTask.WirMirLookupByNumber => AconexProfile.MirWir,
            _ => AconexProfile.Workflows,
        };
    }

    public static string UserSecretName(AconexProfile p) => $"aconex-{Key(p)}-user";
    public static string PasswordSecretName(AconexProfile p) => $"aconex-{Key(p)}-password";

    /// <summary>Separate browser profile (cookies / session) per account: the configured folder + "-mirwir" / "-workflows".</summary>
    public static string ProfileDir(AconexConfig cfg, AconexProfile p) => cfg.ResolvedProfileDir.TrimEnd('\\', '/') + "-" + Key(p);
}

/// <summary>
/// One account's login in the app's secret vault (<see cref="ISecretVault"/>; on Windows DPAPI for the current Windows
/// user). Implements <see cref="ICredentialVault"/> so the browser automation can use it unchanged.
/// </summary>
public sealed class AconexAccountVault : ICredentialVault
{
    private readonly ISecretVault _vault;

    public AconexAccountVault(ISecretVault vault, AconexProfile profile)
    {
        _vault = vault;
        Profile = profile;
    }

    public AconexProfile Profile { get; }
    public bool IsSupported => true;
    /// <summary>False where there is no DPAPI (the login then lives for this session only).</summary>
    public bool IsPersistent => _vault.IsPersistent;
    public bool HasCredential => !string.IsNullOrEmpty(_vault.Get(AconexProfiles.UserSecretName(Profile))) && !string.IsNullOrEmpty(_vault.Get(AconexProfiles.PasswordSecretName(Profile)));

    public void Save(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) throw new ArgumentException("User name and password are both needed.");
        _vault.Set(AconexProfiles.UserSecretName(Profile), userName.Trim());
        _vault.Set(AconexProfiles.PasswordSecretName(Profile), password);
    }

    public (string User, string Password)? Load()
    {
        var u = _vault.Get(AconexProfiles.UserSecretName(Profile));
        var p = _vault.Get(AconexProfiles.PasswordSecretName(Profile));
        return string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p) ? null : (u, p);
    }

    public void Clear()
    {
        _vault.Set(AconexProfiles.UserSecretName(Profile), null);
        _vault.Set(AconexProfiles.PasswordSecretName(Profile), null);
    }

    /// <summary>Status line for the UI: never the user name or password, only whether a login is saved.</summary>
    public string Status => HasCredential ? (IsPersistent ? "SAVED (encrypted for this Windows user)" : "SAVED FOR THIS SESSION ONLY (no Windows encryption here)") : "NOT SAVED";
}

// ----------------------------------------------------------------------------------------------- login page states

/// <summary>What the browser shows while logging in.</summary>
public enum AconexLoginState
{
    Unknown,
    LoggedIn,
    LoginForm,
    WrongPassword,
    Mfa,
    Sso,
    Captcha,
    PasswordExpired,
    AccountLocked,
}

/// <summary>What the automation saw on the page (visibility of the configured selectors + visible text + URL).</summary>
public sealed record AconexLoginProbe(string Url, string Text, bool LoggedInMarker, bool LoginForm, bool PasswordInput, bool MfaInput, bool CaptchaElement);

/// <summary>Turns a page probe into a login state. Pure logic (unit tested); the browser part only collects the probe.</summary>
public static class AconexLoginDetector
{
    public static AconexLoginState Classify(AconexLoginProbe p, LoginConfig cfg, string baseUrl)
    {
        var text = p.Text ?? "";
        bool Has(IEnumerable<string> words) => words.Any(w => w.Length > 0 && text.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (p.CaptchaElement || Has(cfg.CaptchaTexts)) return AconexLoginState.Captcha;
        if (p.LoggedInMarker && !p.LoginForm && !p.PasswordInput) return AconexLoginState.LoggedIn;
        // never type the Aconex password into a page of another site (SSO / identity provider)
        if (!IsAllowedLoginHost(p.Url, cfg, baseUrl)) return AconexLoginState.Sso;
        if (Has(cfg.PasswordExpiredTexts)) return AconexLoginState.PasswordExpired;
        if (Has(cfg.AccountLockedTexts)) return AconexLoginState.AccountLocked;
        // MFA words on a page that still asks for the password (e.g. a help line) do not count
        if (p.MfaInput || (Has(cfg.MfaTexts) && !p.PasswordInput)) return AconexLoginState.Mfa;
        if (p.LoginForm || p.PasswordInput) return Has(cfg.WrongPasswordTexts) ? AconexLoginState.WrongPassword : AconexLoginState.LoginForm;
        if (Has(cfg.SsoTexts)) return AconexLoginState.Sso;
        return AconexLoginState.Unknown;
    }

    /// <summary>True when the page belongs to the Aconex site (BaseUrl host) or a host listed in Login.AllowedLoginHosts.</summary>
    public static bool IsAllowedLoginHost(string url, LoginConfig cfg, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return true;
        if (u.Scheme is not ("http" or "https")) return true;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) && string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase)) return true;
        return cfg.AllowedLoginHosts.Any(h => string.Equals(h.Trim(), u.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Plain-English instruction shown to the user when the app hands the login over.</summary>
    public static string HandOverMessage(AconexLoginState s, string accountName) => s switch
    {
        AconexLoginState.Mfa => $"Aconex asks for a one-time / verification code ({accountName}). Finish the login in the browser, then press CONTINUE.",
        AconexLoginState.Sso => $"Aconex sent the login to a single sign-on page ({accountName}). Finish the login in the browser, then press CONTINUE.",
        AconexLoginState.Captcha => $"Aconex shows an 'I am not a robot' check ({accountName}). Finish the login in the browser, then press CONTINUE.",
        AconexLoginState.PasswordExpired => $"Aconex says the password has expired ({accountName}). Change it in the browser, save the new one in Settings > Aconex, then press CONTINUE.",
        AconexLoginState.LoginForm => $"Log in to Aconex in the browser ({accountName}), then press CONTINUE.",
        _ => $"Aconex shows a page the app does not recognise ({accountName}). Finish the login in the browser, then press CONTINUE.",
    };
}

/// <summary>Aconex refused the saved login (wrong password / locked). The app does not try again on its own.</summary>
public sealed class AconexLoginFailedException : Exception
{
    public AconexLoginFailedException(string message, AconexLoginState state) : base(message) { State = state; }
    public AconexLoginState State { get; }
}