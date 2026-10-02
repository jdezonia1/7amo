using System.Security.Cryptography;
using Raffaello.Core.Imaging;

namespace Raffaello.Core.Insights;

/// <summary>Decodes an image file to RGBA (null when the format is not supported). The app adds a Windows decoder for JPEG / HEIC.</summary>
public delegate RgbaImage? ImageDecoder(string path);

/// <summary>A document / photo referenced by the data, with where it is used (for duplicate detection).</summary>
public sealed record DocumentUse(string Path, string UsedBy, string EvidenceKind, long EvidenceId, string Subcontractor = "", int InvoiceNo = 0);

/// <summary>
/// SHA-256 (exact copy) and a 64-bit difference hash (dHash: 9x8 grey thumbnail, one bit per horizontal gradient) for photos -
/// the same photo re-saved, resized or recompressed keeps (almost) the same dHash. Hashes are cached by path + size + date.
/// </summary>
public static class DocumentHashes
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp" };

    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Managed decoder: PNG only (runs everywhere). The app chains the Windows imaging decoder in front of it.</summary>
    public static RgbaImage? DecodePng(string path)
    {
        try { return System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? PngLite.Decode(File.ReadAllBytes(path)) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>64-bit dHash as 16 hex characters.</summary>
    public static string DHash(RgbaImage img)
    {
        var small = PngLite.Resize(img, 9, 8);
        ulong bits = 0;
        var px = small.Pixels;
        double Grey(int x, int y) { var o = (y * 9 + x) * 4; return 0.299 * px[o] + 0.587 * px[o + 1] + 0.114 * px[o + 2]; }
        var i = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, i++)
                if (Grey(x, y) > Grey(x + 1, y)) bits |= 1UL << i;
        return bits.ToString("x16");
    }

    public static int Distance(string a, string b)
    {
        if (a.Length != 16 || b.Length != 16) return 64;
        var x = Convert.ToUInt64(a, 16) ^ Convert.ToUInt64(b, 16);
        return System.Numerics.BitOperations.PopCount(x);
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// Hashes the given files, reusing cached rows whose size and date did not change. Returns every hash (cached or new) and the new /
    /// changed rows to store. Missing files are skipped.
    /// </summary>
    public static (List<InsightFileHash> All, List<InsightFileHash> Changed) Hash(IEnumerable<string> paths, IEnumerable<InsightFileHash> cache, ImageDecoder? decoder, DateTime now)
    {
        var byPath = cache.GroupBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First(), StringComparer.OrdinalIgnoreCase);
        var all = new List<InsightFileHash>();
        var changed = new List<InsightFileHash>();
        decoder ??= DecodePng;
        foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileInfo fi;
            try { fi = new FileInfo(p); if (!fi.Exists) continue; } catch (Exception) { continue; }
            var mod = fi.LastWriteTimeUtc;
            if (byPath.TryGetValue(p, out var hit) && hit.Bytes == fi.Length && Math.Abs((hit.ModifiedUtc - mod).TotalSeconds) < 2) { all.Add(hit); continue; }
            var row = hit ?? new InsightFileHash { FilePath = p };
            row.Bytes = fi.Length; row.ModifiedUtc = mod; row.HashedAt = now;
            try { row.Sha256 = Sha256(p); } catch (Exception) { continue; }
            row.PHash = "";
            if (IsImage(p))
                try { if (decoder(p) is { } img && img.Width >= 9 && img.Height >= 8) row.PHash = DHash(img); } catch (Exception) { /* not decodable */ }
            all.Add(row); changed.Add(row);
        }
        return (all, changed);
    }
}
