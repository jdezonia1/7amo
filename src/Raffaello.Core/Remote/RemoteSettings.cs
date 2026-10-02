using System.Text.Json;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Remote;

public static class DataSources
{
    public const string Local = "Local";
    public const string Server = "Server";
}

/// <summary>
/// Where the data lives for this user: the local / shared SQLite file (AppSettings.DataFilePath) or the Raffaello server.
/// Stored next to settings.json in %APPDATA%\Raffaello\server.json (separate file so it never mixes with other settings).
/// </summary>
public sealed class RemoteSettings
{
    public string Mode { get; set; } = DataSources.Local;
    /// <summary>e.g. http://RAFFAELLO-SRV:5180</summary>
    public string ServerUrl { get; set; } = "";
    /// <summary>Sign in with the Windows account (domain PCs). When false, an app account token is used.</summary>
    public bool UseWindowsAuth { get; set; } = true;
    /// <summary>App-account session token from the last sign-in (the password itself is never stored).</summary>
    public string Token { get; set; } = "";
    public string TokenUser { get; set; } = "";
    public DateTime? TokenExpires { get; set; }
    /// <summary>Offline cache + queued writes. Empty = %LOCALAPPDATA%\Raffaello\server-cache\&lt;host&gt;.</summary>
    public string CacheFolder { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;

    public bool IsServer => string.Equals(Mode, DataSources.Server, StringComparison.OrdinalIgnoreCase) && ServerUrl.Trim().Length > 0;

    public static string DefaultPath => Path.Combine(AppSettings.SettingsFolder, "server.json");

    public string EffectiveCacheFolder()
    {
        if (!string.IsNullOrWhiteSpace(CacheFolder)) return CacheFolder;
        var host = Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var u) ? $"{u.Host}_{u.Port}" : "server";
        foreach (var ch in Path.GetInvalidFileNameChars()) host = host.Replace(ch, '_');
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "server-cache", host);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RemoteSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<RemoteSettings>(File.ReadAllText(path), Json) ?? new RemoteSettings();
        }
        catch { /* corrupt file: defaults (local) */ }
        return new RemoteSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
