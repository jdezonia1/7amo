using System.Globalization;
using Npgsql;
using Raffaello.Core.Trust;
using Raffaello.Server.Data;

namespace Raffaello.Server.Trust;

/// <summary>
/// The server computes the audit hash chain authoritatively. Writers only INSERT audit rows (unchanged code paths, no global
/// lock inside the write transaction - so no deadlock with the ledger-key locks); a sealer takes committed unsealed rows in id
/// order under one advisory lock and gives each its ChainSeq / PrevHash / Hash, moving the head in the same transaction. It runs
/// every few seconds and before every verification. The chain order is the sealing order (ChainSeq), not the id order.
/// The head is also appended to an anchor file in the backup folder (outside the database), so rewriting the whole chain in the
/// database is still detected.
/// </summary>
public sealed class ServerAuditChain
{
    private readonly NpgsqlDataSource _ds;
    private readonly object _gate = new();
    public string AnchorFile { get; }
    public TimeSpan AnchorEvery { get; set; } = TimeSpan.FromHours(1);
    public int BatchSize { get; set; } = 2000;
    private DateTime _lastAnchor = DateTime.MinValue;

    public ServerAuditChain(NpgsqlDataSource ds, string anchorFile) { _ds = ds; AnchorFile = anchorFile; }

    public const string Schema = """
        ALTER TABLE "AuditLog" ADD COLUMN IF NOT EXISTS "ChainSeq" bigint;
        ALTER TABLE "AuditLog" ADD COLUMN IF NOT EXISTS "PrevHash" text;
        ALTER TABLE "AuditLog" ADD COLUMN IF NOT EXISTS "Hash" text;
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Unsealed" ON "AuditLog" ("Id") WHERE "Hash" IS NULL;
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_ChainSeq" ON "AuditLog" ("ChainSeq");
        CREATE TABLE IF NOT EXISTS "AuditChainHead" (
            "Id" integer PRIMARY KEY CHECK ("Id" = 1), "BaseSeq" bigint NOT NULL, "BaseHash" text NOT NULL,
            "LastSeq" bigint NOT NULL, "LastHash" text NOT NULL, "LastId" bigint NOT NULL, "ResetAt" timestamp, "UpdatedAt" timestamp);
        """;

    public void EnsureSchema()
    {
        using var c = _ds.OpenConnection();
        using (var l = new NpgsqlCommand("SELECT pg_advisory_lock(7101976)", c)) l.ExecuteNonQuery();
        try
        {
            using var tx = c.BeginTransaction();
            new PgTx(c, tx).Exec(Schema);
            tx.Commit();
        }
        finally
        {
            using var u = new NpgsqlCommand("SELECT pg_advisory_unlock(7101976)", c);
            u.ExecuteNonQuery();
        }
    }

    /// <summary>Seals every committed unsealed row. Returns the number sealed. Safe to call from several places at once.</summary>
    public int Seal()
    {
        lock (_gate)
        {
            var total = 0;
            while (true)
            {
                var n = SealBatch();
                total += n;
                if (n < BatchSize) break;
            }
            return total;
        }
    }

