using ClosedXML.Excel;
using Raffaello.Core.Boq;
using Raffaello.Core.Coding;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Materials;
using Raffaello.Core.Mos;
using static Raffaello.Core.Tests.Phase3Fixtures;

namespace Raffaello.Core.Tests;

public class Phase3CodingMosBoqTests
{
    private static List<BoqItem> Budget() => new()
    {
        new() { ItemCode = "B6-01-01-00-6-26-D-4", Description = "4C 10mm2 Cu/XLPE/SWA/LSZH + 1C 10mm2 Cu/LSF", CostCode = "160100", BudgetResourceCode = "12020121", Unit = "" },
        new() { ItemCode = "B5-01-01-00-5-26-K-4", Description = "4C 10mm2 Cu/XLPE/SWA/LSHZ + 1C 10mm2 Cu/LSF G/Y", CostCode = "160100", BudgetResourceCode = "12020121", Unit = "" },
        new() { ItemCode = "B6-01-01-00-6-26-G-4", Description = "4C 10mm2 Fire Rated + 1C 10mm2 Fire Rated/ECC", CostCode = "160100", BudgetResourceCode = "12020121", Unit = "" },
        new() { ItemCode = "B6-01-01-00-6-31-A-1", Description = "25mm PVC conduit heavy gauge including fittings", CostCode = "160300", BudgetResourceCode = "12020121", Unit = "m" },
        new() { ItemCode = "B6-01-01-00-6-31-A-2", Description = "20mm PVC conduit heavy gauge including fittings", CostCode = "160300", BudgetResourceCode = "12020121", Unit = "m" },
        new() { ItemCode = "B6-01-01-00-6-40-A-1", Description = "Smoke detector addressable", CostCode = "170100", BudgetResourceCode = "12020121", Unit = "no" },
    };

    [Fact]
    public void Text_index_ranks_closest_description_first()
    {
        var idx = new TextIndex();
        idx.Add("25mm PVC conduit heavy gauge");
        idx.Add("Smoke detector addressable");
        idx.Add("Cable tray 300mm");
        var hits = idx.Search("PVC CONDUIT 25MM", 3);
        Assert.Equal(0, hits[0].Doc);
        Assert.True(hits[0].Score > 0.3);
    }

    [Fact]
    public void Auto_coder_fingerprint_text_unit_and_ambiguity()
    {
        var coder = new AutoCoder(new CodingSources { BudgetList = Budget() });
        // fire-rated cable: only one budget row has the attributes -> AUTO, cost codes filled
        var fr = coder.Suggest(new CodingRequest("S", "", "4X10 CU/MICA/XLPE/SWA/LSOH", "MT"));
        Assert.Equal(CodeStatus.Auto, fr.Status);
        Assert.Equal("B6-01-01-00-6-26-G-4", fr.Best!.BoqCode);
        Assert.Equal("160100", fr.Best.CostCode);
        // plain 4X10 exists in two bills -> filled but flagged
        var plain = coder.Suggest(new CodingRequest("S", "", "4X10 CU/XLPE/SWA/LSOH", "MT"));
        Assert.Equal(CodeStatus.Review, plain.Status);
        Assert.Equal(2, plain.Top.Count);
        Assert.DoesNotContain(plain.Top, c => c.BoqCode.EndsWith("G-4"));
        // generic: size must agree (25MM, not 20MM)
        var conduit = coder.Suggest(new CodingRequest("S", "", "PVC CONDUIT 25MM", "M"));
        Assert.Equal("B6-01-01-00-6-31-A-1", conduit.Best!.BoqCode);
        Assert.NotEqual(CodeStatus.Auto, conduit.Status);
        // unit must agree: a detector line in metres finds nothing
        var bad = coder.Suggest(new CodingRequest("S", "", "Smoke detector addressable", "M"));
        Assert.DoesNotContain(bad.Top, c => c.BoqCode.EndsWith("40-A-1"));
        Assert.Equal(CodeStatus.Ask, coder.Suggest(new CodingRequest("S", "", "Something completely unrelated", "LOT")).Status);
    }

