using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Documents.Smart;

/// <summary>
/// Test certificates (drum / batch no, quantity, PO reference) and drum-label photos (batch, quantity, description) read from OCR text
/// into <see cref="MatMirEvidence"/> rows - the MIR cross-check compares their batches with the DN batches.
/// </summary>
public static class EvidenceExtractor
{
    private static readonly Regex BatchRx = new(@"\b(00\d{8})\b", RegexOptions.Compiled);

    public static List<MatMirEvidence> TestCertificate(SmartPage page)
    {
        var res = new List<MatMirEvidence>();
        var text = page.Text + "\n" + page.ReadingText;
        var fold = ArabicText.Fold(text);
        var po = Regex.Match(fold, @"P\.?\s?O\.?\s*REF\.?\s*:?\s*([A-Z]{2,6}-P\.?O\.?-[A-Z]-\d{3}-\d{4})");
        var unit = Regex.IsMatch(fold, @"QTY\s*\(\s*PC") ? "PCS" : Regex.IsMatch(fold, @"QTY\s*\(\s*KM") ? Units.Km : Units.M;
        var desc = Regex.Match(text, @"Description\s*:?\s*(.+)", RegexOptions.IgnoreCase);
        foreach (var line in page.Text.Split('\n'))
        {
            var m = Regex.Match(ArabicText.NormalizeDigits(line), @"^\s*(00\d{8})\s+(\d[\d,]*(?:\.\d+)?)\b");
            if (!m.Success) continue;
            var qty = ArabicText.ParseNumber(m.Groups[2].Value) ?? 0;
            res.Add(new MatMirEvidence
            {
                Kind = "CERT", Page = page.Number, DrumNo = m.Groups[1].Value, Batch = m.Groups[1].Value, Qty = unit == Units.Km ? qty * 1000 : qty, Unit = unit == Units.Km ? Units.M : unit,
                PoRef = po.Success ? po.Groups[1].Value : "", Description = desc.Success ? desc.Groups[1].Value.Trim() : "", Source = page.Source,
            });
        }
        if (res.Count == 0)
        {
            // certificate layout not recognised: keep the batch numbers so the cross-check still works
            foreach (Match m in BatchRx.Matches(ArabicText.NormalizeDigits(text)))
                if (res.All(r => r.Batch != m.Value))
                    res.Add(new MatMirEvidence { Kind = "CERT", Page = page.Number, DrumNo = m.Value, Batch = m.Value, PoRef = po.Success ? po.Groups[1].Value : "", Source = page.Source });
        }
        return res;
    }

    public static List<MatMirEvidence> DrumLabel(SmartPage page)
    {
        var text = ArabicText.NormalizeDigits(page.Text + "\n" + page.ReadingText);
        var batch = Regex.Match(text, @"Batch\s*:?\s*(\d{8,12})", RegexOptions.IgnoreCase);
        var b = batch.Success ? batch.Groups[1].Value : BatchRx.Match(text) is { Success: true } bm ? bm.Value : "";
        if (b.Length == 0) return new List<MatMirEvidence>();
        var qty = Regex.Match(text, @"Quantity\s*:?\s*(\d[\d,]*(?:\.\d+)?)\s*(KM|M|MTR|PCS)?\b", RegexOptions.IgnoreCase);
        var desc = Regex.Match(text, @"(\d+\s*[Xx]\s*\d+(?:\.\d+)?\s*mm\S*)", RegexOptions.IgnoreCase);
        var q = qty.Success ? ArabicText.ParseNumber(qty.Groups[1].Value) ?? 0 : 0;
        var u = qty.Success && qty.Groups[2].Value.Equals("KM", StringComparison.OrdinalIgnoreCase) ? Units.Km : Units.M;
        return new List<MatMirEvidence>
        {
            new() { Kind = "LABEL", Page = page.Number, Batch = b, DrumNo = b, Qty = u == Units.Km ? q * 1000 : q, Unit = Units.M, Description = desc.Success ? desc.Groups[1].Value : "", Source = page.Source },
        };
    }
}

