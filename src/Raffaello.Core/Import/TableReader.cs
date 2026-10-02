using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Raffaello.Core.Import;

public sealed class TableRow
{
    public int RowNumber { get; init; }
    public Dictionary<string, string> Cells { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First non-empty value among the given header aliases.</summary>
    public string Get(params string[] names)
    {
        foreach (var n in names)
            if (Cells.TryGetValue(TableReader.NormalizeHeader(n), out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }

    public double? GetNumber(params string[] names)
    {
        var s = Get(names).Replace(",", "").Replace("SAR", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (s.EndsWith('%') && double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return pct / 100.0;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public DateTime? GetDate(params string[] names)
    {
        var s = Get(names);
        if (s.Length == 0) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 20000 && oa < 80000) return DateTime.FromOADate(oa);
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d-MMM-yy", "dd.MM.yyyy", "M/d/yyyy h:mm:ss tt", "dd/MM/yyyy HH:mm:ss" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d) ? d : null;
    }
}

public sealed class TableData
{
    public string Source { get; init; } = "";
    public List<string> Headers { get; } = new();
    public List<TableRow> Rows { get; } = new();
    public bool Has(params string[] names) => names.Any(n => Headers.Contains(TableReader.NormalizeHeader(n), StringComparer.OrdinalIgnoreCase));
}

/// <summary>Reads the first sheet of an .xlsx/.xlsm, or a .csv, into header-keyed rows.</summary>
public static class TableReader
{
    public static string NormalizeHeader(string h) =>
        string.Join(' ', (h ?? "").ToUpperInvariant().Replace("_", " ").Replace(".", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static TableData Read(string path, string? sheet = null)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".csv" or ".txt" ? ReadCsv(path) : ReadExcel(path, sheet);
    }

    public static TableData ReadExcel(string path, string? sheet = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = sheet is null ? wb.Worksheets.First() : wb.Worksheet(sheet);
        var t = new TableData { Source = path };
        var used = ws.RangeUsed();
        if (used is null) return t;
        // header = first row with at least two non-empty cells
        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        var lastCol = used.LastColumn().ColumnNumber();
        var headerRow = firstRow;
        for (var r = firstRow; r <= Math.Min(lastRow, firstRow + 15); r++)
        {
            var nonEmpty = Enumerable.Range(1, lastCol).Count(c => !ws.Cell(r, c).IsEmpty());
            if (nonEmpty >= 2) { headerRow = r; break; }
        }
        var headers = Enumerable.Range(1, lastCol).Select(c => NormalizeHeader(ws.Cell(headerRow, c).GetFormattedString())).ToList();
        t.Headers.AddRange(headers.Where(h => h.Length > 0));
        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var row = new TableRow { RowNumber = r };
            var any = false;
            for (var c = 1; c <= lastCol; c++)
            {
                if (headers[c - 1].Length == 0) continue;
                var cell = ws.Cell(r, c);
                string v;
                if (cell.DataType == XLDataType.Number) v = cell.GetDouble().ToString(CultureInfo.InvariantCulture);
                else if (cell.DataType == XLDataType.DateTime) v = cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                else v = cell.GetFormattedString();
                if (!string.IsNullOrWhiteSpace(v)) any = true;
                row.Cells[headers[c - 1]] = v;
            }
            if (any) t.Rows.Add(row);
        }
        return t;
    }

    public static TableData ReadCsv(string path)
    {
        var t = new TableData { Source = path };
        using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, true);
        var lines = ParseCsv(sr.ReadToEnd());
        if (lines.Count == 0) return t;
        var headers = lines[0].Select(NormalizeHeader).ToList();
        t.Headers.AddRange(headers.Where(h => h.Length > 0));
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].All(string.IsNullOrWhiteSpace)) continue;
            var row = new TableRow { RowNumber = i + 1 };
            for (var c = 0; c < headers.Count && c < lines[i].Count; c++)
                if (headers[c].Length > 0) row.Cells[headers[c]] = lines[i][c];
            t.Rows.Add(row);
        }
        return t;
    }

    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var delimiter = text.Split('\n').FirstOrDefault()?.Count(ch => ch == ';') > text.Split('\n').FirstOrDefault()?.Count(ch => ch == ',') ? ';' : ',';
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else sb.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { row.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '\n') { row.Add(sb.ToString().TrimEnd('\r')); sb.Clear(); rows.Add(row); row = new List<string>(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString().TrimEnd('\r')); rows.Add(row); }
        return rows;
    }
}
