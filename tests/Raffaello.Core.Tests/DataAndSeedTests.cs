using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;
using Raffaello.Core.Seed;

namespace Raffaello.Core.Tests;

public class DbTests
{
    [Fact]
    public void Insert_StampsAuditFields_AndLogs()
    {
        var db = TestData.NewDb();
        var room = db.Insert(new Room { Code = "101", Level = "L1" });
        Assert.True(room.Id > 0);
        Assert.Equal(1, room.RowVersion);
        Assert.Equal("tester", room.UpdatedBy);
        var audit = db.RecentAudit(10);
        Assert.Contains(audit, a => a.TableName == "Rooms" && a.Action == "INSERT" && a.RowId == room.Id);
    }

    [Fact]
    public void Update_IncrementsRowVersion_AndRecordsDiff()
    {
        var db = TestData.NewDb();
        var line = db.Insert(new QtyLine { Room = "101", QsQty = 10, ProjectQty = null });
        line.ProjectQty = 9;
        db.Update(line);
        Assert.Equal(2, line.RowVersion);
        var stored = db.Get<QtyLine>(line.Id)!;
        Assert.Equal(9, stored.ProjectQty);
        Assert.Equal(2, stored.RowVersion);
        var upd = db.RecentAudit(5).First(a => a.Action == "UPDATE");
        Assert.Contains("ProjectQty", upd.Changes);
    }

    [Fact]
    public void Update_WithStaleRowVersion_ThrowsConcurrency()
    {
        var db = TestData.NewDb();
        var a = db.Insert(new QtyLine { Room = "101", QsQty = 10 });
        var copyA = db.Get<QtyLine>(a.Id)!;
        var copyB = db.Get<QtyLine>(a.Id)!;
        copyA.SitePct = 0.5;
        db.Update(copyA);
        copyB.SitePct = 0.9;
        var ex = Assert.Throws<ConcurrencyException>(() => db.Update(copyB));
        Assert.Equal("tester", ex.ChangedBy);
        Assert.Equal(0.5, db.Get<QtyLine>(a.Id)!.SitePct);
    }

    [Fact]
    public void Store_UsesWalMode()
    {
        var db = TestData.NewDb();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (cmd.ExecuteScalar() as string)?.ToLowerInvariant());
    }

    [Fact]
    public void RoundTrips_DatesNullablesAndBools()
    {
        var db = TestData.NewDb();
        var at = new DateTime(2026, 9, 30, 14, 5, 0);
        var w = db.Insert(new Wir { WirNo = "WIR-1", SubmittedAt = at, ApprovedAt = null });
        var l = db.Insert(new WirLine { WirId = w.Id, LineId = 3, Qty = 2.5, IsRework = true });
        var w2 = db.Get<Wir>(w.Id)!;
        Assert.Equal(at, w2.SubmittedAt);
        Assert.Null(w2.ApprovedAt);
        Assert.True(db.Get<WirLine>(l.Id)!.IsRework);
    }

    [Fact]
    public void Presence_HeartbeatShowsOtherUsers()
    {
        var path = Path.Combine(TestData.TempDir(), "p.db");
        var me = new Db(path, "mohamed", "PC1"); me.EnsureSchema();
        var other = new Db(path, "ahmed", "PC2");
        me.Heartbeat("Dashboard");
        other.Heartbeat("Quantities");
        var others = me.OthersOnline(TimeSpan.FromMinutes(3));
        Assert.Single(others);
        Assert.Equal("ahmed", others[0].User);
    }

    [Fact]
    public void Schema_AddsMissingColumns()
    {
        var db = TestData.NewDb();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS X (Id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }
        db.EnsureSchema(); // second run is a no-op and must not throw
        Assert.Equal(0, db.Count<Room>());
    }
}

public class SeedTests
{
    private static readonly Lazy<(Db db, ProjectSnapshot s, List<ChainRow> chain, RuleOptions o)> Seeded = new(() =>
    {
        var db = TestData.NewDb();
        new DemoSeeder(new DateTime(2026, 10, 2)).Seed(db);
        var o = new RuleOptions { Today = new DateTime(2026, 10, 2) };
        var s = ProjectSnapshot.Load(db);
        return (db, s, ChainBuilder.Build(s, new RulesEngine(o)), o);
    });

    [Fact]
    public void Seeds_389Wirs()
    {
        Assert.Equal(DemoSeeder.TargetWirCount, Seeded.Value.s.Wirs.Count(w => w.Kind == "WIR"));
    }

