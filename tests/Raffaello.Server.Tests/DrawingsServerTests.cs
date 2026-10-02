using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>[drawings] Drawings store on the server: same rules as SQLite, decisions append-only, migration and reset.</summary>
public sealed class DrawingsServerTests
{
    [PgFact]
    public async Task Takeoff_review_and_decisions_through_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var c = srv.Client("qs-a");
        var store = new RemoteDrawingStore(c);
        var sheet = store.SaveSheet(new DwgSheet { Building = Buildings.Branded, SheetNo = "E-101", Revision = "B", FileName = "e101.pdf" });
        var sym = store.SaveSymbol(new DwgSymbol { Name = "socket", System = "POWER", TemplatePng = new byte[] { 1, 2, 3 } });
        Assert.Throws<InvalidOperationException>(() => store.SaveSymbol(new DwgSymbol { Name = "SOCKET" }));
        var hits = Enumerable.Range(0, 5).Select(i => new DwgHit { SymbolId = sym.Id, SymbolName = "SOCKET", X = i * 10, W = 8, H = 8, Room = "R01" }).ToList();
        var t = store.SaveTakeoff(new DwgTakeoff { SheetId = sheet.Id }, hits, new List<DwgRun> { new() { ClassName = "TRAY", Points = "0,0 10,0", LengthM = 1.5 } });
        Assert.True(t.Id > 0);
        var stored = store.Hits(t.Id);
        Assert.Equal(5, stored.Count);
        Assert.All(stored, h => Assert.Equal(t.Id, h.TakeoffId));
        stored[0].Status = DwgHitStatus.Removed;
        store.SaveReview(store.Takeoffs(sheet.Id)[0], stored, store.Runs(t.Id));
        Assert.Equal(4, store.Takeoffs(sheet.Id)[0].Hits);
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Symbols()[0].TemplatePng);

        store.AddDecisions(new[] { new DwgQtyDecision { Room = "R01", Stage = "1ST FIX", Item = "POWER", Proposed = 4, Decision = "ACCEPTED", SheetId = sheet.Id, TakeoffId = t.Id } });
        var d = store.Decisions().Single();
        d.Proposed = 99;
        var ex = Assert.ThrowsAny<Exception>(() => c.Update(d));
        Assert.Contains("audit", ex.Message, StringComparison.OrdinalIgnoreCase);

        store.DeleteSheet(store.Sheet(sheet.Id)!);
        Assert.Empty(store.Hits(t.Id));
        Assert.Empty(store.Sheets());
    }

    [PgFact]
    public async Task Local_drawings_are_migrated_and_reset()
    {
        await using var srv = await TestServer.StartAsync();
        var file = Path.Combine(srv.Folder, "local-dwg.db");
        var local = new Db(file, "mohamed", "LAPTOP");
        local.EnsureSchema();
        var ls = new SqliteDrawingStore(file, "mohamed");
        ls.EnsureSchema();
        var sheet = ls.SaveSheet(new DwgSheet { SheetNo = "E-200", FileName = "e200.pdf" });
        ls.ReplaceRooms(sheet.Id, new[] { new DwgRoom { Room = "R01", Polygon = "0,0 10,0 10,10" } }, "rooms");
        using var admin = srv.Client("admin", Roles.Admin);
        LocalToServerMigrator.Run(local, admin);
        var remote = new RemoteDrawingStore(admin);
        var s = Assert.Single(remote.Sheets());
        Assert.Single(remote.Rooms(s.Id));
        admin.ClearAll();
        Assert.Empty(new RemoteDrawingStore(admin).Sheets());
    }
}
