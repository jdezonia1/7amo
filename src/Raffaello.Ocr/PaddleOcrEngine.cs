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
            if (Math.Abs(skew) is > 0.5 and < 12)
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
        var crops = pick.Select(b => Crop(img, b)).ToArray();
        try
        {
            var res = rec.Run(crops, 0);
            // long confident strings win; tall boxes on a sideways page read as garbage with low scores
            return res.Average(r => Score(r.Score) * Math.Min(1, r.Text.Trim().Length / 4.0));
        }
        finally { foreach (var c in crops) c.Dispose(); }
    }

    /// <summary>
    /// Crop of one text box, upright: a tight perspective warp with the corners ordered by position (PaddleOcrAll.GetRotateCropImage orders by the rectangle's own corner order and can return the
    /// crop upside down, which would fool the orientation test).
    /// </summary>
    internal static Mat Crop(Mat img, RotatedRect b)
    {
        var pts = b.Points();
        var tl = pts.OrderBy(p => p.X + p.Y).First();
        var br = pts.OrderByDescending(p => p.X + p.Y).First();
        var tr = pts.OrderBy(p => p.Y - p.X).First();
        var bl = pts.OrderByDescending(p => p.Y - p.X).First();
        var W = (int)Math.Max(4, Math.Round(Math.Max(Dist(tl, tr), Dist(bl, br))));
        var H = (int)Math.Max(4, Math.Round(Math.Max(Dist(tl, bl), Dist(tr, br))));
        var dst = new[] { new Point2f(0, 0), new Point2f(W - 1, 0), new Point2f(W - 1, H - 1), new Point2f(0, H - 1) };
        using var M = Cv2.GetPerspectiveTransform(new[] { tl, tr, br, bl }, dst);
        var outp = new Mat();
        Cv2.WarpPerspective(img, outp, M, new Size(W, H), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return outp;
    }

    private static double Dist(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

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

    /// <summary>Fill gaps the Arabic model leaves where Latin words / numbers sit inside an Arabic line ("PVC 1st Fix", "4.5").</summary>
    public bool MixedScriptMerge { get; init; } = true;

    private List<OcrWord> Recognize(Mat img, RotatedRect[] boxes, PaddleOcrRecognizer rec)
    {
        var words = new List<OcrWord>(boxes.Length);
        if (boxes.Length == 0) return words;
        var crops = boxes.Select(b => Crop(img, b)).ToArray();
        try
        {
            // one crop at a time: batching pads crops to the widest one and costs accuracy on long Arabic lines
            var res = rec.Run(crops, 0);
            var texts = res.Select(r => r.Text).ToArray();
            var merged = new bool[res.Length];
            if (MixedScriptMerge && rec == _recAr)
            {
                var lines = Enumerable.Range(0, res.Length).Where(i => ArabicText.HasArabic(texts[i]) && crops[i].Width > crops[i].Height * 4).ToList();
                var filled = FillLatin(lines.Select(i => (crops[i], texts[i])).ToList());
                for (var k = 0; k < lines.Count; k++)
                    if (filled[k] != null) { texts[lines[k]] = filled[k]!; merged[lines[k]] = true; }
            }
            for (var i = 0; i < boxes.Length; i++)
            {
                var text = texts[i].Trim();
                if (text.Length == 0) continue;
                var r = boxes[i].BoundingRect();
                words.Add(new OcrWord
                {
                    Text = ArabicText.VisualToLogical(text),
                    Box = new Box(Math.Max(0, r.X), Math.Max(0, r.Y), r.Width, r.Height),
                    Confidence = Score(res[i].Score),
                    Engine = rec == _recEn ? "paddle-en" : merged[i] ? "paddle-ar+en" : "paddle-ar",
                });
            }
        }
        finally { foreach (var c in crops) c.Dispose(); }
        return words;
    }

    /// <summary>Recognises crops in batches of similar width (padding to the widest crop of a batch is what costs time).</summary>
    private static PaddleOcrRecognizerResult[] RunBatched(PaddleOcrRecognizer rec, IReadOnlyList<Mat> mats, int batch = 16)
    {
        var res = new PaddleOcrRecognizerResult[mats.Count];
        var order = Enumerable.Range(0, mats.Count).OrderBy(i => mats[i].Width / (double)Math.Max(1, mats[i].Height)).ToList();
        for (var k = 0; k < order.Count; k += batch)
        {
            var idx = order.Skip(k).Take(batch).ToList();
            var r = rec.Run(idx.Select(i => mats[i]).ToArray(), idx.Count);
            for (var j = 0; j < idx.Count; j++) res[idx[j]] = r[j];
        }
        return res;
    }

    private static bool IsLatinish(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or ',' or '/' or '-' or '*' or '&' or '%' or '+' or '(' or ')' or '\'' or '×' or '²' or ' ' or '_';

    /// <summary>Word segments of a line crop: runs of ink separated by gaps of at least a sixth of the line height.</summary>
    internal static List<(int X0, int X1)> Segments(Mat crop)
    {
        using var gray = ImagePrep.ToGray(crop);
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        var w = bin.Width; var h = bin.Height;
        // ignore the top / bottom rows (neighbouring lines touching the crop)
        var y0 = h / 8; var y1 = h - h / 8;
        using var band = new Mat(bin, new Rect(0, y0, w, Math.Max(1, y1 - y0)));
        using var colSum = new Mat();
        Cv2.Reduce(band, colSum, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
        colSum.GetArray(out int[] ink);
        var minGap = Math.Max(3, h / 6);
        var res = new List<(int, int)>();
        int start = -1, gap = 0;
        for (var x = 0; x < w; x++)
        {
            if (ink[x] > 255) { if (start < 0) start = x; gap = 0; }
            else if (start >= 0 && ++gap >= minGap) { res.Add((start, x - gap)); start = -1; gap = 0; }
        }
        if (start >= 0) res.Add((start, w - 1));
        return res.Where(r => r.Item2 - r.Item1 >= 3).ToList();
    }

    /// <summary>
    /// The Arabic model drops Latin words and numbers inside Arabic lines ("PVC 1st Fix", "4.5"). Every word segment of those lines is read
    /// by the English model; segments it reads as confident Latin text, and that the Arabic model does not read better as Arabic, are put
    /// back into the line at their horizontal position (visual order, at the nearest word boundary). Returns null per line when nothing changed.
    /// </summary>
    internal List<string?> FillLatin(IReadOnlyList<(Mat Crop, string ArVisual)> lines)
    {
        var result = new List<string?>(new string?[lines.Count]);
        if (lines.Count == 0) return result;
        EnsureLoaded(needEnglish: true);
        var segMats = new List<Mat>();
        var owner = new List<(int Line, int X0, int X1)>();
        try
        {
            for (var li = 0; li < lines.Count; li++)
            {
                var crop = lines[li].Crop;
                foreach (var sg in Segments(crop))
                {
                    var x0 = Math.Max(0, sg.X0 - 2); var x1 = Math.Min(crop.Width, sg.X1 + 3);
                    if (x1 - x0 < 6) continue;
                    segMats.Add(new Mat(crop, new Rect(x0, 0, x1 - x0, crop.Height)).Clone());
                    owner.Add((li, sg.X0, sg.X1));
                }
            }
            if (segMats.Count == 0) return result;
            var en = RunBatched(_recEn!, segMats);
            var cand = new List<int>();
            for (var i = 0; i < en.Length; i++)
            {
                var t = en[i].Text.Trim();
                if (t.Length == 0 || !t.All(IsLatinish) || Score(en[i].Score) < 0.85) continue;
                if (t.Count(char.IsAsciiDigit) == 0 && t.Count(char.IsAsciiLetter) < 2) continue;
                cand.Add(i);
            }
            if (cand.Count == 0) return result;
            var ar = RunBatched(_recAr!, cand.Select(i => segMats[i]).ToList());
            var latin = new List<(int Line, double X, int X0, int X1, string Text)>();
            for (var k = 0; k < cand.Count; k++)
            {
                var i = cand[k];
                // the Arabic model reads Arabic here (a lone waw is often read "9" by the English model)
                if (ArabicText.ArabicLetters(ar[k].Text) >= 1 && Score(ar[k].Score) >= Score(en[i].Score) - 0.15) continue;
                if (en[i].Text.Trim().Length == 1 && ArabicText.HasArabic(ar[k].Text)) continue;
                latin.Add((owner[i].Line, (owner[i].X0 + owner[i].X1) / 2.0, owner[i].X0, owner[i].X1, en[i].Text.Trim()));
            }
            foreach (var g in latin.GroupBy(l => l.Line))
            {
                var crop = lines[g.Key].Crop;
                var text = lines[g.Key].ArVisual.Trim();
                // adjacent Latin segments form one run ("Linear" "lighting" "fixtures")
                var runs = new List<(double X0, double X1, string Text)>();
                foreach (var l in g.OrderBy(l => l.X0))
                {
                    if (runs.Count > 0 && l.X0 - runs[^1].X1 < crop.Height * 0.9) runs[^1] = (runs[^1].X0, l.X1, runs[^1].Text + " " + l.Text);
                    else runs.Add((l.X0, l.X1, l.Text));
                }
                var plainUpper = text.ToUpperInvariant();
                var inserts = new List<(int Index, string Text)>();
                foreach (var r in runs)
                {
                    if (plainUpper.Contains(r.Text.ToUpperInvariant())) continue;
                    // already read by the Arabic model (digits / Latin present at that place)?
                    var cx = (r.X0 + r.X1) / 2 / crop.Width * text.Length;
                    var lo = Math.Clamp((int)(r.X0 / crop.Width * text.Length) - 1, 0, text.Length); var hi = Math.Clamp((int)Math.Ceiling(r.X1 / crop.Width * text.Length) + 1, 0, text.Length);
                    if (text[lo..hi].Count(char.IsAsciiLetterOrDigit) >= r.Text.Count(char.IsAsciiLetterOrDigit) * 0.6) continue;
                    // nearest word boundary to the run's position
                    var best = -1; double bd = double.MaxValue;
                    for (var k = 0; k <= text.Length; k++)
                        if (k == 0 || k == text.Length || text[k - 1] == ' ' || text[k] == ' ')
                            if (Math.Abs(k - cx) < bd) { bd = Math.Abs(k - cx); best = k; }
                    if (best >= 0) inserts.Add((best, r.Text));
                }
                if (inserts.Count == 0) continue;
                var sb = new System.Text.StringBuilder(text);
                foreach (var (i, t) in inserts.OrderByDescending(x => x.Index)) sb.Insert(i, " " + t + " ");
                result[g.Key] = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            }
            return result;
        }
        finally { foreach (var m in segMats) m.Dispose(); }
    }

    public void Dispose()
    {
        _det?.Dispose();
        _recAr?.Dispose();
        _recEn?.Dispose();
        _gate.Dispose();
    }
}
