using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Remote;

/// <summary>
/// [phase6] Entity types of the module stores (materials / MOS / BOQ / coding memory, Aconex, variations). Registered with
/// <see cref="EntityMeta"/> so the remote store, the offline cache and <see cref="LocalToServerMigrator"/> know them, and
/// cleared by a data reset.
/// </summary>
public static class ModuleEntities
{
    public static readonly Type[] Aconex =
    {
        typeof(AconexWorkflowLink), typeof(AconexWorkflowCheck), typeof(AconexStepChange), typeof(InvoiceAttachment),
        typeof(AconexDownload), typeof(AconexDownloadJob), typeof(AconexQueueItem),
    };

    public static readonly Type[] Variations = { typeof(Variation), typeof(VariationLine), typeof(VariationDoc), typeof(VariationStatusChange) };

    public static IReadOnlyList<Type> All => MaterialsStoreBase.EntityTypes.Concat(Aconex).Concat(Variations)
        .Concat(Cables.CableStore.EntityTypes)   // [cables]
        .ToList();

    private static bool _registered;
    public static void RegisterAll()
    {
        if (_registered) return;
        foreach (var t in All) EntityMeta.Register(t);
        _registered = true;
    }
}

/// <summary>[phase6] Materials store on the Raffaello server: same rules as SQLite (shared base); the DN-line lock is re-checked by the server guard.</summary>
public sealed class RemoteMaterialsStore : MaterialsStoreBase
{
    private readonly RemoteProjectStore _r;
    static RemoteMaterialsStore() => ModuleEntities.RegisterAll();
    public RemoteMaterialsStore(RemoteProjectStore r) => _r = r;

    public override string User => _r.User;
    public override void EnsureSchema() { }   // the server creates the tables
    public override List<T> All<T>() => _r.All<T>();
    public override T Insert<T>(T entity, string? summary = null) => _r.Insert(entity, summary);
    public override T Update<T>(T entity, string? summary = null) => _r.Update(entity, summary);
    public override void Delete<T>(T entity, string? summary = null) => _r.Delete(entity, summary);
    public override void Batch(Action<IStoreBatch> work, string summary) => _r.Batch(work, summary);
    protected override DateTime Now() => _r.Clock();
    protected override bool IsLockConflict(Exception ex) => ex is RemoteRejectedException { Error.Code: ErrorCodes.Locked or ErrorCodes.Conflict };
}

/// <summary>
/// [phase6] Picks the materials store for the current data source on every call: SQLite data file (local) or the server.
/// Lets the app keep one singleton while the user switches the data source in Settings.
/// </summary>
public sealed class MaterialsStoreSelector : IMaterialsStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private IMaterialsStore? _impl;

    public MaterialsStoreSelector(Func<IProjectStore> store) => _store = store;

    private IMaterialsStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null)
            {
                _impl = s switch
                {
                    RemoteProjectStore r => new RemoteMaterialsStore(r),
                    Db db => new SqliteMaterialsStore(() => db),
                    _ => throw new InvalidOperationException($"Materials are not available for the data source {s.GetType().Name}."),
                };
                _for = s;
            }
            return _impl;
        }
    }

    public string User => Impl.User;
    public void EnsureSchema() => Impl.EnsureSchema();
    public MaterialsSnapshot Load() => Impl.Load();
    public List<T> All<T>() where T : Entity, new() => Impl.All<T>();
    public T Insert<T>(T entity, string? summary = null) where T : Entity => Impl.Insert(entity, summary);
    public T Update<T>(T entity, string? summary = null) where T : Entity, new() => Impl.Update(entity, summary);
    public void Delete<T>(T entity, string? summary = null) where T : Entity => Impl.Delete(entity, summary);
    public void Batch(Action<IStoreBatch> work, string summary) => Impl.Batch(work, summary);
    public MatPo SavePo(MatPo po, IReadOnlyList<MatPoLine> lines, IReadOnlyList<MatPoScope> scope) => Impl.SavePo(po, lines, scope);
    public MatDn SaveDn(MatDn dn, IReadOnlyList<MatDnLine> lines) => Impl.SaveDn(dn, lines);
    public MatMir SaveMir(MatMir mir, IReadOnlyList<MatMirDn> dns, IReadOnlyList<MatMirEvidence> evidence) => Impl.SaveMir(mir, dns, evidence);
    public void LockDnLines(IReadOnlyCollection<long> dnLineIds, string supplier, string poNo, int invoiceNo, long subInvoiceId) => Impl.LockDnLines(dnLineIds, supplier, poNo, invoiceNo, subInvoiceId);
    public int ReleaseLocks(string supplier, string poNo, int invoiceNo) => Impl.ReleaseLocks(supplier, poNo, invoiceNo);
    public int ReleaseLines(IReadOnlyCollection<long> dnLineIds) => Impl.ReleaseLines(dnLineIds);
}

