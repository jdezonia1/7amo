using System.Net.Http.Json;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;
using Raffaello.Core.Remote;
using Raffaello.Core.Variations;

namespace Raffaello.Server.Tests;

/// <summary>[phase6] Server support for the phase-3 / phase-4 stores.</summary>
public sealed class ModuleStoreTests
{
    private static (MatDn Dn, List<MatDnLine> Lines) Dn(IMaterialsStore s)
    {
        var lines = new List<MatDnLine>
        {
            new() { Order = 1, ItemCode = "C-4X16", Description = "4C 16mm2 CU/XLPE/SWA", Qty = 500, Unit = "M" },
            new() { Order = 2, ItemCode = "C-4X25", Description = "4C 25mm2 CU/XLPE/SWA", Qty = 300, Unit = "M" },
        };
        var dn = s.SaveDn(new MatDn { DnNo = "DN-1001", Supplier = "TEST CABLES", PoNo = "PO-1", ImportedAt = DateTime.Now }, lines);
        return (dn, lines);
    }

    [PgFact]
    public async Task Two_users_invoicing_the_same_DN_line_one_wins()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        using var b = srv.Client("qs-b");
        var storeA = new RemoteMaterialsStore(a);
        var storeB = new RemoteMaterialsStore(b);
        var (_, lines) = Dn(storeA);
        var ids = storeA.All<MatDnLine>().Select(l => l.Id).ToList();
        Assert.Equal(2, ids.Count);

        using var start = new ManualResetEventSlim(false);
        var tA = Task.Run(() => { start.Wait(); try { storeA.LockDnLines(ids, "TEST CABLES", "PO-1", 1, 0); return "OK"; } catch (DnLineLockedException) { return "LOCKED"; } });
        var tB = Task.Run(() => { start.Wait(); try { storeB.LockDnLines(ids, "TEST CABLES", "PO-1", 2, 0); return "OK"; } catch (DnLineLockedException) { return "LOCKED"; } });
        start.Set();
        var results = await Task.WhenAll(tA, tB);
        Assert.Equal(1, results.Count(r => r == "OK"));
        Assert.Equal(1, results.Count(r => r == "LOCKED"));

        var locks = storeA.All<MatDnInvoiceLock>();
        Assert.Equal(2, locks.Count);
        Assert.Single(locks.Select(k => k.InvoiceNo).Distinct());

        // the guard also refuses a raw insert that bypasses the client-side check
        var winner = locks[0].InvoiceNo;
        var ex = Assert.Throws<RemoteRejectedException>(() => b.Insert(new MatDnInvoiceLock { DnLineId = ids[0], Supplier = "TEST CABLES", PoNo = "PO-1", InvoiceNo = winner + 10 }));
        Assert.Equal(ErrorCodes.Locked, ex.Error.Code);

