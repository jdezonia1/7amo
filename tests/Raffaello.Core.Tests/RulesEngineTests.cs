using Raffaello.Core.Domain;
using Raffaello.Core.Rules;
using static Raffaello.Core.Tests.TestData;

namespace Raffaello.Core.Tests;

public class RulesEngineTests
{
    private static readonly RuleOptions O = new() { WirDueDays = 14, SiteTolerance = 0.15 };

    [Fact]
    public void GivenOverQs_IsOver_AndPostedAtFullQty()
    {
        var r = Row(qs: 100, given: 108, done: 0);
        var f = new GivenOverQsRule().Evaluate(r, O).Single();
        Assert.Equal(Verdict.Over, f.Severity);
        Assert.Contains("full qty", f.Message);
        Assert.Contains("+8", f.Message);
        Assert.Equal(108, r.Given); // nothing trimmed
    }

    [Fact]
    public void ClaimAboveProjectQty_IsOver()
    {
        var r = Row(qs: 100, project: 90, given: 100, done: 95, claimed: 95);
        var f = new ClaimOverCapRule().Evaluate(r, O).Single();
        Assert.Equal(Verdict.Over, f.Severity);
        Assert.Contains("PROJECT QTY", f.Message);
    }

    [Fact]
    public void ClaimCap_FallsBackToQs_WhenProjectQtyEmpty()
    {
        var under = Row(qs: 100, project: null, claimed: 100, done: 100, given: 100);
        Assert.Empty(new ClaimOverCapRule().Evaluate(under, O));
        var over = Row(qs: 100, project: null, claimed: 101, done: 101, given: 101);
        Assert.Contains("QS (PROJECT QTY empty)", new ClaimOverCapRule().Evaluate(over, O).Single().Message);
    }

    [Fact]
    public void ClaimedAboveDone_IsCheck_HoldBeforeCertifying()
    {
        var r = Row(qs: 100, given: 80, done: 40, claimed: 55);
        var f = new ClaimOverDoneRule().Evaluate(r, O).Single();
        Assert.Equal(Verdict.Check, f.Severity);
        Assert.Contains("hold before certifying", f.Message);
        Assert.True(ClaimRules.NeedsHold(55, 40));
    }

    [Fact]
    public void ClaimEqualDone_NoCheck()
    {
        Assert.Empty(new ClaimOverDoneRule().Evaluate(Row(qs: 100, given: 80, done: 40, claimed: 40), O));
    }

    [Fact]
    public void DoneAboveGiven_IsCheck()
    {
        Assert.Equal(Verdict.Check, new DoneOverGivenRule().Evaluate(Row(qs: 100, given: 50, done: 60), O).Single().Severity);
    }

    [Fact]
    public void SiteAheadOfWir_IsDue_WirAheadOfSite_IsCheck()
    {
        var siteAhead = Row(qs: 100, given: 100, done: 40, sitePct: 0.70);
        Assert.Equal(Verdict.Due, new SiteVsWirRule().Evaluate(siteAhead, O).Single().Severity);
        var wirAhead = Row(qs: 100, given: 100, done: 90, sitePct: 0.50);
        Assert.Equal(Verdict.Check, new SiteVsWirRule().Evaluate(wirAhead, O).Single().Severity);
        var close = Row(qs: 100, given: 100, done: 60, sitePct: 0.70);
        Assert.Empty(new SiteVsWirRule().Evaluate(close, O));
    }

    [Fact]
    public void OldOpenWir_IsDue()
    {
        Assert.Equal(Verdict.Due, new OpenWirDueRule().Evaluate(Row(openWirs: 1, oldestDays: 20), O).Single().Severity);
        Assert.Empty(new OpenWirDueRule().Evaluate(Row(openWirs: 1, oldestDays: 5), O));
    }

    [Fact]
    public void Engine_TakesWorstFinding()
    {
        var engine = new RulesEngine(O);
        var r = Row(qs: 100, project: 90, given: 110, done: 50, claimed: 95, sitePct: 0.5);
        engine.Apply(r);
        Assert.Equal(Verdict.Over, r.Verdict);
        Assert.Contains(r.Findings, f => f.Severity == Verdict.Check);
        Assert.Equal("OVER", r.Status);
    }

    [Fact]
    public void Engine_CleanCompleteLine_IsOk()
    {
        var engine = new RulesEngine(O);
        var r = Row(qs: 100, given: 100, done: 100, claimed: 100, sitePct: 1.0);
        engine.Apply(r);
        Assert.Equal(Verdict.Ok, r.Verdict);
    }

    [Fact]
    public void Engine_RemainingToGive_IsOpen()
    {
        var engine = new RulesEngine(O);
        var r = Row(qs: 100, given: 60, done: 60, claimed: 60, sitePct: 0.6);
        engine.Apply(r);
        Assert.Equal(Verdict.Open, r.Verdict);
        Assert.Equal(40, r.Remaining);
    }

    [Fact]
    public void Engine_AcceptsCustomRuleSet()
    {
        var engine = new RulesEngine(O, new IChainRule[] { new ClaimOverDoneRule() });
        var r = Row(qs: 100, given: 200, done: 10, claimed: 10);
        engine.Apply(r);
        Assert.Equal(Verdict.Ok, r.Verdict); // GIVEN>QS rule not registered
        Assert.Single(engine.Rules);
    }
}
