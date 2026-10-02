using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Assistant;

/// <summary>
/// Persistence of the assistant: conversations and their messages (per user), proposed actions, reminders, notification rules / log
/// and brief snapshots. Same data file as the project (own tables) or the Raffaello server (module tables), chosen per call.
/// </summary>
public interface IAssistantStore
{
    string User { get; }
    void EnsureSchema();
    List<T> All<T>() where T : Entity, new();
    T Insert<T>(T entity, string? summary = null) where T : Entity;
    T Update<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity;
    void Batch(Action<IStoreBatch> work, string summary);
}

public static class AssistantStoreExtensions
{
    public static List<AssistantConversation> Conversations(this IAssistantStore s, string owner) =>
        s.All<AssistantConversation>().Where(c => string.Equals(c.Owner, owner, StringComparison.OrdinalIgnoreCase) && !c.Archived)
            .OrderByDescending(c => c.LastAt).ThenByDescending(c => c.Id).ToList();

    public static List<AssistantMessage> Messages(this IAssistantStore s, long conversationId) =>
        s.All<AssistantMessage>().Where(m => m.ConversationId == conversationId).OrderBy(m => m.Seq).ThenBy(m => m.Id).ToList();

    public static List<AssistantAction> Actions(this IAssistantStore s, long conversationId) =>
        s.All<AssistantAction>().Where(a => a.ConversationId == conversationId).OrderBy(a => a.Id).ToList();

    public static List<AssistantReminder> Reminders(this IAssistantStore s, string? owner = null, bool openOnly = true) =>
        s.All<AssistantReminder>().Where(r => (owner is null || string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)) && (!openOnly || !r.Done))
            .OrderBy(r => r.Due).ToList();

    public static List<NotificationRule> Rules(this IAssistantStore s, string owner) =>
        s.All<NotificationRule>().Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Id).ToList();

    public static BriefSnapshot? LastBrief(this IAssistantStore s, string owner, DateTime before) =>
        s.All<BriefSnapshot>().Where(b => string.Equals(b.Owner, owner, StringComparison.OrdinalIgnoreCase) && b.Date.Date < before.Date)
            .OrderByDescending(b => b.Date).ThenByDescending(b => b.Id).FirstOrDefault();
}

/// <summary>SQLite: the assistant tables in the shared data file (WAL, RowVersion, audit row per change).</summary>
public sealed class SqliteAssistantStore : SideStore, IAssistantStore
{
    public SqliteAssistantStore(string path, string user, string? machine = null) : base(path, user, machine) { }

    protected override IEnumerable<Type> Tables => AssistantEntityTypes.All;

    protected override IEnumerable<string> Indexes => new[]
    {
        "CREATE INDEX IF NOT EXISTS IX_AssistantMessages_Conv ON AssistantMessages(ConversationId, Seq);",
        "CREATE INDEX IF NOT EXISTS IX_AssistantActions_Conv ON AssistantActions(ConversationId);",
        "CREATE INDEX IF NOT EXISTS IX_NotificationLogs_Key ON NotificationLogs(Owner, EventKey, Channel);",
    };

    List<T> IAssistantStore.All<T>() => All<T>();

    public void Batch(Action<IStoreBatch> work, string summary) => Batch(b => work(new Adapter(b)), summary);

    private sealed class Adapter : IStoreBatch
    {
        private readonly SideBatch _b;
        public Adapter(SideBatch b) => _b = b;
        public T Insert<T>(T entity) where T : Entity => _b.Insert(entity);
        public int InsertMany<T>(IEnumerable<T> entities) where T : Entity { var n = 0; foreach (var e in entities) { _b.Insert(e); n++; } return n; }
        public void Update<T>(T entity) where T : Entity, new() => _b.Update(entity);
        public void Delete<T>(T entity) where T : Entity => _b.Delete(entity);
    }
}

/// <summary>Server: the assistant tables are module tables (AssistantServerModule); offline queue and conflicts come with the remote store.</summary>
public sealed class RemoteAssistantStore : IAssistantStore
{
    private readonly RemoteProjectStore _r;
    static RemoteAssistantStore() { foreach (var t in AssistantEntityTypes.All) EntityMeta.Register(t); }
    public RemoteAssistantStore(RemoteProjectStore r) => _r = r;

