using System.Collections.Concurrent;
using System.Reflection;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Server.Data;

/// <summary>
/// The entity types the server stores, by table name. Starts with every type of the SQLite store (<see cref="Db.EntityTypes"/>),
/// then adds the types of each <see cref="IServerModule"/>. A registered type gets a PostgreSQL table (created / extended
/// additively at start-up), the generic table endpoints, RowVersion concurrency, the audit trail and change notifications.
/// </summary>
public static class EntityRegistry
{
    private static readonly ConcurrentDictionary<string, Type> ByTable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            foreach (var t in Db.EntityTypes) Register(t);
            foreach (var m in ServerModules.All)
                foreach (var t in m.EntityTypes) Register(t);
            _initialized = true;
        }
    }

    public static void Register(Type t)
    {
        if (!typeof(Entity).IsAssignableFrom(t) || t.IsAbstract || t.GetConstructor(Type.EmptyTypes) is null)
            throw new ArgumentException($"{t.Name} must be a concrete Entity with a parameterless constructor.");
        ByTable[TableOf(t)] = t;
        Core.Remote.EntityMeta.Register(t);
    }

    public static IReadOnlyCollection<Type> All { get { EnsureInitialized(); return ByTable.Values.OrderBy(t => t.Name).ToList(); } }

    public static Type? Find(string table) { EnsureInitialized(); return ByTable.GetValueOrDefault(table); }

    public static Type Require(string table) => Find(table) ?? throw new WriteRejectedException(404, Core.Remote.ErrorCodes.NotFound, $"Unknown table '{table}'.");

    public static string TableOf(Type t) => Db.TableOf(t);

    /// <summary>Stored columns: public read/write properties (computed getters like ClaimLine.Key are not stored). Same rule as the client.</summary>
    public static PropertyInfo[] Props(Type t) => Core.Remote.EntityMeta.Props(t);

    /// <summary>Foreign-key-like columns (long / long? named *Id, not Id itself) - remapped when they carry a temporary negative id.</summary>
    public static IEnumerable<PropertyInfo> ForeignKeys(Type t) => Core.Remote.EntityMeta.ForeignKeys(t);
}

/// <summary>
/// A plug-in for the server: extra entity types (tables), extra write guards and extra endpoints.
/// Other phases (materials, Aconex, variations ...) add one class implementing this and list it in <see cref="ServerModules.All"/>.
/// </summary>
public interface IServerModule
{
    string Name { get; }
    IEnumerable<Type> EntityTypes { get; }
    IEnumerable<IWriteGuard> Guards(IServiceProvider services);
    void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder api);
}

public static class ServerModules
{
    /// <summary>Add new modules here (one line each). Phase 5 ships the core module only.</summary>
    public static readonly List<IServerModule> All = new()
    {
        // [phase-N] new SomethingServerModule(),
        new Modules.MaterialsServerModule(),     // [phase6]
        new Modules.AconexServerModule(),        // [phase6]
        new Modules.VariationsServerModule(),    // [phase6]
        new Modules.AssistantServerModule(),     // [assistant]
    };
}
