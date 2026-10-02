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
