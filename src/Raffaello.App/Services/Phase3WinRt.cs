using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using OcrWord = Raffaello.Core.Documents.Ocr.OcrWord;
using OcrPage = Raffaello.Core.Documents.Ocr.OcrPage;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Raffaello.App.Services;

/// <summary>
/// Local OCR with Windows.Media.Ocr (no cloud) - the second offline engine after PaddleOCR (escalation / voting). Returns word boxes;
/// English is preferred, the Arabic recogniser is used for Arabic pages when the Arabic language pack is installed.
/// </summary>
public sealed class WindowsOcrEngine : ILayoutOcrEngine
{
    private readonly OcrEngine? _engine;
    private readonly OcrEngine? _arabic;

    public WindowsOcrEngine()
    {
        try
        {
            var en = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            _engine = en != null ? OcrEngine.TryCreateFromLanguage(en) : OcrEngine.TryCreateFromUserProfileLanguages();
            var ar = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("ar", StringComparison.OrdinalIgnoreCase));
            _arabic = ar != null ? OcrEngine.TryCreateFromLanguage(ar) : null;
        }
        catch { _engine = null; }
    }

    public string Name => _engine is null ? "Windows OCR (no language pack)" : $"Windows OCR ({_engine.RecognizerLanguage.LanguageTag}{(_arabic != null ? "+ar" : "")})";
    public bool IsAvailable => _engine != null;

    private static async Task<SoftwareBitmap> Decode(byte[] bytes, CancellationToken ct, Box? crop = null)
    {
        using var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(bytes.AsBuffer());
        ms.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ms);
        var max = OcrEngine.MaxImageDimension;
        var t = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (crop is { } c)
            t.Bounds = new BitmapBounds { X = (uint)Math.Max(0, c.X), Y = (uint)Math.Max(0, c.Y), Width = (uint)Math.Max(1, Math.Min(c.W, decoder.PixelWidth - c.X)), Height = (uint)Math.Max(1, Math.Min(c.H, decoder.PixelHeight - c.Y)) };
        else if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            var k = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            t.ScaledWidth = (uint)(decoder.PixelWidth * k); t.ScaledHeight = (uint)(decoder.PixelHeight * k);
        }
        ct.ThrowIfCancellationRequested();
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    public async Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default)
    {
        var page = new OcrPage { Engine = Name, Dpi = hints.Dpi, ImagePng = hints.KeepImage ? image.Bytes : null };
        if (_engine is null) return page;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var bmp = await Decode(image.Bytes, ct);
        var engine = hints.Script == Scripts.Arabic && _arabic != null ? _arabic : _engine;
        var result = await engine.RecognizeAsync(bmp);
        var scale = image.Width > 0 ? image.Width / (double)bmp.PixelWidth : 1;
        page.Width = image.Width > 0 ? image.Width : bmp.PixelWidth;
        page.Height = image.Height > 0 ? image.Height : bmp.PixelHeight;
        foreach (var line in result.Lines)
            foreach (var w in line.Words)
                page.Words.Add(new OcrWord
                {
                    Text = ArabicText.VisualToLogical(w.Text), Confidence = 0.85, Engine = "windows-ocr",
                    Box = new Box(w.BoundingRect.X * scale, w.BoundingRect.Y * scale, w.BoundingRect.Width * scale, w.BoundingRect.Height * scale),
                });
        page.Elapsed = sw.Elapsed;
        return page;
    }

    public async Task<OcrWord?> RecognizeCropAsync(PageImage image, Box box, OcrHints hints, CancellationToken ct = default)
    {
        if (_engine is null) return null;
        using var bmp = await Decode(image.Bytes, ct, box);
        var engine = hints.Script == Scripts.Arabic && _arabic != null ? _arabic : _engine;
        var r = await engine.RecognizeAsync(bmp);
        var text = string.Join(" ", r.Lines.Select(l => l.Text)).Trim();
        return text.Length == 0 ? null : new OcrWord { Text = ArabicText.VisualToLogical(text), Box = box, Confidence = 0.85, Engine = "windows-ocr" };
    }

    public async Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) =>
        LayoutBuilder.Text(await RecognizeLayoutAsync(image, new OcrHints { KeepImage = false }, ct));
}

/// <summary>PDF page to PNG with Windows.Data.Pdf, for the review screen.</summary>
public sealed class WindowsPdfRenderer : IPageRenderer
{
    public async Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, CancellationToken ct = default)
    {
        try
        {
            if (!pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return File.Exists(pdfPath) ? await File.ReadAllBytesAsync(pdfPath, ct) : null;
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            if (pageNumber < 1 || pageNumber > doc.PageCount) return null;
            using var page = doc.GetPage((uint)(pageNumber - 1));
            using var stream = new InMemoryRandomAccessStream();
            var width = (uint)Math.Min(2000, Math.Max(800, page.Size.Width * 1.6));
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = width });
            var bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
