using System.Buffers.Binary;
using System.IO.Compression;

namespace Raffaello.Core.Imaging;

/// <summary>A decoded image: RGBA, 8 bits per channel, row-major.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public RgbaImage(int w, int h, byte[]? px = null) { Width = w; Height = h; Pixels = px ?? new byte[w * h * 4]; }
}

/// <summary>
/// [phase6] Small managed PNG reader / writer (no native imaging library is needed, so it runs on the server and on Linux CI):
/// reads 8-bit non-interlaced PNGs (grey, RGB, palette, grey+alpha, RGBA), downsamples by area averaging and writes 8-bit
/// greyscale or RGB PNGs. Used to keep the plan images in the head-office tracker small.
/// </summary>
public static class PngLite
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static (int Width, int Height)? Size(byte[] png) =>
        png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(Signature) ? null
            : ((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));

    /// <summary>Decodes, or returns null for a format this reader does not handle (16-bit, interlaced, sub-byte depths).</summary>
    public static RgbaImage? Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return null;
        int pos = 8, w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            var kind = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (pos + 12 + len > png.Length) return null;
            var data = png.AsSpan(pos + 8, len);
            switch (kind)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(data); h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            pos += 12 + len;
            if (kind == "IEND") break;
        }
        if (depth != 8 || interlace != 0 || w <= 0 || h <= 0) return null;
        var channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || (type == 3 && palette is null)) return null;
        var stride = w * channels;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length) { var n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
            if (read < raw.Length) return null;
        }
        var cur = new byte[stride];
        var prev = new byte[stride];
        var img = new RgbaImage(w, h);
        var px = img.Pixels;
        for (var y = 0; y < h; y++)
        {
            var f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            Unfilter(f, cur, prev, channels);
            var o = y * w * 4;
            for (var x = 0; x < w; x++, o += 4)
            {
                switch (type)
                {
                    case 0: px[o] = px[o + 1] = px[o + 2] = cur[x]; px[o + 3] = 255; break;
                    case 2: px[o] = cur[x * 3]; px[o + 1] = cur[x * 3 + 1]; px[o + 2] = cur[x * 3 + 2]; px[o + 3] = 255; break;
                    case 3:
                        var i = cur[x];
                        px[o] = palette![i * 3 % palette.Length]; px[o + 1] = palette[(i * 3 + 1) % palette.Length]; px[o + 2] = palette[(i * 3 + 2) % palette.Length];
                        px[o + 3] = trns != null && i < trns.Length ? trns[i] : (byte)255; break;
                    case 4: px[o] = px[o + 1] = px[o + 2] = cur[x * 2]; px[o + 3] = cur[x * 2 + 1]; break;
                    default: Buffer.BlockCopy(cur, x * 4, px, o, 4); break;
                }
            }
            (cur, prev) = (prev, cur);
        }
        return img;
    }

    private static void Unfilter(byte f, byte[] cur, byte[] prev, int bpp)
    {
        for (var i = 0; i < cur.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            cur[i] = f switch
            {
                1 => (byte)(cur[i] + a),
                2 => (byte)(cur[i] + b),
                3 => (byte)(cur[i] + ((a + b) >> 1)),
                4 => (byte)(cur[i] + Paeth(a, b, c)),
                _ => cur[i],
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Area-average downsample to the given size (alpha is composited on white).</summary>
    public static RgbaImage Resize(RgbaImage src, int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var dst = new RgbaImage(w, h);
        double sx = (double)src.Width / w, sy = (double)src.Height / h;
        var s = src.Pixels;
        for (var y = 0; y < h; y++)
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(src.Height, (int)Math.Ceiling((y + 1) * sy)));
            for (var x = 0; x < w; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(src.Width, (int)Math.Ceiling((x + 1) * sx)));
                long r = 0, g = 0, b = 0, n = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var o = (yy * src.Width + x0) * 4;
                    for (var xx = x0; xx < x1; xx++, o += 4)
                    {
                        int al = s[o + 3];
                        r += (s[o] * al + 255 * (255 - al)) / 255; g += (s[o + 1] * al + 255 * (255 - al)) / 255; b += (s[o + 2] * al + 255 * (255 - al)) / 255;
                        n++;
                    }
                }
                var d = (y * w + x) * 4;
                dst.Pixels[d] = (byte)(r / n); dst.Pixels[d + 1] = (byte)(g / n); dst.Pixels[d + 2] = (byte)(b / n); dst.Pixels[d + 3] = 255;
            }
        }
        return dst;
    }

    /// <summary>Writes an 8-bit PNG: greyscale (one byte per pixel) or RGB, each row with the best of the five filters.</summary>
    public static byte[] Encode(RgbaImage img, bool grey)
    {
        var bpp = grey ? 1 : 3;
        var stride = img.Width * bpp;
        var px = img.Pixels;
        using var raw = new MemoryStream();
        var prev = new byte[stride];
        var line = new byte[stride];
        var cand = new byte[stride];
        var best = new byte[stride];
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                var o = (y * img.Width + x) * 4;
                if (grey) line[x] = (byte)((px[o] * 299 + px[o + 1] * 587 + px[o + 2] * 114) / 1000);
                else { line[x * 3] = px[o]; line[x * 3 + 1] = px[o + 1]; line[x * 3 + 2] = px[o + 2]; }
            }
            long bestScore = long.MaxValue; byte bestF = 0;
            for (byte f = 0; f <= 4; f++)
            {
                long score = 0;
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    var v = f switch { 1 => line[i] - a, 2 => line[i] - b, 3 => line[i] - ((a + b) >> 1), 4 => line[i] - Paeth(a, b, c), _ => line[i] };
                    cand[i] = (byte)v;
                    score += Math.Abs((int)(sbyte)(byte)v);
                }
                if (score < bestScore) { bestScore = score; bestF = f; Buffer.BlockCopy(cand, 0, best, 0, stride); }
            }
            raw.WriteByte(bestF);
            raw.Write(best, 0, stride);
            Buffer.BlockCopy(line, 0, prev, 0, stride);
        }
        using var outp = new MemoryStream();
        outp.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)img.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)img.Height);
        ihdr[8] = 8; ihdr[9] = (byte)(grey ? 0 : 2);
        Chunk(outp, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true)) { raw.Position = 0; raw.CopyTo(zs); }
            Chunk(outp, "IDAT", z.ToArray());
        }
        Chunk(outp, "IEND", Array.Empty<byte>());
        return outp.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc(t, data));
        s.Write(len);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Re-encodes a plan image to fit a byte budget: greyscale, width reduced in steps (aspect ratio kept, so normalised room
    /// coordinates stay aligned). Returns the original bytes when they already fit or the format is not supported.
    /// </summary>
    public static byte[] Shrink(byte[] png, long maxBytes, int startWidth = 2400, int minWidth = 600)
    {
        if (png.Length <= maxBytes) return png;
        var img = Decode(png);
        if (img is null) return png;
        var width = Math.Min(startWidth, img.Width);
        byte[] best = png;
        while (true)
        {
            var h = (int)Math.Round((double)img.Height * width / img.Width);
            var bytes = Encode(Resize(img, width, h), grey: true);
            if (bytes.Length < best.Length) best = bytes;
            if (bytes.Length <= maxBytes || width <= minWidth) return best;
            width = Math.Max(minWidth, (int)(width * 0.8));
        }
    }
}
