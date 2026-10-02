using System.Globalization;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Remote;

public static class QueuedKinds
{
    public const string Write = "WRITE";
    public const string Event = "EVENT";
    public const string Meta = "META";
}

/// <summary>A write made while the server was unreachable, replayed in order on reconnect.</summary>
public sealed class QueuedWrite
{
    public long Seq { get; set; }
    public string Kind { get; set; } = QueuedKinds.Write;
    public DateTime At { get; set; }
    public string User { get; set; } = "";
    public WriteRequestDto Request { get; set; } = new();
    /// <summary>For each op: the cached row before the change (JSON), used as the base of a 3-way merge.</summary>
    public List<string?> Bases { get; set; } = new();
    public string Key { get; set; } = "";
    public string? Value { get; set; }

    public string Describe() => Kind switch
    {
        QueuedKinds.Event => $"{Key}: {Value}",
        QueuedKinds.Meta => $"setting {Key} = {Value}",
        _ => Request.Summary ?? string.Join(", ", Request.Ops.Select(o => $"{o.Op} {o.Table}")),
    };
}

public static class ConflictKinds
{
    /// <summary>Someone else changed the same row (RowVersion moved on): keep mine / keep theirs / merge.</summary>
    public const string RowVersion = "ROWVERSION";
    /// <summary>A queued claim no longer fits the remaining quantity: keep mine (post OVER with a reason) / keep theirs (drop).</summary>
    public const string Remaining = "REMAINING";
    /// <summary>The server refused the write (permission, locked invoice, deleted row ...): it can only be dropped.</summary>
    public const string Refused = "REFUSED";
}

public sealed class SyncConflict
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = ConflictKinds.RowVersion;
    public DateTime At { get; set; }
    public string Table { get; set; } = "";
    public long RowId { get; set; }
    public string Message { get; set; } = "";
    public string? ChangedBy { get; set; }
    public DateTime? ChangedAt { get; set; }
    public QueuedWrite Unit { get; set; } = new();
    public int OpIndex { get; set; } = -1;
    public string? MineJson { get; set; }
    public string? BaseJson { get; set; }
    public string? TheirsJson { get; set; }

    public string Title => Kind switch
    {
        ConflictKinds.RowVersion => $"{Table} #{RowId} was changed by {ChangedBy ?? "someone else"} while you were offline",
        ConflictKinds.Remaining => "Claim no longer fits the remaining quantity",
        _ => "Change refused by the server",
    };

    public string Summary => Unit.Describe();
}

public enum MergeSide { Mine, Theirs }

/// <summary>One field of a 3-way merge (base = row before my offline edit).</summary>
public sealed class MergeField
{
    public string Name { get; init; } = "";
    public string Base { get; init; } = "";
    public string Mine { get; init; } = "";
    public string Theirs { get; init; } = "";
    public bool ChangedByMe { get; init; }
    public bool ChangedByThem { get; init; }
    /// <summary>Both sides changed the field to different values: the user picks.</summary>
    public bool IsConflict => ChangedByMe && ChangedByThem && Mine != Theirs;
    public MergeSide Use { get; set; }
}

/// <summary>
/// Field-level 3-way merge for simple fields (text, numbers, dates, flags). Fields changed only by me take mine, fields
/// changed only by them take theirs, fields changed by both default to theirs unless the user picks mine.
/// </summary>
public static class FieldMerge
{
    private static readonly HashSet<string> Skip = new() { nameof(Entity.Id), nameof(Entity.RowVersion), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy) };

    public static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid);
    }

    public static List<MergeField> Plan(Entity? baseRow, Entity mine, Entity theirs)
    {
        var list = new List<MergeField>();
        foreach (var p in EntityMeta.Props(mine.GetType()))
        {
            if (Skip.Contains(p.Name) || !IsSimple(p.PropertyType)) continue;
            var b = Show(baseRow is null ? null : p.GetValue(baseRow));
            var m = Show(p.GetValue(mine));
            var t = Show(p.GetValue(theirs));
            var byMe = baseRow is null ? m != t : m != b;
            var byThem = baseRow is not null && t != b;
            if (!byMe && !byThem) continue;
            var f = new MergeField { Name = p.Name, Base = b, Mine = m, Theirs = t, ChangedByMe = byMe, ChangedByThem = byThem };
            f.Use = byMe && !byThem ? MergeSide.Mine : MergeSide.Theirs;
            list.Add(f);
        }
        return list;
    }

    /// <summary>Theirs + the fields chosen as mine; RowVersion = theirs (so the update applies on top of their change).</summary>
    public static Entity Apply(Entity mine, Entity theirs, IEnumerable<MergeField> fields)
    {
        var result = EntityMeta.Clone(theirs);
        var props = EntityMeta.Props(mine.GetType()).ToDictionary(p => p.Name);
        foreach (var f in fields.Where(f => f.Use == MergeSide.Mine))
            if (props.TryGetValue(f.Name, out var p)) p.SetValue(result, p.GetValue(mine));
        result.RowVersion = theirs.RowVersion;
        return result;
    }

    private static string Show(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double x => x.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

/// <summary>Persisted sync state (queue, temp-id map, version map, conflicts).</summary>
public sealed class SyncState
{
    public long NextTempId { get; set; } = -1;
    public long NextSeq { get; set; } = 1;
    public List<QueuedWrite> Queue { get; set; } = new();
    /// <summary>Temporary id given offline -> id given by the server at replay.</summary>
    public Dictionary<long, long> IdMap { get; set; } = new();
    /// <summary>"table|id" -> [version my queued edits were based on, version after my replayed edit].</summary>
    public Dictionary<string, long[]> VersionMap { get; set; } = new();
    public List<SyncConflict> Conflicts { get; set; } = new();
}