        // re-locking for the same invoice is idempotent; releasing frees the lines
        var sameInvoice = new RemoteMaterialsStore(results[0] == "OK" ? a : b);
        sameInvoice.LockDnLines(ids, "TEST CABLES", "PO-1", winner, 0);
        Assert.Equal(2, sameInvoice.ReleaseLocks("TEST CABLES", "PO-1", winner));
        storeB.LockDnLines(ids, "TEST CABLES", "PO-1", 7, 0);
        Assert.All(storeA.All<MatDnInvoiceLock>(), k => Assert.Equal(7, k.InvoiceNo));
        Assert.Throws<DnLineLockedException>(() => storeA.SaveDn(new MatDn { DnNo = "DN-1001", Supplier = "TEST CABLES", PoNo = "PO-1" }, lines));
    }

    [PgFact]
    public async Task Aconex_and_variation_stores_work_against_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var c = srv.Client("qs");
        var ac = new RemoteAconexStore(c);
        var link = ac.Link(42, "dsh-wf-000123");
        Assert.True(link.Id > 0);
        Assert.Equal(link.Id, ac.Link(42, "DSH-WF-000123").Id);
        Assert.Single(ac.ActiveLinks());
        ac.SaveCheck(new WorkflowLookupResult { WorkflowNo = "DSH-WF-000123", State = "IN PROGRESS", CheckedAt = DateTime.Now }, 42);
        Assert.NotNull(ac.LatestCheck("dsh-wf-000123"));
        var job = ac.CreateJob(new DocumentQuery(), new[] { new DocumentHit { DocumentNo = "WIR-1", Revision = "0" }, new DocumentHit { DocumentNo = "WIR-2", Revision = "0" } }, new HashSet<string>());
        Assert.True(job.Id > 0);
        Assert.Equal(2, ac.Queue(job.Id).Count);
        Assert.NotNull(ac.ResumableJob());

        var vs = new RemoteVariationStore(c);
        var v = vs.Create(new Variation { Type = VariationTypes.Vo, Title = "Extra sockets" });
        Assert.Equal("VO-001", v.Number);
        vs.Save(v, new List<VariationLine> { new() { Kind = VariationLineKinds.Addition, Description = "socket", Qty = 4, Rate = 55 } });
        Assert.Single(vs.Lines(v.Id));
        var sub = vs.SetStatus(vs.Get(v.Id)!, VariationStatus.Submitted);
        Assert.Equal(VariationStatus.Submitted, sub.Status);
        Assert.Equal(2, vs.StatusLog(v.Id).Count);
        Assert.Equal("VO-002", vs.NextNumber(VariationTypes.Vo));
    }

    [PgFact]
    public async Task Migration_and_reset_include_the_module_tables()
    {
        await using var srv = await TestServer.StartAsync();
        var file = Path.Combine(srv.Folder, "local-mod.db");
        var local = new Db(file, "mohamed", "LAPTOP");
        local.EnsureSchema();
        local.Insert(new Room { Code = "P1-1" });
        var mats = new SqliteMaterialsStore(local);
        Dn(mats);
        var vs = new SqliteVariationStore(file, "mohamed");
        vs.EnsureSchema();
        vs.Create(new Variation { Type = VariationTypes.Vo, Title = "x" });

        using var admin = srv.Client("admin", Roles.Admin);
        var run = LocalToServerMigrator.Run(local, admin);
        Assert.Contains(run.Tables, t => t.Table == "MatDnLines" && t.ToCopy == 2);
        Assert.Equal(2, new RemoteMaterialsStore(admin).All<MatDnLine>().Count);
        Assert.Single(new RemoteVariationStore(admin).Variations());

        // a data reset clears the module tables on both sides
        local.ClearAll();
        Assert.Empty(mats.All<MatDnLine>());
        Assert.Empty(vs.Variations());
        admin.ClearAll();
        Assert.Empty(new RemoteMaterialsStore(admin).All<MatDn>());
    }
}

public sealed class LoginThrottleTests
{
    [Fact]
    public void Five_wrong_passwords_lock_the_user_then_success_clears()
    {
        var now = new DateTime(2026, 10, 1, 8, 0, 0);
        var t = new Raffaello.Server.Auth.LoginThrottle(new ServerOptions { LoginMaxFailures = 5, LoginWindowMinutes = 15, LoginLockoutMinutes = 5 }) { Clock = () => now };
        for (var i = 0; i < 4; i++) t.Failed("mohamed", "10.0.0.5");
        Assert.Null(t.RetryAfter("mohamed", "10.0.0.5"));
        t.Failed("mohamed", "10.0.0.5");
        Assert.InRange(t.RetryAfter("mohamed", "10.0.0.9")!.Value, 290, 300);   // user locked from any address
        Assert.NotNull(t.RetryAfter("someone", "10.0.0.5"));                     // address locked for any user
        now = now.AddMinutes(6);
        Assert.Null(t.RetryAfter("mohamed", "10.0.0.5"));
        t.Failed("mohamed", "10.0.0.5");
        t.Succeeded("mohamed");
        for (var i = 0; i < 4; i++) t.Failed("mohamed", "10.0.0.7");
        Assert.Null(t.RetryAfter("mohamed", "10.0.0.7"));
    }

    [PgFact]
    public async Task Login_endpoint_returns_429_after_repeated_failures()
    {
        await using var srv = await TestServer.StartAsync();
        srv.Token("eng", Raffaello.Core.Remote.Roles.Qs);
        using var http = new HttpClient { BaseAddress = new Uri(srv.Url) };
        System.Net.HttpStatusCode last = 0;
        for (var i = 0; i < 6; i++)
            last = (await http.PostAsJsonAsync(Raffaello.Core.Remote.ApiRoutes.Login, new { UserName = "eng", Password = "wrong", Machine = "PC" })).StatusCode;
        Assert.Equal((System.Net.HttpStatusCode)429, last);
        var ok = await http.PostAsJsonAsync(Raffaello.Core.Remote.ApiRoutes.Login, new { UserName = "eng", Password = "password-eng", Machine = "PC" });
        Assert.Equal((System.Net.HttpStatusCode)429, ok.StatusCode);   // still locked out, even with the right password
    }
}
