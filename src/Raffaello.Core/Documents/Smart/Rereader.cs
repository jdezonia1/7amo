using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

/// <summary>Second opinions on one region of a page (escalation): every available engine re-reads the crop with the Latin and the Arabic model.</summary>
public interface IFieldRereader
{
    Task<List<FieldCandidate>> RereadAsync(SmartPage page, Box box, bool numeric, CancellationToken ct = default);
}

public sealed class NullRereader : IFieldRereader
{
    public static readonly NullRereader Instance = new();
    public Task<List<FieldCandidate>> RereadAsync(SmartPage page, Box box, bool numeric, CancellationToken ct = default) => Task.FromResult(new List<FieldCandidate>());
}

/// <summary>Re-reads crops of the processed page image (the image the boxes refer to) with each engine that supports crops.</summary>
public sealed class EngineRereader : IFieldRereader
{
    private readonly IReadOnlyList<ILayoutOcrEngine> _engines;
    public EngineRereader(IReadOnlyList<ILayoutOcrEngine> engines) => _engines = engines;
    public int Calls { get; private set; }

    public async Task<List<FieldCandidate>> RereadAsync(SmartPage page, Box box, bool numeric, CancellationToken ct = default)
    {
        var res = new List<FieldCandidate>();
        if (page.Ocr?.ImagePng is not { } png) return res;
        var img = new PageImage { Bytes = png, MediaType = "image/png", Width = page.Ocr.Width, Height = page.Ocr.Height, Coverage = 1 };
        foreach (var e in _engines.Where(e => e.IsAvailable))
            foreach (var script in numeric ? new[] { Scripts.Latin, Scripts.Arabic } : new[] { Scripts.Arabic, Scripts.Latin })
            {
                ct.ThrowIfCancellationRequested();
                Calls++;
                OcrWord? w;
                try { w = await e.RecognizeCropAsync(img, box, new OcrHints { Script = script, KeepImage = false }, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { w = null; }
                if (w != null && w.Text.Trim().Length > 0) res.Add(new FieldCandidate(w.Text.Trim(), w.Confidence, w.Engine, box, page.Number));
            }
        return res;
    }
}
