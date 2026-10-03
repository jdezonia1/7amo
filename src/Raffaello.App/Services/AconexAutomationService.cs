using System.IO;
using Raffaello.Automation;
using Raffaello.Core;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Variations;

namespace Raffaello.App.Services;

/// <summary>
/// [phase4] One place for the Aconex browser session, the config file and the phase-4 stores (their tables live in the
/// same data file as the project). The browser is opened on first use and kept open between lookups; only one
/// automation runs at a time.
/// </summary>
public sealed class AconexAutomationService : IAsyncDisposable, IDisposable
{
    private readonly ProjectService _project;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // one browser (own profile folder = own cookies) per Aconex account, so both can stay logged in
    private readonly Dictionary<AconexProfile, (PlaywrightAconexClient Client, bool Headless)> _clients = new();
    private readonly HashSet<AconexProfile> _autoLoginBlocked = new();
    private PlaywrightAconexClient? _current;
    private string? _storePath;
    private IAconexStore? _store;
    private IVariationStore? _variations;

    public AconexAutomationService(ProjectService project)
    {
        _project = project;
        Vault = OperatingSystem.IsWindows() ? new DpapiCredentialVault() : new NoCredentialVault();
        Secrets = OperatingSystem.IsWindows() ? new Raffaello.Core.Assistant.DpapiSecretVault() : new Raffaello.Core.Assistant.MemorySecretVault();
        Config = LoadConfigSafe(out _);
    }

    public event Action<string>? Log;
    /// <summary>Instruction when the user must finish an Aconex login in the browser ("" = over). Raised on a worker thread.</summary>
    public event Action<string>? LoginAttention;
    /// <summary>Old single stored login (before the two accounts). No longer used for logging in; Forget removes it.</summary>
    public ICredentialVault Vault { get; }
    /// <summary>The app's DPAPI secret vault (current Windows user) holding the two Aconex logins.</summary>
    public Raffaello.Core.Assistant.ISecretVault Secrets { get; }
    public AconexAccountVault Account(AconexProfile p) => new(Secrets, p);
    /// <summary>Account chosen by the user for the next runs (null = automatic per task).</summary>
    public AconexProfile? ProfileOverride { get; set; }
    public string LoginAttentionText { get; private set; } = "";

    /// <summary>Saves one account's login (encrypted) and allows the automatic login again after a refusal.</summary>
    public void SaveAccount(AconexProfile p, string user, string password)
    {
        Account(p).Save(user, password);
        lock (_autoLoginBlocked) _autoLoginBlocked.Remove(p);
        if (Vault.HasCredential) Vault.Clear(); // the old single login is replaced by the two accounts
    }

