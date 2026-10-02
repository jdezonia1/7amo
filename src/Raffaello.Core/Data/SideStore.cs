using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Data;

/// <summary>
/// [phase4] Base for module stores that keep their own tables in the shared data file without touching
/// <see cref="Db"/>'s table list: same file, same pragmas (WAL, busy timeout), same conventions
/// (Id / UpdatedBy / UpdatedAt / RowVersion, optimistic concurrency, AuditLog row per change, additive column migration).
/// </summary>
public abstract class SideStore
{
    protected SideStore(string path, string user, string? machine = null)
    {
        Path = path;
        User = string.IsNullOrWhiteSpace(user) ? Environment.UserName : user;
        Machine = machine ?? Environment.MachineName;
    }

    public string Path { get; }
    public string User { get; set; }
    public string Machine { get; }
    public int BusyTimeoutMs { get; set; } = 8000;
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Entity types (tables) this store owns.</summary>
    protected abstract IEnumerable<Type> Tables { get; }

    /// <summary>Extra indexes (CREATE INDEX IF NOT EXISTS ...).</summary>
    protected virtual IEnumerable<string> Indexes => Array.Empty<string>();

    public SqliteConnection Open()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = Math.Max(1, BusyTimeoutMs / 1000) }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, null, $"PRAGMA busy_timeout={BusyTimeoutMs};");
        Exec(c, null, "PRAGMA journal_mode=WAL;");
        return c;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in Tables) EnsureTable(c, tx, t);
        Exec(c, tx, @"CREATE TABLE IF NOT EXISTS AuditLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, User TEXT, Machine TEXT,
            TableName TEXT, RowId INTEGER, Action TEXT, Summary TEXT, Changes TEXT);");
        foreach (var ix in Indexes) Exec(c, tx, ix);
        tx.Commit();
    }

    private static void EnsureTable(SqliteConnection c, SqliteTransaction tx, Type t)
    {
        var table = Db.TableOf(t);
        var props = Db.Props(t);
        var cols = props.Where(p => p.Name != nameof(Entity.Id)).Select(p => $"[{p.Name}] {SqlType(p.PropertyType)}");
        Exec(c, tx, $"CREATE TABLE IF NOT EXISTS [{table}] (Id INTEGER PRIMARY KEY AUTOINCREMENT, {string.Join(", ", cols)});");
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
        if (t == typeof(byte[])) return "BLOB";
        return "TEXT";
    }

    // ------------------------------------------------------------------ reads

    public List<T> All<T>(string? where = null, object? args = null) where T : new()
    {
        using var c = Open();
        return Db.Query<T>(c, null, $"SELECT * FROM [{Db.TableOf<T>()}]" + (where is null ? "" : " WHERE " + where), args);
    }

    public T? Get<T>(long id) where T : Entity, new() => All<T>("Id=@Id", new { Id = id }).FirstOrDefault();

    // ------------------------------------------------------------------ writes

    public T Insert<T>(T e, string? summary = null) where T : Entity
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        InsertCore(c, tx, e);
        Audit(c, tx, Db.TableOf(e.GetType()), e.Id, "INSERT", summary ?? $"Added {typeof(T).Name} #{e.Id}", JsonSerializer.Serialize(e, e.GetType()));
        tx.Commit();
        return e;
    }

    public T Update<T>(T e, string? summary = null) where T : Entity, new()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        UpdateCore(c, tx, e, summary);
        tx.Commit();
        return e;
    }

    public void Delete<T>(T e, string? summary = null) where T : Entity
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        DeleteCore(c, tx, e);
        Audit(c, tx, Db.TableOf(e.GetType()), e.Id, "DELETE", summary ?? $"Deleted {typeof(T).Name} #{e.Id}", "");
        tx.Commit();
    }

    /// <summary>Several writes in one transaction with one audit row.</summary>
    public void Batch(Action<SideBatch> work, string summary)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        work(new SideBatch(this, c, tx));
        Audit(c, tx, "", 0, "BATCH", summary, "");
        tx.Commit();
    }

    public sealed class SideBatch
    {
        private readonly SideStore _s; private readonly SqliteConnection _c; private readonly SqliteTransaction _tx;
        internal SideBatch(SideStore s, SqliteConnection c, SqliteTransaction tx) { _s = s; _c = c; _tx = tx; }
        public T Insert<T>(T e) where T : Entity { _s.InsertCore(_c, _tx, e); return e; }
        public void Update<T>(T e) where T : Entity, new() => _s.UpdateCore(_c, _tx, e, null, audit: false);
        public void Delete<T>(T e) where T : Entity => _s.DeleteCore(_c, _tx, e);
        public List<T> All<T>(string? where = null, object? args = null) where T : new() =>
            Db.Query<T>(_c, _tx, $"SELECT * FROM [{Db.TableOf<T>()}]" + (where is null ? "" : " WHERE " + where), args);
    }

    private void InsertCore<T>(SqliteConnection c, SqliteTransaction tx, T e) where T : Entity
    {
        var t = e.GetType();
        e.UpdatedBy = User;
        e.UpdatedAt = Clock();
        e.RowVersion = 1;
        var props = Db.Props(t).Where(p => p.Name != nameof(Entity.Id)).ToArray();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO [{Db.TableOf(t)}] ({string.Join(",", props.Select(p => $"[{p.Name}]"))}) VALUES ({string.Join(",", props.Select(p => "@" + p.Name))}); SELECT last_insert_rowid();";
        foreach (var p in props) cmd.Parameters.AddWithValue("@" + p.Name, Db.ToDb(p.GetValue(e)));
        e.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void UpdateCore<T>(SqliteConnection c, SqliteTransaction tx, T e, string? summary, bool audit = true) where T : Entity, new()
    {
        var table = Db.TableOf<T>();
        var before = Db.Query<T>(c, tx, $"SELECT * FROM [{table}] WHERE Id=@Id", new { e.Id }).FirstOrDefault()
                     ?? throw new InvalidOperationException($"{table} #{e.Id} no longer exists.");
        if (before.RowVersion != e.RowVersion) throw new ConcurrencyException(table, e.Id, before.UpdatedBy);
        var props = Db.Props(typeof(T)).Where(p => p.Name is not (nameof(Entity.Id) or nameof(Entity.RowVersion))).ToArray();
        var old = e.RowVersion;
        e.UpdatedBy = User;
        e.UpdatedAt = Clock();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE [{table}] SET {string.Join(",", props.Select(p => $"[{p.Name}]=@{p.Name}"))}, RowVersion=RowVersion+1 WHERE Id=@Id AND RowVersion=@OldVersion";
            foreach (var p in props) cmd.Parameters.AddWithValue("@" + p.Name, Db.ToDb(p.GetValue(e)));
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@OldVersion", old);
            if (cmd.ExecuteNonQuery() != 1) throw new ConcurrencyException(table, e.Id, before.UpdatedBy);
        }
        e.RowVersion = old + 1;
        if (!audit) return;
        var diff = new Dictionary<string, object?[]>();
        foreach (var p in Db.Props(typeof(T)))
        {
            if (p.Name is nameof(Entity.UpdatedAt) or nameof(Entity.UpdatedBy) or nameof(Entity.RowVersion)) continue;
            var a = p.GetValue(before); var b = p.GetValue(e);
            if (!Equals(a, b)) diff[p.Name] = new[] { a, b };
        }
        Audit(c, tx, table, e.Id, "UPDATE", summary ?? $"Changed {typeof(T).Name} #{e.Id}: {string.Join(", ", diff.Keys)}", JsonSerializer.Serialize(diff));
    }

    private void DeleteCore<T>(SqliteConnection c, SqliteTransaction tx, T e) where T : Entity
    {
        var table = Db.TableOf(e.GetType());
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM [{table}] WHERE Id=@Id AND RowVersion=@V";
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@V", e.RowVersion);
        if (cmd.ExecuteNonQuery() != 1) throw new ConcurrencyException(table, e.Id, null);
    }

    protected void Audit(SqliteConnection c, SqliteTransaction tx, string table, long rowId, string action, string summary, string changes)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO AuditLog (At, User, Machine, TableName, RowId, Action, Summary, Changes) VALUES (@At,@User,@Machine,@T,@R,@A,@S,@C)";
        cmd.Parameters.AddWithValue("@At", Db.ToDb(Clock()));
        cmd.Parameters.AddWithValue("@User", User);
        cmd.Parameters.AddWithValue("@Machine", Machine);
        cmd.Parameters.AddWithValue("@T", table);
        cmd.Parameters.AddWithValue("@R", rowId);
        cmd.Parameters.AddWithValue("@A", action);
        cmd.Parameters.AddWithValue("@S", summary);
        cmd.Parameters.AddWithValue("@C", changes);
        cmd.ExecuteNonQuery();
        Trust.SqliteAuditChain.Seal(c, tx);   // [trust] hash chain: seal the new row in the same transaction
    }

    public void LogEvent(string action, string summary)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Audit(c, tx, "", 0, action, summary, "");
        tx.Commit();
    }

    protected static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
