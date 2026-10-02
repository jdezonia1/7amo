using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Core.Insights;

/// <summary>Everything the insights module keeps (small tables, loaded at once).</summary>
public sealed class InsightsData
{
    public List<InsightDismissal> Dismissals { get; init; } = new();
    public List<InsightThreshold> Thresholds { get; init; } = new();
    public List<InsightNorm> Norms { get; init; } = new();
    public List<InsightProgramme> Programme { get; init; } = new();
    public List<InsightPaymentTerm> Terms { get; init; } = new();
    public List<InsightInvoicePeriod> InvoicePeriods { get; init; } = new();
    public List<InsightFileHash> FileHashes { get; init; } = new();

    public double Threshold(string key) => InsightThresholds.Get(Thresholds, key);

    /// <summary>Active dismissals by fingerprint (latest wins).</summary>
    public Dictionary<string, InsightDismissal> ActiveDismissals() =>
        Dismissals.Where(d => d.Active).GroupBy(d => d.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DismissedAt).ThenByDescending(d => d.Id).First(), StringComparer.Ordinal);
}

/// <summary>
/// Persistence of the insights inputs and decisions. SQLite: own tables in the shared data file (<see cref="SqliteInsightsStore"/>);
/// server: <see cref="RemoteInsightsStore"/> on the generic entity API (tables registered by the Insights server module).
/// </summary>
public interface IInsightsStore
{
    string User { get; set; }
    void EnsureSchema();
    InsightsData Load();
    /// <summary>Insert (Id 0) or update (optimistic RowVersion check).</summary>
    T Save<T>(T entity, string? summary = null) where T : Entity, new();
    void Delete<T>(T entity, string? summary = null) where T : Entity, new();
    /// <summary>Replaces a whole editable table (norms, programme, terms, periods, thresholds) in one transaction.</summary>
    void ReplaceAll<T>(IReadOnlyList<T> rows, string summary) where T : Entity, new();
    InsightDismissal Dismiss(string fingerprint, string kind, string title, string reason);
    void Restore(string fingerprint, string note = "");
    void SaveFileHashes(IReadOnlyList<InsightFileHash> hashes);
}

internal static class InsightsStoreRules
{
    public static void CheckReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Give a reason for dismissing the warning (it is recorded).");
    }

    /// <summary>Plans the writes of a table replace: (inserts, updates, deletes).</summary>
    public static (List<T> Ins, List<T> Upd, List<T> Del) Plan<T>(IReadOnlyList<T> rows, IReadOnlyList<T> current) where T : Entity
    {
        var keep = rows.Where(r => r.Id > 0).Select(r => r.Id).ToHashSet();
        return (rows.Where(r => r.Id <= 0).ToList(), rows.Where(r => r.Id > 0).ToList(), current.Where(c => !keep.Contains(c.Id)).ToList());
    }
}

/// <summary>SQLite implementation: Insight* tables in the shared data file (same conventions, audit log shared).</summary>
public sealed class SqliteInsightsStore : SideStore, IInsightsStore
{
    public SqliteInsightsStore(string path, string user, string? machine = null) : base(path, user, machine) { }

    protected override IEnumerable<Type> Tables => InsightsEntities.All;

    protected override IEnumerable<string> Indexes => new[]
    {
        "CREATE INDEX IF NOT EXISTS IX_InsightDismissals_Fp ON InsightDismissals(Fingerprint);",
        $"CREATE INDEX IF NOT EXISTS IX_InsightFileHashs_Path ON {Db.TableOf<InsightFileHash>()}(FilePath);",
    };

    private bool _ready;
    private void Ready() { if (!_ready) { EnsureSchema(); _ready = true; } }

    public InsightsData Load()
    {
        Ready();
        return new InsightsData
        {
            Dismissals = All<InsightDismissal>(), Thresholds = All<InsightThreshold>(), Norms = All<InsightNorm>(), Programme = All<InsightProgramme>(),
            Terms = All<InsightPaymentTerm>(), InvoicePeriods = All<InsightInvoicePeriod>(), FileHashes = All<InsightFileHash>(),
        };
    }

    public T Save<T>(T entity, string? summary = null) where T : Entity, new()
    {
        Ready();
        return entity.Id > 0 ? Update(entity, summary) : Insert(entity, summary);
    }

    void IInsightsStore.Delete<T>(T entity, string? summary) { Ready(); Delete(entity, summary); }

    public void ReplaceAll<T>(IReadOnlyList<T> rows, string summary) where T : Entity, new()
    {
        Ready();
        Batch(b =>
        {
            var (ins, upd, del) = InsightsStoreRules.Plan(rows, b.All<T>());
            foreach (var d in del) b.Delete(d);
            foreach (var u in upd) b.Update(u);
            foreach (var i in ins) b.Insert(i);
        }, summary);
    }

    public InsightDismissal Dismiss(string fingerprint, string kind, string title, string reason)
    {
        InsightsStoreRules.CheckReason(reason);
        Ready();
        var d = new InsightDismissal { Fingerprint = fingerprint, Kind = kind, Title = title, Reason = reason.Trim(), DismissedBy = User, DismissedAt = Clock(), Active = true };
        return Insert(d, $"Insight dismissed: {title} - {reason.Trim()}");
    }

