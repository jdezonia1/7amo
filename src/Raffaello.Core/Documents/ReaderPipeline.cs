using System.Text.Json.Nodes;

namespace Raffaello.Core.Documents;

/// <summary>Local OCR (Windows.Media.Ocr in the app). Core never depends on Windows; the CLI and tests run without OCR.</summary>
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    /// <summary>Recognises one page image; returns layout-ish text (one line per OCR line).</summary>
    Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default);
}

public sealed class NullOcrEngine : IOcrEngine
{
    public static readonly NullOcrEngine Instance = new();
    public string Name => "none";
    public bool IsAvailable => false;
    public Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) => Task.FromResult("");
}

/// <summary>What to send to the cloud reader: one page as an image or as a single-page PDF, plus the fields wanted.</summary>
public sealed class VisionRequest
{
    public string DocumentKind { get; init; } = "";
    public string Instructions { get; init; } = "";
    /// <summary>JSON schema of the answer (structured output).</summary>
    public JsonObject Schema { get; init; } = new();
    public byte[] Content { get; init; } = Array.Empty<byte>();
    /// <summary>image/jpeg, image/png or application/pdf.</summary>
    public string MediaType { get; init; } = "image/jpeg";
    public int Page { get; init; }
}

/// <summary>Cloud page reader (Claude vision). Only available when the user enabled cloud reading and set an API key.</summary>
public interface IVisionReader
{
    bool IsAvailable { get; }
    /// <summary>Why the reader is not available ("cloud reading is off", "no API key").</summary>
    string Status { get; }
    Task<JsonNode?> ExtractAsync(VisionRequest request, CancellationToken ct = default);
}

public sealed class UnavailableVisionReader : IVisionReader
{
    public UnavailableVisionReader(string why) => Status = why;
    public bool IsAvailable => false;
    public string Status { get; }
    public Task<JsonNode?> ExtractAsync(VisionRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Cloud reading is not available: " + Status);
}

public sealed class ReaderOptions
{
    public IOcrEngine Ocr { get; init; } = NullOcrEngine.Instance;
    public IVisionReader? Vision { get; init; }
    /// <summary>Metres per piece used when a DN in PCS is matched to a PO line in M.</summary>
    public double PipeLengthM { get; init; } = 6;
    public static ReaderOptions Default => new();
    public bool CanUseVision => Vision?.IsAvailable == true;
}

/// <summary>Text layer first, then local OCR for scan pages (never the scanner's own OCR layer when it is garbled).</summary>
public static class ReaderPipeline
{
    public static async Task<DocText> ReadPdfAsync(string path, ReaderOptions options, CancellationToken ct = default)
    {
        var text = PdfTextReader.Read(path);
        await OcrScansAsync(text, options, ct).ConfigureAwait(false);
        return text;
    }

    public static async Task OcrScansAsync(DocText text, ReaderOptions options, CancellationToken ct = default)
    {
        if (!options.Ocr.IsAvailable) return;
        foreach (var p in text.Pages.Where(p => p.IsScan && p.DominantImage != null))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var t = await options.Ocr.RecognizeAsync(p.DominantImage!, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(t)) { p.Text = t; p.Source = TextSource.Ocr; }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* OCR failure leaves the page empty; the parser flags it */ }
        }
    }

    /// <summary>Content for the vision reader: the page's photo/scan when it fills the page, else the page as a one-page PDF.</summary>
    public static (byte[] Bytes, string MediaType) PageContent(string pdfPath, DocPage page) =>
        page.DominantImage is { Coverage: > 0.6 } im ? (im.Bytes, im.MediaType) : (PdfTextReader.ExtractPage(pdfPath, page.Number), "application/pdf");
}

/// <summary>Renders a PDF page to PNG for the review screen (Windows.Data.Pdf in the app; null where unavailable).</summary>
public interface IPageRenderer
{
    Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, CancellationToken ct = default);
}

public sealed class NullPageRenderer : IPageRenderer
{
    public static readonly NullPageRenderer Instance = new();
    public Task<byte[]?> RenderPngAsync(string pdfPath, int pageNumber, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
}
