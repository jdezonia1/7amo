using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Mapping;
using Raffaello.Core.Queue;
using Raffaello.Core.Statements;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Cables;

/// <summary>
/// [cables] The small entry points the shared modules call (tracker import, site statement import / generation, invoice build,
/// invoice package, "Needs you today"). Each one is tolerant: a cable problem never stops the main operation.
/// </summary>
public static class CableHooks
{
    // ------------------------------------------------------------------ tracker

    /// <summary>Reads the tracker's 'CABLES BRANDED' / 'CABLES HOTEL' sheets (the subcontractors' cable statements).</summary>
    public static List<CableClaim> ReadTracker(XlsxStreamReader x, string fileName, List<ImportIssue> issues)
    {
        try { return CableSheets.ReadTracker(x, fileName, issues); }
        catch (Exception ex) { issues.Add(new(0, IssueLevel.Warning, "Cable sheets could not be read: " + ex.Message)); return new(); }
    }

    /// <summary>
    /// After the tracker commit: cable claims from the CABLES sheets plus the ledger CABLE PULLING lines (paired with the sheet rows that are the
    /// same claim, so nothing is counted twice). Returns a one-line summary for the import message.
    /// </summary>
    public static string CommitTracker(IReadOnlyList<CableClaim> sheetClaims, IProjectStore store, string fileName)
    {
        try
        {
            var svc = new CableService(CableStore.For(store));
            svc.Store.EnsureSchema();
            var ledger = LedgerCableLines(store.All<ClaimLine>());
            var plan = svc.Import(sheetClaims, ledger, $"Tracker cables {Path.GetFileName(fileName)}");
            return plan.Summary;
        }
        catch (Exception ex) { return "cables not imported: " + ex.Message; }
    }

    /// <summary>
    /// Ledger entry screen: a CABLE PULLING line just posted (LOCATION = FROM panel, NOTES = TO) becomes a cable claim; returns the cable flags
    /// it raises (duplicate FROM-TO, over length, unknown run ...) as one warning text, or "" when there are none.
    /// </summary>
    public static string LedgerPosted(IProjectStore store, ClaimLine line)
    {
        try
        {
            if (!CableStages.IsLedgerCableStage(line.Stage)) return "";
            if (line.SourceKey.Length == 0 && line.Id <= 0) return "";
            var svc = new CableService(CableStore.For(store));
            svc.Store.EnsureSchema();
            var plan = svc.Import(Array.Empty<CableClaim>(), new[] { CableService.FromLedger(line) }, $"Ledger cable line {line.Subcontractor} INV {line.InvoiceNo} {line.Room} {line.Item}");
            var open = plan.Flags.Where(f => !f.IsBypassed && f.Code != CableFlagCodes.UnknownRun).ToList();
            return string.Join("\n", open.Take(4).Select(f => $"{f.Code}: {f.Message}"));
        }
        catch (Exception ex) { return "Cable checks not available: " + ex.Message; }
    }

    public static List<CableClaim> LedgerCableLines(IEnumerable<ClaimLine> lines) =>
        lines.Where(l => CableStages.IsLedgerCableStage(l.Stage) && !l.ReplacedBySplit).Select(CableService.FromLedger).ToList();

    // ------------------------------------------------------------------ site statement

    /// <summary>Adds the CABLES input sheet (+ CABLE RUNS list) to a generated statement workbook.</summary>
    public static void AddStatementSheets(ClosedXML.Excel.XLWorkbook wb, string sub, string statementNo, IReadOnlyList<CableRun>? runs) =>
        CableSheets.AddStatementSheets(wb, sub, statementNo, runs);

    public static IReadOnlyList<CableRun> RunsFor(IProjectStore store)
    {
        try { return CableStore.For(store).All<CableRun>(); } catch { return Array.Empty<CableRun>(); }
    }

    /// <summary>Statement preview: cable flags as import warnings (duplicates FROM-TO with the earlier claims, unknown runs ...).</summary>
    public static List<CableFlag> AnnotateStatement(StatementImportResult res, IProjectStore store)
    {
        if (res.CableClaims.Count == 0) return new();
        try
        {
            var svc = new CableService(CableStore.For(store));
            var copies = res.CableClaims.Select(Copy).ToList();
            var plan = svc.Prepare(copies);
            foreach (var f in plan.Flags.Where(f => !f.IsBypassed))
                res.Issues.Add(new(0, IssueLevel.Warning, $"CABLE {f.Code}: {f.ClaimText} - {f.Message}"));
            res.CableFlags.Clear();
            res.CableFlags.AddRange(plan.Flags);
            return plan.Flags;
        }
        catch (Exception ex) { res.Issues.Add(new(0, IssueLevel.Warning, "Cable checks not available: " + ex.Message)); return new(); }
    }

