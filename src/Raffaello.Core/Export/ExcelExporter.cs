using ClosedXML.Excel;

namespace Raffaello.Core.Export;

public enum ColumnKind { Text, Integer, Number, Money, Percent, Date }

public sealed record ExportColumn(string Header, ColumnKind Kind = ColumnKind.Text, double Width = 0);

public sealed class ExportSheet
{
    public string Name { get; init; } = "SHEET";
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public List<ExportColumn> Columns { get; init; } = new();
    public List<object?[]> Rows { get; init; } = new();
    /// <summary>Optional total row (accent-soft fill, bold) appended under the data.</summary>
    public object?[]? TotalRow { get; init; }
}

/// <summary>
/// Excel output in the house format: header fill #A6A6A6 with bold black text, number formats per column,
/// frozen header row, autofilter, sensible widths. Excel is import/export only - the data lives in SQLite.
/// </summary>
public static class ExcelExporter
{
    public static readonly XLColor HeaderFill = XLColor.FromHtml("#A6A6A6");
    public static readonly XLColor TitleRed = XLColor.FromHtml("#8E1B22");
    public static readonly XLColor TotalFill = XLColor.FromHtml("#F4E3E3");

    public static string Format(ColumnKind k) => k switch
    {
        ColumnKind.Integer => "#,##0",
        ColumnKind.Number => "#,##0.00",
        ColumnKind.Money => "#,##0.00",
        ColumnKind.Percent => "0.0%",
        ColumnKind.Date => "dd-mmm-yyyy",
        _ => "@",
    };

    public static void Export(string path, params ExportSheet[] sheets) => Export(path, (IEnumerable<ExportSheet>)sheets);

    public static void Export(string path, IEnumerable<ExportSheet> sheets)
    {
        using var wb = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
        {
            var name = SafeName(s.Name, used);
            var ws = wb.Worksheets.Add(name);
            Write(ws, s);
        }
        if (!wb.Worksheets.Any()) wb.Worksheets.Add("EMPTY");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    private static void Write(IXLWorksheet ws, ExportSheet s)
    {
        var r = 1;
        if (!string.IsNullOrEmpty(s.Title))
        {
            ws.Cell(r, 1).Value = s.Title.ToUpperInvariant();
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 1).Style.Font.FontSize = 14;
            ws.Cell(r, 1).Style.Font.FontColor = TitleRed;
            r++;
            if (!string.IsNullOrEmpty(s.Subtitle)) { ws.Cell(r, 1).Value = s.Subtitle; ws.Cell(r, 1).Style.Font.Italic = true; r++; }
            r++;
        }
        var headerRow = r;
        for (var c = 0; c < s.Columns.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = s.Columns[c].Header.ToUpperInvariant();
            cell.Style.Fill.BackgroundColor = HeaderFill;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.Black;
            cell.Style.Alignment.Horizontal = s.Columns[c].Kind == ColumnKind.Text ? XLAlignmentHorizontalValues.Left : XLAlignmentHorizontalValues.Right;
            cell.Style.Alignment.WrapText = true;
        }
        r++;
        foreach (var row in s.Rows) { WriteRow(ws, r, s.Columns, row); r++; }
        if (s.TotalRow != null)
        {
            WriteRow(ws, r, s.Columns, s.TotalRow);
            var range = ws.Range(r, 1, r, Math.Max(1, s.Columns.Count));
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = TotalFill;
            r++;
        }
        if (s.Columns.Count > 0)
        {
            ws.SheetView.FreezeRows(headerRow);
            if (s.Rows.Count > 0) ws.Range(headerRow, 1, headerRow + s.Rows.Count, s.Columns.Count).SetAutoFilter();
            for (var c = 0; c < s.Columns.Count; c++)
            {
                var col = ws.Column(c + 1);
                if (s.Columns[c].Width > 0) col.Width = s.Columns[c].Width;
                else
                {
                    col.AdjustToContents(headerRow, Math.Min(r, headerRow + 500));
                    col.Width = Math.Clamp(col.Width + 2, 9, 60);
                }
            }
        }
    }

    private static void WriteRow(IXLWorksheet ws, int r, List<ExportColumn> cols, object?[] row)
    {
        for (var c = 0; c < cols.Count && c < row.Length; c++)
        {
            var cell = ws.Cell(r, c + 1);
            var v = row[c];
            switch (v)
            {
                case null: break;
                case DateTime d: cell.Value = d; break;
                case double x when double.IsNaN(x) || double.IsInfinity(x): break;
                case double x: cell.Value = x; break;
                case float x: cell.Value = x; break;
                case int x: cell.Value = x; break;
                case long x: cell.Value = x; break;
                case decimal x: cell.Value = x; break;
                case bool b: cell.Value = b ? "Y" : ""; break;
                default: cell.Value = v.ToString(); break;
            }
            if (cols[c].Kind != ColumnKind.Text) cell.Style.NumberFormat.Format = Format(cols[c].Kind);
        }
    }

    private static string SafeName(string name, HashSet<string> used)
    {
        var bad = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var n = new string((name ?? "SHEET").Select(ch => bad.Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (n.Length == 0) n = "SHEET";
        if (n.Length > 31) n = n[..31];
        var baseName = n; var i = 2;
        while (!used.Add(n)) { var suffix = $" ({i++})"; n = (baseName.Length + suffix.Length > 31 ? baseName[..(31 - suffix.Length)] : baseName) + suffix; }
        return n;
    }
}
