using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using static Raffaello.Server.Data.PgMap;

namespace Raffaello.Server.Data;

/// <summary>Receives committed changes (the SignalR hub publisher in the server; a list in tests).</summary>
public interface IChangeSink
{
    void Publish(IReadOnlyList<ChangeNotice> notices);
}

/// <summary>One entity write of a request.</summary>
public sealed record EntityOp(string Op, Type Type, Entity Entity);

/// <summary>
/// PostgreSQL implementation of <see cref="IProjectStore"/> with the same semantics as the SQLite <see cref="Db"/>:
/// RowVersion optimistic concurrency (UPDATE ... WHERE RowVersion = @old), an AuditLog row in the same transaction
/// (who / when / machine / old / new), presence and meta. On top: write guards (permissions, append-only ledger with the
/// REMAINING check under a ledger-key lock, invoice approvals), idempotent requests, per-table change versions (ETag)
/// and change notices. One instance per request / user; <see cref="StoreIdentity"/> comes from authentication, never from the client body.
/// </summary>
public sealed class PgStore : IProjectStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly IReadOnlyList<IWriteGuard> _guards;
    private readonly IChangeSink? _sink;
    private StoreIdentity _who;

    public PgStore(NpgsqlDataSource ds, StoreIdentity who, IEnumerable<IWriteGuard>? guards = null, IChangeSink? sink = null)
    {
        _ds = ds; _who = who; _sink = sink;
        _guards = (guards ?? DefaultGuards()).ToList();
        EntityRegistry.EnsureInitialized();
    }

    public static IEnumerable<IWriteGuard> DefaultGuards(bool requireInternalApproval = true) =>
        new IWriteGuard[] { new PermissionGuard(), new LedgerGuard(), new InvoiceGuard { RequireInternalApproval = requireInternalApproval } };

    public StoreIdentity Identity => _who;
    public NpgsqlDataSource DataSource => _ds;
    public string Location { get { var b = new NpgsqlConnectionStringBuilder(_ds.ConnectionString); return $"postgres://{b.Host}:{b.Port}/{b.Database}"; } }
    public string User { get => _who.User; set => _who = _who with { User = value }; }
    public string Machine => _who.Machine;
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public void EnsureSchema() => PgSchema.Migrate(_ds);

    // ------------------------------------------------------------------ reads

    public int Count<T>() => CountOf(typeof(T));

    public int CountOf(Type t)
    {
        using var c = _ds.OpenConnection();
        return Convert.ToInt32(new PgTx(c, null).Scalar($"SELECT COUNT(*) FROM {Q(EntityRegistry.TableOf(t))}"), CultureInfo.InvariantCulture);
    }

    public List<T> All<T>() where T : new() => AllOf(typeof(T)).Cast<T>().ToList();

    public List<object> AllOf(Type t) => AllWithVersion(t).Rows;

    /// <summary>All rows plus the table's change version, read in one snapshot (the version is the ETag clients cache on).</summary>
    public (List<object> Rows, long Version) AllWithVersion(Type t)
    {
        var table = EntityRegistry.TableOf(t);
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction(IsolationLevel.RepeatableRead);
        var p = new PgTx(c, tx);
        var version = TableVersion(p, table);
        var rows = p.Query(t, $"SELECT * FROM {Q(table)} ORDER BY {Q("Id")}");
        tx.Commit();
        return (rows, version);
    }

    public long TableVersion(string table)
    {
        using var c = _ds.OpenConnection();
        return TableVersion(new PgTx(c, null), table);
    }

    private static long TableVersion(PgTx p, string table) =>
        Convert.ToInt64(p.Scalar("""SELECT "Version" FROM "__table_versions" WHERE "TableName" = @t""", ("t", table)) ?? 0L, CultureInfo.InvariantCulture);

    public T? Get<T>(long id) where T : Entity, new() => (T?)GetOf(typeof(T), id);

    public Entity? GetOf(Type t, long id)
    {
        using var c = _ds.OpenConnection();
        return new PgTx(c, null).Query(t, $"SELECT * FROM {Q(EntityRegistry.TableOf(t))} WHERE {Q("Id")} = @id", ("id", id)).Cast<Entity>().FirstOrDefault();
    }

    // ------------------------------------------------------------------ IProjectStore writes

    public T Insert<T>(T entity, string? summary = null) where T : Entity
    {
        Execute(new[] { new EntityOp(WriteOps.Insert, entity.GetType(), entity) }, WriteModes.Single, summary, null);
        return entity;
    }

    public int InsertMany<T>(IEnumerable<T> entities, string? summary = null) where T : Entity
    {
        var ops = entities.Select(e => new EntityOp(WriteOps.Insert, e.GetType(), e)).ToList();
        if (ops.Count == 0) return 0;
        Execute(ops, WriteModes.Many, summary, null);
        return ops.Count;
    }

    public T Update<T>(T entity, string? summary = null) where T : Entity, new()
    {
        Execute(new[] { new EntityOp(WriteOps.Update, entity.GetType(), entity) }, WriteModes.Single, summary, null);
        return entity;
    }

    public void Delete<T>(T entity, string? summary = null) where T : Entity =>
        Execute(new[] { new EntityOp(WriteOps.Delete, entity.GetType(), entity) }, WriteModes.Single, summary, null);

    public void Batch(Action<IStoreBatch> work, string summary)
    {
        var rec = new RecordingBatch();
        work(rec);
        Execute(rec.Ops, WriteModes.Batch, summary, null);
    }

    /// <summary>Collects the ops of a batch; inserts get temporary negative ids so later ops can reference them.</summary>
    private sealed class RecordingBatch : IStoreBatch
    {
        private long _next = -1;
        public List<EntityOp> Ops { get; } = new();
        public T Insert<T>(T e) where T : Entity { e.Id = _next--; Ops.Add(new(WriteOps.Insert, e.GetType(), e)); return e; }
        public int InsertMany<T>(IEnumerable<T> rows) where T : Entity { var n = 0; foreach (var e in rows) { Insert(e); n++; } return n; }
        public void Update<T>(T e) where T : Entity, new() => Ops.Add(new(WriteOps.Update, e.GetType(), e));
        public void Delete<T>(T e) where T : Entity => Ops.Add(new(WriteOps.Delete, e.GetType(), e));
    }

    // ------------------------------------------------------------------ the write engine

    /// <summary>Executes a request from the wire (entities as JSON).</summary>
    public WriteResponseDto Execute(WriteRequestDto req)
    {
        var ops = req.Ops.Select(o =>
        {
            var t = EntityRegistry.Require(o.Table);
            var e = (Entity?)o.Entity.Deserialize(t, RemoteJson.Options) ?? throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Empty entity for {o.Table}.");
            var op = (o.Op ?? "").ToUpperInvariant();
            if (op is not (WriteOps.Insert or WriteOps.Update or WriteOps.Delete)) throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Unknown op '{o.Op}'.");
            return new EntityOp(op, t, e);
        }).ToList();
        var mode = (req.Mode ?? WriteModes.Single).ToUpperInvariant();
        if (mode == WriteModes.Single && ops.Count != 1) mode = WriteModes.Batch;
        return Execute(ops, mode, req.Summary, req.RequestId == Guid.Empty ? null : req.RequestId);
    }

    /// <summary>
    /// Runs the ops atomically (READ COMMITTED + row locks / ledger advisory locks). Insert ids &lt; 0 are temporary: the real
    /// id replaces them, and every *Id column of later ops that carries the same temporary id is remapped.
    /// </summary>
    public WriteResponseDto Execute(IReadOnlyList<EntityOp> ops, string mode, string? summary, Guid? requestId)
    {
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction(IsolationLevel.ReadCommitted);
        var p = new PgTx(c, tx);

        if (requestId is Guid rid)
        {
            p.Lock("request|" + rid);
            var stored = p.Scalar("""SELECT "Response" FROM "__requests" WHERE "RequestId" = @id""", ("id", rid)) as string;
            if (stored != null)
            {
                tx.Rollback();
                var prev = RemoteJson.Deserialize<WriteResponseDto>(stored)!;
                prev.Replayed = true;
                ApplyResults(ops, prev);
                return prev;
            }
        }

        var res = new WriteResponseDto { RequestId = requestId ?? Guid.Empty };
        var notices = new List<ChangeNotice>();
        var touched = new HashSet<string>();
        var inserted = new HashSet<(string, long)>();
        var now = Clock();
        var insertsInBatch = ops.Count(o => o.Op == WriteOps.Insert);

        foreach (var op in ops)
        {
            var table = EntityRegistry.TableOf(op.Type);
            var e = op.Entity;
            Remap(e, op.Type, res.TempIds, op.Op);
            touched.Add(table);
            switch (op.Op)
            {
                case WriteOps.Insert:
                {
                    var tempId = e.Id < 0 ? e.Id : 0;
                    e.Id = 0;
                    var check = NewCheck(WriteKind.Insert, op.Type, table, e, null, p, inserted);
                    foreach (var g in _guards) g.Check(check);
                    e.UpdatedBy = _who.User; e.UpdatedAt = now; e.RowVersion = 1;
                    InsertRow(p, e, op.Type, keepId: false);
                    if (tempId < 0) res.TempIds[tempId] = e.Id;
                    inserted.Add((table, e.Id));
                    foreach (var g in _guards) g.AfterWrite(check);
                    if (mode == WriteModes.Single || (mode == WriteModes.Batch && insertsInBatch <= 50))
                        Audit(p, table, e.Id, "INSERT", mode == WriteModes.Single ? summary ?? $"Added {op.Type.Name} #{e.Id}" : $"Added {op.Type.Name} #{e.Id}", "", null, Json(e));
                    res.Results.Add(Result(op.Op, table, tempId, e));
                    if (mode != WriteModes.Many) notices.Add(Notice(table, e, "INSERT", summary));
                    break;
                }
                case WriteOps.Update:
                {
                    var before = LockRow(p, op.Type, table, e.Id);
                    if (before.RowVersion != e.RowVersion) throw Conflict(table, e.Id, before);
                    var check = NewCheck(WriteKind.Update, op.Type, table, e, before, p, inserted);
                    foreach (var g in _guards) g.Check(check);
                    var oldVersion = e.RowVersion;
                    e.UpdatedBy = _who.User; e.UpdatedAt = now;
                    UpdateRow(p, e, op.Type, oldVersion);
                    e.RowVersion = oldVersion + 1;
                    foreach (var g in _guards) g.AfterWrite(check);
                    var diff = Diff(before, e, op.Type);
                    var s = mode == WriteModes.Single && summary != null ? summary : $"Changed {op.Type.Name} #{e.Id}: {string.Join(", ", diff.Keys)}";
                    Audit(p, table, e.Id, "UPDATE", s, JsonSerializer.Serialize(diff, RemoteJson.Options), Json(before), Json(e));
                    res.Results.Add(Result(op.Op, table, 0, e));
                    notices.Add(Notice(table, e, "UPDATE", summary));
                    break;
                }
                case WriteOps.Delete:
                {
                    var before = LockRow(p, op.Type, table, e.Id);
                    if (before.RowVersion != e.RowVersion) throw Conflict(table, e.Id, before);
                    var check = NewCheck(WriteKind.Delete, op.Type, table, e, before, p, inserted);
                    foreach (var g in _guards) g.Check(check);
                    p.Exec($"DELETE FROM {Q(table)} WHERE {Q("Id")} = @id", ("id", e.Id));
                    foreach (var g in _guards) g.AfterWrite(check);
                    Audit(p, table, e.Id, "DELETE", mode == WriteModes.Single && summary != null ? summary : $"Deleted {op.Type.Name} #{e.Id}", "", Json(before), null);
                    res.Results.Add(Result(op.Op, table, 0, e));
                    notices.Add(Notice(table, e, "DELETE", summary));
                    break;
                }
            }
        }

        if (mode == WriteModes.Many && ops.Count > 0)
        {
            var table = EntityRegistry.TableOf(ops[0].Type);
            Audit(p, table, 0, "IMPORT", summary ?? $"Imported {ops.Count} {table}", "", null, null);
            notices.Add(new ChangeNotice { Table = table, Action = "IMPORT", Count = ops.Count, By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId, Summary = summary ?? "", At = now });
        }
        if (mode == WriteModes.Batch) Audit(p, "", 0, "BATCH", summary ?? $"{ops.Count} changes", "", null, null);

        foreach (var t in touched) BumpVersion(p, t);
        if (requestId is Guid r2)
            p.Exec("""INSERT INTO "__requests" ("RequestId", "At", "User", "Response") VALUES (@id, @at, @u, @resp)""",
                ("id", r2), ("at", now), ("u", _who.User), ("resp", RemoteJson.Serialize(res)));
        tx.Commit();

        Publish(Compact(notices, summary, now));
        return res;
    }

    private static void ApplyResults(IReadOnlyList<EntityOp> ops, WriteResponseDto res)
    {
        for (var i = 0; i < ops.Count && i < res.Results.Count; i++)
        {
            var r = res.Results[i];
            if (ops[i].Op == WriteOps.Delete) continue;
            ops[i].Entity.Id = r.Id; ops[i].Entity.RowVersion = r.RowVersion; ops[i].Entity.UpdatedBy = r.UpdatedBy; ops[i].Entity.UpdatedAt = r.UpdatedAt;
        }
    }

    private WriteCheck NewCheck(WriteKind kind, Type type, string table, Entity e, Entity? before, PgTx p, HashSet<(string, long)> inserted) => new()
    {
        Kind = kind, Type = type, Table = table, Entity = e, Before = before, Who = _who, Tx = p,
        InsertedInRequest = (t, id) => inserted.Contains((t, id)), Clock = Clock,
    };

    private static void Remap(Entity e, Type t, Dictionary<long, long> map, string op)
    {
        if (op != WriteOps.Insert && e.Id < 0)
            e.Id = map.TryGetValue(e.Id, out var real) ? real : throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Temporary id {e.Id} of {t.Name} is not known in this request.");
        foreach (var fk in EntityRegistry.ForeignKeys(t))
        {
            var v = fk.GetValue(e) as long?;
            if (v is long id && id < 0)
            {
                if (!map.TryGetValue(id, out var real))
                    throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"{t.Name}.{fk.Name} refers to temporary id {id} that was not inserted earlier in this request.");
                fk.SetValue(e, real);
            }
        }
    }

    private static Entity LockRow(PgTx p, Type t, string table, long id) =>
        p.Query(t, $"SELECT * FROM {Q(table)} WHERE {Q("Id")} = @id FOR UPDATE", ("id", id)).Cast<Entity>().FirstOrDefault()
        ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"{table} #{id} no longer exists.");

    private static ConcurrencyException Conflict(string table, long id, Entity current)
    {
        var ex = new ConcurrencyException(table, id, current.UpdatedBy);
        ex.Data["current"] = current;
        return ex;
    }

    internal static void InsertRow(PgTx p, Entity e, Type t, bool keepId)
    {
        var table = EntityRegistry.TableOf(t);
        var props = EntityRegistry.Props(t).Where(x => keepId || x.Name != nameof(Entity.Id)).ToArray();
        var sql = $"INSERT INTO {Q(table)} ({string.Join(",", props.Select(x => Q(x.Name)))}) VALUES ({string.Join(",", props.Select((_, i) => "@p" + i))})"
                  + (keepId ? $" ON CONFLICT ({Q("Id")}) DO NOTHING RETURNING {Q("Id")}" : $" RETURNING {Q("Id")}");
        using var cmd = new NpgsqlCommand(sql, p.Connection, p.Transaction);
        for (var i = 0; i < props.Length; i++) cmd.Parameters.Add(Param("p" + i, props[i].GetValue(e), props[i].PropertyType));
        var id = cmd.ExecuteScalar();
        if (!keepId) e.Id = Convert.ToInt64(id, CultureInfo.InvariantCulture);
        else if (id is null) throw new DuplicateIdException();
    }

    internal sealed class DuplicateIdException : Exception { }

    private static void UpdateRow(PgTx p, Entity e, Type t, long oldVersion)
    {
        var table = EntityRegistry.TableOf(t);
        var props = EntityRegistry.Props(t).Where(x => x.Name is not (nameof(Entity.Id) or nameof(Entity.RowVersion))).ToArray();
        var sql = $"UPDATE {Q(table)} SET {string.Join(",", props.Select((x, i) => $"{Q(x.Name)} = @p{i}"))}, {Q("RowVersion")} = {Q("RowVersion")} + 1 WHERE {Q("Id")} = @id AND {Q("RowVersion")} = @old";
        using var cmd = new NpgsqlCommand(sql, p.Connection, p.Transaction);
        for (var i = 0; i < props.Length; i++) cmd.Parameters.Add(Param("p" + i, props[i].GetValue(e), props[i].PropertyType));
        cmd.Parameters.Add(Param("id", e.Id));
        cmd.Parameters.Add(Param("old", oldVersion));
        if (cmd.ExecuteNonQuery() != 1) throw new ConcurrencyException(table, e.Id, null);
    }

    private static Dictionary<string, object?[]> Diff(Entity before, Entity after, Type t)
    {
        var d = new Dictionary<string, object?[]>();
        foreach (var prop in EntityRegistry.Props(t))
        {
            if (prop.Name is nameof(Entity.UpdatedAt) or nameof(Entity.UpdatedBy) or nameof(Entity.RowVersion)) continue;
            var a = prop.GetValue(before); var b = prop.GetValue(after);
            if (a is byte[] ba && b is byte[] bb ? !ba.AsSpan().SequenceEqual(bb) : !Equals(a, b))
                d[prop.Name] = a is byte[] || b is byte[] ? new object?[] { "(binary)", "(binary)" } : new[] { a, b };
        }
        return d;
    }

    private static string Json(Entity e) => RemoteJson.Serialize(e);

    private static WriteResultDto Result(string op, string table, long tempId, Entity e) =>
        new() { Op = op, Table = table, TempId = tempId, Id = e.Id, RowVersion = e.RowVersion, UpdatedBy = e.UpdatedBy, UpdatedAt = e.UpdatedAt };

    private ChangeNotice Notice(string table, Entity e, string action, string? summary) => new()
    {
        Table = table, Id = e.Id, Version = e.RowVersion, Action = action, By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId,
        Summary = summary ?? $"{action} {table} #{e.Id}", At = Clock(),
    };

    /// <summary>Big batches become one notice per table (clients reload the table anyway).</summary>
    private List<ChangeNotice> Compact(List<ChangeNotice> notices, string? summary, DateTime now)
    {
        if (notices.Count <= 50) return notices;
        return notices.GroupBy(n => n.Table).Select(g => new ChangeNotice
        {
            Table = g.Key, Action = "BATCH", Count = g.Sum(n => n.Count), By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId, Summary = summary ?? "", At = now,
        }).ToList();
    }

    private void Publish(IReadOnlyList<ChangeNotice> notices)
    {
        if (notices.Count == 0 || _sink is null) return;
        try { _sink.Publish(notices); } catch { /* notification must never fail a committed write */ }
    }

    private static void BumpVersion(PgTx p, string table) =>
        p.Exec("""INSERT INTO "__table_versions" ("TableName", "Version") VALUES (@t, 1) ON CONFLICT ("TableName") DO UPDATE SET "Version" = "__table_versions"."Version" + 1""", ("t", table));

    private void Audit(PgTx p, string table, long rowId, string action, string summary, string changes, string? oldJson, string? newJson) =>
        p.Exec("""
            INSERT INTO "AuditLog" ("At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes", "OldJson", "NewJson")
            VALUES (@at, @u, @m, @t, @r, @a, @s, @c, @o, @n)
            """, ("at", Clock()), ("u", _who.User), ("m", _who.Machine), ("t", table), ("r", rowId), ("a", action), ("s", summary), ("c", changes), ("o", oldJson), ("n", newJson));

    // ------------------------------------------------------------------ admin

    public void ClearAll()
    {
        if (!_who.Can(Permissions.ClearAll)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{_who.User} ({_who.Role}) may not clear the project data.");
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        var tables = EntityRegistry.All.Select(EntityRegistry.TableOf).ToList();
        p.Exec($"TRUNCATE {string.Join(", ", tables.Select(Q))} RESTART IDENTITY");
        p.Exec("""DELETE FROM "ApprovalStamps" """);
        foreach (var t in tables) BumpVersion(p, t);
        Audit(p, "", 0, "RESET", "All project data cleared (audit history kept)", "", null, null);
        tx.Commit();
        Publish(new[] { new ChangeNotice { Table = "*", Action = "RESET", By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId, Summary = "All project data cleared", At = Clock() } });
    }

    /// <summary>
    /// Migration import: inserts rows keeping their ids (so foreign keys stay valid), skipping ids that already exist,
    /// then moves the identity sequence past the highest id. Idempotent. No guards: the rows are history.
    /// </summary>
    public (int Inserted, int Skipped) ImportWithIds(Type t, IEnumerable<Entity> rows, string source)
    {
        if (!_who.Can(Permissions.Migrate)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{_who.User} ({_who.Role}) may not migrate data.");
        var table = EntityRegistry.TableOf(t);
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        int ins = 0, skip = 0;
        foreach (var e in rows)
        {
            if (e.Id <= 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"{table}: migrated rows need their local id.");
            if (e.RowVersion <= 0) e.RowVersion = 1;
            try { InsertRow(p, e, t, keepId: true); ins++; }
            catch (DuplicateIdException) { skip++; }
        }
        PgSchema.ResetIdentity(p, table);
        if (ins > 0)
        {
            BumpVersion(p, table);
            Audit(p, table, 0, "MIGRATE", $"Migrated {ins} {table} from {source} ({skip} already on the server)", "", null, null);
        }
        tx.Commit();
        if (ins > 0) Publish(new[] { new ChangeNotice { Table = table, Action = "MIGRATE", Count = ins, By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId, Summary = $"Migrated {ins} {table}", At = Clock() } });
        return (ins, skip);
    }

    /// <summary>Copies the local audit history once (tracked per source in Meta). Returns rows added.</summary>
    public int ImportAudit(string sourceKey, IReadOnlyList<AuditEntry> rows)
    {
        if (!_who.Can(Permissions.Migrate)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{_who.User} ({_who.Role}) may not migrate data.");
        var metaKey = "Migration:AuditMaxId:" + sourceKey;
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        p.Lock(metaKey);
        var done = long.TryParse(p.Scalar("""SELECT "Value" FROM "Meta" WHERE "Key" = @k""", ("k", metaKey)) as string, out var m) ? m : 0;
        var add = rows.Where(r => r.Id > done).OrderBy(r => r.Id).ToList();
        foreach (var r in add)
            p.Exec("""
                INSERT INTO "AuditLog" ("At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes")
                VALUES (@at, @u, @m, @t, @r, @a, @s, @c)
                """, ("at", r.At), ("u", r.User), ("m", r.Machine), ("t", r.TableName), ("r", r.RowId), ("a", r.Action), ("s", "[migrated] " + r.Summary), ("c", r.Changes));
        if (add.Count > 0)
            p.Exec("""INSERT INTO "Meta" ("Key", "Value") VALUES (@k, @v) ON CONFLICT ("Key") DO UPDATE SET "Value" = @v""", ("k", metaKey), ("v", add.Max(r => r.Id).ToString(CultureInfo.InvariantCulture)));
        tx.Commit();
        return add.Count;
    }

    // ------------------------------------------------------------------ audit / presence / meta

    public void LogEvent(string action, string summary)
    {
        using var c = _ds.OpenConnection();
        Audit(new PgTx(c, null), "", 0, action, summary, "", null, null);
    }

    public List<AuditEntry> RecentAudit(int take = 50, DateTime? since = null)
    {
        using var c = _ds.OpenConnection();
        var p = new PgTx(c, null);
        return since is null
            ? p.Query<AuditEntry>("""SELECT * FROM "AuditLog" ORDER BY "Id" DESC LIMIT @n""", ("n", take))
            : p.Query<AuditEntry>("""SELECT * FROM "AuditLog" WHERE "At" > @s ORDER BY "Id" DESC LIMIT @n""", ("n", take), ("s", since.Value));
    }

    public void Heartbeat(string screen)
    {
        using var c = _ds.OpenConnection();
        new PgTx(c, null).Exec("""
            INSERT INTO "Presence" ("Machine", "User", "Screen", "LastSeen") VALUES (@m, @u, @s, @l)
            ON CONFLICT ("Machine", "User") DO UPDATE SET "Screen" = @s, "LastSeen" = @l
            """, ("m", Machine), ("u", User), ("s", screen ?? ""), ("l", Clock()));
    }

    public List<PresenceRow> OthersOnline(TimeSpan window)
    {
        using var c = _ds.OpenConnection();
        return new PgTx(c, null).Query<PresenceRow>("""SELECT * FROM "Presence" WHERE "LastSeen" > @cut ORDER BY "User" """, ("cut", Clock() - window))
            .Where(x => !(x.Machine == Machine && x.User == User)).ToList();
    }

    public string? GetMeta(string key)
    {
        using var c = _ds.OpenConnection();
        return new PgTx(c, null).Scalar("""SELECT "Value" FROM "Meta" WHERE "Key" = @k""", ("k", key)) as string;
    }

    public void SetMeta(string key, string value)
    {
        if (!_who.Can(Permissions.EditData)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{_who.User} ({_who.Role}) may not change project settings.");
        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        var old = p.Scalar("""SELECT "Value" FROM "Meta" WHERE "Key" = @k FOR UPDATE""", ("k", key)) as string;
        p.Exec("""INSERT INTO "Meta" ("Key", "Value") VALUES (@k, @v) ON CONFLICT ("Key") DO UPDATE SET "Value" = @v""", ("k", key), ("v", value));
        if (old != value) Audit(p, "Meta", 0, "META", $"{key} = {value}", JsonSerializer.Serialize(new Dictionary<string, object?[]> { [key] = new object?[] { old, value } }), null, null);
        BumpVersion(p, "Meta");
        tx.Commit();
    }

    // ------------------------------------------------------------------ approvals

    public List<ApprovalStampDto> Stamps(string table, long id)
    {
        using var c = _ds.OpenConnection();
        return new PgTx(c, null).Query<ApprovalStampDto>("""SELECT * FROM "ApprovalStamps" WHERE "TableName" = @t AND "RowId" = @id ORDER BY "Id" """, ("t", table), ("id", id));
    }

    /// <summary>Gives the CHECKED or APPROVED stamp to a subcontractor invoice revision (internal approval chain).</summary>
    public ApprovalStampDto Stamp(StampRequest req)
    {
        var stage = (req.Stage ?? "").ToUpperInvariant();
        if (!string.Equals(req.Table, "SubInvoices", StringComparison.OrdinalIgnoreCase))
            throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Approval stamps apply to SubInvoices.");
        if (stage is not (ApprovalStages.Checked or ApprovalStages.Approved))
            throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Stage must be {ApprovalStages.Checked} or {ApprovalStages.Approved}.");
        var need = stage == ApprovalStages.Checked ? Permissions.CheckInvoices : Permissions.ApproveInvoices;
        if (!_who.Can(need)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{_who.User} ({_who.Role}) may not give the {stage} stamp.");

        using var c = _ds.OpenConnection();
        using var tx = c.BeginTransaction();
        var p = new PgTx(c, tx);
        var inv = (SubInvoice?)p.Query(typeof(SubInvoice), """SELECT * FROM "SubInvoices" WHERE "Id" = @id FOR UPDATE""", ("id", req.Id)).FirstOrDefault()
                  ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"SubInvoices #{req.Id} no longer exists.");
        if (inv.RowVersion != req.RowVersion) throw Conflict("SubInvoices", inv.Id, inv);
        if (inv.Locked) throw new WriteRejectedException(409, ErrorCodes.Locked, $"{inv.Title} is approved and locked.");
        if (inv.Status != SubInvoiceStatus.Draft)
            throw new WriteRejectedException(409, ErrorCodes.InvalidTransition, $"{inv.Title} is {inv.Status}: only DRAFT invoices are checked / approved.");
        var stamps = p.Query<ApprovalStampDto>("""SELECT * FROM "ApprovalStamps" WHERE "TableName" = 'SubInvoices' AND "RowId" = @id ORDER BY "Id" """, ("id", inv.Id))
            .Where(s => s.RowVersion == inv.RowVersion).ToList();
        if (stage == ApprovalStages.Approved)
        {
            var chk = stamps.LastOrDefault(s => s.Stage == ApprovalStages.Checked)
                      ?? throw new WriteRejectedException(409, ErrorCodes.InvalidTransition, $"{inv.Title} must be CHECKED (on its current version) before it is APPROVED.");
            _ = chk;
        }
        var from = stamps.LastOrDefault()?.Stage ?? SubInvoiceStatus.Draft;
        var id = InvoiceGuard.InsertStamp(p, "SubInvoices", inv.Id, stage, from, stage, inv.RowVersion, _who, Clock(), req.Note ?? "");
        Audit(p, "SubInvoices", inv.Id, stage, $"{inv.Title} {stage.ToLowerInvariant()} by {_who.User}{(string.IsNullOrWhiteSpace(req.Note) ? "" : ": " + req.Note)}", "", null, null);
        BumpVersion(p, "ApprovalStamps");
        tx.Commit();
        Publish(new[] { new ChangeNotice { Table = "SubInvoices", Id = inv.Id, Version = inv.RowVersion, Action = stage, By = _who.User, Machine = _who.Machine, ClientId = _who.ClientId, Summary = $"{inv.Title} {stage}", At = Clock() } });
        return Stamps("SubInvoices", inv.Id).First(s => s.Id == id);
    }
}
