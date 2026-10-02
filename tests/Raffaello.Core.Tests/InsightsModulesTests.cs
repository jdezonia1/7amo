using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Insights;
using Raffaello.Core.Materials;
using Raffaello.Core.Mos;
using static Raffaello.Core.Tests.InsightsAnomalyTests;

namespace Raffaello.Core.Tests;

/// <summary>[insights] Store, material reconciliation, rate benchmarking, cash-flow forecast and earned value (synthetic data).</summary>
public class InsightsModulesTests
{
    [Fact]
    public void Sqlite_store_keeps_dismissals_with_reasons_and_editable_tables()
    {
        var db = TestData.NewDb();
        var store = new SqliteInsightsStore(db.Path, "mohamed", "PC1");
        store.EnsureSchema();
        Assert.Throws<InvalidOperationException>(() => store.Dismiss("SHARED|R1|1ST FIX|POWER", AnomalyKinds.MultiSub, "R1 shared", " "));
        store.Dismiss("SHARED|R1|1ST FIX|POWER", AnomalyKinds.MultiSub, "R1 shared", "work split agreed on site");
        var d = store.Load();
        var dis = Assert.Single(d.ActiveDismissals().Values);
        Assert.Equal("mohamed", dis.DismissedBy);
        Assert.Equal("work split agreed on site", dis.Reason);
        store.Restore("SHARED|R1|1ST FIX|POWER", "re-opened");
        Assert.Empty(store.Load().ActiveDismissals());
        Assert.Single(store.Load().Dismissals);   // kept as a record

        store.ReplaceAll(MaterialReconciliation.DefaultNorms(), "norms");
        var norms = store.Load().Norms;
        Assert.Equal(MaterialReconciliation.DefaultNorms().Count, norms.Count);
        norms[0].PerPoint = 7.5;
        norms.RemoveAt(1);
        norms.Add(new InsightNorm { Material = "CABLE|4X16", System = "POWER", PerPoint = 3 });
        store.ReplaceAll(norms, "norms edited");
        var after = store.Load().Norms;
        Assert.Equal(norms.Count, after.Count);
        Assert.Contains(after, n => n.PerPoint == 7.5);
        Assert.Contains(after, n => n.Material == "CABLE|4X16");
        store.Save(new InsightThreshold { Key = InsightThresholds.RobustZ, Value = 5 });
        Assert.Equal(5, store.Load().Threshold(InsightThresholds.RobustZ));
        Assert.Equal(1.5, store.Load().Threshold(InsightThresholds.MinRatio));
        // audit rows in the shared data file
        Assert.Contains(db.RecentAudit(50), e => e.Summary.Contains("Insight dismissed"));

        // the selector picks SQLite for a Db data source
        var sel = new InsightsStoreSelector(() => db);
        Assert.Equal(norms.Count, sel.Load().Norms.Count);
    }

