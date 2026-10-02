using System.Globalization;
using Raffaello.Core.Data;
using Raffaello.Core.Export;
using Raffaello.Core.Insights;
using Raffaello.Core.Materials;

namespace Raffaello.Cli;

/// <summary>
/// [insights] Headless run of the insights module against a data file:
///   raffaello-cli insights --db FILE --out DIR [--po PO.pdf] [--dn DN.pdf ...] [--building B] [--today yyyy-MM-dd]
///                          [--start yyyy-MM-dd] [--finish yyyy-MM-dd] [--delay WEEKS]
/// --po / --dn read the documents into the data file's materials tables first (3-way match applied).
/// Writes INSIGHTS.xlsx (anomalies, material reconciliation, MOS release, rate benchmark, cash flow, earned value) and prints a summary.
/// Outputs contain project data: keep them out of the repository.
/// </summary>
public static class InsightsCommands
{
    public static bool Handles(string cmd) => cmd == "insights";

    public static int Run(string[] a)
    {
        try
        {
            var dbPath = Opt(a, "db") ?? throw new ArgumentException("--db FILE is required");
            var outDir = Opt(a, "out") ?? throw new ArgumentException("--out DIR is required");
            Directory.CreateDirectory(outDir);
            var today = Opt(a, "today") is { } t ? DateTime.ParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today;
            var building = Opt(a, "building");
            var db = new Db(dbPath, "cli");
            db.EnsureSchema();
            var mats = new SqliteMaterialsStore(db);
            mats.EnsureSchema();
            foreach (var po in Many(a, "po"))
            {
                var r = PoReader.ReadAsync(po).GetAwaiter().GetResult();
                mats.SavePo(r.Value.Header, r.Value.Lines, r.Value.Scope);
                Console.WriteLine($"PO {r.Value.Header.PoNo}: {r.Value.Lines.Count} lines, SAR {r.Value.Lines.Sum(l => l.Amount):N2}");
            }
            foreach (var dn in Many(a, "dn"))
            {
                var r = DnReader.ReadAsync(dn).GetAwaiter().GetResult();
                var poHit = mats.Load().FindPo(r.Value.Header.PoNo);
                if (poHit != null && r.Value.Header.Supplier.Length == 0) r.Value.Header.Supplier = poHit.Supplier;
                mats.SaveDn(r.Value.Header, r.Value.Lines);
                Console.WriteLine($"DN {r.Value.Header.DnNo}: {r.Value.Lines.Count} lines");
            }
            if (Many(a, "dn").Count > 0) ThreeWayMatcher.Apply(mats, ThreeWayMatcher.Match(mats.Load(), new MaterialsSettings()));

            var store = new SqliteInsightsStore(db.Path, "cli");
            store.EnsureSchema();
            var data = store.Load();
            var snap = ProjectSnapshot.Load(db);
            var m = mats.Load();
            DateTime? Meta(string key) => DateTime.TryParse(db.GetMeta(key), CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;
            var start = Opt(a, "start") is { } s0 ? DateTime.ParseExact(s0, "yyyy-MM-dd", CultureInfo.InvariantCulture) : Meta("ProjectStart") ?? today.AddDays(-7 * 38);
            var finish = Opt(a, "finish") is { } f0 ? DateTime.ParseExact(f0, "yyyy-MM-dd", CultureInfo.InvariantCulture) : Meta("PlannedFinish") ?? today.AddDays(7 * 28);
            var delay = int.Parse(Opt(a, "delay") ?? "0", CultureInfo.InvariantCulture);
            var scope = $"{building ?? "ALL BUILDINGS"}  |  as of {today:dd MMM yyyy}";
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var docs = InsightsEngine.CollectDocuments(snap);
            var hashes = InsightsEngine.HashDocuments(store, data, docs, null, DateTime.Now);
            var recon = MaterialReconciliation.Build(snap, m, data.Norms, building);
            var ev = EarnedValue.Build(new EvInputs { Project = snap, Data = data, Today = today, ProjectStart = start, PlannedFinish = finish, Building = building });
            var anomalies = InsightsEngine.All(new AnomalyInputs { Project = snap, Data = data, Materials = m, Documents = docs, Hashes = hashes, Building = building, Today = today }, recon, ev);
            var bench = RateBenchmark.Build(snap, m);
            var cash = CashFlowForecast.Build(new CashFlowInputs { Project = snap, Materials = m, Data = data, Today = today, ProjectStart = start, PlannedFinish = finish, DelayWeeks = delay, Building = building });
            Console.WriteLine($"insights computed in {sw.ElapsedMilliseconds:N0} ms ({docs.Count} documents, {hashes.Count} hashed)");

            Console.WriteLine($"\nANOMALIES: {anomalies.Count} ({string.Join(", ", anomalies.GroupBy(x => x.Severity).OrderByDescending(g => g.Key).Select(g => $"{g.Key.ToString().ToUpperInvariant()} {g.Count()}"))})");
            foreach (var g in anomalies.GroupBy(x => x.Kind).OrderByDescending(g => g.Max(x => x.Severity)).ThenByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-18} {g.Count(),5}   {string.Join(" ", g.GroupBy(x => x.Severity).OrderByDescending(x => x.Key).Select(x => $"{x.Key.ToString().ToUpperInvariant()}:{x.Count()}"))}");
            Console.WriteLine("  top 25:");
            foreach (var x in anomalies.Take(25)) Console.WriteLine($"   [{x.Tag,-6}] {x.Kind,-16} {x.Title}");

            Console.WriteLine($"\nMATERIAL RECONCILIATION ({(recon.UsingDefaultNorms ? "DEFAULT norms" : $"{data.Norms.Count} norms")}):");
            foreach (var r in recon.Rows)
                Console.WriteLine($"  {r.Material,-14} installed {r.InstalledPoints,8:N0} pts  theoretical {r.Theoretical,10:N0}  delivered {r.Delivered,10:N0}  invoiced {r.Invoiced,9:N0}  on site {r.OnSite,10:N0}  need {r.TheoreticalTotal,10:N0}  {r.Status}");
            foreach (var u in recon.Unmatched.Take(12)) Console.WriteLine($"  unmatched delivery: {u.Description} {u.Qty:N0} {u.Unit}");
            Console.WriteLine($"  MOS release candidates: {recon.Mos.Count}");

            var cmp = RateBenchmark.Comparable(bench).ToList();
            Console.WriteLine($"\nRATE BENCHMARK: {bench.Count} item groups, {cmp.Count} compare 2+ parties or carry an owner rate, {cmp.Sum(g => g.Outliers)} outlier rates, {cmp.Count(g => g.OwnerRate.HasValue)} with owner rate");
            foreach (var g in cmp.Where(g => g.Parties >= 2).OrderByDescending(g => g.Spread).Take(12))
                Console.WriteLine($"  {g.Spread,5:0.00}x  {g.Min,9:N2} - {g.Max,9:N2} {g.Unit,-5} {g.Sources,-24} {Trim(g.Label, 70)}");

            Console.WriteLine($"\nCASH FLOW ({cash.Months.Count} months to {cash.Finish:MMM yyyy}, delay {delay} weeks): payables SAR {cash.TotalPayables:N0}, owner receipts SAR {cash.TotalReceipts:N0}, lowest cumulative SAR {cash.PeakNegative:N0} ({cash.PeakNegativeMonth:MMM yyyy})");
            foreach (var x in cash.Assumptions) Console.WriteLine("  - " + x);

            Console.WriteLine($"\nEARNED VALUE: SPI {ev.Spi:0.00}, {ev.DatedLines:N0} dated / {ev.UndatedLines:N0} undated lines, {ev.Warnings.Count} early warnings");
            foreach (var p in ev.Productivity) Console.WriteLine($"  {p.Subcontractor,-18} {p.Periods.Count} invoices {p.TotalPoints,9:N0} pts  avg/invoice {p.AvgPerInvoice,8:N0}  avg/week {p.AvgPerWeek?.ToString("N0") ?? "-",6}  trend {p.TrendText}");
            foreach (var w in ev.Warnings.Take(10)) Console.WriteLine($"  [{w.Severity.ToString().ToUpperInvariant()}] {w.Subject}: {w.Message}");
            foreach (var n in ev.Notes) Console.WriteLine("  note: " + n);

            var sheets = new List<ExportSheet> { InsightsEngine.AnomalySheet(anomalies, scope), InsightsEngine.ReconSheet(recon, scope), InsightsEngine.MosSheet(recon, scope) };
            sheets.AddRange(RateBenchmark.Sheets(bench, scope));
            sheets.AddRange(CashFlowForecast.Sheets(cash, scope));
            sheets.AddRange(EarnedValue.Sheets(ev, scope));
            var xlsx = Path.Combine(outDir, "INSIGHTS.xlsx");
            ExcelExporter.Export(xlsx, sheets);
            Console.WriteLine($"\nwritten {xlsx}");
            foreach (var sub in anomalies.Where(x => x.InvoiceNo > 0).Select(x => (x.Subcontractor, x.InvoiceNo)).Distinct().OrderBy(x => x.Subcontractor).ThenByDescending(x => x.InvoiceNo).GroupBy(x => x.Subcontractor).Select(g => g.First()).Take(3))
            {
                var pdf = Path.Combine(outDir, $"insight_checks_{sub.Subcontractor.Replace(' ', '_')}_INV-{sub.InvoiceNo:00}.pdf");
                InsightsEngine.PackagePdf(pdf, $"{sub.Subcontractor} INV-{sub.InvoiceNo:00}", InsightsEngine.ForInvoice(anomalies, sub.Subcontractor, sub.InvoiceNo), today);
                Console.WriteLine($"written {pdf}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 1;
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    private static string? Opt(string[] a, string name)
    {
        var i = Array.IndexOf(a, "--" + name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static List<string> Many(string[] a, string name)
    {
        var res = new List<string>();
        for (var i = 0; i < a.Length - 1; i++) if (a[i] == "--" + name) res.Add(a[i + 1]);
        return res;
    }
}
