using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Cli;

/// <summary>
/// Benchmark helper: caches an engine's page and crop results on disk (keyed by the image bytes, hints and engine name) so the
/// extractors can be re-run on the same OCR output without re-reading every page. The cache lives in the bench output folder.
/// </summary>
public sealed class CachedOcrEngine : ILayoutOcrEngine
{
    private readonly ILayoutOcrEngine _inner;
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public CachedOcrEngine(ILayoutOcrEngine inner, string dir)
    {
        _inner = inner;
        _dir = dir;
        Directory.CreateDirectory(Path.Combine(dir, "crops"));
    }

    public string Name => _inner.Name;
    public bool IsAvailable => _inner.IsAvailable;
    public Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) => _inner.RecognizeAsync(image, ct);

    private string Key(byte[] bytes, string extra) =>
        Convert.ToHexString(SHA256.HashData(bytes.Concat(Encoding.UTF8.GetBytes("|" + _inner.Name + "|v3|" + extra)).ToArray()))[..32];

    public async Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default)
    {
        var key = Key(image.Bytes, $"{hints.Script}|{hints.Photo}|{hints.AutoRotate}");
        var json = Path.Combine(_dir, key + ".json");
        var png = Path.Combine(_dir, key + ".png");
        if (File.Exists(json))
        {
            var cached = JsonSerializer.Deserialize<OcrPage>(await File.ReadAllTextAsync(json, ct).ConfigureAwait(false), Json)!;
            if (File.Exists(png)) cached.ImagePng = await File.ReadAllBytesAsync(png, ct).ConfigureAwait(false);
            Hits++;
            return cached;
        }
        Misses++;
        var page = await _inner.RecognizeLayoutAsync(image, hints, ct).ConfigureAwait(false);
        var img = page.ImagePng;
        page.ImagePng = null;
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(page, Json), ct).ConfigureAwait(false);
        if (img != null) await File.WriteAllBytesAsync(png, img, ct).ConfigureAwait(false);
        page.ImagePng = img;
        return page;
    }

    public async Task<OcrWord?> RecognizeCropAsync(PageImage image, Box box, OcrHints hints, CancellationToken ct = default)
    {
        var key = Key(image.Bytes, $"{hints.Script}|{box.X:0}|{box.Y:0}|{box.W:0}|{box.H:0}");
        var json = Path.Combine(_dir, "crops", key + ".json");
        if (File.Exists(json))
        {
            Hits++;
            var t = await File.ReadAllTextAsync(json, ct).ConfigureAwait(false);
            return t == "null" ? null : JsonSerializer.Deserialize<OcrWord>(t, Json);
        }
        Misses++;
        var w = await _inner.RecognizeCropAsync(image, box, hints, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(json, w is null ? "null" : JsonSerializer.Serialize(w, Json), ct).ConfigureAwait(false);
        return w;
    }
}
