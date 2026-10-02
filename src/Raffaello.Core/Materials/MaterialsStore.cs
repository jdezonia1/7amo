using System.Globalization;
using Microsoft.Data.Sqlite;
using Raffaello.Core.Boq;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Mos;

namespace Raffaello.Core.Materials;

/// <summary>Thrown when a DN line is already invoiced under another supplier invoice (hard lock).</summary>
public sealed class DnLineLockedException : Exception
{
    public IReadOnlyList<MatDnInvoiceLock> Conflicts { get; }
    public DnLineLockedException(IReadOnlyList<MatDnInvoiceLock> conflicts, string message) : base(message) { Conflicts = conflicts; }
}

/// <summary>Everything phase 3 reads, loaded at once (small tables).</summary>
public sealed class MaterialsSnapshot
{
    public List<MatPo> Pos { get; init; } = new();
    public List<MatPoLine> PoLines { get; init; } = new();
    public List<MatPoScope> PoScope { get; init; } = new();
    public List<MatDn> Dns { get; init; } = new();
    public List<MatDnLine> DnLines { get; init; } = new();
    public List<MatMir> Mirs { get; init; } = new();
    public List<MatMirDn> MirDns { get; init; } = new();
    public List<MatMirEvidence> MirEvidence { get; init; } = new();
    public List<MatDnInvoiceLock> Locks { get; init; } = new();
    public List<MatCodeMemory> CodeMemory { get; init; } = new();
    public List<MosValuation> MosValuations { get; init; } = new();
    public List<MosLine> MosLines { get; init; } = new();
    public List<MosInstalled> MosInstalled { get; init; } = new();
    public List<BoqLine> BoqLines { get; init; } = new();
    public List<BoqCatRule> BoqRules { get; init; } = new();

    public IEnumerable<MatPoLine> LinesOf(MatPo po) => PoLines.Where(l => l.PoId == po.Id).OrderBy(l => l.LineNo);
    public IEnumerable<MatDnLine> LinesOf(MatDn dn) => DnLines.Where(l => l.DnId == dn.Id).OrderBy(l => l.Order);
    public MatPo? FindPo(string? poNo) => Pos.FirstOrDefault(p => PoKey(p.PoNo) == PoKey(poNo));

