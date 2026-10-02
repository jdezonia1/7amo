using System.IO.Compression;
using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Materials;
using static Raffaello.Core.Tests.Phase3Fixtures;

namespace Raffaello.Core.Tests;

public class Phase3MaterialsTests
{
    private static MatMir SaveMir(Env env, params string[] dnNos)
    {
        var mir = new MirDocument { Header = new MatMir { MirNo = "MIR-EL-000001", Revision = "00", MirDate = new DateTime(2026, 8, 18) } };
        foreach (var d in dnNos) mir.Dns.Add(new MatMirDn { DnNo = d, PoNo = PoNo, Source = "MANUAL" });
        return env.Service.CommitMir(mir);
    }

    [Fact]
    public void Store_creates_own_tables_and_audits_po_saves()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        Assert.True(po.Id > 0);
        var s = env.Service.Snapshot;
        Assert.Equal(3, s.LinesOf(po).Count());
        Assert.Equal(2, s.PoScope.Count);
        Assert.Contains(env.Db.RecentAudit(20), a => a.Summary.Contains("PO ABC-P.O-E-001-2026"));
        // re-import keeps line ids (DN links survive)
        var ids = s.LinesOf(po).Select(l => l.Id).ToList();
        SavePo(env);
        Assert.Equal(ids, env.Service.Snapshot.LinesOf(env.Service.Snapshot.Pos.Single()).Select(l => l.Id).ToList());
        Assert.Single(env.Service.Snapshot.Pos);
    }

    [Fact]
    public void Po_lines_are_coded_from_the_scope_sheet_with_split()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        var l1 = env.Service.Snapshot.LinesOf(po).First();
        Assert.Equal(CodeStatus.Document, l1.CodeStatus);
        Assert.Equal("B2-01-01-00-2-27-R-2", l1.BoqCode);
        Assert.Contains("2 BOQ codes", l1.CodeSource);
        var splits = SupplierInvoices.Splits(env.Service.Snapshot, po, l1);
        Assert.Equal(2, splits.Count);
        Assert.Equal(0.6, splits.Single(x => x.Code == "B2-01-01-00-2-27-R-2").Share, 6);
    }

    [Fact]
    public void Three_way_match_statuses()
    {
        using var env = NewEnv();
        SavePo(env);
        SaveDn(env);                                                     // 600 M of 4X10 + 300 M of 4X16 MICA
        var r = env.Service.RunMatch();
        Assert.All(r.Rows, x => Assert.Equal(MatchStatus.DnWithoutMir, x.Status));
        Assert.Equal(600, r.Rows[0].Qty);
        Assert.Equal(1, r.Rows[0].PoLine!.LineNo);
        Assert.Equal(2, r.Rows[1].PoLine!.LineNo);

        SaveMir(env, "81000001");
        r = env.Service.RunMatch();
        Assert.All(r.Rows, x => Assert.Equal(MatchStatus.Matched, x.Status));

        // second DN pushes 4X10 to 1,060 M: within PO 1,000 + 5 % clause = 1,050? no -> OVER PO
        SaveDn(env, "81000002", 0.46, "Aug 20, 2026", "0013280000");
        SaveMir(env, "81000001", "81000002");
        r = env.Service.RunMatch();
        var over = r.Rows.Single(x => x.Dn.DnNo == "81000002" && x.PoLine!.LineNo == 1);
        Assert.Equal(MatchStatus.OverPo, over.Status);
        Assert.Equal(1060, over.CumQty, 6);
        Assert.Equal(1050, over.AllowedQty, 6);
        var prog = r.PoLines.Single(p => p.Line.LineNo == 1);
        Assert.True(prog.IsOver);

        // header tolerance (0 %) is stricter; custom 10 % lets it pass
        env.Service.Settings.ToleranceMode = "CUSTOM"; env.Service.Settings.CustomTolerancePct = 0.10;
        r = env.Service.RunMatch();
        Assert.Equal(MatchStatus.Matched, r.Rows.Single(x => x.Dn.DnNo == "81000002" && x.PoLine!.LineNo == 1).Status);
        // persisted on the DN lines (the 4X16 MICA line is now 600 M > 500 + 10 %)
        Assert.All(env.Service.Snapshot.DnLines.Where(l => l.Description.StartsWith("4X10")), l => Assert.Equal(MatchStatus.Matched, l.MatchStatus));
        Assert.Equal(MatchStatus.OverPo, env.Service.Snapshot.DnLines.Last(l => l.Description.StartsWith("4X16")).MatchStatus);
    }

    [Fact]
    public void Not_on_po_and_mir_quantity_and_batch_cross_checks()
    {
        using var env = NewEnv();
        SavePo(env);
        var dn = new DnDocument { Header = new MatDn { DnNo = "81000009", PoNo = PoNo, DnDate = new DateTime(2026, 8, 1) } };
        dn.Lines.Add(new MatDnLine { Order = 1, ItemNo = "10", Description = "4X25 CU/XLPE/SWA/LSOH", Batch = "X1", RawQty = 0.1, RawUnit = "KM", Qty = 100, Unit = "M" });
        dn.Lines.Add(new MatDnLine { Order = 2, ItemNo = "20", Description = "4X10 CU/XLPE/SWA/HF", Batch = "X2", RawQty = 0.2, RawUnit = "KM", Qty = 200, Unit = "M" });
        env.Service.CommitDn(dn);
        var mir = new MirDocument { Header = new MatMir { MirNo = "MIR-EL-000002", Revision = "00" } };
        mir.Dns.Add(new MatMirDn { DnNo = "81000009", PoNo = PoNo });
        mir.Evidence.Add(new MatMirEvidence { Kind = "DNQTY", DnNo = "81000009", Batch = "X2", Qty = 210, Unit = "M" });
        mir.Evidence.Add(new MatMirEvidence { Kind = "LABEL", Batch = "Z9", Qty = 100, Unit = "M" });
        env.Service.CommitMir(mir);
        var r = env.Service.LastMatch;
        Assert.Equal(MatchStatus.NotOnPo, r.Rows.Single(x => x.Line.ItemNo == "10").Status);
        var x2 = r.Rows.Single(x => x.Line.ItemNo == "20");
        Assert.Equal(MatchStatus.QtyMismatch, x2.Status);
        Assert.Equal("OK", x2.BatchCheck);
        Assert.Equal("NOT IN MIR", r.Rows.Single(x => x.Line.ItemNo == "10").BatchCheck);
    }

    [Fact]
    public void Pcs_delivery_is_converted_to_metres_by_pipe_length()
    {
        using var env = NewEnv();
        SavePo(env);
        var dn = new DnDocument { Header = new MatDn { DnNo = "D-PIPE", PoNo = PoNo, DnDate = new DateTime(2026, 8, 1) } };
        dn.Lines.Add(new MatDnLine { Order = 1, ItemNo = "1", Description = "25mm PVC conduit heavy gauge", RawQty = 50, RawUnit = "PCS", Qty = 50, Unit = "PCS" });
        env.Service.CommitDn(dn);
        var row = env.Service.LastMatch.Rows.Single();
        Assert.Equal(3, row.PoLine!.LineNo);
        Assert.Equal(300, row.Qty, 6);
        Assert.Contains("x 6 M", row.ConversionNote);
    }

    [Fact]
    public void Manual_pin_survives_rematch()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        SaveDn(env);
        var line = env.Service.Snapshot.DnLines.First();
        var target = env.Service.Snapshot.LinesOf(po).Single(l => l.LineNo == 3);
        ThreeWayMatcher.PinManual(env.Store, line, target);
        env.Service.RunMatch();
        Assert.Equal(target.Id, env.Service.Snapshot.DnLines.First(l => l.Id == line.Id).PoLineId);
    }

    [Fact]
    public void Supplier_invoice_prev_curr_cum_split_and_hard_lock()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        SaveDn(env);
        SaveMir(env, "81000001");
        env.Service.RunMatch();
        var s = env.Service.Snapshot;
        po = s.Pos.Single();
        Assert.Equal(1, SupplierInvoices.NextInvoiceNo(s, po));
        var first = SupplierInvoices.Invoiceable(s, po, 1).Select(l => l.Id).ToList();
        Assert.Equal(2, first.Count);
        var b1 = env.Service.BuildInvoice(po, 1, first);
        var rows4x10 = b1.Build.Lines.Where(l => l.Kind == "ITEM" && l.ItemNo == "01").ToList();
        Assert.Equal(2, rows4x10.Count);                                         // split over the 2 scope BOQ codes
        Assert.Equal(360, rows4x10.Single(l => l.BoqCode.EndsWith("R-2")).CurrQty, 6);
        Assert.Equal(240, rows4x10.Single(l => l.BoqCode.EndsWith("D-4")).CurrQty, 6);
        Assert.Equal(600 * 28.432 + 300 * 47.242, b1.Build.Totals.CurrGross, 2);
        var h1 = env.Service.SaveInvoice(b1);
        Assert.Equal(SubInvoiceStatus.Draft, h1.Status);
        Assert.StartsWith(SupplierInvoices.Marker, h1.Notes);
        Assert.Single(env.Service.SupplierInvoiceHeaders());
        Assert.Equal(2, env.Service.LockedLines(h1).Count);
        Assert.All(env.Service.Snapshot.Locks, k => Assert.Equal(h1.Id, k.SubInvoiceId));

        // the same DN lines cannot go on INV-02
        Assert.Throws<DnLineLockedException>(() => env.Service.SaveInvoice(env.Service.BuildInvoice(po, 2, first)));
        // nor can the DN be re-imported once invoiced
        Assert.Throws<DnLineLockedException>(() => SaveDn(env));

        // a revision of INV-01 may reuse its own lines
        var rev = InvoiceWorkflow.NextRevision(env.Project.Snapshot.SubInvoices, po.PoNo, po.Supplier, 1);
        Assert.Equal(1, rev);
        var b1r1 = env.Service.BuildInvoice(po, 1, env.Service.LockedLines(h1), rev);
        env.Service.SaveInvoice(b1r1);

        // INV-02 with a new DN: PREV = INV-01 quantity
        SaveDn(env, "81000003", 0.2, "Aug 25, 2026", "0013281111");
        SaveMir(env, "81000001", "81000003");
        env.Service.RunMatch();
        s = env.Service.Snapshot;
        Assert.Equal(2, SupplierInvoices.NextInvoiceNo(s, po));
        var second = SupplierInvoices.Invoiceable(s, po, 2).Select(l => l.Id).ToList();
        Assert.Equal(2, second.Count);
        var b2 = env.Service.BuildInvoice(po, 2, second);
        var r = b2.Build.Lines.Where(l => l.Kind == "ITEM" && l.ItemNo == "01").Sum(l => l.PrevQty);
        Assert.Equal(600, r, 6);
        Assert.Equal(800, b2.Build.Lines.Where(l => l.Kind == "ITEM" && l.ItemNo == "01").Sum(l => l.CumQty), 6);
        env.Service.SaveInvoice(b2);

        // an approved invoice cannot take new lines, and the failed save leaves no lock behind
        InvoiceWorkflow.Approve(env.Project.Store, env.Project.Store.All<SubInvoice>().Single(i => i.InvoiceNo == 1 && i.Revision == 1));
        env.Project.Reload(); env.Service.Reload();
        var locksBefore = env.Service.Snapshot.Locks.Count;
        SaveDn(env, "81000004", 0.1, "Aug 26, 2026", "0013282222");
        env.Service.RunMatch();
        var extra = env.Service.Snapshot.DnLines.Where(l => env.Service.Snapshot.Dns.Single(d => d.DnNo == "81000004").Id == l.DnId).Select(l => l.Id).ToList();
        var locked1 = env.Service.Snapshot.Locks.Where(k => k.InvoiceNo == 1).Select(k => k.DnLineId).ToList();
        var approvedBuild = env.Service.BuildInvoice(po, 1, locked1.Concat(extra).ToList(), 1);
        Assert.Throws<InvalidOperationException>(() => env.Service.SaveInvoice(approvedBuild));
        env.Service.Reload();
        Assert.Equal(locksBefore, env.Service.Snapshot.Locks.Count);

        // release of an abandoned draft frees the lines
        Assert.Equal(2, env.Store.ReleaseLocks(po.Supplier, po.PoNo, 2));
    }

    [Fact]
    public void Supplier_invoice_package_and_mir_tracker_export()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        SaveDn(env);
        SaveDn(env, "81000002", 0.1, "Aug 20, 2026", "0013280000");
        SaveMir(env, "81000001");
        env.Service.RunMatch();
        po = env.Service.Snapshot.Pos.Single();
        var b = env.Service.BuildInvoice(po, 1, SupplierInvoices.Invoiceable(env.Service.Snapshot, po, 1).Select(l => l.Id).ToList());
        env.Service.SaveInvoice(b);
        var zip = env.Service.ExportInvoice(Path.Combine(env.Dir, "out"), b, new InvoiceHeaderInfo());
        Assert.EndsWith("INV-01-Rev0.zip", zip);
        using (var z = ZipFile.OpenRead(zip))
        {
            Assert.Contains(z.Entries, e => e.Name.EndsWith(".pdf"));
            Assert.Equal(2, z.Entries.Count(e => e.Name.EndsWith(".xlsx")));
        }
        var tracker = Directory.GetFiles(Path.Combine(env.Dir, "out"), "*MIR-TRACKER.xlsx").Single();
        using var wb = new XLWorkbook(tracker);
        var ws = wb.Worksheet("MIR TRACKER");
        Assert.Equal("MIR-EL-000001", ws.Cell(3, 6).GetString());
        Assert.Equal("NO MIR", ws.Cell(3, 7).GetString());
        Assert.Equal("DN 81000001", ws.Cell(4, 6).GetString());
        Assert.Equal(600, ws.Cell(6, 6).GetDouble());
        Assert.Contains("SUMPRODUCT", ws.Cell(9, 6).FormulaA1);
        Assert.Contains("OVER PO", ws.Cell(6, 12).FormulaA1);
        Assert.Contains("SUM(F6:G6)", ws.Cell(6, 8).FormulaA1);
        Assert.True(wb.Worksheets.Contains("MIR SUMMARY"));
        Assert.True(wb.Worksheets.Contains("DN LINES"));
    }

    [Fact]
    public void Dn_lookup_by_dn_po_batch_and_supplier()
    {
        using var env = NewEnv();
        var po = SavePo(env);
        SaveDn(env);
        SaveMir(env, "81000001");
        env.Service.RunMatch();
        po = env.Service.Snapshot.Pos.Single();
        var b = env.Service.BuildInvoice(po, 1, SupplierInvoices.Invoiceable(env.Service.Snapshot, po, 1).Select(l => l.Id).ToList());
        var h = env.Service.SaveInvoice(b);
        InvoiceWorkflow.Submit(env.Project.Store, h, "WF-001234");
        env.Project.Reload();

        var byDn = env.Service.Lookup("Test", "81000001").Single();
        Assert.Equal(PoNo, byDn.PoNo);
        Assert.Equal("INV-01 Rev 0", byDn.Invoice);
        Assert.Equal(SubInvoiceStatus.Submitted, byDn.InvoiceStatus);
        Assert.Equal("WF-001234", byDn.AconexNo);
        Assert.Equal("MIR-EL-000001", byDn.MirNo);
        Assert.Equal("DN", byDn.MatchedOn);
        Assert.Equal("PO", env.Service.Lookup(null, "ABC-PO-E-001").Single().MatchedOn);
        Assert.Equal("BATCH", env.Service.Lookup(null, "13289917").Single().MatchedOn);
        Assert.Empty(env.Service.Lookup("Other Supplier", "81000001"));
        Assert.Contains("Test Cables Co.", DnLookup.Suppliers(env.Service.Snapshot));
    }
}
