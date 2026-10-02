using System.Diagnostics;
using System.Security.Cryptography;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

/// <summary>Engines and limits for <see cref="SmartReader"/>. Everything is offline unless <see cref="Vision"/> is available (cloud reading on + key).</summary>
public sealed class SmartReaderOptions
{
    /// <summary>PDF page -> bitmap (PDFium / Windows.Data.Pdf). Without it the page's embedded scan image is used.</summary>
    public IPageRasterizer? Rasterizer { get; init; }
    /// <summary>OCR engines in order of preference (PaddleOCR first, Windows OCR second). The first available one reads every page; the others are escalation.</summary>
    public IReadOnlyList<ILayoutOcrEngine> Engines { get; init; } = Array.Empty<ILayoutOcrEngine>();
    /// <summary>Claude vision - only when the user enabled cloud reading and set a key.</summary>
    public IVisionReader? Vision { get; init; }
    public int Dpi { get; init; } = 300;
    /// <summary>A PDF text layer is used when its plausibility is at least this (scanner OCR layers need <see cref="ScannerLayerMinQuality"/>).</summary>
    public double TextLayerMinQuality { get; init; } = 0.75;
    public double ScannerLayerMinQuality { get; init; } = 0.85;
    /// <summary>Page mean OCR confidence below this runs the next engine as well (ensemble).</summary>
    public double EscalateBelowConfidence { get; init; } = 0.72;
    /// <summary>Pages to read (1-based); null = all.</summary>
    public Func<int, bool>? Pages { get; init; }
    /// <summary>Script of the document if known (AUTO / ARABIC / LATIN).</summary>
    public string Script { get; init; } = Scripts.Auto;
    public IProgress<string>? Progress { get; init; }
    public bool KeepImages { get; init; } = true;

    public ILayoutOcrEngine? Primary => Engines.FirstOrDefault(e => e.IsAvailable);
    public bool CanOcr => Primary != null;
    public bool CanUseVision => Vision?.IsAvailable == true;
}

/// <summary>One page after the smart pass: text (layout and reading order), where it came from, OCR boxes and the page type.</summary>
public sealed class SmartPage
{
    public int Number { get; init; }
    public DocPage Base { get; init; } = new();
    /// <summary>Layout text (columns as runs of spaces) - what the regex parsers read.</summary>
    public string Text { get; set; } = "";
    /// <summary>Reading-order text (Arabic right-to-left) - search index and clause text.</summary>
    public string ReadingText { get; set; } = "";
    /// <summary>TEXT / OCR / VISION / NONE.</summary>
    public string Source { get; set; } = TextSource.None;
    public string Engine { get; set; } = "";
    public OcrPage? Ocr { get; set; }
    /// <summary>Results of the other engines on this page (ensemble / escalation).</summary>
    public List<OcrPage> Alternatives { get; } = new();
    public TextQualityReport? LayerQuality => Base.LayerQuality;
    public TextQualityReport? Quality { get; set; }
    public Classification Kind { get; set; } = Classification.Unknown;
    public double Confidence { get; set; }
    public List<string> Notes { get; } = new();
    public bool IsPhoto { get; set; }
    /// <summary>Words of the source that was used (OCR words, or text-layer words in points).</summary>
    public IReadOnlyList<OcrWord> Words => Ocr?.Words ?? (Source == TextSource.TextLayer ? Base.Words : new List<OcrWord>());
}

