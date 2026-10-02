using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Materials;
using Raffaello.Ocr;

namespace Raffaello.App.Services;

/// <summary>
/// The offline reading stack of the app: PaddleOCR (first engine), Windows OCR (second engine for escalation / voting), PDFium page
/// rendering at 300 dpi; Claude vision only when cloud reading is on and a key is set (Materials > SETTINGS). Nothing leaves the PC
/// otherwise.
/// </summary>
public sealed class SmartReading : IDisposable
{
    private readonly MaterialsService _materials;
    public PaddleOcrEngine Paddle { get; } = new();
    public WindowsOcrEngine Windows { get; }
    public PdfiumRasterizer Rasterizer { get; } = new();
    public IDocumentStore Documents { get; }

    public SmartReading(MaterialsService materials, WindowsOcrEngine windows, IDocumentStore documents)
    {
        _materials = materials;
        Windows = windows;
        Documents = documents;
        materials.LayoutEngines = Engines;
        materials.Rasterizer = Rasterizer;
        materials.Documents = documents;
    }

    public IReadOnlyList<ILayoutOcrEngine> Engines => new ILayoutOcrEngine[] { Paddle, Windows };

    public string Status =>
        $"Offline: {(Paddle.IsAvailable ? Paddle.Name : "PaddleOCR not available (" + Paddle.LoadError + ")")}; {Windows.Name}{(Windows.IsAvailable ? "" : " - not installed")}. " +
        $"Cloud: {_materials.Vision().Status}.";

    public SmartReaderOptions Options(IProgress<string>? progress = null) => new()
    {
        Rasterizer = Rasterizer, Engines = Engines, Vision = _materials.Vision(), Progress = progress,
    };

    public void Dispose() => Paddle.Dispose();
}
