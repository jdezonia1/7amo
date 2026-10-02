using Raffaello.Core.Coding;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;

namespace Raffaello.Core.Materials;

/// <summary>
/// Phase-3 entry point for the screens: imports (preview / commit), auto-coding, 3-way match, supplier invoices, DN lookup.
/// Uses <see cref="ProjectService"/> for the shared data (BOQ list, invoices) and <see cref="IMaterialsStore"/> for its own tables.
/// </summary>
public sealed class MaterialsService
{
    public ProjectService Project { get; }
    public IMaterialsStore Store { get; }
    public MaterialsSettings Settings { get; }
    public MaterialsSnapshot Snapshot { get; private set; } = new();
    public MatchResult LastMatch { get; private set; } = new();

    public MaterialsService(ProjectService project, IMaterialsStore store, MaterialsSettings settings)
    {
        Project = project; Store = store; Settings = settings;
    }

    public void Reload()
    {
        Snapshot = Store.Load();
        LastMatch = ThreeWayMatcher.Match(Snapshot, Settings);
    }

    /// <summary>Reader options from the settings: OCR engine from the host, Claude vision only when enabled and a key is set.</summary>
    public ReaderOptions ReaderOptions(IOcrEngine? ocr = null) => new()
    {
        Ocr = ocr ?? NullOcrEngine.Instance,
        Vision = Vision(),
        PipeLengthM = Settings.PipeLengthM,
        LayoutEngines = LayoutEngines,
        Rasterizer = Rasterizer,
        Progress = ReadProgress,
    };

    /// <summary>Offline layout OCR engines (PaddleOCR first, Windows OCR second) set by the host; empty = text layer + legacy OCR only.</summary>
    public IReadOnlyList<Documents.Ocr.ILayoutOcrEngine> LayoutEngines { get; set; } = Array.Empty<Documents.Ocr.ILayoutOcrEngine>();
    public Documents.Ocr.IPageRasterizer? Rasterizer { get; set; }
    public IProgress<string>? ReadProgress { get; set; }
    /// <summary>Evidence index / full-text search (set by the host).</summary>
    public IDocumentStore? Documents { get; set; }

    /// <summary>Puts the pages that were read into the document archive (search + evidence), linked to the saved record. Never throws.</summary>
    public void ArchiveRead(DocText? text, string path, string docType, string linkedTable, string linkedKey)
    {
        if (Documents is null || text is null) return;
        try { DocArchive.Save(Documents, text, path, docType, linkedTable, linkedKey); }
        catch (Exception) { /* archive is best effort: the record itself is saved */ }
    }

    public IVisionReader Vision() => new ClaudeVisionReader(Settings.CloudReading, Project.Settings.AnthropicApiKey, Project.Settings.AnthropicModel)
    { Effort = Settings.VisionEffort, UseServerFallbacks = Project.Settings.UseServerFallbacks };

    public AutoCoder Coder() => new(CodingSources.From(Project.Snapshot, Snapshot), Settings.AutoCodeHigh, Settings.AutoCodeMedium);

    // ------------------------------------------------------------------ PO

    /// <summary>Fills BOQ / cost / budget resource codes: the PO's own scope-of-work sheet first, then the auto-coder. Confirmed codes are kept.</summary>
    public CodingReport CodePoLines(PoDocument doc, AutoCoder? coder = null)
    {
        coder ??= Coder();
        var report = new CodingReport();
        foreach (var l in doc.Lines)
        {
            if (l.CodeStatus is CodeStatus.Confirmed or CodeStatus.Manual) { report.Add(l.CodeStatus); continue; }
            var scope = l.Fingerprint.Length == 0 ? new List<MatPoScope>() : doc.Scope.Where(s => s.Fingerprint == l.Fingerprint && Units.Agree(s.Unit, l.Unit) && s.BoqCode.Length > 0).ToList();
            if (scope.Count > 0)
            {
                var codes = scope.GroupBy(s => s.BoqCode).OrderByDescending(g => g.Sum(s => s.Qty)).ToList();
                var budget = Project.Snapshot.BoqItems.FirstOrDefault(b => b.ItemCode.Equals(codes[0].Key, StringComparison.OrdinalIgnoreCase));
                l.BoqCode = codes[0].Key; l.ResourceCode = codes[0].First().ResourceCode;
                l.CostCode = budget?.CostCode ?? l.CostCode; l.BudgetResourceCode = budget?.BudgetResourceCode ?? l.BudgetResourceCode;
                l.CodeStatus = CodeStatus.Document; l.CodeScore = 1;
                l.CodeSource = codes.Count == 1 ? $"PO scope-of-work sheet (resource {l.ResourceCode})"
                    : $"PO scope-of-work sheet splits the line over {codes.Count} BOQ codes: {string.Join(", ", codes.Select(g => $"{g.Key} {Units.Fmt(g.Sum(s => s.Qty))}"))}";
                report.Add(CodeStatus.Document);
                continue;
            }
            var sug = coder.Suggest(new CodingRequest(doc.Header.Supplier, l.ItemCode, l.Description, l.Unit, doc.Header.Id == 0 ? null : doc.Header.Id));
            AutoCoder.Apply(l, sug);
            report.Add(sug.Status);
        }
        return report;
    }

