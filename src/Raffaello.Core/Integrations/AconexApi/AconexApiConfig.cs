using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Raffaello.Core.Trust;

namespace Raffaello.Core.Integrations.AconexApi;

/// <summary>
/// Settings of the Oracle Aconex REST API route (%APPDATA%\Raffaello\aconex-api.json). Off until MOBCO obtains API access
/// (an integration registered with Oracle: application key + OAuth client, or a service user). When <see cref="Enabled"/> is on,
/// workflow lookups and register downloads use the API instead of the browser automation; everything else stays the same.
/// Paths are templates ({projectId}, {documentId}) so they can be corrected without a new build if Oracle's tenant differs.
/// </summary>
public sealed class AconexApiConfig
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    /// <summary>Instance root, e.g. https://ksa1.aconex.com (the API lives under /api).</summary>
    public string BaseUrl { get; set; } = "https://ksa1.aconex.com";
    public string ProjectId { get; set; } = "";

    /// <summary>OAUTH_CLIENT_CREDENTIALS (service integration), BEARER (a token pasted / issued elsewhere) or BASIC (legacy user + password).</summary>
    public string AuthMode { get; set; } = AconexAuthModes.OAuthClientCredentials;
    /// <summary>OAuth token endpoint of the Oracle identity domain / Aconex Lobby.</summary>
    public string TokenUrl { get; set; } = "https://constructionandengineering.oraclecloud.com/auth/token";
    public string ClientId { get; set; } = "";
    /// <summary>Client secret / password, protected with DPAPI (Windows) - set it with SetSecret, never in plain text here.</summary>
    public string SecretProtected { get; set; } = "";
    public string Scope { get; set; } = "";
    /// <summary>For BASIC.</summary>
    public string UserName { get; set; } = "";
    /// <summary>Application key issued by Oracle for the integration (sent as X-Application-Key).</summary>
    public string ApplicationKey { get; set; } = "";
    public string ApplicationKeyHeader { get; set; } = "X-Application-Key";

    public AconexApiPaths Paths { get; set; } = new();
    public AconexApiFields Fields { get; set; } = new();
    public int PageSize { get; set; } = 250;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>Pause between calls (Aconex rate-limits integrations).</summary>
    public int DelayBetweenCallsMs { get; set; } = 250;
    public int MaxRetries { get; set; } = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello", "aconex-api.json");

    public static AconexApiConfig Load(string? path = null, bool writeIfMissing = true)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            var d = new AconexApiConfig();
            if (writeIfMissing) d.Save(path);
            return d;
        }
        return JsonSerializer.Deserialize<AconexApiConfig>(File.ReadAllText(path), Json) ?? new AconexApiConfig();
    }

    /// <summary>Never throws: a broken file means "API off".</summary>
    public static AconexApiConfig LoadSafe(string? path = null)
    {
        try { return Load(path, writeIfMissing: false); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new AconexApiConfig(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public List<string> Validate()
    {
        var p = new List<string>();
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)) p.Add("BaseUrl is not a URL.");
        if (ProjectId.Length == 0) p.Add("ProjectId is empty (GET /api/projects lists the ids you can see).");
        switch (AuthMode)
        {
            case AconexAuthModes.OAuthClientCredentials:
                if (ClientId.Length == 0) p.Add("ClientId is empty.");
                if (!Uri.TryCreate(TokenUrl, UriKind.Absolute, out _)) p.Add("TokenUrl is not a URL.");
                break;
            case AconexAuthModes.Basic:
                if (UserName.Length == 0) p.Add("UserName is empty.");
                break;
            case AconexAuthModes.Bearer: break;
            default: p.Add($"AuthMode must be {AconexAuthModes.OAuthClientCredentials}, {AconexAuthModes.Bearer} or {AconexAuthModes.Basic}."); break;
        }
        return p;
    }

    /// <summary>Stores the secret (client secret, token or password) protected for the current Windows user; elsewhere it must come from RAFFAELLO_ACONEX_SECRET.</summary>
    public void SetSecret(string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Set RAFFAELLO_ACONEX_SECRET instead (DPAPI needs Windows).");
        SecretProtected = Convert.ToBase64String(new DpapiKeyProtector().Protect(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>The secret: environment variable RAFFAELLO_ACONEX_SECRET first, then the DPAPI-protected value.</summary>
    public string Secret()
    {
        var env = Environment.GetEnvironmentVariable("RAFFAELLO_ACONEX_SECRET");
        if (!string.IsNullOrEmpty(env)) return env;
        if (SecretProtected.Length == 0) return "";
        if (!OperatingSystem.IsWindows()) return "";
        return Encoding.UTF8.GetString(new DpapiKeyProtector().Unprotect(Convert.FromBase64String(SecretProtected)));
    }
}

public static class AconexAuthModes
{
    public const string OAuthClientCredentials = "OAUTH_CLIENT_CREDENTIALS";
    public const string Bearer = "BEARER";
    public const string Basic = "BASIC";
}

/// <summary>Endpoint templates (Oracle Aconex REST API, XML responses).</summary>
public sealed class AconexApiPaths
{
    public string Projects { get; set; } = "/api/projects";
    public string RegisterSearch { get; set; } = "/api/projects/{projectId}/register";
    public string DocumentFile { get; set; } = "/api/projects/{projectId}/register/{documentId}/file";
    public string WorkflowSearch { get; set; } = "/api/projects/{projectId}/workflows/search";
}

/// <summary>Search fields / return fields used in queries (register search_query syntax).</summary>
public sealed class AconexApiFields
{
    public string DocNo { get; set; } = "docno";
    public string DocType { get; set; } = "doctype";
    public string Discipline { get; set; } = "discipline";
    public string Group { get; set; } = "category";
    /// <summary>Date field for the date range, written as field:[yyyyMMdd TO yyyyMMdd].</summary>
    public string Date { get; set; } = "registered";
    public string DateFormat { get; set; } = "yyyyMMdd";
    public string ReturnFields { get; set; } = "docno,title,revision,versionnumber,doctype,discipline,category,statusid,registered,filename";
    public string WorkflowNo { get; set; } = "workflow_number";
}