    /// <summary>
    /// Statement commit: writes the cable claims (with their runs / panels) first - idempotent by source key - and returns the ledger lines of the
    /// PULLING claims, which the statement batch inserts so the existing mapping invoices them (70 % stage).
    /// </summary>
    public static List<ClaimLine> CommitStatementCables(StatementImportResult res, IProjectStore store)
    {
        if (res.CableClaims.Count == 0) return new();
        var svc = new CableService(CableStore.For(store));
        svc.Store.EnsureSchema();
        var plan = svc.Prepare(res.CableClaims);
        var ledger = plan.Claims.Where(c => c.Stage == CableStages.Pulling).Select(CableService.ToLedgerLine).ToList();
        svc.Commit(plan, $"Site statement {res.StatementNo} ({res.Subcontractor}) cables: {plan.Summary}");
        var existing = store.All<ClaimLine>().Select(l => l.SourceKey).ToHashSet();
        return ledger.Where(l => !existing.Contains(l.SourceKey)).ToList();
    }

    /// <summary>Cable flags of a statement preview again (after a bypass), without adding issues.</summary>
    public static List<CableFlag> StatementFlags(StatementImportResult res, IProjectStore store)
    {
        if (res.CableClaims.Count == 0) return new();
        var plan = new CableService(CableStore.For(store)).Prepare(res.CableClaims.Select(Copy).ToList());
        res.CableFlags.Clear();
        res.CableFlags.AddRange(plan.Flags);
        return plan.Flags;
    }

    private static CableClaim Copy(CableClaim c) => new()
    {
        Building = c.Building, Subcontractor = c.Subcontractor, InvoiceNo = c.InvoiceNo, StatementNo = c.StatementNo, Stage = c.Stage, RawStage = c.RawStage, Location = c.Location, Level = c.Level,
        FromRaw = c.FromRaw, ToRaw = c.ToRaw, SizeRaw = c.SizeRaw, Qty = c.Qty, SitePct = c.SitePct, WirPct = c.WirPct, WirNo = c.WirNo, Notes = c.Notes, Source = c.Source,
        SourceKey = c.SourceKey, EnteredAt = c.EnteredAt,
    };

    // ------------------------------------------------------------------ invoice

