using System.Text;
using UglyToad.PdfPig;

namespace Raffaello.Core.Variations;

/// <summary>Reads the text layer of consultant documents (PdfPig). Scans without a text layer are flagged, never guessed.</summary>
public static class VariationDocuments
{
    public const int MaxChars = 200_000;

    public sealed record Extracted(string Text, int Pages, string Status);

    public static Extracted Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext is ".txt" or ".csv" or ".md") return new Extracted(Limit(File.ReadAllText(path)), 1, "TEXT");
            if (ext != ".pdf") return new Extracted("", 0, "NOT PDF");
            using var doc = PdfDocument.Open(path);
            var sb = new StringBuilder();
            var pages = 0;
            foreach (var page in doc.GetPages())
            {
                pages++;
                var t = page.Text;
                if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
                if (sb.Length > MaxChars) break;
            }
            var text = sb.ToString();
            var letters = text.Count(char.IsLetter);
            // a scan usually has no text, or a few stray characters per page
            return letters < Math.Max(20, pages * 10) ? new Extracted(Limit(text), pages, "SCANNED") : new Extracted(Limit(text), pages, "TEXT");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                                       or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            return new Extracted("", 0, "ERROR: " + ex.Message);
        }
    }

    private static string Limit(string s) => s.Length <= MaxChars ? s : s[..MaxChars];

    /// <summary>Copies the document next to the data (optional) and builds the attachment row with hash and text.</summary>
    public static VariationDoc Prepare(long variationId, string sourcePath, string? copyToFolder = null)
    {
        var path = sourcePath;
        if (!string.IsNullOrWhiteSpace(copyToFolder))
        {
            Directory.CreateDirectory(copyToFolder);
            path = AconexWeb.ZipBundle.UniquePath(Path.Combine(copyToFolder, Path.GetFileName(sourcePath)));
            File.Copy(sourcePath, path);
        }
        var x = Extract(path);
        return new VariationDoc
        {
            VariationId = variationId, FileName = Path.GetFileName(path), Path = path, Sha256 = AconexWeb.FileHash.Sha256(path),
            Pages = x.Pages, TextStatus = x.Status, ExtractedText = x.Text,
        };
    }
}
