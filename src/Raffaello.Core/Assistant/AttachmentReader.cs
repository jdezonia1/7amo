using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using UglyToad.PdfPig;

namespace Raffaello.Core.Assistant;

/// <summary>A file attached to a question, already read: the content blocks that go into the user message.</summary>
public sealed class ChatAttachment
{
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "";
    public int Pages { get; init; }
    /// <summary>How it was read: TEXT (local text layer / table), OCR (local), CLOUD (sent to Claude as a document / image), NONE.</summary>
    public string ReadAs { get; init; } = "NONE";
    public string Note { get; init; } = "";
    /// <summary>Bytes sent to the cloud as a file (0 when only extracted text is sent).</summary>
    public long CloudBytes { get; init; }
    public List<JsonObject> Blocks { get; init; } = new();
    public string Summary => $"{FileName} ({Kind}{(Pages > 0 ? $", {Pages} p." : "")}, {ReadAs}){(Note.Length > 0 ? " - " + Note : "")}";
}

/// <summary>
/// Reads an attached PDF / image / Excel / CSV / text file. Text layers and tables are read locally and only their text is sent;
/// scans and images are sent to Claude as a document / image only when cloud document reading is switched on (Settings), otherwise a
/// local OCR engine is used when one is available (Windows OCR in the app).
/// </summary>
public sealed class AttachmentReader
{
    public const int MaxTextChars = 60_000;
    public const long MaxCloudBytes = 24L * 1024 * 1024;
    public const int MaxPdfPagesCloud = 100;

    private readonly bool _cloud;
    private readonly Func<byte[], string, CancellationToken, Task<string?>>? _ocrImage;

    /// <param name="cloudReading">Settings: cloud document reading.</param>
    /// <param name="ocrImage">Local OCR for an image (bytes, media type) - null when not available.</param>
    public AttachmentReader(bool cloudReading, Func<byte[], string, CancellationToken, Task<string?>>? ocrImage = null)
    {
        _cloud = cloudReading;
        _ocrImage = ocrImage;
    }

    public static readonly string FileFilter = "Documents|*.pdf;*.png;*.jpg;*.jpeg;*.gif;*.webp;*.xlsx;*.xlsm;*.csv;*.txt|All files|*.*";

