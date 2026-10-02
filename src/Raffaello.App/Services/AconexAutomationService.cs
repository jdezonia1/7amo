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
    private PlaywrightAconexClient? _client;
    private bool? _clientHeadless;
    private string? _storePath;
    private IAconexStore? _store;
    private IVariationStore? _variations;

    public AconexAutomationService(ProjectService project)
    {
        _project = project;
        Vault = OperatingSystem.IsWindows() ? new DpapiCredentialVault() : new NoCredentialVault();
        Config = LoadConfigSafe(out _);
    }

    public event Action<string>? Log;
    public ICredentialVault Vault { get; }
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

    /// <summary>Runs work with the (shared) browser session. <paramref name="visible"/> forces a visible window (first login).</summary>
    public async Task<T> RunAsync<T>(Func<IAconexClient, Task<T>> work, bool visible = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var headless = Config.Headless && !visible;
            if (_client != null && _clientHeadless != headless) await CloseCoreAsync().ConfigureAwait(false);
            if (_client == null)
            {
                _client = new PlaywrightAconexClient(headless ? Config : CloneWithHeadless(false), Vault);
                _client.Log += Relay;
                _clientHeadless = headless;
            }
            try { return await work(_client).ConfigureAwait(false); }
            catch (Microsoft.Playwright.PlaywrightException) when (!ct.IsCancellationRequested)
            {
                // the user closed the browser window: start a new one and try once more
                Relay("Browser was closed - reopening.");
                await CloseCoreAsync().ConfigureAwait(false);
                _client = new PlaywrightAconexClient(headless ? Config : CloneWithHeadless(false), Vault);
                _client.Log += Relay;
                _clientHeadless = headless;
                return await work(_client).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
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

    private async Task CloseCoreAsync()
    {
        if (_client == null) return;
        _client.Log -= Relay;
        try { await _client.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { Relay("Close: " + ex.Message); }
        _client = null;
        _clientHeadless = null;
    }

    public async ValueTask DisposeAsync() => await CloseCoreAsync().ConfigureAwait(false);

    /// <summary>The DI host disposes synchronously on exit: close the browser, but never hang the shutdown.</summary>
    public void Dispose()
    {
        try { Task.Run(CloseCoreAsync).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* closing is best effort */ }
    }
}
