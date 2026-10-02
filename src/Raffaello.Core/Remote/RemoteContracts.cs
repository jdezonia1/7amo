using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raffaello.Core.Remote;

// =====================================================================================================
//  Wire contracts shared by Raffaello.Server and the client (RemoteProjectStore). Plain DTOs + JSON options.
//  Version the routes (/api/v1) - a breaking change gets /api/v2, never a silent change.
// =====================================================================================================

public static class ApiRoutes
{
    public const string Health = "/api/v1/health";
    public const string Info = "/api/v1/info";
    public const string Me = "/api/v1/me";
    public const string Login = "/api/v1/auth/login";
    public const string Logout = "/api/v1/auth/logout";
    public const string Tables = "/api/v1/tables";
    public const string Write = "/api/v1/write";
    public const string ClearAll = "/api/v1/admin/clear-all";
    public const string Audit = "/api/v1/audit";
    public const string Events = "/api/v1/events";
    public const string Presence = "/api/v1/presence";
    public const string Meta = "/api/v1/meta";
    public const string Approvals = "/api/v1/approvals";
    public const string Documents = "/api/v1/documents";
    public const string Users = "/api/v1/users";
    public const string MigrateImport = "/api/v1/migrate/import";
    public const string MigrateAudit = "/api/v1/migrate/audit";
    public const string ChangesHub = "/hubs/changes";
    /// <summary>Full-text search over read document pages (PostgreSQL full text + substring).</summary>
    public const string DocSearch = "/api/v1/docs/search";

    /// <summary>Header carrying the client PC name (audit "Machine").</summary>
    public const string MachineHeader = "X-Raffaello-Machine";
    /// <summary>Header carrying a per-process client id, echoed in change notices so a client can skip its own.</summary>
    public const string ClientHeader = "X-Raffaello-Client";
    public const string ShaHeader = "X-Sha256";
}

public static class RemoteJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
    public static object? Deserialize(string json, Type t) => JsonSerializer.Deserialize(json, t, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public static class WriteOps
{
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
}

/// <summary>How the server writes the audit trail for a request (mirrors the SQLite store).</summary>
public static class WriteModes
{
    /// <summary>One Insert / Update / Delete: one audit row with full JSON / old-new diff.</summary>
    public const string Single = "SINGLE";
    /// <summary>InsertMany: one IMPORT audit row.</summary>
    public const string Many = "MANY";
    /// <summary>Batch: one row per update/delete (old/new) + a BATCH summary row.</summary>
    public const string Batch = "BATCH";
}

public sealed class WriteOpDto
{
    public string Op { get; set; } = WriteOps.Insert;
    public string Table { get; set; } = "";
    /// <summary>The entity as JSON. Negative ids (and negative *Id foreign keys) are temporary ids assigned by the client.</summary>
    public JsonElement Entity { get; set; }
}

public sealed class WriteRequestDto
{
    /// <summary>Idempotency key: a replayed request with the same id returns the stored result instead of writing twice.</summary>
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string Mode { get; set; } = WriteModes.Single;
    public string? Summary { get; set; }
    public List<WriteOpDto> Ops { get; set; } = new();
}

public sealed class WriteResultDto
{
    public string Op { get; set; } = "";
    public string Table { get; set; } = "";
    public long TempId { get; set; }
    public long Id { get; set; }
    public long RowVersion { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public sealed class WriteResponseDto
{
    public Guid RequestId { get; set; }
    public List<WriteResultDto> Results { get; set; } = new();
    /// <summary>Temporary (negative) id -> real id.</summary>
    public Dictionary<long, long> TempIds { get; set; } = new();
    public bool Replayed { get; set; }
}

/// <summary>Body of every non-2xx answer. Code is stable (conflict, remaining_exceeded, forbidden, append_only, ...).</summary>
public sealed class ErrorDto
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Table { get; set; }
    public long? RowId { get; set; }
    public string? ChangedBy { get; set; }
    public DateTime? ChangedAt { get; set; }
    /// <summary>On 409 conflict: the current row as stored on the server.</summary>
    public JsonElement? Current { get; set; }
}

public static class ErrorCodes
{
    public const string Conflict = "conflict";
    public const string RemainingExceeded = "remaining_exceeded";
    public const string Forbidden = "forbidden";
    public const string AppendOnly = "append_only";
    public const string Locked = "locked";
    public const string InvalidTransition = "invalid_transition";
    public const string ApprovalRequired = "approval_required";
    public const string NotFound = "not_found";
    public const string BadRequest = "bad_request";
    public const string TooLarge = "too_large";
}

/// <summary>Pushed over SignalR after every committed write ("changed" message).</summary>
public sealed class ChangeNotice
{
    public string Table { get; set; } = "";
    public long Id { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = "";
    public int Count { get; set; } = 1;
    public string By { get; set; } = "";
    public string Machine { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Summary { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class LoginRequest
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Machine { get; set; } = "";
}

public sealed class LoginResponse
{
    public string Token { get; set; } = "";
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public sealed class MeDto
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string AuthType { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
}

public sealed class ServerInfoDto
{
    public string Server { get; set; } = "Raffaello.Server";
    public string Version { get; set; } = "";
    public string Database { get; set; } = "";
    public List<string> Tables { get; set; } = new();
    public bool WindowsAuth { get; set; }
    public bool RequireInternalApproval { get; set; }
    public long MaxDocumentBytes { get; set; }
    public DateTime ServerTime { get; set; }
}

public sealed class PresenceDto
{
    public string Screen { get; set; } = "";
}

public sealed class EventDto
{
    public string Action { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed class MetaDto
{
    public string? Value { get; set; }
}

/// <summary>Internal approval stages of a subcontractor invoice: DRAFT -> CHECKED -> APPROVED -> SUBMITTED.</summary>
public static class ApprovalStages
{
    public const string Checked = "CHECKED";
    public const string Approved = "APPROVED";
    public const string Submitted = "SUBMITTED";
    /// <summary>Automatic stamp for any other status change (head-office approval, rejection ...).</summary>
    public const string Status = "STATUS";
}

public sealed class ApprovalStampDto
{
    public long Id { get; set; }
    public string TableName { get; set; } = "";
    public long RowId { get; set; }
    public string Stage { get; set; } = "";
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    /// <summary>RowVersion of the invoice when stamped; a later edit of the invoice makes the stamp stale.</summary>
    public long RowVersion { get; set; }
    public string By { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTime At { get; set; }
    public string Note { get; set; } = "";
}

public sealed class StampRequest
{
    public string Table { get; set; } = "SubInvoices";
    public long Id { get; set; }
    public string Stage { get; set; } = ApprovalStages.Checked;
    public long RowVersion { get; set; }
    public string Note { get; set; } = "";
}

public sealed class DocumentInfo
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Category { get; set; } = "";
    public string LinkedTable { get; set; } = "";
    public long LinkedId { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public sealed class UserDto
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string WindowsAccount { get; set; } = "";
    public bool Active { get; set; } = true;
    /// <summary>Only on create / password change; never returned.</summary>
    public string? Password { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class MigrateImportRequest
{
    public string Table { get; set; } = "";
    /// <summary>Rows with their local ids; the server keeps the ids so foreign keys stay valid.</summary>
    public List<JsonElement> Rows { get; set; } = new();
    public string Source { get; set; } = "";
}

public sealed class MigrateImportResponse
{
    public string Table { get; set; } = "";
    public int Inserted { get; set; }
    public int Skipped { get; set; }
}

public sealed class MigrateAuditRequest
{
    public string SourceKey { get; set; } = "";
    public List<Domain.AuditEntry> Rows { get; set; } = new();
}
