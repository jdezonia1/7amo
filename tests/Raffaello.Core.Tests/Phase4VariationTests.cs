using ClosedXML.Excel;
using QuestPDF.Fluent;
using Raffaello.Core.Domain;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Tests;

public class Phase4VariationTests
{
    private static readonly DateTime Today = new(2026, 10, 2);

    private static VariationLine Omit(string code, double qty, double rate) => new() { Kind = VariationLineKinds.Omission, ItemCode = code, Description = "omit " + code, Unit = "no", Qty = qty, Rate = rate };
    private static VariationLine Add(string code, double qty, double rate) => new() { Kind = VariationLineKinds.Addition, ItemCode = code, Description = "add " + code, Unit = "no", Qty = qty, Rate = rate };
    private static VariationLine New(double qty, double m, double l, double e, double oh, double p) =>
        new() { Kind = VariationLineKinds.NewItem, Description = "New feature lighting", Unit = "no", Qty = qty, Material = m, Labour = l, Equipment = e, OverheadPct = oh, ProfitPct = p };

    [Fact]
    public void Amounts_signs_build_up_and_totals()
    {
        Assert.Equal(126.5, VariationMath.BuildUpRate(80, 20, 0, 0.10, 0.15)); // 100 x 1.1 x 1.15
        var lines = new[] { Omit("B6-1", 10, 55), Add("B6-2", 4, 57), New(2, 80, 20, 0, 0.10, 0.15) };
        Assert.Equal(-550, VariationMath.Amount(lines[0]));
        Assert.Equal(-550, VariationMath.Amount(lines[0].WithNegativeQty()));
        Assert.Equal(228, VariationMath.Amount(lines[1]));
        Assert.Equal(253, VariationMath.Amount(lines[2]));
        var t = VariationMath.Totals(lines);
        Assert.Equal(228, t.Additions);
        Assert.Equal(-550, t.Omissions);
        Assert.Equal(253, t.NewItems);
        Assert.Equal(481, t.AddTotal);
        Assert.Equal(-69, t.Net);
        Assert.Empty(VariationMath.Validate(lines[0]));
        Assert.Contains("rate build-up is empty", VariationMath.Validate(New(1, 0, 0, 0, 0, 0)));
        Assert.Contains("existing item code missing", VariationMath.Validate(new VariationLine { Kind = VariationLineKinds.Addition, Description = "x", Unit = "m", Qty = 1, Rate = 1 }));
    }

    [Fact]
    public void Ageing_counts_open_variations_by_bucket()
    {
        var vs = new[]
        {
            new Variation { Id = 1, Status = VariationStatus.Submitted, Date = Today.AddDays(-50), SubmittedAt = Today.AddDays(-40) },
            new Variation { Id = 2, Status = VariationStatus.UnderReview, Date = Today.AddDays(-100), SubmittedAt = Today.AddDays(-95) },
            new Variation { Id = 3, Status = VariationStatus.Draft, Date = Today.AddDays(-5) },
            new Variation { Id = 4, Status = VariationStatus.Approved, Date = Today.AddDays(-200), SubmittedAt = Today.AddDays(-200), DecidedAt = Today.AddDays(-150) },
        };
        Assert.Equal(40, VariationMath.AgeDays(vs[0], Today));
        Assert.Equal(50, VariationMath.AgeDays(vs[3], Today));
        var a = VariationMath.Ageing(vs, v => v.Id * 100, Today);
        Assert.Equal(new[] { 1, 1, 0, 1 }, a.Select(x => x.Count));
        Assert.Equal(300, a[0].Net);
        Assert.Equal(200, a[3].Net);
    }

    [Fact]
    public void Status_rules()
    {
        Assert.True(VariationStatus.CanMove(VariationStatus.Draft, VariationStatus.Submitted));
        Assert.False(VariationStatus.CanMove(VariationStatus.Draft, VariationStatus.Approved));
        Assert.True(VariationStatus.CanMove(VariationStatus.Rejected, VariationStatus.Draft));
        Assert.Empty(VariationStatus.Next(VariationStatus.Approved));
        Assert.True(VariationStatus.IsClosed(VariationStatus.Withdrawn));
    }