/// <summary>[phase6] Aconex store on the server (same behaviour as <see cref="SqliteAconexStore"/>, filters done in memory).</summary>
public sealed class RemoteAconexStore : IAconexStore
{
    private readonly RemoteProjectStore _r;
    static RemoteAconexStore() => ModuleEntities.RegisterAll();
    public RemoteAconexStore(RemoteProjectStore r) { _r = r; User = r.User; }

    public string User { get; set; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public void EnsureSchema() { }

    private static string Wf(string s) => s.Trim().ToUpperInvariant();

    public AconexWorkflowLink Link(long subInvoiceId, string workflowNo, string note = "")
    {
        var wf = Wf(workflowNo);
        var active = _r.All<AconexWorkflowLink>().Where(l => l.SubInvoiceId == subInvoiceId && l.Active).ToList();
        var same = active.FirstOrDefault(o => WorkflowParser.NormalizeWf(o.WorkflowNo) == WorkflowParser.NormalizeWf(wf));
        if (same != null) return same;
        var created = new AconexWorkflowLink { SubInvoiceId = subInvoiceId, WorkflowNo = wf, Note = note, LinkedAt = Clock(), Active = true };
        _r.Batch(b =>
        {
            foreach (var old in active) { old.Active = false; b.Update(old); }
            b.Insert(created);
        }, $"Invoice revision #{subInvoiceId} linked to Aconex workflow {wf}");
        return created;
    }

    public List<AconexWorkflowLink> ActiveLinks() => _r.All<AconexWorkflowLink>().Where(l => l.Active).ToList();

    public AconexWorkflowCheck SaveCheck(WorkflowLookupResult r, long? subInvoiceId)
    {
        var check = new AconexWorkflowCheck
        {
            WorkflowNo = Wf(r.WorkflowNo), WorkflowName = r.WorkflowName, CheckedAt = r.CheckedAt == default ? Clock() : r.CheckedAt,
            State = r.State, CurrentStep = r.CurrentStep, WithWhom = r.WithWhom, DateDue = r.DateDue, IsOverdue = r.IsOverdue, DaysOverdue = r.DaysOverdue,
            Outcome = r.Outcome, DocumentNo = r.DocumentNo, DocumentTitle = r.DocumentTitle, StepsJson = StepJson.Serialize(r.Steps),
            PageScreenshot = r.PageScreenshotPath, TableScreenshot = r.TableScreenshotPath, Error = r.Error,
        };
        var previous = r.State is WorkflowStates.Error ? null
            : Checks(check.WorkflowNo).FirstOrDefault(c => c.State != WorkflowStates.Error);
        var changes = previous is null ? new List<AconexStepChange>()
            : WorkflowHistory.Diff(check.WorkflowNo, StepJson.Deserialize(previous.StepsJson), r.Steps, check.CheckedAt);
        _r.Batch(b =>
        {
            b.Insert(check);
            foreach (var ch in changes) b.Insert(ch);
            if (subInvoiceId is { } id)
                foreach (var shot in new[] { r.PageScreenshotPath, r.TableScreenshotPath }.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)))
                    b.Insert(new InvoiceAttachment { SubInvoiceId = id, WorkflowNo = check.WorkflowNo, Path = shot, Sha256 = FileHash.Sha256(shot), AddedAt = check.CheckedAt });
        }, $"Aconex {r.Summary}" + (changes.Count > 0 ? $" ({changes.Count} changes)" : ""));
        return check;
    }

    public List<AconexWorkflowCheck> Checks(string workflowNo)
    {
        var wf = Wf(workflowNo);
        return _r.All<AconexWorkflowCheck>().Where(c => c.WorkflowNo == wf).OrderByDescending(c => c.CheckedAt).ThenByDescending(c => c.Id).ToList();
    }

    public AconexWorkflowCheck? LatestCheck(string workflowNo) => Checks(workflowNo).FirstOrDefault();

    public Dictionary<string, AconexWorkflowCheck> LatestChecks() =>
        _r.All<AconexWorkflowCheck>().GroupBy(c => c.WorkflowNo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Id).First(), StringComparer.OrdinalIgnoreCase);

    public List<AconexStepChange> History(string? workflowNo = null, int take = 500) =>
        _r.All<AconexStepChange>().Where(c => workflowNo is null || c.WorkflowNo == Wf(workflowNo))
            .OrderByDescending(c => c.At).ThenByDescending(c => c.Id).Take(take).ToList();

    public List<InvoiceAttachment> Attachments(long subInvoiceId) => _r.All<InvoiceAttachment>().Where(a => a.SubInvoiceId == subInvoiceId).OrderByDescending(a => a.AddedAt).ToList();
    public void AddAttachment(InvoiceAttachment a) => _r.Insert(a, $"Attached {Path.GetFileName(a.Path)} to invoice revision #{a.SubInvoiceId}");

    public List<AconexDownload> Downloads() => _r.All<AconexDownload>().OrderByDescending(d => d.DownloadedAt).ThenByDescending(d => d.Id).ToList();

    public AconexDownload? FindDownload(string docNo, string revision) =>
        _r.All<AconexDownload>().Where(d => d.DocumentNo.Trim().Equals(docNo.Trim(), StringComparison.OrdinalIgnoreCase) && d.Revision.Trim().Equals(revision.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Id).FirstOrDefault();

    public AconexDownload RegisterDownload(AconexDownload d) => _r.Insert(d, $"Downloaded {d.DocumentNo} rev {d.Revision} -> {d.FileName}");

    public AconexDownloadJob CreateJob(DocumentQuery query, IEnumerable<DocumentHit> hits, IReadOnlySet<string> alreadyDownloaded)
    {
        var list = hits.ToList();
        var job = new AconexDownloadJob
        {
            CreatedAt = Clock(), CriteriaJson = System.Text.Json.JsonSerializer.Serialize(query), Description = query.Describe(), State = JobStates.Pending, Total = list.Count,
            Skipped = list.Count(h => alreadyDownloaded.Contains(h.RevisionKey)),
        };
        _r.Batch(b =>
        {
            b.Insert(job);
            var i = 0;
            foreach (var h in list)
            {
                var skip = alreadyDownloaded.Contains(h.RevisionKey);
                b.Insert(new AconexQueueItem
                {
                    JobId = job.Id, Order = ++i, DocumentNo = h.DocumentNo, Revision = h.Revision, Version = h.Version, Title = h.Title, DocType = h.Type,
                    Discipline = h.Discipline, DocDate = h.Date, State = skip ? JobStates.Skipped : JobStates.Pending, Message = skip ? "already downloaded" : "",
                });
            }
        }, $"Aconex download job: {job.Description} ({list.Count} revisions)");
        return _r.Get<AconexDownloadJob>(job.Id) ?? job;
    }

    public List<AconexDownloadJob> Jobs(int take = 50) => _r.All<AconexDownloadJob>().OrderByDescending(j => j.Id).Take(take).ToList();
    public List<AconexQueueItem> Queue(long jobId) => _r.All<AconexQueueItem>().Where(q => q.JobId == jobId).OrderBy(q => q.Order).ToList();
    public void UpdateQueueItem(AconexQueueItem item) => _r.Update(item, $"Queue {item.DocumentNo} rev {item.Revision}: {item.State}");
    public void UpdateJob(AconexDownloadJob job) => _r.Update(job, $"Download job #{job.Id}: {job.State} {job.Done}/{job.Total}");

    public AconexDownloadJob? ResumableJob() =>
        _r.All<AconexDownloadJob>().Where(j => j.State is JobStates.Pending or JobStates.Running or JobStates.Paused).OrderByDescending(j => j.Id).FirstOrDefault();
}

