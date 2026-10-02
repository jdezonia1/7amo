namespace Raffaello.Core.Documents.Ocr;

/// <summary>
/// The document reader's layout OCR engines as one engine for other modules (Cables > READ SCANNED SLD): the first available engine in
/// order (PaddleOCR offline, then Windows OCR). When the first one fails on a page the next one is tried.
/// </summary>
public sealed class FirstAvailableLayoutOcr : ILayoutOcrEngine
{
    private readonly Func<IReadOnlyList<ILayoutOcrEngine>> _engines;

    public FirstAvailableLayoutOcr(Func<IReadOnlyList<ILayoutOcrEngine>> engines) => _engines = engines;
    public FirstAvailableLayoutOcr(params ILayoutOcrEngine[] engines) : this(() => engines) { }

    private IEnumerable<ILayoutOcrEngine> Available => _engines().Where(e => e.IsAvailable);

    public string Name => Available.FirstOrDefault()?.Name ?? "no OCR engine";
    public bool IsAvailable => Available.Any();

    public async Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var e in Available)
        {
            try { return await e.RecognizeLayoutAsync(image, hints, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        throw new InvalidOperationException("No layout OCR engine could read the page" + (last is null ? " (none installed)." : ": " + last.Message), last);
    }

    public async Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) =>
        LayoutBuilder.Text(await RecognizeLayoutAsync(image, OcrHints.Default, ct).ConfigureAwait(false));

    public Task<OcrWord?> RecognizeCropAsync(PageImage image, Box box, OcrHints hints, CancellationToken ct = default) =>
        Available.FirstOrDefault()?.RecognizeCropAsync(image, box, hints, ct) ?? Task.FromResult<OcrWord?>(null);
}
