using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Cables;

/// <summary>Everything the cables module reads, loaded at once (small tables).</summary>
public sealed class CableSnapshot
{
    public List<CablePanel> Panels { get; init; } = new();
    public List<CablePanelAlias> Aliases { get; init; } = new();
    public List<CableRun> Runs { get; init; } = new();
    public List<CableClaim> Claims { get; init; } = new();
    public List<CableFlagDecision> Decisions { get; init; } = new();
    public List<CableImportProfile> Profiles { get; init; } = new();

    public PanelResolver Resolver() => new(Panels, Aliases);
    public CableRun? Run(long? id) => id is null ? null : Runs.FirstOrDefault(r => r.Id == id);
}

/// <summary>
/// [cables] Persistence boundary of the cables module. SQLite: own tables in the shared data file through <see cref="SideStore"/>
/// (same pragmas, RowVersion concurrency, AuditLog row per change); server: <see cref="RemoteCableStore"/> (offline queue, conflicts,
/// audit, live notices via the generic entity API). Business rules live in <see cref="CableService"/>, written once for both.
/// </summary>
public interface ICableStore
{
    string User { get; }
    void EnsureSchema();
    CableSnapshot Load();
    List<T> All<T>() where T : Entity, new();
    T Insert<T>(T entity, string? summary = null) where T : Entity;
    T Update<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity;
    /// <summary>Several writes in one transaction with one audit row.</summary>
    void Batch(Action<IStoreBatch> work, string summary);
}

public static class CableStore
{
    public static readonly Type[] EntityTypes =
    {
        typeof(CablePanel), typeof(CablePanelAlias), typeof(CableRun), typeof(CableClaim), typeof(CableFlagDecision), typeof(CableImportProfile),
    };

    public static CableSnapshot Load(ICableStore s) => new()
    {
        Panels = s.All<CablePanel>(), Aliases = s.All<CablePanelAlias>(), Runs = s.All<CableRun>(), Claims = s.All<CableClaim>(),
        Decisions = s.All<CableFlagDecision>(), Profiles = s.All<CableImportProfile>(),
    };

    private static bool _registered;
    /// <summary>Makes the server client, the offline cache and the local-to-server migration know the cable tables.</summary>
    public static void Register()
    {
        if (_registered) return;
        foreach (var t in EntityTypes) EntityMeta.Register(t);
        _registered = true;
    }

    /// <summary>The cable store for a project store: SQLite side tables in the same data file, or the server.</summary>
    public static ICableStore For(IProjectStore store) => store switch
    {
        RemoteProjectStore r => new RemoteCableStore(r),
        Db db => new SqliteCableStore(db.Path, db.User, db.Machine) { Clock = db.Clock },
        _ => throw new InvalidOperationException($"Cables are not available for the data source {store.GetType().Name}."),
    };
}

/// <summary>SQLite implementation: the cable tables live in the shared data file (WAL, busy timeout, shared AuditLog).</summary>
public sealed class SqliteCableStore : ICableStore
{
    private sealed class Side : SideStore
    {
        public Side(string path, string user, string? machine) : base(path, user, machine) { }
        protected override IEnumerable<Type> Tables => CableStore.EntityTypes;
        protected override IEnumerable<string> Indexes => new[]
        {
            "CREATE INDEX IF NOT EXISTS IX_CableClaims_Run ON CableClaims(RunId);",
            "CREATE INDEX IF NOT EXISTS IX_CableClaims_Source ON CableClaims(SourceKey);",
            "CREATE INDEX IF NOT EXISTS IX_CableRuns_From ON CableRuns(FromKey, ToKey);",
            "CREATE INDEX IF NOT EXISTS IX_CablePanels_Key ON CablePanels([Key]);",
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

    private readonly Side _side;
    private bool _ready;

    public SqliteCableStore(string path, string user, string? machine = null) => _side = new Side(path, user, machine);

    public string Path => _side.Path;
    public string User => _side.User;
    public Func<DateTime> Clock { get => _side.Clock; set => _side.Clock = value; }

    public void EnsureSchema() { _side.EnsureSchema(); _ready = true; }
    private SideStore S { get { if (!_ready) EnsureSchema(); return _side; } }

    public CableSnapshot Load() => CableStore.Load(this);
    public List<T> All<T>() where T : Entity, new() => S.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => S.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => S.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => S.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => S.Batch(b => work(new BatchAdapter(b)), summary);
}

/// <summary>Cables on the Raffaello server (generic entity API: RowVersion checks, audit, offline queue, live notices).</summary>
public sealed class RemoteCableStore : ICableStore
{
    private readonly RemoteProjectStore _r;
    static RemoteCableStore() => CableStore.Register();
    public RemoteCableStore(RemoteProjectStore r) => _r = r;

    public string User => _r.User;
    public void EnsureSchema() { }   // the server creates the tables (CablesServerModule)
    public CableSnapshot Load() => CableStore.Load(this);
    public List<T> All<T>() where T : Entity, new() => _r.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => _r.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => _r.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => _r.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => _r.Batch(work, summary);
}

/// <summary>Picks the cable store for the current data source on every call (the user can switch local / server in Settings).</summary>
public sealed class CableStoreSelector : ICableStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private ICableStore? _impl;

    public CableStoreSelector(Func<IProjectStore> store) => _store = store;

    private ICableStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null) { _impl = CableStore.For(s); _for = s; }
            return _impl;
        }
    }

    public string User => Impl.User;
    public void EnsureSchema() => Impl.EnsureSchema();
    public CableSnapshot Load() => Impl.Load();
    public List<T> All<T>() where T : Entity, new() => Impl.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => Impl.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => Impl.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => Impl.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => Impl.Batch(work, summary);
}
