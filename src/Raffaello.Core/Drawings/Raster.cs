using Raffaello.Core.Imaging;

namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] 8-bit greyscale raster (0 = black ink, 255 = white paper), row-major. Pure managed: runs on the server, the CLI and
/// Linux CI without a native imaging library.
/// </summary>
public sealed class GrayImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public GrayImage(int w, int h, byte[]? data = null, byte fill = 255)
    {
        if (w <= 0 || h <= 0) throw new ArgumentException($"Bad image size {w}x{h}.");
        Width = w; Height = h;
        Data = data ?? Filled(w * h, fill);
    }

    private static byte[] Filled(int n, byte v) { var a = new byte[n]; if (v != 0) Array.Fill(a, v); return a; }

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public byte At(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? (byte)255 : Data[y * Width + x];

    public GrayImage Clone() => new(Width, Height, (byte[])Data.Clone());

    public GrayImage Crop(int x, int y, int w, int h)
    {
        var o = new GrayImage(w, h);
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
                o.Data[j * w + i] = At(x + i, y + j);
        return o;
    }

    /// <summary>Area-average downscale by an integer factor.</summary>
    public GrayImage Downscale(int f)
    {
        if (f <= 1) return Clone();
        int w = Math.Max(1, Width / f), h = Math.Max(1, Height / f);
        var o = new GrayImage(w, h);
        Parallel.For(0, h, j =>
        {
            for (var i = 0; i < w; i++)
            {
                var s = 0;
                for (var dy = 0; dy < f; dy++)
                {
                    var row = (j * f + dy) * Width + i * f;
                    for (var dx = 0; dx < f; dx++) s += Data[row + dx];
                }
                o.Data[j * w + i] = (byte)(s / (f * f));
            }
        });
        return o;
    }

    /// <summary>Bilinear resize to an exact size.</summary>
    public GrayImage Resize(int w, int h)
    {
        var o = new GrayImage(Math.Max(1, w), Math.Max(1, h));
        double sx = (double)Width / o.Width, sy = (double)Height / o.Height;
        if (sx >= 2 || sy >= 2)
        {
            // area average for large reductions
            Parallel.For(0, o.Height, j =>
            {
                int y0 = (int)(j * sy), y1 = Math.Max(y0 + 1, (int)((j + 1) * sy));
                for (var i = 0; i < o.Width; i++)
                {
                    int x0 = (int)(i * sx), x1 = Math.Max(x0 + 1, (int)((i + 1) * sx));
                    long s = 0; var n = 0;
                    for (var y = y0; y < y1 && y < Height; y++)
                        for (var x = x0; x < x1 && x < Width; x++) { s += Data[y * Width + x]; n++; }
                    o.Data[j * o.Width + i] = n == 0 ? (byte)255 : (byte)(s / n);
                }
            });
            return o;
        }
        Parallel.For(0, o.Height, j =>
        {
            for (var i = 0; i < o.Width; i++)
                o.Data[j * o.Width + i] = (byte)Math.Clamp(Math.Round(Sample((i + 0.5) * sx - 0.5, (j + 0.5) * sy - 0.5)), 0, 255);
        });
        return o;
    }

    /// <summary>Bilinear sample; outside the image is white paper.</summary>
    public double Sample(double x, double y)
    {
        var x0 = (int)Math.Floor(x); var y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double a = At(x0, y0), b = At(x0 + 1, y0), c = At(x0, y0 + 1), d = At(x0 + 1, y0 + 1);
        return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
    }

    /// <summary>Rotates by a multiple of 90 degrees (clockwise) and optionally mirrors left-right first.</summary>
    public GrayImage Orient(int quarterTurns, bool mirror)
    {
        var src = this;
        if (mirror)
        {
            src = new GrayImage(Width, Height);
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++) src.Data[y * Width + x] = Data[y * Width + (Width - 1 - x)];
        }
        var q = ((quarterTurns % 4) + 4) % 4;
        for (var k = 0; k < q; k++) src = src.Rotate90();
        return src;
    }

    private GrayImage Rotate90()
    {
        var o = new GrayImage(Height, Width);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                o.Data[x * o.Width + (Height - 1 - y)] = Data[y * Width + x];
        return o;
    }

    /// <summary>Box blur with the given radius (separable, edges clamp).</summary>
    public GrayImage BoxBlur(int r)
    {
        if (r <= 0) return Clone();
        var tmp = new byte[Data.Length];
        var o = new GrayImage(Width, Height);
        var n = 2 * r + 1;
        Parallel.For(0, Height, y =>
        {
            var row = y * Width; var s = 0;
            for (var k = -r; k <= r; k++) s += Data[row + Math.Clamp(k, 0, Width - 1)];
            for (var x = 0; x < Width; x++)
            {
                tmp[row + x] = (byte)(s / n);
                s += Data[row + Math.Min(Width - 1, x + r + 1)] - Data[row + Math.Max(0, x - r)];
            }
        });
        Parallel.For(0, Width, x =>
        {
            var s = 0;
            for (var k = -r; k <= r; k++) s += tmp[Math.Clamp(k, 0, Height - 1) * Width + x];
            for (var y = 0; y < Height; y++)
            {
                o.Data[y * Width + x] = (byte)(s / n);
                s += tmp[Math.Min(Height - 1, y + r + 1) * Width + x] - tmp[Math.Max(0, y - r) * Width + x];
            }
        });
        return o;
    }

    /// <summary>Ink mask: true where the pixel is darker than the threshold.</summary>
    public BitMask Ink(byte threshold = 160)
    {
        var m = new BitMask(Width, Height);
        for (var i = 0; i < Data.Length; i++) m.Bits[i] = Data[i] < threshold;
        return m;
    }

    /// <summary>Share of dark pixels.</summary>
    public double InkShare(byte threshold = 160) => Data.Count(v => v < threshold) / (double)Data.Length;

    public byte[] ToPng() => PngLite.Encode(ToRgba(), grey: true);

    public RgbaImage ToRgba()
    {
        var r = new RgbaImage(Width, Height);
        for (var i = 0; i < Data.Length; i++) { var v = Data[i]; r.Pixels[i * 4] = v; r.Pixels[i * 4 + 1] = v; r.Pixels[i * 4 + 2] = v; r.Pixels[i * 4 + 3] = 255; }
        return r;
    }

    public static GrayImage FromPng(byte[] png) => ColorImage.FromPng(png).ToGray();

    public ColorImage ToColor()
    {
        var c = new ColorImage(Width, Height);
        for (var i = 0; i < Data.Length; i++) { var v = Data[i]; c.Data[i * 3] = v; c.Data[i * 3 + 1] = v; c.Data[i * 3 + 2] = v; }
        return c;
    }
}

