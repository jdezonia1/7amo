using ClosedXML.Excel;
using Raffaello.Core.Ai;
using Raffaello.Core.Analytics;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Import;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Tests;

public class ForecastTests
{
    [Fact]
    public void Regression_FitsStraightLine()
    {
        var (slope, intercept, r2) = ProjectAnalytics.LinearRegression(new double[] { 0, 1, 2, 3 }, new[] { 0.1, 0.2, 0.3, 0.4 });
        Assert.Equal(0.1, slope, 6);
        Assert.Equal(0.1, intercept, 6);
        Assert.Equal(1.0, r2, 6);
    }

    [Fact]
    public void Forecast_ExtrapolatesTo100Percent()
    {
        var w0 = new DateTime(2026, 1, 4);
        // 5% per week from 0 -> week 20 reaches 100%
        var curve = Enumerable.Range(0, 10).Select(i => new WeeklyPoint(w0.AddDays(7 * i), 0, 0.05 * i)).ToList();
        var f = ProjectAnalytics.ForecastCompletion(curve, window: 8);
        Assert.Equal(0.05, f.SlopePerWeek, 6);
        Assert.Equal(w0.AddDays(7 * 20), f.CompletionDate);
    }

    [Fact]
    public void Forecast_FlatProgress_HasNoDate()
    {
        var w0 = new DateTime(2026, 1, 4);
        var curve = Enumerable.Range(0, 6).Select(i => new WeeklyPoint(w0.AddDays(7 * i), 0, 0.3)).ToList();
        Assert.Null(ProjectAnalytics.ForecastCompletion(curve).CompletionDate);
    }

    [Fact]
    public void Logistic_IsNormalisedSCurve()
    {
        Assert.Equal(0, ProjectAnalytics.Logistic(0), 9);
        Assert.Equal(1, ProjectAnalytics.Logistic(1), 9);
        Assert.Equal(0.5, ProjectAnalytics.Logistic(0.5), 9);
        Assert.True(ProjectAnalytics.Logistic(0.25) < 0.25);
    }

    [Fact]
    public void Ageing_Buckets()
    {
        var today = new DateTime(2026, 10, 2);
        var wirs = new[] { 1, 7, 8, 14, 15, 30, 31, 90 }.Select(d => new Wir { Status = WirStatus.Open, SubmittedAt = today.AddDays(-d) })
            .Append(new Wir { Status = WirStatus.Approved, SubmittedAt = today.AddDays(-100) });
        var b = ProjectAnalytics.WirAgeing(wirs, today);
        Assert.Equal(new[] { 2, 2, 2, 2 }, b.Select(x => x.Count).ToArray());
    }

    [Fact]
    public void CashFlow_UsesIncrementsOfCumulativeStatements()
    {
        var inv = new[]
        {
            new Invoice { Subcontractor = "A", InvDate = new DateTime(2026, 6, 25), ClaimedAmount = 100, CertifiedAmount = 90, CertifiedAt = new DateTime(2026, 7, 10), Status = InvoiceStatus.Certified },
            new Invoice { Subcontractor = "A", InvDate = new DateTime(2026, 7, 25), ClaimedAmount = 250, CertifiedAmount = 200, CertifiedAt = new DateTime(2026, 8, 12), Status = InvoiceStatus.Certified },
        };
        var cf = ProjectAnalytics.CashFlow(inv);
        Assert.Equal(new[] { 100.0, 150.0, 0.0 }, cf.Select(c => c.Claimed).ToArray());
        Assert.Equal(new[] { 0.0, 90.0, 110.0 }, cf.Select(c => c.Certified).ToArray());
        Assert.Equal(200, cf.Last().CumulativeCertified);
    }
}

