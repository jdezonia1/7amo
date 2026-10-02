using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Documents;

/// <summary>
/// Files live on the configured shared partition (UNC path); the database keeps metadata + SHA-256 only.
/// Layout: &lt;root&gt;\yyyy\MM\&lt;sha256&gt;_&lt;safe file name&gt;. Upload streams to a temp file while hashing and enforces the size
/// limit; the same content uploaded again with the same link returns the existing record (no duplicate file).
/// </summary>
public sealed class DocumentStore
{
    private readonly NpgsqlDataSource _ds;
    public string Root { get; }
    public long MaxBytes { get; }

    public DocumentStore(NpgsqlDataSource ds, string root, long maxBytes)
    {
        _ds = ds; Root = root; MaxBytes = maxBytes;
    }

    public async Task<DocumentInfo> SaveAsync(Stream content, string fileName, string contentType, string category, string linkedTable, long linkedId, StoreIdentity who, CancellationToken ct = default)
    {
        var safe = SafeName(fileName);
        Directory.CreateDirectory(Path.Combine(Root, ".incoming"));
        var temp = Path.Combine(Root, ".incoming", Guid.NewGuid().ToString("N") + ".part");
        long size = 0;
        string sha;
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await content.ReadAsync(buf, ct)) > 0)
                {
                    size += n;
                    if (size > MaxBytes) throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"{safe} is larger than the limit of {MaxBytes / (1024 * 1024)} MB.");
                    hash.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (size == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "The file is empty.");

            await using var c = await _ds.OpenConnectionAsync(ct);
            var p = new PgTx(c, null);
            var existing = p.Query<DocRow>("""
                SELECT * FROM "Documents" WHERE "Sha256" = @s AND "FileName" = @f AND "LinkedTable" = @t AND "LinkedId" = @l LIMIT 1
                """, ("s", sha), ("f", safe), ("t", linkedTable ?? ""), ("l", linkedId)).FirstOrDefault();
            if (existing != null) return existing.ToInfo();

            var now = DateTime.Now;
            var rel = Path.Combine(now.ToString("yyyy", CultureInfo.InvariantCulture), now.ToString("MM", CultureInfo.InvariantCulture), $"{sha[..16]}_{safe}");
            var full = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) File.Move(temp, full);
            var id = Convert.ToInt64(p.Scalar("""
                INSERT INTO "Documents" ("FileName", "ContentType", "Size", "Sha256", "RelPath", "Category", "LinkedTable", "LinkedId", "UploadedBy", "UploadedAt")
                VALUES (@f, @ct, @sz, @s, @rel, @cat, @t, @l, @by, @at) RETURNING "Id"
                """, ("f", safe), ("ct", contentType ?? ""), ("sz", size), ("s", sha), ("rel", rel), ("cat", category ?? ""), ("t", linkedTable ?? ""),
                ("l", linkedId), ("by", who.User), ("at", now)), CultureInfo.InvariantCulture);
            p.Exec("""
                INSERT INTO "AuditLog" ("At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes")
                VALUES (@at, @u, @m, 'Documents', @id, 'UPLOAD', @s, '')
                """, ("at", now), ("u", who.User), ("m", who.Machine), ("id", id), ("s", $"Uploaded {safe} ({size:N0} bytes, sha256 {sha[..12]}...)"));
            return Get(id)!;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp clean-up is best effort */ }
        }
    }

    public DocumentInfo? Get(long id)
    {
        using var c = _ds.OpenConnection();
        return new PgTx(c, null).Query<DocRow>("""SELECT * FROM "Documents" WHERE "Id" = @id""", ("id", id)).FirstOrDefault()?.ToInfo();
    }

    public List<DocumentInfo> List(string? linkedTable, long? linkedId, int take = 500)
    {
        using var c = _ds.OpenConnection();
        var p = new PgTx(c, null);
        var rows = string.IsNullOrEmpty(linkedTable)
            ? p.Query<DocRow>("""SELECT * FROM "Documents" ORDER BY "Id" DESC LIMIT @n""", ("n", take))
            : p.Query<DocRow>("""SELECT * FROM "Documents" WHERE "LinkedTable" = @t AND (@l = 0 OR "LinkedId" = @l) ORDER BY "Id" DESC LIMIT @n""",
                ("t", linkedTable), ("l", linkedId ?? 0), ("n", take));
        return rows.Select(r => r.ToInfo()).ToList();
    }

    public string PathOf(long id)
    {
        using var c = _ds.OpenConnection();
        var rel = new PgTx(c, null).Scalar("""SELECT "RelPath" FROM "Documents" WHERE "Id" = @id""", ("id", id)) as string
                  ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"Document #{id} not found.");
        var full = Path.GetFullPath(Path.Combine(Root, rel));
        if (!full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase)) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Bad document path.");
        return full;
    }

    /// <summary>Re-hashes the stored file and compares with the database (detects files changed or damaged on the share).</summary>
    public async Task<bool> VerifyAsync(long id, CancellationToken ct = default)
    {
        var info = Get(id) ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"Document #{id} not found.");
        var path = PathOf(id);
        if (!File.Exists(path)) return false;
        await using var f = File.OpenRead(path);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        return sha == info.Sha256;
    }

    public static string SafeName(string? name)
    {
        var n = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "file.bin" : name.Trim());
        foreach (var ch in Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' })) n = n.Replace(ch, '_');
        if (n.Length > 120) n = n[..80] + "~" + n[^39..];
        return n.Length == 0 ? "file.bin" : n;
    }

    private sealed class DocRow
    {
        public long Id { get; set; }
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public string RelPath { get; set; } = "";
        public string Category { get; set; } = "";
        public string LinkedTable { get; set; } = "";
        public long LinkedId { get; set; }
        public string UploadedBy { get; set; } = "";
        public DateTime UploadedAt { get; set; }

        public DocumentInfo ToInfo() => new()
        {
            Id = Id, FileName = FileName, ContentType = ContentType, Size = Size, Sha256 = Sha256, Category = Category,
            LinkedTable = LinkedTable, LinkedId = LinkedId, UploadedBy = UploadedBy, UploadedAt = UploadedAt,
        };
    }
}
