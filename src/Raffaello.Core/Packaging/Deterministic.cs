using System.IO.Compression;
using System.Security.Cryptography;

namespace Raffaello.Core.Packaging;

/// <summary>Helpers that make generated files byte-for-byte reproducible (fixed zip entry order and timestamps).</summary>
public static class Deterministic
{
    /// <summary>Rewrites a zip-based file (xlsx, zip) with entries in ordinal order and one fixed timestamp.</summary>
    public static void NormalizeZip(string path, DateTime stamp)
    {
        var entries = new List<(string Name, byte[] Data)>();
        using (var src = ZipFile.OpenRead(path))
            foreach (var e in src.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                entries.Add((e.FullName, ms.ToArray()));
            }
        entries = CanonicalizeOffice(entries);
        // [Content_Types].xml first (Office expects it early), then ordinal
        var ordered = entries.OrderBy(e => e.Name == "[Content_Types].xml" ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var when = new DateTimeOffset(Clamp(stamp), TimeSpan.Zero);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, data) in ordered)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            e.LastWriteTime = when;
            using var w = e.Open();
            w.Write(data);
        }
    }

    /// <summary>
    /// Removes the random parts the OpenXML SDK / ClosedXML write: package relationship ids, the core-properties part name,
    /// and lowercase GUIDs (data bar ids) inside worksheets - replaced by values derived from their order.
    /// </summary>
    private static List<(string Name, byte[] Data)> CanonicalizeOffice(List<(string Name, byte[] Data)> entries)
    {
        if (!entries.Any(e => e.Name == "[Content_Types].xml")) return entries;
        var res = new List<(string, byte[])>();
        var psmdcp = entries.Select(e => e.Name).FirstOrDefault(n => n.EndsWith(".psmdcp", StringComparison.Ordinal));
        const string fixedPsm = "package/services/metadata/core-properties/core.psmdcp";
        foreach (var (name, data) in entries)
        {
            var newName = name == psmdcp ? fixedPsm : name;
            var isXml = name.EndsWith(".xml", StringComparison.Ordinal) || name.EndsWith(".rels", StringComparison.Ordinal);
            if (!isXml) { res.Add((newName, data)); continue; }
            var text = System.Text.Encoding.UTF8.GetString(data);
            var orig = text;
            if (psmdcp != null) text = text.Replace(psmdcp, fixedPsm);
            if (name == "_rels/.rels")
            {
                var n = 0;
                text = System.Text.RegularExpressions.Regex.Replace(text, @"Id=""R[0-9a-f]{16}""", _ => $"Id=\"rIdPkg{++n}\"");
            }
            if (name.StartsWith("xl/worksheets/", StringComparison.Ordinal))
            {
                var map = new Dictionary<string, string>();
                text = System.Text.RegularExpressions.Regex.Replace(text, @"\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}", m =>
                {
                    if (!map.TryGetValue(m.Value, out var v)) { v = $"{{00000000-0000-4000-8000-{map.Count + 1:x12}}}"; map[m.Value] = v; }
                    return v;
                });
            }
            res.Add((newName, ReferenceEquals(text, orig) || text == orig ? data : System.Text.Encoding.UTF8.GetBytes(text)));
        }
        return res;
    }

    public static DateTime Clamp(DateTime d) => d.Year < 1981 ? new DateTime(1981, 1, 1) : new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second / 2 * 2, DateTimeKind.Unspecified);

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
