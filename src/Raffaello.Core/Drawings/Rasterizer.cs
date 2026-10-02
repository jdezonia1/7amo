using Docnet.Core;
using Docnet.Core.Models;
using Raffaello.Core.Documents;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Renders drawing pages to RGB rasters at a chosen resolution.</summary>
public interface IDrawingRasterizer
{
    string Name { get; }
    int PageCount(string path);
    /// <summary>Renders a 1-based page. The returned DPI can be lower than asked when the page would be too large.</summary>
    (ColorImage Image, int Dpi) Render(string path, int page, int dpi);
}

/// <summary>
/// [drawings] PDFium through Docnet.Core (managed wrapper, native pdfium for win-x64 / linux-x64 / osx in the package; no SkiaSharp,
/// so it does not clash with the charts). Thread-safe through a lock (PDFium is single-threaded).
/// </summary>
public sealed class PdfiumRasterizer : IDrawingRasterizer
{
    private static readonly object Gate = new();
    /// <summary>Largest raster (pixels) - an A0 at 300 dpi would need 140 MP; the DPI is lowered to stay under this.</summary>
    public long MaxPixels { get; set; } = 80_000_000;
    public string Name => "PDFium";

    public int PageCount(string path)
    {
        lock (Gate)
        {
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
            return doc.GetPageCount();
        }
    }

    public (ColorImage Image, int Dpi) Render(string path, int page, int dpi)
    {
        lock (Gate)
        {
            int w0, h0;
            using (var probe = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0)))
            {
                if (page < 1 || page > probe.GetPageCount()) throw new ArgumentOutOfRangeException(nameof(page), $"{Path.GetFileName(path)} has {probe.GetPageCount()} pages.");
                using var pr = probe.GetPageReader(page - 1);
                w0 = pr.GetPageWidth(); h0 = pr.GetPageHeight();
            }
            var k = dpi / 72.0;
            if ((double)w0 * k * h0 * k > MaxPixels) { k = Math.Sqrt(MaxPixels / ((double)w0 * h0)); dpi = (int)Math.Floor(k * 72); k = dpi / 72.0; }
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(k));
            using var reader = doc.GetPageReader(page - 1);
            int w = reader.GetPageWidth(), h = reader.GetPageHeight();
            var bgra = reader.GetImage();
            return (ColorImage.FromBgra(bgra, w, h), dpi);
        }
    }
}

/// <summary>
/// [drawings] Adapter over the app's <see cref="IPageRenderer"/> (Windows.Data.Pdf) - used when PDFium is not available. The DPI
/// follows from the rendered width (the Windows renderer caps the width), so hits stay in the same sheet units.
/// </summary>
public sealed class PageRendererRasterizer : IDrawingRasterizer
{
    private readonly IPageRenderer _r;
    public PageRendererRasterizer(IPageRenderer r) => _r = r;
    public string Name => "page renderer";
    public int PageCount(string path) { using var d = UglyToad.PdfPig.PdfDocument.Open(path); return d.NumberOfPages; }

    public (ColorImage Image, int Dpi) Render(string path, int page, int dpi)
    {
        var png = _r.RenderPngAsync(path, page).GetAwaiter().GetResult() ?? throw new InvalidOperationException($"Page {page} could not be rendered.");
        var img = ColorImage.FromPng(png);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var wPt = pdf.GetPage(page).Width;
        return (img, (int)Math.Round(img.Width / (wPt / 72.0)));
    }
}

/// <summary>[drawings] Loads a sheet image: a PDF page through the rasteriser, or a PNG file.</summary>
public static class SheetImages
{
    public static (ColorImage Image, int Dpi) Load(string path, int page, int dpi, IDrawingRasterizer? rasterizer = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return (rasterizer ?? new PdfiumRasterizer()).Render(path, page, dpi);
        if (ext == ".png") return (ColorImage.FromPng(File.ReadAllBytes(path)), dpi);
        throw new NotSupportedException($"{Path.GetFileName(path)}: use a PDF or PNG for image takeoff (DWG / DXF / IFC / Revit JSON are read as models).");
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f));
    }
}