public sealed class SmartDocument
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Bytes { get; init; }
    public List<SmartPage> Pages { get; } = new();
    public List<DocSegment> Segments { get; } = new();
    public TimeSpan Elapsed { get; set; }

    public IEnumerable<SmartPage> PagesOf(string type) => Pages.Where(p => p.Kind.Type == type);
    public IEnumerable<SmartPage> PagesIn(DocSegment s) => Pages.Where(p => p.Number >= s.FirstPage && p.Number <= s.LastPage);

    /// <summary>The same document as a <see cref="DocText"/> for the existing PO / DN / MIR parsers (layout text, sources, kinds).</summary>
    public DocText ToDocText()
    {
        var t = new DocText { FileName = FileName };
        foreach (var p in Pages)
        {
            var hasText = p.Text.Trim().Length > 0;
            t.Pages.Add(new DocPage
            {
                Number = p.Number, Text = p.Text, Source = p.Source,
                HasTextLayer = p.Base.HasTextLayer && p.Source == TextSource.TextLayer,
                WordCount = hasText ? Math.Max(p.Base.WordCount, p.Words.Count) : 0,
                DominantImage = p.Base.DominantImage, WidthPt = p.Base.WidthPt, HeightPt = p.Base.HeightPt, Words = p.Words.ToList(),
                LayerQuality = p.Base.LayerQuality,
            });
        }
        return t;
    }

    public string Summary => $"{FileName}: {Pages.Count} page(s), {string.Join("; ", Segments)} | " +
                             $"{Pages.Count(p => p.Source == TextSource.TextLayer)} text layer, {Pages.Count(p => p.Source == TextSource.Ocr)} OCR, " +
                             $"{Pages.Count(p => p.Base.LayerQuality?.IsGarbage == true)} garbage layer(s) discarded, {Elapsed.TotalSeconds:0}s";
}