/// <summary>[drawings] 24-bit RGB raster, row-major (R, G, B per pixel). White background by default.</summary>
public sealed class ColorImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public ColorImage(int w, int h, byte[]? data = null)
    {
        if (w <= 0 || h <= 0) throw new ArgumentException($"Bad image size {w}x{h}.");
        Width = w; Height = h;
        if (data is null) { data = new byte[w * h * 3]; Array.Fill(data, (byte)255); }
        Data = data;
    }

    public ColorImage Clone() => new(Width, Height, (byte[])Data.Clone());

    public (byte R, byte G, byte B) Get(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return (255, 255, 255);
        var i = (y * Width + x) * 3;
        return (Data[i], Data[i + 1], Data[i + 2]);
    }

    public void Set(int x, int y, Rgb c)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var i = (y * Width + x) * 3;
        Data[i] = c.R; Data[i + 1] = c.G; Data[i + 2] = c.B;
    }

    /// <summary>Alpha blend (a = 0..1).</summary>
    public void Blend(int x, int y, Rgb c, double a)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var i = (y * Width + x) * 3;
        Data[i] = (byte)(Data[i] * (1 - a) + c.R * a);
        Data[i + 1] = (byte)(Data[i + 1] * (1 - a) + c.G * a);
        Data[i + 2] = (byte)(Data[i + 2] * (1 - a) + c.B * a);
    }

    /// <summary>Multiply blend - how a highlighter pen colours paper (dark lines stay dark).</summary>
    public void Multiply(int x, int y, Rgb c)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var i = (y * Width + x) * 3;
        Data[i] = (byte)(Data[i] * c.R / 255);
        Data[i + 1] = (byte)(Data[i + 1] * c.G / 255);
        Data[i + 2] = (byte)(Data[i + 2] * c.B / 255);
    }

    /// <summary>Luminance (Rec. 601).</summary>
    public GrayImage ToGray()
    {
        var g = new GrayImage(Width, Height);
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 3;
                g.Data[y * Width + x] = (byte)((Data[i] * 299 + Data[i + 1] * 587 + Data[i + 2] * 114) / 1000);
            }
        });
        return g;
    }

    /// <summary>
    /// Ink of the drawing with highlighter colour removed: a bright, saturated pixel (green / yellow / pink highlighter over paper)
    /// reads as paper, a dark pixel stays ink whatever its hue - so highlights do not look like ink to the matcher or the registration.
    /// </summary>
    public GrayImage ToInkGray()
    {
        var g = new GrayImage(Width, Height);
        Parallel.For(0, Height, y =>
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 3;
                int r = Data[i], gg = Data[i + 1], b = Data[i + 2];
                var max = Math.Max(r, Math.Max(gg, b));
                var lum = (r * 299 + gg * 587 + b * 114) / 1000;
                // a saturated bright pixel (highlighter) is paper; a dark pixel is ink whatever its hue
                var v = max > 170 && lum > 120 ? Math.Max(lum, 230) : lum;
                g.Data[y * Width + x] = (byte)Math.Min(255, v);
            }
        });
        return g;
    }

    public ColorImage Crop(int x, int y, int w, int h)
    {
        var o = new ColorImage(w, h);
        for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                var (r, g, b) = Get(x + i, y + j);
                var k = (j * w + i) * 3;
                o.Data[k] = r; o.Data[k + 1] = g; o.Data[k + 2] = b;
            }
        return o;
    }

    public ColorImage Resize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var o = new ColorImage(w, h);
        double sx = (double)Width / w, sy = (double)Height / h;
        Parallel.For(0, h, j =>
        {
            int y0 = (int)(j * sy), y1 = Math.Max(y0 + 1, (int)((j + 1) * sy));
            for (var i = 0; i < w; i++)
            {
                int x0 = (int)(i * sx), x1 = Math.Max(x0 + 1, (int)((i + 1) * sx));
                long r = 0, g = 0, b = 0; var n = 0;
                for (var y = y0; y < y1 && y < Height; y++)
                    for (var x = x0; x < x1 && x < Width; x++)
                    {
                        var k = (y * Width + x) * 3; r += Data[k]; g += Data[k + 1]; b += Data[k + 2]; n++;
                    }
                var q = (j * w + i) * 3;
                if (n == 0) { o.Data[q] = o.Data[q + 1] = o.Data[q + 2] = 255; continue; }
                o.Data[q] = (byte)(r / n); o.Data[q + 1] = (byte)(g / n); o.Data[q + 2] = (byte)(b / n);
            }
        });
        return o;
    }

    public ColorImage FitWithin(int maxSide)
    {
        var m = Math.Max(Width, Height);
        if (m <= maxSide) return this;
        var k = (double)maxSide / m;
        return Resize((int)Math.Round(Width * k), (int)Math.Round(Height * k));
    }

    public RgbaImage ToRgba()
    {
        var r = new RgbaImage(Width, Height);
        for (var i = 0; i < Width * Height; i++)
        {
            r.Pixels[i * 4] = Data[i * 3]; r.Pixels[i * 4 + 1] = Data[i * 3 + 1]; r.Pixels[i * 4 + 2] = Data[i * 3 + 2]; r.Pixels[i * 4 + 3] = 255;
        }
        return r;
    }

    public byte[] ToPng() => PngLite.Encode(ToRgba(), grey: false);

    public static ColorImage FromRgba(RgbaImage im)
    {
        var c = new ColorImage(im.Width, im.Height);
        for (var i = 0; i < im.Width * im.Height; i++)
        {
            var a = im.Pixels[i * 4 + 3] / 255.0;
            c.Data[i * 3] = (byte)(im.Pixels[i * 4] * a + 255 * (1 - a));
            c.Data[i * 3 + 1] = (byte)(im.Pixels[i * 4 + 1] * a + 255 * (1 - a));
            c.Data[i * 3 + 2] = (byte)(im.Pixels[i * 4 + 2] * a + 255 * (1 - a));
        }
        return c;
    }

    public static ColorImage FromPng(byte[] png) =>
        FromRgba(PngLite.Decode(png) ?? throw new InvalidDataException("Unsupported PNG (16-bit or interlaced) - save it as an 8-bit PNG."));

    /// <summary>From BGRA bytes (PDFium output); transparent pixels are white paper.</summary>
    public static ColorImage FromBgra(byte[] bgra, int w, int h)
    {
        var c = new ColorImage(w, h);
        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x; var a = bgra[i * 4 + 3] / 255.0;
                c.Data[i * 3] = (byte)(bgra[i * 4 + 2] * a + 255 * (1 - a));
                c.Data[i * 3 + 1] = (byte)(bgra[i * 4 + 1] * a + 255 * (1 - a));
                c.Data[i * 3 + 2] = (byte)(bgra[i * 4] * a + 255 * (1 - a));
            }
        });
        return c;
    }
}