/// <summary>One row of a handwritten site-statement summary (draft for review).</summary>
public sealed class StatementDraftRow
{
    public int No { get; set; }
    public string Building { get; set; } = "";
    public string Floors { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>1ST FIX / 2ND FIX / 3RD FIX.</summary>
    public string Stage { get; set; } = "";
    public string UnitType { get; set; } = "";
    public List<string> Systems { get; } = new();
    public double? Pct { get; set; }
    public string WirNo { get; set; } = "";
    public List<string> Rooms { get; } = new();
    public double Confidence { get; set; }
    public string Raw { get; set; } = "";
}

/// <summary>Counts written on a marked typical-unit drawing ("36 point socket power", "84 x 3 = 252").</summary>
public sealed class MarkedCounts
{
    public int Page { get; set; }
    public Dictionary<string, double> PerUnit { get; } = new();
    public List<(double A, double B, double Result, bool Ok)> Products { get; } = new();
    public List<string> Rooms { get; } = new();
    public string UnitType { get; set; } = "";
}

public sealed class StatementDraft
{
    public DateTime? Date { get; set; }
    public string Subcontractor { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public List<StatementDraftRow> Rows { get; } = new();
    public List<MarkedCounts> Drawings { get; } = new();
    public List<ExtractionIssue> Issues { get; } = new();
}

/// <summary>
/// Site statements: the handwritten summary table (stage, unit type, floors, WIR, %, room lists in the margin) and the counts written
/// on the marked typical-unit drawings. Handwriting read offline is weak - everything lands in a DRAFT that the user reviews.
/// </summary>
public static class SiteStatementExtractor
{
    public static readonly Regex Room = new(@"\bP\s?(\d)\s*[-–_ ]\s*(\d{2,3})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnitType = new(@"\b([1-4])\s*BR\b(?:\s*[-_]?\s*(?:TYPE\s*)?([A-C])\b)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StatementDraft Read(IEnumerable<SmartPage> summaryPages, IEnumerable<SmartPage> drawingPages)
    {
        var d = new StatementDraft();
        foreach (var p in summaryPages) ReadSummary(d, p);
        foreach (var p in drawingPages) d.Drawings.Add(ReadDrawing(p));
        if (d.Rows.Count == 0) d.Issues.Add(new(IssueLevel.Warn, "NO_ROWS", "no statement rows recognised (handwriting) - enter them by hand from the scan", null, ""));
        return d;
    }

    public static IEnumerable<string> Rooms(string text) =>
        Room.Matches(ArabicText.NormalizeDigits(text)).Select(m => $"P{m.Groups[1].Value}-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct();

    private static void ReadSummary(StatementDraft d, SmartPage p)
    {
        var fold = ArabicText.Fold(p.ReadingText + "\n" + p.Text);
        var date = DocValidators.Date(Regex.Match(ArabicText.NormalizeDigits(p.ReadingText), @"\d{4}\s*/\s*\d{1,2}\s*/\s*\d{1,2}|\d{1,2}\s*/\s*\d{1,2}\s*/\s*\d{4}").Value.Replace(" ", ""));
        d.Date ??= date;
        var no = Regex.Match(fold, @"مستخلص\s*رقم\s*:?\s*(\d{1,3})");
        if (no.Success) d.StatementNo = no.Groups[1].Value;
        // rows: lines that describe work ("Install First Fix walls 2BR (Power+light+GRMS+IT)")
        foreach (var line in Ocr.LayoutBuilder.Lines(p.Words))
        {
            var t = line.Text;
            var u = ArabicText.Fold(t);
            if (!Regex.IsMatch(u, @"INSTA|FIX|PULL|WIRE|DB\b")) continue;
            var row = new StatementDraftRow { No = d.Rows.Count + 1, Raw = t, Confidence = line.Confidence, Description = t.Trim() };
            row.Stage = Regex.IsMatch(u, @"\b(2ND|SECOND)\b|PULL") ? "2ND FIX" : Regex.IsMatch(u, @"\b(3RD|THIRD)\b") ? "3RD FIX" : Regex.IsMatch(u, @"\b(1ST|FIRST)\b|FIX") ? "1ST FIX" : "";
            var ut = UnitType.Match(u);
            if (ut.Success) row.UnitType = $"{ut.Groups[1].Value}BR{(ut.Groups[2].Success ? "-" + ut.Groups[2].Value : "")}";
            foreach (var (k, rx) in new[] { ("POWER", "POWER|POWR"), ("LIGHT", "LIGHT|LIGH"), ("GRMS", "GRMS|GRM"), ("DATA", @"\bIT\b|DATA"), ("FIRE", "FIRE"), ("EMERGENCY LIGHT", "EMERG") })
                if (Regex.IsMatch(u, rx)) row.Systems.Add(k);
            // % and WIR on the same band
            var band = p.Words.Where(w => w.Box.Cy > line.Box.Y - line.Box.H && w.Box.Cy < line.Box.Bottom + line.Box.H).ToList();
            var bandText = ArabicText.NormalizeDigits(string.Join(" ", band.Select(w => w.Text)));
            var pct = Regex.Match(bandText, @"%\s*\.?\s*(\d{2,3})\b|\b(\d{2,3})\s*\.?\s*%");
            if (pct.Success) row.Pct = double.Parse(pct.Groups[1].Success ? pct.Groups[1].Value : pct.Groups[2].Value, CultureInfo.InvariantCulture) / 100;
            row.Rooms.AddRange(Rooms(bandText));
            d.Rows.Add(row);
        }
        // rooms written in the margin that did not land on a row band
        var orphan = Rooms(p.ReadingText).Except(d.Rows.SelectMany(r => r.Rooms)).ToList();
        if (orphan.Count > 0) d.Issues.Add(new(IssueLevel.Info, "ROOMS", $"rooms found on the page but not on a row: {string.Join(", ", orphan)}", null, ""));
    }

    public static MarkedCounts ReadDrawing(SmartPage p)
    {
        var mc = new MarkedCounts { Page = p.Number };
        // handwriting: "1B" is 18, "O" is 0 when the token is otherwise a number
        var text = Regex.Replace(ArabicText.NormalizeDigits(p.ReadingText + "\n" + p.Text), @"\b[0-9OolISBZ]{1,4}\b", m => m.Value.Any(char.IsDigit) ? ArabicText.FixDigitConfusions(m.Value) : m.Value);
        var u = ArabicText.Fold(text);
        foreach (Match m in Regex.Matches(u, @"\b(\d{1,3})[ \t]*POI\w*[ \t]+([^\n]{0,40}?GRMS|SOCKET\s*POWER|SOCKET|POWER|IT\s*BOX|IT\b|DATA|LIGH\w*\s*BOX|LIGH\w*)"))
        {
            var sys = m.Groups[2].Value switch
            {
                var s when s.Contains("GRMS") => "GRMS",
                var s when s.StartsWith("SOCKET") || s.StartsWith("POWER") => "POWER",
                var s when s.StartsWith("IT") || s.StartsWith("DATA") => "DATA",
                _ => "LIGHT",
            };
            mc.PerUnit[sys] = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        foreach (Match m in Regex.Matches(u, @"\b(\d{1,4})\s*[X×*]\s*(\d{1,3})\s*=\s*(\d{1,5})\b"))
        {
            var a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); var b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); var c = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            mc.Products.Add((a, b, c, Math.Abs(a * b - c) < 0.5));
        }
        mc.Rooms.AddRange(Rooms(text));
        var ut = UnitType.Match(u);
        if (ut.Success) mc.UnitType = $"{ut.Groups[1].Value}BR";
        return mc;
    }
}

/// <summary>Turns a reviewed handwritten-statement draft into ledger claim lines (then checked against the room caps like any statement).</summary>
public static class StatementDraftConverter
{
    /// <summary>
    /// One claim line per room x system of each row. Quantity per room = the count written on the marked typical-unit drawing for that
    /// system (same unit type when known), else 0 for the user to fill. Site % = the row %.
    /// </summary>
    public static Statements.StatementImportResult ToImport(StatementDraft d, Data.ProjectSnapshot s, string subcontractor, string statementNo, int invoiceNo, string building)
    {
        var res = new Statements.StatementImportResult { Subcontractor = subcontractor.ToUpperInvariant(), StatementNo = statementNo };
        var rooms = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in d.Rows.Where(r => r.Stage.Length > 0))
        {
            var counts = d.Drawings.FirstOrDefault(m => m.UnitType.Length > 0 && row.UnitType.StartsWith(m.UnitType, StringComparison.OrdinalIgnoreCase))?.PerUnit
                         ?? (d.Drawings.Count == 1 ? d.Drawings[0].PerUnit : new Dictionary<string, double>());
            foreach (var room in row.Rooms)
                foreach (var sys in row.Systems)
                {
                    rooms.TryGetValue(room, out var r);
                    if (r is null) res.Issues.Add(new(0, Import.IssueLevel.Warning, $"{room} is not a known room."));
                    var qty = counts.GetValueOrDefault(sys);
                    if (qty <= 0) res.Issues.Add(new(0, Import.IssueLevel.Warning, $"{room} {row.Stage} {sys}: no count on the marked drawing - enter the quantity."));
                    var line = new Domain.ClaimLine
                    {
                        Building = r?.Building ?? building, Subcontractor = res.Subcontractor, InvoiceNo = invoiceNo, Stage = row.Stage, Floor = r?.Level ?? "", Room = room, Item = sys,
                        Qty = qty, SitePct = row.Pct ?? 1, WirNo = row.WirNo, Notes = "read from handwritten statement: " + row.Raw, AreaType = r?.AreaType ?? "",
                        Source = "STATEMENT SCAN", StatementNo = statementNo, SourceKey = $"STSCAN|{res.Subcontractor}|{statementNo}|{room}|{row.Stage}|{sys}", EnteredAt = DateTime.Now,
                    };
                    res.Claims.Add(line);
                }
        }
        var pending = new List<Domain.ClaimLine>(s.Claims);
        foreach (var c in res.Claims)
        {
            var bal = Ledger.LedgerRules.Balance(s.RoomQtys, pending, c.Room, c.Stage, c.Item);
            var check = Ledger.LedgerRules.Check(bal, c.Qty, null);
            res.Checks.Add((c, check));
            pending.Add(c);
        }
        res.Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join(";", res.Claims.Select(c => c.SourceKey + "|" + c.Qty)))));
        return res;
    }
}
