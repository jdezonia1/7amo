using System.Collections.Concurrent;
using System.Globalization;
using Raffaello.Core.Portal;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Portal;

/// <summary>Portal settings: appsettings.json section "Raffaello:Portal".</summary>
public sealed class PortalOptions
{
    public bool Enabled { get; set; } = true;
    public long MaxFileBytes { get; set; } = 25L * 1024 * 1024;
    public long MaxSubmissionBytes { get; set; } = 100L * 1024 * 1024;
    public int MaxFilesPerSubmission { get; set; } = 30;
    public int MaxSubmissionsPerHour { get; set; } = 10;
    /// <summary>Default for companies; PortalCompanySetting.MaxSubmissionsPerDay overrides.</summary>
    public int MaxSubmissionsPerDay { get; set; } = 20;
    /// <summary>Requests per account (or per address before sign-in) per minute.</summary>
    public int RequestsPerMinute { get; set; } = 120;
    public int MessagesPerHour { get; set; } = 30;
    public int MaxMessageChars { get; set; } = 4000;
    public int TokenHours { get; set; } = 12;
}

/// <summary>The signed-in subcontractor of a portal request (from the portal token only).</summary>
public sealed record PortalPrincipal(long AccountId, string UserName, string DisplayName, string Company, string Language, string Address)
{
    /// <summary>The identity used for audit rows / entity writes made on the subcontractor's behalf.</summary>
    public StoreIdentity Who => new($"portal:{UserName}", PortalRoles.Subcontractor, Address.Length > 64 ? Address[..64] : Address, "", "portal", Trusted: true);
}

/// <summary>
/// Fixed-window counters in memory (per account / address): requests per minute, submissions per hour, messages per hour.
/// A refused call gets 429 with Retry-After. A server restart clears them (the sign-in lockout is the existing LoginThrottle).
/// </summary>
public sealed class PortalRateLimiter
{
    private readonly ConcurrentDictionary<string, Window> _w = new(StringComparer.OrdinalIgnoreCase);
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    private sealed class Window { public DateTime Start; public int Count; }

    /// <summary>Throws 429 when <paramref name="limit"/> hits already happened within <paramref name="period"/> (does not count).</summary>
    public void Check(string key, int limit, TimeSpan period, string what)
    {
        if (!_w.TryGetValue(key, out var w)) return;
        var now = Clock();
        lock (w)
        {
            if (now - w.Start >= period || w.Count < limit) return;
            var wait = (int)Math.Ceiling((w.Start + period - now).TotalSeconds);
            throw new PortalLimitException(Math.Max(1, wait), $"Too many {what} - try again in {Math.Max(1, wait)} seconds.");
        }
    }

    /// <summary>Counts one hit; throws 429 when <paramref name="limit"/> hits happened within <paramref name="period"/>.</summary>
    public void Hit(string key, int limit, TimeSpan period, string what)
    {
        var now = Clock();
        var w = _w.GetOrAdd(key, _ => new Window { Start = now });
        lock (w)
        {
            if (now - w.Start >= period) { w.Start = now; w.Count = 0; }
            if (w.Count >= limit)
            {
                var wait = (int)Math.Ceiling((w.Start + period - now).TotalSeconds);
                throw new PortalLimitException(Math.Max(1, wait), $"Too many {what} - try again in {Math.Max(1, wait)} seconds.");
            }
            w.Count++;
        }
    }
}

public sealed class PortalLimitException : Exception
{
    public int RetryAfterSeconds { get; }
    public PortalLimitException(int retryAfter, string message) : base(message) => RetryAfterSeconds = retryAfter;
}

/// <summary>What a portal upload may be: statement workbook, PDF drawing or photo - checked by extension AND content signature.</summary>
public static class PortalFileTypes
{
    public static readonly string[] Extensions = { ".xlsx", ".pdf", ".jpg", ".jpeg", ".png", ".heic" };

    /// <summary>The kind (statement / drawing / photo) and content type, or null when the file is not allowed.</summary>
    public static (string Kind, string ContentType)? Classify(string fileName, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        switch (ext)
        {
            case ".xlsx" when head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04:
                return (PortalFileKinds.Statement, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            case ".pdf" when head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8):
                return (PortalFileKinds.Drawing, "application/pdf");
            case ".jpg" or ".jpeg" when head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF:
                return (PortalFileKinds.Photo, "image/jpeg");
            case ".png" when head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }):
                return (PortalFileKinds.Photo, "image/png");
            case ".heic" when head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8):
                return (PortalFileKinds.Photo, "image/heic");
            default:
                return null;
        }
    }

    public static string Describe(long bytes) => bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
}

/// <summary>Errors of the portal API (same JSON shape as the rest of the server).</summary>
public static class PortalErrors
{
    public static WriteRejectedException Unauthorized() => new(401, "unauthorized", "Not signed in to the portal, or the session expired. Sign in again.");
    public static WriteRejectedException NotFound(string what) => new(404, ErrorCodes.NotFound, $"{what} not found.");
    public static WriteRejectedException UnsupportedFile(string name) =>
        new(415, "unsupported_file", $"{name}: only the statement workbook (.xlsx), drawings (.pdf) and photos (.jpg, .png, .heic) can be uploaded.");
}
