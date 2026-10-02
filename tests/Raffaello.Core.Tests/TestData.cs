using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

internal static class TestData
{
    public static ChainRow Row(double qs = 100, double? project = null, double given = 0, double done = 0, double claimed = 0,
        string stage = Stages.First, double sitePct = 0, int openWirs = 0, int oldestDays = 0, string system = Systems.Light, double rate = 10)
    {
        var line = new QtyLine
        {
            Id = Interlocked.Increment(ref _id), Building = Buildings.Hotel, Level = "L1", Room = "101", System = system, Stage = stage,
            QsQty = qs, ProjectQty = project, SitePct = sitePct, Rate = rate,
        };
        return new ChainRow { Line = line, Given = given, Done = done, Claimed = claimed, OpenWirs = openWirs, OldestOpenWirDays = oldestDays, LastWirNo = openWirs > 0 ? "WIR-1" : "" };
    }

    private static long _id;

    /// <summary>
    /// Deletes a temp SQLite file and its -wal / -shm. Microsoft.Data.Sqlite pools connections, so on Windows the file stays open
    /// (IOException "being used by another process") until the pool is cleared; cleanup of a temp file never fails a test.
    /// </summary>
    public static void DeleteDb(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            for (var attempt = 0; File.Exists(f); attempt++)
            {
                try { File.Delete(f); }
                catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
                catch (IOException) { break; }
            }
    }

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "raffaello-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static Db NewDb()
    {
        var db = new Db(Path.Combine(TempDir(), "test.db"), "tester", "TESTPC");
        db.EnsureSchema();
        return db;
    }
}