    [Fact]
    public void Store_numbers_saves_locks_and_logs_status()
    {
        var db = TestData.NewDb();
        var s = new SqliteVariationStore(db.Path, "tester", "TESTPC");
        s.EnsureSchema();
        var v1 = s.Create(new Variation { Type = VariationTypes.Vo, Title = "Extra sockets in lobby", Date = Today.AddDays(-3) });
        var v2 = s.Create(new Variation { Type = VariationTypes.Vo, Title = "Second" });
        var e1 = s.Create(new Variation { Type = VariationTypes.Ei, Title = "EI" });
        Assert.Equal("VO-001", v1.Number);
        Assert.Equal("VO-002", v2.Number);
        Assert.Equal("EI-001", e1.Number);
        Assert.Throws<InvalidOperationException>(() => s.Create(new Variation { Number = "vo-001", Title = "dup" }));

        Assert.Throws<InvalidOperationException>(() => s.SetStatus(v1, VariationStatus.Submitted)); // no lines yet
        s.Save(v1, new List<VariationLine> { Omit("B6-1", 10, 55), New(2, 80, 20, 0, 0.1, 0.15) });
        var lines = s.Lines(v1.Id);
        Assert.Equal(2, lines.Count);
        Assert.Equal(126.5, lines[1].Rate); // stored computed rate for new items

        lines.RemoveAt(0);
        lines.Add(Add("B6-3", 1, 10));
        s.Save(v1, lines);
        Assert.Equal(new[] { VariationLineKinds.NewItem, VariationLineKinds.Addition }, s.Lines(v1.Id).Select(l => l.Kind));

        v1 = s.SetStatus(v1, VariationStatus.Submitted, "sent", Today.AddDays(-1));
        Assert.Equal(Today.AddDays(-1), v1.SubmittedAt);
        Assert.Throws<InvalidOperationException>(() => s.SetStatus(v1, VariationStatus.Draft + "X"));
        v1 = s.SetStatus(v1, VariationStatus.Approved, "consultant ok", Today);
        Assert.Equal(Today, v1.DecidedAt);
        Assert.Throws<InvalidOperationException>(() => s.Save(v1, s.Lines(v1.Id)));
        Assert.Equal(new[] { "", VariationStatus.Draft, VariationStatus.Submitted }, s.StatusLog(v1.Id).Select(x => x.FromStatus));

        Assert.Throws<InvalidOperationException>(() => s.Delete(v1));
        s.Delete(s.Get(v2.Id)!);
        Assert.Equal(2, s.Variations().Count);
        Assert.Contains(db.RecentAudit(50), a => a.Summary.Contains("VO-001"));
    }

    private static List<SuggestCandidate> Candidates() => SuggestCandidate.From(
        new[]
        {
            new BoqItem { Id = 1, ItemCode = "B6-01-01-00-1", Description = "Supply and install 4C x 16 mm2 CU/XLPE/SWA/LSOH cable 0.6/1kV", Unit = "m", Rate = 95 },
            new BoqItem { Id = 2, ItemCode = "B6-01-01-00-2", Description = "Supply and install 4C x 25 mm2 CU/XLPE/SWA/LSOH cable 0.6/1kV", Unit = "m", Rate = 130 },
            new BoqItem { Id = 3, ItemCode = "B6-02-01-00-1", Description = "13A twin switched socket outlet, wall mounted", Unit = "no", Rate = 55 },
            new BoqItem { Id = 4, ItemCode = "B6-03-01-00-1", Description = "Recessed LED downlight 18W IP44 including driver", Unit = "no", Rate = 210 },
            new BoqItem { Id = 5, ItemCode = "B6-04-01-00-1", Description = "Cable tray 300mm hot dip galvanized", Unit = "m", Rate = 160 },
        },
        new[]
        {
            new ContractItem { Id = 10, ContractNo = "SUB-1", ItemNo = "12", Description = "تمديد كابل 4x16 مم نحاس", Unit = "م.ط", Rate = 12 },
            new ContractItem { Id = 11, ContractNo = "SUB-1", ItemNo = "2", Description = "Wiring of 13A socket outlet, 2nd fix", Unit = "عدد", Rate = 57 },
        }).ToList();