    public string User => _r.User;
    public void EnsureSchema() { }
    public List<T> All<T>() where T : Entity, new() => _r.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => _r.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => _r.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => _r.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => _r.Batch(work, summary);
}

/// <summary>Over any project store that knows the assistant tables (the server's PostgreSQL store in the scheduled briefs).</summary>
public sealed class ProjectStoreAssistantStore : IAssistantStore
{
    private readonly IProjectStore _s;
    public ProjectStoreAssistantStore(IProjectStore s) => _s = s;
    public string User => _s.User;
    public void EnsureSchema() { }
    public List<T> All<T>() where T : Entity, new() => _s.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => _s.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => _s.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => _s.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => _s.Batch(work, summary);
}

/// <summary>Picks the assistant store for the current data source on every call (the user can switch it in Settings).</summary>
public sealed class AssistantStoreSelector : IAssistantStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private IAssistantStore? _impl;

    public AssistantStoreSelector(Func<IProjectStore> store) => _store = store;

    private IAssistantStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null)
            {
                IAssistantStore impl = s switch
                {
                    RemoteProjectStore r => new RemoteAssistantStore(r),
                    Db db => new SqliteAssistantStore(db.Path, db.User, db.Machine),
                    _ => new InMemoryAssistantStore(s.User),
                };
                impl.EnsureSchema();
                _impl = impl;
                _for = s;
            }
            if (_impl is SqliteAssistantStore side) side.User = s.User;
            return _impl;
        }
    }

    public string User => Impl.User;
    public void EnsureSchema() => Impl.EnsureSchema();
    public List<T> All<T>() where T : Entity, new() => Impl.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => Impl.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => Impl.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => Impl.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => Impl.Batch(work, summary);
}

/// <summary>For tests and data sources without a file: keeps everything in memory (same RowVersion rule).</summary>
public sealed class InMemoryAssistantStore : IAssistantStore
{
    private readonly Dictionary<Type, List<Entity>> _rows = new();
    private long _next = 1;
    private readonly object _gate = new();
    public InMemoryAssistantStore(string user = "tester") => User = user;
    public string User { get; set; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public void EnsureSchema() { }

    private List<Entity> Rows(Type t) => _rows.TryGetValue(t, out var l) ? l : _rows[t] = new List<Entity>();

    public List<T> All<T>() where T : Entity, new()
    {
        lock (_gate) return Rows(typeof(T)).Cast<T>().ToList();
    }

    public T Insert<T>(T entity, string? summary = null) where T : Entity
    {
        lock (_gate)
        {
            entity.Id = _next++;
            entity.RowVersion = 1;
            entity.UpdatedBy = User;
            entity.UpdatedAt = Clock();
            Rows(entity.GetType()).Add(entity);
            return entity;
        }
    }

    public T Update<T>(T entity, string? summary = null) where T : Entity, new()
    {
        lock (_gate)
        {
            var list = Rows(typeof(T));
            var i = list.FindIndex(e => e.Id == entity.Id);
            if (i < 0) throw new InvalidOperationException($"{typeof(T).Name} #{entity.Id} no longer exists.");
            if (list[i].RowVersion != entity.RowVersion && !ReferenceEquals(list[i], entity)) throw new ConcurrencyException(typeof(T).Name, entity.Id, list[i].UpdatedBy);
            entity.RowVersion++;
            entity.UpdatedBy = User;
            entity.UpdatedAt = Clock();
            list[i] = entity;
            return entity;
        }
    }

    public void Delete<T>(T entity, string? summary = null) where T : Entity
    {
        lock (_gate) Rows(entity.GetType()).RemoveAll(e => e.Id == entity.Id);
    }

    public void Batch(Action<IStoreBatch> work, string summary) => work(new Writer(this));

    private sealed class Writer : IStoreBatch
    {
        private readonly InMemoryAssistantStore _s;
        public Writer(InMemoryAssistantStore s) => _s = s;
        public T Insert<T>(T entity) where T : Entity => _s.Insert(entity);
        public int InsertMany<T>(IEnumerable<T> entities) where T : Entity { var n = 0; foreach (var e in entities) { _s.Insert(e); n++; } return n; }
        public void Update<T>(T entity) where T : Entity, new() => _s.Update(entity);
        public void Delete<T>(T entity) where T : Entity => _s.Delete(entity);
    }
}