public class InvoiceCopyTests
{
    [Fact]
    public void DetectsLineForLineCopy()
    {
        var inv = new[]
        {
            new Invoice { Id = 1, Subcontractor = "BANDER SEIF", InvoiceNo = "INV-02", InvDate = new DateTime(2026, 7, 31) },
            new Invoice { Id = 2, Subcontractor = "BANDER SEIF", InvoiceNo = "INV-03", InvDate = new DateTime(2026, 9, 28) },
            new Invoice { Id = 3, Subcontractor = "MARUF", InvoiceNo = "INV-01", InvDate = new DateTime(2026, 7, 31) },
            new Invoice { Id = 4, Subcontractor = "MARUF", InvoiceNo = "INV-02", InvDate = new DateTime(2026, 8, 31) },
        };
        var lines = new List<InvoiceLine>();
        for (var i = 1; i <= 20; i++)
        {
            lines.Add(new InvoiceLine { InvoiceId = 1, LineId = i, CumQty = i * 2 });
            lines.Add(new InvoiceLine { InvoiceId = 2, LineId = i, CumQty = i * 2 });
            lines.Add(new InvoiceLine { InvoiceId = 3, LineId = i, CumQty = i });
            lines.Add(new InvoiceLine { InvoiceId = 4, LineId = i, CumQty = i + (i % 3 == 0 ? 1 : 0) }); // moved lines: genuine
        }
        var found = Assert.Single(InvoiceCopyDetector.Detect(inv, lines));
        Assert.Equal("INV-03", found.Invoice.InvoiceNo);
        Assert.Equal("INV-02", found.CopyOf.InvoiceNo);
        Assert.Equal(20, found.MatchingLines);
    }
}

