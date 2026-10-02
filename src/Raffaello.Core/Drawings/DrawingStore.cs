using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Persistence of the drawings module: sheets, symbol library, linear classes, takeoffs, calibrations, rooms, decisions, checks.</summary>
public interface IDrawingStore
{
    string User { get; }
    Func<DateTime> Clock { get; }
    void EnsureSchema();

    List<DwgSheet> Sheets();
    DwgSheet? Sheet(long id);
    DwgSheet SaveSheet(DwgSheet s);
    void DeleteSheet(DwgSheet s);

    List<DwgSymbol> Symbols();
    DwgSymbol SaveSymbol(DwgSymbol s);
    void DeleteSymbol(DwgSymbol s);

    List<DwgLinearClass> LinearClasses();
    DwgLinearClass SaveLinearClass(DwgLinearClass c);
    void DeleteLinearClass(DwgLinearClass c);

    List<DwgTakeoff> Takeoffs(long sheetId);
    List<DwgHit> Hits(long takeoffId);
    List<DwgRun> Runs(long takeoffId);
    /// <summary>Stores a new takeoff run with its hits and runs (one transaction).</summary>
    DwgTakeoff SaveTakeoff(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs);
    /// <summary>Review edits: inserts new hits (Id 0), updates changed ones; updates the run's counters.</summary>
    void SaveReview(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs);

    DwgCalibration? Calibration(long sheetId);
    DwgCalibration SaveCalibration(DwgCalibration c);

    List<DwgRoom> Rooms(long sheetId);
    void ReplaceRooms(long sheetId, IReadOnlyList<DwgRoom> rooms, string summary);

    List<DwgQtyDecision> Decisions();
    void AddDecisions(IReadOnlyList<DwgQtyDecision> d);

    List<DwgStatementCheck> StatementChecks();
    List<DwgStatementLine> StatementLines(long checkId);
    DwgStatementCheck SaveStatementCheck(DwgStatementCheck c, IReadOnlyList<DwgStatementLine> lines);

    List<DwgRevisionCompare> Compares();
    List<DwgRevisionDelta> Deltas(long compareId);
    DwgRevisionCompare SaveCompare(DwgRevisionCompare c, IReadOnlyList<DwgRevisionDelta> deltas);
    DwgRevisionCompare UpdateCompare(DwgRevisionCompare c);
}

/// <summary>[drawings] Shared rules of the SQLite and server stores (same behaviour whichever data source is active).</summary>
public abstract class DrawingStoreBase : IDrawingStore
{
    public abstract string User { get; }
    public abstract Func<DateTime> Clock { get; }
    public abstract void EnsureSchema();
    protected abstract List<T> AllRows<T>() where T : Entity, new();
    protected virtual List<T> RowsOf<T>(Func<T, bool> where, string column, long id) where T : Entity, new() => AllRows<T>().Where(where).ToList();
    protected abstract T InsertRow<T>(T e, string summary) where T : Entity;
    protected abstract T UpdateRow<T>(T e, string summary) where T : Entity, new();
    protected abstract void DeleteRow<T>(T e, string summary) where T : Entity;
    protected abstract void Batch(Action<IStoreBatch> work, string summary);

    public List<DwgSheet> Sheets() => AllRows<DwgSheet>().OrderBy(s => s.Building).ThenBy(s => s.Level).ThenBy(s => s.SheetNo).ThenBy(s => s.Revision).ToList();
    public DwgSheet? Sheet(long id) => AllRows<DwgSheet>().FirstOrDefault(s => s.Id == id);

    public DwgSheet SaveSheet(DwgSheet s)
    {
        if (s.ImportedAt == default) s.ImportedAt = Clock();
        return s.Id > 0 ? UpdateRow(s, $"Drawing sheet {s.Label} changed") : InsertRow(s, $"Drawing sheet {s.Label} imported ({s.FileName})");
    }

