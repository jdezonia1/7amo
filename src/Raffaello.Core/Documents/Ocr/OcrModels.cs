namespace Raffaello.Core.Documents.Ocr;

/// <summary>Axis-aligned box in page-image pixels (origin top-left).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Cx => X + W / 2;
    public double Cy => Y + H / 2;
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
    public static Box Union(IEnumerable<Box> boxes)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var b in boxes) { x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y); x1 = Math.Max(x1, b.Right); y1 = Math.Max(y1, b.Bottom); }
        return x0 == double.MaxValue ? default : new Box(x0, y0, x1 - x0, y1 - y0);
    }
    /// <summary>Box as fractions of the page (0..1), so it survives a different render resolution.</summary>
    public Box Relative(double pageW, double pageH) => pageW <= 0 || pageH <= 0 ? this : new Box(X / pageW, Y / pageH, W / pageW, H / pageH);
    public Box Absolute(double pageW, double pageH) => new(X * pageW, Y * pageH, W * pageW, H * pageH);
    public double VerticalOverlap(Box o) => Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));
    public double HorizontalOverlap(Box o) => Math.Max(0, Math.Min(Right, o.Right) - Math.Max(X, o.X));
}

/// <summary>
/// One recognised text segment (a word or a phrase - PaddleOCR returns phrases, Windows OCR words). Text is in LOGICAL order
/// (Arabic already re-ordered from the visual order the recognisers return) with digits as printed.
/// </summary>
public sealed class OcrWord
{
    public string Text { get; set; } = "";
    public Box Box { get; set; }
    /// <summary>0..1 recogniser confidence (0 when the engine gives none).</summary>
    public double Confidence { get; set; }
    public string Engine { get; set; } = "";
    public bool IsArabic => ArabicText.HasArabic(Text);
    public override string ToString() => $"{Text} ({Confidence:0.00})";
}

/// <summary>A ruling line of a table (horizontal or vertical), page-image pixels.</summary>
public sealed record Ruling(bool Horizontal, double X1, double Y1, double X2, double Y2)
{
    public double Length => Horizontal ? Math.Abs(X2 - X1) : Math.Abs(Y2 - Y1);
    public double Pos => Horizontal ? (Y1 + Y2) / 2 : (X1 + X2) / 2;
    public double Start => Horizontal ? Math.Min(X1, X2) : Math.Min(Y1, Y2);
    public double End => Horizontal ? Math.Max(X1, X2) : Math.Max(Y1, Y2);
}

/// <summary>Result of reading one page image: words with boxes, table rulings, the orientation that was applied and the image the boxes refer to.</summary>
public sealed class OcrPage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double Dpi { get; set; }
    public List<OcrWord> Words { get; set; } = new();
    public List<Ruling> Rulings { get; set; } = new();
    /// <summary>Clockwise rotation applied to the input (0 / 90 / 180 / 270) before recognition.</summary>
    public int Rotation { get; set; }
    public double SkewDegrees { get; set; }
    public string Engine { get; set; } = "";
    /// <summary>Pre-processing steps that were applied ("rotate 90", "deskew 0.8", "perspective crop", "binarise" ...).</summary>
    public List<string> Steps { get; set; } = new();
    /// <summary>The processed page image (PNG) the word boxes refer to - shown in the review window.</summary>
    public byte[]? ImagePng { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double MeanConfidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);
}

/// <summary>What the caller knows about the page, so the engine can pick models and pre-processing.</summary>
public sealed class OcrHints
{
    /// <summary>AUTO / ARABIC / LATIN.</summary>
    public string Script { get; init; } = Scripts.Auto;
    /// <summary>A phone photo (perspective crop, contrast) rather than a flat scan.</summary>
    public bool? Photo { get; init; }
    public bool AutoRotate { get; init; } = true;
    public bool DetectRulings { get; init; } = true;
    /// <summary>Keep the processed image in <see cref="OcrPage.ImagePng"/> (review screen).</summary>
    public bool KeepImage { get; init; } = true;
    public double Dpi { get; init; } = 300;
    public static OcrHints Default => new();
}

public static class Scripts
{
    public const string Auto = "AUTO", Arabic = "ARABIC", Latin = "LATIN";
}

/// <summary>
/// An OCR engine that returns word boxes (layout), not only text: PaddleOCR (offline, Raffaello.Ocr), Windows OCR (app).
/// <see cref="IOcrEngine.RecognizeAsync"/> stays for older callers and returns the layout text of the same result.
/// </summary>
public interface ILayoutOcrEngine : IOcrEngine
{
    Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default);
    /// <summary>Second opinion on one crop (used by the ensemble to re-read a low-confidence cell). Null when not supported.</summary>
    Task<OcrWord?> RecognizeCropAsync(PageImage image, Box box, OcrHints hints, CancellationToken ct = default) => Task.FromResult<OcrWord?>(null);
}

/// <summary>PDF page to a bitmap at a resolution (300 dpi for OCR). PDFium in Raffaello.Ocr (Windows + Linux), Windows.Data.Pdf in the app.</summary>
public interface IPageRasterizer
{
    string Name { get; }
    Task<PageImage?> RenderAsync(string pdfPath, int pageNumber, int dpi, CancellationToken ct = default);
}
