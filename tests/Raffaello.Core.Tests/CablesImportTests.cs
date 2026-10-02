using ClosedXML.Excel;
using Raffaello.Core.Cables;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Statements;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Tests;

/// <summary>[cables] Tracker CABLES sheets, site statement CABLES sheet round trip, invoice hook, package report, queue.</summary>
public class CablesImportTests
{
    private static readonly string[] Head = { "STAGE", "SUBCONTRACTOR", "INVOICE #", "LOCATION", "LEVEL", "FROM", "TO", "CABLE SIZE", "QTY", "SITE %", "WIR %", "FINAL QTY" };

    /// <summary>The Phase-1 synthetic tracker plus hidden CABLES BRANDED / CABLES HOTEL sheets and ledger CABLE PULLING lines.</summary>
    internal static string TrackerWithCables()
    {
        var path = Phase1Fixtures.Tracker();
        using (var wb = new XLWorkbook(path))
        {
            void Sheet(string name, object[][] rows)
            {
                var ws = wb.Worksheets.Add(name);
                for (var i = 0; i < Head.Length; i++) ws.Cell(1, i + 1).Value = Head[i];
                for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(rows[r][c]);
                ws.Visibility = XLWorksheetVisibility.Hidden;
            }
            Sheet("CABLES BRANDED", new[]
            {
                new object[] { "CABLE PULLING", "SUBA", 3, "BRANDED", "BASEMENT 1", "EMCC-BR-Z2-LB1-FANS", "JF-LB1-04", "4X6", 65, 0.9, 0.8, 46.8 },
                new object[] { "CABLE PULLING", "SUBA", 3, "BRANDED", "BASEMENT 1", "EMCC-BR-Z2-LB1-FANS", "JF-LB1-04", "1X6", 65, 0.9, 0.8, 46.8 },
                new object[] { "CABLE PULLING", "SUBA", 4, "BRANDED", "Ground Floor", "SMDB", "P9-101", "4X16", 101, 0.7, 0.8, 56.56 },
            });
            Sheet("CABLES HOTEL", new[]
            {
                new object[] { "CABLE PULLING", "SUBB", 1, "HOTEL", "BASEMENT 1", "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 145, 0.85, 0.7, 86.3 },
                new object[] { "CABLE PULLING", "SUBB", 1, "HOTEL", "BASEMENT 1", "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "1x16", 145, 0.85, 0.7, 86.3 },
                new object[] { "CABLE PULLING", "SUBA", 4, "HOTEL", "BASEMENT 1", "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 140, 0.7, 0.8, 78.4 },
            });
            var led = wb.Worksheet("LEDGER");
            var row = led.LastRowUsed()!.RowNumber() + 1;
            object[][] lr =
            {
                new object[] { "SUBA", 3, "CABLE PULLING", "BASEMENT 1", "EMCC-BR-Z2-LB1-FANS", "4X6", "m", 65, 0.9, 58.5, 0.8, 46.8, 0, 0, "JF-LB1-04" },
                new object[] { "SUBA", 3, "CABLE PULLING", "BASEMENT 1", "EMCC-BR-Z2-LB1-FANS", "1X6", "m", 65, 0.9, 58.5, 0.8, 46.8, 0, 0, "JF-LB1-04" },
                new object[] { "SUBA", 4, "CABLE PULLING", "Ground Floor", "P9-101", "4X16", "m", 101, 0.7, 70.7, 0.8, 56.56, 0, 0, "" },
            };
            foreach (var r in lr) { for (var c = 0; c < r.Length; c++) led.Cell(row, c + 1).Value = XLCellValue.FromObject(r[c]); row++; }
            wb.Save();
        }
        return path;
    }

    [Fact]
    public void Tracker_cable_sheets_import_as_claims_linked_to_the_ledger_and_flag_duplicates()
    {
        var db = TestData.NewDb();
        var r = TrackerImporter.Read(TrackerWithCables(), Buildings.Branded);
        Assert.Equal(6, r.CableClaims.Count);
        Assert.Equal(3, r.CableClaims.Count(c => c.Building == Buildings.Hotel));
        TrackerImporter.Commit(r, db);
        Assert.Contains("6 cable claims", r.CableSummary);
        var svc = new CableService(CableStore.For(db));
        var snap = svc.Load();
        Assert.Equal(6, snap.Claims.Count);                                   // ledger lines are the same claims, not extra ones
        Assert.Equal(3, snap.Claims.Count(c => c.LedgerSourceKey.Length > 0));
        Assert.Equal(3, snap.Runs.Count);
        var dup = Assert.Single(svc.Flags(snap), f => f.Code == CableFlagCodes.Duplicate);
        Assert.Equal("SUBA", dup.Claim.Subcontractor);                        // SUBA INV 4 repeats SUBB INV 1
        Assert.Equal("SUBB", dup.Related.Single().Subcontractor);
        // importing the same tracker again adds nothing
        var again = TrackerImporter.Read(TrackerWithCables(), Buildings.Branded);
        TrackerImporter.Commit(again, db);
        Assert.Equal(6, svc.Load().Claims.Count);
    }

    [Fact]
    public void Statement_has_a_cables_sheet_that_is_read_back_flagged_and_posted()
    {
        var db = TestData.NewDb();
        var project = new ProjectService(new Settings.AppSettings { DataFilePath = db.Path, SeedDemoData = false }, _ => db);
        project.Initialize();
        var svc = new CableService(CableStore.For(db));
        svc.Store.EnsureSchema();
        svc.SaveRegister(new[] { new CableRun { Building = Buildings.Hotel, FromName = "SMDB-HT-Z1-LB2-CM-01", ToName = "LDB-HT-Z1-LB2-03", SizeKey = "4x16+1x16E", DesignLength = 150 } },
            Array.Empty<CablePanel>(), "schedule");
        svc.Import(new[] { CablesServiceTests.Claim("SUBB", 1, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4X16", 145) }, null, "earlier");

        var path = Path.Combine(TestData.TempDir(), "statement.xlsx");
        project.Workflow.GenerateStatement(path, "SUBA", "ST-07", Buildings.Hotel);
        using (var wb = new XLWorkbook(path))
        {
            Assert.True(wb.Worksheets.Contains(CableSheets.StatementSheet));
            Assert.True(wb.Worksheets.Contains(CableSheets.RunsSheet));
            Assert.Equal("LDB-HT-Z1-LB2-03", wb.Worksheet(CableSheets.RunsSheet).Cell(2, 5).GetString());
            var ws = wb.Worksheet(CableSheets.StatementSheet);
            Assert.Equal(CableSheets.Columns, Enumerable.Range(1, CableSheets.Columns.Length).Select(c => ws.Cell(4, c).GetString()));
            object[][] rows =
            {
                new object[] { "CABLE PULLING", "SUBA", "", "HOTEL", "BASEMENT 1", "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "4x16", 148, 1, 1, "WIR-77" },
                new object[] { "CABLE PULLING", "SUBA", "", "HOTEL", "BASEMENT 1", "SMDB HT-Z1-LB2- CM 01", "LDB-HT-Z1-LB2-03", "1x16", 148, 1, 1, "WIR-77" },
                new object[] { "TERMINATION & TEST", "SUBA", "", "HOTEL", "BASEMENT 1", "EMDB-HT-Z1-LB2", "ESMDB-HT-Z1-LB2-KT-01", "4x120", 135, 1, 1, "" },
            };
            for (var r = 0; r < rows.Length; r++) for (var c = 0; c < rows[r].Length; c++) ws.Cell(5 + r, c + 1).Value = XLCellValue.FromObject(rows[r][c]);
            wb.Save();
        }
        var res = project.Workflow.PreviewStatement(path, 5, Buildings.Hotel);
        Assert.Equal(3, res.CableClaims.Count);
        Assert.All(res.CableClaims, c => Assert.Equal(5, c.InvoiceNo));
        Assert.Contains(res.Issues, i => i.Message.Contains(CableFlagCodes.Duplicate) && i.Message.Contains("SUBB INV 1"));
        Assert.Contains(res.Issues, i => i.Message.Contains(CableFlagCodes.StageOrder));
        Assert.Contains(res.Issues, i => i.Message.Contains(CableFlagCodes.Cumulative));   // 145 + 148 > 150 m
        Assert.False(res.IsDuplicate);

        var n = project.Workflow.CommitStatement(res, path, null);
        Assert.Equal(2, n);   // 2 PULLING ledger lines (termination stays in the cable register)
        var ledger = project.Snapshot.Claims.Where(c => c.Stage == CableStages.LedgerPulling).ToList();
        Assert.Equal(2, ledger.Count);
        Assert.Equal("SMDB HT-Z1-LB2-CM 01", ledger[0].Room);
        Assert.Equal("LDB-HT-Z1-LB2-03", ledger[0].Notes);
        var claims = svc.Load().Claims.Where(c => c.Subcontractor == "SUBA").ToList();
        Assert.Equal(3, claims.Count);
        Assert.Equal(2, claims.Count(c => c.LedgerSourceKey.Length > 0 && ledger.Any(l => l.SourceKey == c.LedgerSourceKey)));
        Assert.True(project.Workflow.PreviewStatement(path, 5, Buildings.Hotel).IsDuplicate);
    }

    [Fact]
    public void Cable_only_statements_have_different_content_hashes()
    {
        var db = TestData.NewDb();
        var project = new ProjectService(new Settings.AppSettings { DataFilePath = db.Path, SeedDemoData = false }, _ => db);
        project.Initialize();
        string Make(string no, double qty)
        {
            var p = Path.Combine(TestData.TempDir(), no + ".xlsx");
            SiteStatementService.Generate(p, "SUBA", no, Array.Empty<Room>());
            using var wb = new XLWorkbook(p);
            var ws = wb.Worksheet(CableSheets.StatementSheet);
            ws.Cell(5, 1).Value = "CABLE PULLING"; ws.Cell(5, 6).Value = "MDB-HT-Z1-LB2"; ws.Cell(5, 7).Value = "SMDB-HT-Z1-LB2-CM-01"; ws.Cell(5, 8).Value = "4x240"; ws.Cell(5, 9).Value = qty;
            wb.Save();
            return p;
        }
        var a = SiteStatementService.Read(Make("ST-1", 50), project.Snapshot, 1, Buildings.Hotel);
        var b = SiteStatementService.Read(Make("ST-2", 60), project.Snapshot, 1, Buildings.Hotel);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Single(a.CableClaims);
        Assert.Empty(a.Claims);
    }

    [Fact]
    public void Invoice_build_adds_termination_stage_at_its_share_and_cable_flags_as_warnings()
    {
        var db = TestData.NewDb();
        var item = Phase1Fixtures.Item("120", "Install, pull and hand over cable 4C x 16mm2 CU/XLPE/SWA/PVC", 10, "m");
        Assert.Equal("CABLE", item.Category);
        Assert.Equal("4X16", item.SizeKey);
        var s = new ProjectSnapshot
        {
            ContractItems = { item },
            ItemBoqs = { new ContractItemBoq { ContractNo = Phase1Fixtures.Contract, ItemNo = "120", BoqCode = "B3-01-01-00-1-1-A-1", BoqDescription = "4c 16mm2 cable" } },
        };
        var svc = new CableService(CableStore.For(db));
        svc.Store.EnsureSchema();
        svc.Import(new[]
        {
            CablesServiceTests.Claim("SUBA", 1, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 100),
            CablesServiceTests.Claim("SUBA", 2, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 100, "TERMINATION & TEST"),
            CablesServiceTests.Claim("SUBB", 2, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 90),
        }, null, "claims");
        var build = new InvoiceBuild
        {
            Header = new SubInvoice { ContractNo = Phase1Fixtures.Contract, Subcontractor = "SUBA", InvoiceNo = 2 },
            Lines = { new SubInvoiceLine { Kind = "ITEM", ItemNo = "120", BoqCode = "B3-01-01-00-1-1-A-1", StagePct = 0.7, Rate = 10, RowOrder = 1 } },
        };
        Cables.CableHooks.AnnotateInvoice(build, db, s);
        var row = build.Lines[0];
        // pulling 100 m is not in the ledger -> added at 70 %; termination 100 m at 20 % = 28.57 m-equivalent at the row's 70 %
        Assert.Equal(100 + 100 * 0.2 / 0.7, row.CumQty, 2);
        Assert.Contains("[CABLE]", row.Explanation);
        Assert.DoesNotContain(build.Warnings, w => w.Contains(CableFlagCodes.Duplicate) && w.Contains("SUBA INV 1"));   // SUBB repeats it, not SUBA

        var subB = new InvoiceBuild
        {
            Header = new SubInvoice { ContractNo = Phase1Fixtures.Contract, Subcontractor = "SUBB", InvoiceNo = 2 },
            Lines = { new SubInvoiceLine { Kind = "ITEM", ItemNo = "120", BoqCode = "B3-01-01-00-1-1-A-1", StagePct = 0.7, Rate = 10, RowOrder = 1 } },
        };
        Cables.CableHooks.AnnotateInvoice(subB, db, s);
        Assert.Contains(subB.Warnings, w => w.Contains(CableFlagCodes.Duplicate) && w.Contains("SUBA INV 1"));
        Assert.Equal(90, subB.Lines[0].CumQty, 3);

        var files = Cables.CableHooks.PackageFiles(db, subB.Header, TestData.TempDir());
        var (name, pdf, _) = Assert.Single(files);
        Assert.Equal("09_Cable_checks.pdf", name);
        Assert.True(new FileInfo(pdf).Length > 1000);

        var queue = Cables.CableHooks.Queue(svc.Store).ToList();
        Assert.Contains(queue, q => q.Category == "CABLES" && q.Severity == Verdict.Over && q.Target.Module == "Cables");
    }

    [Fact]
    public void Register_export_has_house_style_sheets()
    {
        var (_, svc) = CablesServiceTests.NewService();
        svc.Import(new[] { CablesServiceTests.Claim("SUBA", 1, "SMDB-HT-Z1-LB2-CM-01", "LDB-HT-Z1-LB2-03", "4x16", 100) }, null, "c");
        var snap = svc.Load();
        var sheets = CableReports.Export(snap, svc.Flags(snap));
        Assert.Equal(new[] { "CABLE REGISTER", "CABLE CLAIMS", "CABLE FLAGS", "PANELS" }, sheets.Select(x => x.Name));
        var path = Path.Combine(TestData.TempDir(), "reg.xlsx");
        Export.ExcelExporter.Export(path, sheets);
        using var wb = new XLWorkbook(path);
        var hdr = wb.Worksheet("CABLE REGISTER").CellsUsed().First(c => c.GetString() == "REF");
        Assert.Equal(XLColor.FromHtml("#A6A6A6"), hdr.Style.Fill.BackgroundColor);
        Assert.True(hdr.Style.Font.Bold);
        var tree = CableReports.Tree(snap, CableReports.Progress(snap));
        var root = Assert.Single(tree);
        Assert.Equal("SMDB-HT-Z1-LB2-CM-1", root.Panel.Key);
        Assert.Single(root.Children);
        Assert.Single(root.Feeders);
    }
}
