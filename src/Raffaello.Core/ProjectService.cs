using System.Globalization;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;
using Raffaello.Core.Seed;
using Raffaello.Core.Settings;

namespace Raffaello.Core;

/// <summary>
/// The app's single entry point into the data: opens the store, seeds demo data on first run,
/// keeps the in-memory snapshot + chain + queue current, and performs audited writes.
/// </summary>
public sealed class ProjectService
{
    public AppSettings Settings { get; }
    public Db Db { get; private set; }
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

    public ProjectService(AppSettings settings)
    {
        Settings = settings;
        Db = new Db(settings.DataFilePath, settings.EffectiveUserName);
        ApplyRuleSettings();
        Engine = new RulesEngine(Options);
    }

    public void ApplyRuleSettings()
    {
        Options.WirDueDays = Settings.WirDueDays;
        Options.SiteTolerance = Settings.SiteTolerance;
        Options.PipeLengthM = Settings.PipeLengthM;
        Options.Today = DateTime.Today;
    }

    /// <summary>Opens (or creates) the data file. Seeds demo data when empty and allowed.</summary>
    public void Initialize(Action<string>? progress = null)
    {
        progress?.Invoke("Opening data file...");
        Db = new Db(Settings.DataFilePath, Settings.EffectiveUserName);
        Db.EnsureSchema();
        if (Db.Count<QtyLine>() == 0 && Settings.SeedDemoData)
        {
            progress?.Invoke("First run: building the demo project...");
            new DemoSeeder(DateTime.Today).Seed(Db);
        }
        progress?.Invoke("Building the chain...");
        Reload();
    }

    public void Reload()
    {
        ApplyRuleSettings();
        Engine = new RulesEngine(Options);
        Snapshot = ProjectSnapshot.Load(Db);
        Chain = ChainBuilder.Build(Snapshot, Engine);
        ChainById = Chain.ToDictionary(c => c.Id);
        Queue = NeedsTodayQueue.Build(Snapshot, Chain, Options);
        if (DateTime.TryParse(Db.GetMeta("ProjectStart"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ps)) ProjectStart = ps;
        else if (Snapshot.Wirs.Count > 0) ProjectStart = Snapshot.Wirs.Min(w => w.SubmittedAt).AddDays(-14);
        if (DateTime.TryParse(Db.GetMeta("PlannedFinish"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pf)) PlannedFinish = pf;
        LastLoad = DateTime.Now;
        Changed?.Invoke();
    }

    public void ResetData(bool seedDemo)
    {
        Db.ClearAll();
        if (seedDemo) new DemoSeeder(DateTime.Today).Seed(Db);
        else Db.LogEvent("RESET", "Data file cleared - starting empty");
        Reload();
    }

    // ------------------------------------------------------------------ audited actions

    public void ApproveWir(Wir wir, bool approve)
    {
        var fresh = Db.Get<Wir>(wir.Id) ?? throw new InvalidOperationException("WIR no longer exists.");
        if (fresh.RowVersion != wir.RowVersion) throw new ConcurrencyException("Wirs", wir.Id, fresh.UpdatedBy);
        fresh.Status = approve ? WirStatus.Approved : WirStatus.Rejected;
        fresh.ApprovedAt = DateTime.Today;
        Db.Update(fresh, $"{(approve ? "Approved" : "Rejected")} {fresh.WirNo} ({fresh.Subcontractor} {fresh.Level} {fresh.System} {fresh.Stage})");
        Reload();
    }

    public void SetInvoiceStatus(Invoice inv, string status, string? note = null)
    {
        var fresh = Db.Get<Invoice>(inv.Id) ?? throw new InvalidOperationException("Invoice no longer exists.");
        if (fresh.RowVersion != inv.RowVersion) throw new ConcurrencyException("Invoices", inv.Id, fresh.UpdatedBy);
        fresh.Status = status;
        if (note != null) fresh.Notes = note;
        Db.Update(fresh, $"{fresh.Subcontractor} {fresh.InvoiceNo} marked {status}");
        Reload();
    }

    /// <summary>Certifies a statement at the certifiable quantities (min of claimed, WIR, cap).</summary>
    public void Certify(ClaimDraft draft)
    {
        if (draft.Statement is null) throw new InvalidOperationException("No statement to certify.");
        var inv = Db.Get<Invoice>(draft.Statement.Id) ?? throw new InvalidOperationException("Statement no longer exists.");
        if (inv.RowVersion != draft.Statement.RowVersion) throw new ConcurrencyException("Invoices", inv.Id, inv.UpdatedBy);
        var byLine = draft.Lines.ToDictionary(l => l.Row.Id);
        var lines = Db.All<InvoiceLine>("InvoiceId=@Id", new { inv.Id });
        Db.InTransaction(w =>
        {
            foreach (var l in lines)
                if (byLine.TryGetValue(l.LineId, out var cl))
                    w.Execute("UPDATE InvoiceLines SET CertifiedQty=@Q, UpdatedBy=@By, UpdatedAt=@At, RowVersion=RowVersion+1 WHERE Id=@Id", new { Q = cl.Certifiable, By = Db.User, At = DateTime.Now, l.Id });
            w.Execute("UPDATE Invoices SET Status=@S, CertifiedAt=@At, CertifiedAmount=@Amt, UpdatedBy=@By, UpdatedAt=@At, RowVersion=RowVersion+1 WHERE Id=@Id AND RowVersion=@V",
                new { S = InvoiceStatus.Certified, At = DateTime.Now, Amt = Math.Round(draft.GrossToDate, 2), By = Db.User, inv.Id, V = inv.RowVersion });
        }, $"Certified {inv.Subcontractor} {inv.InvoiceNo}: SAR {draft.GrossToDate:N2} to date, held SAR {draft.HeldValue:N2}");
        Reload();
    }

    public void UpdateLine(QtyLine line, string summary)
    {
        Db.Update(line, summary);
        Reload();
    }

    public void UpdateBoq(BoqItem item)
    {
        Db.Update(item, $"BOQ {item.ItemCode}: PROJECT QTY {item.ProjectQty?.ToString("N0") ?? "-"}, RATE {item.Rate:N2}");
        Reload();
    }

    public void MarkDownloaded(IEnumerable<(string DocNo, string Path)> files)
    {
        var map = Snapshot.AconexDocs.ToDictionary(d => d.DocNo, StringComparer.OrdinalIgnoreCase);
        var list = files.ToList();
        Db.InTransaction(w =>
        {
            foreach (var (no, path) in list)
                if (map.TryGetValue(no, out var d))
                    w.Execute("UPDATE AconexDocs SET DownloadedAt=@At, LocalPath=@P, Queued=0, UpdatedBy=@By, UpdatedAt=@At, RowVersion=RowVersion+1 WHERE Id=@Id", new { At = DateTime.Now, P = path, By = Db.User, d.Id });
        }, $"Aconex: {list.Count} documents registered as downloaded");
        Reload();
    }

    public void SetQueued(IEnumerable<AconexDoc> docs, bool queued)
    {
        var ids = docs.Select(d => d.Id).ToList();
        Db.InTransaction(w =>
        {
            foreach (var id in ids) w.Execute("UPDATE AconexDocs SET Queued=@Q, UpdatedBy=@By, UpdatedAt=@At, RowVersion=RowVersion+1 WHERE Id=@Id", new { Q = queued, By = Db.User, At = DateTime.Now, Id = id });
        }, $"Aconex queue: {(queued ? "added" : "removed")} {ids.Count} documents");
        Reload();
    }

    public IEnumerable<ChainRow> Filter(FilterSpec f) => f.Apply(Chain);
}