public class ImportExportTests
{
    private static string Xlsx(string name, string[] headers, params object[][] rows)
    {
        var path = Path.Combine(TestData.TempDir(), name);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("DATA");
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = rows[r][c] switch { double d => d, int i => i, DateTime dt => dt, var o => o?.ToString() };
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void QsExport_BuildsPointsPerStage_TwinDataTwoAt2ndFix_TerraceExcluded()
    {
        var file = Xlsx("qs.xlsx", new[] { "BUILDING", "LEVEL", "ROOM", "ROOM TYPE", "BLOCK", "LAYER", "COUNT" },
            new object[] { "HOTEL", "L2", "201", "ER-01", "TWIN DATA", "DATA", 3 },
            new object[] { "HOTEL", "L2", "201", "ER-01", "TWIN SOCKET", "POWER", 4 },
            new object[] { "HOTEL", "L2", "201", "ER-01", "SWITCH 1G", "SWITCH", 5 },
            new object[] { "HOTEL", "L2", "201", "ER-01", "TERRACE LIGHT", "LIGHTING", 2 },
            new object[] { "HOTEL", "L2", "201", "ER-01", "CP-4", "CONTROL", 1 });
        var t = TableReader.Read(file);
        var p = new QsExportImporter().Parse(t, new ProjectSnapshot(), new RuleOptions());
        Assert.Contains(p.Issues, i => i.Message.Contains("Terrace"));
        Assert.Equal(12, p.ValidRows); // DATA, POWER, LIGHT, GRMS x 3 stages
        var db = TestData.NewDb();
        p.Commit(db);
        var lines = db.All<QtyLine>();
        Assert.Equal(6, lines.Single(l => l.System == Systems.Data && l.Stage == Stages.Second).QsQty);
        Assert.Equal(3, lines.Single(l => l.System == Systems.Data && l.Stage == Stages.First).QsQty);
        Assert.Equal(4, lines.Single(l => l.System == Systems.Power && l.Stage == Stages.Second).QsQty);
        Assert.Equal(5, lines.Single(l => l.System == Systems.Light && l.Stage == Stages.Final).QsQty);
        Assert.Equal(1, lines.Single(l => l.System == Systems.Grms && l.Stage == Stages.Final).QsQty);
        Assert.Single(db.All<ImportBatch>());
    }

    [Fact]
    public void Po_And_Dn_Import_ConvertPcs_FlagTotalsAndOverPo()
    {
        var db = TestData.NewDb();
        var po = Xlsx("po.xlsx", new[] { "PO NO", "SUPPLIER", "LINE", "ITEM", "DESCRIPTION", "UNIT", "QTY", "RATE", "STATED TOTAL" },
            new object[] { "PO-T-1", "SAUDI PVC", 1, "PVC-20", "PVC 20MM", "M", 600.0, 2.0, 1500.0 },
            new object[] { "PO-T-1", "SAUDI PVC", 2, "PVC-25", "PVC 25MM", "M", 120.0, 2.5, 1500.0 });
        var pp = new PoImporter().Parse(TableReader.Read(po), ProjectSnapshot.Load(db), new RuleOptions());
        Assert.DoesNotContain(pp.Issues, i => i.Message.Contains("stated")); // 1200 + 300 = 1500 matches
        pp.Commit(db);
        Assert.Equal(2, db.Count<PoLine>());

        var dn = Xlsx("dn.xlsx", new[] { "DN NO", "DATE", "PO NO", "LINE", "QTY", "UNIT" },
            new object[] { "DN-1", "2026-09-01", "PO-T-1", 1, 90.0, "PCS" },
            new object[] { "DN-1", "2026-09-01", "PO-T-1", 2, 30.0, "PCS" });
        var dp = new DnImporter().Parse(TableReader.Read(dn), ProjectSnapshot.Load(db), new RuleOptions { PipeLengthM = 6 });
        Assert.Contains(dp.Issues, i => i.Message.Contains("converted to 540"));
        Assert.Contains(dp.Issues, i => i.Message.Contains("OVER alarm")); // 30 PCS = 180 M > 120 M
        dp.Commit(db);
        var s = ProjectSnapshot.Load(db);
        var prog = MaterialAnalysis.ForPo(s, s.PurchaseOrders.Single());
        Assert.Equal(540, prog.Lines[0].Delivered);
        Assert.True(prog.Lines[1].IsOver);
        Assert.Equal(540 * 2.0 + 180 * 2.5, prog.DeliveredValue);
    }

    [Fact]
    public void PoImport_WarnsWhenTotalDoesNotAddUp()
    {
        var po = Xlsx("po2.xlsx", new[] { "PO NO", "LINE", "DESCRIPTION", "UNIT", "QTY", "RATE", "STATED TOTAL" },
            new object[] { "PO-T-2", 1, "SWITCH", "NO", 100.0, 22.0, 3450.0 });
        var p = new PoImporter().Parse(TableReader.Read(po), new ProjectSnapshot(), new RuleOptions());
        Assert.Contains(p.Issues, i => i.Message.Contains("diff -1,250.00") || i.Message.Contains("diff -1250"));
    }

    [Fact]
    public void Csv_IsReadWithQuotes()
    {
        var path = Path.Combine(TestData.TempDir(), "x.csv");
        File.WriteAllText(path, "WIR NO,DESCRIPTION,QTY\nWIR-1,\"L1, LIGHT\",5\n");
        var t = TableReader.Read(path);
        Assert.Equal("L1, LIGHT", t.Rows[0].Get("DESCRIPTION"));
        Assert.Equal(5, t.Rows[0].GetNumber("QTY"));
        Assert.IsType<WirImporter>(ImporterRegistry.Guess(t));
    }

    [Fact]
    public void Export_HeaderIsGreyA6A6A6_BoldBlack_Frozen()
    {
        var path = Path.Combine(TestData.TempDir(), "out.xlsx");
        ExcelExporter.Export(path, new ExportSheet
        {
            Name = "LINES",
            Columns = new() { new("ROOM"), new("QS", ColumnKind.Integer), new("WIR %", ColumnKind.Percent) },
            Rows = new() { new object?[] { "101", 12.0, 0.5 } },
            TotalRow = new object?[] { "TOTAL", 12.0, null },
        });
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("LINES");
        var h = ws.Cell(1, 1);
        Assert.Equal("ROOM", h.GetString());
        Assert.Equal("FFA6A6A6", h.Style.Fill.BackgroundColor.Color.ToArgb().ToString("X8"));
        Assert.True(h.Style.Font.Bold);
        Assert.Equal(XLColor.Black, h.Style.Font.FontColor);
        Assert.Equal(1, ws.SheetView.SplitRow);
        Assert.Equal("0.0%", ws.Cell(2, 3).Style.NumberFormat.Format);
        Assert.Equal("#,##0", ws.Cell(2, 2).Style.NumberFormat.Format);
    }
}

public class AnthropicClientTests
{
    [Fact]
    public void Body_UsesAdaptiveThinking_Effort_AndFallbacks()
    {
        var c = new AnthropicClient("k") { Model = "claude-opus-5-5", Effort = "medium" };
        var body = c.BuildBody("sys", new[] { new ChatMessage("user", "hi") }, stream: true);
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("medium", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public void Errors_AreReadable()
    {
        Assert.Contains("API key rejected", AnthropicClient.DescribeError(401, "{\"error\":{\"message\":\"invalid x-api-key\"}}"));
        Assert.Contains("Rate limited", AnthropicClient.DescribeError(429, "not json"));
    }

    [Fact]
    public async Task NoKey_Throws()
    {
        var c = new AnthropicClient("");
        await Assert.ThrowsAsync<AnthropicException>(() => c.CompleteAsync("s", new[] { new ChatMessage("user", "x") }));
    }
}
