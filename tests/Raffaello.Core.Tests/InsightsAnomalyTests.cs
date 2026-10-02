using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Imaging;
using Raffaello.Core.Insights;
using Raffaello.Core.Packaging;

namespace Raffaello.Core.Tests;

/// <summary>[insights] Claim anomaly detection on small synthetic ledgers (no real data).</summary>
public class InsightsAnomalyTests
{
    private static long _id;

    internal static ClaimLine Claim(string sub, int inv, string room, string stage, string item, double qty, string wir = "") =>
        new() { Id = Interlocked.Increment(ref _id), Building = Buildings.Branded, Subcontractor = sub, InvoiceNo = inv, Room = room, Stage = stage, Item = item, Qty = qty, WirNo = wir, Source = "TRACKER" };

    internal static Room Room(string code, string type, string area = "APARTMENT") => new() { Id = Interlocked.Increment(ref _id), Building = Buildings.Branded, Code = code, RoomType = type, AreaType = area, Level = "Level 01" };

    internal static RoomQty Cap(string room, string stage, string item, double qty) => new() { Building = Buildings.Branded, Room = room, Stage = stage, Item = item, Qty = qty };

    private static List<Anomaly> Detect(ProjectSnapshot s, InsightsData? d = null) => AnomalyDetector.Detect(new AnomalyInputs { Project = s, Data = d ?? new InsightsData(), Today = new DateTime(2026, 10, 1) });

    [Fact]
    public void Robust_z_score_and_benford()
    {
        Assert.Equal(3, RobustStats.Median(new double[] { 5, 1, 3 }));
        Assert.Equal(2.5, RobustStats.Median(new double[] { 1, 2, 3, 4 }));
        var sample = new double[] { 39, 39, 40, 38, 39, 41, 39, 82 };
        Assert.True(RobustStats.Z(82, sample) > 10);
        Assert.True(Math.Abs(RobustStats.Z(39, sample)) < 1);
        Assert.Equal(0, RobustStats.Z(5, new double[] { 5, 5, 5 }));
        Assert.Equal(99, RobustStats.Z(6, new double[] { 5, 5, 5 }));
        // Benford-like data passes, uniform 5..9 data does not
        var rnd = new Random(1);
        var benford = Enumerable.Range(0, 400).Select(_ => Math.Pow(10, rnd.NextDouble() * 3)).ToList();
        Assert.False(RobustStats.Benford(benford).Deviates);
        Assert.True(RobustStats.Benford(Enumerable.Range(0, 400).Select(i => 5.0 + i % 5)).Deviates);
        Assert.Equal(4, RobustStats.FirstDigit(0.0042));
    }

    [Fact]
    public void Room_far_above_rooms_of_the_same_type_is_flagged_and_can_be_dismissed()
    {
        var s = new ProjectSnapshot();
        for (var i = 1; i <= 8; i++)
        {
            s.Rooms.Add(Room($"P1-{i:00}", "2BR"));
            s.RoomQtys.Add(Cap($"P1-{i:00}", "1ST FIX", "LIGHT", 45));
            s.Claims.Add(Claim("ROOTS", 1, $"P1-{i:00}", "1ST FIX", "LIGHT", i == 8 ? 82 : 38 + i % 3, "WIR-1"));
        }
        var list = Detect(s);
        var a = Assert.Single(list, x => x.Kind == AnomalyKinds.KeyOutlier);
        Assert.Equal("P1-08", a.Room);
        Assert.Equal(InsightSeverity.High, a.Severity);   // also above the room's PROJECT QTY
        Assert.Contains("typical", a.Explanation);
        Assert.NotEmpty(a.Evidence);
        Assert.False(a.IsDismissed);

        var d = new InsightsData { Dismissals = { new InsightDismissal { Fingerprint = a.Fingerprint, Reason = "site confirmed: two flats merged", DismissedBy = "mohamed", Active = true } } };
        var again = Detect(s, d).Single(x => x.Kind == AnomalyKinds.KeyOutlier);
        Assert.True(again.IsDismissed);
        Assert.Equal("site confirmed: two flats merged", again.Dismissal!.Reason);
        Assert.DoesNotContain(InsightsEngine.QueueItems(new[] { again }), q => q.Title == again.Title);
    }

