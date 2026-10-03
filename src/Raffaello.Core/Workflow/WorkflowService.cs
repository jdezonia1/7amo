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
        LengthExtras.ConvertDataRack(line, alreadyInvoiced: false, _p.CurrentUser);   // extra 15 m points: never against the cap
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
        return $"{rooms} rooms, {qty} PROJECT QTY, {claims} new ledger lines ({skipped} already imported)"
               + (r.CableSummary.Length > 0 ? "; cables: " + r.CableSummary : "");   // [cables]
    }

    /// <summary>[recon] HOTEL RECON: new hotel 100% total (PROJECT QTY) + cleaned past claims (CLEAN CLAIMS).</summary>
    public Recon.HotelReconResult PreviewHotelRecon(string projectQtyPath, string cleanClaimsPath) => Recon.HotelReconImporter.Read(projectQtyPath, cleanClaimsPath);
    public string CommitHotelRecon(Recon.HotelReconResult r)
    {
        var (rooms, qty, claims, removed) = Recon.HotelReconImporter.Commit(r, _p.Store);
        _p.Reload();
        return $"{rooms} locations, {qty} PROJECT QTY, {claims} RECON claim lines ({removed} earlier RECON lines replaced)";
    }

    /// <summary>[phase6] Room list of a building (e.g. HOTEL) by header text.</summary>
    public RoomListImportResult PreviewRoomList(string path, string building) => RoomListImporter.Read(path, building);
    public string CommitRoomList(RoomListImportResult r)
    {
        var (added, updated, qty) = RoomListImporter.Commit(r, _p.Store);
        _p.Reload();
        return $"{added} rooms added, {updated} updated, {qty} PROJECT QTY";
    }

    public ContractImportResult PreviewContract(string path, string contractNo, string sub, string? building)
    {
        var r = ContractLinkImporter.Read(path, contractNo, sub, building);
        // gas meters are done by the electrical subcontractor: metering items go to the gas-meter codes, not the BMS lump sum
        foreach (var f in Raffaello.Core.Contracts.LinkTableCheck.FixGasMeterLinks(r.Items, r.Links)) r.Issues.Add(new(0, Import.IssueLevel.Warning, "GAS METER FIX: " + f));
        foreach (var w in Raffaello.Core.Contracts.LinkTableCheck.Check(r.Items, r.Links, ProjectCodeDescription)) r.Issues.Add(new(0, Import.IssueLevel.Warning, "LINK CHECK: " + w.Message));
        return r;
    }

    /// <summary>Description of a project code from the project code list ("" when unknown).</summary>
    public string ProjectCodeDescription(string code) =>
        _p.Snapshot.BoqItems.FirstOrDefault(b => b.ItemCode.Equals(code, StringComparison.OrdinalIgnoreCase))?.Description ?? "";

    /// <summary>Link-table check of a stored contract (items linked to a code of another system).</summary>
    public List<Raffaello.Core.Contracts.LinkWarning> CheckLinks(string contractNo)
    {
        var s = _p.Snapshot;
        var desc = s.BoqItems.GroupBy(b => b.ItemCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);
        return Raffaello.Core.Contracts.LinkTableCheck.Check(s.ContractItems.Where(i => i.ContractNo == contractNo), s.ItemBoqs.Where(l => l.ContractNo == contractNo), c => desc.GetValueOrDefault(c, ""));
    }

    /// <summary>Applies the gas-meter fix to a contract already imported (audited). Returns the changes.</summary>
    public List<string> FixGasMeterLinks(string contractNo)
    {
        var s = _p.Snapshot;
        var links = s.ItemBoqs.Where(l => l.ContractNo == contractNo).ToList();
        var before = links.ToDictionary(l => l.Id, l => l.BoqCode);
        var working = links.ToList();
        var log = Raffaello.Core.Contracts.LinkTableCheck.FixGasMeterLinks(s.ContractItems.Where(i => i.ContractNo == contractNo), working);
        if (log.Count == 0) return log;
        var removed = links.Where(l => !working.Contains(l)).ToList();
        _p.Store.Batch(w =>
        {
            foreach (var l in working.Where(l => before[l.Id] != l.BoqCode)) w.Update(l);
            foreach (var l in removed) w.Delete(l);
        }, $"{contractNo}: {log.Count} gas-meter link(s) moved from the BMS lump sum to the gas-meter codes");
        _p.Reload();
        return log;
    }
    public void CommitContract(ContractImportResult r) { ContractLinkImporter.Commit(r, _p.Store); _p.Reload(); }

    public EPromiseImportResult PreviewEPromise(string path) => EPromiseImporter.Read(path);
    public int CommitEPromise(EPromiseImportResult r) { var n = EPromiseImporter.Commit(r, _p.Store); _p.Reload(); return n; }

    public TemplateImportResult PreviewTemplate(string path, string contractNo) => InvoiceTemplateImporter.Read(path, contractNo);
    public void CommitTemplate(TemplateImportResult r, string contractNo) { InvoiceTemplateImporter.Commit(r, _p.Store, contractNo); _p.Reload(); }

    // ------------------------------------------------------------------ invoices

    public string CumulativeBanner() => CumulativeSplit.Banner(_p.Snapshot.Claims);

    public InvoiceBuild BuildInvoice(string contractNo, string sub, int invoiceNo, int revision)
    {
        var build = InvoiceBuilder.Build(_p.Snapshot, contractNo, sub, invoiceNo, revision: revision, options: MappingOptions());
        Cables.CableHooks.AnnotateInvoice(build, _p.Store, _p.Snapshot, MappingOptions());   // [cables] cable flags + termination / handover stages
        return build;
    }

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
    /// <summary>[phase6] Applies a confirmed Aconex outcome: approve, or reject + (subcontractor invoices) a new draft revision from the ledger.</summary>
    public string ApplyAconexOutcome(AconexWeb.OutcomeProposal p)
    {
        var inv = p.Invoice;
        if (p.Action == AconexWeb.OutcomeProposal.Approve)
        {
            if (inv.Status == SubInvoiceStatus.Draft) Submit(inv, p.WorkflowNo);
            Approve(inv);
            return $"{inv.Title} approved (Aconex {p.WorkflowNo})";
        }
        Reject(inv, p.Reason);
        if (!InvoiceKinds.IsSubcontractor(inv)) return $"{inv.Title} rejected";
        var rev = NextRevision(inv.ContractNo, inv.Subcontractor, inv.InvoiceNo);
        var build = BuildInvoice(inv.ContractNo, inv.Subcontractor, inv.InvoiceNo, rev);
        build.Header.RetentionPct = inv.RetentionPct;
        build.Header.Notes = $"Revision after Aconex rejection: {p.Reason}";
        var saved = SaveInvoice(build);
        return $"{inv.Title} rejected; {saved.Title} prepared as a draft";
    }

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

    // ------------------------------------------------------------------ past invoices + cumulative split

    public PastInvoiceFile PreviewPastInvoice(string path, string contractNo) => PastInvoiceImporter.Read(path, contractNo);

    /// <summary>Row key ("item|code") a ledger line maps to in this contract (the biggest part when height splits it).</summary>
    public Func<ClaimLine, string?> RowKeyOf(string contractNo)
    {
        var s = _p.Snapshot;
        var ctx = new MappingContext(contractNo, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = MappingOptions() };
        var areas = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AreaType, StringComparer.OrdinalIgnoreCase);
        var engine = new MappingEngine();
        return c =>
        {
            var probe = LedgerRules.Copy(c);
            probe.IsCumulative = false;
            var part = engine.Map(new[] { probe }, ctx, areas).Parts.Where(p => p.Item != null && p.BoqCode.Length > 0).OrderByDescending(p => Math.Abs(p.Qty)).FirstOrDefault();
            return part is null ? null : PastInvoiceFile.RowKey(part.Item!.ItemNo, part.BoqCode);
        };
    }

    public SplitResult PreviewSplit(string contractNo, string sub, IReadOnlyList<PastInvoiceFile> files) =>
        CumulativeSplit.Split(_p.Snapshot.Claims, sub, files.Select(f => new InvoiceCum(f.InvoiceNo, f.CumByRow())).ToList(), RowKeyOf(contractNo));

    /// <summary>Posts the split lines, retires the cumulative lines they replace, and stores the files as approved invoices.</summary>
    public string CommitSplit(SplitResult r, string contractNo, IReadOnlyList<PastInvoiceFile> files)
    {
        if (r.NewLines.Count > 0)
            _p.Store.Batch(w =>
            {
                w.InsertMany(r.NewLines);
                foreach (var c in r.Replaced) { c.ReplacedBySplit = true; w.Update(c); }
            }, $"{r.Summary}");
        var stored = files.Select(f => PastInvoiceImporter.Commit(f, _p.Store, contractNo, r.Subcontractor)).Count(h => h != null);
        _p.Reload();
        return $"{r.Summary}; {stored} invoice files stored as approved invoices";
    }

    // ------------------------------------------------------------------ attachments, head-office tracker, packages

    public Attachment AddAttachment(string ownerKind, string ownerKey, string kind, string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists) throw new FileNotFoundException("File not found.", filePath);
        var a = new Attachment
        {
            OwnerKind = ownerKind, OwnerKey = ownerKey, Kind = kind, FilePath = fi.FullName, FileName = fi.Name, Bytes = fi.Length,
            Sha256 = Packaging.Deterministic.Sha256(fi.FullName), AddedAt = DateTime.Now,
        };
        _p.Store.Insert(a, $"Attached {kind} {fi.Name} to {ownerKind} {ownerKey}");
        _p.Reload();
        return a;
    }

    public void RemoveAttachment(Attachment a) { _p.Store.Delete(a, $"Removed attachment {a.FileName} from {a.OwnerKind} {a.OwnerKey}"); _p.Reload(); }

    public List<Attachment> AttachmentsOf(string ownerKind, string ownerKey) =>
        _p.Snapshot.Attachments.Where(a => a.OwnerKind == ownerKind && a.OwnerKey.Equals(ownerKey, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.FileName).ToList();

    public HeadOffice.TrackerExportResult ExportTracker(string path, string? sub, int? invoiceNo, string contractNo, bool ledgerAll, InvoiceBuild? invoice = null) =>
        HeadOffice.TrackerExporter.Export(path, _p.Snapshot, _p.LoadPlans(), new HeadOffice.TrackerExportScope
        {
            Subcontractor = sub, InvoiceNo = invoiceNo, ContractNo = contractNo, LedgerAllSubcontractors = ledgerAll, AsOf = DateTime.Today, Password = _p.Settings.TrackerPassword,
        }, invoice);

    /// <summary>[phase6] WIR / MIR documents downloaded through the Aconex document register (they go into packages).</summary>
    public List<(string DocumentNo, string Path)> RegisteredDocuments()
    {
        try
        {
            AconexWeb.IAconexStore? ac = _p.Store switch
            {
                Remote.RemoteProjectStore r => new Remote.RemoteAconexStore(r),
                Data.Db db when File.Exists(db.Path) => new AconexWeb.SqliteAconexStore(db.Path, _p.CurrentUser),
                _ => null,
            };
            if (ac is AconexWeb.SqliteAconexStore sq) sq.EnsureSchema();
            return ac?.Downloads().Select(d => (d.DocumentNo, d.Path)).ToList() ?? new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>[phase6] The shared data / documents folders (WIR, MIR, packages, variation docs, Aconex screenshots).</summary>
    public Settings.ProjectFolders Folders() => Settings.ProjectFolders.From(_p.Settings);

    public string PackageFolder() => Folders().Packages;

    /// <summary>Builds the head-office ZIP for a saved invoice revision and records file + SHA-256 on it.</summary>
    public Packaging.PackageResult BuildPackage(SubInvoice inv, bool final)
    {
        var res = Packaging.InvoicePackageBuilder.Build(new Packaging.PackageRequest
        {
            Snapshot = _p.Snapshot, Plans = _p.LoadPlans(), Build = Stored(inv), Info = HeaderInfo(), OutputFolder = PackageFolder(),
            WirFolder = Folders().Wir, NamePattern = _p.Settings.PackageNamePattern, Final = final, RegisteredDocuments = RegisteredDocuments(),
            ExtraFiles = Cables.CableHooks.PackageFiles(_p.Store, inv, Path.Combine(Path.GetTempPath(), "raffaello-cables")),   // [cables] 09_Cable_checks.pdf
        });
        inv.PackageFile = res.ZipPath; inv.PackageSha256 = res.Sha256; inv.PackageKind = res.Kind; inv.PackageBuiltAt = DateTime.Now; inv.PackageMissingWirs = res.MissingWirs.Count;
        _p.Store.Update(inv, $"{inv.Title} {res.Kind} package built: {Path.GetFileName(res.ZipPath)} SHA-256 {res.Sha256}");
        _p.Reload();
        return res;
    }

    // ------------------------------------------------------------------ site statements

    public int GenerateStatement(string path, string sub, string statementNo, string building, IEnumerable<string>? stages = null, IEnumerable<string>? systems = null)
    {
        var rooms = SiteStatementService.ScopeRooms(_p.Snapshot, sub, building);
        SiteStatementService.Generate(path, sub, statementNo, rooms, stages, systems, Balances(), Cables.CableHooks.RunsFor(_p.Store));   // [cables] CABLES sheet + runs list
        _p.Store.Insert(new SiteStatement { Subcontractor = sub, StatementNo = statementNo, Direction = "OUT", FileName = Path.GetFileName(path), Lines = rooms.Count, At = DateTime.Now },
            $"Site statement {statementNo} issued to {sub} ({rooms.Count} rooms)");
        _p.Reload();
        return rooms.Count;
    }

    public StatementImportResult PreviewStatement(string path, int invoiceNo, string building)
    {
        var r = SiteStatementService.Read(path, _p.Snapshot, invoiceNo, building);
        Cables.CableHooks.AnnotateStatement(r, _p.Store);   // [cables] duplicate FROM-TO / unknown run ... as warnings in the preview
        // contract-rule warnings (height bands, 15 m rule) of the claim lines, bypassable in the preview
        Wiring.StatementPreviewChecks.RuleWarnings(r, _p.Snapshot, StatementDocs());
        return r;
    }
    /// <summary>Contract intelligence of the current data source (null when it cannot be opened).</summary>
    public Documents.IDocumentStore? StatementDocs()
    {
        try { return new Remote.DocumentStoreSelector(() => _p.Store); } catch (Exception) { return null; }
    }

    public int CommitStatement(StatementImportResult r, string path, string? overReason) { var n = SiteStatementService.Commit(r, _p.Store, path, overReason); _p.Reload(); return n; }
}
