using Raffaello.Core.AconexWeb;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Queue;
using Raffaello.Core.Settings;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Notify;

/// <summary>
/// The brief's inputs read straight from any <see cref="IProjectStore"/> (the server's PostgreSQL store, or a data file): the project
/// snapshot, chain and "Needs you today" queue including the module items (materials, Aconex, variations). Used by the server's
/// scheduled briefs, where no desktop app is running.
/// </summary>
public static class StoreBriefData
{
    public static (ProjectService Project, IReadOnlyList<QueueItem> Queue) Load(IProjectStore store, DateTime today)
    {
        var project = new ProjectService(new AppSettings { SeedDemoData = false, UserName = store.User }, _ => store);
        project.QueueSources.Add(p => ModuleQueue.Materials(new StoreMaterials(store).Load(), new MaterialsSettings(), today));
        project.QueueSources.Add(p => ModuleQueue.Aconex(StatusBoard.Build(p.Snapshot.SubInvoices, Safe(() => store.All<AconexWorkflowLink>()), LatestChecks(store), today, false)));
        project.QueueSources.Add(p => ModuleQueue.Variations(Safe(() => store.All<Variation>()), today));
        // contract obligations (handover, warranty end, retention release, penalties) from the signed contracts' terms and rules
        project.QueueSources.Add(p => Wiring.ContractObligations.Queue(Wiring.ContractObligations.Build(store, p.Snapshot), today));
        project.Reload();
        return (project, project.Queue);
    }

    private static List<T> Safe<T>(Func<List<T>> read)
    {
        try { return read(); } catch (Exception) { return new List<T>(); }
    }

    public static Dictionary<string, AconexWorkflowCheck> LatestChecks(IProjectStore store) =>
        Safe(() => store.All<AconexWorkflowCheck>()).Where(c => c.State != WorkflowStates.Error)
            .GroupBy(c => c.WorkflowNo.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CheckedAt).ThenByDescending(c => c.Id).First());

    /// <summary>Read-only materials view over a generic store.</summary>
    private sealed class StoreMaterials : MaterialsStoreBase
    {
        private readonly IProjectStore _s;
        public StoreMaterials(IProjectStore s) => _s = s;
        public override string User => _s.User;
        public override void EnsureSchema() { }
        public override List<T> All<T>() => Safe(() => _s.All<T>());
        public override T Insert<T>(T entity, string? summary = null) => throw new NotSupportedException("read-only");
        public override T Update<T>(T entity, string? summary = null) => throw new NotSupportedException("read-only");
        public override void Delete<T>(T entity, string? summary = null) => throw new NotSupportedException("read-only");
        public override void Batch(Action<IStoreBatch> work, string summary) => throw new NotSupportedException("read-only");
        protected override DateTime Now() => DateTime.Now;
        protected override bool IsLockConflict(Exception ex) => false;
    }
}
