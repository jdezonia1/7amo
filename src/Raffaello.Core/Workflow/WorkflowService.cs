using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;
using Raffaello.Core.Statements;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Workflow;

/// <summary>
/// Audited operations of the real workflow (room ledger, checks, contracts, mapping rules, invoices, site statements).
/// View models call these; nothing above Core touches the store directly.
/// </summary>
public sealed class WorkflowService
{
    private readonly ProjectService _p;
    public WorkflowService(ProjectService project) => _p = project;

    public IReadOnlyDictionary<string, RoomBalance> Balances() => LedgerRules.Balances(_p.Snapshot.RoomQtys, _p.Snapshot.Claims);

    public RoomBalance Balance(string room, string stage, string item) => LedgerRules.Balance(_p.Snapshot.RoomQtys, _p.Snapshot.Claims, room, stage, item);

    // ------------------------------------------------------------------ ledger

    /// <summary>Posts a claim after the remaining check. Returns the decision; blocked claims are not written.</summary>
    public ClaimCheck AddClaim(ClaimLine line, string? overReason = null)
    {
        var bal = Balance(line.Room, line.Stage, line.Item);
        var check = LedgerRules.Check(bal, line.Qty, overReason);
        if (!check.CanPost) return check;
        line.IsOver = check.IsOver;
        line.OverReason = check.IsOver ? overReason!.Trim() : "";
        line.EnteredAt = DateTime.Now;
        if (string.IsNullOrEmpty(line.AreaType)) line.AreaType = _p.Snapshot.Rooms.FirstOrDefault(r => r.Code == line.Room)?.AreaType ?? "";
        if (line.QtyAbove45 > 0 && line.HeightStatus.Length == 0) line.HeightStatus = CheckStatus.Pending;
        if (line.LengthApplies && line.LengthStatus.Length == 0 && line.LengthClaimedQty > line.Qty) { line.LengthStatus = CheckStatus.Pending; LengthCheck.Recalculate(line); }
        _p.Store.Insert(line, $"Claim {line.Subcontractor} INV {line.InvoiceNo}: {line.Room} {line.Stage} {line.Item} {line.Qty:0.##}{(line.IsOver ? " OVER - " + line.OverReason : "")}");
        _p.Reload();
        return check;
    }

    public void Reverse(ClaimLine original, string reason)
    {
        _p.Store.Insert(LedgerRules.Reversal(original, reason), $"Reversed claim #{original.Id} ({original.Room} {original.Stage} {original.Item}): {reason}");
        _p.Reload();
    }

    public void DecideHeight(ClaimLine line, string status, double? acceptedQty, string note)
    {
        HeightCheck.Decide(line, status, acceptedQty, _p.CurrentUser, note);
        _p.Store.Update(line, $"Height check {line.Room} {line.Stage} {line.Item}: {line.HeightStatus} {line.QtyAbove45Accepted:0.##}/{line.QtyAbove45:0.##}");
        _p.Reload();
    }

    public void DecideLength(ClaimLine line, string status, double? routeTotal, string? groups, double? revisedOverride, string note, string attachment = "")
    {
        if (routeTotal.HasValue) line.RouteLengthTotal = routeTotal.Value;
        if (groups != null) line.LengthGroups = groups;
        line.LengthRevisedOverride = revisedOverride;
        if (note.Length > 0) line.LengthNote = note;
        LengthCheck.Recalculate(line);
        line.LengthStatus = status;
        line.LengthCheckedBy = _p.CurrentUser;
        line.LengthCheckDate = DateTime.Now;
        if (attachment.Length > 0) line.LengthAttachment = attachment;
        _p.Store.Update(line, $"Length check {line.Room} {line.Stage} {line.Item}: {status}, invoiced {LengthCheck.InvoiceBaseQty(line):0.#} of claimed {line.LengthClaimedQty:0.#}");
        _p.Reload();
    }

    public void SetAreaType(Room room, string areaType, string highAreaNote)
    {
        room.AreaType = areaType;
        room.HighAreaNote = highAreaNote;
        _p.Store.Update(room, $"Room {room.Code}: area {areaType}{(highAreaNote.Length > 0 ? ", high area: " + highAreaNote : "")}");
        _p.Reload();
    }

    // ------------------------------------------------------------------ contracts + mapping

    public void ConfirmItem(ContractItem item)
    {
        item.AttributesConfirmed = true;
        _p.Store.Update(item, $"Contract {item.ContractNo} item {item.ItemNo} attributes confirmed: {item.FixStage} {item.ConduitType} {item.Mount} {item.HeightBand} [{item.Systems}]");
        _p.Reload();
    }

