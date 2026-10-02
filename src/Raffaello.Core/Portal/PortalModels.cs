using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Portal;

/// <summary>[trust] The subcontractor role of the web portal (separate accounts, never an app role).</summary>
public static class PortalRoles
{
    public const string Subcontractor = "SUBCONTRACTOR";
}

/// <summary>
/// A subcontractor company on the portal. <see cref="Name"/> is the name used in the ledger and on invoices (e.g. ROOTS) -
/// everything a portal user sees is filtered by it on the server.
/// </summary>
public sealed class PortalCompanySetting : Entity
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>BRANDED / HOTEL; the statement template and the remaining quantities are for this building.</summary>
    public string Building { get; set; } = Buildings.Branded;
    public string ContractNo { get; set; } = "";
    /// <summary>Rooms of their scope: comma-separated room codes or wildcards (P2-*, L3-1??). Empty = the rooms they already claimed in.</summary>
    public string RoomScope { get; set; } = "";
    public bool Active { get; set; } = true;
    public int MaxSubmissionsPerDay { get; set; } = 20;
}

public static class PortalSubmissionStatus
{
    public const string Submitted = "SUBMITTED";
    public const string UnderReview = "UNDER_REVIEW";
    public const string Imported = "IMPORTED";
    public const string Rejected = "REJECTED";
    public static readonly string[] All = { Submitted, UnderReview, Imported, Rejected };

    public static bool CanMove(string from, string to) => (from, to) switch
    {
        (Submitted, UnderReview or Imported or Rejected) => true,
        (UnderReview, Imported or Rejected or Submitted) => true,
        _ => false,
    };
}

/// <summary>A filled site statement (+ marked-up drawings and photos) sent by a subcontractor through the portal. The files are server documents linked to it.</summary>
public sealed class PortalSubmission : Entity
{
    public string Company { get; set; } = "";
    public string SubmittedBy { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public string StatementNo { get; set; } = "";
    public string Note { get; set; } = "";
    public string Status { get; set; } = PortalSubmissionStatus.Submitted;
    /// <summary>Why it was rejected / the QS's remark - shown to the subcontractor.</summary>
    public string Reason { get; set; } = "";
    public string ReviewedBy { get; set; } = "";
    public DateTime? ReviewedAt { get; set; }
    public int Files { get; set; }
    public long TotalBytes { get; set; }
    /// <summary>Server document id of the statement workbook (0 = none sent).</summary>
    public long StatementDocumentId { get; set; }
    public string StatementSha256 { get; set; } = "";
    /// <summary>Claim lines found in the statement at upload, and their total quantity.</summary>
    public int StatementLines { get; set; }
    public double StatementQty { get; set; }
    /// <summary>Upload-time findings (duplicate statement, unknown rooms ...).</summary>
    public string Findings { get; set; } = "";
    public int ImportedLines { get; set; }
    public int InvoiceNo { get; set; }
}

public static class PortalMessageDirections
{
    public const string ToSubcontractor = "TO_SUB";
    public const string FromSubcontractor = "FROM_SUB";
}

public sealed class PortalMessage : Entity
{
    public string Company { get; set; } = "";
    public string Direction { get; set; } = PortalMessageDirections.ToSubcontractor;
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public long SubmissionId { get; set; }
}

public static class PortalEntities
{
    public static readonly Type[] All = { typeof(PortalCompanySetting), typeof(PortalSubmission), typeof(PortalMessage) };
    private static bool _registered;
    public static void RegisterAll()
    {
        if (_registered) return;
        foreach (var t in All) EntityMeta.Register(t);
        _registered = true;
    }
}

/// <summary>Document categories of portal uploads.</summary>
public static class PortalFileKinds
{
    public const string Statement = "PORTAL-STATEMENT";
    public const string Drawing = "PORTAL-DRAWING";
    public const string Photo = "PORTAL-PHOTO";
    public const string LinkedTable = "PortalSubmissions";
}

/// <summary>Routes of the portal (the subcontractor web API) and of its administration by the QS / ADMIN.</summary>
public static class PortalRoutes
{
    public const string Page = "/portal";
    public const string Api = "/api/v1/portal";
    public const string Login = Api + "/login";
    public const string Logout = Api + "/logout";
    public const string Me = Api + "/me";
    public const string Template = Api + "/template";
    public const string Submissions = Api + "/submissions";
    public const string Files = Api + "/files";
    public const string Claims = Api + "/claims";
    public const string Invoices = Api + "/invoices";
    public const string Remaining = Api + "/remaining";
    public const string Messages = Api + "/messages";
    public const string AdminAccounts = "/api/v1/portal-admin/accounts";
}

// ------------------------------------------------------------------ DTOs (JSON, PascalCase like the rest of the API)

public sealed class PortalLoginRequest
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class PortalLoginResponse
{
    public string Token { get; set; } = "";
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Language { get; set; } = "en";
    public DateTime ExpiresAt { get; set; }
}

public sealed class PortalMeDto
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = PortalRoles.Subcontractor;
    public string Company { get; set; } = "";
    public string CompanyDisplayName { get; set; } = "";
    public string Building { get; set; } = "";
    public string ContractNo { get; set; } = "";
    public string Language { get; set; } = "en";
    public long MaxFileBytes { get; set; }
    public int MaxFiles { get; set; }
    public List<string> AllowedExtensions { get; set; } = new();
    public int UnreadMessages { get; set; }
}

public sealed class PortalAccountDto
{
    public long Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Company { get; set; } = "";
    public string Language { get; set; } = "en";
    public bool Active { get; set; } = true;
    public string? Password { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class PortalFileDto
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public sealed class PortalClaimDto
{
    public int InvoiceNo { get; set; }
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public double Qty { get; set; }
    public double SitePct { get; set; }
    public double WirPct { get; set; }
    public string WirNo { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public bool IsOver { get; set; }
    public string HeightStatus { get; set; } = "";
    public string LengthStatus { get; set; } = "";
    public DateTime EnteredAt { get; set; }
}

public sealed class PortalInvoiceDto
{
    public int InvoiceNo { get; set; }
    public int Revision { get; set; }
    public string ContractNo { get; set; } = "";
    /// <summary>draft / submitted / approved / rejected.</summary>
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public double CurrentAmount { get; set; }
    public double CumulativeAmount { get; set; }
    public string AconexWorkflowNo { get; set; } = "";
}

public sealed class PortalRemainingDto
{
    public string Room { get; set; } = "";
    public string Level { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public double ProjectQty { get; set; }
    public double ClaimedByYou { get; set; }
    /// <summary>PROJECT QTY minus every claim on the key (all subcontractors, names never shown).</summary>
    public double Remaining { get; set; }
}

public sealed class PortalMessageDto
{
    public long Id { get; set; }
    public string Direction { get; set; } = "";
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public long SubmissionId { get; set; }
}

public sealed class PortalSendMessageRequest
{
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public long SubmissionId { get; set; }
}