/// <summary>
/// The offline-first reading pipeline for one PDF or image:
/// 1. PDF text layer (PdfPig) scored for plausibility - garbled scanner layers (reversed Arabic, "O28-2O26", "MPBCO") are discarded;
/// 2. otherwise the page is rendered at 300 dpi and read by the first OCR engine (PaddleOCR: orientation, deskew, perspective crop,
///    contrast, rulings), with the next engine run as well when the page confidence is low (results kept as alternatives for voting);
/// 3. the page is classified (contract, rate schedule, PO, DN, MIR form, certificate, drum label, statement, Aconex screenshot ...)
///    and consecutive pages are grouped into typed segments.
/// Field extraction, validation and voting happen in the extractors (<see cref="Extractors"/>).
/// </summary>
public static class SmartReader
{
    public static async Task<SmartDocument> ReadAsync(string path, SmartReaderOptions options, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var doc = new SmartDocument
        {
            Path = path, FileName = System.IO.Path.GetFileName(path), Bytes = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff")
        {
            var img = new PageImage { Bytes = bytes, MediaType = ext == ".png" ? "image/png" : "image/jpeg", Coverage = 1 };
            var page = new SmartPage { Number = 1, Base = new DocPage { Number = 1, DominantImage = img } };
            await OcrPageAsync(page, img, options, ct).ConfigureAwait(false);
            Classify(page);
            doc.Pages.Add(page);
        }
        else
        {
            var text = PdfTextReader.Read(bytes, doc.FileName);
            foreach (var bp in text.Pages)
            {
                ct.ThrowIfCancellationRequested();
                if (options.Pages != null && !options.Pages(bp.Number)) continue;
                options.Progress?.Report($"{doc.FileName}: page {bp.Number} of {text.Pages.Count}");
                var page = await ReadPageAsync(path, bp, options, ct).ConfigureAwait(false);
                doc.Pages.Add(page);
            }
        }
        doc.Segments.AddRange(DocClassifier.Segments(doc.Pages.Select(p => (p.Number, p.Kind)).ToList()));
        doc.Elapsed = sw.Elapsed;
        return doc;
    }

    public static async Task<SmartPage> ReadPageAsync(string pdfPath, DocPage bp, SmartReaderOptions options, CancellationToken ct = default)
    {
        var page = new SmartPage { Number = bp.Number, Base = bp };
        var useLayer = false;
        if (bp.HasTextLayer)
        {
            bp.LayerQuality = TextQuality.Score(bp.Text);
            var scannerLayer = bp.DominantImage is { Coverage: > 0.6 };
            var min = scannerLayer ? options.ScannerLayerMinQuality : options.TextLayerMinQuality;
            useLayer = bp.LayerQuality.Score >= min && bp.WordCount >= 8;
            if (!useLayer) page.Notes.Add($"text layer discarded: quality {bp.LayerQuality}{(scannerLayer ? " (scanner OCR layer)" : "")}");
        }
        if (useLayer)
        {
            page.Text = bp.Text;
            page.ReadingText = bp.Words.Count > 0 ? LayoutBuilder.ReadingText(bp.Words) : bp.Text;
            page.Source = TextSource.TextLayer;
            page.Engine = "PdfPig text layer";
            page.Quality = bp.LayerQuality;
            page.Confidence = Math.Min(1, 0.6 + 0.4 * bp.LayerQuality!.Score);
        }
        else if (options.CanOcr)
        {
            PageImage? img = null;
            if (options.Rasterizer != null)
            {
                try { img = await options.Rasterizer.RenderAsync(pdfPath, bp.Number, options.Dpi, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { page.Notes.Add("render failed: " + ex.Message); }
            }
            img ??= bp.DominantImage;
            if (img != null) await OcrPageAsync(page, img, options, ct, photoHint: bp.DominantImage is { MediaType: "image/jpeg", Coverage: > 0.3 } && LooksLikePhoto(bp.DominantImage)).ConfigureAwait(false);
            else page.Notes.Add("no image to OCR");
        }
        else
        {
            page.Notes.Add("no OCR engine available - page left unread");
        }
        Classify(page);
        return page;
    }

    /// <summary>Phone photos are JPEGs whose pixel aspect is not A4/Letter or that are small (camera frames placed on a page).</summary>
    private static bool LooksLikePhoto(PageImage im)
    {
        if (im.Width <= 0 || im.Height <= 0) return false;
        var r = Math.Max(im.Width, im.Height) / (double)Math.Min(im.Width, im.Height);
        return Math.Abs(r - 1.414) > 0.05 && Math.Abs(r - 1.294) > 0.05 || im.Width * im.Height < 2_000_000;
    }

    private static async Task OcrPageAsync(SmartPage page, PageImage img, SmartReaderOptions options, CancellationToken ct, bool? photoHint = null)
    {
        var engines = options.Engines.Where(e => e.IsAvailable).ToList();
        if (engines.Count == 0) { page.Notes.Add("no OCR engine available"); return; }
        var hints = new OcrHints { Script = options.Script, Dpi = options.Dpi, KeepImage = options.KeepImages, Photo = photoHint == true ? true : null };
        OcrPage? best = null;
        foreach (var e in engines)
        {
            ct.ThrowIfCancellationRequested();
            OcrPage r;
            try { r = await e.RecognizeLayoutAsync(img, hints, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { page.Notes.Add($"{e.Name} failed: {ex.Message}"); continue; }
            if (best is null) best = r;
            else
            {
                page.Alternatives.Add(r);
                if (r.MeanConfidence * TextQuality.Score(LayoutBuilder.Text(r)).Score > best.MeanConfidence * TextQuality.Score(LayoutBuilder.Text(best)).Score + 0.05)
                {
                    page.Alternatives.Remove(r);
                    page.Alternatives.Add(best);
                    best = r;
                }
            }
            if (best.MeanConfidence >= options.EscalateBelowConfidence) break;
            page.Notes.Add($"{e.Name}: mean confidence {r.MeanConfidence:0.00} - escalating");
        }
        if (best is null) return;
        page.Ocr = best;
        page.Text = LayoutBuilder.Text(best);
        page.ReadingText = LayoutBuilder.ReadingText(best.Words);
        page.Source = TextSource.Ocr;
        page.Engine = best.Engine;
        page.Quality = TextQuality.Score(page.ReadingText);
        page.Confidence = best.MeanConfidence;
        page.IsPhoto = best.Steps.Any(s => s.Contains("perspective") || s.Contains("CLAHE"));
        if (best.Steps.Count > 0) page.Notes.Add("pre-processing: " + string.Join(", ", best.Steps));
    }

    public static void Classify(SmartPage page)
    {
        var hasGrid = page.Ocr != null && TableBuilder.FromRulings(page.Ocr).Any(g => g.ColCount >= 4);
        var w = page.Ocr?.Width ?? page.Base.WidthPt;
        var h = page.Ocr?.Height ?? page.Base.HeightPt;
        page.Kind = DocClassifier.Classify(page.ReadingText + "\n" + page.Text, new PageFeatures
        {
            IsPhoto = page.IsPhoto, IsScan = page.Source != TextSource.TextLayer, Words = page.Words.Count, HasGrid = hasGrid, AspectWH = h > 0 ? w / h : 0,
        });
        page.Base.Kind = page.Kind.Type;
    }
}