/// <summary>[phase6] Variations / EI register on the server (same rules as <see cref="SqliteVariationStore"/>).</summary>
public sealed class RemoteVariationStore : IVariationStore
{
    private readonly RemoteProjectStore _r;
    static RemoteVariationStore() => ModuleEntities.RegisterAll();
    public RemoteVariationStore(RemoteProjectStore r) { _r = r; User = r.User; }

    public string User { get; set; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public void EnsureSchema() { }

    public List<Variation> Variations() => _r.All<Variation>().OrderByDescending(v => v.Date).ThenByDescending(v => v.Id).ToList();
    public Variation? Get(long id) => _r.Get<Variation>(id);
    public List<VariationLine> Lines(long variationId) => _r.All<VariationLine>().Where(l => l.VariationId == variationId).OrderBy(l => l.Order).ThenBy(l => l.Id).ToList();
    public List<VariationLine> AllLines() => _r.All<VariationLine>();
    public List<VariationDoc> Docs(long variationId) => _r.All<VariationDoc>().Where(d => d.VariationId == variationId).OrderBy(d => d.AddedAt).ToList();
    public List<VariationStatusChange> StatusLog(long variationId) => _r.All<VariationStatusChange>().Where(s => s.VariationId == variationId).OrderBy(s => s.Id).ToList();

    public string NextNumber(string type)
    {
        var t = (type ?? VariationTypes.Vo).Trim().ToUpperInvariant();
        var max = _r.All<Variation>().Where(v => v.Type == t)
            .Select(v => Regex.Match(v.Number, @"(\d+)\s*$")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        return $"{t}-{max + 1:000}";
    }

    public Variation Create(Variation v)
    {
        if (string.IsNullOrWhiteSpace(v.Number)) v.Number = NextNumber(v.Type);
        if (_r.All<Variation>().Any(x => x.Number.Trim().Equals(v.Number.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{v.Number} already exists.");
        if (v.Date == default) v.Date = Clock().Date;
        v.StatusChangedAt = Clock();
        v.CreatedBy = User;
        _r.Batch(b =>
        {
            b.Insert(v);
            b.Insert(new VariationStatusChange { VariationId = v.Id, FromStatus = "", ToStatus = v.Status, At = Clock(), By = User, Note = "created" });
        }, $"Variation {v.Number} created: {v.Title}");
        return v;
    }

    public void Save(Variation v, IReadOnlyList<VariationLine> lines)
    {
        var current = Get(v.Id) ?? throw new InvalidOperationException($"Variation #{v.Id} no longer exists.");
        if (VariationStatus.IsClosed(current.Status))
            throw new InvalidOperationException($"{current.Number} is {current.Status} - it is locked. Move it back to DRAFT (if allowed) to change it.");
        var t = VariationMath.Totals(lines);
        var keep = lines.Where(l => l.Id > 0).Select(l => l.Id).ToHashSet();
        var drop = Lines(v.Id).Where(o => !keep.Contains(o.Id)).ToList();
        _r.Batch(b =>
        {
            b.Update(v);
            foreach (var old in drop) b.Delete(old);
            var order = 0;
            foreach (var l in lines)
            {
                l.VariationId = v.Id;
                l.Order = ++order;
                if (l.Kind == VariationLineKinds.NewItem) l.Rate = VariationMath.RateOf(l);
                if (l.Id > 0) b.Update(l); else b.Insert(l);
            }
        }, $"Variation {v.Number} saved: {lines.Count} lines, net {t.Net:N2}");
    }

    public Variation SetStatus(Variation v, string to, string note = "", DateTime? at = null)
    {
        var current = Get(v.Id) ?? throw new InvalidOperationException($"Variation #{v.Id} no longer exists.");
        if (current.RowVersion != v.RowVersion) throw new ConcurrencyException("Variations", v.Id, current.UpdatedBy);
        if (!VariationStatus.CanMove(current.Status, to))
            throw new InvalidOperationException($"{current.Number}: {current.Status} -> {to} is not allowed (allowed: {string.Join(", ", VariationStatus.Next(current.Status))}).");
        if (to == VariationStatus.Submitted && Lines(v.Id).Count == 0)
            throw new InvalidOperationException($"{current.Number} has no lines - add the omission / addition / new items before submitting.");
        var when = at ?? Clock();
        var from = current.Status;
        current.Status = to;
        current.StatusChangedAt = when;
        if (to == VariationStatus.Submitted && current.SubmittedAt is null) current.SubmittedAt = when;
        if (VariationStatus.IsClosed(to)) current.DecidedAt = when;
        if (to == VariationStatus.Draft) current.DecidedAt = null;
        _r.Batch(b =>
        {
            b.Update(current);
            b.Insert(new VariationStatusChange { VariationId = v.Id, FromStatus = from, ToStatus = to, At = when, By = User, Note = note });
        }, $"Variation {current.Number}: {from} -> {to}" + (note.Length > 0 ? $" ({note})" : ""));
        return current;
    }

    public VariationDoc AddDoc(VariationDoc d)
    {
        if (d.AddedAt == default) d.AddedAt = Clock();
        return _r.Insert(d, $"Document {d.FileName} added to variation #{d.VariationId}");
    }

    public void RemoveDoc(VariationDoc d) => _r.Delete(d, $"Document {d.FileName} removed from variation #{d.VariationId}");

    public void Delete(Variation v)
    {
        if (v.Status != VariationStatus.Draft) throw new InvalidOperationException("Only DRAFT variations can be deleted - withdraw it instead.");
        var lines = Lines(v.Id);
        var docs = Docs(v.Id);
        _r.Batch(b =>
        {
            foreach (var l in lines) b.Delete(l);
            foreach (var d in docs) b.Delete(d);
            b.Delete(v);
        }, $"Variation {v.Number} deleted");
    }
}