    public Task<ExtractionResult<PoDocument>> ReadPoAsync(string path, IOcrEngine? ocr = null, CancellationToken ct = default) => PoReader.ReadAsync(path, ReaderOptions(ocr), ct);

    public MatPo CommitPo(PoDocument doc)
    {
        if (doc.Header.PoNo.Trim().Length == 0) throw new InvalidOperationException("Enter the PO number before saving.");
        if (doc.Header.Supplier.Trim().Length == 0) throw new InvalidOperationException("Enter the supplier before saving.");
        var po = Store.SavePo(doc.Header, doc.Lines, doc.Scope);
        Reload();
        return po;
    }

    /// <summary>User picked / confirmed a code for a PO line: saved as CONFIRMED and learned for next time.</summary>
    public void ConfirmCode(MatPoLine line, CodeCandidate chosen, bool manual = false)
    {
        var po = Snapshot.Pos.FirstOrDefault(p => p.Id == line.PoId);
        line.BoqCode = chosen.BoqCode; line.CostCode = chosen.CostCode; line.BudgetResourceCode = chosen.BudgetResourceCode;
        if (chosen.ResourceCode.Length > 0) line.ResourceCode = chosen.ResourceCode;
        line.CodeStatus = manual ? CodeStatus.Manual : CodeStatus.Confirmed; line.CodeScore = manual ? 1 : chosen.Score;
        line.CodeSource = $"{(manual ? "typed" : "confirmed")} by {Store.User} ({chosen.Source})";
        Store.Update(line, $"PO {po?.PoNo} line {line.LineNo}: code {chosen.BoqCode} {(manual ? "typed" : "confirmed")}");
        AutoCoder.Learn(Store, Snapshot, new CodingRequest(po?.Supplier ?? "", line.ItemCode, line.Description, line.Unit), chosen, Store.User);
        Reload();
    }

    /// <summary>Re-runs auto-coding on a saved PO (lines not confirmed by hand).</summary>
    public CodingReport RecodePo(MatPo po)
    {
        var doc = new PoDocument { Header = po };
        doc.Lines.AddRange(Snapshot.LinesOf(po));
        doc.Scope.AddRange(Snapshot.PoScope.Where(s => s.PoId == po.Id));
        var report = CodePoLines(doc);
        Store.Batch(w => { foreach (var l in doc.Lines) w.Update(l); }, $"PO {po.PoNo}: auto-coding {report}");
        Reload();
        return report;
    }

    public void UpdatePo(MatPo po, string summary) { Store.Update(po, summary); Reload(); }

    // ------------------------------------------------------------------ DN / MIR

    public Task<ExtractionResult<DnDocument>> ReadDnAsync(string path, IOcrEngine? ocr = null, CancellationToken ct = default) => DnReader.ReadAsync(path, ReaderOptions(ocr), ct);

