using Raffaello.Core.Insights;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>[insights] The insights store against the server (tables from the Insights module, reason guard).</summary>
public sealed class InsightsServerTests
{
    [Fact]
    public void Insights_module_is_registered() =>
        Assert.Contains(Raffaello.Server.Data.ServerModules.All, m => m.Name == "Insights" && m.EntityTypes.Contains(typeof(InsightDismissal)));

    [PgFact]
    public async Task Dismissals_norms_and_programme_live_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("qs-a");
        using var b = srv.Client("qs-b");
        var storeA = new RemoteInsightsStore(a);
        var storeB = new RemoteInsightsStore(b);

        Assert.Throws<InvalidOperationException>(() => storeA.Dismiss("SHARED|R1|1ST FIX|POWER", AnomalyKinds.MultiSub, "R1", ""));
        storeA.Dismiss("SHARED|R1|1ST FIX|POWER", AnomalyKinds.MultiSub, "R1 shared", "split agreed with the site team");
        var seen = Assert.Single(storeB.Load().ActiveDismissals().Values);
        Assert.Equal("qs-a", seen.DismissedBy);

        // the guard refuses a dismissal without a reason even when the client check is bypassed
        var ex = Assert.Throws<RemoteRejectedException>(() => b.Insert(new InsightDismissal { Fingerprint = "X", Active = true, Reason = " " }));
        Assert.Equal("reason_required", ex.Error.Code);

        storeB.Restore("SHARED|R1|1ST FIX|POWER", "re-opened");
        Assert.Empty(storeA.Load().ActiveDismissals());

        storeA.ReplaceAll(MaterialReconciliation.DefaultNorms(), "norms");
        var norms = storeB.Load().Norms;
        Assert.Equal(MaterialReconciliation.DefaultNorms().Count, norms.Count);
        norms[0].PerPoint = 9;
        norms.RemoveAt(norms.Count - 1);
        storeB.ReplaceAll(norms, "norms edited");
        var after = storeA.Load().Norms;
        Assert.Equal(norms.Count, after.Count);
        Assert.Contains(after, n => n.PerPoint == 9);

        storeA.Save(new InsightProgramme { Area = "ALL", Stage = "1ST FIX", PlannedStart = new DateTime(2026, 6, 1), PlannedFinish = new DateTime(2026, 12, 1) });
        storeA.Save(new InsightInvoicePeriod { Subcontractor = "ROOTS", InvoiceNo = 1, PeriodEnd = new DateTime(2026, 7, 31) });
        var d = storeB.Load();
        Assert.Single(d.Programme);
        Assert.Single(d.InvoicePeriods);
    }
}