    [Fact]
    public void Seeds_HotelEnlargedRoomPoints_PerStage()
    {
        var hotelRooms = Seeded.Value.s.Lines.Where(l => l.Building == Buildings.Hotel && l.RoomType.StartsWith("ER-")).ToList();
        foreach (var stage in Stages.All)
        {
            var byStage = hotelRooms.Where(l => l.Stage == stage).ToList();
            Assert.Equal(4401, byStage.Sum(l => l.QsQty));
            foreach (var (sys, pts) in DemoSeeder.HotelPoints)
                Assert.Equal(pts, byStage.Where(l => l.System == sys).Sum(l => l.QsQty));
        }
        Assert.Equal(28, hotelRooms.Select(l => l.RoomType).Distinct().Count());
    }

    [Fact]
    public void Seeds_TheBanderSeifCopyStatement()
    {
        var copies = InvoiceCopyDetector.Detect(Seeded.Value.s.Invoices, Seeded.Value.s.InvoiceLines);
        var c = Assert.Single(copies);
        Assert.Equal("BANDER SEIF", c.Invoice.Subcontractor);
        Assert.Equal("INV-03", c.Invoice.InvoiceNo);
        Assert.Equal("INV-02", c.CopyOf.InvoiceNo);
    }

    [Fact]
    public void Chain_HasEveryVerdictAndQueueItems()
    {
        var (_, s, chain, o) = Seeded.Value;
        Assert.Contains(chain, r => r.Verdict == Verdict.Over);
        Assert.Contains(chain, r => r.Verdict == Verdict.Check);
        Assert.Contains(chain, r => r.Verdict == Verdict.Ok || r.Verdict == Verdict.Open);
        var q = NeedsTodayQueue.Build(s, chain, o);
        Assert.Contains(q, i => i.Category == "STATEMENT" && i.Title.Contains("INV-03"));
        Assert.Contains(q, i => i.Category == "MATERIAL" && i.Title.Contains("delivered > PO"));
        Assert.Contains(q, i => i.Category == "MATERIAL" && i.Title.Contains("PO total"));
        Assert.Contains(q, i => i.Title.Contains("AWRAD INV-01"));
        Assert.True(q.Zip(q.Skip(1)).All(p => p.First.Severity >= p.Second.Severity));
    }

    [Fact]
    public void Chain_ClaimedIsLatestStatement_NotSumOfStatements()
    {
        var (_, s, chain, _) = Seeded.Value;
        var maruf = s.Invoices.Where(i => i.Subcontractor == "MARUF").OrderBy(i => i.InvDate).ToList();
        var latest = maruf.Last();
        var line = s.InvoiceLines.First(l => l.InvoiceId == latest.Id && s.InvoiceLines.Count(x => x.LineId == l.LineId) > 1);
        Assert.Equal(line.CumQty, chain.First(r => r.Id == line.LineId).Claimed);
    }

    [Fact]
    public void Analytics_RunOnSeed()
    {
        var (_, s, chain, o) = Seeded.Value;
        var curve = ProjectAnalytics.SCurve(s, chain, o.Today.AddDays(-7 * 38), o.Today.AddDays(7 * 28), o.Today);
        Assert.True(curve.Count > 30);
        var actual = curve.Where(c => c.Actual.HasValue).Select(c => c.Actual!.Value).ToList();
        Assert.True(actual.Zip(actual.Skip(1)).All(p => p.Second >= p.First - 1e-9), "cumulative actual must not decrease");
        Assert.True(actual.Last() > 0.2);
        Assert.NotNull(ProjectAnalytics.ForecastCompletion(curve).CompletionDate);
        Assert.Equal(4, ProjectAnalytics.WirAgeing(s.Wirs, o.Today).Count);
        Assert.NotEmpty(ProjectAnalytics.CashFlow(s.Invoices));
        Assert.NotEmpty(ProjectAnalytics.GivenHeatmap(chain));
        var pts = ProjectAnalytics.PointsBySystem(chain.Where(r => r.Building == Buildings.Hotel && r.RoomType.StartsWith("ER-")));
        Assert.Equal(2697, pts.First(p => p.Name == Systems.Light).Value);
        var mats = MaterialAnalysis.All(s);
        Assert.Contains(mats, m => !m.TotalCheck.Matches);
        Assert.Contains(mats, m => m.OverCount > 0);
        Assert.NotEmpty(StatementAnalysis.Scorecard(s, chain));
    }

    [Fact]
    public void ClaimBuilder_CertifiesMinOfClaimDoneCap()
    {
        var (_, s, chain, _) = Seeded.Value;
        var byId = chain.ToDictionary(c => c.Id);
        var draft = ClaimBuilder.Build(s, byId, "MARUF");
        Assert.Equal("INV-04", draft.Statement!.InvoiceNo);
        Assert.All(draft.Lines, l => Assert.True(l.Certifiable <= Math.Min(l.Claimed, Math.Min(l.Done, l.Cap)) + 1e-9));
        Assert.True(draft.HeldValue > 0);
        Assert.True(draft.NetPayable < draft.ThisPeriodGross);
    }
}
