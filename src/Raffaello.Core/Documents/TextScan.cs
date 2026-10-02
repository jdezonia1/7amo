using System.Globalization;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Documents;

/// <summary>Small helpers shared by the document parsers: dates in supplier spellings, label values, column splits.</summary>
public static class TextScan
{
    private static readonly string[] DateFormats =
    {
        "d-MMM-yy", "dd-MMM-yy", "d-MMM-yyyy", "dd-MMM-yyyy", "MMM d, yyyy", "MMM dd, yyyy", "d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "dd MMM yyyy",
        "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "MMMM d, yyyy",
    };

    public static readonly Regex DatePattern = new(
        @"\b(\d{1,2}-[A-Za-z]{3}-\d{2,4}|[A-Za-z]{3,9}\s+\d{1,2},\s*\d{4}|\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}|\d{1,2}[/.]\d{1,2}[/.]\d{4}|\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled);

    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        return null;
    }

    public static IEnumerable<(DateTime Date, int Index)> Dates(string line)
    {
        foreach (Match m in DatePattern.Matches(line))
            if (ParseDate(m.Value) is DateTime d) yield return (d, m.Index);
    }

    /// <summary>Value after a label on the same line, up to the next column gap ("To:   Riyadh Cables Co.   Att:" -> "Riyadh Cables Co.").</summary>
    public static string? LabelValue(IEnumerable<string> lines, string labelRegex)
    {
        var rx = new Regex(@"(?:^|\s)" + labelRegex + @"\s*(?<v>\S(?:.*?\S)?)(?=\s{3,}|$)", RegexOptions.IgnoreCase);
        foreach (var l in lines)
        {
            var m = rx.Match(l);
            if (m.Success) return m.Groups["v"].Value.Trim();
        }
        return null;
    }

    /// <summary>Splits a layout line on column gaps (2+ spaces).</summary>
    public static string[] Columns(string line) => Regex.Split(line.Trim(), @"\s{2,}");

    public static double? Percent(string text, string labelRegex)
    {
        var m = Regex.Match(text, labelRegex + @"\s*:?\s*(\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0 : null;
    }

    /// <summary>Last money-looking number on a line ("SAR 9,367,585.11").</summary>
    public static double? LastAmount(string line)
    {
        var ms = Regex.Matches(line, @"(\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+\.\d{2,3})");
        return ms.Count == 0 ? null : Materials.Units.ParseNumber(ms[^1].Value);
    }

    /// <summary>The lines after a heading line up to the next heading.</summary>
    public static string Section(IReadOnlyList<string> lines, string headingRegex, string nextHeadingsRegex, int maxLines = 6)
    {
        var h = new Regex(@"^\s*" + headingRegex + @"\s*:?\s*$", RegexOptions.IgnoreCase);
        var n = new Regex(@"^\s*(" + nextHeadingsRegex + @")\s*:?\s*$", RegexOptions.IgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!h.IsMatch(lines[i])) continue;
            var parts = new List<string>();
            for (var j = i + 1; j < lines.Count && parts.Count < maxLines; j++)
            {
                if (n.IsMatch(lines[j])) break;
                if (lines[j].Trim().Length > 0) parts.Add(lines[j].Trim());
            }
            return string.Join(" ", parts);
        }
        return "";
    }
}
