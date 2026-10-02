using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;
using static Raffaello.Core.Tests.TestData;

namespace Raffaello.Core.Tests;

public class HouseRulesTests
{
    [Fact]
    public void Stages_AreNeverSummed_SingleStageTotalThrowsOnMix()
    {
        var rows = new[] { Row(qs: 10, stage: Stages.First), Row(qs: 10, stage: Stages.Second) };
        Assert.Throws<StageMixException>(() => ChainMath.SingleStageTotal(rows));
    }

    [Fact]
    public void TotalsByStage_KeepsStagesSeparate()
    {
        var rows = new[]
        {
            Row(qs: 10, given: 10, stage: Stages.First), Row(qs: 20, given: 5, stage: Stages.First),
            Row(qs: 10, given: 2, stage: Stages.Second), Row(qs: 20, given: 0, stage: Stages.Final),
        };
        var t = ChainMath.TotalsByStage(rows);
        Assert.Equal(3, t.Count);
        Assert.Equal(30, t[Stages.First].Qs);
        Assert.Equal(15, t[Stages.First].Given);
        Assert.Equal(10, t[Stages.Second].Qs);
        Assert.Equal(new[] { Stages.First, Stages.Second, Stages.Final }, t.Keys.ToArray());
    }

    [Fact]
    public void SingleStageTotal_SumsWithinOneStage()
    {
        var t = ChainMath.SingleStageTotal(new[] { Row(qs: 10, given: 4, stage: Stages.Final), Row(qs: 5, given: 5, stage: Stages.Final) });
        Assert.Equal(15, t.Qs);
        Assert.Equal(9, t.Given);
    }

    [Fact]
    public void MeanStagePct_AveragesStageRatios()
    {
        var rows = new[] { Row(qs: 100, given: 100, stage: Stages.First), Row(qs: 100, given: 0, stage: Stages.Second) };
        Assert.Equal(0.5, ChainMath.MeanStagePct(rows, t => t.GivenPct), 6);
    }

    [Fact]
    public void Exceeded_PostsAtFullQty_WithOverFlag()
    {
        var p = PostingRules.Post(120, 100);
        Assert.True(p.IsOver);
        Assert.Equal(120, p.Posted);
        Assert.Equal(20, p.Excess);
        Assert.False(PostingRules.Post(100, 100).IsOver);
    }

    [Theory]
    [InlineData(false, WirStatus.Approved, Systems.Light, true)]
    [InlineData(true, WirStatus.Approved, Systems.Light, false)]   // rework line
    [InlineData(false, WirStatus.Approved, Systems.Emt, false)]    // EMT is rework
    [InlineData(false, WirStatus.Open, Systems.Light, false)]      // not approved yet
    [InlineData(false, WirStatus.Rejected, Systems.Light, false)]
    public void Emt_And_Rework_NeverCountAsProgress(bool rework, string status, string system, bool expected)
    {
        var wir = new Wir { Status = status, System = system, Kind = "WIR" };
        Assert.Equal(expected, ProgressRules.CountsAsProgress(wir, new WirLine { IsRework = rework, Qty = 5 }));
    }

    [Fact]
    public void ProjectQty_CapsCertifiable()
    {
        Assert.Equal(90, ClaimRules.CertifiableQty(claimed: 100, done: 95, cap: 90));
        Assert.Equal(40, ClaimRules.CertifiableQty(claimed: 60, done: 40, cap: 90));
        Assert.Equal(30, ClaimRules.CertifiableQty(claimed: 30, done: 40, cap: 90));
        Assert.Equal(0, ClaimRules.CertifiableQty(claimed: -5, done: 40, cap: 90));
    }

    [Fact]
    public void Invoices_UseWirPct_ComparisonsUseSitePct()
    {
        Assert.Equal(40, ClaimRules.InvoiceQtyFromWirPct(100, 0.4), 6);
        Assert.Equal(70, ProgressRules.SiteQty(100, 0.7), 6);
        Assert.Equal(0.3, ProgressRules.SiteGap(0.7, 0.4), 6);
    }

    [Fact]
    public void Pipes_PcsToM_Default3m_AndConfigurable()
    {
        Assert.Equal(30, UnitConverter.PcsToM(10));
        Assert.Equal(60, UnitConverter.PcsToM(10, 6));
        Assert.Equal(20, UnitConverter.MToPcs(60));
        Assert.Equal(30, UnitConverter.PcsToM(10, 0)); // 0 = default
        Assert.Equal(300, UnitConverter.ToPoUnit(100, "PCS", "M"));
        Assert.Equal(10, UnitConverter.ToPoUnit(30, "M", "PCS"));
        Assert.Equal(42, UnitConverter.ToPoUnit(42, "NO", "NO"));
    }

    [Fact]
    public void DeliveredAbovePo_IsOverAlarm()
    {
        Assert.True(MaterialRules.IsOverPo(1460, 1400));
        Assert.False(MaterialRules.IsOverPo(1400, 1400));
    }

    [Fact]
    public void PoTotalCheck_LinesMustAddUpToStatedTotal()
    {
        var lines = new[] { (10.0, 5.0), (4.0, 2.5) }; // 50 + 10 = 60
        Assert.True(MaterialRules.CheckPoTotal(lines, 60).Matches);
        var bad = MaterialRules.CheckPoTotal(lines, 1310);
        Assert.False(bad.Matches);
        Assert.Equal(-1250, bad.Difference);
        Assert.Equal(60, MaterialRules.DeliveredValue(lines));
    }

    [Theory]
    [InlineData("TWIN SOCKET 13A", Stages.Second, 1)]
    [InlineData("TWIN_SOCKET", Stages.Final, 1)]
    [InlineData("TWIN DATA", Stages.Second, 2)]
    [InlineData("TWIN-DATA-CAT6A", Stages.Second, 2)]
    [InlineData("TWIN DATA", Stages.First, 1)]
    [InlineData("TWIN DATA", Stages.Final, 1)]
    [InlineData("TV+SOUNDBAR", Stages.Second, 1)]
    public void Points_TwinSocketOne_TwinDataTwoAtSecondFix(string block, string stage, int points)
    {
        Assert.Equal(points, PointRules.PointsFor(block, stage));
    }

    [Theory]
    [InlineData("CONTROL", Systems.Grms)]
    [InlineData("EM", Systems.Emergency)]
    [InlineData("EV", Systems.Evacuation)]
    [InlineData("SWITCH", Systems.Light)]
    [InlineData("LIGHTING", Systems.Light)]
    [InlineData("CONTROL-CP4", Systems.Grms)]
    [InlineData("power", Systems.Power)]
    public void SystemMap_FollowsProjectNaming(string raw, string expected)
    {
        Assert.Equal(expected, SystemMap.Normalize(raw));
    }

    [Theory]
    [InlineData("1st fix", Stages.First)]
    [InlineData("2ND-FIX", Stages.Second)]
    [InlineData("final", Stages.Final)]
    [InlineData("3", Stages.Final)]
    public void StageNames_Normalize(string raw, string expected) => Assert.Equal(expected, Stages.Normalize(raw));
}
