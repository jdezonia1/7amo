using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Assistant;

/// <summary>
/// Per-user settings of the assistant, the morning brief, notifications and the interface language
/// (%APPDATA%\Raffaello\assistant.json). Secrets (API key, SMTP password, webhook URLs) are never in this file - see <see cref="ISecretVault"/>.
/// </summary>
public sealed class AssistantSettings
{
    // language
    /// <summary>"en" or "ar".</summary>
    public string Language { get; set; } = "en";

    // assistant
    /// <summary>Read tools may look at project data (rooms, ledger, invoices, DNs ...) and send the results to Claude. Default on.</summary>
    public bool AllowReadProjectData { get; set; } = true;
    /// <summary>Attached scans / images may be sent to Claude to be read (otherwise only the local text layer / OCR is used). Default off.</summary>
    public bool CloudDocumentReading { get; set; }
    public int MaxTokens { get; set; } = 16000;
    public int MaxToolRounds { get; set; } = 8;
    /// <summary>Characters of one tool result sent to the model (longer results are cut with a note).</summary>
    public int MaxToolResultChars { get; set; } = 24000;

    // brief
    public bool ShowBriefFirst { get; set; } = true;
    public string BriefTime { get; set; } = "07:30";
    /// <summary>Add a short natural-language summary written by Claude (sends the brief figures, no documents).</summary>
    public bool BriefClaudeSummary { get; set; }
    public DateTime? LastBriefShown { get; set; }

    // channels
    public bool InAppToasts { get; set; } = true;
    public bool WindowsToasts { get; set; } = true;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpFrom { get; set; } = "";
    public string EmailTo { get; set; } = "";
    /// <summary>Phone / recipient id passed to the WhatsApp webhook.</summary>
    public string WhatsAppTo { get; set; } = "";
    /// <summary>JSON body template for the WhatsApp webhook: {to}, {title}, {text} are replaced (JSON-escaped).</summary>
    public string WhatsAppTemplate { get; set; } = "{\"to\":\"{to}\",\"text\":\"{title}\\n{text}\"}";
    /// <summary>Optional header sent with the WhatsApp webhook, e.g. "Authorization" (value in the vault).</summary>
    public string WhatsAppHeaderName { get; set; } = "";
    public int NotifyEveryMinutes { get; set; } = 15;

    public static string DefaultPath => Path.Combine(AppSettings.SettingsFolder, "assistant.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AssistantSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(path), Json) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AssistantSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public bool IsArabic => string.Equals(Language, "ar", StringComparison.OrdinalIgnoreCase);
}

public static class SecretNames
{
    public const string AnthropicKey = "anthropic-api-key";
    public const string SmtpPassword = "smtp-password";
    public const string TeamsWebhook = "teams-webhook-url";
    public const string WhatsAppWebhook = "whatsapp-webhook-url";
    public const string WhatsAppHeaderValue = "whatsapp-header-value";
}

/// <summary>Secrets of this user on this PC. Never logged, never written in plain text.</summary>
public interface ISecretVault
{
    bool IsPersistent { get; }
    string? Get(string name);
    void Set(string name, string? value);
}

/// <summary>
/// Windows DPAPI (current user) per secret under %LOCALAPPDATA%\Raffaello\secrets. On other systems nothing is stored on disk:
/// secrets live for the session only (the API key can always come from ANTHROPIC_API_KEY).
/// </summary>
public sealed class DpapiSecretVault : ISecretVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Raffaello.Secrets.v1");
    private readonly string _folder;
    private readonly Dictionary<string, string> _session = new(StringComparer.Ordinal);

    public DpapiSecretVault(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "secrets");
    }

    public bool IsPersistent => OperatingSystem.IsWindows();
    private string FileOf(string name) => Path.Combine(_folder, string.Concat(name.Where(char.IsLetterOrDigit)) + ".bin");

    public string? Get(string name)
    {
        if (_session.TryGetValue(name, out var v)) return v;
        if (!OperatingSystem.IsWindows()) return null;
        var f = FileOf(name);
        if (!File.Exists(f)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(f), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain); }
        }
        catch (CryptographicException) { return null; }   // another Windows user / PC
    }

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            _session.Remove(name);
            if (OperatingSystem.IsWindows() && File.Exists(FileOf(name))) File.Delete(FileOf(name));
            return;
        }
        if (!OperatingSystem.IsWindows()) { _session[name] = value; return; }
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllBytes(FileOf(name), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
        }
        finally { Array.Clear(plain); }
    }
}

/// <summary>Session-only vault (tests, server without DPAPI).</summary>
public sealed class MemorySecretVault : ISecretVault
{
    private readonly Dictionary<string, string> _v = new();
    public bool IsPersistent => false;
    public string? Get(string name) => _v.GetValueOrDefault(name);
    public void Set(string name, string? value) { if (string.IsNullOrEmpty(value)) _v.Remove(name); else _v[name] = value; }
}

public static class ApiKeys
{
    /// <summary>Where the key came from (for the privacy / status line; never the key itself).</summary>
    public enum Source { None, Vault, Environment, LegacySettings }

    /// <summary>Vault (DPAPI) first, then ANTHROPIC_API_KEY, then a key still in settings.json from an older version.</summary>
    public static (string? Key, Source From) Resolve(ISecretVault vault, AppSettings settings)
    {
        if (vault.Get(SecretNames.AnthropicKey) is { Length: > 0 } k) return (k.Trim(), Source.Vault);
        if (Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env) return (env.Trim(), Source.Environment);
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey)) return (settings.AnthropicApiKey.Trim(), Source.LegacySettings);
        return (null, Source.None);
    }

    /// <summary>
    /// Moves a key typed in older versions (plain text in settings.json) into the DPAPI vault and blanks it in the file.
    /// Returns true when something moved. Only where the vault is persistent (Windows).
    /// </summary>
    public static bool MigrateLegacy(ISecretVault vault, AppSettings settings, Action? save = null)
    {
        if (!vault.IsPersistent || string.IsNullOrWhiteSpace(settings.AnthropicApiKey)) return false;
        vault.Set(SecretNames.AnthropicKey, settings.AnthropicApiKey.Trim());
        settings.AnthropicApiKey = "";
        (save ?? (() => settings.Save()))();
        return true;
    }

    /// <summary>"sk-ant-...7Qx" style hint for the UI.</summary>
    public static string Mask(string? key) => string.IsNullOrEmpty(key) ? "" : key.Length <= 12 ? new string('*', key.Length) : key[..7] + "..." + key[^4..];
}