    [Fact]
    public void Key_shared_by_two_subcontractors_over_cap_is_high()
    {
        var s = new ProjectSnapshot
        {
            RoomQtys = { Cap("P2-106", "1ST FIX", "POWER", 59), Cap("P2-107", "1ST FIX", "POWER", 59) },
            Claims =
            {
                Claim("ROOTS", 1, "P2-106", "1ST FIX", "POWER", 36), Claim("ABRAG", 1, "P2-106", "1ST FIX", "POWER", 40),
                Claim("ROOTS", 1, "P2-107", "1ST FIX", "POWER", 20), Claim("ABRAG", 1, "P2-107", "1ST FIX", "POWER", 20),
            },
        };
        var shared = Detect(s).Where(x => x.Kind == AnomalyKinds.MultiSub).ToList();
        Assert.Equal(2, shared.Count);
        Assert.Equal(InsightSeverity.High, shared.Single(x => x.Room == "P2-106").Severity);
        Assert.Equal(InsightSeverity.Low, shared.Single(x => x.Room == "P2-107").Severity);
        Assert.Contains("over the cap", shared.Single(x => x.Room == "P2-106").Explanation);
    }

    [Fact]
    public void Invoice_total_jump_and_repeat_claim_after_cap()
    {
        var s = new ProjectSnapshot();
        var rooms = Enumerable.Range(1, 40).Select(i => $"R{i:00}").ToList();
        foreach (var r in rooms) s.RoomQtys.Add(Cap(r, "1ST FIX", "POWER", 10));
        for (var inv = 1; inv <= 3; inv++)
            for (var k = 0; k < 4; k++) s.Claims.Add(Claim("SUBA", inv, rooms[(inv - 1) * 4 + k], "1ST FIX", "POWER", 10));
        foreach (var r in rooms.Skip(12)) s.Claims.Add(Claim("SUBA", 4, r, "1ST FIX", "POWER", 10));   // 280 points vs 40 usual
        s.Claims.Add(Claim("SUBA", 4, rooms[0], "1ST FIX", "POWER", 10));   // R01 again after reaching its cap in INV 1
        var list = Detect(s);
        var jump = Assert.Single(list, x => x.Kind == AnomalyKinds.InvoiceJump);
        Assert.Equal(4, jump.InvoiceNo);
        Assert.Equal(InsightSeverity.High, jump.Severity);
        var rep = Assert.Single(list, x => x.Kind == AnomalyKinds.KeyRepeat);
        Assert.Equal("R01", rep.Room);
    }

    [Fact]
    public void Length_and_height_claims_far_above_typical()
    {
        var s = new ProjectSnapshot { Rooms = { Room("P3-101", "2BR"), Room("P3-102", "2BR") }, RoomQtys = { Cap("P3-101", "2ND FIX", "DATA", 40) } };
        var len = Claim("ROOTS", 2, "P3-101", "2ND FIX", "DATA", 40);
        len.LengthApplies = true; len.LengthClaimedQty = 150; len.LengthStatus = CheckStatus.Pending;
        var high = Claim("ROOTS", 2, "P3-102", "1ST FIX", "LIGHT", 30);
        high.QtyAbove45 = 20;
        s.Claims.Add(len); s.Claims.Add(high);
        var list = Detect(s);
        var l = Assert.Single(list, x => x.Kind == AnomalyKinds.Length);
        Assert.Equal(InsightSeverity.High, l.Severity);
        Assert.Contains("3.8x", l.Title);
        Assert.Contains("round number", l.Explanation);
        var h = Assert.Single(list, x => x.Kind == AnomalyKinds.Height);
        Assert.Equal(InsightSeverity.Medium, h.Severity);
        Assert.Contains("apartment", h.Explanation);
    }

