using System.Globalization;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Cables;

/// <summary>Result of preparing claims for import: what will be written, and the flags it raises.</summary>
public sealed class CableClaimPlan
{
    public List<CableClaim> Claims { get; } = new();
    public List<CableRun> NewRuns { get; } = new();
    public List<CablePanel> NewPanels { get; } = new();
    /// <summary>Stored claims that get their ledger link filled in (sheet row = ledger line).</summary>
    public List<CableClaim> Relinked { get; } = new();
    public int SkippedExisting { get; set; }
    public int LinkedToLedger { get; set; }
    public List<CableFlag> Flags { get; } = new();
    public List<string> Notes { get; } = new();
    public string Summary =>
        $"{Claims.Count} cable claims ({SkippedExisting} already imported, {LinkedToLedger} matched to ledger lines), {NewRuns.Count} new runs ({NewRuns.Count(r => r.Status == CableStatus.Provisional)} provisional), " +
        $"{NewPanels.Count} new panels; flags: {CableFlagEngine.Summary(Flags)}";
}

/// <summary>Result of saving runs / panels read from a schedule, an SLD or a drawing.</summary>
public sealed record RegisterMerge(int RunsAdded, int RunsUpdated, int ProvisionalUpgraded, int PanelsAdded, int PanelsUpdated, List<string> Conflicts);

/// <summary>
/// [cables] Register and claim operations, written once against <see cref="ICableStore"/> (SQLite file or server).
/// Claims are matched to runs through the panel-name normaliser; claims without a design run create PROVISIONAL runs
/// ("from statements only - no design length") so FROM-TO duplicates across statements and invoices are still caught.
/// </summary>
public sealed class CableService
{
    public ICableStore Store { get; }
    public CableOptions Options { get; set; } = CableOptions.Default;
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public CableService(ICableStore store) => Store = store;

    public CableSnapshot Load() => Store.Load();

    public List<CableFlag> Flags(CableSnapshot? snap = null) => CableFlagEngine.Evaluate(snap ?? Load(), Options);

    // =================================================================== claims