/// <summary>[drawings] A colour.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb DarkRed = new(0x8B, 0, 0);
    public static readonly Rgb Grey = new(0xA6, 0xA6, 0xA6);
    public static readonly Rgb Yellow = new(255, 255, 0);
    public static readonly Rgb HighlightGreen = new(150, 255, 110);
    public static readonly Rgb HighlightYellow = new(255, 250, 90);
    public static readonly Rgb HighlightPink = new(255, 150, 220);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public static Rgb Parse(string? hex, Rgb fallback)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return fallback;
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>A distinct marker colour per index (palette-friendly: dark red first).</summary>
    public static Rgb Palette(int i) => Pal[((i % Pal.Length) + Pal.Length) % Pal.Length];
    private static readonly Rgb[] Pal =
    {
        new(0x8B, 0, 0), new(0, 0x66, 0xCC), new(0x2E, 0x8B, 0x57), new(0xE0, 0x7B, 0), new(0x80, 0, 0x80),
        new(0, 0x8B, 0x8B), new(0xB8, 0x86, 0x0B), new(0xFF, 0x14, 0x93), new(0x55, 0x6B, 0x2F), new(0x46, 0x46, 0x46),
    };

    /// <summary>Hue 0..360, saturation 0..1, value 0..1.</summary>
    public static (double H, double S, double V) Hsv(byte r, byte g, byte b)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        var max = Math.Max(R, Math.Max(G, B)); var min = Math.Min(R, Math.Min(G, B)); var d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == R) h = 60 * (((G - B) / d) % 6);
            else if (max == G) h = 60 * (((B - R) / d) + 2);
            else h = 60 * (((R - G) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }
}

