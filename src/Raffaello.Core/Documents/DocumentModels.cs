namespace Raffaello.Core.Documents;

/// <summary>Where a page's text came from. Scanner OCR layers are never trusted (see REQUIREMENTS: garbled contract PDF).</summary>
public static class TextSource
{
    public const string TextLayer = "TEXT";
    public const string Ocr = "OCR";
    public const string Vision = "VISION";
    public const string Excel = "EXCEL";
    public const string Manual = "MANUAL";
    public const string None = "NONE";
}

/// <summary>An image embedded in a PDF page (a scan or a phone photo usually fills the page).</summary>
public sealed class PageImage
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    /// <summary>image/jpeg or image/png.</summary>
    public string MediaType { get; init; } = "image/png";
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Share of the page area covered by the image (0..1).</summary>
    public double Coverage { get; init; }
}

/// <summary>One page of a document after reading: layout text (columns kept as runs of spaces) and how it was obtained.</summary>
public sealed class DocPage
{
    public int Number { get; init; }
    public string Text { get; set; } = "";
    public string Source { get; set; } = TextSource.None;
    public bool HasTextLayer { get; init; }
    public int WordCount { get; init; }
    /// <summary>The largest embedded image when it covers most of the page (scan / photo), else null.</summary>
    public PageImage? DominantImage { get; init; }
    /// <summary>Page classification set by the parser (MIR FORM, DN PHOTO, TEST CERT ...).</summary>
    public string Kind { get; set; } = "";
    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
    /// <summary>Text-layer words with boxes (points, origin top-left) - lets the table reader work on digital PDFs too.</summary>
    public List<Ocr.OcrWord> Words { get; set; } = new();
    /// <summary>Plausibility of the text layer (null when the page has none).</summary>
    public TextQualityReport? LayerQuality { get; set; }

    public bool IsScan => !HasTextLayer || WordCount < 8;
    public IEnumerable<string> Lines => Text.Split('\n').Select(l => l.TrimEnd('\r'));
}

/// <summary>A whole document after the text-layer pass (and optional OCR).</summary>
public sealed class DocText
{
    public string FileName { get; init; } = "";
    public List<DocPage> Pages { get; } = new();
    public IEnumerable<string> AllLines => Pages.SelectMany(p => p.Lines);
    public string AllText => string.Join("\n", Pages.Select(p => p.Text));
    public IEnumerable<DocPage> ScanPages => Pages.Where(p => p.IsScan);
}

public enum IssueLevel { Info, Warn, Error }

/// <summary>A failed check on a header field or on one extracted line.</summary>
public sealed record ExtractionIssue(IssueLevel Level, string Code, string Message, int? Line = null, string Field = "")
{
    public string Status => Level switch { IssueLevel.Error => "ERROR", IssueLevel.Warn => "CHECK", _ => "OK" };
    public override string ToString() => $"[{Code}] {(Line is int l ? $"line {l}: " : "")}{Message}";
}

/// <summary>Result of reading one document: the parsed object, per-line issues and where each page's text came from.</summary>
public sealed class ExtractionResult<T>
{
    public required T Value { get; init; }
    public string FileName { get; init; } = "";
    public List<ExtractionIssue> Issues { get; } = new();
    /// <summary>"p1 NONE, p2 TEXT ..." - provenance of the text the parser saw.</summary>
    public Dictionary<int, string> PageSources { get; } = new();
    public DocText? Text { get; init; }

    public bool HasErrors => Issues.Any(i => i.Level == IssueLevel.Error);
    public int Warnings => Issues.Count(i => i.Level == IssueLevel.Warn);
    public IEnumerable<ExtractionIssue> IssuesFor(int line) => Issues.Where(i => i.Line == line);
    public void Error(string code, string msg, int? line = null, string field = "") => Issues.Add(new(IssueLevel.Error, code, msg, line, field));
    public void Warn(string code, string msg, int? line = null, string field = "") => Issues.Add(new(IssueLevel.Warn, code, msg, line, field));
    public void Info(string code, string msg, int? line = null, string field = "") => Issues.Add(new(IssueLevel.Info, code, msg, line, field));
}

/// <summary>Arithmetic checks shared by every reader (qty x rate = amount, totals) with money tolerances.</summary>
public static class Checks
{
    /// <summary>Line tolerance: half a halala plus rounding of 3-decimal unit rates (0.0005 x qty).</summary>
    public static bool AmountOk(double qty, double rate, double amount) =>
        Math.Abs(qty * rate - amount) <= Math.Max(0.011, Math.Abs(qty) * 0.0005 + 0.005);

    public static bool TotalOk(double expected, double actual, double tol = 0.05) => Math.Abs(expected - actual) <= tol;
}
