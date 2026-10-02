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
/// Common: --db PATH (default ./raffaello-cli.db). Outputs contain project data: keep them out of the repository.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Help(); return 0; }
        var opts = Options(args.Skip(1).ToArray(), out var positional);
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
        var ctx = new MappingContext(contract, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules);
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
        var b = InvoiceBuilder.Build(s, contract, sub, invoice, revision: save ? rev : 0);
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

    private static void Report(IProjectStore store)
    {
        var s = Snap(store);
        Console.WriteLine($"rooms {s.Rooms.Count(r => r.Plot > 0 || r.Plan.Length > 0)}, PROJECT QTY rows {s.RoomQtys.Count}, ledger lines {s.Claims.Count}, contract items {s.ContractItems.Count}, BOQ links {s.ItemBoqs.Count}, BOQ codes {s.BoqItems.Count(b => b.Job.Length > 0)}, template rows {s.TemplateRows.Count}, invoices {s.SubInvoices.Count}");
        var bal = LedgerRules.Balances(s.RoomQtys, s.Claims);
        Console.WriteLine($"balances: {bal.Count} keys; over cap {bal.Values.Count(b => b.IsOver)}; height pending {s.Claims.Count(HeightCheck.IsPending)}; length pending {s.Claims.Count(LengthCheck.IsPending)}");
        _ = CultureInfo.InvariantCulture;
    }
}