    /// <summary>
    /// Normalises names and sizes, links each claim to its run (earth 1xN on a multi-core route = companion), creates provisional runs and
    /// panels for unknown routes, pairs tracker CABLES rows with the ledger CABLE PULLING lines that carry the same claim, and evaluates the flags.
    /// Nothing is written: call <see cref="Commit"/>.
    /// </summary>
    public CableClaimPlan Prepare(IEnumerable<CableClaim> incoming, IEnumerable<CableClaim>? ledgerClaims = null, CableSnapshot? snap = null)
    {
        snap ??= Load();
        var plan = new CableClaimPlan();
        var known = snap.Claims.Select(c => c.SourceKey).Where(k => k.Length > 0).ToHashSet();
        var knownLedger = snap.Claims.Select(c => c.LedgerSourceKey).Where(k => k.Length > 0).ToHashSet();
        var resolver = snap.Resolver();
        var panels = snap.Panels.ToDictionary(p => p.Key);
        var runs = snap.Runs.ToList();
        var tempId = -1L;

        var batch = new List<CableClaim>();
        foreach (var c in incoming)
        {
            if (c.SourceKey.Length > 0 && (known.Contains(c.SourceKey) || batch.Any(b => b.SourceKey == c.SourceKey))) { plan.SkippedExisting++; continue; }
            Normalise(c, resolver);
            batch.Add(c);
        }

        // ledger CABLE PULLING lines: the same claim as a CABLES row -> link, else a claim of its own
        if (ledgerClaims != null)
        {
            var open = batch.Concat(snap.Claims).Where(c => c.LedgerSourceKey.Length == 0).ToList();
            foreach (var l in ledgerClaims)
            {
                if (knownLedger.Contains(l.LedgerSourceKey) || (l.SourceKey.Length > 0 && known.Contains(l.SourceKey))) { plan.SkippedExisting++; continue; }
                Normalise(l, resolver);
                var twin = open.FirstOrDefault(c => SameClaim(c, l));
                if (twin != null)
                {
                    twin.LedgerSourceKey = l.LedgerSourceKey;
                    open.Remove(twin);
                    plan.LinkedToLedger++;
                    if (twin.Id > 0) plan.Relinked.Add(twin);
                    continue;
                }
                // a ledger line that totals several sheet rows (e.g. LOCATION = HOTEL, one line per size and invoice)
                var group = open.Where(c => string.Equals(c.Subcontractor, l.Subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo == l.InvoiceNo
                                            && c.SizeKey == l.SizeKey && c.Stage == l.Stage
                                            && (string.Equals(l.Location, c.Building, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Location, c.Location, StringComparison.OrdinalIgnoreCase))).ToList();
                if (group.Count > 1 && Math.Abs(group.Sum(c => c.Qty) - l.Qty) <= 0.5)
                {
                    foreach (var c in group)
                    {
                        c.LedgerSourceKey = l.LedgerSourceKey;
                        open.Remove(c);
                        if (c.Id > 0) plan.Relinked.Add(c);
                    }
                    plan.LinkedToLedger++;
                    plan.Notes.Add($"ledger line {l.Subcontractor} INV {l.InvoiceNo} {l.Location} {l.SizeKey} {l.Qty:0.##} m = total of {group.Count} cable rows");
                    continue;
                }
                batch.Add(l);
            }
        }

        // phase cables first, so a 1xN on the same route becomes the earth companion of that run
        var routeHasMulti = batch.Concat(snap.Claims).Where(c => CableSize.Parse(c.SizeKey).Cores > 1)
            .Select(c => c.Route).ToHashSet();
        foreach (var c in batch.OrderBy(c => CableSize.Parse(c.SizeKey).Cores == 1 ? 1 : 0))
        {
            var size = CableSize.Parse(c.SizeKey);
            var routeRuns = runs.Where(r => r.RouteKey == c.Route && c.Route.Length > 2).ToList();
            CableRun? run = routeRuns.FirstOrDefault(r => r.SizeKey == c.SizeKey && r.Status != CableStatus.Rejected);
            if (run is null && size.Cores == 1 && !size.SingleCoreBundle)
            {
                run = routeRuns.FirstOrDefault(r => r.EarthSizeKey == c.SizeKey && r.Status != CableStatus.Rejected)
                      ?? (routeHasMulti.Contains(c.Route) || size.IsEarthOnly ? routeRuns.Where(r => CableSize.Parse(r.SizeKey).Cores > 1 && r.Status != CableStatus.Rejected).OrderByDescending(r => r.SizeMm2).FirstOrDefault() : null);
                if (run != null || size.IsEarthOnly) c.IsEarth = run != null || size.IsEarthOnly;
                if (run != null && run.Id <= 0 && run.EarthSizeKey.Length == 0) run.EarthSizeKey = c.SizeKey;
            }
            if (run is null && c.FromKey.Length > 0 && c.ToKey.Length > 0)
            {
                run = new CableRun
                {
                    Id = tempId--, Building = c.Building, Level = c.Level, FromKey = c.FromKey, FromName = PanelNames.Tidy(c.FromRaw), ToKey = c.ToKey, ToName = PanelNames.Tidy(c.ToRaw),
                    Cores = size.Cores, SizeMm2 = size.Mm2, SizeKey = c.SizeKey, Conductor = size.Conductor.Length > 0 ? size.Conductor : "CU", Insulation = size.Insulation,
                    EarthSizeKey = size.EarthKey, Status = CableStatus.Provisional, SourceKind = CableSources.Statement, SourceDoc = c.Source + " " + c.Subcontractor + " INV " + c.InvoiceNo,
                    Confidence = 0, Notes = "from statements only - no design length",
                };
                runs.Add(run);
                plan.NewRuns.Add(run);
            }
            c.RunId = run?.Id;
            c.MatchStatus = run is null ? "UNKNOWN" : run.Status == CableStatus.Provisional ? "PROVISIONAL" : "MATCHED";
            c.MatchScore = run is null ? 0 : run.Status == CableStatus.Provisional ? 0.5 : 1;
            foreach (var (key, raw) in new[] { (c.FromKey, c.FromRaw), (c.ToKey, c.ToRaw) })
                if (key.Length > 0 && !panels.ContainsKey(key))
                {
                    var p = NewPanel(raw, key, CableSources.Statement, c.Source + " " + c.Subcontractor + " INV " + c.InvoiceNo, CableStatus.Provisional, 0, c.Building);
                    if (p.Building.Length == 0) p.Building = c.Building;
                    if (p.Level.Length == 0) p.Level = c.Level;
                    panels[key] = p;
                    plan.NewPanels.Add(p);
                }
            plan.Claims.Add(c);
        }
        // provisional parent: FROM feeds TO
        foreach (var r in plan.NewRuns)
            if (panels.TryGetValue(r.ToKey, out var to) && to.ParentKey.Length == 0 && to.Id <= 0) to.ParentKey = r.FromKey;

        var preview = new CableSnapshot { Panels = snap.Panels.Concat(plan.NewPanels).ToList(), Aliases = snap.Aliases, Runs = runs, Claims = snap.Claims, Decisions = snap.Decisions };
        plan.Flags.AddRange(CableFlagEngine.For(preview, plan.Claims, Options));
        return plan;
    }

    /// <summary>A tracker CABLES row and a ledger CABLE PULLING line are the same claim (sub, invoice, size, qty, panels).</summary>
    public static bool SameClaim(CableClaim sheet, CableClaim ledger)
    {
        if (!string.Equals(sheet.Subcontractor, ledger.Subcontractor, StringComparison.OrdinalIgnoreCase) || sheet.InvoiceNo != ledger.InvoiceNo) return false;
        if (sheet.SizeKey != ledger.SizeKey || Math.Abs(sheet.Qty - ledger.Qty) > 0.01 || sheet.Stage != ledger.Stage) return false;
        var keys = new[] { sheet.FromKey, sheet.ToKey };
        var a = ledger.FromKey; var b = ledger.ToKey;
        return (a.Length == 0 || keys.Contains(a)) && (b.Length == 0 || keys.Contains(b)) && (a.Length > 0 || b.Length > 0);
    }

    private static void Normalise(CableClaim c, PanelResolver resolver)
    {
        c.Subcontractor = (c.Subcontractor ?? "").Trim().ToUpperInvariant();
        c.Stage = CableStages.Normalize(c.RawStage.Length > 0 ? c.RawStage : c.Stage);
        var f = resolver.Resolve(c.FromRaw, c.Building);
        var t = resolver.Resolve(c.ToRaw, c.Building);
        c.FromKey = f.Key;
        c.ToKey = t.Key;
        var size = CableSize.Parse(c.SizeRaw.Length > 0 ? c.SizeRaw : c.SizeKey);
        c.SizeKey = size.IsValid ? size.Key : CableSize.KeyOf(c.SizeRaw);
        if (size.IsEarthOnly) c.IsEarth = true;
        if (c.EnteredAt == default) c.EnteredAt = DateTime.Now;
    }

    public static CablePanel NewPanel(string raw, string key, string sourceKind, string sourceDoc, string status, int page = 0, string? building = null)
    {
        var p = PanelNames.Parse(raw, building);
        return new CablePanel
        {
            Name = PanelNames.Tidy(raw), Key = key.Length > 0 ? key : p.Key, Type = p.Type, Building = PanelNames.BuildingOf(p), Zone = p.Zone, Level = p.Level,
            Suffix = string.Join("-", p.Suffix), IsEquipment = !p.IsPanelType, Status = status, SourceKind = sourceKind, SourceDoc = sourceDoc, SourcePage = page,
        };
    }

    /// <summary>Writes the plan in one transaction (new panels, runs with their temporary ids re-pointed, claims, ledger relinks).</summary>
    public void Commit(CableClaimPlan plan, string summary)
    {
        if (plan.Claims.Count == 0 && plan.Relinked.Count == 0) return;
        Store.Batch(w =>
        {
            foreach (var p in plan.NewPanels) { p.Id = 0; w.Insert(p); }
            var map = new Dictionary<long, long>();
            foreach (var r in plan.NewRuns)
            {
                var temp = r.Id;
                r.Id = 0;
                w.Insert(r);
                map[temp] = r.Id;
                if (r.Ref.Length == 0) { r.Ref = "C-" + Math.Abs(r.Id).ToString("0000", CultureInfo.InvariantCulture); w.Update(r); }
            }
            foreach (var c in plan.Claims)
            {
                if (c.RunId is long id && map.TryGetValue(id, out var real)) c.RunId = real;
                w.Insert(c);
            }
            foreach (var c in plan.Relinked) w.Update(c);
        }, summary);
    }

    /// <summary>Prepare + commit in one call (CLI, hooks).</summary>
    public CableClaimPlan Import(IEnumerable<CableClaim> incoming, IEnumerable<CableClaim>? ledgerClaims, string summary)
    {
        var plan = Prepare(incoming, ledgerClaims);
        Commit(plan, summary + ": " + plan.Summary);
        return plan;
    }

    /// <summary>A ledger CABLE PULLING line as a cable claim (LOCATION = FROM panel, NOTES = TO, ITEM = size; NOTES empty -> LOCATION is the TO end).</summary>
    public static CableClaim FromLedger(ClaimLine l)
    {
        var to = l.Notes.Trim();
        var useNotes = to.Length > 0 && !to.Equals("REWORK", StringComparison.OrdinalIgnoreCase) && PanelNames.Parse(to).Tokens.Count > 0 && !CableSize.Contains(to);
        return new CableClaim
        {
            Building = l.Building, Subcontractor = l.Subcontractor, InvoiceNo = l.InvoiceNo, StatementNo = l.StatementNo, Stage = CableStages.Pulling, RawStage = l.Stage,
            Location = l.Room, Level = l.Floor, FromRaw = useNotes ? l.Room : "", ToRaw = useNotes ? to : l.Room, SizeRaw = l.Item, Qty = l.Qty, SitePct = l.SitePct, WirPct = l.WirPct,
            WirNo = l.WirNo, Notes = useNotes ? "" : l.Notes, Source = "LEDGER", SourceKey = "LEDGER|" + (l.SourceKey.Length > 0 ? l.SourceKey : "#" + l.Id),
            LedgerSourceKey = l.SourceKey.Length > 0 ? l.SourceKey : "#" + l.Id, EnteredAt = l.EnteredAt == default ? DateTime.Now : l.EnteredAt,
        };
    }

    /// <summary>The ledger line a PULLING cable claim posts (same shape as the tracker's CABLE PULLING lines, so the existing mapping invoices it).</summary>
    public static ClaimLine ToLedgerLine(CableClaim c)
    {
        var key = "CBL|" + c.SourceKey;
        c.LedgerSourceKey = key;
        return new ClaimLine
        {
            Building = c.Building, Subcontractor = c.Subcontractor, InvoiceNo = c.InvoiceNo, Stage = CableStages.LedgerPulling, Floor = c.Level, Room = PanelNames.Tidy(c.FromRaw),
            Item = c.SizeKey, Unit = "m", Qty = c.Qty, SitePct = c.SitePct, WirPct = c.WirPct, WirNo = c.WirNo, Notes = PanelNames.Tidy(c.ToRaw), Source = c.Source,
            SourceKey = key, StatementNo = c.StatementNo, EnteredAt = c.EnteredAt == default ? DateTime.Now : c.EnteredAt,
        };
    }

    public CableClaim AddClaim(CableClaim c, out List<CableFlag> flags)
    {
        if (c.SourceKey.Length == 0) c.SourceKey = "MANUAL|" + Guid.NewGuid().ToString("N");
        if (c.Source.Length == 0) c.Source = CableSources.Manual;
        var plan = Prepare(new[] { c });
        flags = plan.Flags;
        Commit(plan, $"Cable claim {c.Subcontractor} INV {c.InvoiceNo} {c.FromRaw} -> {c.ToRaw} {c.SizeKey} {c.Qty:0.##} m ({CableFlagEngine.Summary(flags)})");
        return c;
    }

    // =================================================================== flags

    /// <summary>Lets a flag through with a reason (audited). Warnings never block; the reason is shown wherever the flag appears.</summary>
    public CableFlagDecision Bypass(CableFlag flag, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required to bypass a cable flag.");
        var d = new CableFlagDecision { FlagKey = flag.Key, Code = flag.Code, ClaimId = flag.Claim.Id, Reason = reason.Trim(), By = Store.User, At = Clock() };
        return Store.Insert(d, $"Cable flag bypassed: {flag.Code} on {flag.ClaimText} - {reason.Trim()}");
    }

    /// <summary>Bypasses flags of claims not yet saved (statement preview): stored by flag key, so they apply once the claims are posted.</summary>
    public void BypassAll(IEnumerable<CableFlag> flags, string reason)
    {
        var list = flags.Where(f => !f.IsBypassed).ToList();
        if (list.Count == 0) return;
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required to bypass cable flags.");
        Store.Batch(w =>
        {
            foreach (var f in list) w.Insert(new CableFlagDecision { FlagKey = f.Key, Code = f.Code, ClaimId = f.Claim.Id, Reason = reason.Trim(), By = Store.User, At = Clock() });
        }, $"{list.Count} cable flags bypassed ({CableFlagEngine.Summary(list)}): {reason.Trim()}");
    }

    // =================================================================== register

    /// <summary>
    /// Saves runs / panels read from a schedule, SLD or drawing. A run on the same route + size updates the existing one: a PROVISIONAL run
    /// (from statements) is upgraded in place, so its claims keep their link; a confirmed run keeps its values and a differing length is reported.
    /// </summary>
    public RegisterMerge SaveRegister(IReadOnlyList<CableRun> runs, IReadOnlyList<CablePanel> panels, string summary, bool confirm = false)
    {
        var snap = Load();
        var resolver = snap.Resolver();
        var byKey = snap.Panels.ToDictionary(p => p.Key);
        int added = 0, updated = 0, upgraded = 0, pAdded = 0, pUpdated = 0;
        var conflicts = new List<string>();
        var newPanels = new List<CablePanel>();
        var changedPanels = new List<CablePanel>();
        var newRuns = new List<CableRun>();
        var changedRuns = new List<CableRun>();

        CablePanel Panel(string raw, string building, CablePanel? template)
        {
            var key = template is { Key.Length: > 0 } ? resolver.Canonical(template.Key) : resolver.Resolve(raw, building).Key;
            if (byKey.TryGetValue(key, out var p))
            {
                if (template != null && p.Status == CableStatus.Provisional)
                {
                    p.Status = confirm ? CableStatus.Confirmed : CableStatus.Proposed;
                    if (template.Name.Length > 0) p.Name = template.Name;   // the design spelling replaces the statement spelling
                    p.SourceKind = template.SourceKind; p.SourceDoc = template.SourceDoc; p.SourcePage = template.SourcePage;
                    if (template.ParentKey.Length > 0) p.ParentKey = resolver.Canonical(template.ParentKey);
                    if (!changedPanels.Contains(p) && p.Id > 0) changedPanels.Add(p);
                }
                else if (template != null && p.ParentKey.Length == 0 && template.ParentKey.Length > 0)
                {
                    p.ParentKey = resolver.Canonical(template.ParentKey);
                    if (!changedPanels.Contains(p) && p.Id > 0) changedPanels.Add(p);
                }
                return p;
            }
            p = template ?? NewPanel(raw, key, "", "", confirm ? CableStatus.Confirmed : CableStatus.Proposed, 0, building);
            p.Key = key;
            if (p.Name.Length == 0) p.Name = PanelNames.Tidy(raw);
            if (template != null && template.ParentKey.Length > 0) p.ParentKey = resolver.Canonical(template.ParentKey);
            byKey[key] = p;
            newPanels.Add(p);
            return p;
        }

        foreach (var p in panels) Panel(p.Name.Length > 0 ? p.Name : p.Key, p.Building, p);
        var existing = snap.Runs.ToList();
        foreach (var r in runs)
        {
            var from = r.FromKey.Length > 0 && byKey.TryGetValue(resolver.Canonical(r.FromKey), out var fp) ? fp : Panel(r.FromName.Length > 0 ? r.FromName : r.FromKey, r.Building, null);
            var to = r.ToKey.Length > 0 && byKey.TryGetValue(resolver.Canonical(r.ToKey), out var tp) ? tp : Panel(r.ToName.Length > 0 ? r.ToName : r.ToKey, r.Building, null);
            if (to.ParentKey.Length == 0 && to.Key != from.Key) { to.ParentKey = from.Key; if (to.Id > 0 && !changedPanels.Contains(to)) changedPanels.Add(to); }
            r.FromKey = from.Key; r.ToKey = to.Key;
            if (r.FromName.Length == 0) r.FromName = from.Name;
            if (r.ToName.Length == 0) r.ToName = to.Name;
            var size = CableSize.Parse(r.SizeKey);
            if (size.IsValid) { r.SizeKey = size.Key; if (r.Cores == 0) r.Cores = size.Cores; if (r.SizeMm2 == 0) r.SizeMm2 = size.Mm2; if (r.EarthSizeKey.Length == 0) r.EarthSizeKey = size.EarthKey; }
            if (confirm) r.Status = CableStatus.Confirmed;
            var same = existing.FirstOrDefault(x => x.RouteKey == r.RouteKey && x.SizeKey == r.SizeKey);
            if (same is null)
            {
                newRuns.Add(r);
                existing.Add(r);
                added++;
                continue;
            }
            if (same.Status == CableStatus.Provisional)
            {
                same.Status = r.Status == CableStatus.Provisional ? CableStatus.Proposed : r.Status;
                same.DesignLength = r.DesignLength ?? same.DesignLength;
                same.MeasuredLength = r.MeasuredLength ?? same.MeasuredLength;
                same.FromKey = r.FromKey; same.FromName = r.FromName; same.ToKey = r.ToKey; same.ToName = r.ToName;
                same.Conductor = r.Conductor; same.Insulation = r.Insulation.Length > 0 ? r.Insulation : same.Insulation;
                if (r.EarthSizeKey.Length > 0) same.EarthSizeKey = r.EarthSizeKey;
                same.CircuitRef = r.CircuitRef; same.Breaker = r.Breaker; same.SourceKind = r.SourceKind; same.SourceDoc = r.SourceDoc; same.SourcePage = r.SourcePage;
                same.Confidence = r.Confidence; same.Notes = r.Notes;
                if (r.Ref.Length > 0) same.Ref = r.Ref;
                if (same.Id > 0 && !changedRuns.Contains(same)) changedRuns.Add(same);
                upgraded++;
                continue;
            }
            var changed = false;
            if (same.DesignLength is null && r.DesignLength is > 0) { same.DesignLength = r.DesignLength; changed = true; }
            else if (r.DesignLength is > 0 && same.DesignLength is > 0 && Math.Abs(same.DesignLength.Value - r.DesignLength.Value) > 0.5)
                conflicts.Add($"{same.Title}: register {same.DesignLength:0.##} m, {r.SourceKind} {r.SourceDoc} says {r.DesignLength:0.##} m - kept the register value.");
            if (same.MeasuredLength is null && r.MeasuredLength is > 0) { same.MeasuredLength = r.MeasuredLength; changed = true; }
            if (same.EarthSizeKey.Length == 0 && r.EarthSizeKey.Length > 0) { same.EarthSizeKey = r.EarthSizeKey; changed = true; }
            if (same.CircuitRef.Length == 0 && r.CircuitRef.Length > 0) { same.CircuitRef = r.CircuitRef; changed = true; }
            if (same.Breaker.Length == 0 && r.Breaker.Length > 0) { same.Breaker = r.Breaker; changed = true; }
            if (confirm && same.Status != CableStatus.Confirmed) { same.Status = CableStatus.Confirmed; changed = true; }
            if (changed) { if (same.Id > 0 && !changedRuns.Contains(same)) changedRuns.Add(same); updated++; }
        }
        pAdded = newPanels.Count;
        pUpdated = changedPanels.Count;
        if (newRuns.Count + changedRuns.Count + newPanels.Count + changedPanels.Count == 0) return new(0, 0, 0, 0, 0, conflicts);
        Store.Batch(w =>
        {
            foreach (var p in newPanels) { p.Id = 0; w.Insert(p); }
            foreach (var p in changedPanels) w.Update(p);
            foreach (var r in newRuns)
            {
                r.Id = 0;
                w.Insert(r);
                if (r.Ref.Length == 0) { r.Ref = "C-" + r.Id.ToString("0000", CultureInfo.InvariantCulture); w.Update(r); }
            }
            foreach (var r in changedRuns) w.Update(r);
        }, $"{summary}: {added} runs added, {updated} updated, {upgraded} provisional runs confirmed by the design, {pAdded} panels added");
        return new(added, updated, upgraded, pAdded, pUpdated, conflicts);
    }

    /// <summary>
    /// Confirms that panel <paramref name="aliasKey"/> (another spelling) is the same as <paramref name="panelKey"/>: learns the alias and re-keys every
    /// run, claim and panel that used the alias key. Runs that become identical (same route + size) are merged and their claims re-pointed.
    /// </summary>
    public int ConfirmAlias(string aliasKey, string panelKey, string aliasDisplay = "", string note = "")
    {
        if (aliasKey.Length == 0 || aliasKey == panelKey) return 0;
        var snap = Load();
        var target = snap.Panels.FirstOrDefault(p => p.Key == panelKey) ?? throw new InvalidOperationException($"Panel {panelKey} is not in the register.");
        var aliasPanel = snap.Panels.FirstOrDefault(p => p.Key == aliasKey);
        var display = aliasDisplay.Length > 0 ? aliasDisplay : aliasPanel?.Name ?? aliasKey;
        var touched = 0;
        var runs = snap.Runs.ToList();
        Store.Batch(w =>
        {
            w.Insert(new CablePanelAlias { PanelKey = panelKey, Alias = display, AliasKey = aliasKey, Kind = "USER", ConfirmedBy = Store.User, ConfirmedAt = Clock() });
            foreach (var p in snap.Panels.Where(p => p.Key == aliasKey)) { w.Delete(p); touched++; }
            foreach (var p in snap.Panels.Where(p => p.ParentKey == aliasKey)) { p.ParentKey = panelKey; w.Update(p); }
            foreach (var r in runs.Where(r => r.FromKey == aliasKey || r.ToKey == aliasKey))
            {
                if (r.FromKey == aliasKey) { r.FromKey = panelKey; r.FromName = target.Name; }
                if (r.ToKey == aliasKey) { r.ToKey = panelKey; r.ToName = target.Name; }
                w.Update(r); touched++;
            }
            foreach (var c in snap.Claims.Where(c => c.FromKey == aliasKey || c.ToKey == aliasKey))
            {
                if (c.FromKey == aliasKey) c.FromKey = panelKey;
                if (c.ToKey == aliasKey) c.ToKey = panelKey;
                w.Update(c); touched++;
            }
            // runs that are now the same route + size -> keep the best one, move the claims
            foreach (var g in runs.GroupBy(r => (r.RouteKey, r.SizeKey)).Where(g => g.Count() > 1))
            {
                var keep = g.OrderBy(r => r.Status == CableStatus.Provisional ? 1 : 0).ThenBy(r => r.Status == CableStatus.Confirmed ? 0 : 1).ThenBy(r => r.Id).First();
                foreach (var dup in g.Where(r => r != keep))
                {
                    foreach (var c in snap.Claims.Where(c => c.RunId == dup.Id)) { c.RunId = keep.Id; w.Update(c); }
                    if (keep.DesignLength is null && dup.DesignLength is > 0) { keep.DesignLength = dup.DesignLength; w.Update(keep); }
                    w.Delete(dup);
                }
            }
        }, $"Panel alias confirmed: '{display}' = {target.Name}{(note.Length > 0 ? " (" + note + ")" : "")} - {touched} rows re-keyed");
        return touched;
    }

    /// <summary>"Not the same panel": the suggestion is not shown again.</summary>
    public void RejectAlias(string aliasKey, string panelKey, string aliasDisplay = "") =>
        Store.Insert(new CablePanelAlias { PanelKey = panelKey, Alias = aliasDisplay.Length > 0 ? aliasDisplay : aliasKey, AliasKey = aliasKey, Kind = "REJECTED", ConfirmedBy = Store.User, ConfirmedAt = Clock() },
            $"Panel alias rejected: '{(aliasDisplay.Length > 0 ? aliasDisplay : aliasKey)}' is not {panelKey}");

    /// <summary>Panel pairs the normaliser thinks are the same (score &gt;= threshold) and nobody decided yet - the confirm-alias queue.</summary>
    public static List<(CablePanel A, CablePanel B, double Score)> AliasSuggestions(CableSnapshot snap, double threshold = 0.8, int max = 200)
    {
        var rejected = snap.Aliases.Where(a => a.Kind == "REJECTED").Select(a => a.AliasKey + "=>" + a.PanelKey).ToHashSet();
        var parsed = snap.Panels.Select(p => (P: p, N: PanelNames.Parse(p.Name.Length > 0 ? p.Name : p.Key, p.Building))).ToList();
        var res = new List<(CablePanel, CablePanel, double)>();
        for (var i = 0; i < parsed.Count; i++)
            for (var j = i + 1; j < parsed.Count; j++)
            {
                var s = PanelNames.Similarity(parsed[i].N, parsed[j].N);
                if (s < threshold || s >= 1) continue;
                var (a, b) = (parsed[i].P, parsed[j].P);
                // the one with fewer claims / provisional is the alias of the other
                if (a.Status == CableStatus.Provisional && b.Status != CableStatus.Provisional) (a, b) = (b, a);
                if (rejected.Contains(b.Key + "=>" + a.Key) || rejected.Contains(a.Key + "=>" + b.Key)) continue;
                res.Add((a, b, s));
            }
        return res.OrderByDescending(x => x.Item3).Take(max).ToList();
    }

    public void UpdateRun(CableRun r, string summary) => Store.Update(r, summary);

    public void SetRunStatus(CableRun r, string status)
    {
        r.Status = status;
        Store.Update(r, $"Cable run {r.Ref} {r.Title}: {status}");
    }

    /// <summary>Links a claim to a run by hand (unknown-run queue). Earth when the claim size is the run's earth / single core on a multi-core run.</summary>
    public void AssignRun(CableClaim c, CableRun r)
    {
        c.RunId = r.Id;
        var size = CableSize.Parse(c.SizeKey);
        c.IsEarth = c.SizeKey != r.SizeKey && size.Cores == 1;
        c.MatchStatus = r.Status == CableStatus.Provisional ? "PROVISIONAL" : "MATCHED";
        c.MatchScore = 1;
        c.FromKey = r.FromKey; c.ToKey = r.ToKey;
        Store.Update(c, $"Cable claim {c.Subcontractor} INV {c.InvoiceNo} {c.FromRaw} -> {c.ToRaw} {c.SizeKey} linked to run {r.Ref} {r.Title}");
    }

    /// <summary>Re-matches claims that are UNKNOWN / PROVISIONAL against the current register (after an SLD / schedule import or an alias).</summary>
    public int Rematch()
    {
        var snap = Load();
        var resolver = snap.Resolver();
        var changed = new List<CableClaim>();
        foreach (var c in snap.Claims)
        {
            var current = snap.Run(c.RunId);
            if (current != null && current.Status != CableStatus.Provisional) continue;
            var fk = resolver.Canonical(c.FromKey.Length > 0 ? c.FromKey : PanelNames.KeyOf(c.FromRaw, c.Building));
            var tk = resolver.Canonical(c.ToKey.Length > 0 ? c.ToKey : PanelNames.KeyOf(c.ToRaw, c.Building));
            var route = CableKeys.Route(fk, tk);
            var run = snap.Runs.Where(r => r.RouteKey == route && r.Status is CableStatus.Proposed or CableStatus.Confirmed)
                .OrderByDescending(r => r.SizeKey == c.SizeKey).ThenByDescending(r => r.EarthSizeKey == c.SizeKey).FirstOrDefault(r => r.SizeKey == c.SizeKey || r.EarthSizeKey == c.SizeKey || (c.IsEarth && r.Cores > 1));
            if (run is null || run.Id == c.RunId) continue;
            c.RunId = run.Id; c.FromKey = fk; c.ToKey = tk; c.MatchStatus = "MATCHED"; c.MatchScore = 1;
            c.IsEarth = run.SizeKey != c.SizeKey;
            changed.Add(c);
        }
        if (changed.Count > 0) Store.Batch(w => { foreach (var c in changed) w.Update(c); }, $"Cable claims re-matched to the register: {changed.Count}");
        return changed.Count;
    }
}
