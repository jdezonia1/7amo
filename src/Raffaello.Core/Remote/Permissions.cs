namespace Raffaello.Core.Remote;

public static class Roles
{
    public const string Site = "SITE";
    public const string Qs = "QS";
    public const string Reviewer = "REVIEWER";
    public const string Admin = "ADMIN";

    public static readonly string[] All = { Site, Qs, Reviewer, Admin };

    public static string Normalize(string? role)
    {
        var r = (role ?? "").Trim().ToUpperInvariant();
        return All.Contains(r) ? r : "";
    }
}

public static class Permissions
{
    public const string Read = "READ";
    public const string UploadStatements = "UPLOAD_STATEMENTS";
    public const string UploadDocuments = "UPLOAD_DOCUMENTS";
    /// <summary>Ledger, quantities, contracts, mapping, BOQ, materials ... (everything not listed separately).</summary>
    public const string EditData = "EDIT_DATA";
    public const string PrepareInvoices = "PREPARE_INVOICES";
    public const string CheckInvoices = "CHECK_INVOICES";
    public const string ApproveInvoices = "APPROVE_INVOICES";
    public const string ManageUsers = "MANAGE_USERS";
    public const string ManageTemplates = "MANAGE_TEMPLATES";
    public const string ManageSettings = "MANAGE_SETTINGS";
    public const string ClearAll = "CLEAR_ALL";
    public const string Migrate = "MIGRATE";
}

/// <summary>
/// Who may do what. SITE: upload statements, view. QS: enter data and prepare invoices. REVIEWER: check / approve.
/// ADMIN: everything incl. users, templates and settings. The server enforces this; the client only uses it to grey out buttons.
/// </summary>
public static class PermissionMatrix
{
    private static readonly Dictionary<string, HashSet<string>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [Roles.Site] = new() { Permissions.Read, Permissions.UploadStatements, Permissions.UploadDocuments },
        [Roles.Qs] = new()
        {
            Permissions.Read, Permissions.UploadStatements, Permissions.UploadDocuments, Permissions.EditData,
            Permissions.PrepareInvoices, Permissions.CheckInvoices,
        },
        [Roles.Reviewer] = new() { Permissions.Read, Permissions.UploadDocuments, Permissions.CheckInvoices, Permissions.ApproveInvoices },
        [Roles.Admin] = new()
        {
            Permissions.Read, Permissions.UploadStatements, Permissions.UploadDocuments, Permissions.EditData,
            Permissions.PrepareInvoices, Permissions.CheckInvoices, Permissions.ApproveInvoices,
            Permissions.ManageUsers, Permissions.ManageTemplates, Permissions.ManageSettings, Permissions.ClearAll, Permissions.Migrate,
        },
    };

    public static bool Can(string? role, string permission) => role != null && Map.TryGetValue(role, out var set) && set.Contains(permission);

    public static IReadOnlyCollection<string> Of(string? role) => role != null && Map.TryGetValue(role, out var set) ? set : Array.Empty<string>();
}
