using OpenCvSharp;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Ocr;

/// <summary>
/// Page-image pre-processing with OpenCV: photo detection and largest-quadrilateral perspective crop, contrast (CLAHE) and light
/// denoise for photos, deskew, rotation by quarter turns, adaptive binarisation and table ruling detection.
/// </summary>
public static class ImagePrep
{
    public static Mat Decode(byte[] bytes)
    {
        var m = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (m.Empty()) throw new InvalidDataException("The page image could not be decoded.");
        return m;
    }

    public static byte[] EncodePng(Mat m) => m.ImEncode(".png");

    /// <summary>
    /// A phone photo rather than a scan: the border is dark / not paper-white, or colour saturation is high around the page.
    /// </summary>
    public static bool LooksLikePhoto(Mat bgr)
    {
        using var gray = ToGray(bgr);
        var w = gray.Width; var h = gray.Height;
        var bw = Math.Max(4, w / 25); var bh = Math.Max(4, h / 25);
        var strips = new[] { new Rect(0, 0, w, bh), new Rect(0, h - bh, w, bh), new Rect(0, 0, bw, h), new Rect(w - bw, 0, bw, h) };
        var dark = 0;
        foreach (var s in strips)
        {
            using var roi = new Mat(gray, s);
            Cv2.MeanStdDev(roi, out var mean, out var sd);
            if (mean.Val0 < 170 || sd.Val0 > 45) dark++;
        }
        return dark >= 2;
    }

    public static Mat ToGray(Mat src)
    {
        var g = new Mat();
        if (src.Channels() == 1) src.CopyTo(g);
        else if (src.Channels() == 4) Cv2.CvtColor(src, g, ColorConversionCodes.BGRA2GRAY);
        else Cv2.CvtColor(src, g, ColorConversionCodes.BGR2GRAY);
        return g;
    }

    public static Mat ToBgr(Mat src)
    {
        var c = new Mat();
        if (src.Channels() == 3) src.CopyTo(c);
        else if (src.Channels() == 4) Cv2.CvtColor(src, c, ColorConversionCodes.BGRA2BGR);
        else Cv2.CvtColor(src, c, ColorConversionCodes.GRAY2BGR);
        return c;
    }

