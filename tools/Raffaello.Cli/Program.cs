using Raffaello.Core.Materials;
using System.Diagnostics;
using System.Globalization;
using Raffaello.Core;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;
using Raffaello.Core.Statements;
using Raffaello.Core.Tracker;

namespace Raffaello.Cli;

/// <summary>
/// Headless verification of the Phase 1 workflow against real files:
///   raffaello-cli import-tracker  FILE [--building BRANDED]
///   raffaello-cli import-contract FILE --contract NO --sub NAME [--building B]
///   raffaello-cli import-epromise FILE
///   raffaello-cli import-template FILE --contract NO
///   raffaello-cli map            --contract NO --sub NAME [--invoice N]
///   raffaello-cli build-invoice  --contract NO --sub NAME --invoice N [--save]
///   raffaello-cli export         --contract NO --sub NAME --invoice N --out DIR
///   raffaello-cli statement      --sub NAME --no S-001 --out FILE
///   raffaello-cli report
/// Mapping: --data-mount WALL|CEILING, --grms-mount WALL|CEILING (DATA / GRMS 1ST FIX outlet item; default WALL).
/// Common: --db PATH (default ./raffaello-cli.db). Outputs contain project data: keep them out of the repository.
/// </summary>
public static class Program
{
    private static MappingOptions MapOptions = MappingOptions.Default;
    private static readonly HashSet<string> Commands = new() { "import-tracker", "import-contract", "import-epromise", "import-template", "map", "build-invoice", "export", "statement", "report", "approve", "analyze-sub", "split", "tracker-export", "package", "import-rooms", "map-sample", "reports" };

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Help(); return 0; }
        // [phase3] begin
        if (Phase3Commands.Handles(args[0])) return Phase3Commands.Run(args);
        // [phase3] end
        if (DocBench.Handles(args[0])) return DocBench.Run(args);
        // [insights] begin
        if (InsightsCommands.Handles(args[0])) return InsightsCommands.Run(args);
        // [insights] end
        // [cables] begin
        if (CableCommands.Handles(args[0])) return CableCommands.Run(args);
        // [cables] end
        if (TrustCommands.Handles(args[0])) return TrustCommands.Run(args);   // [trust]
        // [assemblies] begin
        if (AssembliesCommands.Handles(args[0])) return AssembliesCommands.Run(args);
        // [assemblies] end
        if (!Commands.Contains(args[0])) { Console.Error.WriteLine($"Unknown command {args[0]}"); Help(); return 2; }
        var opts = Options(args.Skip(1).ToArray(), out var positional);
        MapOptions = new MappingOptions
        {
            Data1stFixMount = opts.GetValueOrDefault("data-mount", "WALL").ToUpperInvariant(),
            Grms1stFixMount = opts.GetValueOrDefault("grms-mount", "WALL").ToUpperInvariant(),
        };
        var dbPath = opts.GetValueOrDefault("db", Path.Combine(Environment.CurrentDirectory, "raffaello-cli.db"));
        var store = new Db(dbPath, "cli");
        store.EnsureSchema();
        var sw = Stopwatch.StartNew();
        try
        {
            switch (args[0])
            {
                case "import-tracker": ImportTracker(store, positional[0], opts.GetValueOrDefault("building", Buildings.Branded)); break;
                case "import-contract": ImportContract(store, positional[0], Req(opts, "contract"), Req(opts, "sub"), opts.GetValueOrDefault("building")); break;
                case "import-epromise": ImportEPromise(store, positional[0]); break;
                case "import-template": ImportTemplate(store, positional[0], Req(opts, "contract")); break;
                case "map": Map(store, Req(opts, "contract"), Req(opts, "sub"), int.Parse(opts.GetValueOrDefault("invoice", "99"))); break;
                case "build-invoice": Build(store, Req(opts, "contract"), Req(opts, "sub"), int.Parse(Req(opts, "invoice")), opts.ContainsKey("save")); break;
                case "export": Export(store, Req(opts, "contract"), Req(opts, "sub"), int.Parse(Req(opts, "invoice")), Req(opts, "out")); break;
                case "statement": Statement(store, Req(opts, "sub"), Req(opts, "no"), Req(opts, "out")); break;
                case "report": Report(store); break;
                case "tracker-export": TrackerExport(store, opts.GetValueOrDefault("contract", ""), opts.GetValueOrDefault("sub"), opts.TryGetValue("invoice", out var ti) ? int.Parse(ti) : null, Req(opts, "out"), opts.ContainsKey("all")); break;
                case "package": Package(store, Req(opts, "contract"), Req(opts, "sub"), int.Parse(Req(opts, "invoice")), Req(opts, "out"), opts.ContainsKey("final"), opts.GetValueOrDefault("wir-folder", "")); break;
                case "reports": Reports(store, Req(opts, "out"), opts.GetValueOrDefault("building")); break;
                case "import-rooms": ImportRooms(store, positional[0], opts.GetValueOrDefault("building", Buildings.Hotel)); break;
                case "map-sample": MapSample(store, Req(opts, "contract"), opts.GetValueOrDefault("building", Buildings.Hotel)); break;
                case "analyze-sub": AnalyzeSub(store, Req(opts, "sub"), Req(opts, "contract")); break;
                case "split": SplitCumulative(store, Req(opts, "contract"), Req(opts, "sub"), positional, opts.ContainsKey("commit")); break;
                case "approve": Approve(store, Req(opts, "contract"), Req(opts, "sub"), int.Parse(Req(opts, "invoice")), opts.GetValueOrDefault("aconex", "")); break;
                default: Console.Error.WriteLine($"Unknown command {args[0]}"); Help(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 1;
        }
        Console.WriteLine($"({sw.ElapsedMilliseconds:N0} ms)");
        return 0;
    }

    private static void Help() => Console.WriteLine(typeof(Program).Assembly.GetName().Name + " - see Program.cs header for commands.");

    private static Dictionary<string, string> Options(string[] a, out List<string> positional)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var k = a[i][2..];
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) d[k] = a[++i]; else d[k] = "true";
            }
            else positional.Add(a[i]);
        }
        return d;
    }

    private static string Req(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");

    private static ProjectSnapshot Snap(IProjectStore store) => ProjectSnapshot.Load(store);

    private static void ImportTracker(IProjectStore store, string file, string building)
    {
        var r = TrackerImporter.Read(file, building);
        Console.WriteLine(r.Summary);
        Console.WriteLine($"  area types: {string.Join(", ", r.Rooms.GroupBy(x => x.AreaType).Select(g => $"{g.Key} {g.Count()}"))}");
        Console.WriteLine($"  ledger by subcontractor: {string.Join(", ", r.Claims.GroupBy(c => c.Subcontractor).Select(g => $"{g.Key} {g.Count()}"))}");
        Console.WriteLine($"  ledger stages: {string.Join(", ", r.Claims.GroupBy(c => c.Stage).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var g in r.Issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"row \d+", "row #")).Take(12))
            Console.WriteLine($"  {g.First().LevelText} x{g.Count()}: {g.Key}");
        var (rooms, qty, claims, skipped) = TrackerImporter.Commit(r, store);
        Console.WriteLine($"Committed: {rooms} rooms, {qty} PROJECT QTY, {claims} new ledger lines, {skipped} already present");
        var s = Snap(store);
        var bal = LedgerRules.Balances(s.RoomQtys, s.Claims);
        Console.WriteLine($"  balances: {bal.Count} room|stage|item keys, {bal.Values.Count(b => b.IsOver)} over cap, {bal.Values.Count(b => !b.HasCap && b.Claimed != 0)} claimed without PROJECT QTY");
    }

    private static void ImportContract(IProjectStore store, string file, string contract, string sub, string? building)
    {
        var r = ContractLinkImporter.Read(file, contract, sub, building);
        Console.WriteLine($"{contract}: {r.Summary}; building {r.Contract.Building}; value SAR {r.Contract.Value:N2}");
        Console.WriteLine($"  attributes: stage {Count(r.Items, i => i.FixStage)}; conduit {Count(r.Items, i => i.ConduitType)}; height {Count(r.Items, i => i.HeightBand)}; mount {Count(r.Items, i => i.Mount)}");
        Console.WriteLine($"  categories: {Count(r.Items, i => i.Category)}; pulling (15 m) {r.Items.Count(i => i.Is2ndFixPulling)}; with notes {r.Items.Count(i => i.ParseNotes.Length > 0)}");
        Console.WriteLine($"  bills: {string.Join(" ", r.Links.GroupBy(l => BoqCodes.Bill(l.BoqCode)).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
        foreach (var i in r.Issues.Take(8)) Console.WriteLine($"  {i.LevelText} {i.Message}");
        ContractLinkImporter.Commit(r, store);
    }

    private static string Count<T>(IEnumerable<T> items, Func<T, string> key) =>
        string.Join(" ", items.GroupBy(i => key(i) is { Length: > 0 } k ? k : "-").OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"));

    private static void ImportEPromise(IProjectStore store, string file)
    {
        var r = EPromiseImporter.Read(file);
        Console.WriteLine(r.Summary);
        Console.WriteLine($"Committed {EPromiseImporter.Commit(r, store)} BOQ codes");
    }

    private static void ImportTemplate(IProjectStore store, string file, string contract)
    {
        var r = InvoiceTemplateImporter.Read(file, contract);
        Console.WriteLine(r.Summary);
        Console.WriteLine($"  imported executed cum (col Q) on {r.Rows.Count(x => x.ImportedExecutedCum != 0)} rows; template contract amount SAR {r.Rows.Sum(x => x.Qty * x.Rate * x.StagePct):N2}");
        foreach (var i in r.Issues) Console.WriteLine($"  {i.LevelText} {i.Message}");
        InvoiceTemplateImporter.Commit(r, store, contract);
    }

    private static void Map(IProjectStore store, string contract, string sub, int invoice)
    {
        var s = Snap(store);
        var ctx = new MappingContext(contract, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = MapOptions };
        var areas = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AreaType, StringComparer.OrdinalIgnoreCase);
        var claims = s.Claims.Where(c => c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo <= invoice).ToList();
        var m = new MappingEngine().Map(claims, ctx, areas);
        Console.WriteLine($"{sub} claims up to INV {invoice}: {claims.Count} lines, invoiceable qty {m.TotalQty:N2}, held lines {m.Held.Count}");
        Console.WriteLine($"  mapped to item+BOQ row: {m.CoveragePct:P1} of qty; automatic (no confirmation needed): {m.AutoPct:P1}");
        foreach (var g in m.Parts.GroupBy(p => p.Confidence).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-10} {g.Count(),5} parts  qty {g.Sum(p => p.Qty),10:N2}");
        Console.WriteLine("  by stage | item -> contract item -> BOQ row:");
        foreach (var g in m.Parts.GroupBy(p => (p.Line.Stage, p.Line.Item, p.Band, p.Item?.ItemNo, p.BoqCode, p.BoqDescription, p.Confidence)).OrderBy(g => g.Key.Stage).ThenBy(g => g.Key.Item))
            Console.WriteLine($"    {g.Key.Stage,-13} {g.Key.Item,-16} {g.Key.Band,-4} -> item {g.Key.ItemNo ?? "-",-4} {g.Key.BoqCode,-24} {Trunc(g.Key.BoqDescription, 42),-42} {g.Key.Confidence,-9} qty {g.Sum(p => p.Qty),9:N2}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    private static InvoiceBuild Build(IProjectStore store, string contract, string sub, int invoice, bool save)
    {
        var s = Snap(store);
        var rev = InvoiceWorkflow.NextRevision(s.SubInvoices, contract, sub.ToUpperInvariant(), invoice);
        var b = InvoiceBuilder.Build(s, contract, sub, invoice, revision: save ? rev : 0, options: MapOptions);
        var t = b.Totals;
        Console.WriteLine($"{b.Header.Title}: {b.Lines.Count} rows ({b.Lines.Count(l => l.Kind == "ITEM" && (l.CumQty != 0 || l.CurrQty != 0))} with quantity)");
        Console.WriteLine($"  subcontract value SAR {t.SubcontractValue:N2}");
        Console.WriteLine($"  gross certified  prev {t.PrevGross:N2}  curr {t.CurrGross:N2}  cum {t.CumGross:N2}");
        Console.WriteLine($"  retention {t.RetentionPct:P0}: curr {t.CurrRetention:N2};  net curr {t.NetCurr:N2};  VAT {t.VatCurr:N2};  net incl. VAT {t.NetInclVatCurr:N2}");
        Console.WriteLine($"  mapping coverage {b.Mapping.CoveragePct:P1} (auto {b.Mapping.AutoPct:P1}); warnings {b.Warnings.Count}");
        foreach (var w in b.Warnings.Take(10)) Console.WriteLine($"    {w}");
        if (save) { InvoiceWorkflow.SaveDraft(store, b); Console.WriteLine($"  saved {b.Header.Title}"); }
        return b;
    }

    private static void Export(IProjectStore store, string contract, string sub, int invoice, string outDir)
    {
        var b = Build(store, contract, sub, invoice, false);
        Directory.CreateDirectory(outDir);
        var name = $"{sub.ToUpperInvariant().Replace(' ', '_')}_INV-{invoice:00}_Rev{b.Header.Revision}";
        var xlsx = Path.Combine(outDir, name + ".xlsx");
        var pdf = Path.Combine(outDir, name + ".pdf");
        InvoiceExcelExporter.Export(xlsx, b);
        InvoicePdfExporter.Export(pdf, b);
        Console.WriteLine($"  wrote {xlsx} ({new FileInfo(xlsx).Length / 1024:N0} KB), {pdf} ({new FileInfo(pdf).Length / 1024:N0} KB, {InvoicePdfExporter.FilteredRows(b.Lines).Count(l => l.Kind == "ITEM")} printed rows)");
    }

    private static void Statement(IProjectStore store, string sub, string no, string outFile)
    {
        var s = Snap(store);
        var rooms = SiteStatementService.ScopeRooms(s, sub, Buildings.Branded);
        SiteStatementService.Generate(outFile, sub.ToUpperInvariant(), no, rooms, balances: LedgerRules.Balances(s.RoomQtys, s.Claims));
        Console.WriteLine($"Statement {no} for {sub}: {rooms.Count} rooms -> {outFile}");
    }

    private static void Approve(IProjectStore store, string contract, string sub, int invoice, string aconex)
    {
        var inv = Snap(store).SubInvoices.Where(i => i.ContractNo == contract && i.Subcontractor == sub.ToUpperInvariant() && i.InvoiceNo == invoice).OrderByDescending(i => i.Revision).FirstOrDefault()
                  ?? throw new InvalidOperationException("Save the invoice first (build-invoice --save).");
        if (inv.Status == SubInvoiceStatus.Draft) InvoiceWorkflow.Submit(store, inv, aconex);
        InvoiceWorkflow.Approve(store, inv);
        Console.WriteLine($"{inv.Title} approved and locked");
    }

    /// <summary>One subcontractor's ledger: totals, keys shared with others, over-cap keys, BOQ codes cited in the notes vs the mapping.</summary>
    private static void AnalyzeSub(IProjectStore store, string sub, string contract)
    {
        var s = Snap(store);
        var mine = s.Claims.Where(c => c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"{sub}: {mine.Count} ledger lines, invoices {string.Join(",", mine.Select(c => CumulativeSplit.InvoiceLabel(c)).Distinct())}, cumulative lines {mine.Count(c => c.IsCumulative)}");
        Console.WriteLine("  qty by stage | item (plan qty / after site % x WIR %):");
        foreach (var g in mine.GroupBy(c => (c.Stage, c.Item)).OrderBy(g => g.Key.Stage).ThenBy(g => g.Key.Item))
            Console.WriteLine($"    {g.Key.Stage,-13} {g.Key.Item,-18} {g.Count(),5} lines  {g.Sum(c => c.Qty),10:N2}  {g.Sum(c => c.QtyAfterWir),10:N2}");
        Console.WriteLine($"  floors: {string.Join(", ", mine.GroupBy(c => c.Floor).Select(g => $"{g.Key} {g.Count()}"))}");
        Console.WriteLine($"  site %: {string.Join(", ", mine.GroupBy(c => c.SitePct).OrderByDescending(g => g.Count()).Select(g => $"{g.Key:0.##} x{g.Count()}"))}");

        var bal = LedgerRules.Balances(s.RoomQtys, s.Claims);
        var myKeys = mine.Select(c => c.Key).ToHashSet();
        var shared = bal.Values.Where(b => myKeys.Contains(LedgerKeys.Key(b.Room, b.Stage, b.Item)) && b.BySubcontractor.Keys.Any(k => !k.Equals(sub, StringComparison.OrdinalIgnoreCase))).ToList();
        Console.WriteLine($"  keys: {myKeys.Count} room|stage|item keys, {shared.Count} shared with other subcontractors " +
                          $"({string.Join(", ", shared.SelectMany(b => b.BySubcontractor.Keys).Where(k => !k.Equals(sub, StringComparison.OrdinalIgnoreCase)).GroupBy(k => k).Select(g => $"{g.Key} {g.Count()}"))})");
        var over = shared.Where(b => b.HasCap && b.IsOver).OrderByDescending(b => b.Claimed - b.ProjectQty).ToList();
        var ownOver = bal.Values.Where(b => myKeys.Contains(LedgerKeys.Key(b.Room, b.Stage, b.Item)) && b.HasCap && b.IsOver).ToList();
        Console.WriteLine($"  shared keys now OVER the cap: {over.Count} (all keys with {sub} over the cap: {ownOver.Count}; no PROJECT QTY: {bal.Values.Count(b => myKeys.Contains(LedgerKeys.Key(b.Room, b.Stage, b.Item)) && !b.HasCap)})");
        foreach (var b in over.Take(15))
        {
            var me = b.BySubcontractor.Where(kv => kv.Key.Equals(sub, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
            Console.WriteLine($"    {b.Room,-14} {b.Stage,-11} {b.Item,-16} cap {b.ProjectQty,8:N1}  others {b.Claimed - me,8:N1}  {sub} {me,8:N1}  excess {b.Claimed - b.ProjectQty,8:N1}  [{string.Join(" ", b.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.#}"))}]");
        }

        // BOQ codes cited in the notes ("BILL-6 r303 6-26-AL-3") vs the mapping engine
        var ctx = new MappingContext(contract, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = MapOptions };
        var areas = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AreaType, StringComparer.OrdinalIgnoreCase);
        var engine = new MappingEngine();
        var cited = new System.Text.RegularExpressions.Regex(@"\b\d+-\d+-[A-Z]{1,3}-\d+\b");
        var agree = new Dictionary<(string, string), (int Agree, int Disagree, int NoCite, HashSet<string> Cited, HashSet<string> Picked)>();
        foreach (var c in mine)
        {
            var probe = LedgerRules.Copy(c); probe.IsCumulative = false;
            var part = engine.Map(new[] { probe }, ctx, areas).Parts.OrderByDescending(p => Math.Abs(p.Qty)).FirstOrDefault();
            var codes = cited.Matches(c.Notes).Select(m => m.Value).ToList();
            var k = (c.Stage, c.Item);
            var v = agree.TryGetValue(k, out var got) ? got : (Agree: 0, Disagree: 0, NoCite: 0, Cited: new HashSet<string>(), Picked: new HashSet<string>());
            foreach (var x in codes) v.Cited.Add(x);
            if (part?.BoqCode is { Length: > 0 } pc) v.Picked.Add($"{part.Item?.ItemNo}:{pc}");
            if (codes.Count == 0) v.NoCite++;
            else if (part != null && codes.Any(x => part.BoqCode.EndsWith(x, StringComparison.OrdinalIgnoreCase))) v.Agree++;
            else v.Disagree++;
            agree[k] = v;
        }
        Console.WriteLine("  BOQ codes cited in the notes vs mapping (lines agree / disagree / no citation):");
        foreach (var ((stage, item), v) in agree.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
            Console.WriteLine($"    {stage,-13} {item,-18} {v.Agree,4} / {v.Disagree,4} / {v.NoCite,4}   cited [{string.Join(" ", v.Cited)}]   picked [{string.Join(" ", v.Picked)}]");
    }

    /// <summary>All project reports (Excel + PDF) from the data file.</summary>
    private static void Reports(IProjectStore store, string outDir, string? building)
    {
        Directory.CreateDirectory(outDir);
        var db = (Db)store;
        var mats = new SqliteMaterialsStore(db);
        var vos = new Raffaello.Core.Variations.SqliteVariationStore(db.Path, "cli"); vos.EnsureSchema();
        var ac = new Raffaello.Core.AconexWeb.SqliteAconexStore(db.Path, "cli"); ac.EnsureSchema();
        var s = Snap(store);
        var inputs = new Raffaello.Core.Reports.ReportInputs
        {
            Project = s, Materials = mats.Load(), MaterialsSettings = new MaterialsSettings(), Variations = vos.Variations(), VariationLines = vos.AllLines(),
            InvoiceBoard = Raffaello.Core.AconexWeb.StatusBoard.Build(s.SubInvoices, ac.ActiveLinks(), ac.LatestChecks(), DateTime.Today, false), Building = building,
        };
        foreach (var def in Raffaello.Core.Reports.ProjectReports.All)
        {
            var sheets = Raffaello.Core.Reports.ProjectReports.Build(def.Key, inputs);
            var x = Path.Combine(outDir, $"REPORT_{def.Key}.xlsx");
            var p = Path.Combine(outDir, $"REPORT_{def.Key}.pdf");
            Raffaello.Core.Export.ExcelExporter.Export(x, sheets);
            Raffaello.Core.Reports.ProjectReports.ExportPdf(p, def.Name, sheets);
            Console.WriteLine($"  {def.Name,-28} {string.Join(", ", sheets.Select(sh => $"{sh.Name} {sh.Rows.Count}"))}  ({new FileInfo(x).Length / 1024} KB xlsx, {new FileInfo(p).Length / 1024} KB pdf)");
        }
    }

    private static void ImportRooms(IProjectStore store, string file, string building)
    {
        var r = RoomListImporter.Read(file, building);
        Console.WriteLine(r.Summary);
        Console.WriteLine($"  columns: {string.Join(", ", r.ColumnsFound.Select(kv => $"{kv.Key}={kv.Value}"))}");
        foreach (var i in r.Issues.Take(10)) Console.WriteLine($"  {i.LevelText} {i.Message}");
        var (a, u, q) = RoomListImporter.Commit(r, store);
        Console.WriteLine($"Committed: {a} added, {u} updated, {q} PROJECT QTY");
    }

    /// <summary>Maps one synthetic claim per stage x system x area type against a contract (smoke test for a new contract, e.g. the HOTEL).</summary>
    private static void MapSample(IProjectStore store, string contract, string building)
    {
        var s = Snap(store);
        var ctx = new MappingContext(contract, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = MapOptions };
        var areas = building == Buildings.Hotel ? new[] { AreaTypes.Guestroom, AreaTypes.Foh, AreaTypes.Boh } : new[] { AreaTypes.Apartment, AreaTypes.Foh, AreaTypes.Boh };
        var claims = new List<ClaimLine>();
        foreach (var area in areas)
            foreach (var (stage, item) in new[] { ("1ST FIX", "POWER"), ("1ST FIX", "LIGHT"), ("1ST FIX", "DATA"), ("1ST FIX", "GRMS"), ("CEILING", "LIGHT"), ("EMT", "LIGHT"), ("FLEXIBLE", "LIGHT"),
                         ("2ND FIX", "POWER"), ("2ND FIX", "LIGHT"), ("2ND FIX", "DATA"), ("2ND FIX", "FIRE"), ("2ND FIX", "EMERGENCY LIGHT"), ("1ST FIX", "CCTV"), ("1ST FIX", "AV"),
                         ("DB PANELS", "PANEL 24"), ("CABLE PULLING", "4X16"), ("CABLE TRAY", "300 MM") })
                claims.Add(new ClaimLine { Building = building, Subcontractor = "SAMPLE", InvoiceNo = 1, Room = "SAMPLE-" + area, Stage = stage, Item = item, Qty = 1, AreaType = area });
        var m = new MappingEngine().Map(claims, ctx, claims.ToDictionary(c => c.Room + c.Stage + c.Item, c => c.AreaType));
        Console.WriteLine($"{contract} ({building}): {claims.Count} sample claims, mapped {m.CoveragePct:P1}, automatic {m.AutoPct:P1}");
        foreach (var p in m.Parts.OrderBy(p => p.Area).ThenBy(p => p.Line.Stage).ThenBy(p => p.Line.Item))
            Console.WriteLine($"    {p.Area,-10} {p.Line.Stage,-13} {p.Line.Item,-16} -> item {p.Item?.ItemNo ?? "-",-4} {p.BoqCode,-24} {Trunc(p.BoqDescription, 40),-40} {p.Confidence}");
    }

    private static void TrackerExport(IProjectStore store, string contract, string? sub, int? invoice, string outPath, bool all)
    {
        var s = Snap(store);
        var plans = store.All<PlanImage>();
        InvoiceBuild? b = sub != null && invoice != null && contract.Length > 0 ? InvoiceBuilder.Build(s, contract, sub, invoice.Value, options: MapOptions) : null;
        var r = Raffaello.Core.HeadOffice.TrackerExporter.Export(outPath, s, plans,
            new Raffaello.Core.HeadOffice.TrackerExportScope { Subcontractor = sub?.ToUpperInvariant(), InvoiceNo = invoice, ContractNo = contract, LedgerAllSubcontractors = all, AsOf = new DateTime(2026, 10, 1) }, b);
        Console.WriteLine($"{r.Path}: {r.Bytes / 1024:N0} KB, {r.Plans} plans, {r.Shapes} room shapes, {r.RoomsWithoutShape} rooms without a shape, {r.RoomBlocks} room blocks, {r.LedgerLines} ledger lines, {r.ControlIssues} control rows");
    }

    private static void Package(IProjectStore store, string contract, string sub, int invoice, string outDir, bool final, string wirFolder)
    {
        var settings = new Raffaello.Core.Settings.AppSettings { SeedDemoData = false, PackageOutputFolder = outDir, WirFolder = wirFolder };
        var p = new ProjectService(settings, _ => store);
        p.Initialize();
        var inv = p.Snapshot.SubInvoices.Where(i => i.ContractNo == contract && i.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && i.InvoiceNo == invoice)
            .OrderByDescending(i => i.Revision).FirstOrDefault() ?? throw new InvalidOperationException("Save the invoice first (build-invoice --save).");
        var r = p.Workflow.BuildPackage(inv, final);
        Console.WriteLine(r.Summary);
        foreach (var e in r.Entries) Console.WriteLine($"  {e.Name,-48} {e.Bytes / 1024.0,9:N1} KB  {e.Sha256[..16]}  {e.Note}");
        foreach (var w in r.MissingWirs) Console.WriteLine($"  MISSING WIR {w}");
        foreach (var w in r.Warnings) Console.WriteLine($"  NOTE {w}");
    }

    private static void SplitCumulative(IProjectStore store, string contract, string sub, List<string> files, bool commit)
    {
        var p = new ProjectService(new Raffaello.Core.Settings.AppSettings { SeedDemoData = false }, _ => store);
        p.Initialize();
        var read = files.Select(f => PastInvoiceImporter.Read(f, contract)).ToList();
        foreach (var f in read) Console.WriteLine($"  {f.Summary}");
        var r = p.Workflow.PreviewSplit(contract, sub, read);
        Console.WriteLine(r.Summary);
        foreach (var row in r.Rows) Console.WriteLine($"    {row.RowKey,-30} lines {row.Lines,4}  ledger {row.LedgerInvoiceQty,10:N2}  file {row.FileCum,10:N2}  {row.Status}  {string.Join(" ", row.PlanQtyByInvoice.OrderBy(kv => kv.Key).Select(kv => $"INV{kv.Key}={kv.Value:0.##}"))}");
        foreach (var i in r.Issues.Take(30)) Console.WriteLine($"  {i.LevelText} {i.Message}");
        if (commit) Console.WriteLine("  " + p.Workflow.CommitSplit(r, contract, read));
    }

    private static void Report(IProjectStore store)
    {
        var s = Snap(store);
        Console.WriteLine($"rooms {s.Rooms.Count(r => r.Plot > 0 || r.Plan.Length > 0)}, PROJECT QTY rows {s.RoomQtys.Count}, ledger lines {s.Claims.Count}, contract items {s.ContractItems.Count}, BOQ links {s.ItemBoqs.Count}, BOQ codes {s.BoqItems.Count(b => b.Job.Length > 0)}, template rows {s.TemplateRows.Count}, invoices {s.SubInvoices.Count}");
        var bal = LedgerRules.Balances(s.RoomQtys, s.Claims);
        Console.WriteLine($"balances: {bal.Count} keys; over cap {bal.Values.Count(b => b.IsOver)}; height pending {s.Claims.Count(HeightCheck.IsPending)}; length pending {s.Claims.Count(LengthCheck.IsPending)}");
        _ = CultureInfo.InvariantCulture;
    }
}
