using Raffaello.Core.Cables;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Tracker;

namespace Raffaello.Cli;

/// <summary>
/// [cables] Panel &amp; cable register verification commands (outputs contain project data: keep --db / --out outside the repository):
///   raffaello-cli cables-import-tracker FILE [--db PATH] [--out DIR]     tracker incl. 'CABLES BRANDED' / 'CABLES HOTEL' + ledger CABLE PULLING
///   raffaello-cli cables-report [--db PATH] [--out DIR] [--top N]        counts, alias merges, duplicate FROM-TO flags, unknown runs (+ Excel)
///   raffaello-cli cables-read FILE.pdf|.dxf|.dwg|.xlsx [--db PATH] [--save] [--building B]   SLD / drawing / cable schedule -> runs
/// </summary>
public static class CableCommands
{
    private static readonly HashSet<string> Names = new() { "cables-import-tracker", "cables-report", "cables-read" };
    public static bool Handles(string cmd) => Names.Contains(cmd);

    public static int Run(string[] args)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--")) { var k = args[i][2..]; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) opts[k] = args[++i]; else opts[k] = "true"; }
            else pos.Add(args[i]);
        }
        var db = new Db(opts.GetValueOrDefault("db", Path.Combine(Environment.CurrentDirectory, "raffaello-cli.db")), "cli");
        db.EnsureSchema();
        try
        {
            switch (args[0])
            {
                case "cables-import-tracker":
                    {
                        var r = TrackerImporter.Read(pos[0], opts.GetValueOrDefault("building", Buildings.Branded), includePlans: false);
                        Console.WriteLine(r.Summary);
                        Console.WriteLine($"  cable sheet rows: {r.CableClaims.Count} ({string.Join(", ", r.CableClaims.GroupBy(c => c.Building).Select(g => $"{g.Key} {g.Count()}"))})");
                        Console.WriteLine($"  ledger CABLE PULLING lines: {r.Claims.Count(c => CableStages.IsLedgerCableStage(c.Stage))}");
                        foreach (var g in r.Issues.Where(i => i.Message.StartsWith("CABLES", StringComparison.OrdinalIgnoreCase)).GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"row \d+", "row #")).Take(8))
                            Console.WriteLine($"  {g.First().LevelText} x{g.Count()}: {g.Key}");
                        TrackerImporter.Commit(r, db);
                        Console.WriteLine("  cables: " + r.CableSummary);
                        Report(db, opts);
                        return 0;
                    }
                case "cables-report": Report(db, opts); return 0;
                case "cables-read": Read(db, pos[0], opts); return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex);
            return 1;
        }
        return 2;
    }

    private static void Report(Db db, Dictionary<string, string> opts)
    {
        var svc = new CableService(CableStore.For(db));
        var snap = svc.Load();
        var flags = svc.Flags(snap);
        var top = int.Parse(opts.GetValueOrDefault("top", "10"));
        Console.WriteLine();
        Console.WriteLine($"CABLE REGISTER: {snap.Runs.Count} runs ({string.Join(", ", snap.Runs.GroupBy(r => r.Status).Select(g => $"{g.Key} {g.Count()}"))}), " +
                          $"{snap.Panels.Count} panels/equipment after normalisation ({snap.Panels.Count(p => !p.IsEquipment)} boards, {snap.Panels.Count(p => p.IsEquipment)} equipment)");
        var spellings = snap.Claims.SelectMany(c => new[] { (c.FromKey, c.FromRaw), (c.ToKey, c.ToRaw) }).Where(x => x.Item1.Length > 0)
            .GroupBy(x => x.Item1).Select(g => (Key: g.Key, Names: g.Select(x => x.Item2.Trim()).Distinct().ToList())).Where(x => x.Names.Count > 1).ToList();
        Console.WriteLine($"ALIAS MERGES by the normaliser: {spellings.Count} panels written {spellings.Sum(s => s.Names.Count)} ways");
        foreach (var s in spellings.OrderByDescending(s => s.Names.Count).Take(top)) Console.WriteLine($"  {s.Key}: {string.Join(" | ", s.Names.Select(n => "'" + n + "'"))}");
        var sugg = CableService.AliasSuggestions(snap);
        Console.WriteLine($"ALIAS SUGGESTIONS (need confirm): {sugg.Count}");
        foreach (var (a, b, sc) in sugg.Take(top)) Console.WriteLine($"  {sc:0.00}  '{a.Name}'  ~  '{b.Name}'");
        Console.WriteLine($"CLAIMS: {snap.Claims.Count} ({string.Join(", ", snap.Claims.GroupBy(c => c.Source).Select(g => $"{g.Key} {g.Count()}"))}); " +
                          $"linked to ledger lines {snap.Claims.Count(c => c.LedgerSourceKey.Length > 0)}; earth companions {snap.Claims.Count(c => c.IsEarth)}; " +
                          $"{snap.Claims.Sum(c => c.Qty):N1} m claimed");
        foreach (var g in snap.Claims.GroupBy(c => (c.Building, c.Subcontractor, c.InvoiceNo)).OrderBy(g => g.Key.Building).ThenBy(g => g.Key.Subcontractor).ThenBy(g => g.Key.InvoiceNo))
            Console.WriteLine($"  {g.Key.Building} {g.Key.Subcontractor} INV {g.Key.InvoiceNo}: {g.Count()} claims, {g.Sum(c => c.Qty):N1} m");
        Console.WriteLine($"FLAGS: {CableFlagEngine.Summary(flags)}");
        foreach (var code in CableFlagCodes.All)
        {
            var list = flags.Where(f => f.Code == code).ToList();
            if (list.Count == 0) continue;
            Console.WriteLine($"  {code}: {list.Count}");
            foreach (var f in list.Take(code == CableFlagCodes.Duplicate ? top : Math.Min(top, 5))) Console.WriteLine($"    {f.ClaimText} :: {f.Message}");
        }
        var unknown = snap.Claims.Where(c => snap.Run(c.RunId) is not { Status: CableStatus.Proposed or CableStatus.Confirmed }).ToList();
        Console.WriteLine($"UNKNOWN RUNS (claims not on a design run): {unknown.Count} claims on {unknown.Select(c => c.RunId).Distinct().Count()} runs; " +
                          $"incomplete names: {unknown.Count(c => PanelNames.Parse(c.FromRaw).IsIncomplete || PanelNames.Parse(c.ToRaw).IsIncomplete)}");
        if (opts.TryGetValue("out", out var outDir))
        {
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "CABLE_REGISTER.xlsx");
            ExcelExporter.Export(path, CableReports.Export(snap, flags));
            Console.WriteLine("Excel: " + path);
        }
    }

    private static void Read(Db db, string file, Dictionary<string, string> opts)
    {
        var svc = new CableService(CableStore.For(db));
        var building = opts.GetValueOrDefault("building", "");
        var read = CableReaders.Read(file, new CableReadOptions { Building = building, Profiles = svc.Load().Profiles });
        Console.WriteLine(read.Summary);
        foreach (var r in read.Runs.Take(int.Parse(opts.GetValueOrDefault("top", "40"))))
            Console.WriteLine($"  {r.FromName} -> {r.ToName}  {r.SizeKey}{(r.EarthSizeKey.Length > 0 ? " + E " + r.EarthSizeKey : "")}  {(r.DesignLength is { } d ? d.ToString("0.#") + " m" : "")}  {r.Breaker}  conf {r.Confidence:0.00}  p{r.SourcePage}");
        foreach (var i in read.Issues.Take(20)) Console.WriteLine("  " + i);
        if (opts.ContainsKey("save"))
        {
            var m = svc.SaveRegister(read.Runs, read.Panels, $"{read.Kind} {Path.GetFileName(file)}");
            Console.WriteLine($"Saved: {m.RunsAdded} runs added, {m.RunsUpdated} updated, {m.ProvisionalUpgraded} provisional upgraded, {m.PanelsAdded} panels added; {m.Conflicts.Count} conflicts");
            Console.WriteLine($"Re-matched claims: {svc.Rematch()}");
        }
    }
}
