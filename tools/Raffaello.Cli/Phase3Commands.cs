using Raffaello.Core.Coding;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Materials;

namespace Raffaello.Cli;

/// <summary>Phase 3 (materials / owner MOS / BOQ) verification commands.</summary>
public static partial class Phase3Commands
{
    private static readonly HashSet<string> Names = new() { "pdf-text", "read-po", "read-dn", "read-mir", "match", "autocode", "read-boq", "supplier-invoice" };

    public static bool Handles(string cmd) => Names.Contains(cmd);

    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "pdf-text":
                    var t = PdfTextReader.Read(args[1]);
                    foreach (var p in t.Pages)
                    {
                        Console.WriteLine($"===== PAGE {p.Number} words={p.WordCount} scan={p.IsScan} image={(p.DominantImage is { } im ? $"{im.MediaType} {im.Width}x{im.Height} {im.Coverage:P0}" : "-")}");
                        Console.WriteLine(p.Text);
                    }
                    return 0;
                case "read-po": ReadPo(args[1]); return 0;
                case "read-dn": ReadDn(args[1]); return 0;
                case "read-mir": ReadMir(args[1]); return 0;
                case "match": Match(args.Skip(1).ToArray()); return 0;
                case "autocode": AutoCode(args.Skip(1).ToArray()); return 0;
                case "read-boq": ReadBoq(args[1]); return 0;
                case "supplier-invoice": SupplierInvoice(args.Skip(1).ToArray()); return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex);
            return 1;
        }
        return 2;
    }

    private static void Issues<T>(ExtractionResult<T> r)
    {
        foreach (var g in r.Issues.GroupBy(i => i.Level).OrderByDescending(g => g.Key))
            foreach (var i in g) Console.WriteLine($"  {i.Status,-5} {i}");
        Console.WriteLine($"  pages: {string.Join(" ", r.PageSources.Select(kv => $"p{kv.Key}={kv.Value}"))}");
    }

    private static void ReadPo(string path)
    {
        var r = PoReader.ReadAsync(path).GetAwaiter().GetResult();
        var h = r.Value.Header;
        Console.WriteLine($"PO {h.PoNo} | supplier {h.Supplier} | date {h.PoDate:dd-MMM-yyyy} | scope {h.Scope}");
        Console.WriteLine($"  terms: advance {h.AdvancePct:P0}, retention {h.RetentionPct:P0}, tolerance header {h.ToleranceHeaderPct:P0} / clause {h.ToleranceClausePct:P0}, penalty {h.PenaltyPctPerWeek:P0}/week max {h.PenaltyMaxPct:P0}, remeasurable {h.Remeasurable}");
        Console.WriteLine($"  payment: {h.PaymentTerms} | delivery: {h.DeliveryTerms}");
        Console.WriteLine($"  lines {r.Value.Lines.Count}, sum {r.Value.LinesTotal:N2}, stated total {h.StatedTotal:N2}, VAT {h.StatedVat:N2}, grand {h.StatedGrandTotal:N2}");
        Console.WriteLine($"  scope-of-work rows {r.Value.Scope.Count}, BOQ codes {r.Value.Scope.Select(s => s.BoqCode).Distinct().Count()}");
        foreach (var l in r.Value.Lines.Take(60)) Console.WriteLine($"   {l.LineNo,3} {l.Description,-36} {l.Unit,-5} {l.Qty,10:N2} x {l.Rate,9:N3} = {l.Amount,13:N2}  {Fingerprints.Cable(l.Description)}");
        Issues(r);
    }

    private static void ReadDn(string path)
    {
        var r = DnReader.ReadAsync(path).GetAwaiter().GetResult();
        var h = r.Value.Header;
        Console.WriteLine($"DN {h.DnNo} | {h.DnDate:dd-MMM-yyyy} | PO {h.PoNo} ({h.PoDate:dd-MMM-yyyy}) | order {h.OrderNo} | customer {h.CustomerNo} | supplier {h.Supplier} | truck {h.TruckNo}");
        foreach (var l in r.Value.Lines) Console.WriteLine($"   {l.ItemNo} {l.ItemCode} {l.Description,-40} batch {l.Batch} {l.RawQty} {l.RawUnit} -> {l.Qty} {l.Unit}  [{Fingerprints.Cable(l.Description)}]");
        Console.WriteLine($"  lines {r.Value.Lines.Count}, total {r.Value.Lines.Sum(l => l.Qty):N0} M, sub-totals {r.Value.SubTotals.Count}");
        Issues(r);
    }

    private static void ReadMir(string path)
    {
        var r = MirReader.ReadAsync(path).GetAwaiter().GetResult();
        var h = r.Value.Header;
        Console.WriteLine($"MIR {h.MirNo} rev {h.Revision} | {h.MirDate:dd-MMM-yyyy} | MAR {h.MarRef} | {h.Description}");
        Console.WriteLine($"  pages: {h.PageKinds}");
        Console.WriteLine($"  DN refs: {string.Join(", ", r.Value.Dns.Select(d => d.DnNo))}; evidence: {string.Join(", ", r.Value.Evidence.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var e in r.Value.Evidence.Take(8)) Console.WriteLine($"   {e.Kind} p{e.Page} {e.Description} {e.Qty} {e.Unit}");
        Issues(r);
    }

    /// <summary>match PO.pdf DN.pdf [MIR.pdf] [--dn-in-mir] : imports into a scratch store and runs the 3-way match.</summary>
    private static void Match(string[] a)
    {
        var files = Positional(a);
        var work = Opt(a, "work") ?? Path.GetTempPath();
        Directory.CreateDirectory(work);
        var dbPath = Path.Combine(work, $"raffaello-phase3-{Guid.NewGuid():N}.db");
        var db = new Db(dbPath, "cli");
        db.EnsureSchema();
        var store = new SqliteMaterialsStore(db);
        var settings = new MaterialsSettings();
        try
        {
            var po = PoReader.ReadAsync(files[0]).GetAwaiter().GetResult();
            store.SavePo(po.Value.Header, po.Value.Lines, po.Value.Scope);
            foreach (var f in files.Skip(1))
            {
                if (f.Contains("MIR", StringComparison.OrdinalIgnoreCase))
                {
                    var m = MirReader.ReadAsync(f).GetAwaiter().GetResult();
                    if (a.Contains("--dn-in-mir"))
                        foreach (var dnNo in store.All<MatDn>().Select(d => d.DnNo)) m.Value.Dns.Add(new MatMirDn { DnNo = dnNo, Source = "MANUAL" });
                    store.SaveMir(m.Value.Header, m.Value.Dns, m.Value.Evidence);
                    Console.WriteLine($"MIR {m.Value.Header.MirNo}: DN refs {string.Join(", ", m.Value.Dns.Select(d => d.DnNo))}");
                }
                else
                {
                    var d = DnReader.ReadAsync(f).GetAwaiter().GetResult();
                    if (d.Value.Header.Supplier.Length == 0 || store.Load().FindPo(d.Value.Header.PoNo) is { } p0) d.Value.Header.Supplier = store.Load().FindPo(d.Value.Header.PoNo)?.Supplier ?? d.Value.Header.Supplier;
                    store.SaveDn(d.Value.Header, d.Value.Lines);
                }
            }
            var res = ThreeWayMatcher.Match(store.Load(), settings);
            ThreeWayMatcher.Apply(store, res);
            foreach (var r in res.Rows)
                Console.WriteLine($"  DN {r.Dn.DnNo} {r.Line.ItemNo} {r.Line.Description,-38} {r.Line.RawQty} {r.Line.RawUnit} -> {Units.Fmt(r.Qty)} {r.Unit} | PO line {r.PoLine?.LineNo.ToString("00") ?? "--"} {r.PoLine?.Description} ({r.Score:P0}) | cum {Units.Fmt(r.CumQty)}/{Units.Fmt(r.PoQty)} | {r.Status} {r.NoteText}");
            Console.WriteLine("  " + res.Summary);
            foreach (var p in res.PoLines.Where(p => p.Delivered > 0)) Console.WriteLine($"  PO line {p.Line.LineNo:00} {p.Line.Description}: delivered {Units.Fmt(p.Delivered)} of {Units.Fmt(p.Line.Qty)} {p.Line.Unit} ({p.Pct:P1}), allowed {Units.Fmt(p.Allowed)}");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) try { File.Delete(f); } catch { }
        }
    }

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static List<string> Positional(string[] a)
    {
        var list = new List<string>();
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--")) { if (i + 1 < a.Length && !a[i + 1].StartsWith("--") && a[i] != "--dn-in-mir") i++; continue; }
            list.Add(a[i]);
        }
        return list;
    }

    private static string Csv(object? x) => "\"" + (x?.ToString() ?? "").Replace("\"", "'") + "\"";

    /// <summary>autocode PO.pdf [--epromise INVOICE.xlsx] [--no-scope] [--out FILE.csv] : codes every PO line and prints coverage.</summary>
    private static void AutoCode(string[] a)
    {
        var files = Positional(a);
        var po = PoReader.ReadAsync(files[0]).GetAwaiter().GetResult();
        var budget = new List<Raffaello.Core.Domain.BoqItem>();
        if (Opt(a, "epromise") is { } ep)
        {
            var r = Raffaello.Core.Contracts.EPromiseImporter.Read(ep);
            budget = r.Items;
            Console.WriteLine("E-Promise: " + r.Summary);
        }
        var noScope = a.Contains("--no-scope");
        var scope = noScope ? new List<MatPoScope>() : po.Value.Scope;
        var coder = new AutoCoder(new CodingSources { BudgetList = budget, PoScope = scope }, 0.85, 0.6);
        Console.WriteLine($"pool {coder.PoolSize} candidates, PO scope rows {scope.Count}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = new List<string> { "line,description,unit,status,score,boq,cost code,budget resource,resource,source,alt1,alt2" };
        var stats = new Dictionary<string, int>();
        foreach (var l in po.Value.Lines)
        {
            var sug = coder.Suggest(new CodingRequest(po.Value.Header.Supplier, l.ItemCode, l.Description, l.Unit));
            stats[sug.Status] = stats.GetValueOrDefault(sug.Status) + 1;
            var b = sug.Best;
            Console.WriteLine($"  {l.LineNo,3} {l.Description,-32} {sug.Status,-6} {b?.Score,5:P0} {b?.BoqCode,-22} {b?.CostCode} {b?.BudgetResourceCode} | {string.Join(" | ", sug.Top.Skip(1).Select(c => $"{c.BoqCode} {c.Score:P0}"))}");
            rows.Add(string.Join(",", new object?[] { l.LineNo, l.Description, l.Unit, sug.Status, b?.Score, b?.BoqCode, b?.CostCode, b?.BudgetResourceCode, b?.ResourceCode, b?.Source,
                sug.Top.ElementAtOrDefault(1)?.BoqCode, sug.Top.ElementAtOrDefault(2)?.BoqCode }.Select(Csv)));
        }
        Console.WriteLine($"coverage: {string.Join(", ", stats.Select(kv => $"{kv.Key} {kv.Value}"))} of {po.Value.Lines.Count} lines ({sw.ElapsedMilliseconds} ms)");
        if (Opt(a, "out") is { } outFile) { File.WriteAllLines(outFile, rows); Console.WriteLine("written " + outFile); }
    }

    private static void ReadBoq(string path)
    {
        var r = Raffaello.Core.Boq.BoqImporter.Read(path);
        Console.WriteLine(r.Summary);
        foreach (var g in r.Rows.Where(x => !x.IsHeading).GroupBy(x => x.System.Length == 0 ? "(none)" : x.System).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-24} {g.Count(),5}  {string.Join(", ", g.GroupBy(x => x.Category.Length == 0 ? "(none)" : x.Category).OrderByDescending(c => c.Count()).Take(4).Select(c => $"{c.Key} {c.Count()}"))}");
        foreach (var i in r.Issues.Take(10)) Console.WriteLine("  CHECK " + i);
    }

    /// <summary>supplier-invoice PO.pdf DN.pdf [MIR.pdf --dn-in-mir] --out DIR : match, build INV-01 from all matched DN lines, save, export package.</summary>
    private static void SupplierInvoice(string[] a)
    {
        var files = Positional(a);
        var outDir = Opt(a, "out") ?? throw new ArgumentException("--out DIR is required");
        Directory.CreateDirectory(outDir);
        var dbPath = Path.Combine(outDir, "phase3-cli.db");
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
        var settings = new Raffaello.Core.Settings.AppSettings { DataFilePath = dbPath, SeedDemoData = false, UserName = "cli" };
        var project = new Raffaello.Core.ProjectService(settings);
        project.Initialize();
        var store = new SqliteMaterialsStore(() => (Db)project.Store);
        var svc = new MaterialsService(project, store, new MaterialsSettings());
        svc.Reload();
        var po = svc.ReadPoAsync(files[0]).GetAwaiter().GetResult();
        Console.WriteLine("coding: " + svc.CodePoLines(po.Value));
        var saved = svc.CommitPo(po.Value);
        foreach (var f in files.Skip(1))
        {
            if (f.Contains("MIR", StringComparison.OrdinalIgnoreCase))
            {
                var m = svc.ReadMirAsync(f).GetAwaiter().GetResult();
                if (a.Contains("--dn-in-mir")) foreach (var d in svc.Snapshot.Dns) m.Value.Dns.Add(new MatMirDn { DnNo = d.DnNo, PoNo = d.PoNo, Source = "MANUAL" });
                svc.CommitMir(m.Value);
            }
            else svc.CommitDn(svc.ReadDnAsync(f).GetAwaiter().GetResult().Value);
        }
        var match = svc.RunMatch();
        Console.WriteLine("match: " + match.Summary);
        var po2 = svc.Snapshot.Pos.First(p => p.Id == saved.Id);
        var no = SupplierInvoices.NextInvoiceNo(svc.Snapshot, po2);
        var lines = SupplierInvoices.Invoiceable(svc.Snapshot, po2, no).Select(l => l.Id).ToList();
        var b = svc.BuildInvoice(po2, no, lines);
        var h = svc.SaveInvoice(b);
        Console.WriteLine($"invoice {h.Title}: {b.CurrentLines.Count} DN lines, {b.Build.Lines.Count(l => l.Kind == "ITEM")} rows, current SAR {b.Build.Totals.CurrGross:N2}, VAT {b.Build.Totals.VatCurr:N2}, incl. VAT {b.Build.Totals.NetInclVatCurr:N2}");
        foreach (var l in b.Build.Lines.Where(l => l.Kind == "ITEM" && l.CurrQty > 0)) Console.WriteLine($"   {l.ItemNo} {l.BoqCode,-22} {l.Description,-26} curr {l.CurrQty,9:N2} {l.Unit} x {l.Rate:N3} = {l.CurrAmount,12:N2}");
        foreach (var w in b.Warnings) Console.WriteLine("  WARN " + w);
        try { svc.SaveInvoice(svc.BuildInvoice(po2, no + 1, lines)); Console.WriteLine("ERROR: second invoice of the same DN lines was accepted"); }
        catch (DnLineLockedException ex) { Console.WriteLine("hard lock OK: " + ex.Message); }
        var zip = svc.ExportInvoice(outDir, b, project.Workflow.HeaderInfo());
        Console.WriteLine("package " + zip);
        Console.WriteLine("lookup: " + string.Join(" | ", svc.Lookup(null, "81064344").Select(r => $"{r.DnNo} PO {r.PoNo} {r.Invoice} {r.InvoiceStatus} MIR {r.MirNo} {r.Match}")));
        // owner MOS from the same deliveries
        var mos = Raffaello.Core.Mos.MosService.Build(project.Snapshot, svc.Snapshot, svc.Settings, 1, 0, DateTime.Today, svc.Coder());
        Console.WriteLine($"MOS-01: {mos.Lines.Count} BOQ rows, on site SAR {mos.CumAmount:N2}, warnings {mos.Warnings.Count}");
        Raffaello.Core.Mos.MosExporter.ExportExcel(Path.Combine(outDir, "MOS-01.xlsx"), mos, project.Workflow.HeaderInfo());
        Raffaello.Core.Mos.MosExporter.ExportPdf(Path.Combine(outDir, "MOS-01.pdf"), mos, project.Workflow.HeaderInfo());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
