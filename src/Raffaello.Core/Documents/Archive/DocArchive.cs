using System.Security.Cryptography;

namespace Raffaello.Core.Documents;

/// <summary>
/// Puts what was read into the evidence index: a <see cref="DocRecord"/> (file SHA-256, type, link to the record it fed) plus one
/// <see cref="DocPageText"/> per page for the full-text search. The data itself is already in the module tables.
/// </summary>
public static class DocArchive
{
    public static DocRecord Save(IDocumentStore store, DocText text, string sourcePath, string docType, string linkedTable, string linkedKey, string evidencePath = "")
    {
        var sha = "";
        long bytes = 0;
        if (File.Exists(sourcePath))
        {
            using var f = File.OpenRead(sourcePath);
            bytes = f.Length;
            sha = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        }
        var rec = new DocRecord
        {
            FileName = text.FileName.Length > 0 ? text.FileName : Path.GetFileName(sourcePath), Sha256 = sha, Bytes = bytes, Pages = text.Pages.Count, DocType = docType,
            Segments = string.Join("; ", text.Pages.GroupBy(p => p.Kind).Where(g => g.Key.Length > 0).Select(g => $"{g.Key} p{string.Join(",", g.Select(p => p.Number))}")),
            StoredPath = evidencePath.Length > 0 ? evidencePath : sourcePath, LinkedTable = linkedTable, LinkedKey = linkedKey,
            Engines = string.Join(", ", text.Pages.Select(p => p.Source).Distinct()), Status = "CONFIRMED", ReadAt = DateTime.Now,
        };
        var pages = text.Pages.Where(p => p.Text.Trim().Length > 0).Select(p => new DocPageText
        {
            Page = p.Number, Kind = p.Kind, Source = p.Source, Quality = p.LayerQuality?.Score ?? 0, Text = p.Text, NormText = ArabicText.Normalize(p.Text),
        }).ToList();
        return store.SaveRead(rec, pages, Array.Empty<DocField>());
    }
}
