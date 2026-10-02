using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Raffaello.Core;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant;
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

    public AssistantHost(ProjectService project, IMaterialsStore materials, AconexAutomationService aconex)
    {
        _project = project;
        _materials = materials;
        _aconex = aconex;
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
        };
        Session = new AssistantSession(Data) { ApiKey = () => Key };
        ApplyModel();
        Current = this;
    }

    public AssistantSettings Settings { get; }
    public ISecretVault Vault { get; }
    public IAssistantStore Store { get; }
    public AssistantData Data { get; }
    public AssistantSession Session { get; }
    public ProjectService Project => _project;

    public string? Key => ApiKeys.Resolve(Vault, _project.Settings).Key;
    public ApiKeys.Source KeySource => ApiKeys.Resolve(Vault, _project.Settings).From;
    public bool IsOnline => !string.IsNullOrWhiteSpace(Key);
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
