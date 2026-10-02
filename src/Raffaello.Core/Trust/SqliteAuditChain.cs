using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Raffaello.Core.Data;

namespace Raffaello.Core.Trust;

/// <summary>
/// The hash chain on the desktop data file. <see cref="Seal"/> runs inside every write transaction right after the audit row
/// is inserted (SQLite has one writer at a time, so the chain order is the commit order): each unsealed row gets
/// ChainSeq / PrevHash / Hash and the head (table AuditChainHead) moves on - same transaction, so a write and its seal
/// commit together. Rows written by an older app version are sealed by the next write.
/// </summary>
public static class SqliteAuditChain
{
    private static readonly ConcurrentDictionary<string, bool> Ready = new(StringComparer.OrdinalIgnoreCase);

    public static void EnsureSchema(SqliteConnection c, SqliteTransaction? tx)
    {
        var key = c.DataSource ?? "";
        if (Ready.ContainsKey(key)) return;
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA table_info(AuditLog);";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (cols.Count == 0)
            Exec(c, tx, @"CREATE TABLE IF NOT EXISTS AuditLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, User TEXT, Machine TEXT,
                TableName TEXT, RowId INTEGER, Action TEXT, Summary TEXT, Changes TEXT);");
        if (!cols.Contains("ChainSeq")) Exec(c, tx, "ALTER TABLE AuditLog ADD COLUMN ChainSeq INTEGER;");
        if (!cols.Contains("PrevHash")) Exec(c, tx, "ALTER TABLE AuditLog ADD COLUMN PrevHash TEXT;");
        if (!cols.Contains("Hash")) Exec(c, tx, "ALTER TABLE AuditLog ADD COLUMN Hash TEXT;");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_AuditLog_Unsealed ON AuditLog(Id) WHERE Hash IS NULL;");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_AuditLog_Seq ON AuditLog(ChainSeq);");
        Exec(c, tx, @"CREATE TABLE IF NOT EXISTS AuditChainHead (Id INTEGER PRIMARY KEY CHECK (Id = 1), BaseSeq INTEGER NOT NULL, BaseHash TEXT NOT NULL,
            LastSeq INTEGER NOT NULL, LastHash TEXT NOT NULL, LastId INTEGER NOT NULL, ResetAt TEXT, UpdatedAt TEXT);");
        Ready[key] = true;
    }

    /// <summary>Seals every unsealed audit row (in id order). Call inside the write transaction. Returns rows sealed.</summary>
    public static int Seal(SqliteConnection c, SqliteTransaction tx)
    {
        try { return SealCore(c, tx); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such", StringComparison.OrdinalIgnoreCase))
        {
            // the file was replaced (new file at the same path in this process): re-check the columns once
            Ready.TryRemove(c.DataSource ?? "", out _);
            return SealCore(c, tx);
        }
    }

    private static int SealCore(SqliteConnection c, SqliteTransaction tx)
    {
        EnsureSchema(c, tx);
        var rows = Db.Query<AuditChainRow>(c, tx, "SELECT Id, At, User, Machine, TableName, RowId, Action, Summary, Changes FROM AuditLog WHERE Hash IS NULL ORDER BY Id");
        if (rows.Count == 0) return 0;
        var head = ReadHead(c, tx) ?? new AuditChainHead();
        foreach (var r in rows)
        {
            var seq = head.LastSeq + 1;
            var hash = AuditHash.Compute(head.LastHash, r, seq);
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE AuditLog SET ChainSeq=@S, PrevHash=@P, Hash=@H WHERE Id=@Id";
                cmd.Parameters.AddWithValue("@S", seq);
                cmd.Parameters.AddWithValue("@P", head.LastHash);
                cmd.Parameters.AddWithValue("@H", hash);
                cmd.Parameters.AddWithValue("@Id", r.Id);
                cmd.ExecuteNonQuery();
            }
            head.LastSeq = seq;
            head.LastHash = hash;
            head.LastId = r.Id;
        }
        head.UpdatedAt = DateTime.Now;
        WriteHead(c, tx, head);
        return rows.Count;
    }

    /// <summary>
    /// A sanctioned data reset (Settings > reset / start empty) deletes the audit log. The chain then continues from the old
    /// head (BaseSeq / BaseHash), so the verifier knows where the new history starts and when the reset happened.
    /// </summary>
    public static void OnReset(SqliteConnection c, SqliteTransaction tx)
    {
        EnsureSchema(c, tx);
        var head = ReadHead(c, tx) ?? new AuditChainHead();
        head.BaseSeq = head.LastSeq;
        head.BaseHash = head.LastHash;
        head.LastId = 0;
        head.ResetAt = DateTime.Now;
        head.UpdatedAt = DateTime.Now;
        WriteHead(c, tx, head);
    }

    public static AuditChainHead? ReadHead(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT BaseSeq, BaseHash, LastSeq, LastHash, LastId, ResetAt, UpdatedAt FROM AuditChainHead WHERE Id = 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AuditChainHead
        {
            BaseSeq = r.GetInt64(0), BaseHash = r.GetString(1), LastSeq = r.GetInt64(2), LastHash = r.GetString(3), LastId = r.GetInt64(4),
            ResetAt = r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            UpdatedAt = r.IsDBNull(6) ? default : DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }

    private static void WriteHead(SqliteConnection c, SqliteTransaction tx, AuditChainHead h)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO AuditChainHead (Id, BaseSeq, BaseHash, LastSeq, LastHash, LastId, ResetAt, UpdatedAt) VALUES (1, @BS, @BH, @LS, @LH, @LI, @RA, @UA)
            ON CONFLICT(Id) DO UPDATE SET BaseSeq=@BS, BaseHash=@BH, LastSeq=@LS, LastHash=@LH, LastId=@LI, ResetAt=@RA, UpdatedAt=@UA";
        cmd.Parameters.AddWithValue("@BS", h.BaseSeq);
        cmd.Parameters.AddWithValue("@BH", h.BaseHash);
        cmd.Parameters.AddWithValue("@LS", h.LastSeq);
        cmd.Parameters.AddWithValue("@LH", h.LastHash);
        cmd.Parameters.AddWithValue("@LI", h.LastId);
        cmd.Parameters.AddWithValue("@RA", Db.ToDb(h.ResetAt));
        cmd.Parameters.AddWithValue("@UA", Db.ToDb(h.UpdatedAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Verifies the chain of a data file (read-only apart from creating the chain columns on a file that never had them).</summary>
    public static AuditVerifyReport Verify(string dbPath, IEnumerable<AuditAnchor>? anchors = null, DateTime? now = null)
    {
        using var c = Open(dbPath);
        using (var tx = c.BeginTransaction())
        {
            EnsureSchema(c, tx);
            tx.Commit();
        }
        var head = ReadHead(c, null);
        long unsealed;
        DateTime? oldest = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), MIN(At) FROM AuditLog WHERE Hash IS NULL";
            using var r = cmd.ExecuteReader();
            r.Read();
            unsealed = r.GetInt64(0);
            if (!r.IsDBNull(1)) oldest = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        return AuditChainVerifier.Verify(Stream(c), head, unsealed, oldest, anchors, dbPath, now);
    }

    /// <summary>Sealed rows in chain order, read lazily.</summary>
    private static IEnumerable<AuditChainRow> Stream(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, ChainSeq, At, User, Machine, TableName, RowId, Action, Summary, Changes, PrevHash, Hash FROM AuditLog WHERE Hash IS NOT NULL ORDER BY ChainSeq, Id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return new AuditChainRow
            {
                Id = r.GetInt64(0), ChainSeq = r.IsDBNull(1) ? null : r.GetInt64(1),
                At = (DateTime)Db.FromDb(r.GetValue(2), typeof(DateTime))!,
                User = r.IsDBNull(3) ? null : r.GetString(3), Machine = r.IsDBNull(4) ? null : r.GetString(4), TableName = r.IsDBNull(5) ? null : r.GetString(5),
                RowId = r.IsDBNull(6) ? 0 : r.GetInt64(6), Action = r.IsDBNull(7) ? null : r.GetString(7), Summary = r.IsDBNull(8) ? null : r.GetString(8),
                Changes = r.IsDBNull(9) ? null : r.GetString(9), PrevHash = r.IsDBNull(10) ? null : r.GetString(10), Hash = r.IsDBNull(11) ? null : r.GetString(11),
            };
    }

    /// <summary>Seals what is pending in a data file (e.g. rows written by an older app version) in its own transaction.</summary>
    public static int SealPending(string dbPath)
    {
        using var c = Open(dbPath);
        using var tx = c.BeginTransaction();
        var n = Seal(c, tx);
        tx.Commit();
        return n;
    }

    private static SqliteConnection Open(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, DefaultTimeout = 8 }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, null, "PRAGMA busy_timeout=8000;");
        return c;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