/// <summary>[drawings] Boolean mask with morphology and connected components.</summary>
public sealed class BitMask
{
    public int Width { get; }
    public int Height { get; }
    public bool[] Bits { get; }

    public BitMask(int w, int h, bool[]? bits = null) { Width = w; Height = h; Bits = bits ?? new bool[w * h]; }

    public bool this[int x, int y]
    {
        get => x >= 0 && y >= 0 && x < Width && y < Height && Bits[y * Width + x];
        set { if (x >= 0 && y >= 0 && x < Width && y < Height) Bits[y * Width + x] = value; }
    }

    public int Count => Bits.Count(b => b);

    public BitMask Clone() => new(Width, Height, (bool[])Bits.Clone());

    /// <summary>Square dilation (separable max filter).</summary>
    public BitMask Dilate(int r) => r <= 0 ? Clone() : Morph(r, dilate: true);
    public BitMask Erode(int r) => r <= 0 ? Clone() : Morph(r, dilate: false);
    public BitMask Close(int r) => Dilate(r).Erode(r);
    public BitMask Open(int r) => Erode(r).Dilate(r);

    private BitMask Morph(int r, bool dilate)
    {
        var tmp = new bool[Bits.Length];
        var o = new BitMask(Width, Height);
        // horizontal pass with a running count
        Parallel.For(0, Height, y =>
        {
            var row = y * Width; var c = 0;
            for (var k = 0; k <= r && k < Width; k++) if (Bits[row + k]) c++;
            for (var x = 0; x < Width; x++)
            {
                var span = Math.Min(Width - 1, x + r) - Math.Max(0, x - r) + 1;
                tmp[row + x] = dilate ? c > 0 : c == span;
                var add = x + r + 1; var rem = x - r;
                if (add < Width && Bits[row + add]) c++;
                if (rem >= 0 && Bits[row + rem]) c--;
            }
        });
        Parallel.For(0, Width, x =>
        {
            var c = 0;
            for (var k = 0; k <= r && k < Height; k++) if (tmp[k * Width + x]) c++;
            for (var y = 0; y < Height; y++)
            {
                var span = Math.Min(Height - 1, y + r) - Math.Max(0, y - r) + 1;
                o.Bits[y * Width + x] = dilate ? c > 0 : c == span;
                var add = y + r + 1; var rem = y - r;
                if (add < Height && tmp[add * Width + x]) c++;
                if (rem >= 0 && tmp[rem * Width + x]) c--;
            }
        });
        return o;
    }