    [Fact]
    public void Claims_without_wir_before_wir_and_on_open_wir()
    {
        var s = new ProjectSnapshot
        {
            Wirs =
            {
                new Wir { Id = 1, WirNo = "WIR-EL-0001", Status = WirStatus.Approved, ApprovedAt = new DateTime(2026, 9, 20), SubmittedAt = new DateTime(2026, 9, 1) },
                new Wir { Id = 2, WirNo = "WIR-EL-0002", Status = WirStatus.Open, SubmittedAt = new DateTime(2026, 9, 1) },
            },
            Claims =
            {
                Claim("ROOTS", 3, "A", "1ST FIX", "POWER", 10, "WIR-EL-0001"), Claim("ROOTS", 3, "B", "1ST FIX", "POWER", 10, "wir-el-0002"),
                Claim("ROOTS", 3, "C", "1ST FIX", "POWER", 10), Claim("ROOTS", 3, "D", "1ST FIX", "POWER", 5, "WIR-EL-9999"),
            },
        };
        var d = new InsightsData { InvoicePeriods = { new InsightInvoicePeriod { Subcontractor = "roots", InvoiceNo = 3, PeriodEnd = new DateTime(2026, 9, 10) } } };
        var list = Detect(s, d);
        Assert.Single(list, x => x.Kind == AnomalyKinds.NoWir);
        var before = Assert.Single(list, x => x.Kind == AnomalyKinds.BeforeWir);
        Assert.Contains("10 days before", before.Title);
        Assert.Single(list, x => x.Kind == AnomalyKinds.WirNotApproved);
        Assert.Single(list, x => x.Kind == AnomalyKinds.WirUnknown);
    }

    [Fact]
    public void Copied_invoice_and_inconsistent_cumulative()
    {
        var s = new ProjectSnapshot();
        for (var i = 0; i < 8; i++) { s.Claims.Add(Claim("BANDER", 2, $"R{i}", "1ST FIX", "LIGHT", 10 + i)); s.Claims.Add(Claim("BANDER", 3, $"R{i}", "1ST FIX", "LIGHT", 10 + i)); }
        var h1 = new SubInvoice { Id = 11, ContractNo = "C1", Subcontractor = "SUBX", InvoiceNo = 1, Status = SubInvoiceStatus.Approved };
        var h2 = new SubInvoice { Id = 12, ContractNo = "C1", Subcontractor = "SUBX", InvoiceNo = 2, Status = SubInvoiceStatus.Submitted };
        s.SubInvoices.AddRange(new[] { h1, h2 });
        s.SubInvoiceLines.Add(new SubInvoiceLine { SubInvoiceId = 11, ItemNo = "1", BoqCode = "B1", CumQty = 100, CurrQty = 100, Rate = 55 });
        s.SubInvoiceLines.Add(new SubInvoiceLine { SubInvoiceId = 12, ItemNo = "1", BoqCode = "B1", PrevQty = 80, CurrQty = 30, CumQty = 120, Rate = 55 });
        s.ContractItems.Add(new ContractItem { ContractNo = "C1", ItemNo = "1", Rate = 55 });
        var list = Detect(s);
        var copy = Assert.Single(list, x => x.Kind == AnomalyKinds.CopiedInvoice);
        Assert.Equal(3, copy.InvoiceNo);
        Assert.Contains(list, x => x.Kind == AnomalyKinds.Cumulative && x.Title.Contains("not previous + current"));
        Assert.Contains(list, x => x.Kind == AnomalyKinds.Cumulative && x.Title.Contains("previous quantities do not match"));
        Assert.DoesNotContain(list, x => x.Kind == AnomalyKinds.Rate);
    }

    [Fact]
    public void Invoice_rate_different_from_contract_rate()
    {
        var s = new ProjectSnapshot
        {
            ContractItems = { new ContractItem { Id = 5, ContractNo = "C1", ItemNo = "2", Rate = 57 } },
            SubInvoices = { new SubInvoice { Id = 1, ContractNo = "C1", Subcontractor = "ROOTS", InvoiceNo = 1 } },
            SubInvoiceLines = { new SubInvoiceLine { SubInvoiceId = 1, Kind = "ITEM", ItemNo = "2", Rate = 60, CumQty = 100, StagePct = 0.9 } },
            TemplateRows = { new InvoiceTemplateRow { ContractNo = "C1", Kind = "ITEM", ItemNo = "2", Rate = 57 } },
        };
        var r = Assert.Single(Detect(s), x => x.Kind == AnomalyKinds.Rate);
        Assert.Equal(InsightSeverity.High, r.Severity);
        Assert.Contains("invoice 60.00 vs contract 57.00", r.Explanation);
        Assert.Contains("270", r.Title);   // (60 - 57) x 100 x 0.9
    }