    [Fact]
    public void Material_reconciliation_theoretical_vs_delivered_vs_paid()
    {
        var s = new ProjectSnapshot
        {
            Rooms = { Room("A1", "2BR"), Room("A2", "2BR") },
            RoomQtys = { Cap("A1", "2ND FIX", "POWER", 40), Cap("A2", "2ND FIX", "POWER", 40) },
            Claims = { Claim("SUBA", 1, "A1", "2ND FIX", "POWER", 40), Claim("SUBA", 1, "A2", "2ND FIX", "POWER", 20) },
            SubInvoices = { new SubInvoice { Id = 9, Kind = InvoiceKinds.Supplier, Status = SubInvoiceStatus.Approved } },
        };
        var m = new MaterialsSnapshot
        {
            Pos = { new MatPo { Id = 1, PoNo = "PO-1", Supplier = "CABLES" } },
            PoLines = { new MatPoLine { Id = 10, PoId = 1, LineNo = 1, Description = "3C 2.5mm2 CU/PVC cable", Unit = "M", Qty = 2000, Rate = 4 } },
            Dns = { new MatDn { Id = 100, DnNo = "DN-1", PoNo = "PO-1" } },
            DnLines =
            {
                new MatDnLine { Id = 1000, DnId = 100, Description = "3X2.5 CU PVC", Qty = 600, Unit = "M", PoLineId = 10, BoqCode = "B6-1" },
                new MatDnLine { Id = 1001, DnId = 100, Description = "3X2.5 CU PVC", Qty = 400, Unit = "M", PoLineId = 10, BoqCode = "B6-1" },
                new MatDnLine { Id = 1002, DnId = 100, Description = "4X16 CU XLPE SWA", Qty = 300, Unit = "M" },
            },
            Locks = { new MatDnInvoiceLock { DnLineId = 1000, SubInvoiceId = 9 }, new MatDnInvoiceLock { DnLineId = 1001, SubInvoiceId = 77 } },
            MosValuations = { new MosValuation { Id = 1, No = 1, Status = MosStatus.Approved, MosPct = 0.75 } },
            MosLines = { new MosLine { ValuationId = 1, BoqCode = "B6-1", BoqDescription = "3C 2.5 cable", DeliveredQty = 1000, InstalledQty = 0, BoqRate = 6, MosPct = 0.75 } },
        };
        Assert.True(MaterialReconciliation.Matches("CABLE|3X2.5", "3C 2.5mm2 CU/PVC cable"));
        Assert.False(MaterialReconciliation.Matches("CABLE|3X2.5", "4X16 CU XLPE SWA"));
        Assert.True(MaterialReconciliation.Matches("conduit 20mm", "PVC CONDUIT 20 MM HEAVY GAUGE"));

        var norms = new List<InsightNorm> { new() { Material = "CABLE|3X2.5", Unit = "M", System = "POWER", Stage = "2ND FIX", PerPoint = 12, WastageAllowance = 0.05 } };
        var r = MaterialReconciliation.Build(s, m, norms);
        var row = Assert.Single(r.Rows);
        Assert.Equal(60, row.InstalledPoints);
        Assert.Equal(720, row.Theoretical);
        Assert.Equal(1000, row.Delivered);
        Assert.Equal(1000, row.Invoiced);
        Assert.Equal(600, row.Paid);
        Assert.Equal(280, row.OnSite);
        Assert.Equal(960, row.TheoreticalTotal);
        Assert.Equal(1000.0 / 60, row.ObservedPerPoint, 6);
        Assert.Equal(0, row.OverDelivered);   // 1000 < 960 x 1.05
        Assert.Equal("OK", row.Status);
        Assert.Single(r.Unmatched);   // the 4X16 line has no norm
        var mos = Assert.Single(r.Mos);
        Assert.Equal(720, mos.ReleaseQty, 6);
        Assert.Equal(720 * 6 * 0.75, mos.ReleaseValue, 2);

        // over-delivery becomes a warning
        m.DnLines.Add(new MatDnLine { Id = 1003, DnId = 100, Description = "3X2.5 CU PVC", Qty = 500, Unit = "M", PoLineId = 10 });
        var r2 = MaterialReconciliation.Build(s, m, norms);
        Assert.Equal("OVER-DELIVERED", r2.Rows[0].Status);
        Assert.Single(MaterialReconciliation.ToAnomalies(r2));
        // no norms entered: defaults are used and said so
        Assert.True(MaterialReconciliation.Build(s, m, new List<InsightNorm>()).UsingDefaultNorms);
    }

