using Raffaello.Core.Domain;

namespace Raffaello.Core.Data;

/// <summary>
/// Writes that run atomically inside <see cref="IProjectStore.Batch"/>. Updates are optimistic:
/// a row whose RowVersion changed since it was read throws <see cref="ConcurrencyException"/> and the whole batch rolls back.
/// </summary>
public interface IStoreBatch
{
    T Insert<T>(T entity) where T : Entity;
    int InsertMany<T>(IEnumerable<T> entities) where T : Entity;
    void Update<T>(T entity) where T : Entity, new();
    void Delete<T>(T entity) where T : Entity;
}

/// <summary>
/// The persistence boundary. Everything above it (rules, analytics, view models) works on entities and
/// <see cref="ProjectSnapshot"/>; nothing above it sees SQL or provider types. Today the implementation is
/// <see cref="Db"/> (SQLite file on a shared drive, WAL); a server implementation (API + PostgreSQL) can replace it
/// as long as it keeps RowVersion concurrency and the audit log.
/// </summary>
public interface IProjectStore
{
    /// <summary>Where the data lives (file path or server URL), for display.</summary>
    string Location { get; }
    string User { get; set; }
    string Machine { get; }
    Func<DateTime> Clock { get; set; }

    void EnsureSchema();
    int Count<T>();
    List<T> All<T>() where T : new();
    T? Get<T>(long id) where T : Entity, new();
    T Insert<T>(T entity, string? summary = null) where T : Entity;
    int InsertMany<T>(IEnumerable<T> entities, string? summary = null) where T : Entity;
    T Update<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity;
    void Batch(Action<IStoreBatch> work, string summary);
    void ClearAll();

    void LogEvent(string action, string summary);
    List<AuditEntry> RecentAudit(int take = 50, DateTime? since = null);

    void Heartbeat(string screen);
    List<PresenceRow> OthersOnline(TimeSpan window);

    string? GetMeta(string key);
    void SetMeta(string key, string value);
}

/// <summary>Turns store exceptions into a message for the user without exposing the provider to the UI.</summary>
public static class StoreErrors
{
    public static string Describe(Exception ex) => ex is ConcurrencyException ? ex.Message : Db.Describe(ex);
}