    /// <summary>Removes the saved login, closes that account's browser and deletes its browser session (cookies).</summary>
    public async Task ForgetAccountAsync(AconexProfile p)
    {
        Account(p).Clear();
        lock (_autoLoginBlocked) _autoLoginBlocked.Remove(p);
        await CloseProfileAsync(p).ConfigureAwait(false);
        var dir = AconexProfiles.ProfileDir(Config, p);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Relay("Could not delete the browser session folder: " + ex.Message); }
    }

    /// <summary>The user finished the login in the browser and pressed CONTINUE.</summary>
    public void ContinueLogin() => _current?.ContinueLogin();
    public AconexConfig Config { get; private set; }
    public string ConfigError { get; private set; } = "";
    public bool IsBusy => _gate.CurrentCount == 0;

    public string ConfigPath => string.IsNullOrWhiteSpace(_project.Settings.AconexConfigPath) ? AconexConfig.DefaultPath : AconexConfig.Expand(_project.Settings.AconexConfigPath);

    private AconexConfig LoadConfigSafe(out string error)
    {
        try { error = ""; ConfigError = ""; return WithProjectFolders(AconexConfig.Load(ConfigPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            error = $"{ConfigPath}: {ex.Message}";
            ConfigError = error;
            return new AconexConfig();
        }
    }

    /// <summary>[phase6] Empty Aconex folders fall back to the shared documents folder (Settings > Documents root).</summary>
    private AconexConfig WithProjectFolders(AconexConfig c)
    {
        var f = Raffaello.Core.Settings.ProjectFolders.From(_project.Settings);
        if (string.IsNullOrWhiteSpace(c.Folders.WirFolder)) c.Folders.WirFolder = f.Wir;
        if (string.IsNullOrWhiteSpace(c.Folders.MirFolder)) c.Folders.MirFolder = f.Mir;
        if (string.IsNullOrWhiteSpace(c.Folders.OtherFolder)) c.Folders.OtherFolder = f.Other;
        if (string.IsNullOrWhiteSpace(c.Folders.ScreenshotFolder) || c.Folders.ScreenshotFolder == new Raffaello.Core.AconexWeb.FolderConfig().ScreenshotFolder)
            c.Folders.ScreenshotFolder = f.AconexScreenshots;
        return c;
    }

    /// <summary>Reads aconex.config.json again (after the user edited it). Returns the problem, or "" when fine.</summary>
    public string ReloadConfig()
    {
        Config = LoadConfigSafe(out var error);
        var problems = Config.Validate();
        return error.Length > 0 ? error : string.Join("; ", problems);
    }

    public void SaveConfig() => Config.Save(ConfigPath);

    // ------------------------------------------------------------------ stores

    private void EnsureStores()
    {
        var path = _project.DataLocation;
        if (_storePath == path && _store != null && _variations != null) return;
        var user = _project.Settings.EffectiveUserName;
        // [phase6] server mode: the Aconex and variation tables live on the server
        if (_project.Store is Raffaello.Core.Remote.RemoteProjectStore remote)
        {
            _store = new Raffaello.Core.Remote.RemoteAconexStore(remote) { User = user };
            _variations = new Raffaello.Core.Remote.RemoteVariationStore(remote) { User = user };
        }
        else
        {
            _store = new SqliteAconexStore(path, user);
            _store.EnsureSchema();
            _variations = new SqliteVariationStore(path, user);
            _variations.EnsureSchema();
        }
        _storePath = path;
    }

    public IAconexStore Store { get { EnsureStores(); _store!.User = _project.Settings.EffectiveUserName; return _store; } }
    public IVariationStore Variations { get { EnsureStores(); _variations!.User = _project.Settings.EffectiveUserName; return _variations; } }

    // ------------------------------------------------------------------ browser

    /// <summary>
    /// Runs work in the browser of the account that <paramref name="task"/> uses (or <see cref="ProfileOverride"/>).
    /// <paramref name="visible"/> forces a visible window (first login / MFA).
    /// </summary>
    public async Task<T> RunAsync<T>(AconexTask task, Func<IAconexClient, Task<T>> work, bool visible = false, CancellationToken ct = default, AconexProfile? profile = null)
    {
        var p = profile ?? AconexProfiles.ForTask(task, ProfileOverride);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // [trust] begin: Oracle Aconex REST API instead of the browser when aconex-api.json says Enabled
            var apiCfg = Raffaello.Core.Integrations.AconexApi.AconexApiConfig.LoadSafe();
            if (apiCfg.Enabled)
            {
                await using var api = new Raffaello.Core.Integrations.AconexApi.AconexApiClient(apiCfg, Config);
                api.Log += Relay;
                return await work(api).ConfigureAwait(false);
            }
            // [trust] end
            var headless = Config.Headless && !visible;
            Relay($"Aconex account: {AconexProfiles.DisplayName(p)}{(profile is null && ProfileOverride is null ? "" : " (chosen by you)")}");
            var client = await ClientForAsync(p, headless).ConfigureAwait(false);
            try { return await work(client).ConfigureAwait(false); }
            catch (Microsoft.Playwright.PlaywrightException) when (!ct.IsCancellationRequested)
            {
                // the user closed the browser window: start a new one and try once more
                Relay("Browser was closed - reopening.");
                await CloseProfileCoreAsync(p).ConfigureAwait(false);
                client = await ClientForAsync(p, headless).ConfigureAwait(false);
                return await work(client).ConfigureAwait(false);
            }
            finally
            {
                if (client.AutoLoginBlocked) lock (_autoLoginBlocked) _autoLoginBlocked.Add(p);
                _current = null;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Opens Aconex with one account (visible) and logs in: SETTINGS > ACONEX > TEST LOGIN.</summary>
    public Task<bool> TestLoginAsync(AconexProfile p, CancellationToken ct = default) =>
        RunAsync(AconexTask.WorkflowLookup, async c => { await c.EnsureLoggedInAsync(ct).ConfigureAwait(false); return true; }, visible: true, ct, profile: p);

    private async Task<PlaywrightAconexClient> ClientForAsync(AconexProfile p, bool headless)
    {
        if (_clients.TryGetValue(p, out var existing) && existing.Headless != headless) await CloseProfileCoreAsync(p).ConfigureAwait(false);
        if (!_clients.TryGetValue(p, out existing))
        {
            var cfg = headless ? Config : CloneWithHeadless(false);
            var client = new PlaywrightAconexClient(cfg, Account(p), null, AconexProfiles.ProfileDir(Config, p), AconexProfiles.DisplayName(p));
            client.Log += Relay;
            client.LoginAttention += OnLoginAttention;
            existing = (client, headless);
            _clients[p] = existing;
        }
        lock (_autoLoginBlocked) existing.Client.AutoLoginBlocked = _autoLoginBlocked.Contains(p);
        _current = existing.Client;
        return existing.Client;
    }

    private void OnLoginAttention(string text)
    {
        LoginAttentionText = text;
        LoginAttention?.Invoke(text);
    }

    private AconexConfig CloneWithHeadless(bool headless)
    {
        var c = AconexConfig.FromJson(Config.ToJson());
        c.Headless = headless;
        return c;
    }

    private void Relay(string s) => Log?.Invoke(s);

    public async Task CloseBrowserAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CloseProfileAsync(AconexProfile p)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseProfileCoreAsync(p).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CloseProfileCoreAsync(AconexProfile p)
    {
        if (!_clients.Remove(p, out var entry)) return;
        entry.Client.Log -= Relay;
        entry.Client.LoginAttention -= OnLoginAttention;
        try { await entry.Client.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Relay("Close: " + ex.Message); }
    }

    private async Task CloseCoreAsync()
    {
        foreach (var p in _clients.Keys.ToList()) await CloseProfileCoreAsync(p).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CloseCoreAsync().ConfigureAwait(false);

    /// <summary>The DI host disposes synchronously on exit: close the browser, but never hang the shutdown.</summary>
    public void Dispose()
    {
        try { Task.Run(CloseCoreAsync).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* closing is best effort */ }
    }
}