    [Fact]
    public void Rate_benchmark_groups_contracts_pos_and_owner_boq()
    {
        ContractItem Item(string c, string no, double rate) => new()
        {
            ContractNo = c, ItemNo = no, Rate = rate, Unit = "No.", Description = "install 20A isolator wall mounted", Category = "ISOLATOR", FixStage = "", Mount = "WALL", HeightBand = "ANY", Systems = "POWER",
        };
        var s = new ProjectSnapshot
        {
            Contracts = { new Contract { ContractNo = "C1", Subcontractor = "ROOTS" }, new Contract { ContractNo = "C2", Subcontractor = "ABRAG" }, new Contract { ContractNo = "C3", Subcontractor = "ELAF" }, new Contract { ContractNo = "C4", Subcontractor = "AWRAD" } },
            ContractItems = { Item("C1", "10", 30), Item("C2", "12", 32), Item("C3", "9", 31), Item("C4", "7", 90), new ContractItem { ContractNo = "C1", ItemNo = "11", Rate = 300, Unit = "No.", Description = "install 63A isolator", Category = "ISOLATOR", Mount = "WALL", HeightBand = "ANY", Systems = "POWER" } },
            ItemBoqs = { new ContractItemBoq { ContractNo = "C1", ItemNo = "10", BoqCode = "B6-01-ISO" } },
            BoqItems = { new BoqItem { ItemCode = "B6-01-ISO", Description = "isolator 20A", Rate = 50 } },
        };
        var m = new MaterialsSnapshot
        {
            Pos = { new MatPo { Id = 1, PoNo = "PO-1", Supplier = "S1" }, new MatPo { Id = 2, PoNo = "PO-2", Supplier = "S2" } },
            PoLines = { new MatPoLine { Id = 1, PoId = 1, Description = "4C 16mm2 CU/XLPE/SWA", Unit = "M", Rate = 40 }, new MatPoLine { Id = 2, PoId = 2, Description = "4X16 CU XLPE SWA LSZH", Unit = "MTR", Rate = 46 } },
        };
        var groups = RateBenchmark.Build(s, m);
        var iso = groups.Single(g => g.Entries.Any(e => e.Ref == "item 10"));
        Assert.Equal(4, iso.Entries.Count);   // 63A item is a different group
        Assert.Equal(30, iso.Min); Assert.Equal(90, iso.Max);
        Assert.True(iso.Entries.Single(e => e.Rate == 90).IsOutlier);
        Assert.False(iso.Entries.Single(e => e.Rate == 31).IsOutlier);
        var roots = iso.Entries.Single(e => e.Ref == "item 10");
        Assert.Equal(50, roots.OwnerRate);
        Assert.Equal(20, roots.Margin);
        var cable = groups.Single(g => g.Entries.Any(e => e.Source == RateSources.Po));
        Assert.Equal(2, cable.Parties);
        Assert.Equal(46.0 / 40, cable.Spread, 6);
        var sheets = RateBenchmark.Sheets(groups, "test");
        Assert.Equal(2, sheets.Count);
        Assert.True(sheets[0].Rows.Count >= 2);
    }

    [Fact]
    public void Cash_flow_forecast_follows_terms_programme_and_delay()
    {
        var today = new DateTime(2026, 10, 1);
        var s = new ProjectSnapshot
        {
            Contracts = { new Contract { ContractNo = "C1", Subcontractor = "ROOTS", RetentionPct = 0.10 } },
            ContractItems = { new ContractItem { ContractNo = "C1", ItemNo = "1", Qty = 100, Rate = 100, FixStage = "1ST FIX", StagePct = 0.9 } },
        };
        var data = new InsightsData
        {
            Programme = { new InsightProgramme { Area = "ALL", Stage = "1ST FIX", PlannedStart = today, PlannedFinish = today.AddDays(61) } },
            Terms = { new InsightPaymentTerm { Party = PaymentParties.Subcontract, Ref = "C1", Scheme = PaymentSchemes.S90_10, RetentionPct = 0.10, PayDays = 30, HandoverMonths = 1 } },
        };
        var m = new MaterialsSnapshot
        {
            Pos = { new MatPo { Id = 1, PoNo = "PO-1", Supplier = "CABLES", PaymentTerms = "100% before delivery by LC" } },
            PoLines = { new MatPoLine { Id = 1, PoId = 1, Qty = 100, Rate = 10, Unit = "M" } },
        };
        var r = CashFlowForecast.Build(new CashFlowInputs { Project = s, Materials = m, Data = data, Today = today, ProjectStart = today.AddMonths(-6), PlannedFinish = today.AddMonths(4) });
        var sub = r.Events.Where(e => e.Kind == CashKinds.Sub).Sum(e => e.Amount);
        Assert.Equal(-10_000, sub, 2);   // every riyal of the remaining contract is paid (stage %, handover %, retention)
        Assert.Equal(-1_000, r.Events.Where(e => e.Kind == CashKinds.Supplier).Sum(e => e.Amount), 2);
        Assert.True(r.Events.Where(e => e.Kind == CashKinds.Owner).Sum(e => e.Amount) > 10_000);
        Assert.Equal(new DateTime(2026, 11, 1), r.Events.Where(e => e.Kind == CashKinds.Sub && e.Note == "stage %").Min(e => e.Month));   // October work paid in November
        Assert.Contains(r.Events, e => e.Note == "retention release" && e.Kind == CashKinds.Sub && e.Month == new DateTime(2027, 3, 1));
        Assert.Contains(r.Events, e => e.Note == "LC before delivery" && e.Month == new DateTime(2026, 10, 1));
        Assert.Equal(r.Months.Sum(x => x.Net), r.Months.Last().Cumulative, 6);
        Assert.Equal(-10_000, r.Months.Sum(x => -x.SubPayables), 2);

        var late = CashFlowForecast.Build(new CashFlowInputs { Project = s, Materials = m, Data = data, Today = today, ProjectStart = today.AddMonths(-6), PlannedFinish = today.AddMonths(4), DelayWeeks = 8 });
        Assert.True(late.Finish > r.Finish);
        double Weighted(CashFlowResult x) => x.Events.Where(e => e.Kind == CashKinds.Sub).Sum(e => e.Amount * e.Month.Ticks) / x.Events.Where(e => e.Kind == CashKinds.Sub).Sum(e => e.Amount);
        Assert.True(Weighted(late) > Weighted(r));
        Assert.Equal(3, CashFlowForecast.Sheets(r, "t").Count);
    }