    private static string MediaTypeOf(string ext) => ext switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "",
    };

    public async Task<ChatAttachment> ReadAsync(string path, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return ext switch
        {
            ".pdf" => ReadPdf(path, name, bytes),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => await ReadImageAsync(path, name, bytes, MediaTypeOf(ext), ct).ConfigureAwait(false),
            ".xlsx" or ".xlsm" => ReadExcel(path, name),
            ".csv" or ".txt" => TextAttachment(path, name, ext == ".csv" ? "CSV" : "TEXT", 0, Encoding.UTF8.GetString(bytes), "TEXT", ""),
            _ => new ChatAttachment { FileName = name, Path = path, Kind = ext.TrimStart('.').ToUpperInvariant(), Note = "this file type cannot be read",
                Blocks = { Text($"<attachment name=\"{name}\">This file type cannot be read by the assistant.</attachment>") } },
        };
    }

    private static JsonObject Text(string t) => new() { ["type"] = "text", ["text"] = t };

    private static ChatAttachment TextAttachment(string path, string name, string kind, int pages, string text, string readAs, string note)
    {
        var cut = text.Length > MaxTextChars;
        var body = cut ? text[..MaxTextChars] : text;
        return new ChatAttachment
        {
            FileName = name, Path = path, Kind = kind, Pages = pages, ReadAs = readAs,
            Note = cut ? (note.Length > 0 ? note + "; " : "") + $"text cut at {MaxTextChars:N0} characters" : note,
            Blocks = { Text($"<attachment name=\"{name}\" kind=\"{kind}\"{(pages > 0 ? $" pages=\"{pages}\"" : "")} read=\"{readAs}\">\n{body}\n{(cut ? "[... cut: the file is longer]\n" : "")}</attachment>") },
        };
    }

    public ChatAttachment ReadPdf(string path, string name, byte[] bytes)
    {
        var sb = new StringBuilder();
        int pages = 0, scanned = 0;
        try
        {
            using var doc = PdfDocument.Open(bytes);
            pages = doc.NumberOfPages;
            foreach (var p in doc.GetPages())
            {
                var text = string.Join(" ", p.GetWords().Select(w => w.Text));
                if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 8) scanned++;
                sb.Append("--- page ").Append(p.Number.ToString(CultureInfo.InvariantCulture)).Append(" ---\n").Append(text).Append('\n');
            }
        }
        catch (Exception ex)
        {
            return new ChatAttachment { FileName = name, Path = path, Kind = "PDF", Note = "could not be opened: " + ex.Message,
                Blocks = { Text($"<attachment name=\"{name}\">The PDF could not be opened ({ex.Message}).</attachment>") } };
        }
        var mostlyScanned = pages > 0 && scanned * 2 >= pages;
        if (mostlyScanned && _cloud && bytes.Length <= MaxCloudBytes && pages <= MaxPdfPagesCloud)
            return new ChatAttachment
            {
                FileName = name, Path = path, Kind = "PDF", Pages = pages, ReadAs = "CLOUD", CloudBytes = bytes.Length, Note = $"{scanned} scanned page(s) sent to Claude",
                Blocks =
                {
                    new JsonObject
                    {
                        ["type"] = "document", ["title"] = name,
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(bytes) },
                    },
                },
            };
        var note = mostlyScanned
            ? (_cloud ? $"scanned, too large for cloud reading ({bytes.Length / 1048576.0:0.#} MB / {pages} pages)" : $"{scanned} scanned page(s) without text: switch on cloud document reading in Settings to read them")
            : scanned > 0 ? $"{scanned} scanned page(s) without text" : "";
        return TextAttachment(path, name, "PDF", pages, sb.ToString(), "TEXT", note);
    }

    private async Task<ChatAttachment> ReadImageAsync(string path, string name, byte[] bytes, string media, CancellationToken ct)
    {
        if (_cloud && bytes.Length <= 5 * 1024 * 1024)
            return new ChatAttachment
            {
                FileName = name, Path = path, Kind = "IMAGE", ReadAs = "CLOUD", CloudBytes = bytes.Length, Note = "image sent to Claude",
                Blocks = { new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = media, ["data"] = Convert.ToBase64String(bytes) } } },
            };
        if (_ocrImage != null)
        {
            var text = await _ocrImage(bytes, media, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text)) return TextAttachment(path, name, "IMAGE", 0, text, "OCR", "read with local OCR");
        }
        var why = _cloud ? "image larger than 5 MB" : "cloud document reading is off and no local OCR is available";
        return new ChatAttachment { FileName = name, Path = path, Kind = "IMAGE", Note = why,
            Blocks = { Text($"<attachment name=\"{name}\" kind=\"IMAGE\">The image could not be read ({why}).</attachment>") } };
    }

    public static ChatAttachment ReadExcel(string path, string name)
    {
        var sb = new StringBuilder();
        var sheets = 0;
        try
        {
            using var wb = new XLWorkbook(path);
            foreach (var ws in wb.Worksheets)
            {
                var used = ws.RangeUsed();
                if (used is null) continue;
                sheets++;
                sb.Append("--- sheet ").Append(ws.Name).Append(" (").Append(used.RowCount()).Append(" rows) ---\n");
                var rows = 0;
                foreach (var row in used.Rows())
                {
                    if (rows++ >= 300) { sb.Append("[... more rows]\n"); break; }
                    var cells = row.Cells().Take(30).Select(c =>
                    {
                        try { return c.GetFormattedString().Replace('\t', ' ').Replace('\n', ' '); } catch (Exception) { return ""; }
                    });
                    var line = string.Join("\t", cells).TrimEnd('\t');
                    if (line.Length > 0) sb.Append(line).Append('\n');
                    if (sb.Length > MaxTextChars) break;
                }
                if (sb.Length > MaxTextChars) break;
            }
        }
        catch (Exception ex)
        {
            return new ChatAttachment { FileName = name, Path = path, Kind = "EXCEL", Note = "could not be opened: " + ex.Message,
                Blocks = { Text($"<attachment name=\"{name}\">The workbook could not be opened ({ex.Message}).</attachment>") } };
        }
        return TextAttachment(path, name, "EXCEL", 0, sb.ToString(), "TEXT", $"{sheets} sheet(s)");
    }
}
