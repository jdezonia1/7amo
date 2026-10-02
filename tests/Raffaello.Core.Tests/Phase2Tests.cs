using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.HeadOffice;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Packaging;
using Raffaello.Core.Plans;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Raffaello.Core.Tests;

public class Phase2Tests
{
    // 1 x 1 transparent PNG
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static ProjectSnapshot Snapshot() => new()
    {
        Rooms = new()
        {
            new Room { Code = "P9-101", Level = "Level 01", Plan = "L01", RoomType = "1BR-A", AreaType = "APARTMENT", Plot = 9, Floor = 1, Building = Buildings.Branded },
            new Room { Code = "P9-102", Level = "Level 01", Plan = "L01", RoomType = "2BR", AreaType = "APARTMENT", Plot = 9, Floor = 1, Building = Buildings.Branded },
            new Room { Code = "P9-103", Level = "Level 01", Plan = "L01", RoomType = "2BR", AreaType = "APARTMENT", Plot = 9, Floor = 1, Building = Buildings.Branded },
        },
        RoomQtys = new()
        {
            new RoomQty { Room = "P9-101", Stage = "1ST FIX", Item = "POWER", Qty = 40 },
            new RoomQty { Room = "P9-101", Stage = "2ND FIX", Item = "POWER", Qty = 40 },
            new RoomQty { Room = "P9-102", Stage = "1ST FIX", Item = "POWER", Qty = 10 },
        },
        Claims = new()
        {
            new ClaimLine { Subcontractor = "SUBA", InvoiceNo = 1, Room = "P9-101", Stage = "1ST FIX", Item = "POWER", Qty = 40, WirNo = "WIR-EL-0001" },
            new ClaimLine { Subcontractor = "SUBA", InvoiceNo = 1, Room = "P9-101", Stage = "2ND FIX", Item = "POWER", Qty = 10, WirNo = "WIR-EL-0002; WIR-EL-0001" },
            new ClaimLine { Subcontractor = "SUBB", InvoiceNo = 1, Room = "P9-102", Stage = "1ST FIX", Item = "POWER", Qty = 12, QtyAbove45 = 2 },
            new ClaimLine { Subcontractor = "SUBA", InvoiceNo = 1, Room = "STAIR 2", Stage = "1ST FIX", Item = "POWER", Qty = 3 },
        },
        RoomShapes = new()
        {
            new RoomShape { Room = "P9-101", Plan = "L01", Polygons = "0.1,0.1 0.4,0.1 0.4,0.4 0.1,0.4" },
            new RoomShape { Room = "P9-102", Plan = "L01", Polygons = "0.5,0.1 0.9,0.1 0.9,0.4|0.5,0.5 0.6,0.5 0.6,0.6" },
        },
    };

    private static readonly List<PlanImage> Plans = new() { new PlanImage { Plan = "L01", Caption = "Level 01", Png = Png, Width = 6_000_000, Height = 3_000_000 } };

