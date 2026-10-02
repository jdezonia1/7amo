using Docnet.Core;
using Docnet.Core.Models;
using OpenCvSharp;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Ocr;

/// <summary>
/// PDF page to PNG with PDFium (Docnet.Core; native PDFium for win-x64 and linux-x64 ships in the package). Honours page rotation and
/// transforms, composites the transparent background onto white. Also serves the review screen (<see cref="IPageRenderer"/>).
/// </summary>
public sealed class PdfiumRasterizer : IPageRasterizer, IPageRenderer
{
    private static readonly object Gate = new();   // PDFium is not thread-safe

    public string Name => "PDFium";

    public Task<PageImage?> RenderAsync(string pdfPath, int pageNumber, int dpi, CancellationToken ct = default) =>
        Task.Run(() => Render(pdfPath, pageNumber, dpi), ct);

    public async Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, CancellationToken ct = default)
    {
        if (!pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return File.Exists(pdfPath) ? await File.ReadAllBytesAsync(pdfPath, ct).ConfigureAwait(false) : null;
        return (await RenderAsync(pdfPath, pageNumber, 150, ct).ConfigureAwait(false))?.Bytes;
    }

    public PageImage? Render(string pdfPath, int pageNumber, int dpi)
    {
        byte[] raw; int w, h;
        lock (Gate)
        {
            using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(dpi / 72.0));
            if (pageNumber < 1 || pageNumber > doc.GetPageCount()) return null;
            using var page = doc.GetPageReader(pageNumber - 1);
            w = page.GetPageWidth(); h = page.GetPageHeight();
            raw = page.GetImage(RenderFlags.RenderAnnotations);
        }
        if (w <= 0 || h <= 0 || raw.Length < w * h * 4) return null;
        using var bgra = Mat.FromPixelData(h, w, MatType.CV_8UC4, raw);
        using var bgr = new Mat(h, w, MatType.CV_8UC3, Scalar.White);
        // alpha-composite on white (PDFium leaves the page background transparent)
        var chans = bgra.Split();
        try
        {
            using var alpha = new Mat();
            chans[3].ConvertTo(alpha, MatType.CV_32F, 1 / 255.0);
            var outs = new Mat[3];
            for (var i = 0; i < 3; i++)
            {
                using var c = new Mat();
                chans[i].ConvertTo(c, MatType.CV_32F);
                using var inv = new Mat();
                Cv2.Subtract(Scalar.All(1.0), alpha, inv);
                using var a = new Mat(); Cv2.Multiply(c, alpha, a);
                using var b = new Mat(); Cv2.Multiply(inv, Scalar.All(255.0), b);
                using var sum = new Mat(); Cv2.Add(a, b, sum);
                outs[i] = new Mat();
                sum.ConvertTo(outs[i], MatType.CV_8U);
            }
            Cv2.Merge(outs, bgr);
            foreach (var o in outs) o.Dispose();
        }
        finally { foreach (var c in chans) c.Dispose(); }
        return new PageImage { Bytes = bgr.ImEncode(".png"), MediaType = "image/png", Width = w, Height = h, Coverage = 1 };
    }

    public static int PageCount(string pdfPath)
    {
        lock (Gate)
        {
            using var doc = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1));
            return doc.GetPageCount();
        }
    }
}