    [Fact]
    public void Suggestions_rank_by_words_and_technical_attributes()
    {
        var sug = new BoqSuggester(Candidates());
        var r = sug.Suggest("Feeder change to 4 core 16mm2 copper LSOH cable", "Revise feeder to DB-L2 using 4Cx16 sqmm armoured cable, 0.6/1 kV.");
        Assert.Equal("B6-01-01-00-1", r[0].Code);
        var c25 = r.First(x => x.Code == "B6-01-01-00-2");
        Assert.True(r[0].Score > c25.Score);
        Assert.Contains("size differs", c25.Why);
        Assert.True(r.Take(3).Any(x => x.Code == "SUB-1 #12"), string.Join(" || ", r.Select(x => $"{x.Code} {x.Score} {x.KeywordScore} {x.AttributeScore} {x.Why}"))); // Arabic description matched on 4X16

        var s2 = sug.Suggest("Additional twin socket outlets in lobby", "", unit: "no");
        Assert.Equal("B6-02-01-00-1", s2[0].Code);
        Assert.Empty(sug.Suggest("", ""));
        Assert.True(BoqSuggester.UnitsAgree("م.ط", "m"));
        Assert.True(BoqSuggester.UnitsAgree("Nos", "عدد"));
        Assert.False(BoqSuggester.UnitsAgree("m", "no"));
    }

    [Fact]
    public void Fingerprint_reads_cable_conduit_and_device_attributes()
    {
        var f = AttributeFingerprint.Of("4C x 16mm2 CU XLPE SWA LSOH 0.6/1kV, 25mm PVC conduit, 32A TP isolator IP65, 18W downlight, 48 way DB");
        foreach (var k in new[] { "CABLE 4X16", "CU", "ARMOURED", "LSOH", "0.6/1KV", "SIZE 25MM", "PVC", "32A", "TP", "ISOLATOR", "IP65", "18W", "LIGHT", "48 WAY", "PANEL", "CONDUIT" })
            Assert.Contains(k, f);
        var (score, matched, conflicts) = AttributeFingerprint.Compare(AttributeFingerprint.Of("4x16 cable"), AttributeFingerprint.Of("4x25 cable"));
        Assert.Single(conflicts);
        Assert.Contains("CABLE", matched);
        Assert.True(score < 0.5);
    }

    [Fact]
    public void Claude_reply_is_parsed_and_merged_defensively()
    {
        var sug = new BoqSuggester(Candidates()).Suggest("socket outlet and cable", "4x16 cable");
        var prompt = ClaudeRanker.BuildPrompt("t", "b", sug);
        Assert.Contains("1. [", prompt);
        var ranking = ClaudeRanker.ParseRanking("Sure:\n```json\n[{\"i\": 2, \"score\": 0.95, \"why\": \"exact match\"}, {\"i\": 99, \"score\": 1}, {\"i\": \"x\"}, {\"i\": 2, \"score\": 0.1}]\n```", sug.Count);
        var only = Assert.Single(ranking);
        Assert.Equal(1, only.Index);
        var merged = ClaudeRanker.Merge(sug, ranking);
        Assert.Equal(sug[1].Code, merged[0].Code);
        Assert.StartsWith("AI: exact match", merged[0].Why);
        Assert.Equal(sug.Count, merged.Count);
        Assert.Empty(ClaudeRanker.ParseRanking("no json here", 3));
        Assert.Empty(ClaudeRanker.ParseRanking("[not json", 3));
    }

    [Fact]
    public void Pdf_text_is_extracted_and_non_pdfs_are_flagged()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var dir = TestData.TempDir();
        var pdf = Path.Combine(dir, "ei.pdf");
        Document.Create(d => d.Page(p => p.Content().Text("Engineer instruction: add 4C x 16mm2 cable feeder to DB-L2 and twin socket outlets."))).GeneratePdf(pdf);
        var x = VariationDocuments.Extract(pdf);
        Assert.Equal("TEXT", x.Status);
        Assert.Equal(1, x.Pages);
        Assert.Contains("twin socket", x.Text);

