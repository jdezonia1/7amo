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
    public double PipeLengthM { get; set; } = 6.0;

    // assistant
    public string AnthropicApiKey { get; set; } = "";
    public string AnthropicModel { get; set; } = "claude-opus-5-5";
    public string AnthropicEffort { get; set; } = "medium";
    public bool UseServerFallbacks { get; set; } = true;

    // aconex
    public string PythonPath { get; set; } = "python";
    public string AconexScriptPath { get; set; } = "";
    public string AconexDownloadFolder { get; set; } = "";
    public string AconexInputExcel { get; set; } = "";

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
