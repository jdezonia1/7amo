using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Raffaello.Core;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant;
using Raffaello.Core.Assistant.ClaudeCode;
using Raffaello.Core.Materials;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Raffaello.App.Services.Assistant;

/// <summary>
/// The assistant's plumbing in the app: per-user settings (assistant.json), the DPAPI secret vault, the store (data file or server,
/// chosen per call), the data sources of the tools (project, materials, Aconex, variations) and the session.
/// </summary>
public sealed class AssistantHost
{
    public static AssistantHost? Current { get; private set; }

    private readonly ProjectService _project;
    private readonly IMaterialsStore _materials;
    private readonly AconexAutomationService _aconex;
    private readonly Raffaello.Core.Documents.IDocumentStore _documents;

    /// <summary>Obligations calendar of the signed contracts (morning brief "obligations due").</summary>
    public IReadOnlyList<Raffaello.Core.Contracts.Rules.Obligation> Obligations() => Raffaello.Core.Wiring.ContractObligations.Build(_documents, _project.Snapshot);

    public AssistantHost(ProjectService project, IMaterialsStore materials, AconexAutomationService aconex,
        Raffaello.App.ViewModels.Insights.InsightsHub insights, Raffaello.Core.Documents.IDocumentStore documents)
    {
        _project = project;
        _materials = materials;
        _aconex = aconex;
        _documents = documents;
        Settings = AssistantSettings.Load();
        Vault = new DpapiSecretVault();
        ApiKeys.UseVaultForSettings(Vault);
        ApiKeys.MigrateLegacy(Vault, project.Settings, () => project.Settings.Save());
        Store = new AssistantStoreSelector(() => project.Store);
        Data = new AssistantData
        {
            Project = project, Store = Store, Settings = Settings,
            Materials = () => _materials.Load(),
            Aconex = () => _aconex.Store,
            Variations = () => _aconex.Variations,
            // cross-module wiring: Insights engine for list_anomalies (built-in checks stay the fallback), the Documents FTS archive for
            // search_documents, contract intelligence for the obligations calendar in get_contract_terms
            Anomalies = () => Raffaello.Core.Wiring.InsightsAnomalies.ToItems(insights.Compute(project, null, false, out _, out _)),
            Documents = new Raffaello.Core.Wiring.ArchiveDocumentSearch(() => _documents),
            ContractDocs = () => _documents,
        };
        Session = new AssistantSession(Data) { ApiKey = () => Key };
        // [claude-login] engine per question (AUTO / API KEY / CLAUDE LOGIN / OFFLINE) and the Claude Code runner
        Session.Router = () => Route();
        Session.ClaudeCode = () => _claude.Ready ? new ClaudeCodeRunner(_claude.Path, McpCommand, McpArgs()) : null;
        ApplyModel();
        Current = this;
        _ = Task.Run(() => CheckClaudeCode());
    }

    // ------------------------------------------------------------------ [claude-login]

    private volatile ClaudeCodeStatus _claude = ClaudeCodeStatus.Unknown;
    private readonly object _probeLock = new();

    /// <summary>Last known state of Claude Code on this PC (checked at start and with CHECK in Settings).</summary>
    public ClaudeCodeStatus ClaudeStatus => _claude;

    /// <summary>Finds Claude Code and checks the login (a few seconds; call off the UI thread).</summary>
    public ClaudeCodeStatus CheckClaudeCode()
    {
        lock (_probeLock)
        {
            try { _claude = ClaudeCodeLocator.Probe(Settings.ClaudeCodePath); }
            catch (Exception ex) { _claude = new ClaudeCodeStatus(false, "", "", null, "", "Claude Code check failed: " + ex.Message); }
            return _claude;
        }
    }

    /// <summary>The engine the next question will use. Never blocks the UI thread (uses the last check there).</summary>
    public RouteDecision Route()
    {
        var provider = AssistantProviders.Normalize(Settings.Provider);
        var needsClaude = provider == AssistantProviders.ClaudeLogin || (provider == AssistantProviders.Auto && !IsKeySet);
        var onUi = System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;
        if (needsClaude && !onUi && (_claude == ClaudeCodeStatus.Unknown || DateTime.Now - _claude.CheckedAt > TimeSpan.FromMinutes(10) || !_claude.Ready))
            CheckClaudeCode();
        var d = RouteDecision.Decide(provider, IsKeySet, _claude);
        if (d.Route == AssistantRoute.ClaudeCode && ServerMode)
            return new RouteDecision(AssistantRoute.Offline, "CLAUDE LOGIN reads the local data file; it is not available in SERVER mode yet. Use an API key or OFFLINE.", true);
        return d;
    }

    private bool IsKeySet => !string.IsNullOrWhiteSpace(Key);

    /// <summary>The MCP server program: this Raffaello.exe started with --mcp (same install, same data file).</summary>
    public static string McpCommand => Environment.ProcessPath is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "Raffaello.exe");

    public List<string> McpArgs() => new() { "--mcp", "--db", _project.Settings.DataFilePath, "--user", Data.User };

    /// <summary>The snippet to paste into Claude Desktop's config (shown in Settings, never written by the app).</summary>
    public string DesktopSnippet() => ClaudeDesktopConfig.Snippet(McpCommand, new List<string> { "--mcp", "--db", _project.Settings.DataFilePath });

    public AssistantSettings Settings { get; }
    public ISecretVault Vault { get; }
    public IAssistantStore Store { get; }
    public AssistantData Data { get; }
    public AssistantSession Session { get; }
    public ProjectService Project => _project;

    public string? Key => ApiKeys.Resolve(Vault, _project.Settings).Key;
    public ApiKeys.Source KeySource => ApiKeys.Resolve(Vault, _project.Settings).From;
    public bool IsOnline => Route().Route != AssistantRoute.Offline;
    public bool ServerMode => _project.Store is Raffaello.Core.Remote.RemoteProjectStore;

    public void ApplyModel()
    {
        var s = _project.Settings;
        Session.Model = string.IsNullOrWhiteSpace(s.AnthropicModel) ? AnthropicClient.DefaultModel : s.AnthropicModel.Trim();
        Session.Effort = string.IsNullOrWhiteSpace(s.AnthropicEffort) ? "medium" : s.AnthropicEffort;
        Session.UseServerFallbacks = s.UseServerFallbacks;
    }

    public void SaveSettings() => Settings.Save();

    public AttachmentReader AttachmentReader() => new(Settings.CloudDocumentReading, OcrImageAsync);

    /// <summary>Local OCR of an image with Windows.Media.Ocr (English first, Arabic when installed). Null when no engine is available.</summary>
    public static async Task<string?> OcrImageAsync(byte[] bytes, string mediaType, CancellationToken ct)
    {
        try
        {
            var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            var engine = lang != null ? OcrEngine.TryCreateFromLanguage(lang) : OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null) return null;
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer());
            ms.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ms);
            using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bmp);
            return string.Join("\n", result.Lines.Select(l => l.Text));
        }
        catch (Exception ex) when (ex is IOException or COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
