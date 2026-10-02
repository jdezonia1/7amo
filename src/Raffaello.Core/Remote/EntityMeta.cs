using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Remote;

/// <summary>
/// Entity types known to the remote store (table name -> type), their stored properties and foreign keys.
/// Starts with <see cref="Db.EntityTypes"/>; other stores register their own types with <see cref="Register"/> so queued
/// offline writes and the migration tool can handle them too.
/// </summary>
public static class EntityMeta
{
    private static readonly ConcurrentDictionary<string, Type> ByTable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropCache = new();

    static EntityMeta()
    {
        foreach (var t in Db.EntityTypes) ByTable[Db.TableOf(t)] = t;
    }

    public static void Register(Type t)
    {
        if (!typeof(Entity).IsAssignableFrom(t) || t.IsAbstract || t.GetConstructor(Type.EmptyTypes) is null)
            throw new ArgumentException($"{t.Name} must be a concrete Entity with a parameterless constructor.");
        ByTable[Db.TableOf(t)] = t;
    }

    public static IReadOnlyCollection<Type> All => ByTable.Values.OrderBy(t => t.Name).ToList();
    public static Type? Find(string table) => ByTable.GetValueOrDefault(table);
    public static string TableOf(Type t) => Db.TableOf(t);

    public static PropertyInfo[] Props(Type t) => PropCache.GetOrAdd(t, x =>
        x.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray());

    /// <summary>long / long? columns named *Id (other than Id): these may carry temporary negative ids.</summary>
    public static IEnumerable<PropertyInfo> ForeignKeys(Type t) => Props(t).Where(p =>
        p.Name != nameof(Entity.Id) && p.Name.EndsWith("Id", StringComparison.Ordinal) && (p.PropertyType == typeof(long) || p.PropertyType == typeof(long?)));

    /// <summary>Calls store.All&lt;T&gt;() for a runtime type.</summary>
    public static List<Entity> AllOf(IProjectStore store, Type t)
    {
        var m = typeof(IProjectStore).GetMethod(nameof(IProjectStore.All))!.MakeGenericMethod(t);
        return ((IEnumerable)m.Invoke(store, null)!).Cast<Entity>().ToList();
    }

    public static Entity Clone(Entity e) => (Entity)RemoteJson.Deserialize(RemoteJson.Serialize(e), e.GetType())!;
}
