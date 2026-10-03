using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Recon;

namespace Raffaello.Cli;

/// <summary>
/// HOTEL RECON (new hotel 100% total + cleaned past claims; outputs contain project data - keep them out of the repo):
///   raffaello-cli recon-hotel --project HOTEL_PROJECT_QTY.xlsx --claims HOTEL_REMAINING.xlsx [--db PATH] [--dry-run] [--replace-hotel-ledger]
/// --dry-run reads and checks only (nothing written). Without it the HOTEL PROJECT QTY is replaced and the HOTEL claim lines with
/// Source RECON are replaced; --replace-hotel-ledger also removes every other HOTEL claim line (TRACKER, MANUAL ...).
/// </summary>
public static class ReconCommands
{
    public static bool Handles(string cmd) => cmd == "recon-hotel";

    public static int Run(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal)) o[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        try
        {
            var project = o.TryGetValue("project", out var p) ? p : throw new ArgumentException("--project is required");
            var claims = o.TryGetValue("claims", out var c) ? c : throw new ArgumentException("--claims is required");
            var dry = o.ContainsKey("dry-run");
            var replaceAll = o.ContainsKey("replace-hotel-ledger");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var r = HotelReconImporter.Read(project, claims);
            Console.WriteLine("READ  " + r.Summary);
            Console.WriteLine($"  area types: {string.Join(", ", r.Rooms.GroupBy(x => x.AreaType).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
            Console.WriteLine($"  claim lines by subcontractor: {string.Join(", ", r.Claims.GroupBy(x => x.Subcontractor).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
            foreach (var g in r.Issues.GroupBy(i => System.Text.RegularExpressions.Regex.Replace(i.Message, @"(row \d+|location ).*$", "*")).OrderByDescending(g => g.Count()).Take(12))
                Console.WriteLine($"  {g.First().LevelText} x{g.Count()}: {g.First().Message}");
            Console.WriteLine($"  issues: {r.Issues.Count(i => i.Level == Raffaello.Core.Import.IssueLevel.Error)} errors, {r.Issues.Count(i => i.Level == Raffaello.Core.Import.IssueLevel.Warning)} warnings");

            var fileCheck = ReconCheck.Run(r);
            Console.WriteLine("CHECK (files) " + fileCheck.Summary);
            PrintKeys(fileCheck);

            if (!o.TryGetValue("db", out var dbPath))
            {
                if (!dry) throw new ArgumentException("--db is required unless --dry-run");
                Console.WriteLine($"DRY RUN - nothing written ({sw.ElapsedMilliseconds:N0} ms)");
                return 0;
            }
            var store = new Db(dbPath, "cli");
            store.EnsureSchema();
            var hotelClaims = store.All<ClaimLine>().Where(x => x.Building == Buildings.Hotel).ToList();
            Console.WriteLine($"DB {dbPath}: HOTEL now {store.All<RoomQty>().Count(q => q.Building == Buildings.Hotel)} PROJECT QTY cells, {hotelClaims.Count} claim lines " +
                              $"({string.Join(", ", hotelClaims.GroupBy(x => x.Source).Select(g => $"{g.Key} {g.Count()}"))})");
            if (dry)
            {
                var toRemove = hotelClaims.Count(x => replaceAll || x.Source == HotelReconImporter.ClaimSource);
                Console.WriteLine($"DRY RUN - would replace HOTEL PROJECT QTY, remove {toRemove} HOTEL claim lines and insert {r.Claims.Count} RECON lines; nothing written ({sw.ElapsedMilliseconds:N0} ms)");
                return 0;
            }
            var (rooms, qty, inserted, removed) = HotelReconImporter.Commit(r, store, replaceAll);
            Console.WriteLine($"COMMITTED {rooms} locations upserted, {qty} PROJECT QTY cells, {inserted} RECON lines inserted, {removed} old HOTEL lines removed");

            var dbQty = store.All<RoomQty>().Where(q => q.Building == Buildings.Hotel).ToList();
            var dbClaims = store.All<ClaimLine>().Where(x => x.Building == Buildings.Hotel).ToList();
            var reconOnly = ReconCheck.Run(dbQty, dbClaims.Where(x => x.Source == HotelReconImporter.ClaimSource));
            Console.WriteLine("CHECK (db, RECON lines only) " + reconOnly.Summary);
            var others = dbClaims.Count(x => x.Source != HotelReconImporter.ClaimSource);
            if (others > 0)
            {
                var all = ReconCheck.Run(dbQty, dbClaims);
                Console.WriteLine($"CHECK (db, ALL {dbClaims.Count} HOTEL lines incl. {others} non-RECON) " + all.Summary);
                Console.WriteLine("  WARNING: non-RECON hotel lines also count in the ledger balances; use --replace-hotel-ledger to make the recon the only hotel ledger.");
            }
            Console.WriteLine($"({sw.ElapsedMilliseconds:N0} ms)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex.Message);
            return 1;
        }
    }

    private static void PrintKeys(ReconCheckResult c)
    {
        Console.WriteLine($"  {"STAGE|ITEM",-30} {"TOTAL",12} {"CLAIMED",12} {"REMAINING",12} {"OVER ROOMS",10}");
        foreach (var k in c.ByStageItem)
            Console.WriteLine($"  {k.Stage + "|" + k.Item,-30} {k.Total,12:N2} {k.Claimed,12:N2} {k.Remaining,12:N2} {k.OverRooms,10}");
    }
}