    private int SealBatch()
    {
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        p.Lock("audit-chain");
        var rows = p.Query<AuditChainRow>("""
            SELECT "Id", "At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes", "OldJson", "NewJson"
            FROM "AuditLog" WHERE "Hash" IS NULL ORDER BY "Id" LIMIT @n
            """, ("n", BatchSize));
        if (rows.Count == 0) { tx.Rollback(); return 0; }
        var head = ReadHead(p) ?? new AuditChainHead();
        var ids = new long[rows.Count];
        var seqs = new long[rows.Count];
        var prevs = new string[rows.Count];
        var hashes = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var seq = head.LastSeq + 1;
            var hash = AuditHash.Compute(head.LastHash, r, seq);
            ids[i] = r.Id; seqs[i] = seq; prevs[i] = head.LastHash; hashes[i] = hash;
            head.LastSeq = seq; head.LastHash = hash; head.LastId = r.Id;
        }
        using (var cmd = new NpgsqlCommand("""
            UPDATE "AuditLog" a SET "ChainSeq" = v.seq, "PrevHash" = v.prev, "Hash" = v.hash
            FROM unnest(@ids, @seqs, @prevs, @hashes) AS v(id, seq, prev, hash) WHERE a."Id" = v.id
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            cmd.Parameters.AddWithValue("seqs", seqs);
            cmd.Parameters.AddWithValue("prevs", prevs);
            cmd.Parameters.AddWithValue("hashes", hashes);
            cmd.ExecuteNonQuery();
        }
        head.UpdatedAt = DateTime.Now;
        WriteHead(p, head);
        tx.Commit();
        return rows.Count;
    }

    public AuditChainHead? Head()
    {
        using var c = _ds.OpenConnection();
        return ReadHead(new PgTx(c, null));
    }

    private static AuditChainHead? ReadHead(PgTx p) =>
        p.Query<AuditChainHead>("""SELECT * FROM "AuditChainHead" WHERE "Id" = 1""").FirstOrDefault();

    private static void WriteHead(PgTx p, AuditChainHead h) => p.Exec("""
        INSERT INTO "AuditChainHead" ("Id", "BaseSeq", "BaseHash", "LastSeq", "LastHash", "LastId", "ResetAt", "UpdatedAt")
        VALUES (1, @bs, @bh, @ls, @lh, @li, @ra, @ua)
        ON CONFLICT ("Id") DO UPDATE SET "BaseSeq" = @bs, "BaseHash" = @bh, "LastSeq" = @ls, "LastHash" = @lh, "LastId" = @li, "ResetAt" = @ra, "UpdatedAt" = @ua
        """, ("bs", h.BaseSeq), ("bh", h.BaseHash), ("ls", h.LastSeq), ("lh", h.LastHash), ("li", h.LastId), ("ra", h.ResetAt), ("ua", h.UpdatedAt));

    /// <summary>Seals what is pending, then walks the whole chain (streaming) with the anchors of the file and the caller.</summary>
    public AuditVerifyReport Verify(IEnumerable<AuditAnchor>? extraAnchors = null)
    {
        Seal();
        using var c = _ds.OpenConnection();
        var p = new PgTx(c, null);
        var head = ReadHead(p);
        long unsealed;
        DateTime? oldest;
        using (var cmd = p.Command("""SELECT COUNT(*), MIN("At") FROM "AuditLog" WHERE "Hash" IS NULL"""))
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            unsealed = r.GetInt64(0);
            oldest = r.IsDBNull(1) ? null : r.GetDateTime(1);
        }
        var anchors = ReadAnchors().Concat(extraAnchors ?? Array.Empty<AuditAnchor>()).ToList();
        var db = new NpgsqlConnectionStringBuilder(_ds.ConnectionString).Database;
        return AuditChainVerifier.Verify(Stream(), head, unsealed, oldest, anchors, $"server database {db}");
    }

    /// <summary>Sealed rows in chain order, read lazily on a separate connection.</summary>
    public IEnumerable<AuditChainRow> Stream(long afterSeq = 0, int? take = null)
    {
        using var c = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"""
            SELECT "Id", "ChainSeq", "At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes", "OldJson", "NewJson", "PrevHash", "Hash"
            FROM "AuditLog" WHERE "Hash" IS NOT NULL AND "ChainSeq" > @a ORDER BY "ChainSeq", "Id" {(take is { } t ? "LIMIT " + t.ToString(CultureInfo.InvariantCulture) : "")}
            """, c);
        cmd.Parameters.AddWithValue("a", afterSeq);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return new AuditChainRow
            {
                Id = r.GetInt64(0), ChainSeq = r.IsDBNull(1) ? null : r.GetInt64(1), At = r.GetDateTime(2),
                User = S(r, 3), Machine = S(r, 4), TableName = S(r, 5), RowId = r.IsDBNull(6) ? 0 : r.GetInt64(6), Action = S(r, 7), Summary = S(r, 8),
                Changes = S(r, 9), OldJson = S(r, 10), NewJson = S(r, 11), PrevHash = S(r, 12), Hash = S(r, 13),
            };
    }

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    // ------------------------------------------------------------------ anchors (outside the database)

    public List<AuditAnchor> ReadAnchors()
    {
        var list = new List<AuditAnchor>();
        try
        {
            if (!File.Exists(AnchorFile)) return list;
            foreach (var line in File.ReadAllLines(AnchorFile))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq)) continue;
                DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                list.Add(new AuditAnchor { Seq = seq, Hash = parts[1], At = at, Source = "server anchor file" });
            }
        }
        catch (IOException) { /* anchors are an extra check; a locked file must not stop verification */ }
        return list;
    }

    /// <summary>Appends the current head to the anchor file (when it moved). Returns the anchor written, or null.</summary>
    public AuditAnchor? WriteAnchor(bool force = false)
    {
        var head = Head();
        if (head is null || head.LastSeq <= 0) return null;
        if (!force && DateTime.Now - _lastAnchor < AnchorEvery) return null;
        var existing = ReadAnchors();
        if (existing.Count > 0 && existing[^1].Seq == head.LastSeq) { _lastAnchor = DateTime.Now; return existing[^1]; }
        var a = new AuditAnchor { Seq = head.LastSeq, Hash = head.LastHash, At = DateTime.Now, Source = "server anchor file" };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(AnchorFile))!);
        File.AppendAllText(AnchorFile, $"{a.Seq} {a.Hash} {a.At:yyyy-MM-ddTHH:mm:ss}{Environment.NewLine}");
        _lastAnchor = DateTime.Now;
        return a;
    }
}
