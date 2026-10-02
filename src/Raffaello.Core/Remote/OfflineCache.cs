using System.Collections;
using System.Text.Json;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Remote;

/// <summary>
/// Read cache of the server data on this PC: one JSON file per table + its ETag, plus meta, audit and the sync state
/// (queued writes, id / version maps, conflicts). Lets the app open and work read-only-plus-queue when the server is down.
/// </summary>
public sealed class OfflineCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Json, string? ETag)> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _meta = new();
    public string Folder { get; }

    public OfflineCache(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "tables"));
        _meta = ReadJson<Dictionary<string, string?>>("meta.json") ?? new();
    }

    private string TablePath(string table) => Path.Combine(Folder, "tables", table + ".json");
    private string TagPath(string table) => Path.Combine(Folder, "tables", table + ".etag");

    public bool HasAny => Directory.EnumerateFiles(Path.Combine(Folder, "tables"), "*.json").Any();

    public string? ETag(string table)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(table, out var t)) return t.ETag;
            var p = TagPath(table);
            return File.Exists(p) && File.Exists(TablePath(table)) ? File.ReadAllText(p) : null;
        }
    }

    public string? Json(string table)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(table, out var t)) return t.Json;
            var p = TablePath(table);
            if (!File.Exists(p)) return null;
            var json = File.ReadAllText(p);
            _tables[table] = (json, File.Exists(TagPath(table)) ? File.ReadAllText(TagPath(table)) : null);
            return json;
        }
    }

    public void Put(string table, string json, string? etag)
    {
        lock (_gate)
        {
            _tables[table] = (json, etag);
            WriteAtomic(TablePath(table), json);
            if (etag is null) { if (File.Exists(TagPath(table))) File.Delete(TagPath(table)); }
            else WriteAtomic(TagPath(table), etag);
        }
    }

    /// <summary>Forgets the ETag so the next online read fetches the table again.</summary>
    public void Invalidate(string? table = null)
    {
        lock (_gate)
        {
            var names = table is null ? _tables.Keys.ToList() : new List<string> { table };
            foreach (var n in names) if (_tables.TryGetValue(n, out var t)) _tables[n] = (t.Json, null);
            if (table is null) foreach (var f in Directory.EnumerateFiles(Path.Combine(Folder, "tables"), "*.etag")) File.Delete(f);
            else if (File.Exists(TagPath(table))) File.Delete(TagPath(table));
        }
    }

    public List<T> Rows<T>(string table) => Json(table) is { } j ? RemoteJson.Deserialize<List<T>>(j) ?? new List<T>() : new List<T>();

    public IList Rows(Type t)
    {
        var listType = typeof(List<>).MakeGenericType(t);
        var j = Json(EntityMeta.TableOf(t));
        return (IList)(j is null ? Activator.CreateInstance(listType)! : RemoteJson.Deserialize(j, listType) ?? Activator.CreateInstance(listType)!);
    }

    /// <summary>Applies an offline change to the cached table (the server copy replaces it after the next sync).</summary>
    public void Apply(string op, Entity e)
    {
        lock (_gate)
        {
            var t = e.GetType();
            var rows = Rows(t);
            var idx = -1;
            for (var i = 0; i < rows.Count; i++) if (((Entity)rows[i]!).Id == e.Id) { idx = i; break; }
            switch (op)
            {
                case WriteOps.Insert: if (idx < 0) rows.Add(EntityMeta.Clone(e)); break;
                case WriteOps.Update: if (idx >= 0) rows[idx] = EntityMeta.Clone(e); else rows.Add(EntityMeta.Clone(e)); break;
                case WriteOps.Delete: if (idx >= 0) rows.RemoveAt(idx); break;
            }
            Put(EntityMeta.TableOf(t), JsonSerializer.Serialize(rows, rows.GetType(), RemoteJson.Options), null);
        }
    }

    public string? GetMeta(string key) { lock (_gate) return _meta.GetValueOrDefault(key); }

    public void SetMeta(string key, string? value)
    {
        lock (_gate) { _meta[key] = value; WriteJson("meta.json", _meta); }
    }

    public T? ReadJson<T>(string file)
    {
        var p = Path.Combine(Folder, file);
        try { return File.Exists(p) ? RemoteJson.Deserialize<T>(File.ReadAllText(p)) : default; }
        catch (JsonException) { return default; }
    }

    public void WriteJson(string file, object value) { lock (_gate) WriteAtomic(Path.Combine(Folder, file), RemoteJson.Serialize(value)); }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
