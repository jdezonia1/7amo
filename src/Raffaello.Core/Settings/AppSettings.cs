using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raffaello.Core.Settings;

/// <summary>Per-user settings, stored as JSON in %APPDATA%\Raffaello\settings.json (never on the shared drive).</summary>
public sealed class AppSettings
{
    public string UserName { get; set; } = "";
    public string DataFilePath { get; set; } = DefaultDataPath();
    public string Theme { get; set; } = "Light";
    public string Accent { get; set; } = "Red";
    public bool SeedDemoData { get; set; } = true;
    public string Project { get; set; } = "RAFFLES HOTEL & BRANDED RESIDENCES";

    // rules
    public int WirDueDays { get; set; } = 14;
    public double SiteTolerance { get; set; } = 0.15;
    public double PipeLengthM { get; set; } = 3.0;

    // assistant
    // [assistant] begin: the API key lives in the Windows DPAPI vault once the app registers it (ProtectedApiKey); settings.json keeps a
    // plain key only where no protected store exists. Every module keeps reading AnthropicApiKey as before.
    [JsonIgnore]
    public string AnthropicApiKey
    {
        get => !string.IsNullOrWhiteSpace(StoredAnthropicApiKey) ? StoredAnthropicApiKey : ProtectedApiKey?.Get() ?? "";
        set
        {
            if (ProtectedApiKey is { } p) { p.Set((value ?? "").Trim()); StoredAnthropicApiKey = ""; }
            else StoredAnthropicApiKey = value ?? "";
        }
    }
    /// <summary>Plain key from settings.json (older versions, or no DPAPI). Moved into the vault on Windows at start-up.</summary>
    [JsonPropertyName("AnthropicApiKey")] public string StoredAnthropicApiKey { get; set; } = "";
    /// <summary>Protected key store registered by the app (Windows DPAPI). Null = plain settings.json.</summary>
    [JsonIgnore] public static (Func<string?> Get, Action<string> Set)? ProtectedApiKey { get; set; }
    // [assistant] end
    public string AnthropicModel { get; set; } = "claude-opus-5-5";
    public string AnthropicEffort { get; set; } = "medium";
    public bool UseServerFallbacks { get; set; } = true;

    // aconex
    public string PythonPath { get; set; } = "python";
    public string AconexScriptPath { get; set; } = "";
    public string AconexDownloadFolder { get; set; } = "";
    public string AconexInputExcel { get; set; } = "";

    // invoice header block (names are typed by the user, never shipped in code)
    public string InvoiceProjectCode { get; set; } = "";
    public string InvoiceProjectDirector { get; set; } = "";
    public string InvoiceVendorNo { get; set; } = "";
    /// <summary>Signature names in role order, separated by ';'.</summary>
    public string InvoiceSignatureNames { get; set; } = "";
    public int LengthRoundingDecimals { get; set; } = 1;
    /// <summary>Which contract outlet item the tracker's DATA 1ST FIX means: WALL (183, confirmed by Mohamed) or CEILING (182).</summary>
    public string Data1stFixMount { get; set; } = "WALL";
    /// <summary>Same for GRMS 1ST FIX: WALL (200, confirmed) or CEILING (199).</summary>
    public string Grms1stFixMount { get; set; } = "WALL";
    /// <summary>[phase6] Root of the shared documents folder (WIR, MIR, packages, variation docs, Aconex screenshots ...). Empty = Documents\Raffaello.</summary>
    public string DocumentsRoot { get; set; } = "";
    /// <summary>[phase6] The first-run wizard was completed (or skipped).</summary>
    public bool FirstRunCompleted { get; set; }
    /// <summary>Where head-office invoice packages are written (empty = Documents\Raffaello\Packages).</summary>
    public string PackageOutputFolder { get; set; } = "";
    /// <summary>Shared-partition folder searched (recursively) for WIR PDFs by WIR no.</summary>
    public string WirFolder { get; set; } = "";
    /// <summary>Package file name: {CONTRACT} {SUB} {INV} {REV}.</summary>
    public string PackageNamePattern { get; set; } = "{CONTRACT}_{SUB}_INV-{INV}_Rev{REV}";
    /// <summary>Protection password of the head-office tracker workbook.</summary>
    public string TrackerPassword { get; set; } = "RAFFAELLO";

    // [phase4] begin
    /// <summary>aconex.config.json (URLs, selectors, columns, folders); empty = %APPDATA%\Raffaello\aconex.config.json.</summary>
    public string AconexConfigPath { get; set; } = "";
    /// <summary>Re-rank variation BOQ suggestions with Claude (needs the API key; sends variation text + candidate descriptions).</summary>
    public bool VariationsUseClaude { get; set; }
    /// <summary>Where attached variation documents are copied (empty = keep the original path).</summary>
    public string VariationDocsFolder { get; set; } = "";
    // [phase4] end

    // session memory
    public DateTime? LastSeenAt { get; set; }
    public string LastModule { get; set; } = "Dashboard";
    public long? LastLineId { get; set; }
    public List<long> LastOverLineIds { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();

    [JsonIgnore] public string EffectiveUserName => string.IsNullOrWhiteSpace(UserName) ? Environment.UserName : UserName;

    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello");

    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    public static string DefaultDataPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "raffaello.db");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public void AddRecentFile(string file)
    {
        RecentFiles.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, file);
        if (RecentFiles.Count > 10) RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }
}