    [Fact]
    public void Earned_value_productivity_and_area_forecast()
    {
        var start = new DateTime(2026, 1, 4);
        var today = new DateTime(2026, 9, 27);
        var s = new ProjectSnapshot();
        for (var i = 1; i <= 10; i++) { s.Rooms.Add(Room($"L1-{i:00}", "2BR")); s.RoomQtys.Add(Cap($"L1-{i:00}", "1ST FIX", "POWER", 10)); }
        // INV 1..4, falling output: 4, 3, 1, 1 rooms per period of 4 weeks
        var plan = new[] { 4, 3, 1, 1 };
        var k = 1;
        for (var inv = 1; inv <= 4; inv++) for (var j = 0; j < plan[inv - 1]; j++) s.Claims.Add(Claim("SUBA", inv, $"L1-{k++:00}", "1ST FIX", "POWER", 10));
        var data = new InsightsData
        {
            Programme = { new InsightProgramme { Area = "ALL", Stage = "1ST FIX", PlannedStart = new DateTime(2026, 6, 1), PlannedFinish = new DateTime(2026, 8, 31) } },
            InvoicePeriods = Enumerable.Range(1, 4).Select(n => new InsightInvoicePeriod { Subcontractor = "SUBA", InvoiceNo = n, PeriodEnd = new DateTime(2026, 6, 1).AddDays(28 * n) }).ToList(),
        };
        var r = EarnedValue.Build(new EvInputs { Project = s, Data = data, Today = today, ProjectStart = start, PlannedFinish = new DateTime(2026, 12, 31) });
        var row = Assert.Single(r.Rows);
        Assert.Equal(0.9, row.ActualPct, 6);
        Assert.True(row.PlannedPct > 0.95);
        Assert.Equal(0, r.UndatedLines);
        var p = Assert.Single(r.Productivity);
        Assert.Equal(4, p.Periods.Count);
        Assert.Equal(7.5, p.Periods[1].PointsPerWeek!.Value, 6);   // 3 rooms x 10 points in 4 weeks
        Assert.Equal(10, p.Periods[0].PointsPerWeek!.Value, 6);    // first period: the typical 4-week gap
        Assert.Equal("FALLING", p.TrendText);
        Assert.Contains(r.Warnings, w => w.Subject == "SUBA" && w.Message.Contains("falling"));
        var area = Assert.Single(r.Areas);
        Assert.Equal("Level 01", area.Area);
        Assert.NotNull(area.ForecastFinish);
        Assert.True(area.ForecastFinish > area.PlannedFinish);
        Assert.Contains(r.Warnings, w => w.Subject == "Level 01");
        Assert.NotEmpty(r.Curve);
        Assert.Equal(5, EarnedValue.Sheets(r, "t").Count);
    }
}