    public BitMask And(BitMask o) { var m = new BitMask(Width, Height); for (var i = 0; i < Bits.Length; i++) m.Bits[i] = Bits[i] && o.Bits[i]; return m; }
    public BitMask AndNot(BitMask o) { var m = new BitMask(Width, Height); for (var i = 0; i < Bits.Length; i++) m.Bits[i] = Bits[i] && !o.Bits[i]; return m; }
    public BitMask Or(BitMask o) { var m = new BitMask(Width, Height); for (var i = 0; i < Bits.Length; i++) m.Bits[i] = Bits[i] || o.Bits[i]; return m; }

    /// <summary>Fraction of the rectangle covered by the mask.</summary>
    public double Coverage(int x, int y, int w, int h)
    {
        int n = 0, on = 0;
        for (var j = Math.Max(0, y); j < Math.Min(Height, y + h); j++)
            for (var i = Math.Max(0, x); i < Math.Min(Width, x + w); i++) { n++; if (Bits[j * Width + i]) on++; }
        return n == 0 ? 0 : (double)on / n;
    }

    /// <summary>8-connected components (iterative flood fill). Components smaller than minArea are dropped.</summary>
    public List<Component> Components(int minArea = 1)
    {
        var label = new int[Bits.Length];
        var res = new List<Component>();
        var stack = new Stack<int>();
        var next = 0;
        for (var start = 0; start < Bits.Length; start++)
        {
            if (!Bits[start] || label[start] != 0) continue;
            next++;
            var pixels = new List<int>();
            stack.Push(start); label[start] = next;
            while (stack.Count > 0)
            {
                var p = stack.Pop(); pixels.Add(p);
                int px = p % Width, py = p / Width;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                        var q = ny * Width + nx;
                        if (!Bits[q] || label[q] != 0) continue;
                        label[q] = next; stack.Push(q);
                    }
            }
            if (pixels.Count >= minArea) res.Add(new Component(Width, pixels));
        }
        return res;
    }

    public static BitMask FromRect(int w, int h, int x, int y, int rw, int rh)
    {
        var m = new BitMask(w, h);
        for (var j = Math.Max(0, y); j < Math.Min(h, y + rh); j++)
            for (var i = Math.Max(0, x); i < Math.Min(w, x + rw); i++) m.Bits[j * w + i] = true;
        return m;
    }
}

/// <summary>[drawings] A connected set of mask pixels.</summary>
public sealed class Component
{
    public Component(int width, List<int> pixels)
    {
        Pixels = pixels;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue; double sx = 0, sy = 0;
        foreach (var p in pixels)
        {
            int x = p % width, y = p / width;
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
            sx += x; sy += y;
        }
        ImageWidth = width;
        X = minX; Y = minY; W = maxX - minX + 1; H = maxY - minY + 1;
        Cx = sx / pixels.Count; Cy = sy / pixels.Count;
    }

    public List<int> Pixels { get; }
    public int ImageWidth { get; }
    public int X { get; }
    public int Y { get; }
    public int W { get; }
    public int H { get; }
    public double Cx { get; }
    public double Cy { get; }
    public int Area => Pixels.Count;
    public double Fill => (double)Area / (W * H);

    /// <summary>The component as a tight mask (with a 1-pixel border).</summary>
    public BitMask LocalMask()
    {
        var m = new BitMask(W + 2, H + 2);
        foreach (var p in Pixels) m[p % ImageWidth - X + 1, p / ImageWidth - Y + 1] = true;
        return m;
    }
}
