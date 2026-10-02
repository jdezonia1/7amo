using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Data;

/// <summary>Thrown when a row was changed by someone else since it was read (RowVersion mismatch).</summary>
public sealed class ConcurrencyException : Exception
{
    public string Table { get; }
    public long RowId { get; }
    public string? ChangedBy { get; }
    public ConcurrencyException(string table, long id, string? by)
        : base($"{table} #{id} was changed by {by ?? "someone else"} since you opened it. Reload and try again.")
    { Table = table; RowId = id; ChangedBy = by; }
}

/// <summary>
/// Tiny SQLite store for a shared-drive data file: WAL mode, busy timeout, reflection-mapped tables,
/// optimistic concurrency on RowVersion and an AuditLog row for every change (same transaction).
/// </summary>
public sealed class Db
{
    public static readonly Type[] EntityTypes =
    {
        typeof(Room), typeof(QtyLine), typeof(Subcontractor), typeof(Allocation), typeof(Wir), typeof(WirLine),
        typeof(Invoice), typeof(InvoiceLine), typeof(PurchaseOrder), typeof(PoLine), typeof(DeliveryNote), typeof(DnLine),
        typeof(BoqItem), typeof(Contract), typeof(AconexDoc), typeof(ImportBatch),
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache = new();

    public string Path { get; }
    public string User { get; set; }
    public string Machine { get; set; }
    public int BusyTimeoutMs { get; set; } = 8000;
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public Db(string path, string user, string? machine = null)
    {
        Path = path;
        User = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user;
        Machine = machine ?? Environment.MachineName;
    }

    public static string TableOf(Type t) => t.Name + "s";
    public static string TableOf<T>() => TableOf(typeof(T));

    public SqliteConnection Open()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000) }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, null, $"PRAGMA busy_timeout={BusyTimeoutMs};");
        Exec(c, null, "PRAGMA journal_mode=WAL;");
        Exec(c, null, "PRAGMA foreign_keys=OFF;");
        return c;
    }

    // ------------------------------------------------------------------ schema

    public void EnsureSchema()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in EntityTypes) EnsureTable(c, tx, t);
        Exec(c, tx, @"CREATE TABLE IF NOT EXISTS AuditLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, User TEXT, Machine TEXT,
            TableName TEXT, RowId INTEGER, Action TEXT, Summary TEXT, Changes TEXT);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_AuditLog_At ON AuditLog(At);");
        Exec(c, tx, "CREATE TABLE IF NOT EXISTS Presence (Machine TEXT NOT NULL, User TEXT NOT NULL, Screen TEXT, LastSeen TEXT, PRIMARY KEY(Machine, User));");
        Exec(c, tx, "CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_WirLines_Line ON WirLines(LineId);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_InvoiceLines_Line ON InvoiceLines(LineId);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_Allocations_Line ON Allocations(LineId);");
        tx.Commit();
    }

    private static void EnsureTable(SqliteConnection c, SqliteTransaction tx, Type t)
    {
        var table = TableOf(t);
        var props = Props(t);
        var cols = props.Where(p => p.Name != nameof(Entity.Id)).Select(p => $"[{p.Name}] {SqlType(p.PropertyType)}");
        Exec(c, tx, $"CREATE TABLE IF NOT EXISTS [{table}] (Id INTEGER PRIMARY KEY AUTOINCREMENT, {string.Join(", ", cols)});");
        // additive migration: add columns introduced after the file was created
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info([{table}]);";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        foreach (var p in props.Where(p => !existing.Contains(p.Name)))
            Exec(c, tx, $"ALTER TABLE [{table}] ADD COLUMN [{p.Name}] {SqlType(p.PropertyType)};");
    }

    private static string SqlType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(long) || t == typeof(int) || t == typeof(bool)) return "INTEGER";
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "REAL";
        return "TEXT";
    }

    internal static PropertyInfo[] Props(Type t) => PropCache.GetOrAdd(t, x =>
        x.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite).ToArray());

    // ------------------------------------------------------------------ reads

    public List<T> All<T>(string? where = null, object? args = null) where T : new()
    {
        using var c = Open();
        return Query<T>(c, null, $"SELECT * FROM [{TableOf<T>()}]" + (where is null ? "" : " WHERE " + where), args);
    }

    public T? Get<T>(long id) where T : Entity, new()
    {
        using var c = Open();
        return Query<T>(c, null, $"SELECT * FROM [{TableOf<T>()}] WHERE Id=@Id", new { Id = id }).FirstOrDefault();
    }

    public int Count<T>()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{TableOf<T>()}]";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static List<T> Query<T>(SqliteConnection c, SqliteTransaction? tx, string sql, object? args = null) where T : new()
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        using var r = cmd.ExecuteReader();
        var props = Props(typeof(T)).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var map = new PropertyInfo?[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++) map[i] = props.GetValueOrDefault(r.GetName(i));
        var list = new List<T>();
        while (r.Read())
        {
            var o = new T();
            for (var i = 0; i < map.Length; i++)
            {
                var p = map[i];
                if (p is null) continue;
                p.SetValue(o, FromDb(r.GetValue(i), p.PropertyType));
            }
            list.Add(o);
        }
        return list;
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Inserts a row, stamps audit fields and RowVersion=1, logs to AuditLog.</summary>
    public T Insert<T>(T e, string? summary = null) where T : Entity
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        InsertCore(c, tx, e);
        Audit(c, tx, TableOf<T>(), e.Id, "INSERT", summary ?? $"Added {typeof(T).Name} #{e.Id}", JsonSerializer.Serialize(e, e.GetType()));
        tx.Commit();
        return e;
    }

    /// <summary>Bulk insert inside one transaction; one summary audit row (imports / seeding).</summary>
    public int InsertMany<T>(IEnumerable<T> rows, string? summary = null) where T : Entity
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var n = InsertManyCore(c, tx, rows);
        if (n > 0) Audit(c, tx, TableOf<T>(), 0, "IMPORT", summary ?? $"Imported {n} {TableOf<T>()}", "");
        tx.Commit();
        return n;
    }

    internal int InsertManyCore<T>(SqliteConnection c, SqliteTransaction tx, IEnumerable<T> rows) where T : Entity
    {
        var n = 0;
        foreach (var e in rows) { InsertCore(c, tx, e); n++; }
        return n;
    }

    private void InsertCore<T>(SqliteConnection c, SqliteTransaction tx, T e) where T : Entity
    {
        var t = e.GetType();
        e.UpdatedBy = User;
        e.UpdatedAt = Clock();
        e.RowVersion = 1;
        var props = Props(t).Where(p => p.Name != nameof(Entity.Id)).ToArray();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO [{TableOf(t)}] ({string.Join(",", props.Select(p => $"[{p.Name}]"))}) VALUES ({string.Join(",", props.Select(p => "@" + p.Name))}); SELECT last_insert_rowid();";
        foreach (var p in props) cmd.Parameters.AddWithValue("@" + p.Name, ToDb(p.GetValue(e)));
        e.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Updates a row only if its RowVersion still matches what was read. On success RowVersion is incremented
    /// and the field-level diff is written to AuditLog. Throws <see cref="ConcurrencyException"/> otherwise.
    /// </summary>
    public T Update<T>(T e, string? summary = null) where T : Entity, new()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var before = Query<T>(c, tx, $"SELECT * FROM [{TableOf<T>()}] WHERE Id=@Id", new { e.Id }).FirstOrDefault()
                     ?? throw new InvalidOperationException($"{TableOf<T>()} #{e.Id} no longer exists.");
        if (before.RowVersion != e.RowVersion) throw new ConcurrencyException(TableOf<T>(), e.Id, before.UpdatedBy);

        var props = Props(typeof(T)).Where(p => p.Name is not (nameof(Entity.Id) or nameof(Entity.RowVersion))).ToArray();
        var oldVersion = e.RowVersion;
        e.UpdatedBy = User;
        e.UpdatedAt = Clock();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE [{TableOf<T>()}] SET {string.Join(",", props.Select(p => $"[{p.Name}]=@{p.Name}"))}, RowVersion=RowVersion+1 WHERE Id=@Id AND RowVersion=@OldVersion";
            foreach (var p in props) cmd.Parameters.AddWithValue("@" + p.Name, ToDb(p.GetValue(e)));
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@OldVersion", oldVersion);
            if (cmd.ExecuteNonQuery() != 1) throw new ConcurrencyException(TableOf<T>(), e.Id, before.UpdatedBy);
        }
        e.RowVersion = oldVersion + 1;
        var diff = Diff(before, e);
        Audit(c, tx, TableOf<T>(), e.Id, "UPDATE", summary ?? $"Changed {typeof(T).Name} #{e.Id}: {string.Join(", ", diff.Keys)}", JsonSerializer.Serialize(diff));
        tx.Commit();
        return e;
    }

    public void Delete<T>(T e, string? summary = null) where T : Entity
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM [{TableOf<T>()}] WHERE Id=@Id AND RowVersion=@V";
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@V", e.RowVersion);
            if (cmd.ExecuteNonQuery() != 1) throw new ConcurrencyException(TableOf<T>(), e.Id, null);
        }
        Audit(c, tx, TableOf<T>(), e.Id, "DELETE", summary ?? $"Deleted {typeof(T).Name} #{e.Id}", JsonSerializer.Serialize(e, e.GetType()));
        tx.Commit();
    }

    /// <summary>Runs several writes atomically; the callback gets a writer bound to one transaction.</summary>
    public void InTransaction(Action<TxWriter> work, string summary)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var w = new TxWriter(this, c, tx);
        work(w);
        Audit(c, tx, "", 0, "BATCH", summary, "");
        tx.Commit();
    }

    public sealed class TxWriter
    {
        private readonly Db _db; private readonly SqliteConnection _c; private readonly SqliteTransaction _tx;
        internal TxWriter(Db db, SqliteConnection c, SqliteTransaction tx) { _db = db; _c = c; _tx = tx; }
        public T Insert<T>(T e) where T : Entity { _db.InsertCore(_c, _tx, e); return e; }
        public int InsertMany<T>(IEnumerable<T> rows) where T : Entity => _db.InsertManyCore(_c, _tx, rows);
        public void Execute(string sql, object? args = null) { using var cmd = _c.CreateCommand(); cmd.Transaction = _tx; cmd.CommandText = sql; Bind(cmd, args); cmd.ExecuteNonQuery(); }
    }

    public void ClearAll()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in EntityTypes) Exec(c, tx, $"DELETE FROM [{TableOf(t)}];");
        Exec(c, tx, "DELETE FROM AuditLog;");
        Exec(c, tx, "DELETE FROM sqlite_sequence;");
        tx.Commit();
    }

    // ------------------------------------------------------------------ audit / presence / meta

    private void Audit(SqliteConnection c, SqliteTransaction tx, string table, long rowId, string action, string summary, string changes)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO AuditLog (At, User, Machine, TableName, RowId, Action, Summary, Changes) VALUES (@At,@User,@Machine,@T,@R,@A,@S,@C)";
        cmd.Parameters.AddWithValue("@At", ToDb(Clock()));
        cmd.Parameters.AddWithValue("@User", User);
        cmd.Parameters.AddWithValue("@Machine", Machine);
        cmd.Parameters.AddWithValue("@T", table);
        cmd.Parameters.AddWithValue("@R", rowId);
        cmd.Parameters.AddWithValue("@A", action);
        cmd.Parameters.AddWithValue("@S", summary);
        cmd.Parameters.AddWithValue("@C", changes);
        cmd.ExecuteNonQuery();
    }

    public void LogEvent(string action, string summary)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Audit(c, tx, "", 0, action, summary, "");
        tx.Commit();
    }

    public List<AuditEntry> RecentAudit(int take = 50, DateTime? since = null)
    {
        using var c = Open();
        return since is null
            ? Query<AuditEntry>(c, null, "SELECT * FROM AuditLog ORDER BY Id DESC LIMIT @N", new { N = take })
            : Query<AuditEntry>(c, null, "SELECT * FROM AuditLog WHERE At > @Since ORDER BY Id DESC LIMIT @N", new { N = take, Since = since.Value });
    }

    public void Heartbeat(string screen)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Presence (Machine, User, Screen, LastSeen) VALUES (@M,@U,@S,@L) ON CONFLICT(Machine, User) DO UPDATE SET Screen=@S, LastSeen=@L";
        cmd.Parameters.AddWithValue("@M", Machine);
        cmd.Parameters.AddWithValue("@U", User);
        cmd.Parameters.AddWithValue("@S", screen);
        cmd.Parameters.AddWithValue("@L", ToDb(Clock()));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Other people with a heartbeat within <paramref name="window"/>.</summary>
    public List<PresenceRow> OthersOnline(TimeSpan window)
    {
        using var c = Open();
        var cutoff = Clock() - window;
        return Query<PresenceRow>(c, null, "SELECT * FROM Presence WHERE LastSeen > @Cut", new { Cut = cutoff })
            .Where(p => !(p.Machine == Machine && p.User == User)).ToList();
    }

    public string? GetMeta(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Meta WHERE Key=@K";
        cmd.Parameters.AddWithValue("@K", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Meta (Key, Value) VALUES (@K,@V) ON CONFLICT(Key) DO UPDATE SET Value=@V";
        cmd.Parameters.AddWithValue("@K", key);
        cmd.Parameters.AddWithValue("@V", value);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, object?[]> Diff<T>(T before, T after)
    {
        var d = new Dictionary<string, object?[]>();
        foreach (var p in Props(typeof(T)))
        {
            if (p.Name is nameof(Entity.UpdatedAt) or nameof(Entity.UpdatedBy) or nameof(Entity.RowVersion)) continue;
            var a = p.GetValue(before); var b = p.GetValue(after);
            if (!Equals(a, b)) d[p.Name] = new[] { a, b };
        }
        return d;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, object? args)
    {
        if (args is null) return;
        foreach (var p in args.GetType().GetProperties())
            cmd.Parameters.AddWithValue("@" + p.Name, ToDb(p.GetValue(args)));
    }

    internal static object ToDb(object? v) => v switch
    {
        null => DBNull.Value,
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
        bool b => b ? 1L : 0L,
        Enum en => Convert.ToInt64(en, CultureInfo.InvariantCulture),
        _ => v,
    };

    internal static object? FromDb(object v, Type target)
    {
        if (v is DBNull) return null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture);
        if (t == typeof(DateTime)) return v is string s ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : Convert.ToDateTime(v, CultureInfo.InvariantCulture);
        if (t == typeof(bool)) return Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt64(v, CultureInfo.InvariantCulture));
        return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
    }

    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder(ex.Message);
        if (ex is SqliteException { SqliteErrorCode: 5 or 6 }) sb.Append(" (the data file is busy - someone else is saving; try again in a moment)");
        return sb.ToString();
    }
}