    [Fact]
    public void Confirmed_codes_are_learned_and_win_next_time()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        var line = env.Service.Snapshot.LinesOf(po).Single(l => l.LineNo == 3);
        var choice = new CodeCandidate("B6-01-01-00-6-31-A-1", "160300", "12020121", "", "25mm PVC conduit", "M", "MANUAL", 1, "picked");
        env.Service.ConfirmCode(line, choice);
        var saved = env.Service.Snapshot.PoLines.Single(l => l.Id == line.Id);
        Assert.Equal(CodeStatus.Confirmed, saved.CodeStatus);
        Assert.Equal(2, env.Service.Snapshot.CodeMemory.Count);                // DESC + FP (no supplier item code on this line)
        var sug = env.Service.Coder().Suggest(new CodingRequest(po.Supplier, "", "PVC  conduit 25mm heavy-gauge", "MT"));
        Assert.Equal(CodeStatus.Auto, sug.Status);
        Assert.Equal("B6-01-01-00-6-31-A-1", sug.Best!.BoqCode);
        Assert.Equal("HISTORY", sug.Best.Source);
        // confirming again increments the use count instead of duplicating
        env.Service.ConfirmCode(env.Service.Snapshot.PoLines.Single(l => l.Id == line.Id), choice);
        Assert.Equal(2, env.Service.Snapshot.CodeMemory.Count);
        Assert.All(env.Service.Snapshot.CodeMemory, m => Assert.Equal(2, m.Uses));
    }

    [Fact]
    public void Coding_report_counts_document_auto_review_ask()
    {
        using var env = NewEnv();
        env.Project.Store.InsertMany(Budget());
        env.Project.Reload();
        var doc = PoReader.Parse(Doc("po.pdf", PoText)).Value;
        doc.Scope.Clear();
        var report = env.Service.CodePoLines(doc);
        Assert.Equal(3, report.Total);
        Assert.Equal(CodeStatus.Review, doc.Lines[0].CodeStatus);       // 4X10 in two bills
        Assert.True(report.Filled >= 2);
        Assert.Contains("filled", report.ToString());
    }

    // ------------------------------------------------------------------ owner MOS

    private static void OwnerRates(Env env)
    {
        env.Store.Batch(w => w.InsertMany(new[]
        {
            new BoqLine { Source = "t", BoqCode = "B2-01-01-00-2-27-R-2", Description = "4C 10mm2 owner B2", Unit = "m", Rate = 40 },
            new BoqLine { Source = "t", BoqCode = "B6-01-01-00-6-26-D-4", Description = "4C 10mm2 owner B6", Unit = "m", Rate = 42 },
        }), "owner rates");
    }

    [Fact]
    public void Mos_line_is_capped_at_75_percent_of_the_boq_value()
    {
        var l = new MosLine { BoqRate = 40, ContractQty = 10, MarketRate = 100, MosPct = 0.75, DeliveredQty = 50 };
        Assert.Equal(40 * 10 * 0.75, l.CumAmount, 2);                    // 50 x 100 x 75% = 3,750 > cap 300
        Assert.True(l.Capped);
        var old = new MosLine { BoqRate = 40, MosPct = 0.75, DeliveredQty = 10 };   // saved before App F: BOQ-rate basis
        Assert.Equal(300, old.CumAmount, 2);
    }

    [Fact]
    public void Mos_values_delivered_less_installed_at_boq_rate_and_releases()
    {
        using var env = NewEnv();
        SavePo(env);
        OwnerRates(env);
        SaveDn(env);
        var mir = new MirDocument { Header = new MatMir { MirNo = "MIR-1", Revision = "00" } };
        mir.Dns.Add(new MatMirDn { DnNo = "81000001", PoNo = PoNo });
        env.Service.CommitMir(mir);
        env.Service.Settings.MosPct = 0.8;
        var s = env.Service.Snapshot;
        var ledger = MosService.Ledger(s, env.Service.Settings);
        Assert.Equal(3, ledger.Count);                                        // 4X10 split over 2 codes + 4X16 MICA
        Assert.Equal(360, ledger.Single(r => r.BoqCode.EndsWith("R-2")).Qty, 6);

        var b = MosService.Build(env.Project.Snapshot, s, env.Service.Settings, 1, 0, new DateTime(2026, 8, 31));
        var r2 = b.Lines.Single(l => l.BoqCode.EndsWith("R-2"));
        // App F rule (03-Oct): MOS % of the supplier / PO rate, not the BOQ rate (no contract qty here, so no cap)
        Assert.True(r2.UsesMarketRate);
        Assert.Equal(Math.Round(360 * r2.MarketRate * 0.8, 2), r2.CumAmount, 2);
        Assert.Equal(360, r2.ThisMonthQty, 6);
        Assert.Contains(b.Warnings, w => w.Contains("not valued: no owner BOQ code"));     // MICA line has no owner code
        var v = MosService.SaveDraft(env.Store, s, b);
        MosService.Approve(env.Store, v);
        env.Service.Reload();
        Assert.Throws<InvalidOperationException>(() => MosService.SaveDraft(env.Store, env.Service.Snapshot, b));

        // installed 100 m on R-2 -> next valuation releases MOS on it
        env.Store.Insert(new MosInstalled { BoqCode = "B2-01-01-00-2-27-R-2", Qty = 100, AsOf = new DateTime(2026, 9, 10) });
        env.Service.Reload();
        var b2 = MosService.Build(env.Project.Snapshot, env.Service.Snapshot, env.Service.Settings, 2, 0, new DateTime(2026, 9, 30));
        var l2 = b2.Lines.Single(l => l.BoqCode.EndsWith("R-2"));
        Assert.Equal(r2.CumAmount, l2.PrevAmount, 2);
        Assert.Equal(Math.Round(260 * l2.MarketRate * 0.8, 2), l2.CumAmount, 2);
        Assert.Equal(Math.Round(100 * l2.MarketRate * 0.8, 2), l2.Released, 2);
        Assert.Equal(360, l2.PrevDeliveredQty, 6);
        Assert.Equal("MIR-1", l2.MirRefs);
        Assert.Equal(2, MosService.NextNo(env.Service.Snapshot));
        Assert.Single(MosService.Diff(b, b2));

        var x = Path.Combine(env.Dir, "mos.xlsx"); var p = Path.Combine(env.Dir, "mos.pdf");
        MosExporter.ExportExcel(x, b2, new InvoiceHeaderInfo());
        MosExporter.ExportPdf(p, b2, new InvoiceHeaderInfo());
        Assert.True(new FileInfo(p).Length > 1000);
        using var wb = new XLWorkbook(x);
        var ws = wb.Worksheet("11. App F - MOS On");
        Assert.Equal("BOQ Item", ws.Cell(10, 2).GetString());
        Assert.Equal("Delivered Qty", ws.Cell(10, 8).GetString());
        Assert.Contains("ROUND(IF(G13*E13>0", ws.Cell(13, 15).FormulaA1);
        Assert.Equal("Total carried forward to IPC", ws.Cell(13 + b2.Lines.Count, 3).GetString());
    }

    [Fact]
    public void Mos_requires_a_mir_unless_the_setting_is_off_and_template_export_works()
    {
        using var env = NewEnv();
        SavePo(env);
        OwnerRates(env);
        SaveDn(env);
        var b = MosService.Build(env.Project.Snapshot, env.Service.Snapshot, env.Service.Settings, 1, 0, new DateTime(2026, 8, 31));
        Assert.Empty(b.Lines);
        Assert.Contains(b.Warnings, w => w.Contains("no MIR yet"));
        env.Service.Settings.MosRequiresMir = false;
        b = MosService.Build(env.Project.Snapshot, env.Service.Snapshot, env.Service.Settings, 1, 0, new DateTime(2026, 8, 31));
        Assert.Equal(2, b.Lines.Count(l => l.BoqRate > 0));

        var tpl = Path.Combine(env.Dir, "tpl.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("MOS");
            ws.Cell(1, 1).Value = "OUR TEMPLATE";
            ws.Cell(5, 1).Value = "BOQ REF"; ws.Cell(5, 2).Value = "DESCRIPTION"; ws.Cell(5, 3).Value = "RATE"; ws.Cell(5, 4).Value = "ON SITE QTY"; ws.Cell(5, 5).Value = "MOS AMOUNT CUM";
            wb.SaveAs(tpl);
        }
        var outPath = Path.Combine(env.Dir, "mos-tpl.xlsx");
        MosExporter.ExportExcel(outPath, b, new InvoiceHeaderInfo(), tpl);
        using var o = new XLWorkbook(outPath);
        Assert.Equal("OUR TEMPLATE", o.Worksheet("MOS").Cell(1, 1).GetString());
        Assert.StartsWith("B", o.Worksheet("MOS").Cell(6, 1).GetString());
        Assert.True(o.Worksheet("MOS").Cell(6, 5).GetDouble() > 0);
    }

    // ------------------------------------------------------------------ BOQ

    [Fact]
    public void Boq_import_categorises_en_ar_headings_and_learns()
    {
        var r = BoqImporter.Read(OwnerBoq());
        Assert.Equal(7, r.Items);
        Assert.Equal(2, r.Rows.Count(x => x.IsHeading));
        var cable = r.Rows.Single(x => x.BoqCode == "B6-01-01-00-6-26-D-4");
        Assert.Equal("CABLE", cable.Category);
        Assert.Equal(40, cable.Rate);
        var mcp = r.Rows.Single(x => x.Description == "Manual call point");
        Assert.Equal("FIRE ALARM", mcp.System);
        Assert.Equal("DEVICE / OUTLET", mcp.Category);
        var ar = r.Rows.Single(x => x.Description.StartsWith("مقبس"));
        Assert.Equal("POWER", ar.System);
        var tray = r.Rows.Single(x => x.Description.StartsWith("Cable tray"));
        Assert.Equal("CONTAINMENT", tray.System);
        Assert.Contains(r.Issues, i => i.Contains("row 12"));                  // 1 x 500 != 999
        var sundry = r.Rows.Single(x => x.Description.StartsWith("Sundry"));
        Assert.Equal("", sundry.Category);

        var rule = BoqCategorizer.Learn(sundry, "PRELIMINARIES", "OTHER", Array.Empty<BoqCatRule>());
        var again = BoqImporter.Read(OwnerBoq(), new[] { rule });
        var s2 = again.Rows.Single(x => x.Description.StartsWith("Sundry"));
        Assert.Equal("PRELIMINARIES", s2.System);
        Assert.Equal("LEARNED", s2.CatSource);

        using var env = NewEnv();
        Assert.Equal(r.Rows.Count, BoqImporter.Commit(env.Store, r));
        Assert.Equal(r.Rows.Count, BoqImporter.Commit(env.Store, r));          // re-import replaces
        Assert.Equal(r.Rows.Count, env.Store.All<BoqLine>().Count);
    }
}