    /// <summary>Stores a manual mapping choice; it overrides the rules for the same key from now on.</summary>
    public void Learn(string kind, string contractNo, string matchKey, string target, string note = "")
    {
        var existing = _p.Snapshot.MappingRules.FirstOrDefault(r => r.Kind == kind && r.ContractNo == contractNo && string.Equals(r.MatchKey, matchKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Target = target; existing.Note = note; existing.UseCount++;
            _p.Store.Update(existing, $"Mapping rule {kind} {matchKey} -> {target}");
        }
        else _p.Store.Insert(MappingEngine.Learn(kind, contractNo, matchKey, target, note), $"Mapping rule learned: {kind} {matchKey} -> {target}");
        _p.Reload();
    }

    public MappingResult Map(string contractNo, string subcontractor, int upToInvoice)
    {
        var s = _p.Snapshot;
        var ctx = new MappingContext(contractNo, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = MappingOptions() };
        var areas = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AreaType, StringComparer.OrdinalIgnoreCase);
        return new MappingEngine().Map(s.Claims.Where(c => c.Subcontractor.Equals(subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo <= upToInvoice && c.InvoiceNo > 0), ctx, areas);
    }

    // ------------------------------------------------------------------ imports

    public TrackerImportResult PreviewTracker(string path, string building) => TrackerImporter.Read(path, building);
    public string CommitTracker(TrackerImportResult r)
    {
        var (rooms, qty, claims, skipped) = TrackerImporter.Commit(r, _p.Store);
        _p.Reload();
        return $"{rooms} rooms, {qty} PROJECT QTY, {claims} new ledger lines ({skipped} already imported)";
    }

    public ContractImportResult PreviewContract(string path, string contractNo, string sub, string? building) => ContractLinkImporter.Read(path, contractNo, sub, building);
    public void CommitContract(ContractImportResult r) { ContractLinkImporter.Commit(r, _p.Store); _p.Reload(); }

    public EPromiseImportResult PreviewEPromise(string path) => EPromiseImporter.Read(path);
    public int CommitEPromise(EPromiseImportResult r) { var n = EPromiseImporter.Commit(r, _p.Store); _p.Reload(); return n; }

    public TemplateImportResult PreviewTemplate(string path, string contractNo) => InvoiceTemplateImporter.Read(path, contractNo);
    public void CommitTemplate(TemplateImportResult r, string contractNo) { InvoiceTemplateImporter.Commit(r, _p.Store, contractNo); _p.Reload(); }

    // ------------------------------------------------------------------ invoices

    public InvoiceBuild BuildInvoice(string contractNo, string sub, int invoiceNo, int revision) => InvoiceBuilder.Build(_p.Snapshot, contractNo, sub, invoiceNo, revision: revision, options: MappingOptions());

    /// <summary>Mapping defaults from Settings (DATA / GRMS 1st fix wall or ceiling).</summary>
    public MappingOptions MappingOptions() => new()
    {
        Data1stFixMount = string.IsNullOrWhiteSpace(_p.Settings.Data1stFixMount) ? Contracts.Mounts.Wall : _p.Settings.Data1stFixMount.ToUpperInvariant(),
        Grms1stFixMount = string.IsNullOrWhiteSpace(_p.Settings.Grms1stFixMount) ? Contracts.Mounts.Wall : _p.Settings.Grms1stFixMount.ToUpperInvariant(),
    };

    public SubInvoice SaveInvoice(InvoiceBuild build) { var h = InvoiceWorkflow.SaveDraft(_p.Store, build); _p.Reload(); return h; }
    public void Submit(SubInvoice inv, string aconexNo) { InvoiceWorkflow.Submit(_p.Store, inv, aconexNo); _p.Reload(); }
    public void Reject(SubInvoice inv, string reason) { InvoiceWorkflow.Reject(_p.Store, inv, reason); _p.Reload(); }
    public void Approve(SubInvoice inv) { InvoiceWorkflow.Approve(_p.Store, inv); _p.Reload(); }
    public int NextRevision(string contractNo, string sub, int invoiceNo) => InvoiceWorkflow.NextRevision(_p.Snapshot.SubInvoices, contractNo, sub, invoiceNo);
    public List<SubInvoiceLine> LinesOf(SubInvoice inv) => _p.Snapshot.SubInvoiceLines.Where(l => l.SubInvoiceId == inv.Id).OrderBy(l => l.RowOrder).ToList();

    public InvoiceBuild Stored(SubInvoice inv) => new() { Header = inv, Lines = LinesOf(inv) };

    public Invoicing.InvoiceHeaderInfo HeaderInfo()
    {
        var st = _p.Settings;
        return new Invoicing.InvoiceHeaderInfo
        {
            ProjectCode = st.InvoiceProjectCode, ProjectDirector = st.InvoiceProjectDirector, VendorNo = st.InvoiceVendorNo,
            SignatureNames = st.InvoiceSignatureNames.Split(';', StringSplitOptions.TrimEntries),
        };
    }

    // ------------------------------------------------------------------ site statements

    public int GenerateStatement(string path, string sub, string statementNo, string building, IEnumerable<string>? stages = null, IEnumerable<string>? systems = null)
    {
        var rooms = SiteStatementService.ScopeRooms(_p.Snapshot, sub, building);
        SiteStatementService.Generate(path, sub, statementNo, rooms, stages, systems, Balances());
        _p.Store.Insert(new SiteStatement { Subcontractor = sub, StatementNo = statementNo, Direction = "OUT", FileName = Path.GetFileName(path), Lines = rooms.Count, At = DateTime.Now },
            $"Site statement {statementNo} issued to {sub} ({rooms.Count} rooms)");
        _p.Reload();
        return rooms.Count;
    }

    public StatementImportResult PreviewStatement(string path, int invoiceNo, string building) => SiteStatementService.Read(path, _p.Snapshot, invoiceNo, building);
    public int CommitStatement(StatementImportResult r, string path, string? overReason) { var n = SiteStatementService.Commit(r, _p.Store, path, overReason); _p.Reload(); return n; }
}
