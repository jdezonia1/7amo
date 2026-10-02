using System.Globalization;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;
using Raffaello.Core.Seed;
using Raffaello.Core.Settings;

namespace Raffaello.Core;

/// <summary>
/// The app's single entry point into the data: opens the store, seeds demo data on first run,
/// keeps the in-memory snapshot + chain + queue current, and performs audited, concurrency-checked writes.
/// View models talk to this service only; they never see the store's provider (SQLite today, a server later).
/// </summary>
public sealed class ProjectService
{
    private readonly Func<AppSettings, IProjectStore> _storeFactory;

    public AppSettings Settings { get; }
    public IProjectStore Store { get; private set; }
    public RuleOptions Options { get; } = new();
    public RulesEngine Engine { get; private set; }
    public ProjectSnapshot Snapshot { get; private set; } = new();
    public IReadOnlyList<ChainRow> Chain { get; private set; } = Array.Empty<ChainRow>();
    public IReadOnlyDictionary<long, ChainRow> ChainById { get; private set; } = new Dictionary<long, ChainRow>();
    public IReadOnlyList<QueueItem> Queue { get; private set; } = Array.Empty<QueueItem>();
    public DateTime ProjectStart { get; private set; } = DateTime.Today.AddDays(-7 * 38);
    public DateTime PlannedFinish { get; private set; } = DateTime.Today.AddDays(7 * 28);
    public DateTime LastLoad { get; private set; }

    public event Action? Changed;

    public ProjectService(AppSettings settings, Func<AppSettings, IProjectStore>? storeFactory = null)
    {
        Settings = settings;
        _storeFactory = storeFactory ?? (s => new Db(s.DataFilePath, s.EffectiveUserName));
        Store = _storeFactory(settings);
        ApplyRuleSettings();
        Engine = new RulesEngine(Options);
    }

    /// <summary>Where the data lives, for display (file path or server URL).</summary>
    public string DataLocation => Store.Location;

    public string CurrentUser
    {
        get => Store.User;
        set => Store.User = value;
    }

    public void ApplyRuleSettings()
    {
        Options.WirDueDays = Settings.WirDueDays;
        Options.SiteTolerance = Settings.SiteTolerance;
        Options.PipeLengthM = Settings.PipeLengthM;
        Options.Today = DateTime.Today;
    }

    /// <summary>Opens (or creates) the store. Seeds demo data when empty and allowed.</summary>
    public void Initialize(Action<string>? progress = null)
    {
        progress?.Invoke("Opening data file...");
        Store = _storeFactory(Settings);
        Store.EnsureSchema();
        if (Store.Count<QtyLine>() == 0 && Settings.SeedDemoData)
        {
            progress?.Invoke("First run: building the demo project...");
            new DemoSeeder(DateTime.Today).Seed(Store);
        }
        progress?.Invoke("Building the chain...");
        Reload();
    }

