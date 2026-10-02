using Raffaello.Core.Cables;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>[cables] The cable register and claims on the server: same service rules, RowVersion conflicts, cross-user duplicate flags.</summary>
public sealed class CablesServerTests
{
    [Fact]
    public void Cables_module_is_registered_with_its_tables()
    {
        Assert.Contains(Data.ServerModules.All, m => m.Name == "Cables" && m.EntityTypes.Contains(typeof(CableClaim)));
        Assert.All(CableStore.EntityTypes, t => Assert.Contains(t, ModuleEntities.All));
    }

    private static CableClaim Claim(string sub, int inv, string from, string to, string size, double qty) => new()
    {
        Building = Buildings.Hotel, Subcontractor = sub, InvoiceNo = inv, RawStage = "CABLE PULLING", FromRaw = from, ToRaw = to, SizeRaw = size, Qty = qty,
        Source = "TEST", SourceKey = $"T|{sub}|{inv}|{from}|{to}|{size}",
    };

    [PgFact]
    public async Task Two_users_claims_on_the_same_route_are_flagged_and_bypass_is_shared()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        using var b = srv.Client("qs-b");
        var svcA = new CableService(CableStore.For(a));
        var svcB = new CableService(CableStore.For(b));
        Assert.IsType<RemoteCableStore>(svcA.Store);

        svcA.Import(new[]
        {
            Claim("SAFIA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145),
            Claim("SAFIA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "1x16", 145),
        }, null, "statement A");
        var snapA = svcA.Load();
        var run = Assert.Single(snapA.Runs);
        Assert.True(run.Id > 0);
        Assert.All(snapA.Claims, c => Assert.Equal(run.Id, c.RunId));   // temporary run ids were re-pointed by the server

        var plan = svcB.Prepare(new[] { Claim("ROOTS", 4, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4X16", 140) });
        var dup = Assert.Single(plan.Flags, f => f.Code == CableFlagCodes.Duplicate);
        Assert.Contains("SAFIA INV 1", dup.Message);
        svcB.Commit(plan, "statement B");
        var flag = svcA.Flags().Single(f => f.Code == CableFlagCodes.Duplicate);
        svcA.Bypass(flag, "separate re-pull ordered by the engineer");
        Assert.True(svcB.Flags().Single(f => f.Code == CableFlagCodes.Duplicate).IsBypassed);

        // schedule from user B upgrades the provisional run in place
        var m = svcB.SaveRegister(new[] { new CableRun { Building = Buildings.Hotel, FromName = "SMDB-HT-Z1-LB2-CM-01", ToName = "LDB-HT-Z1-LB2-03", SizeKey = "4x16", DesignLength = 150 } },
            Array.Empty<CablePanel>(), "schedule");
        Assert.Equal(1, m.ProvisionalUpgraded);
        Assert.Equal(150, svcA.Load().Runs.Single().DesignLength);

        // stale edit -> conflict
        var r1 = svcA.Load().Runs.Single();
        var r2 = svcB.Load().Runs.Single();
        r1.Notes = "A"; svcA.UpdateRun(r1, "A edits");
        r2.Notes = "B";
        Assert.ThrowsAny<Exception>(() => svcB.UpdateRun(r2, "B edits stale copy"));
    }
}
