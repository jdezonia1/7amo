using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Raffaello.Core.Documents;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Raffaello.App.Services;

/// <summary>
/// Local OCR with Windows.Media.Ocr (no cloud). Lines are rebuilt with column padding from the word positions so the same
/// parsers as the PDF text layer can read them. English is preferred (supplier documents); Arabic when installed.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;

    public WindowsOcrEngine()
    {
        try
        {
            var en = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            _engine = en != null ? OcrEngine.TryCreateFromLanguage(en) : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch { _engine = null; }
    }

    public string Name => _engine is null ? "Windows OCR (no language pack)" : $"Windows OCR ({_engine.RecognizerLanguage.LanguageTag})";
    public bool IsAvailable => _engine != null;

    public async Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default)
    {
        if (_engine is null) return "";
        using var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(image.Bytes.AsBuffer());
        ms.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ms);
        SoftwareBitmap bmp;
        var max = OcrEngine.MaxImageDimension;
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            var k = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            var t = new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * k), ScaledHeight = (uint)(decoder.PixelHeight * k), InterpolationMode = BitmapInterpolationMode.Fant };
            bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, t, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        else bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        ct.ThrowIfCancellationRequested();
        var result = await _engine.RecognizeAsync(bmp);
        var words = result.Lines.SelectMany(l => l.Words).ToList();
        if (words.Count == 0) return "";
        var cw = words.Where(w => w.Text.Length > 0).Select(w => w.BoundingRect.Width / w.Text.Length).OrderBy(x => x).ElementAt(words.Count / 2);
        cw = Math.Max(2, cw);
        var sb = new StringBuilder();
        foreach (var line in result.Lines.OrderBy(l => l.Words.Min(w => w.BoundingRect.Y)))
        {
            var row = new StringBuilder();
            foreach (var w in line.Words.OrderBy(w => w.BoundingRect.X))
            {
                var col = (int)Math.Round(w.BoundingRect.X / cw);
                col = Math.Max(col, row.Length + (row.Length == 0 ? 0 : 1));
                if (col > row.Length) row.Append(' ', col - row.Length);
                row.Append(w.Text);
            }
            sb.AppendLine(row.ToString());
        }
        return sb.ToString();
    }
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