    public void Reload()
    {
        ApplyRuleSettings();
        Engine = new RulesEngine(Options);
        Snapshot = new ProjectSnapshot
        {
            Rooms = Store.All<Room>(), Lines = Store.All<QtyLine>(), Subcontractors = Store.All<Subcontractor>(), Allocations = Store.All<Allocation>(),
            Wirs = Store.All<Wir>(), WirLines = Store.All<WirLine>(), Invoices = Store.All<Invoice>(), InvoiceLines = Store.All<InvoiceLine>(),
            PurchaseOrders = Store.All<PurchaseOrder>(), PoLines = Store.All<PoLine>(), DeliveryNotes = Store.All<DeliveryNote>(), DnLines = Store.All<DnLine>(),
            BoqItems = Store.All<BoqItem>(), Contracts = Store.All<Contract>(), AconexDocs = Store.All<AconexDoc>(), Imports = Store.All<ImportBatch>(),
            LoadedAt = DateTime.Now,
        };
        Chain = ChainBuilder.Build(Snapshot, Engine);
        ChainById = Chain.ToDictionary(c => c.Id);
        Queue = NeedsTodayQueue.Build(Snapshot, Chain, Options);
        if (DateTime.TryParse(Store.GetMeta("ProjectStart"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ps)) ProjectStart = ps;
        else if (Snapshot.Wirs.Count > 0) ProjectStart = Snapshot.Wirs.Min(w => w.SubmittedAt).AddDays(-14);
        if (DateTime.TryParse(Store.GetMeta("PlannedFinish"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pf)) PlannedFinish = pf;
        LastLoad = DateTime.Now;
        Changed?.Invoke();
    }

    public void ResetData(bool seedDemo)
    {
        Store.ClearAll();
        if (seedDemo) new DemoSeeder(DateTime.Today).Seed(Store);
        else Store.LogEvent("RESET", "Data file cleared - starting empty");
        Reload();
    }

    // ------------------------------------------------------------------ reads used by the UI

    public QtyLine? GetLine(long id) => Store.Get<QtyLine>(id);
    public List<AuditEntry> RecentActivity(int take = 50, DateTime? since = null) => Store.RecentAudit(take, since);
    public void Heartbeat(string screen) => Store.Heartbeat(screen);
    public List<PresenceRow> OthersOnline(TimeSpan window) => Store.OthersOnline(window);

    // ------------------------------------------------------------------ audited actions

    public void ApproveWir(Wir wir, bool approve)
    {
        wir.Status = approve ? WirStatus.Approved : WirStatus.Rejected;
        wir.ApprovedAt = DateTime.Today;
        Store.Update(wir, $"{(approve ? "Approved" : "Rejected")} {wir.WirNo} ({wir.Subcontractor} {wir.Level} {wir.System} {wir.Stage})");
        Reload();
    }

    public void SetInvoiceStatus(Invoice inv, string status, string? note = null)
    {
        inv.Status = status;
        if (note != null) inv.Notes = note;
        Store.Update(inv, $"{inv.Subcontractor} {inv.InvoiceNo} marked {status}");
        Reload();
    }

    /// <summary>Certifies a statement at the certifiable quantities (min of claimed, WIR, cap).</summary>
    public void Certify(ClaimDraft draft)
    {
        if (draft.Statement is null) throw new InvalidOperationException("No statement to certify.");
        var inv = draft.Statement;
        var byLine = draft.Lines.ToDictionary(l => l.Row.Id);
        var lines = Snapshot.InvoiceLines.Where(l => l.InvoiceId == inv.Id).ToList();
        Store.Batch(w =>
        {
            foreach (var l in lines)
                if (byLine.TryGetValue(l.LineId, out var cl)) { l.CertifiedQty = cl.Certifiable; w.Update(l); }
            inv.Status = InvoiceStatus.Certified;
            inv.CertifiedAt = DateTime.Now;
            inv.CertifiedAmount = Math.Round(draft.GrossToDate, 2);
            w.Update(inv);
        }, $"Certified {inv.Subcontractor} {inv.InvoiceNo}: SAR {draft.GrossToDate:N2} to date, held SAR {draft.HeldValue:N2}");
        Reload();
    }

    public void UpdateLine(QtyLine line, string summary)
    {
        Store.Update(line, summary);
        Reload();
    }

    public void UpdateBoq(BoqItem item)
    {
        Store.Update(item, $"BOQ {item.ItemCode}: PROJECT QTY {item.ProjectQty?.ToString("N0") ?? "-"}, RATE {item.Rate:N2}");
        Reload();
    }

    /// <summary>Fills PROJECT QTY on the given lines (values already set on the entities) in one atomic, concurrency-checked batch.</summary>
    public void FillProjectQty(IReadOnlyCollection<QtyLine> lines)
    {
        Store.Batch(w => { foreach (var l in lines) w.Update(l); }, $"PROJECT QTY filled on {lines.Count} lines from BOQ");
        Reload();
    }

    public int CommitImport(ImportPreview preview)
    {
        var n = preview.Commit(Store);
        Reload();
        return n;
    }

    public void MarkDownloaded(IEnumerable<(string DocNo, string Path)> files)
    {
        var map = Snapshot.AconexDocs.ToDictionary(d => d.DocNo, StringComparer.OrdinalIgnoreCase);
        var list = files.ToList();
        Store.Batch(w =>
        {
            foreach (var (no, path) in list)
                if (map.TryGetValue(no, out var d)) { d.DownloadedAt = DateTime.Now; d.LocalPath = path; d.Queued = false; w.Update(d); }
        }, $"Aconex: {list.Count} documents registered as downloaded");
        Reload();
    }

    public void SetQueued(IEnumerable<AconexDoc> docs, bool queued)
    {
        var list = docs.ToList();
        Store.Batch(w => { foreach (var d in list) { d.Queued = queued; w.Update(d); } }, $"Aconex queue: {(queued ? "added" : "removed")} {list.Count} documents");
        Reload();
    }

    public IEnumerable<ChainRow> Filter(FilterSpec f) => f.Apply(Chain);
}