    public void Restore(string fingerprint, string note = "")
    {
        Ready();
        var rows = All<InsightDismissal>("Fingerprint=@F AND Active=1", new { F = fingerprint });
        if (rows.Count == 0) return;
        Batch(b => { foreach (var r in rows) { r.Active = false; b.Update(r); } }, $"Insight restored: {rows[0].Title}" + (note.Length > 0 ? $" ({note})" : ""));
    }

    public void SaveFileHashes(IReadOnlyList<InsightFileHash> hashes)
    {
        if (hashes.Count == 0) return;
        Ready();
        Batch(b => { foreach (var h in hashes) { if (h.Id > 0) b.Update(h); else b.Insert(h); } }, $"{hashes.Count} document hashes cached");
    }
}

/// <summary>Server implementation on the generic entity API (offline queue, conflicts, audit and notices come with it).</summary>
public sealed class RemoteInsightsStore : IInsightsStore
{
    private readonly RemoteProjectStore _r;
    static RemoteInsightsStore() { foreach (var t in InsightsEntities.All) EntityMeta.Register(t); }
    public RemoteInsightsStore(RemoteProjectStore r) { _r = r; User = r.User; }

    /// <summary>Registers the tables with <see cref="EntityMeta"/> (migration, offline cache).</summary>
    public static void Register() { foreach (var t in InsightsEntities.All) EntityMeta.Register(t); }

    public string User { get; set; }
    public void EnsureSchema() { }

    public InsightsData Load() => new()
    {
        Dismissals = _r.All<InsightDismissal>(), Thresholds = _r.All<InsightThreshold>(), Norms = _r.All<InsightNorm>(), Programme = _r.All<InsightProgramme>(),
        Terms = _r.All<InsightPaymentTerm>(), InvoicePeriods = _r.All<InsightInvoicePeriod>(), FileHashes = _r.All<InsightFileHash>(),
    };

    public T Save<T>(T entity, string? summary = null) where T : Entity, new() => entity.Id > 0 ? _r.Update(entity, summary) : _r.Insert(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity, new() => _r.Delete(entity, summary);

    public void ReplaceAll<T>(IReadOnlyList<T> rows, string summary) where T : Entity, new()
    {
        var (ins, upd, del) = InsightsStoreRules.Plan(rows, _r.All<T>());
        _r.Batch(b =>
        {
            foreach (var d in del) b.Delete(d);
            foreach (var u in upd) b.Update(u);
            foreach (var i in ins) b.Insert(i);
        }, summary);
    }

    public InsightDismissal Dismiss(string fingerprint, string kind, string title, string reason)
    {
        InsightsStoreRules.CheckReason(reason);
        var d = new InsightDismissal { Fingerprint = fingerprint, Kind = kind, Title = title, Reason = reason.Trim(), DismissedBy = User, DismissedAt = _r.Clock(), Active = true };
        return _r.Insert(d, $"Insight dismissed: {title} - {reason.Trim()}");
    }

    public void Restore(string fingerprint, string note = "")
    {
        var rows = _r.All<InsightDismissal>().Where(d => d.Active && d.Fingerprint == fingerprint).ToList();
        if (rows.Count == 0) return;
        _r.Batch(b => { foreach (var r in rows) { r.Active = false; b.Update(r); } }, $"Insight restored: {rows[0].Title}" + (note.Length > 0 ? $" ({note})" : ""));
    }

    public void SaveFileHashes(IReadOnlyList<InsightFileHash> hashes)
    {
        if (hashes.Count == 0) return;
        _r.Batch(b => { foreach (var h in hashes) { if (h.Id > 0) b.Update(h); else b.Insert(h); } }, $"{hashes.Count} document hashes cached");
    }
}

/// <summary>Picks the insights store for the current data source on every call (SQLite file or server).</summary>
public sealed class InsightsStoreSelector : IInsightsStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private IInsightsStore? _impl;

    public InsightsStoreSelector(Func<IProjectStore> store) => _store = store;

    private IInsightsStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null)
            {
                _impl = s switch
                {
                    RemoteProjectStore r => new RemoteInsightsStore(r),
                    Db db => new SqliteInsightsStore(db.Path, db.User, db.Machine) { Clock = db.Clock },
                    _ => throw new InvalidOperationException($"Insights are not available for the data source {s.GetType().Name}."),
                };
                _for = s;
            }
            _impl.User = s.User;
            return _impl;
        }
    }

    public string User { get => Impl.User; set => Impl.User = value; }
    public void EnsureSchema() => Impl.EnsureSchema();
    public InsightsData Load() => Impl.Load();
    public T Save<T>(T entity, string? summary = null) where T : Entity, new() => Impl.Save(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity, new() => Impl.Delete(entity, summary);
    public void ReplaceAll<T>(IReadOnlyList<T> rows, string summary) where T : Entity, new() => Impl.ReplaceAll(rows, summary);
    public InsightDismissal Dismiss(string fingerprint, string kind, string title, string reason) => Impl.Dismiss(fingerprint, kind, title, reason);
    public void Restore(string fingerprint, string note = "") => Impl.Restore(fingerprint, note);
    public void SaveFileHashes(IReadOnlyList<InsightFileHash> hashes) => Impl.SaveFileHashes(hashes);
}