    [Fact]
    public void RoomStatus_FollowsTheLedger()
    {
        var info = RoomStatusCalc.Compute(Snapshot());
        Assert.Equal(RoomStatusKinds.InProgress, info["P9-101"].Status);        // 1st fix done, 2nd fix 10 / 40
        Assert.Equal(0.625, info["P9-101"].UsedPct, 6);
        Assert.Equal(RoomStatusKinds.OverCap, info["P9-102"].Status);
        Assert.Equal(1, info["P9-102"].PendingChecks);
        Assert.Equal(RoomStatusKinds.NoProjectQty, info["P9-103"].Status);
        Assert.Equal(RoomStatusKinds.NoProjectQty, info["STAIR 2"].Status);
        Assert.Equal("SUBA", info["P9-101"].DominantSub);
        var only2nd = RoomStatusCalc.Compute(Snapshot(), new PlanFilter(Stage: "2ND FIX"));
        Assert.Equal(0.25, only2nd["P9-101"].UsedPct, 6);
        var subB = RoomStatusCalc.Compute(Snapshot(), new PlanFilter(Subcontractor: "SUBB"));
        Assert.Equal(RoomStatusKinds.NotStarted, subB["P9-101"].Status);
        Assert.Equal("FFFFFF", RoomStatusCalc.HeatColour(0));
        Assert.Equal("8B0000", RoomStatusCalc.HeatColour(2));
        Assert.Equal((0.5, 0.5), RoomStatusCalc.Centroid(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }));
    }

    [Fact]
    public void HeadOfficeTracker_HasShapesLinksAndProtection()
    {
        var path = Path.Combine(TestData.TempDir(), "ho_tracker.xlsx");
        var r = TrackerExporter.Export(path, Snapshot(), Plans, new TrackerExportScope { Subcontractor = "SUBA", InvoiceNo = 1, ContractNo = "SUB-TEST-001", AsOf = new DateTime(2026, 9, 30) });
        Assert.Equal(1, r.Plans);
        Assert.Equal(2, r.Shapes);
        Assert.Equal(1, r.RoomsWithoutShape);

        using (var doc = SpreadsheetDocument.Open(path, false))
        {
            Assert.Null(doc.WorkbookPart!.VbaProjectPart);
            var wb = doc.WorkbookPart.Workbook;
            Assert.True(wb.WorkbookProtection?.LockStructure?.Value);
            var names = wb.Sheets!.Elements<S.Sheet>().Select(x => x.Name!.Value!).ToList();
            Assert.Equal(new[] { "DASHBOARD", "PLANS", "ROOM DETAILS", "LEDGER", "PROJECT QTY", "CONTROL", "INVOICE SUMMARY" }, names);
            foreach (var sh in wb.Sheets!.Elements<S.Sheet>())
            {
                var ws = ((WorksheetPart)doc.WorkbookPart.GetPartById(sh.Id!)).Worksheet;
                var prot = ws.Elements<S.SheetProtection>().Single();
                Assert.True(prot.Sheet?.Value);
                Assert.False(prot.AutoFilter?.Value ?? true);   // false = allowed
                Assert.False(prot.Sort?.Value ?? true);
            }
            var plans = (WorksheetPart)doc.WorkbookPart.GetPartById(wb.Sheets!.Elements<S.Sheet>().First(x => x.Name == "PLANS").Id!);
            var shapes = plans.DrawingsPart!.WorksheetDrawing.Descendants<Xdr.Shape>().ToList();
            Assert.Equal(new[] { "RM_P9-101-L01", "RM_P9-102-L01" }, shapes.Select(x => x.NonVisualShapeProperties!.NonVisualDrawingProperties!.Name!.Value));
            Assert.Single(plans.DrawingsPart.WorksheetDrawing.Descendants<Xdr.Picture>());
            foreach (var sp in shapes)
            {
                var link = sp.NonVisualShapeProperties!.NonVisualDrawingProperties!.GetFirstChild<DocumentFormat.OpenXml.Drawing.HyperlinkOnClick>()!;
                var rel = plans.DrawingsPart.HyperlinkRelationships.Single(h => h.Id == link.Id);
                Assert.StartsWith("#RD_P9_10", rel.Uri.OriginalString);
                Assert.Contains(wb.DefinedNames!.Elements<S.DefinedName>(), d => "#" + d.Name == rel.Uri.OriginalString && d.Text.StartsWith("'ROOM DETAILS'!"));
            }
            // the over-cap room is dark red, the in-progress one yellow
            Assert.Contains("8B0000", shapes[1].OuterXml);
            Assert.Contains("FFFF00", shapes[0].OuterXml);
            var errors = new OpenXmlValidator().Validate(doc).Where(e => e.Part?.Uri.ToString().Contains("drawing") == true).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Description)));
        }

        // values only, and the same inputs give the same bytes
        var again = Path.Combine(TestData.TempDir(), "ho_tracker2.xlsx");
        TrackerExporter.Export(again, Snapshot(), Plans, new TrackerExportScope { Subcontractor = "SUBA", InvoiceNo = 1, ContractNo = "SUB-TEST-001", AsOf = new DateTime(2026, 9, 30) });
        Assert.Equal(Deterministic.Sha256(path), Deterministic.Sha256(again));
        using var x = new ClosedXML.Excel.XLWorkbook(path);
        Assert.DoesNotContain(x.Worksheets.SelectMany(w => w.CellsUsed()), c => c.HasFormula);
        Assert.Equal("back to plan", x.Worksheet("ROOM DETAILS").Cell(4, 8).GetString());
    }

    private static InvoiceBuild Invoice() => new()
    {
        Header = new SubInvoice { ContractNo = "SUB-TEST-001", Subcontractor = "SUBA", InvoiceNo = 1, Revision = 0, Status = SubInvoiceStatus.Draft, CreatedAt = new DateTime(2026, 9, 30, 10, 0, 0) },
        Lines =
        {
            new SubInvoiceLine { Kind = "SECTION", Description = "1st fix", RowOrder = 1 },
            new SubInvoiceLine { Kind = "ITEM", ItemNo = "1", BoqCode = "B6-01-01-00-6-26-V-5", Description = "small power points", Unit = "No.", ContractQty = 100, Rate = 55, StagePct = 0.9, CumQty = 50, CurrQty = 50, RowOrder = 2 },
        },
    };

    [Fact]
    public void Package_IsCompleteReproducibleAndListsMissingWirs()
    {
        var dir = TestData.TempDir();
        var wirs = Path.Combine(dir, "WIRS", "EL");
        Directory.CreateDirectory(wirs);
        File.WriteAllBytes(Path.Combine(wirs, "DG-DSH-WIR-EL-0001 rev0.pdf"), new byte[] { 1, 2, 3 });
        var contractDoc = Path.Combine(dir, "contract.pdf");
        File.WriteAllBytes(contractDoc, new byte[] { 9, 9, 9 });
        var snap = Snapshot();
        snap.Attachments.Add(new Attachment { OwnerKind = AttachmentKinds.Contract, OwnerKey = "SUB-TEST-001", Kind = AttachmentKinds.Contract, FilePath = contractDoc, FileName = "contract.pdf" });

        PackageRequest Req(string outDir, bool final) => new() { Snapshot = snap, Plans = Plans, Build = Invoice(), OutputFolder = outDir, WirFolder = Path.Combine(dir, "WIRS"), Final = final };
        Assert.Throws<InvalidOperationException>(() => InvoicePackageBuilder.Build(Req(Path.Combine(dir, "a"), final: true)));

        var a = InvoicePackageBuilder.Build(Req(Path.Combine(dir, "a"), final: false));
        Assert.Equal("SUB-TEST-001_SUBA_INV-1_Rev0_DRAFT.zip", Path.GetFileName(a.ZipPath));
        Assert.Equal(new[] { "00_INDEX.pdf", "01_Invoice.xlsx", "02_Invoice_filtered.pdf", "04_Contract/contract.pdf", "05_WIRs/DG-DSH-WIR-EL-0001_rev0.pdf", "07_Tracker_SUBA_INV-1.xlsx", "08_Checks.pdf" },
            a.Entries.Select(e => e.Name));
        Assert.Equal(new[] { "WIR-EL-0002" }, a.MissingWirs);
        using (var z = ZipFile.OpenRead(a.ZipPath))
            Assert.Equal(a.Entries.Select(e => e.Name), z.Entries.Select(e => e.FullName));

        var b = InvoicePackageBuilder.Build(Req(Path.Combine(dir, "b"), final: false));
        Assert.Equal(a.Sha256, b.Sha256);

        // with the signed scan the FINAL package carries it as 03
        var signed = Path.Combine(dir, "signed.pdf");
        File.WriteAllBytes(signed, new byte[] { 7, 7 });
        snap.Attachments.Add(new Attachment { OwnerKind = AttachmentKinds.Invoice, OwnerKey = Attachment.InvoiceKey("SUB-TEST-001", "SUBA", 1, 0), Kind = AttachmentKinds.Signed, FilePath = signed, FileName = "signed.pdf" });
        var f = InvoicePackageBuilder.Build(Req(Path.Combine(dir, "c"), final: true));
        Assert.Equal("FINAL", f.Kind);
        Assert.Contains(f.Entries, e => e.Name == "03_Signed_invoice.pdf");
    }
}
