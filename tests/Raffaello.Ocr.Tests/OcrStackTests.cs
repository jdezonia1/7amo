using OpenCvSharp;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Materials;
using Raffaello.Ocr;

namespace Raffaello.Ocr.Tests;

/// <summary>One engine for the whole class (model loading takes a second).</summary>
public sealed class EngineFixture : IDisposable
{
    public PaddleOcrEngine Engine { get; } = new();
    public void Dispose() => Engine.Dispose();
}

/// <summary>The offline OCR stack on synthetic pages: PaddleOCR reading, orientation, rulings, PDFium rendering, the full smart pipeline.</summary>
public class OcrStackTests : IClassFixture<EngineFixture>
{
    private readonly PaddleOcrEngine _ocr;
    public OcrStackTests(EngineFixture f) => _ocr = f.Engine;

    private static string Temp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "raffaello-ocr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return Path.Combine(d, name);
    }

    /// <summary>A white page with a ruled table drawn with OpenCV (Hershey font: Latin letters and digits).</summary>
    private static Mat TablePage()
    {
        var m = new Mat(1400, 1800, MatType.CV_8UC3, Scalar.White);
        var xs = new[] { 100, 250, 900, 1100, 1300, 1500, 1700 };
        var rows = new[]
        {
            new[] { "No", "Description", "Unit", "Qty", "Rate", "Total" },
            new[] { "1", "PVC 1st Fix outlet", "No", "4000", "55", "220000" },
            new[] { "2", "EMT 1st Fix outlet", "No", "400", "90", "36000" },
            new[] { "3", "Cable tray 50mm", "m", "2000", "35", "70000" },
        };
        var y = 200;
        foreach (var r in rows)
        {
            for (var c = 0; c < 6; c++) Cv2.PutText(m, r[c], new Point(xs[c] + 15, y + 55), HersheyFonts.HersheySimplex, 1.3, Scalar.Black, 3, LineTypes.AntiAlias);
            y += 90;
        }
        for (var i = 0; i <= rows.Length; i++) Cv2.Line(m, new Point(xs[0], 200 + i * 90), new Point(xs[^1], 200 + i * 90), Scalar.Black, 3);
        foreach (var x in xs) Cv2.Line(m, new Point(x, 200), new Point(x, 200 + rows.Length * 90), Scalar.Black, 3);
        Cv2.PutText(m, "Delivery Note 81064344", new Point(100, 120), HersheyFonts.HersheySimplex, 1.6, Scalar.Black, 3, LineTypes.AntiAlias);
        return m;
    }

    private static PageImage Png(Mat m) => new() { Bytes = m.ImEncode(".png"), MediaType = "image/png", Width = m.Width, Height = m.Height, Coverage = 1 };

    [Fact]
    public void Paddle_runtime_loads()
    {
        Assert.True(_ocr.IsAvailable, _ocr.LoadError);
    }

    [Fact]
    public async Task Reads_a_ruled_table_with_boxes_and_rulings()
    {
        using var m = TablePage();
        var page = await _ocr.RecognizeLayoutAsync(Png(m), new OcrHints { Script = Scripts.Latin });
        var text = LayoutBuilder.Text(page);
        Assert.Contains("81064344", text);
        Assert.Contains("220000", text);
        Assert.Contains("36000", text);
        Assert.True(page.MeanConfidence > 0.8, $"confidence {page.MeanConfidence}");
        Assert.True(page.Rulings.Count(r => r.Horizontal) >= 5, $"{page.Width}x{page.Height} {string.Join(",", page.Steps)} {string.Join(" ", page.Rulings.Select(r => $"{(r.Horizontal ? "H" : "V")}{r.Pos:0}:{r.Start:0}-{r.End:0}"))}");
        Assert.True(page.Rulings.Count(r => !r.Horizontal) >= 7, $"V {page.Rulings.Count(r => !r.Horizontal)} H {page.Rulings.Count(r => r.Horizontal)} {page.Width}x{page.Height} {string.Join(",", page.Steps)} {string.Join(" ", page.Rulings.Select(r => $"{(r.Horizontal ? "H" : "V")}{r.Pos:0}:{r.Start:0}-{r.End:0}"))}");
        var grid = TableBuilder.FromRulings(page).Single();
        Assert.Equal(6, grid.ColCount);
        Assert.Equal(4, grid.RowCount);
        var smart = new SmartPage { Number = 1, Ocr = page, Source = TextSource.Ocr, Text = text, ReadingText = text, Kind = new Classification(DocTypes.RateSchedule, 1, "") };
        var read = await ScheduleExtractor.ReadAsync(new[] { smart }, new EngineRereader(new[] { _ocr }));
        Assert.Equal(3, read.Items.Count);
        Assert.All(read.Items, i => Assert.True(i.ArithmeticOk, $"{i.ItemNo}: {i.Qty.Value} x {i.Rate.Value} = {i.Total.Value}"));
        Assert.Equal(new[] { "1", "2", "3" }, read.Items.Select(i => i.ItemNo));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Turned_pages_are_put_upright(int turn)
    {
        using var m = TablePage();
        using var rotated = ImagePrep.RotateQuarter(m, turn);
        var page = await _ocr.RecognizeLayoutAsync(Png(rotated), new OcrHints { Script = Scripts.Latin });
        Assert.Equal((360 - turn) % 360, page.Rotation);
        Assert.Contains("81064344", LayoutBuilder.Text(page));
    }

    [Fact]
    public async Task Skewed_photo_is_deskewed_and_read()
    {
        using var m = TablePage();
        using var skewed = ImagePrep.Rotate(m, 3.5);
        var page = await _ocr.RecognizeLayoutAsync(Png(skewed), new OcrHints { Script = Scripts.Latin, Photo = false });
        Assert.Contains(page.Steps, s => s.StartsWith("deskew"));
        Assert.Contains("220000", LayoutBuilder.Text(page));
    }

    [Fact]
    public void Photo_of_a_sheet_on_a_dark_desk_is_cropped_to_the_paper()
    {
        using var sheet = TablePage();
        using var photo = new Mat(2200, 2600, MatType.CV_8UC3, new Scalar(40, 40, 40));
        var src = new[] { new Point2f(0, 0), new Point2f(sheet.Width, 0), new Point2f(sheet.Width, sheet.Height), new Point2f(0, sheet.Height) };
        var dst = new[] { new Point2f(300, 250), new Point2f(2250, 330), new Point2f(2180, 1900), new Point2f(380, 1950) };
        using var H = Cv2.GetPerspectiveTransform(src, dst);
        Cv2.WarpPerspective(sheet, photo, H, photo.Size(), InterpolationFlags.Linear, BorderTypes.Transparent);
        Assert.True(ImagePrep.LooksLikePhoto(photo));
        using var crop = ImagePrep.PerspectiveCrop(photo);
        Assert.NotNull(crop);
        Assert.InRange(crop!.Width / (double)crop.Height, 1.1, 1.5);
    }

    [Fact]
    public async Task Pdf_is_rendered_by_pdfium_and_read_end_to_end()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var path = Temp("po.pdf");
        Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4);
            p.Margin(40);
            p.Content().Column(col =>
            {
                col.Item().Text("PURCHASE ORDER").FontSize(22).Bold();
                col.Item().Text("P.O Ref: RAF-P.O-E-045-2026").FontSize(14);
                col.Item().Text("Delivery Note 81064344").FontSize(14);
                col.Item().Text("Grand Total SAR 10,772,722.88").FontSize(14);
            });
        })).GeneratePdf(path);
        var raster = new PdfiumRasterizer();
        var img = await raster.RenderAsync(path, 1, 200);
        Assert.NotNull(img);
        Assert.InRange(img!.Width, 1600, 1700);   // A4 at 200 dpi = 1654 px
        var png = await raster.RenderPngAsync(path, 1);
        Assert.NotNull(png);

        var doc = await SmartReader.ReadAsync(path, new SmartReaderOptions { Rasterizer = raster, Engines = new ILayoutOcrEngine[] { _ocr }, Dpi = 200 });
        var p1 = Assert.Single(doc.Pages);
        Assert.Equal(TextSource.TextLayer, p1.Source);            // clean digital text layer is used as-is
        Assert.Equal(DocTypes.Po, p1.Kind.Type);
        Assert.Contains("81064344", p1.Text);

        // the same page as a scan (image only): rendered, OCR'd and classified
        var scan = Temp("scan.pdf");
        using (var m = ImagePrep.Decode(img.Bytes))
        {
            var bytes = m.ImEncode(".png");
            Document.Create(c => c.Page(p => { p.Size(PageSizes.A4); p.Margin(0); p.Content().Image(bytes).FitArea(); })).GeneratePdf(scan);
        }
        var sdoc = await SmartReader.ReadAsync(scan, new SmartReaderOptions { Rasterizer = raster, Engines = new ILayoutOcrEngine[] { _ocr }, Dpi = 200 });
        var s1 = Assert.Single(sdoc.Pages);
        Assert.Equal(TextSource.Ocr, s1.Source);
        Assert.Contains("81064344", s1.Text);
        Assert.Contains("RAF-P", s1.Text);
        Assert.Equal(DocTypes.Po, s1.Kind.Type);

        // the Materials readers use the same stack through ReaderOptions
        var dn = await DnReader.ReadAsync(scan, new ReaderOptions { LayoutEngines = new ILayoutOcrEngine[] { _ocr }, Rasterizer = raster });
        Assert.Equal("81064344", dn.Value.Header.DnNo);
    }

    [Fact]
    public async Task Crop_rereads_give_a_second_opinion()
    {
        using var m = TablePage();
        var img = Png(m);
        var page = await _ocr.RecognizeLayoutAsync(img, new OcrHints { Script = Scripts.Latin });
        var w = page.Words.First(x => x.Text.Contains("220000"));
        var processed = new PageImage { Bytes = page.ImagePng!, MediaType = "image/png", Coverage = 1 };
        var again = await _ocr.RecognizeCropAsync(processed, w.Box, new OcrHints { Script = Scripts.Latin });
        Assert.NotNull(again);
        Assert.Equal("220000", again!.Text.Trim());
    }
}
