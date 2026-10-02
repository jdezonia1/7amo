using System.Globalization;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Documents.Smart;

/// <summary>
/// Domain checks used by every reader and by the voting: arithmetic (qty x rate = amount, section and grand totals, VAT 15 %),
/// vocabularies (units), identifiers (BOQ codes, room IDs, contract / PO / DN numbers, item numbering) and dates.
/// </summary>
public static class DocValidators
{
    public const double VatRate = 0.15;

    /// <summary>qty x rate = amount within a halala plus rounding of 3-decimal rates (same tolerance as <see cref="Checks.AmountOk"/>).</summary>
    public static bool AmountOk(double qty, double rate, double amount) => Checks.AmountOk(qty, rate, amount);

    public static bool TotalOk(IEnumerable<double> lines, double stated, double relTol = 0.0005) =>
        Math.Abs(lines.Sum() - stated) <= Math.Max(0.05, Math.Abs(stated) * relTol);

    public static bool VatOk(double net, double vat, double rate = VatRate) => Math.Abs(net * rate - vat) <= Math.Max(0.05, net * 0.00005);
    public static bool GrandTotalOk(double net, double vat, double grand) => Math.Abs(net + vat - grand) <= 0.05;

    // ------------------------------------------------------------------ units

    private static readonly Dictionary<string, string> UnitMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["عدد"] = "عدد", ["عدد."] = "عدد", ["ع"] = "عدد", ["NO"] = "No.", ["NO."] = "No.", ["NOS"] = "No.", ["NOS."] = "No.", ["NR"] = "No.", ["EA"] = "No.", ["PCS"] = "PCS", ["PC"] = "PCS", ["SET"] = "SET",
        ["م.ط"] = "م.ط", ["مط"] = "م.ط", ["م ط"] = "م.ط", ["م . ط"] = "م.ط", ["م .ط"] = "م.ط", ["م. ط"] = "م.ط", ["ط.م"] = "م.ط", ["ط م"] = "م.ط", ["م/ط"] = "م.ط",
        ["M"] = "m", ["LM"] = "m", ["RM"] = "m", ["MTR"] = "m", ["MTRS"] = "m", ["METER"] = "m", ["KM"] = "KM", ["MT"] = "MT",
        ["م2"] = "م2", ["م٢"] = "م2", ["M2"] = "m2", ["SQM"] = "m2", ["طرف"] = "طرف", ["مقطوعيه"] = "LS", ["مقطوعية"] = "LS", ["LS"] = "LS", ["LOT"] = "LOT", ["KG"] = "KG",
        ["نقطه"] = "نقطة", ["نقطة"] = "نقطة", ["لوحه"] = "لوحة", ["لوحة"] = "لوحة",
    };

    /// <summary>Canonical unit or null when the text is not a known unit ("م ط" / "مط" / "م.ط" -> "م.ط", "Nos" -> "No.").</summary>
    public static string? Unit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = Regex.Replace(raw.Trim(), @"\s+", " ");
        if (UnitMap.TryGetValue(t, out var u)) return u;
        var squeezed = Regex.Replace(t, @"[\s.]", "");
        if (squeezed is "مط" or "طم") return "م.ط";
        if (UnitMap.TryGetValue(squeezed, out u)) return u;
        var n = ArabicText.Normalize(t);
        if (UnitMap.TryGetValue(n, out u)) return u;
        return null;
    }

    public static bool IsUnit(string? raw) => Unit(raw) != null;

    // ------------------------------------------------------------------ identifiers

    public static readonly Regex BoqCode = new(@"^B\d-\d\d-\d\d-\d\d-\d-\d\d-[A-Z]{1,3}-\d+$", RegexOptions.Compiled);
    public static readonly Regex RoomId = new(@"\b(P\d|B\d|L\d{1,2}|GF|G)-\d{2,3}[A-Z]?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex ContractNo = new(@"\b[A-Za-z]+-[A-Za-z]+-SUB-[A-Z]{2,4}-\d{3}-\d{4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex PoNo = new(@"\b[A-Z]{2,6}-P\.?O\.?-[A-Z]-\d{3}-\d{4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex DnNo = new(@"\b8\d{7}\b", RegexOptions.Compiled);
    public static readonly Regex Batch = new(@"\b00\d{8}\b", RegexOptions.Compiled);
    public static readonly Regex WorkflowNo = new(@"\bWF-\d{6}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsBoqCode(string? s) => s != null && BoqCode.IsMatch(s.Trim());

    /// <summary>Item numbers must run 1, 2, 3 ... ; returns the gaps (missing numbers) and duplicates.</summary>
    public static (List<int> Missing, List<int> Duplicates) Sequence(IEnumerable<int> numbers)
    {
        var l = numbers.ToList();
        var dup = l.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(x => x).ToList();
        if (l.Count == 0) return (new(), dup);
        var set = l.ToHashSet();
        var missing = Enumerable.Range(l.Min(), l.Max() - l.Min() + 1).Where(x => !set.Contains(x)).ToList();
        return (missing, dup);
    }

    // ------------------------------------------------------------------ dates

    private static readonly Dictionary<string, int> ArabicMonths = new()
    {
        ["يناير"] = 1, ["فبراير"] = 2, ["مارس"] = 3, ["ابريل"] = 4, ["مايو"] = 5, ["يونيو"] = 6, ["يونيه"] = 6, ["يوليو"] = 7, ["يوليه"] = 7,
        ["اغسطس"] = 8, ["سبتمبر"] = 9, ["اكتوبر"] = 10, ["نوفمبر"] = 11, ["ديسمبر"] = 12,
    };

    /// <summary>Dates as printed on project documents: "12-May-2026", "12 مايو 2026", "15.08.2026", "2026/06/15", "18-May-26".</summary>
    public static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = ArabicText.FixDigitConfusions(ArabicText.NormalizeDigits(s.Trim()));
        var d = TextScan.ParseDate(t);
        if (d != null) return d;
        var n = ArabicText.Normalize(t);
        var m = Regex.Match(n, @"(\d{1,2})\s+([ء-ي]+)\s+(\d{4})");
        if (m.Success && ArabicMonths.TryGetValue(m.Groups[2].Value, out var mon) && int.TryParse(m.Groups[1].Value, out var day) && day is >= 1 and <= 31)
            return new DateTime(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), mon, Math.Min(day, DateTime.DaysInMonth(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), mon)));
        m = Regex.Match(t, @"\b(\d{4})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{1,2})\b");
        if (m.Success && TryDate(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), out var a)) return a;
        m = Regex.Match(t, @"\b(\d{1,2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{4})\b");
        if (m.Success && TryDate(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), out var b)) return b;
        m = Regex.Match(t, @"\b(\d{1,2})-([A-Za-z]{3})-(\d{2})\b");
        if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-20{m.Groups[3].Value}", "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)) return c;
        return null;
    }

    private static bool TryDate(int y, int m, int d, out DateTime v)
    {
        v = default;
        if (y is < 1990 or > 2100 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        v = new DateTime(y, m, d);
        return true;
    }

    /// <summary>A number written in words / digits twice (Arabic and English) must agree: both parse to the same value.</summary>
    public static bool NumbersAgree(string? a, string? b) =>
        ArabicText.ParseNumber(a) is double x && ArabicText.ParseNumber(b) is double y && Math.Abs(x - y) < 0.005;
}