    public void DeleteSheet(DwgSheet s) => Batch(b =>
    {
        foreach (var t in RowsOf<DwgTakeoff>(x => x.SheetId == s.Id, nameof(DwgTakeoff.SheetId), s.Id))
        {
            foreach (var h in RowsOf<DwgHit>(x => x.TakeoffId == t.Id, nameof(DwgHit.TakeoffId), t.Id)) b.Delete(h);
            foreach (var r in RowsOf<DwgRun>(x => x.TakeoffId == t.Id, nameof(DwgRun.TakeoffId), t.Id)) b.Delete(r);
            b.Delete(t);
        }
        foreach (var r in RowsOf<DwgRoom>(x => x.SheetId == s.Id, nameof(DwgRoom.SheetId), s.Id)) b.Delete(r);
        foreach (var c in RowsOf<DwgCalibration>(x => x.SheetId == s.Id, nameof(DwgCalibration.SheetId), s.Id)) b.Delete(c);
        b.Delete(s);
    }, $"Drawing sheet {s.Label} deleted with its takeoffs");

    public List<DwgSymbol> Symbols() => AllRows<DwgSymbol>().OrderBy(s => s.System).ThenBy(s => s.Name).ToList();

    public DwgSymbol SaveSymbol(DwgSymbol s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new InvalidOperationException("Give the symbol a name.");
        s.Name = s.Name.Trim().ToUpperInvariant();
        if (AllRows<DwgSymbol>().Any(x => x.Id != s.Id && x.Name == s.Name && x.Building.Equals(s.Building, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A symbol named {s.Name} already exists - pick another name or edit that one.");
        return s.Id > 0 ? UpdateRow(s, $"Symbol {s.Name} changed") : InsertRow(s, $"Symbol {s.Name} added to the library ({s.System})");
    }

    public void DeleteSymbol(DwgSymbol s) => DeleteRow(s, $"Symbol {s.Name} removed from the library");

    public List<DwgLinearClass> LinearClasses() => AllRows<DwgLinearClass>().OrderBy(c => c.System).ThenBy(c => c.Name).ToList();

    public DwgLinearClass SaveLinearClass(DwgLinearClass c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidOperationException("Give the linear class a name (e.g. TRAY 300).");
        c.Name = c.Name.Trim().ToUpperInvariant();
        return c.Id > 0 ? UpdateRow(c, $"Linear class {c.Name} changed") : InsertRow(c, $"Linear class {c.Name} added");
    }

    public void DeleteLinearClass(DwgLinearClass c) => DeleteRow(c, $"Linear class {c.Name} removed");

    public List<DwgTakeoff> Takeoffs(long sheetId) => RowsOf<DwgTakeoff>(t => t.SheetId == sheetId, nameof(DwgTakeoff.SheetId), sheetId).OrderByDescending(t => t.RunAt).ThenByDescending(t => t.Id).ToList();
    public List<DwgHit> Hits(long takeoffId) => RowsOf<DwgHit>(h => h.TakeoffId == takeoffId, nameof(DwgHit.TakeoffId), takeoffId).OrderBy(h => h.Id).ToList();
    public List<DwgRun> Runs(long takeoffId) => RowsOf<DwgRun>(r => r.TakeoffId == takeoffId, nameof(DwgRun.TakeoffId), takeoffId).OrderBy(r => r.Id).ToList();

    public DwgTakeoff SaveTakeoff(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs)
    {
        if (t.RunAt == default) t.RunAt = Clock();
        t.Hits = hits.Count(h => DwgHitStatus.Counts(h.Status));
        t.Runs = runs.Count;
        Batch(b =>
        {
            b.Insert(t);
            foreach (var h in hits) { h.TakeoffId = t.Id; b.Insert(h); }
            foreach (var r in runs) { r.TakeoffId = t.Id; b.Insert(r); }
        }, $"Takeoff on sheet #{t.SheetId} ({t.Source}): {t.Hits} symbols, {t.Runs} runs");
        return t;
    }

    public void SaveReview(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs)
    {
        t.Hits = hits.Count(h => DwgHitStatus.Counts(h.Status));
        t.Runs = runs.Count(r => DwgHitStatus.Counts(r.Status));
        if (t.Status == "DRAFT") t.Status = "REVIEWED";
        Batch(b =>
        {
            b.Update(t);
            foreach (var h in hits) { h.TakeoffId = t.Id; if (h.Id > 0) b.Update(h); else b.Insert(h); }
            foreach (var r in runs) { r.TakeoffId = t.Id; if (r.Id > 0) b.Update(r); else b.Insert(r); }
        }, $"Takeoff #{t.Id} reviewed: {t.Hits} symbols counted, {hits.Count(h => h.Status == DwgHitStatus.Removed)} removed, {hits.Count(h => h.Status == DwgHitStatus.Added)} added");
    }

    public DwgCalibration? Calibration(long sheetId) => RowsOf<DwgCalibration>(c => c.SheetId == sheetId, nameof(DwgCalibration.SheetId), sheetId).OrderByDescending(c => c.Id).FirstOrDefault();

    public DwgCalibration SaveCalibration(DwgCalibration c)
    {
        if (c.At == default) c.At = Clock();
        return c.Id > 0 ? UpdateRow(c, $"Calibration of sheet #{c.SheetId} changed") : InsertRow(c, $"Sheet #{c.SheetId} calibrated to plan {c.Plan} (residual {c.Residual:0.0})");
    }

    public List<DwgRoom> Rooms(long sheetId) => RowsOf<DwgRoom>(r => r.SheetId == sheetId, nameof(DwgRoom.SheetId), sheetId).OrderBy(r => r.Room).ToList();

    public void ReplaceRooms(long sheetId, IReadOnlyList<DwgRoom> rooms, string summary) => Batch(b =>
    {
        foreach (var r in RowsOf<DwgRoom>(x => x.SheetId == sheetId, nameof(DwgRoom.SheetId), sheetId)) b.Delete(r);
        foreach (var r in rooms) { r.SheetId = sheetId; b.Insert(r); }
    }, summary);

    public List<DwgQtyDecision> Decisions() => AllRows<DwgQtyDecision>().OrderByDescending(d => d.DecidedAt).ToList();

    public void AddDecisions(IReadOnlyList<DwgQtyDecision> d) =>
        Batch(b => { foreach (var x in d) b.Insert(x); }, $"PROJECT QTY proposals: {d.Count(x => x.Decision == "ACCEPTED")} accepted, {d.Count(x => x.Decision != "ACCEPTED")} rejected");

    public List<DwgStatementCheck> StatementChecks() => AllRows<DwgStatementCheck>().OrderByDescending(c => c.RunAt).ToList();
    public List<DwgStatementLine> StatementLines(long checkId) => RowsOf<DwgStatementLine>(l => l.CheckId == checkId, nameof(DwgStatementLine.CheckId), checkId).OrderBy(l => l.Room).ThenBy(l => l.Stage).ThenBy(l => l.Item).ToList();

    public DwgStatementCheck SaveStatementCheck(DwgStatementCheck c, IReadOnlyList<DwgStatementLine> lines)
    {
        if (c.RunAt == default) c.RunAt = Clock();
        Batch(b =>
        {
            b.Insert(c);
            foreach (var l in lines) { l.CheckId = c.Id; b.Insert(l); }
        }, $"Statement {c.Subcontractor} {c.StatementNo} verified: {c.Flags} differences");
        return c;
    }

    public List<DwgRevisionCompare> Compares() => AllRows<DwgRevisionCompare>().OrderByDescending(c => c.RunAt).ToList();
    public List<DwgRevisionDelta> Deltas(long compareId) => RowsOf<DwgRevisionDelta>(d => d.CompareId == compareId, nameof(DwgRevisionDelta.CompareId), compareId).OrderBy(d => d.Room).ToList();

    public DwgRevisionCompare SaveCompare(DwgRevisionCompare c, IReadOnlyList<DwgRevisionDelta> deltas)
    {
        if (c.RunAt == default) c.RunAt = Clock();
        Batch(b =>
        {
            b.Insert(c);
            foreach (var d in deltas) { d.CompareId = c.Id; b.Insert(d); }
        }, $"Revision compare sheet #{c.OldSheetId} -> #{c.NewSheetId}: +{c.AddedSymbols} / -{c.RemovedSymbols} symbols");
        return c;
    }

    public DwgRevisionCompare UpdateCompare(DwgRevisionCompare c) => UpdateRow(c, $"Revision compare #{c.Id} changed");
}

/// <summary>[drawings] Drawings tables in the shared SQLite data file (same file, pragmas, audit log and concurrency as the other modules).</summary>
public sealed class SqliteDrawingStore : DrawingStoreBase
{
    private sealed class Side : SideStore
    {
        public Side(string path, string user, string? machine) : base(path, user, machine) { }
        protected override IEnumerable<Type> Tables => DrawingEntities.All;
        protected override IEnumerable<string> Indexes => new[]
        {
            "CREATE INDEX IF NOT EXISTS IX_DwgHits_Takeoff ON DwgHits(TakeoffId);",
            "CREATE INDEX IF NOT EXISTS IX_DwgRuns_Takeoff ON DwgRuns(TakeoffId);",
            "CREATE INDEX IF NOT EXISTS IX_DwgTakeoffs_Sheet ON DwgTakeoffs(SheetId);",
            "CREATE INDEX IF NOT EXISTS IX_DwgRooms_Sheet ON DwgRooms(SheetId);",
            "CREATE INDEX IF NOT EXISTS IX_DwgStatementLines_Check ON DwgStatementLines(CheckId);",
            "CREATE INDEX IF NOT EXISTS IX_DwgRevisionDeltas_Compare ON DwgRevisionDeltas(CompareId);",
        };
    }

    private sealed class BatchAdapter : IStoreBatch
    {
        private readonly SideStore.SideBatch _b;
        public BatchAdapter(SideStore.SideBatch b) => _b = b;
        public T Insert<T>(T entity) where T : Entity => _b.Insert(entity);
        public int InsertMany<T>(IEnumerable<T> entities) where T : Entity { var n = 0; foreach (var e in entities) { _b.Insert(e); n++; } return n; }
        public void Update<T>(T entity) where T : Entity, new() => _b.Update(entity);
        public void Delete<T>(T entity) where T : Entity => _b.Delete(entity);
    }

    private readonly Side _s;

    public SqliteDrawingStore(string path, string user, string? machine = null) => _s = new Side(path, user, machine);

    public override string User => _s.User;
    public override Func<DateTime> Clock => _s.Clock;
    public Func<DateTime> ClockSetter { set => _s.Clock = value; }
    public override void EnsureSchema() => _s.EnsureSchema();
    protected override List<T> AllRows<T>() => _s.All<T>();
    protected override List<T> RowsOf<T>(Func<T, bool> where, string column, long id) => _s.All<T>($"[{column}]=@V", new { V = id });
    protected override T InsertRow<T>(T e, string summary) => _s.Insert(e, summary);
    protected override T UpdateRow<T>(T e, string summary) => _s.Update(e, summary);
    protected override void DeleteRow<T>(T e, string summary) => _s.Delete(e, summary);
    protected override void Batch(Action<IStoreBatch> work, string summary) => _s.Batch(b => work(new BatchAdapter(b)), summary);
}

/// <summary>[drawings] Drawings tables on the Raffaello server (offline queue, conflicts, audit and live notices come with it).</summary>
public sealed class RemoteDrawingStore : DrawingStoreBase
{
    private readonly RemoteProjectStore _r;

    static RemoteDrawingStore() { foreach (var t in DrawingEntities.All) EntityMeta.Register(t); }

    public RemoteDrawingStore(RemoteProjectStore r) => _r = r;

    public override string User => _r.User;
    public override Func<DateTime> Clock => _r.Clock;
    public override void EnsureSchema() { }   // the server creates the tables (DrawingsServerModule)
    protected override List<T> AllRows<T>() => _r.All<T>();
    protected override T InsertRow<T>(T e, string summary) => _r.Insert(e, summary);
    protected override T UpdateRow<T>(T e, string summary) => _r.Update(e, summary);
    protected override void DeleteRow<T>(T e, string summary) => _r.Delete(e, summary);
    protected override void Batch(Action<IStoreBatch> work, string summary) => _r.Batch(work, summary);
}

/// <summary>[drawings] Picks the store for the current data source on every call (local data file or the server).</summary>
public sealed class DrawingStoreSelector : IDrawingStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private IDrawingStore? _impl;

    public DrawingStoreSelector(Func<IProjectStore> store) => _store = store;

    private IDrawingStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null)
            {
                _impl = s switch
                {
                    RemoteProjectStore r => new RemoteDrawingStore(r),
                    Db db => new SqliteDrawingStore(db.Path, db.User, db.Machine),
                    _ => throw new InvalidOperationException($"Drawings are not available for the data source {s.GetType().Name}."),
                };
                _impl.EnsureSchema();
                _for = s;
            }
            return _impl;
        }
    }

    public string User => Impl.User;
    public Func<DateTime> Clock => Impl.Clock;
    public void EnsureSchema() => Impl.EnsureSchema();
    public List<DwgSheet> Sheets() => Impl.Sheets();
    public DwgSheet? Sheet(long id) => Impl.Sheet(id);
    public DwgSheet SaveSheet(DwgSheet s) => Impl.SaveSheet(s);
    public void DeleteSheet(DwgSheet s) => Impl.DeleteSheet(s);
    public List<DwgSymbol> Symbols() => Impl.Symbols();
    public DwgSymbol SaveSymbol(DwgSymbol s) => Impl.SaveSymbol(s);
    public void DeleteSymbol(DwgSymbol s) => Impl.DeleteSymbol(s);
    public List<DwgLinearClass> LinearClasses() => Impl.LinearClasses();
    public DwgLinearClass SaveLinearClass(DwgLinearClass c) => Impl.SaveLinearClass(c);
    public void DeleteLinearClass(DwgLinearClass c) => Impl.DeleteLinearClass(c);
    public List<DwgTakeoff> Takeoffs(long sheetId) => Impl.Takeoffs(sheetId);
    public List<DwgHit> Hits(long takeoffId) => Impl.Hits(takeoffId);
    public List<DwgRun> Runs(long takeoffId) => Impl.Runs(takeoffId);
    public DwgTakeoff SaveTakeoff(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs) => Impl.SaveTakeoff(t, hits, runs);
    public void SaveReview(DwgTakeoff t, IReadOnlyList<DwgHit> hits, IReadOnlyList<DwgRun> runs) => Impl.SaveReview(t, hits, runs);
    public DwgCalibration? Calibration(long sheetId) => Impl.Calibration(sheetId);
    public DwgCalibration SaveCalibration(DwgCalibration c) => Impl.SaveCalibration(c);
    public List<DwgRoom> Rooms(long sheetId) => Impl.Rooms(sheetId);
    public void ReplaceRooms(long sheetId, IReadOnlyList<DwgRoom> rooms, string summary) => Impl.ReplaceRooms(sheetId, rooms, summary);
    public List<DwgQtyDecision> Decisions() => Impl.Decisions();
    public void AddDecisions(IReadOnlyList<DwgQtyDecision> d) => Impl.AddDecisions(d);
    public List<DwgStatementCheck> StatementChecks() => Impl.StatementChecks();
    public List<DwgStatementLine> StatementLines(long checkId) => Impl.StatementLines(checkId);
    public DwgStatementCheck SaveStatementCheck(DwgStatementCheck c, IReadOnlyList<DwgStatementLine> lines) => Impl.SaveStatementCheck(c, lines);
    public List<DwgRevisionCompare> Compares() => Impl.Compares();
    public List<DwgRevisionDelta> Deltas(long compareId) => Impl.Deltas(compareId);
    public DwgRevisionCompare SaveCompare(DwgRevisionCompare c, IReadOnlyList<DwgRevisionDelta> deltas) => Impl.SaveCompare(c, deltas);
    public DwgRevisionCompare UpdateCompare(DwgRevisionCompare c) => Impl.UpdateCompare(c);
}