    /// <summary>
    /// Finds the sheet of paper in a photo (largest convex quadrilateral covering at least a quarter of the frame) and warps it to a
    /// flat rectangle. Returns null when no such quadrilateral is found (the photo is used as it is).
    /// </summary>
    public static Mat? PerspectiveCrop(Mat bgr)
    {
        var scale = 1000.0 / Math.Max(bgr.Width, bgr.Height);
        using var small = new Mat();
        Cv2.Resize(bgr, small, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        using var gray = ToGray(small);
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
        // paper is the bright region: Otsu threshold, close gaps, take the largest contour
        using var bin = new Mat();
        Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9));
        Cv2.MorphologyEx(bin, bin, MorphTypes.Close, k);
        Cv2.FindContours(bin, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var area = small.Width * (double)small.Height;
        Point[]? quad = null;
        foreach (var c in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(5))
        {
            var a = Cv2.ContourArea(c);
            if (a < area * 0.25) break;
            var hull = Cv2.ConvexHull(c);
            var peri = Cv2.ArcLength(hull, true);
            for (var eps = 0.02; eps <= 0.08 && quad is null; eps += 0.01)
            {
                var ap = Cv2.ApproxPolyDP(hull, eps * peri, true);
                if (ap.Length == 4 && Cv2.IsContourConvex(ap)) quad = ap;
            }
            if (quad is null)
            {
                // fall back to the rotated bounding box of the paper
                var rr = Cv2.MinAreaRect(hull);
                if (rr.Size.Width * rr.Size.Height > area * 0.25) quad = rr.Points().Select(p => new Point(p.X, p.Y)).ToArray();
            }
            if (quad != null) break;
        }
        if (quad is null) return null;
        var pts = Order(quad.Select(p => new Point2f((float)(p.X / scale), (float)(p.Y / scale))).ToArray());
        var wTop = Dist(pts[0], pts[1]); var wBot = Dist(pts[3], pts[2]);
        var hL = Dist(pts[0], pts[3]); var hR = Dist(pts[1], pts[2]);
        var W = (int)Math.Max(wTop, wBot); var H = (int)Math.Max(hL, hR);
        if (W < 200 || H < 200) return null;
        // nothing to gain when the quadrilateral is the whole frame
        if (W > bgr.Width * 0.97 && H > bgr.Height * 0.97) return null;
        var dst = new[] { new Point2f(0, 0), new Point2f(W - 1, 0), new Point2f(W - 1, H - 1), new Point2f(0, H - 1) };
        using var M = Cv2.GetPerspectiveTransform(pts, dst);
        var outp = new Mat();
        Cv2.WarpPerspective(bgr, outp, M, new Size(W, H), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return outp;
    }

    private static double Dist(Point2f a, Point2f b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Top-left, top-right, bottom-right, bottom-left.</summary>
    private static Point2f[] Order(Point2f[] p)
    {
        var tl = p.OrderBy(q => q.X + q.Y).First();
        var br = p.OrderByDescending(q => q.X + q.Y).First();
        var tr = p.OrderBy(q => q.Y - q.X).First();
        var bl = p.OrderByDescending(q => q.Y - q.X).First();
        return new[] { tl, tr, br, bl };
    }

    /// <summary>Contrast for photos and faded scans: CLAHE on the luminance, light median denoise.</summary>
    public static Mat Enhance(Mat bgr, bool photo)
    {
        using var gray = ToGray(bgr);
        Cv2.MeanStdDev(gray, out _, out var sd);
        if (!photo && sd.Val0 > 55) return ToBgr(gray);
        using var clahe = Cv2.CreateCLAHE(photo ? 2.0 : 1.5, new Size(8, 8));
        var outp = new Mat();
        clahe.Apply(gray, outp);
        if (photo) Cv2.MedianBlur(outp, outp, 3);
        var bgrOut = ToBgr(outp);
        outp.Dispose();
        return bgrOut;
    }

    /// <summary>Adaptive (local) binarisation - black text on white.</summary>
    public static Mat Binarize(Mat src)
    {
        using var gray = ToGray(src);
        var bin = new Mat();
        var block = Math.Max(15, (Math.Min(gray.Width, gray.Height) / 60) | 1);
        Cv2.AdaptiveThreshold(gray, bin, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, block, 15);
        return bin;
    }

    public static Mat RotateQuarter(Mat src, int clockwise)
    {
        var dst = new Mat();
        switch (((clockwise % 360) + 360) % 360)
        {
            case 90: Cv2.Rotate(src, dst, RotateFlags.Rotate90Clockwise); break;
            case 180: Cv2.Rotate(src, dst, RotateFlags.Rotate180); break;
            case 270: Cv2.Rotate(src, dst, RotateFlags.Rotate90Counterclockwise); break;
            default: src.CopyTo(dst); break;
        }
        return dst;
    }

    /// <summary>Rotates by a small angle (degrees, counter-clockwise positive) around the centre on a white background.</summary>
    public static Mat Rotate(Mat src, double degrees)
    {
        var center = new Point2f(src.Width / 2f, src.Height / 2f);
        using var M = Cv2.GetRotationMatrix2D(center, degrees, 1.0);
        var dst = new Mat();
        Cv2.WarpAffine(src, dst, M, src.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.White);
        return dst;
    }

    public static Mat Upscale(Mat src, double factor)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(0, 0), factor, factor, InterpolationFlags.Cubic);
        return dst;
    }

    /// <summary>
    /// Horizontal and vertical ruling lines (table grids) from the binarised page: morphological opening with long thin kernels,
    /// then the bounding boxes of the remaining strokes.
    /// </summary>
    public static List<Ruling> Rulings(Mat src)
    {
        using var bin = Binarize(src);
        using var inv = new Mat();
        Cv2.BitwiseNot(bin, inv);
        var res = new List<Ruling>();
        var w = inv.Width; var h = inv.Height;
        using (var hk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(20, w / 30), 1)))
        using (var hm = new Mat())
        {
            Cv2.MorphologyEx(inv, hm, MorphTypes.Open, hk);
            Cv2.Dilate(hm, hm, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 3)));
            Cv2.FindContours(hm, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var c in cs)
            {
                var r = Cv2.BoundingRect(c);
                if (r.Width >= w * 0.08 && r.Height <= Math.Max(12, h / 150) + r.Width * 0.012) res.Add(new Ruling(true, r.X, r.Y + r.Height / 2.0, r.Right, r.Y + r.Height / 2.0));
            }
        }
        using (var vk = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, Math.Max(20, h / 60))))
        using (var vm = new Mat())
        {
            Cv2.MorphologyEx(inv, vm, MorphTypes.Open, vk);
            Cv2.Dilate(vm, vm, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 9)));
            Cv2.FindContours(vm, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var c in cs)
            {
                var r = Cv2.BoundingRect(c);
                if (r.Height >= h * 0.035 && r.Width <= Math.Max(12, w / 150) + r.Height * 0.012) res.Add(new Ruling(false, r.X + r.Width / 2.0, r.Y, r.X + r.Width / 2.0, r.Bottom));
            }
        }
        return res;
    }
}