    public MatDn CommitDn(DnDocument doc)
    {
        if (doc.Header.DnNo.Trim().Length == 0) throw new InvalidOperationException("Enter the DN number before saving.");
        var po = Snapshot.FindPo(doc.Header.PoNo);
        if (po != null && (doc.Header.Supplier.Length == 0 || !po.Supplier.StartsWith(doc.Header.Supplier.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
            doc.Header.Supplier = po.Supplier;
        if (po != null) doc.Header.PoNo = po.PoNo;
        var dn = Store.SaveDn(doc.Header, doc.Lines);
        Reload();
        ThreeWayMatcher.Apply(Store, LastMatch);
        Reload();
        return dn;
    }

    public Task<ExtractionResult<MirDocument>> ReadMirAsync(string path, IOcrEngine? ocr = null, MirReadOptions? mir = null, CancellationToken ct = default) =>
        MirReader.ReadAsync(path, ReaderOptions(ocr), mir, ct);

    public MatMir CommitMir(MirDocument doc, bool saveDnReads = true)
    {
        if (doc.Header.MirNo.Trim().Length == 0) throw new InvalidOperationException("Enter the MIR number before saving.");
        if (saveDnReads)
            foreach (var d in doc.DnReads.Where(d => d.Value.Header.DnNo.Length > 0 && d.Value.Lines.Count > 0))
                if (!Snapshot.Dns.Any(x => x.DnNo == d.Value.Header.DnNo)) Store.SaveDn(d.Value.Header, d.Value.Lines);
        var mir = Store.SaveMir(doc.Header, doc.Dns, doc.Evidence);
        Reload();
        ThreeWayMatcher.Apply(Store, LastMatch);
        Reload();
        return mir;
    }

    public void SetMirStatus(MatMir mir, string status) { mir.Status = status; Store.Update(mir, $"MIR {mir.MirNo}: {status}"); Reload(); }

    public void AddDnToMir(MatMir mir, string dnNo, string poNo)
    {
        Store.Insert(new MatMirDn { MirId = mir.Id, DnNo = dnNo.Trim(), PoNo = poNo.Trim(), Source = TextSource.Manual }, $"MIR {mir.MirNo}: DN {dnNo} added by hand");
        Reload();
        ThreeWayMatcher.Apply(Store, LastMatch);
        Reload();
    }

    public MatchResult RunMatch()
    {
        Reload();
        ThreeWayMatcher.Apply(Store, LastMatch);
        Reload();
        return LastMatch;
    }

    // ------------------------------------------------------------------ supplier invoices

    public SupplierInvoiceBuild BuildInvoice(MatPo po, int invoiceNo, IReadOnlyCollection<long> dnLineIds, int revision = 0) =>
        SupplierInvoices.Build(Project.Snapshot, Snapshot, Settings, po, invoiceNo, dnLineIds, revision);

    public SubInvoice SaveInvoice(SupplierInvoiceBuild b)
    {
        var h = SupplierInvoices.Save(Project.Store, Store, b);
        Project.Reload();
        Reload();
        return h;
    }

    /// <summary>Supplier invoices stored through the shared invoice tables (marked in Notes).</summary>
    public List<SubInvoice> SupplierInvoiceHeaders() => Project.Snapshot.SubInvoices.Where(i => i.Notes.StartsWith(SupplierInvoices.Marker, StringComparison.Ordinal)).ToList();

    /// <summary>DN lines locked to a stored supplier invoice number.</summary>
    public List<long> LockedLines(SubInvoice inv) => Snapshot.Locks.Where(k => k.InvoiceNo == inv.InvoiceNo && MaterialsSnapshot.PoKey(k.PoNo) == MaterialsSnapshot.PoKey(inv.ContractNo)
                                                                             && k.Supplier.Equals(inv.Subcontractor, StringComparison.OrdinalIgnoreCase)).Select(k => k.DnLineId).ToList();

    public string ExportInvoice(string folder, SupplierInvoiceBuild b, InvoiceHeaderInfo info) => SupplierInvoices.ExportPackage(folder, b, info, Snapshot, Settings);

    public List<DnLookupRow> Lookup(string? supplier, string? query) => DnLookup.Search(Project.Snapshot, Snapshot, supplier, query);
}

/// <summary>Coverage of an auto-coding run (AUTO / REVIEW / ASK / DOCUMENT / CONFIRMED counts).</summary>
public sealed class CodingReport
{
    public Dictionary<string, int> Counts { get; } = new();
    public void Add(string status) => Counts[status.Length == 0 ? CodeStatus.Ask : status] = Counts.GetValueOrDefault(status.Length == 0 ? CodeStatus.Ask : status) + 1;
    public int Total => Counts.Values.Sum();
    public int Filled => Total - Counts.GetValueOrDefault(CodeStatus.Ask);
    public override string ToString() => string.Join(", ", Counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")) + $" ({Filled}/{Total} filled)";
}
