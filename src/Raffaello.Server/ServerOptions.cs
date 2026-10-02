namespace Raffaello.Server;

/// <summary>Settings from appsettings.json section "Raffaello" (or environment variables Raffaello__Name).</summary>
public sealed class ServerOptions
{
    /// <summary>Where the server listens. 0.0.0.0 = every network card of the server PC.</summary>
    public string Urls { get; set; } = "http://0.0.0.0:5180";

    /// <summary>Accept Windows sign-in (domain PCs, no password prompt). Off by default on Linux.</summary>
    public bool WindowsAuth { get; set; } = OperatingSystem.IsWindows();
    public bool AutoCreateWindowsUsers { get; set; } = true;
    public string DefaultWindowsRole { get; set; } = "SITE";

    /// <summary>Invoices must be CHECKED and APPROVED (stamps) before they can be SUBMITTED.</summary>
    public bool RequireInternalApproval { get; set; } = true;

    /// <summary>Shared partition for documents, e.g. \\FILESERVER\RAFFLES\RaffaelloDocs. Default: Documents folder next to the server.</summary>
    public string DocumentsRoot { get; set; } = "";
    public long MaxDocumentBytes { get; set; } = 200L * 1024 * 1024;

    public string BackupFolder { get; set; } = "";
    /// <summary>Local time of the nightly backup (HH:mm).</summary>
    public string BackupAt { get; set; } = "02:00";
    public int BackupRetentionDays { get; set; } = 30;
    /// <summary>Always keep at least this many backups, whatever their age.</summary>
    public int BackupKeepMin { get; set; } = 7;
    public bool NightlyBackup { get; set; } = true;
    /// <summary>Folder of pg_dump / pg_restore. Empty = search PATH and the usual install folders.</summary>
    public string PgBinPath { get; set; } = "";

    /// <summary>BCrypt cost for app-account passwords (11 = ~0.2 s per sign-in).</summary>
    public int BcryptWorkFactor { get; set; } = 11;

    /// <summary>[phase6] Sign-in rate limit: wrong passwords allowed per user / address within the window, then a lockout.</summary>
    public int LoginMaxFailures { get; set; } = 5;
    public int LoginWindowMinutes { get; set; } = 15;
    public int LoginLockoutMinutes { get; set; } = 5;

    public string ResolveDocumentsRoot(string contentRoot) =>
        string.IsNullOrWhiteSpace(DocumentsRoot) ? Path.Combine(contentRoot, "Documents") : DocumentsRoot;

    public string ResolveBackupFolder(string contentRoot) =>
        string.IsNullOrWhiteSpace(BackupFolder) ? Path.Combine(contentRoot, "Backups") : BackupFolder;
}