    [Fact]
    public void Duplicate_attachment_and_reused_photo()
    {
        var dir = TestData.TempDir();
        var img = new RgbaImage(64, 48);
        for (var y = 0; y < 48; y++) for (var x = 0; x < 64; x++) { var o = (y * 64 + x) * 4; img.Pixels[o] = (byte)(x * 4); img.Pixels[o + 1] = (byte)(y * 5); img.Pixels[o + 2] = (byte)((x * y) % 255); img.Pixels[o + 3] = 255; }
        var a = Path.Combine(dir, "a.png"); var b = Path.Combine(dir, "b.png"); var c = Path.Combine(dir, "c_small.png");
        File.WriteAllBytes(a, PngLite.Encode(img, false));
        File.Copy(a, b);
        File.WriteAllBytes(c, PngLite.Encode(PngLite.Resize(img, 32, 24), false));
        var s = new ProjectSnapshot
        {
            Attachments =
            {
                new Attachment { Id = 1, OwnerKind = AttachmentKinds.Invoice, OwnerKey = "C|SUBA|1|0", FilePath = a },
                new Attachment { Id = 2, OwnerKind = AttachmentKinds.Invoice, OwnerKey = "C|SUBB|2|0", FilePath = b },
            },
        };
        var lineWithPhoto = Claim("SUBA", 1, "R1", "1ST FIX", "LIGHT", 5); lineWithPhoto.HeightPhoto = c; s.Claims.Add(lineWithPhoto);
        var docs = InsightsEngine.CollectDocuments(s);
        Assert.Equal(3, docs.Count);
        var (hashes, changed) = DocumentHashes.Hash(docs.Select(x => x.Path), Array.Empty<InsightFileHash>(), null, DateTime.Now);
        Assert.Equal(3, changed.Count);
        Assert.All(hashes, h => Assert.Equal(16, h.PHash.Length));
        // cached rows are reused
        var (again, changed2) = DocumentHashes.Hash(docs.Select(x => x.Path), hashes, null, DateTime.Now);
        Assert.Empty(changed2);
        var list = AnomalyDetector.Detect(new AnomalyInputs { Project = s, Documents = docs, Hashes = again });
        var dup = Assert.Single(list, x => x.Kind == AnomalyKinds.DuplicateFile);
        Assert.Equal(InsightSeverity.High, dup.Severity);   // two different subcontractors
        Assert.Contains(list, x => x.Kind == AnomalyKinds.DuplicatePhoto);
    }

    [Fact]
    public void Queue_feed_and_invoice_package_page()
    {
        var s = new ProjectSnapshot { RoomQtys = { Cap("R1", "1ST FIX", "POWER", 10) }, Claims = { Claim("SUBA", 1, "R1", "1ST FIX", "POWER", 8), Claim("SUBB", 1, "R1", "1ST FIX", "POWER", 8) } };
        var list = Detect(s);
        var q = InsightsEngine.QueueItems(list).ToList();
        Assert.Contains(q, i => i.Category == "INSIGHT" && i.Severity == Verdict.Check && i.Target.Module == InsightsEngine.NavKey);

        var dir = TestData.TempDir();
        var build = new Raffaello.Core.Invoicing.InvoiceBuild { Header = new SubInvoice { ContractNo = "C", Subcontractor = "SUBA", InvoiceNo = 1, CreatedAt = new DateTime(2026, 9, 1) } };
        var data = new InsightsData();
        var req = new PackageRequest { Snapshot = s, Plans = new List<PlanImage>(), Build = build, OutputFolder = dir };
        req.Sections.Add((work, stamp) => InsightsPackage.Files(req, work, stamp, data));
        var r = InvoicePackageBuilder.Build(req);
        Assert.Contains(r.Entries, e => e.Name == InsightsPackage.FileName);
        Assert.Equal(1, InsightsEngine.ForInvoice(list, "SUBA", 1).Count(a => a.Kind == AnomalyKinds.MultiSub));
        // same inputs, same bytes
        var r2 = InvoicePackageBuilder.Build(req);
        Assert.Equal(r.Entries.Single(e => e.Name == InsightsPackage.FileName).Sha256, r2.Entries.Single(e => e.Name == InsightsPackage.FileName).Sha256);
    }
}
