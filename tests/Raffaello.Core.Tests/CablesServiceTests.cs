using Raffaello.Core.Cables;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

/// <summary>[cables] Store, claim matching, provisional runs, ledger pairing, flags, bypass, aliases (synthetic data only).</summary>
public class CablesServiceTests
{
    internal static (Db Db, CableService Svc) NewService()
    {
        var db = TestData.NewDb();
        var svc = new CableService(CableStore.For(db));
        svc.Store.EnsureSchema();
        return (db, svc);
    }

    internal static CableClaim Claim(string sub, int inv, string from, string to, string size, double qty, string stage = "CABLE PULLING", string building = Buildings.Hotel,
        double site = 1, double wir = 1, string? key = null) => new()
    {
        Building = building, Subcontractor = sub, InvoiceNo = inv, RawStage = stage, FromRaw = from, ToRaw = to, SizeRaw = size, Qty = qty, SitePct = site, WirPct = wir,
        Source = "TEST", SourceKey = key ?? $"T|{sub}|{inv}|{from}|{to}|{size}|{qty}|{stage}|{Guid.NewGuid():N}",
    };

    [Fact]
    public void Claims_without_a_design_run_create_provisional_runs_and_earth_companions()
    {
        var (_, svc) = NewService();
        var plan = svc.Import(new[]
        {
            Claim("SUBA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145),
            Claim("SUBA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "1x16", 145),
        }, null, "test");
        Assert.Equal(2, plan.Claims.Count);
        var snap = svc.Load();
        var run = Assert.Single(snap.Runs);
        Assert.Equal(CableStatus.Provisional, run.Status);
        Assert.Equal("4X16", run.SizeKey);
        Assert.Equal("1X16", run.EarthSizeKey);
        Assert.StartsWith("C-", run.Ref);
        Assert.Equal(2, snap.Panels.Count);
        Assert.All(snap.Claims, c => Assert.Equal(run.Id, c.RunId));
        Assert.True(snap.Claims.Single(c => c.SizeKey == "1X16").IsEarth);
        var flags = svc.Flags(snap);
        Assert.Equal(2, flags.Count(f => f.Code == CableFlagCodes.UnknownRun));
        Assert.Contains("no design length", flags.First(f => f.Code == CableFlagCodes.UnknownRun).Message);
    }

    [Fact]
    public void Same_from_to_claimed_again_is_flagged_with_the_earlier_claim_never_blocked()
    {
        var (_, svc) = NewService();
        svc.Import(new[] { Claim("SAFIA", 1, "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 1");
        // another subcontractor, later invoice, other spelling and reversed direction
        var plan = svc.Prepare(new[] { Claim("ROOTS", 4, "ldb-ht-z1-lb2-3", "SMDB-HT-Z1-LB2-CM-01", "4X16", 140) });
        var dup = Assert.Single(plan.Flags, f => f.Code == CableFlagCodes.Duplicate);
        Assert.Equal(Verdict.Over, dup.Severity);
        Assert.Contains("SAFIA INV 1", dup.Message);
        Assert.Contains("another subcontractor", dup.Message);
        Assert.Single(dup.Related);
        Assert.Contains(plan.Flags, f => f.Code == CableFlagCodes.Spelling);
        svc.Commit(plan, "INV 4");   // warnings never block
        Assert.Equal(2, svc.Load().Claims.Count);
        Assert.Single(svc.Load().Runs);
    }

    [Fact]
    public void Different_size_or_stage_on_the_same_route_is_not_a_duplicate_but_termination_before_pulling_is_flagged()
    {
        var (_, svc) = NewService();
        svc.Import(new[] { Claim("SUBA", 1, "MDB-HT-Z1-LB2", "SMDB-HT-Z1-LB2-CM-01", "4x240", 80) }, null, "INV 1");
        var plan = svc.Prepare(new[]
        {
            Claim("SUBA", 2, "MDB-HT-Z1-LB2", "SMDB-HT-Z1-LB2-CM-01", "4x240", 80, "TERMINATION & TEST"),
            Claim("SUBA", 2, "MDB-HT-Z1-LB2", "SMDB-HT-Z1-LB2-KT-01", "4x120", 60, "TERMINATION"),
        });
        Assert.DoesNotContain(plan.Flags, f => f.Code == CableFlagCodes.Duplicate);
        var order = Assert.Single(plan.Flags, f => f.Code == CableFlagCodes.StageOrder);
        Assert.Contains("KT-01", order.Claim.ToRaw);
    }

    [Fact]
    public void Design_run_from_a_schedule_upgrades_the_provisional_run_and_length_checks_start()
    {
        var (_, svc) = NewService();
        svc.Import(new[]
        {
            Claim("SUBA", 1, "EMCC-HT-Z1-LB2-FANS", "MAF-LL2-01", "4x10", 140, site: 0.5),
            Claim("SUBA", 1, "EMCC-HT-Z1-LB2-FANS", "MAF-LL2-01", "1x10", 120, site: 0.5),
        }, null, "INV 1");
        var provisional = svc.Load().Runs.Single();
        var merge = svc.SaveRegister(new[]
        {
            new CableRun { Building = Buildings.Hotel, FromName = "EMCC HT-Z1-LB2- FANS", ToName = "MAF-LL2-01", SizeKey = "4C x 10mm2 + 1x10 E", DesignLength = 100, SourceKind = CableSources.Schedule, SourceDoc = "schedule.xlsx" },
        }, Array.Empty<CablePanel>(), "schedule");
        Assert.Equal(1, merge.ProvisionalUpgraded);
        Assert.Equal(0, merge.RunsAdded);
        var snap = svc.Load();
        var run = Assert.Single(snap.Runs);
        Assert.Equal(provisional.Id, run.Id);
        Assert.Equal(CableStatus.Proposed, run.Status);
        Assert.Equal(100, run.DesignLength);
        Assert.All(snap.Claims, c => Assert.Equal(run.Id, c.RunId));

        svc.Import(new[] { Claim("SUBB", 2, "EMCC-HT-Z1-LB2-FANS", "MAF-LL2-01", "4x10", 60, site: 1) }, null, "INV 2");
        var flags = svc.Flags();
        Assert.Contains(flags, f => f.Code == CableFlagCodes.OverLength && f.Claim.Qty == 140);
        var cum = Assert.Single(flags, f => f.Code == CableFlagCodes.Cumulative);
        Assert.Equal("SUBB", cum.Claim.Subcontractor);   // 70 m after SITE % + 60 m = 130 % of 100 m
        Assert.Contains(flags, f => f.Code == CableFlagCodes.Earth && f.Message.Contains("120"));
        Assert.DoesNotContain(flags, f => f.Code == CableFlagCodes.UnknownRun);
    }

    [Fact]
    public void Bypass_needs_a_reason_is_audited_and_marks_the_flag()
    {
        var (db, svc) = NewService();
        svc.Import(new[] { Claim("SUBA", 1, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 1");
        svc.Import(new[] { Claim("SUBB", 2, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 145) }, null, "INV 2");
        var flag = svc.Flags().Single(f => f.Code == CableFlagCodes.Duplicate);
        Assert.Throws<InvalidOperationException>(() => svc.Bypass(flag, " "));
        svc.Bypass(flag, "re-pulled after damage, accepted by site");
        var again = svc.Flags().Single(f => f.Code == CableFlagCodes.Duplicate);
        Assert.True(again.IsBypassed);
        Assert.Equal("BYPASSED", again.Tag);
        Assert.Contains(db.RecentAudit(20), a => a.Summary.Contains("Cable flag bypassed") && a.Summary.Contains("re-pulled"));
        Assert.DoesNotContain(CableHooks.Queue(svc.Store), q => q.Title.Contains("FROM-TO"));
    }

    [Fact]
    public void Tracker_rows_and_ledger_lines_of_the_same_claim_are_counted_once()
    {
        var (_, svc) = NewService();
        var sheet = new[]
        {
            Claim("ROOTS", 3, "EMCC-BR-Z2-LB1-FANS", "JF-LB1-04", "4X6", 65, building: Buildings.Branded),
            Claim("ROOTS", 4, "SMDB", "P3-9", "4X16", 101, building: Buildings.Branded),
            Claim("ROOTS", 4, "MCC-HT-Z2-LB1-02", "MAHU-LL1-01", "4x6", 85),
            Claim("ROOTS", 4, "MCC-HT-Z2-LB1-02", "FAHU-LL1-01", "4x6", 85),
        };
        var ledger = new[]
        {
            new ClaimLine { Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 3, Stage = "CABLE PULLING", Room = "EMCC-BR-Z2-LB1-FANS", Item = "4X6", Qty = 65, Notes = "JF-LB1-04", SourceKey = "L1" },
            new ClaimLine { Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 4, Stage = "CABLE PULLING", Room = "P3-9", Item = "4X16", Qty = 101, SourceKey = "L2" },
            new ClaimLine { Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 4, Stage = "CABLE PULLING", Room = "HOTEL", Item = "4X6", Qty = 170, SourceKey = "L3" },
            new ClaimLine { Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 4, Stage = "CABLE PULLING", Room = "P3-GF", Item = "4X25", Qty = 50, Notes = "SMDB-BR-Z3-LB1-01", SourceKey = "L4" },
        };
        foreach (var c in sheet.Where(c => c.FromRaw.StartsWith("MCC"))) c.Location = Buildings.Hotel;
        var plan = svc.Import(sheet, CableHooks.LedgerCableLines(ledger), "tracker");
        Assert.Equal(3, plan.LinkedToLedger);   // 2 one-to-one + 1 HOTEL total line
        var snap = svc.Load();
        Assert.Equal(5, snap.Claims.Count);     // 4 sheet rows + the ledger line that has no sheet row
        Assert.Equal("L1", snap.Claims.Single(c => c.ToRaw == "JF-LB1-04").LedgerSourceKey);
        Assert.All(snap.Claims.Where(c => c.FromRaw.StartsWith("MCC")), c => Assert.Equal("L3", c.LedgerSourceKey));
        var own = snap.Claims.Single(c => c.Source == "LEDGER");
        Assert.Equal("P3-GF", own.FromRaw);
        Assert.Equal("SMDB-BR-Z3-LB1-01", own.ToRaw);
        // re-import adds nothing
        var again = svc.Prepare(sheet.Select(c => Claim(c.Subcontractor, c.InvoiceNo, c.FromRaw, c.ToRaw, c.SizeRaw, c.Qty, key: c.SourceKey)), CableHooks.LedgerCableLines(ledger));
        Assert.Empty(again.Claims);
        // incomplete FROM name is reported
        Assert.Contains(svc.Flags(), f => f.Code == CableFlagCodes.UnknownRun && f.Message.Contains("'SMDB' incomplete"));
    }

    [Fact]
    public void Confirming_an_alias_merges_panels_runs_and_claims_and_rejecting_hides_the_suggestion()
    {
        var (_, svc) = NewService();
        svc.Import(new[]
        {
            Claim("SAFIA", 1, "EMCC HT-Z1-LB2- FAN", "MAF-LL2-01", "4x10", 140),
            Claim("ROOTS", 2, "EMCC-HT-Z1-LB2-FANS", "MAF-LL2-01", "4x10", 140),
        }, null, "two spellings");
        var snap = svc.Load();
        Assert.Equal(2, snap.Runs.Count);
        Assert.DoesNotContain(svc.Flags(snap), f => f.Code == CableFlagCodes.Duplicate);   // not caught until confirmed
        var (a, b, score) = Assert.Single(CableService.AliasSuggestions(snap));
        Assert.True(score >= 0.8);
        var fan = new[] { a, b }.Single(p => p.Key.EndsWith("FAN"));
        var fans = new[] { a, b }.Single(p => p.Key.EndsWith("FANS"));
        svc.ConfirmAlias(fan.Key, fans.Key);
        snap = svc.Load();
        Assert.Single(snap.Runs);
        Assert.DoesNotContain(snap.Panels, p => p.Key == fan.Key);
        Assert.All(snap.Claims, c => Assert.Equal(fans.Key, c.FromKey));
        Assert.Contains(svc.Flags(snap), f => f.Code == CableFlagCodes.Duplicate);
        // the alias is learned: a new claim with the other spelling lands on the same panel
        var plan = svc.Prepare(new[] { Claim("SUBC", 3, "EMCC HT-Z1-LB2- FAN", "MAF-LL2-01", "4x10", 10) });
        Assert.Equal(fans.Key, plan.Claims[0].FromKey);
        Assert.Empty(plan.NewPanels);

        var (_, svc2) = NewService();
        svc2.Import(new[] { Claim("A", 1, "EMCC HT-Z1-LB2- FAN", "X-1", "4x10", 1), Claim("B", 1, "EMCC-HT-Z1-LB2-FANS", "X-2", "4x10", 1) }, null, "t");
        var s2 = svc2.Load();
        var (a2, b2, _) = CableService.AliasSuggestions(s2).Single();
        svc2.RejectAlias(a2.Key, b2.Key);
        Assert.Empty(CableService.AliasSuggestions(svc2.Load()));
    }

    [Fact]
    public void Rematch_links_unknown_claims_after_the_register_learns_the_route()
    {
        var (_, svc) = NewService();
        svc.Import(new[] { Claim("SUBA", 1, "SMDB-HT-Z4-LB1-EV", "EV CHARGER-01", "4x16", 40) }, null, "INV 1");
        var c = svc.Load().Claims.Single();
        // a second design run on the same route but another size does not take the claim
        svc.SaveRegister(new[] { new CableRun { Building = Buildings.Hotel, FromName = "SMDB-HT-Z4-LB1-EV", ToName = "EV CHARGER-01", SizeKey = "4x25", DesignLength = 45 } }, Array.Empty<CablePanel>(), "sld");
        Assert.Equal(0, svc.Rematch());
        Assert.Equal(c.RunId, svc.Load().Claims.Single().RunId);
    }

    [Fact]
    public void Sqlite_store_keeps_rowversion_concurrency_and_audit()
    {
        var (db, svc) = NewService();
        var run = svc.Store.Insert(new CableRun { FromKey = "A", ToKey = "B", SizeKey = "4X16" }, "run added");
        var copy = svc.Store.All<CableRun>().Single();
        svc.UpdateRun(run, "first edit");
        Assert.Throws<ConcurrencyException>(() => svc.UpdateRun(copy, "stale edit"));
        Assert.Contains(db.RecentAudit(10), a => a.Summary == "first edit");
        Assert.Equal(6, CableStore.EntityTypes.Length);
        Assert.Contains(typeof(CableClaim), Remote.ModuleEntities.All);
    }
}
