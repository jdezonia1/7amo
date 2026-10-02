using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;
using Raffaello.Core.HeadOffice;
using Raffaello.Core.Imaging;
using Raffaello.Core.Packaging;
using Raffaello.Core.Settings;
using Xunit;

namespace Raffaello.Core.Tests;

public class Phase6Tests
{
    /// <summary>A white plan with a black square at a known place, as a real (large) RGB PNG.</summary>
    private static byte[] Plan(int w, int h, double x0, double y0, double x1, double y1)
    {
        var img = new RgbaImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var dark = x >= x0 * w && x < x1 * w && y >= y0 * h && y < y1 * h;
                // a light grid like drawing lines
                var v = dark ? (byte)0 : (x % 37 == 0 || y % 29 == 0) ? (byte)150 : (byte)255;
                img.Pixels[o] = img.Pixels[o + 1] = img.Pixels[o + 2] = v; img.Pixels[o + 3] = 255;
            }
        return PngLite.Encode(img, grey: false);
    }

    private static (double X, double Y) DarkCentroid(RgbaImage img)
    {
        double sx = 0, sy = 0, n = 0;
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
                if (img.Pixels[(y * img.Width + x) * 4] < 100) { sx += x + 0.5; sy += y + 0.5; n++; }
        return (sx / n / img.Width, sy / n / img.Height);
    }

    [Fact]
    public void Plan_images_shrink_under_budget_and_stay_aligned()
    {
        var png = Plan(3000, 1200, 0.30, 0.40, 0.36, 0.55);
        var before = DarkCentroid(PngLite.Decode(png)!);
        var small = PngLite.Shrink(png, 8_000, startWidth: 1600);
        Assert.True(png.Length > 8_000 && small.Length <= 8_000, $"{png.Length} -> {small.Length} bytes");
        var img = PngLite.Decode(small)!;
        Assert.True(img.Width <= 1600);
        Assert.Equal(3000.0 / 1200, (double)img.Width / img.Height, 2);
        var after = DarkCentroid(img);
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        Assert.Same(png, PngLite.Shrink(png, png.Length + 1));   // already small enough: untouched

        // through the tracker: extent (EMU) unchanged, so the room shapes keep their place over the image
        var plans = new List<PlanImage> { new() { Plan = "L01", Png = png, Width = 6_000_000, Height = 2_400_000 }, new() { Plan = "L02", Png = png, Width = 6_000_000, Height = 2_400_000 } };
        var shrunk = TrackerExporter.ShrinkPlans(plans, 16_000);
        Assert.All(shrunk, p => Assert.True(p.Png!.Length <= 8_000));
        Assert.Equal(plans.Select(p => (p.Width, p.Height)), shrunk.Select(p => (p.Width, p.Height)));
    }

    [Fact]
    public void Invoice_kind_is_explicit_and_inferred_for_old_rows()
    {
        Assert.Equal(InvoiceKinds.Subcontractor, InvoiceKinds.Of(new SubInvoice()));
        Assert.Equal(InvoiceKinds.Supplier, InvoiceKinds.Of(new SubInvoice { Notes = "SUPPLIER INVOICE | PO 45" }));
        Assert.Equal(InvoiceKinds.OwnerMos, InvoiceKinds.Of(new SubInvoice { Kind = InvoiceKinds.OwnerMos }));
    }

    [Fact]
    public void Supplier_package_has_no_room_ledger_parts()
    {
        var dir = TestData.TempDir();
        var build = new Raffaello.Core.Invoicing.InvoiceBuild
        {
            Header = new SubInvoice { ContractNo = "PO-45", Subcontractor = "CABLES CO", InvoiceNo = 1, Kind = InvoiceKinds.Supplier, CreatedAt = new DateTime(2026, 9, 1) },
            Lines = { new SubInvoiceLine { Kind = "ITEM", ItemNo = "01", Description = "cable", Unit = "M", ContractQty = 100, Rate = 10, StagePct = 1, CumQty = 50, CurrQty = 50 } },
        };
        var extra = Path.Combine(dir, "mir.xlsx");
        File.WriteAllBytes(extra, new byte[] { 1 });
        var r = InvoicePackageBuilder.Build(new PackageRequest
        {
            Snapshot = new Raffaello.Core.Data.ProjectSnapshot(), Plans = new List<PlanImage>(), Build = build, OutputFolder = dir, ExtraFiles = { ("07_MIR_tracker.xlsx", extra, "MIR tracker") },
        });
        Assert.Equal(new[] { "00_INDEX.pdf", "01_Invoice.xlsx", "02_Invoice_filtered.pdf", "07_MIR_tracker.xlsx" }, r.Entries.Select(e => e.Name));
        Assert.Contains(r.Warnings, w => w.Contains("SUPPLIER"));
    }

    [Fact]
    public void Package_takes_WIRs_from_the_Aconex_register_first()
    {
        var dir = TestData.TempDir();
        var wir = Path.Combine(dir, "downloaded", "x.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(wir)!);
        File.WriteAllBytes(wir, new byte[] { 5 });
        var snap = new Raffaello.Core.Data.ProjectSnapshot { Claims = { new ClaimLine { Subcontractor = "SUBA", InvoiceNo = 1, Room = "R", Stage = "1ST FIX", Item = "POWER", Qty = 1, WirNo = "WIR-EL-0007" } } };
        var build = new Raffaello.Core.Invoicing.InvoiceBuild { Header = new SubInvoice { ContractNo = "C", Subcontractor = "SUBA", InvoiceNo = 1, CreatedAt = new DateTime(2026, 9, 1) } };
        var r = InvoicePackageBuilder.Build(new PackageRequest
        {
            Snapshot = snap, Plans = new List<PlanImage>(), Build = build, OutputFolder = dir, WirFolder = Path.Combine(dir, "none"),
            RegisteredDocuments = { ("DG-TEST-WIR-EL-0007", wir) },
        });
        Assert.Contains(r.Entries, e => e.Name == "05_WIRs/x.pdf");
        Assert.Empty(r.MissingWirs);
    }

    [Fact]
    public void Aconex_outcome_is_proposed_never_applied()
    {
        var inv = new SubInvoice { Id = 7, ContractNo = "C", Subcontractor = "SUBA", InvoiceNo = 1, Status = SubInvoiceStatus.Submitted };
        var links = new[] { new AconexWorkflowLink { SubInvoiceId = 7, WorkflowNo = "DSH-WF-000123", Active = true } };
        var ok = AconexOutcomes.Propose(new WorkflowLookupResult { WorkflowNo = "dsh-wf-000123", State = WorkflowStates.Approved }, links, new[] { inv });
        Assert.Equal(OutcomeProposal.Approve, ok!.Action);
        Assert.Equal(SubInvoiceStatus.Submitted, inv.Status);
        var rej = AconexOutcomes.Propose(new WorkflowLookupResult
        {
            WorkflowNo = "DSH-WF-000123", State = WorkflowStates.Rejected,
            Steps = { new WorkflowStep { StepName = "QS review", StepOutcome = "Reject - quantities above WIR" } },
        }, links, new[] { inv });
        Assert.Equal(OutcomeProposal.Reject, rej!.Action);
        Assert.Contains("quantities above WIR", rej.Reason);
        Assert.Contains("Rev 1", rej.Question);
        Assert.Null(AconexOutcomes.Propose(new WorkflowLookupResult { WorkflowNo = "DSH-WF-000123", State = WorkflowStates.InProgress }, links, new[] { inv }));
        inv.Locked = true;
        Assert.Null(AconexOutcomes.Propose(new WorkflowLookupResult { WorkflowNo = "DSH-WF-000123", State = WorkflowStates.Approved }, links, new[] { inv }));
    }

    [Fact]
    public void Shared_folders_default_under_one_root()
    {
        var f = ProjectFolders.From(new AppSettings { DocumentsRoot = @"/share/RAFFAELLO", WirFolder = "/other/WIR" });
        Assert.Equal("/other/WIR", f.Wir);
        Assert.Equal(Path.Combine("/share/RAFFAELLO", "MIR"), f.Mir);
        Assert.Equal(Path.Combine("/share/RAFFAELLO", "Packages"), f.Packages);
        Assert.Equal(Path.Combine("/share/RAFFAELLO", "Aconex", "Screenshots"), f.AconexScreenshots);
    }

    [Fact]
    public void Hotel_room_list_is_read_by_header_text()
    {
        var path = Path.Combine(TestData.TempDir(), "hotel_rooms.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("KEYS");
            ws.Cell(1, 1).Value = "HOTEL KEY SCHEDULE - TEST";
            string[] h = { "Floor", "Key Type", "Room No.", "Zone", "1ST FIX|POWER", "2ND FIX|LIGHT" };
            for (var i = 0; i < h.Length; i++) ws.Cell(3, i + 1).Value = h[i];
            object[][] rows =
            {
                new object[] { "Level 03", "KING", "H-301", "EAST", 12, 20 },
                new object[] { "Level 03", "Corridor", "H-3COR", "EAST", 4, 30 },
                new object[] { "B1", "Kitchen", "H-B1-KIT", "", 40, 0 },
                new object[] { "Level 03", "KING", "H-301", "EAST", 1, 1 },
            };
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(4 + r, c + 1).Value = ClosedXML.Excel.XLCellValue.FromObject(rows[r][c]);
            wb.SaveAs(path);
        }
        var res = Raffaello.Core.Tracker.RoomListImporter.Read(path, Buildings.Hotel);
        Assert.Equal(3, res.HeaderRow);
        Assert.Equal(new[] { "H-301", "H-3COR", "H-B1-KIT" }, res.Rooms.Select(r => r.Code));
        Assert.Equal(new[] { "GUESTROOM", "FOH", "BOH" }, res.Rooms.Select(r => r.AreaType));
        Assert.All(res.Rooms, r => Assert.Equal(Buildings.Hotel, r.Building));
        Assert.Equal(-1, res.Rooms[2].Floor);
        Assert.Equal(5, res.Quantities.Count);   // zero cells skipped, duplicate room skipped
        Assert.Contains(res.Issues, i => i.Message.Contains("twice"));
        Assert.Contains(res.Quantities, q => q.Room == "H-301" && q.Stage == "2ND FIX" && q.Item == "LIGHT" && q.Qty == 20);
    }

    [Fact]
    public void Reports_build_from_real_shaped_data_and_render()
    {
        var snap = new Raffaello.Core.Data.ProjectSnapshot
        {
            Rooms = { new Room { Code = "P1-1", Level = "Level 01", Building = Buildings.Branded }, new Room { Code = "H-301", Level = "Level 03", Building = Buildings.Hotel } },
            RoomQtys = { new RoomQty { Room = "P1-1", Stage = "1ST FIX", Item = "POWER", Qty = 10, Building = Buildings.Branded }, new RoomQty { Room = "H-301", Stage = "1ST FIX", Item = "POWER", Qty = 5, Building = Buildings.Hotel } },
            Claims =
            {
                new ClaimLine { Subcontractor = "SUBA", InvoiceNo = 1, Room = "P1-1", Stage = "1ST FIX", Item = "POWER", Qty = 12, Building = Buildings.Branded, QtyAbove45 = 2, HeightStatus = CheckStatus.Rejected },
                new ClaimLine { Subcontractor = "SUBB", InvoiceNo = 1, Room = "H-301", Stage = "1ST FIX", Item = "POWER", Qty = 3, Building = Buildings.Hotel, LengthApplies = true, LengthClaimedQty = 5, LengthStatus = CheckStatus.Rejected },
            },
            SubInvoices = { new SubInvoice { Id = 1, Subcontractor = "SUBA", ContractNo = "C", InvoiceNo = 1, Status = SubInvoiceStatus.Approved, CreatedAt = new DateTime(2026, 8, 3) } },
            SubInvoiceLines = { new SubInvoiceLine { SubInvoiceId = 1, Kind = "ITEM", Rate = 10, StagePct = 1, CurrQty = 12, CumQty = 12 } },
        };
        var vos = new List<Raffaello.Core.Variations.Variation> { new() { Id = 1, Number = "VO-001", Status = Raffaello.Core.Variations.VariationStatus.Submitted, SubmittedAt = new DateTime(2026, 8, 1), Date = new DateTime(2026, 7, 30) } };
        var inputs = new Raffaello.Core.Reports.ReportInputs { Project = snap, Variations = vos, AsOf = new DateTime(2026, 10, 1) };
        var card = Raffaello.Core.Reports.ProjectReports.Build("SCORECARDS", inputs)[0];
        var suba = card.Rows.Single(r => (string)r[0]! == "SUBA");
        Assert.Equal(1, suba[6]);                 // keys over cap
        Assert.Equal(2.0, suba[9]);               // >4.5 m rejected
        Assert.Equal(120.0, suba[17]);            // certified SAR
        var subb = card.Rows.Single(r => (string)r[0]! == "SUBB");
        Assert.Equal(2.0, subb[13]);              // 15 m extra rejected
        var hotelOnly = Raffaello.Core.Reports.ProjectReports.Build("WEEKLY", new Raffaello.Core.Reports.ReportInputs { Project = snap, Building = Buildings.Hotel })[0];
        Assert.Equal(5.0, hotelOnly.Rows.Single()[1]);
        Assert.Equal(61, Raffaello.Core.Reports.ProjectReports.Build("VO", inputs)[0].Rows.Single()[12]);
        var dir = TestData.TempDir();
        foreach (var def in Raffaello.Core.Reports.ProjectReports.All)
        {
            var sheets = Raffaello.Core.Reports.ProjectReports.Build(def.Key, inputs);
            Raffaello.Core.Export.ExcelExporter.Export(Path.Combine(dir, def.Key + ".xlsx"), sheets);
            Raffaello.Core.Reports.ProjectReports.ExportPdf(Path.Combine(dir, def.Key + ".pdf"), def.Name, sheets);
            Assert.True(new FileInfo(Path.Combine(dir, def.Key + ".pdf")).Length > 1000);
        }
    }

    [Fact]
    public void Module_queue_items_for_Aconex_and_variations()
    {
        var board = new List<StatusBoardRow>
        {
            new() { Invoice = "SUBA INV-02 Rev 0", WorkflowNo = "WF-1", IsOverdue = true, DaysOverdue = 4, CurrentStep = "QS", WithWhom = "HO", State = WorkflowStates.Overdue },
            new() { Invoice = "SUBA INV-01 Rev 0", WorkflowNo = "WF-0", State = WorkflowStates.Approved, InvoiceStatus = SubInvoiceStatus.Submitted },
        };
        var items = Raffaello.Core.Queue.ModuleQueue.Aconex(board).ToList();
        Assert.Contains(items, i => i.Title.Contains("overdue 4 days"));
        Assert.Contains(items, i => i.Title.Contains("approved in Aconex"));
        var vo = Raffaello.Core.Queue.ModuleQueue.Variations(new[] { new Raffaello.Core.Variations.Variation { Number = "VO-003", Status = Raffaello.Core.Variations.VariationStatus.Submitted, SubmittedAt = new DateTime(2026, 8, 1) } }, new DateTime(2026, 10, 1)).ToList();
        Assert.Single(vo);
        Assert.Contains("VO-003", vo[0].Detail);
    }
}
