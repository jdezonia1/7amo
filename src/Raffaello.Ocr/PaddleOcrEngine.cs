using System.Diagnostics;
using OpenCvSharp;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Ocr;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Local;

namespace Raffaello.Ocr;

/// <summary>
/// Offline OCR with PaddleOCR PP-OCRv5 (text detection + Arabic or English recognition; the models ship inside the
/// Sdcb.PaddleOCR.Models.Local NuGet packages, nothing is downloaded and nothing leaves the PC). Around the recogniser:
/// photo detection and perspective crop, contrast, quarter-turn orientation chosen by recognition confidence, deskew from the
/// text-line angles, upscaling of small text, ruling-line detection for tables, and a binarised second pass for faded scans.
/// Arabic comes back from the recogniser in visual order and is turned into logical order here.
/// </summary>
public sealed class PaddleOcrEngine : ILayoutOcrEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaddleOcrDetector? _det;
    private PaddleOcrRecognizer? _recAr;
    private PaddleOcrRecognizer? _recEn;
    private string? _loadError;

    /// <summary>Threads for the CPU (MKL-DNN) predictor; 0 = all cores.</summary>
    public int Threads { get; init; }
    /// <summary>Longest side the text detector works on.</summary>
    public int DetectorMaxSize { get; init; } = 2048;

    public string Name => "PaddleOCR PP-OCRv5 (offline)";

    public bool IsAvailable
    {
        get
        {
            try { EnsureLoaded(needEnglish: false); return true; }
            catch (Exception ex) { _loadError = ex.Message; return false; }
        }
    }

    /// <summary>Why the engine is not available (missing native runtime), else empty.</summary>
    public string LoadError => _loadError ?? "";

    private Action<PaddleConfig> Device => Threads > 0 ? PaddleDevice.Mkldnn(cpuMathThreadCount: Threads) : PaddleDevice.Mkldnn();

    private void EnsureLoaded(bool needEnglish)
    {
        if (_det is null)
        {
            _det = new PaddleOcrDetector(LocalDetectionModel.ChineseV5, Device) { MaxSize = DetectorMaxSize };
            _recAr = new PaddleOcrRecognizer(LocalRecognizationModel.ArabicV5, Device);
        }
        if (needEnglish && _recEn is null) _recEn = new PaddleOcrRecognizer(LocalRecognizationModel.EnglishV5, Device);
    }

    public async Task<string> RecognizeAsync(PageImage image, CancellationToken ct = default) =>
        LayoutBuilder.Text(await RecognizeLayoutAsync(image, OcrHints.Default, ct).ConfigureAwait(false));

    public async Task<OcrPage> RecognizeLayoutAsync(PageImage image, OcrHints hints, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Run(image, hints, ct), ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<OcrWord?> RecognizeCropAsync(PageImage image, Box box, OcrHints hints, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                EnsureLoaded(needEnglish: true);
                using var src = ImagePrep.Decode(image.Bytes);
                var r = new Rect((int)Math.Max(0, box.X - 2), (int)Math.Max(0, box.Y - 2), 0, 0);
                r.Width = (int)Math.Min(src.Width - r.X, box.W + 4);
                r.Height = (int)Math.Min(src.Height - r.Y, box.H + 4);
                if (r.Width < 4 || r.Height < 4) return null;
                using var crop = new Mat(src, r);
                var rec = hints.Script == Scripts.Latin ? _recEn! : _recAr!;
                var res = rec.Run(crop);
                return new OcrWord { Text = ArabicText.VisualToLogical(res.Text.Trim()), Box = box, Confidence = Score(res.Score), Engine = Name + (rec == _recEn ? " en" : " ar") };
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static double Score(float s) => float.IsNaN(s) ? 0 : Math.Clamp(s, 0, 1);

    private OcrPage Run(PageImage image, OcrHints hints, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        EnsureLoaded(needEnglish: hints.Script == Scripts.Latin);
        var page = new OcrPage { Engine = Name, Dpi = hints.Dpi };
        var mats = new List<Mat>();
        Mat Track(Mat m) { mats.Add(m); return m; }
        try
        {
            var img = Track(ImagePrep.Decode(image.Bytes));
            var photo = hints.Photo ?? ImagePrep.LooksLikePhoto(img);
            if (photo)
            {
                var crop = ImagePrep.PerspectiveCrop(img);
                if (crop != null) { img = Track(crop); page.Steps.Add("perspective crop"); }
            }
            img = Track(ImagePrep.Enhance(img, photo));
            if (photo) page.Steps.Add("contrast (CLAHE) + denoise");
            ct.ThrowIfCancellationRequested();

            var rec = hints.Script == Scripts.Latin ? _recEn! : _recAr!;
            var boxes = _det!.Run(img);

            // quarter turns: mostly tall text boxes = page on its side
            if (hints.AutoRotate && boxes.Length >= 3)
            {
                var tall = boxes.Count(b => { var r = b.BoundingRect(); return r.Height > r.Width * 1.3; });
                var turns = new List<int>();
                if (tall > boxes.Length * 0.5) turns.AddRange(new[] { 90, 270 });
                else turns.AddRange(new[] { 0, 180 });
                var best = (Rot: turns[0], Score: -1.0);
                foreach (var t in turns)
                {
                    using var probe = ImagePrep.RotateQuarter(img, t);
                    var s = ProbeScore(probe, t == turns[0] && t == 0 ? boxes : null, rec);
                    if (s > best.Score + 0.02) best = (t, s);
                }
                if (best.Rot != 0)
                {
                    img = Track(ImagePrep.RotateQuarter(img, best.Rot));
                    page.Rotation = best.Rot;
                    page.Steps.Add($"rotate {best.Rot}");
                    boxes = _det.Run(img);
                }
            }
            ct.ThrowIfCancellationRequested();

            // deskew from the angle of long text lines
            var skew = Skew(boxes);
            if (Math.Abs(skew) is > 0.25 and < 12)
            {
                img = Track(ImagePrep.Rotate(img, skew));
                page.SkewDegrees = skew;
                page.Steps.Add($"deskew {skew:0.0}°");
                boxes = _det.Run(img);
            }

            // small text: upscale so the recogniser sees ~32 px high lines
            var hs = boxes.Select(b => (double)Math.Min(b.Size.Width, b.Size.Height)).OrderBy(x => x).ToList();
            var medianH = hs.Count == 0 ? 0 : hs[hs.Count / 2];
            if (medianH is > 0 and < 18 && Math.Max(img.Width, img.Height) < 3600)
            {
                var f = Math.Min(2.0, 26 / medianH);
                img = Track(ImagePrep.Upscale(img, f));
                page.Steps.Add($"upscale x{f:0.0}");
                boxes = _det.Run(img);
            }
            ct.ThrowIfCancellationRequested();

            var words = Recognize(img, boxes, rec);
            // faded / noisy scan: a binarised pass, keep it when it reads better
            if (!photo && words.Count > 0 && words.Average(w => w.Confidence) < 0.6)
            {
                using var bin = ImagePrep.Binarize(img);
                using var bin3 = ImagePrep.ToBgr(bin);
                var boxes2 = _det.Run(bin3);
                var words2 = Recognize(bin3, boxes2, rec);
                if (words2.Count > 0 && words2.Average(w => w.Confidence) > words.Average(w => w.Confidence) + 0.05)
                {
                    words = words2;
                    page.Steps.Add("binarised pass");
                }
            }
            page.Words = words;
            page.Width = img.Width;
            page.Height = img.Height;
            if (hints.DetectRulings) page.Rulings = ImagePrep.Rulings(img);
            if (hints.KeepImage) page.ImagePng = ImagePrep.EncodePng(img);
            page.Elapsed = sw.Elapsed;
            return page;
        }
        finally
        {
            foreach (var m in mats) m.Dispose();
        }
    }

    /// <summary>Mean recognition score of the widest boxes in an orientation (how "readable" the page is that way up).</summary>
    private double ProbeScore(Mat img, RotatedRect[]? known, PaddleOcrRecognizer rec)
    {
        var boxes = known ?? _det!.Run(img);
        var pick = boxes.OrderByDescending(b => Math.Max(b.Size.Width, b.Size.Height)).Take(12).ToList();
        if (pick.Count == 0) return 0;
        var crops = pick.Select(b => PaddleOcrAll.GetRotateCropImage(img, b)).ToArray();
        try
        {
            var res = rec.Run(crops, 0);
            // long confident strings win; tall boxes on a sideways page read as garbage with low scores
            return res.Average(r => Score(r.Score) * Math.Min(1, r.Text.Trim().Length / 4.0));
        }
        finally { foreach (var c in crops) c.Dispose(); }
    }

    private static double Skew(RotatedRect[] boxes)
    {
        var angles = new List<double>();
        foreach (var b in boxes)
        {
            double w = b.Size.Width, h = b.Size.Height, a = b.Angle;
            if (w < h) { (w, h) = (h, w); a -= 90; }
            while (a > 45) a -= 90;
            while (a < -45) a += 90;
            if (w > h * 4 && w > 80) angles.Add(a);
        }
        if (angles.Count < 3) return 0;
        angles.Sort();
        // OpenCV angles grow clockwise in image coordinates; Rotate() takes counter-clockwise degrees
        return angles[angles.Count / 2];
    }

    private List<OcrWord> Recognize(Mat img, RotatedRect[] boxes, PaddleOcrRecognizer rec)
    {
        var words = new List<OcrWord>(boxes.Length);
        if (boxes.Length == 0) return words;
        var crops = boxes.Select(b => PaddleOcrAll.GetRotateCropImage(img, b)).ToArray();
        try
        {
            var res = rec.Run(crops, 0);
            for (var i = 0; i < boxes.Length; i++)
            {
                var text = res[i].Text.Trim();
                if (text.Length == 0) continue;
                var r = boxes[i].BoundingRect();
                words.Add(new OcrWord
                {
                    Text = ArabicText.VisualToLogical(text),
                    Box = new Box(Math.Max(0, r.X), Math.Max(0, r.Y), r.Width, r.Height),
                    Confidence = Score(res[i].Score),
                    Engine = rec == _recEn ? "paddle-en" : "paddle-ar",
                });
            }
        }
        finally { foreach (var c in crops) c.Dispose(); }
        return words;
    }

    public void Dispose()
    {
        _det?.Dispose();
        _recAr?.Dispose();
        _recEn?.Dispose();
        _gate.Dispose();
    }
}