    /// <summary>
    /// Invoice build: (1) cable flags on this subcontractor's cable claims up to the invoice become warnings; (2) TERMINATION &amp; TEST (20 %) and
    /// HANDOVER (10 %) claims, and pulling claims that are not in the ledger, are added to the cable item rows found by the same mapping
    /// (cable item by size -> BOQ code). When the layout has no row at that stage %, the quantity is converted to the row's stage %
    /// (qty x stage % / row %) and explained on the row.
    /// </summary>
    public static void AnnotateInvoice(InvoiceBuild build, IProjectStore store, ProjectSnapshot s, MappingOptions? options = null)
    {
        try
        {
            var cs = CableStore.For(store);
            var snap = cs.Load();
            var h = build.Header;
            var mine = snap.Claims.Where(c => c.Subcontractor.Equals(h.Subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo > 0 && c.InvoiceNo <= h.InvoiceNo).ToList();
            if (mine.Count == 0) return;
            var flags = CableFlagEngine.For(snap, mine, subjectIsNew: false).Where(f => !f.IsBypassed).ToList();
            foreach (var f in flags.Where(f => f.Claim.InvoiceNo == h.InvoiceNo).Take(25))
                build.Warnings.Add($"CABLE {f.Code}: {f.ClaimText} - {f.Message}");
            var older = flags.Count(f => f.Claim.InvoiceNo < h.InvoiceNo);
            if (older > 0) build.Warnings.Add($"CABLE: {older} open flags on earlier invoices' cable claims (Cables page).");

            var ledgerKeys = s.Claims.Select(c => c.SourceKey).Where(k => k.Length > 0).ToHashSet();
            var extra = mine.Where(c => c.Qty != 0 && (c.Stage != CableStages.Pulling || c.LedgerSourceKey.Length == 0 || !ledgerKeys.Contains(c.LedgerSourceKey))).ToList();
            if (extra.Count == 0) return;
            var ctx = new MappingContext(h.ContractNo, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = options ?? MappingOptions.Default };
            var items = new DefaultItemResolver();
            var boqs = new DefaultBoqResolver();
            foreach (var c in extra)
            {
                var size = c.SizeKey;
                var im = items.Resolve(CableStages.LedgerPulling, size, HeightBands.Low, ctx);
                if (im.Item is null) { build.Warnings.Add($"CABLE {c.Stage} {c.FromRaw} -> {c.ToRaw} {size}: {im.Explanation} - {c.QtyAfterWir:0.##} m not invoiced."); continue; }
                var bm = boqs.Resolve(im.Item, size, "", ctx);
                var rows = build.Lines.Where(l => l.Kind == "ITEM" && l.ItemNo == im.Item.ItemNo && (bm.BoqCode.Length == 0 || l.BoqCode == bm.BoqCode || l.BoqCode.Length == 0)).ToList();
                if (rows.Count == 0) rows = build.Lines.Where(l => l.Kind == "ITEM" && l.ItemNo == im.Item.ItemNo).ToList();
                if (rows.Count == 0) { build.Warnings.Add($"CABLE item {im.Item.ItemNo} is not in the invoice layout - {c.Stage} {size} {c.QtyAfterWir:0.##} m not invoiced."); continue; }
                var pct = CableStages.Pct(c.Stage);
                var row = rows.FirstOrDefault(r => Math.Abs(r.StagePct - pct) < 1e-6) ?? rows[0];
                var qty = c.QtyAfterWir;
                var why = $"{c.Stage} {c.FromRaw} -> {c.ToRaw} {size} {c.Subcontractor} INV {c.InvoiceNo}: {c.Qty:0.##} m x site {c.SitePct:P0} x WIR {c.WirPct:P0} = {qty:0.##} m";
                if (Math.Abs(row.StagePct - pct) > 1e-6 && row.StagePct > 0)
                {
                    qty = qty * pct / row.StagePct;
                    why += $" at {pct:P0} = {qty:0.##} m-equivalent at the row's {row.StagePct:P0}";
                }
                row.CumQty = Math.Round(row.CumQty + qty, 4);
                row.CurrQty = Math.Round(row.CumQty - row.PrevQty, 4);
                row.Explanation = string.Join("\n", new[] { row.Explanation, why + " [CABLE]" }.Where(e => e.Length > 0));
            }
            build.Warnings.Add($"CABLE: {extra.Count} cable claims added from the Cables register (termination / handover stages or not in the ledger).");
        }
        catch (Exception ex) { build.Warnings.Add("Cable checks not available: " + ex.Message); }
    }

    // ------------------------------------------------------------------ package

    /// <summary>09_Cable_checks.pdf for the invoice package: open and bypassed cable flags of the subcontractor up to the invoice.</summary>
    public static List<(string Name, string Path, string Note)> PackageFiles(IProjectStore store, SubInvoice h, string tempFolder)
    {
        try
        {
            var snap = CableStore.For(store).Load();
            var mine = snap.Claims.Where(c => c.Subcontractor.Equals(h.Subcontractor, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo > 0 && c.InvoiceNo <= h.InvoiceNo).ToList();
            if (mine.Count == 0) return new();
            var flags = CableFlagEngine.For(snap, mine, subjectIsNew: false);
            Directory.CreateDirectory(tempFolder);
            var path = Path.Combine(tempFolder, $"cables_{Guid.NewGuid():N}.pdf");
            ChecksPdf(path, h, mine, flags, h.CreatedAt == default ? new DateTime(2026, 1, 1) : h.CreatedAt);
            return new() { ("09_Cable_checks.pdf", path, $"cable claims {mine.Count}, flags: {CableFlagEngine.Summary(flags)}") };
        }
        catch { return new(); }
    }

    public static void ChecksPdf(string path, SubInvoice h, IReadOnlyList<CableClaim> claims, IReadOnlyList<CableFlag> flags, DateTime stamp)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        const string red = "#8B0000";
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontSize(7.5f));
            page.Header().Text($"{h.Subcontractor} INV-{h.InvoiceNo:00} Rev {h.Revision}  -  cable claims and flags").FontSize(13).Bold().FontColor(red);
            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Text($"{claims.Count} cable claims up to INV {h.InvoiceNo}: {claims.Sum(c => c.Qty):N1} m claimed, {claims.Sum(c => c.QtyAfterWir):N1} m after SITE % x WIR %. Flags: {CableFlagEngine.Summary(flags)}.");
                if (flags.Count == 0) { col.Item().PaddingTop(6).Text("No cable flags."); return; }
                col.Item().PaddingTop(6).Table(tb =>
                {
                    tb.ColumnsDefinition(cd => { cd.ConstantColumn(30); cd.RelativeColumn(1.6f); cd.RelativeColumn(1.2f); cd.RelativeColumn(2); cd.RelativeColumn(2); cd.ConstantColumn(44); cd.ConstantColumn(42); cd.RelativeColumn(4); cd.RelativeColumn(2); });
                    tb.Header(hd =>
                    {
                        foreach (var x in new[] { "INV", "FLAG", "STAGE", "FROM", "TO", "SIZE", "QTY M", "DETAIL", "DECISION" })
                            hd.Cell().Background("#A6A6A6").Padding(2).Text(x).Bold();
                    });
                    foreach (var f in flags.OrderBy(f => f.Claim.InvoiceNo).ThenBy(f => f.Code))
                    {
                        tb.Cell().Padding(2).Text(f.Claim.InvoiceNo.ToString());
                        tb.Cell().Padding(2).Text(f.Code).Bold().FontColor(f.IsBypassed ? Colors.Black : red);
                        tb.Cell().Padding(2).Text(f.Claim.Stage);
                        tb.Cell().Padding(2).Text(f.Claim.FromRaw);
                        tb.Cell().Padding(2).Text(f.Claim.ToRaw);
                        tb.Cell().Padding(2).Text(f.Claim.SizeKey);
                        tb.Cell().Padding(2).AlignRight().Text($"{f.Claim.Qty:0.##}");
                        tb.Cell().Padding(2).Text(f.Message);
                        tb.Cell().Padding(2).Text(f.IsBypassed ? $"BYPASSED {f.Decision!.By} {f.Decision.At:dd-MMM-yy}: {f.Decision.Reason}" : "OPEN");
                    }
                });
            });
            page.Footer().AlignRight().Text(x => { x.Span($"{stamp:dd MMM yyyy}   Page "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).WithMetadata(new DocumentMetadata { Title = $"{h.Title} - cable checks", Author = "Raffaello", Creator = "Raffaello", Producer = "Raffaello", CreationDate = stamp, ModifiedDate = stamp })
            .GeneratePdf(path);
    }

    // ------------------------------------------------------------------ needs you today

    public static IEnumerable<QueueItem> Queue(ICableStore store, CableOptions? options = null)
    {
        var snap = store.Load();
        if (snap.Claims.Count == 0 && snap.Runs.Count == 0) yield break;
        var flags = CableFlagEngine.Evaluate(snap, options).Where(f => !f.IsBypassed).ToList();
        var dup = flags.Where(f => f.Code == CableFlagCodes.Duplicate).ToList();
        if (dup.Count > 0)
        {
            var top = dup.OrderByDescending(f => f.Claim.Qty).First();
            yield return new QueueItem(Verdict.Over, "CABLES", $"{dup.Count} cable claims repeat a FROM-TO already claimed",
                $"e.g. {top.ClaimText}: {top.Message}", new NavTarget("Cables", Key: "FLAGS|" + CableFlagCodes.Duplicate), 3.7e8 + dup.Count);
        }
        var cum = flags.Count(f => f.Code is CableFlagCodes.Cumulative or CableFlagCodes.OverLength);
        if (cum > 0)
            yield return new QueueItem(Verdict.Check, "CABLES", $"{cum} cable claims above the run length", "Claimed length above the design / measured length or cumulative above 100 %.",
                new NavTarget("Cables", Key: "FLAGS|" + CableFlagCodes.Cumulative), 2.2e8 + cum);
        var order = flags.Count(f => f.Code == CableFlagCodes.StageOrder);
        if (order > 0)
            yield return new QueueItem(Verdict.Check, "CABLES", $"{order} cable terminations / handovers claimed before pulling", "Check the stage of these claims.",
                new NavTarget("Cables", Key: "FLAGS|" + CableFlagCodes.StageOrder), 2.1e8 + order);
        var unknown = flags.Count(f => f.Code == CableFlagCodes.UnknownRun);
        if (unknown > 0)
            yield return new QueueItem(Verdict.Open, "CABLES", $"{unknown} cable claims on runs not in the register", "Import the SLD / cable schedule, or link the claims to runs (Cables > UNKNOWN RUNS).",
                new NavTarget("Cables", Key: "UNKNOWN"), 1.1e8 + unknown);
        var aliases = CableService.AliasSuggestions(snap).Count;
        if (aliases > 0)
            yield return new QueueItem(Verdict.Open, "CABLES", $"{aliases} panel names look like the same panel", "Confirm or reject the suggested aliases (Cables > PANELS).",
                new NavTarget("Cables", Key: "ALIASES"), 1.05e8 + aliases);
    }
}