        var blank = Path.Combine(dir, "scan.pdf");
        Document.Create(d => d.Page(p => p.Content().Text(""))).GeneratePdf(blank);
        Assert.Equal("SCANNED", VariationDocuments.Extract(blank).Status);

        var img = Path.Combine(dir, "photo.jpg");
        File.WriteAllBytes(img, new byte[] { 1, 2 });
        Assert.Equal("NOT PDF", VariationDocuments.Extract(img).Status);

        var bad = Path.Combine(dir, "bad.pdf");
        File.WriteAllText(bad, "not a pdf");
        Assert.StartsWith("ERROR", VariationDocuments.Extract(bad).Status);

        var doc = VariationDocuments.Prepare(5, pdf, Path.Combine(dir, "store"));
        Assert.True(File.Exists(doc.Path));
        Assert.Equal(64, doc.Sha256.Length);
        Assert.Equal(5, doc.VariationId);
    }

    [Fact]
    public void Submission_and_register_exports_use_the_house_style()
    {
        var dir = TestData.TempDir();
        var v = new Variation { Id = 1, Number = "VO-001", Type = VariationTypes.Vo, Date = Today, Title = "Lobby sockets", Description = "Consultant asked for more sockets.", Status = VariationStatus.Draft, ConsultantRef = "EI-CONS-12" };
        var lines = new List<VariationLine> { Omit("B6-1", 10, 55), Add("B6-2", 4, 57), New(2, 80, 20, 0, 0.10, 0.15) };
        var docs = new List<VariationDoc> { new() { FileName = "ei.pdf", Pages = 2, TextStatus = "TEXT", Sha256 = new string('a', 64), AddedAt = Today } };
        var xlsx = Path.Combine(dir, "vo.xlsx");
        VariationExporter.SubmissionExcel(xlsx, v, lines, docs, new VariationHeaderInfo("DEMO PROJECT", "DEMO CONTRACTOR", "QS"));
        using (var wb = new XLWorkbook(xlsx))
        {
            var ws = wb.Worksheet("VO-001");
            var header = ws.CellsUsed().First(c => c.GetString() == "DESCRIPTION" && c.Style.Fill.BackgroundColor.Color.ToArgb() == System.Drawing.Color.FromArgb(0xA6, 0xA6, 0xA6).ToArgb());
            Assert.True(header.Style.Font.Bold);
            Assert.Equal(XLColor.Black, header.Style.Font.FontColor);
            var net = ws.CellsUsed().First(c => c.GetString() == "NET VARIATION").CellRight();
            Assert.Equal(-69, net.GetDouble(), 2);
            Assert.True(net.HasFormula);
            var build = wb.Worksheet("RATE BUILD-UP");
            Assert.Equal(126.5, build.Cell(2, 12).GetDouble(), 2);
            Assert.Equal("ei.pdf", wb.Worksheet("DOCUMENTS").Cell(2, 1).GetString());
        }
        var pdf = Path.Combine(dir, "vo.pdf");
        VariationExporter.SubmissionPdf(pdf, v, lines, new VariationHeaderInfo("DEMO PROJECT"));
        Assert.True(new FileInfo(pdf).Length > 1000);

        var rows = VariationExporter.RegisterRows(new[] { v }, lines.Select(l => { l.VariationId = 1; return l; }), docs.Select(d => { d.VariationId = 1; return d; }), Today);
        var sheet = VariationExporter.RegisterSheet(rows, Today);
        Assert.Equal(-69.0, (double)sheet.Rows[0][9]!);
        Assert.Equal(1, sheet.Rows[0][15]);
        var reg = Path.Combine(dir, "reg.xlsx");
        Raffaello.Core.Export.ExcelExporter.Export(reg, sheet, VariationExporter.AgeingSheet(VariationMath.Ageing(new[] { v }, _ => -69, Today)));
        Assert.True(File.Exists(reg));
    }
}

internal static class VariationLineTestExtensions
{
    /// <summary>Typed negative quantity on an omission still gives a negative amount.</summary>
    public static VariationLine WithNegativeQty(this VariationLine l) => new()
    {
        Kind = l.Kind, ItemCode = l.ItemCode, Description = l.Description, Unit = l.Unit, Qty = -Math.Abs(l.Qty), Rate = l.Rate,
    };
}