    /// <summary>PO numbers compared without punctuation ("RAF-P.O-E-045-2026" = "RAF-PO-E-045-2026").</summary>
    public static string PoKey(string? po) => new string((po ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Persistence boundary for phase 3 (materials, supplier invoices, owner MOS, BOQ, coding memory). The SQLite implementation keeps its
/// own tables in the shared data file; a server implementation must keep RowVersion concurrency, the audit log and the DN-line hard lock.
/// </summary>
public interface IMaterialsStore
{
    string User { get; }
    void EnsureSchema();
    MaterialsSnapshot Load();
    List<T> All<T>() where T : Entity, new();
    T Insert<T>(T entity, string? summary = null) where T : Entity;
    T Update<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity;
    void Batch(Action<IStoreBatch> work, string summary);

    /// <summary>Saves a PO with its lines (replaces the lines of an existing PO with the same number and supplier).</summary>
    MatPo SavePo(MatPo po, IReadOnlyList<MatPoLine> lines, IReadOnlyList<MatPoScope> scope);
    /// <summary>Saves a DN; refuses to replace a DN that has invoiced lines.</summary>
    MatDn SaveDn(MatDn dn, IReadOnlyList<MatDnLine> lines);
    MatMir SaveMir(MatMir mir, IReadOnlyList<MatMirDn> dns, IReadOnlyList<MatMirEvidence> evidence);
    /// <summary>Locks DN lines to one supplier invoice number. Throws <see cref="DnLineLockedException"/> when a line belongs to another invoice.</summary>
    void LockDnLines(IReadOnlyCollection<long> dnLineIds, string supplier, string poNo, int invoiceNo, long subInvoiceId);
    /// <summary>Releases the locks of a draft invoice that is abandoned.</summary>
    int ReleaseLocks(string supplier, string poNo, int invoiceNo);
    /// <summary>Releases the locks of the given DN lines (used to undo a lock when saving the invoice failed).</summary>
    int ReleaseLines(IReadOnlyCollection<long> dnLineIds);
}

/// <summary>
/// The store-independent part of <see cref="IMaterialsStore"/> (PO / DN / MIR saves, DN-line hard lock): written once against
/// the generic entity primitives, so the SQLite file and the server get the same rules.
/// </summary>
public abstract class MaterialsStoreBase : IMaterialsStore
{
    public static readonly Type[] EntityTypes =
    {
        typeof(MatPo), typeof(MatPoLine), typeof(MatPoScope), typeof(MatDn), typeof(MatDnLine), typeof(MatMir), typeof(MatMirDn), typeof(MatMirEvidence),
        typeof(MatDnInvoiceLock), typeof(MatCodeMemory), typeof(MosValuation), typeof(MosLine), typeof(MosInstalled), typeof(BoqLine), typeof(BoqCatRule),
    };

    public abstract string User { get; }
    public abstract void EnsureSchema();
    public abstract List<T> All<T>() where T : Entity, new();
    public abstract T Insert<T>(T entity, string? summary = null) where T : Entity;
    public abstract T Update<T>(T entity, string? summary = null) where T : Entity, new();
    public abstract void Delete<T>(T entity, string? summary = null) where T : Entity;
    public abstract void Batch(Action<IStoreBatch> work, string summary);
    protected abstract DateTime Now();
    /// <summary>True when a write failed because another user locked the same DN line first (unique index / server guard).</summary>
    protected abstract bool IsLockConflict(Exception ex);

    public MaterialsSnapshot Load() => new()
    {
        Pos = All<MatPo>(), PoLines = All<MatPoLine>(), PoScope = All<MatPoScope>(), Dns = All<MatDn>(), DnLines = All<MatDnLine>(),
        Mirs = All<MatMir>(), MirDns = All<MatMirDn>(), MirEvidence = All<MatMirEvidence>(), Locks = All<MatDnInvoiceLock>(),
        CodeMemory = All<MatCodeMemory>(), MosValuations = All<MosValuation>(), MosLines = All<MosLine>(), MosInstalled = All<MosInstalled>(),
        BoqLines = All<BoqLine>(), BoqRules = All<BoqCatRule>(),
    };

    public MatPo SavePo(MatPo po, IReadOnlyList<MatPoLine> lines, IReadOnlyList<MatPoScope> scope)
    {
                var key = MaterialsSnapshot.PoKey(po.PoNo);
        var existing = All<MatPo>().FirstOrDefault(p => MaterialsSnapshot.PoKey(p.PoNo) == key && string.Equals(p.Supplier, po.Supplier, StringComparison.OrdinalIgnoreCase));
        var oldLines = existing is null ? new List<MatPoLine>() : All<MatPoLine>().Where(l => l.PoId == existing.Id).ToList();
        var oldScope = existing is null ? new List<MatPoScope>() : All<MatPoScope>().Where(l => l.PoId == existing.Id).ToList();
        var dnLinks = existing is null ? new List<MatDnLine>() : All<MatDnLine>().Where(d => d.PoLineId != null && oldLines.Any(o => o.Id == d.PoLineId)).ToList();
        Batch(w =>
        {
            if (existing != null)
            {
                po.Id = existing.Id; po.RowVersion = existing.RowVersion;
                w.Update(po);
                foreach (var l in oldScope) w.Delete(l);
                // keep line ids where the line number survives, so DN matches stay linked
                var byNo = oldLines.GroupBy(l => l.LineNo).ToDictionary(g => g.Key, g => g.First());
                foreach (var l in lines)
                {
                    l.PoId = po.Id;
                    if (byNo.Remove(l.LineNo, out var old)) { l.Id = old.Id; l.RowVersion = old.RowVersion; w.Update(l); }
                    else { l.Id = 0; w.Insert(l); }
                }
                foreach (var gone in byNo.Values)
                {
                    foreach (var d in dnLinks.Where(d => d.PoLineId == gone.Id)) { d.PoLineId = null; d.MatchStatus = ""; w.Update(d); }
                    w.Delete(gone);
                }
            }
            else
            {
                w.Insert(po);
                foreach (var l in lines) { l.Id = 0; l.PoId = po.Id; }
                w.InsertMany(lines);
            }
            foreach (var s in scope) { s.Id = 0; s.PoId = po.Id; }
            w.InsertMany(scope);
        }, $"PO {po.PoNo} ({po.Supplier}) saved: {lines.Count} lines, SAR {lines.Sum(l => l.Amount):N2}");
        return po;
    }

    public MatDn SaveDn(MatDn dn, IReadOnlyList<MatDnLine> lines)
    {
                var existing = All<MatDn>().FirstOrDefault(d => d.DnNo.Equals(dn.DnNo, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Supplier, dn.Supplier, StringComparison.OrdinalIgnoreCase));
        var oldLines = existing is null ? new List<MatDnLine>() : All<MatDnLine>().Where(l => l.DnId == existing.Id).ToList();
        if (oldLines.Count > 0)
        {
            var locked = All<MatDnInvoiceLock>().Where(k => oldLines.Any(o => o.Id == k.DnLineId)).ToList();
            if (locked.Count > 0)
                throw new DnLineLockedException(locked, $"DN {dn.DnNo} has {locked.Count} line(s) already invoiced (INV-{locked[0].InvoiceNo:00}); it cannot be re-imported.");
        }
        Batch(w =>
        {
            if (existing != null)
            {
                dn.Id = existing.Id; dn.RowVersion = existing.RowVersion;
                w.Update(dn);
                foreach (var l in oldLines) w.Delete(l);
            }
            else w.Insert(dn);
            foreach (var l in lines) { l.Id = 0; l.DnId = dn.Id; }
            w.InsertMany(lines);
        }, $"DN {dn.DnNo} ({dn.Supplier}, PO {dn.PoNo}) saved: {lines.Count} lines");
        return dn;
    }

    public MatMir SaveMir(MatMir mir, IReadOnlyList<MatMirDn> dns, IReadOnlyList<MatMirEvidence> evidence)
    {
                var existing = All<MatMir>().FirstOrDefault(m => m.MirNo.Equals(mir.MirNo, StringComparison.OrdinalIgnoreCase) && m.Revision == mir.Revision);
        var oldDns = existing is null ? new List<MatMirDn>() : All<MatMirDn>().Where(x => x.MirId == existing.Id).ToList();
        var oldEv = existing is null ? new List<MatMirEvidence>() : All<MatMirEvidence>().Where(x => x.MirId == existing.Id).ToList();
        Batch(w =>
        {
            if (existing != null)
            {
                mir.Id = existing.Id; mir.RowVersion = existing.RowVersion;
                w.Update(mir);
                foreach (var x in oldDns) w.Delete(x);
                foreach (var x in oldEv) w.Delete(x);
            }
            else w.Insert(mir);
            foreach (var x in dns) { x.Id = 0; x.MirId = mir.Id; }
            foreach (var x in evidence) { x.Id = 0; x.MirId = mir.Id; }
            w.InsertMany(dns);
            w.InsertMany(evidence);
        }, $"MIR {mir.MirNo} Rev {mir.Revision} saved: {dns.Count} DN refs, {evidence.Count} evidence rows");
        return mir;
    }

    public void LockDnLines(IReadOnlyCollection<long> dnLineIds, string supplier, string poNo, int invoiceNo, long subInvoiceId)
    {
                var ids = dnLineIds.Distinct().ToList();
        var existing = All<MatDnInvoiceLock>().Where(k => ids.Contains(k.DnLineId)).ToList();
        bool Same(MatDnInvoiceLock k) => k.InvoiceNo == invoiceNo && string.Equals(k.Supplier, supplier, StringComparison.OrdinalIgnoreCase)
                                         && MaterialsSnapshot.PoKey(k.PoNo) == MaterialsSnapshot.PoKey(poNo);
        var conflicts = existing.Where(k => !Same(k)).ToList();
        if (conflicts.Count > 0)
            throw new DnLineLockedException(conflicts, $"{conflicts.Count} DN line(s) are already invoiced under {string.Join(", ", conflicts.Select(k => $"{k.Supplier} {k.PoNo} INV-{k.InvoiceNo:00}").Distinct())}. A DN line can be invoiced once.");
        var have = existing.Select(k => k.DnLineId).ToHashSet();
        var now = Now();
        try
        {
            Batch(w =>
            {
                foreach (var k in existing.Where(k => k.SubInvoiceId != subInvoiceId)) { k.SubInvoiceId = subInvoiceId; w.Update(k); }
                w.InsertMany(ids.Where(i => !have.Contains(i)).Select(i => new MatDnInvoiceLock
                { DnLineId = i, Supplier = supplier, PoNo = poNo, InvoiceNo = invoiceNo, SubInvoiceId = subInvoiceId, LockedAt = now }));
            }, $"{supplier} {poNo} INV-{invoiceNo:00}: {ids.Count - have.Count} DN line(s) locked");
        }
        catch (Exception ex) when (IsLockConflict(ex))
        {
            // another user locked the same line between our read and write: the unique index is the hard lock
            var now2 = All<MatDnInvoiceLock>().Where(k => ids.Contains(k.DnLineId) && !Same(k)).ToList();
            throw new DnLineLockedException(now2, "Another user invoiced some of these DN lines a moment ago. Reload and try again.");
        }
    }

    public int ReleaseLocks(string supplier, string poNo, int invoiceNo)
    {
                var mine = All<MatDnInvoiceLock>().Where(k => k.InvoiceNo == invoiceNo && string.Equals(k.Supplier, supplier, StringComparison.OrdinalIgnoreCase)
                                                         && MaterialsSnapshot.PoKey(k.PoNo) == MaterialsSnapshot.PoKey(poNo)).ToList();
        if (mine.Count == 0) return 0;
        Batch(w => { foreach (var k in mine) w.Delete(k); }, $"{supplier} {poNo} INV-{invoiceNo:00}: {mine.Count} DN line lock(s) released");
        return mine.Count;
    }

    public int ReleaseLines(IReadOnlyCollection<long> dnLineIds)
    {
                var set = dnLineIds.ToHashSet();
        var mine = All<MatDnInvoiceLock>().Where(k => set.Contains(k.DnLineId)).ToList();
        if (mine.Count == 0) return 0;
        Batch(w => { foreach (var k in mine) w.Delete(k); }, $"{mine.Count} DN line lock(s) released");
        return mine.Count;
    }

}

/// <summary>SQLite implementation in the same data file as <see cref="Db"/> (WAL, busy timeout, audit log shared).</summary>
public sealed class SqliteMaterialsStore : MaterialsStoreBase
{
    public new static readonly Type[] EntityTypes = MaterialsStoreBase.EntityTypes;

    private readonly Func<Db> _db;
    private bool _schemaReady;
    private string? _schemaPath;

    public SqliteMaterialsStore(Func<Db> db) => _db = db;
    public SqliteMaterialsStore(Db db) : this(() => db) { }

    private Db Db
    {
        get
        {
            var db = _db();
            if (!_schemaReady || _schemaPath != db.Path) { EnsureSchema(db); _schemaReady = true; _schemaPath = db.Path; }
            return db;
        }
    }

    public override string User => _db().User;

    public override void EnsureSchema() { EnsureSchema(_db()); _schemaReady = true; _schemaPath = _db().Path; }

    private static void EnsureSchema(Db db)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Exec(c, tx, @"CREATE TABLE IF NOT EXISTS AuditLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, User TEXT, Machine TEXT,
            TableName TEXT, RowId INTEGER, Action TEXT, Summary TEXT, Changes TEXT);");
        foreach (var t in EntityTypes) EnsureTable(c, tx, t);
        Exec(c, tx, "CREATE UNIQUE INDEX IF NOT EXISTS UX_MatDnInvoiceLocks_Line ON MatDnInvoiceLocks(DnLineId);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_MatDnLines_Dn ON MatDnLines(DnId);");
        Exec(c, tx, "CREATE INDEX IF NOT EXISTS IX_MatPoLines_Po ON MatPoLines(PoId);");
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

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public override List<T> All<T>() => Db.All<T>();
    public override T Insert<T>(T entity, string? summary = null) => Db.Insert(entity, summary);
    public override T Update<T>(T entity, string? summary = null) => Db.Update(entity, summary);
    public override void Delete<T>(T entity, string? summary = null) => Db.Delete(entity, summary);
    public override void Batch(Action<IStoreBatch> work, string summary) => Db.Batch(work, summary);
    protected override DateTime Now() => Db.Clock();
    protected override bool IsLockConflict(Exception ex) => ex is SqliteException { SqliteErrorCode: 19 };

    internal static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);
}
