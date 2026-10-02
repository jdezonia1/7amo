using System.Collections;
using System.Globalization;
using System.Resources;

namespace Raffaello.Core.Localization;

/// <summary>
/// Interface strings in English and Arabic (Localization/Strings.resx and Strings.ar.resx, both embedded in Raffaello.Core).
/// Keys are stable identifiers (Nav_Dashboard, Btn_Save ...). Lookups by the English text are also possible (<see cref="FromEnglish"/>)
/// so screens that still have literal English text are translated at runtime until they are converted to keys.
/// Storage stays invariant: only display formatting follows the language (<see cref="Culture"/>, Gregorian calendar for Arabic).
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Arabic = "ar";

    private static readonly ResourceManager En = new("Raffaello.Core.Localization.Strings", typeof(Loc).Assembly);
    private static readonly ResourceManager Ar = new("Raffaello.Core.Localization.Strings.ar", typeof(Loc).Assembly);
    private static Dictionary<string, string>? _byEnglish;
    private static readonly object Gate = new();

    /// <summary>Current interface language ("en" / "ar").</summary>
    public static string Language { get; private set; } = English;
    public static bool IsArabic => Language == Arabic;
    public static event Action? LanguageChanged;

    public static void SetLanguage(string? lang)
    {
        var l = Normalize(lang);
        if (l == Language) return;
        Language = l;
        LanguageChanged?.Invoke();
    }

    public static string Normalize(string? lang) => string.Equals(lang?.Trim(), Arabic, StringComparison.OrdinalIgnoreCase) || (lang?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) ?? false) ? Arabic : English;

    /// <summary>The string for a key in the current language (English, then the key itself, as fallbacks).</summary>
    public static string T(string key) => Get(key, Language);

    public static string T(string key, params object?[] args) => string.Format(Culture(Language), Get(key, Language), args);

    public static string Get(string key, string lang)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (Normalize(lang) == Arabic && Ar.GetString(key, CultureInfo.InvariantCulture) is { Length: > 0 } a) return a;
        return En.GetString(key, CultureInfo.InvariantCulture) ?? key;
    }

    public static bool Has(string key) => En.GetString(key, CultureInfo.InvariantCulture) != null;

    /// <summary>All keys with their English and Arabic text (coverage reports, tests).</summary>
    public static IReadOnlyList<(string Key, string En, string? Ar)> All()
    {
        var res = new List<(string, string, string?)>();
        var set = En.GetResourceSet(CultureInfo.InvariantCulture, true, false);
        if (set is null) return res;
        foreach (DictionaryEntry e in set)
        {
            var k = (string)e.Key;
            res.Add((k, e.Value as string ?? "", Ar.GetString(k, CultureInfo.InvariantCulture)));
        }
        return res.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }

    private static string Fold(string s) => string.Join(" ", (s ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    /// <summary>
    /// Translation of a literal English text (case and spacing ignored) into the current language, or null when the table has no entry.
    /// Used for screens whose XAML still carries English literals.
    /// </summary>
    public static string? FromEnglish(string english, string? lang = null)
    {
        var l = Normalize(lang ?? Language);
        if (l == English || string.IsNullOrWhiteSpace(english)) return null;
        if (_byEnglish is null)
            lock (Gate)
                _byEnglish ??= All().Where(r => r.Ar != null).GroupBy(r => Fold(r.En)).ToDictionary(g => g.Key, g => g.First().Key);
        return _byEnglish.TryGetValue(Fold(english), out var key) ? Get(key, l) : null;
    }

    /// <summary>
    /// Display culture: en-GB style for English (dd MMM yyyy, 1,234.5), ar-SA for Arabic with the Gregorian calendar and Latin digits
    /// (project dates and quantities stay comparable with the documents). Never used for storage.
    /// </summary>
    public static CultureInfo Culture(string? lang = null)
    {
        if (Normalize(lang ?? Language) == English)
        {
            var en = (CultureInfo)CultureInfo.GetCultureInfo("en-GB").Clone();
            en.NumberFormat.CurrencySymbol = "SAR";
            return en;
        }
        CultureInfo ar;
        try { ar = (CultureInfo)CultureInfo.GetCultureInfo("ar-SA").Clone(); }
        catch (CultureNotFoundException) { ar = (CultureInfo)CultureInfo.InvariantCulture.Clone(); }
        var greg = ar.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() ?? new GregorianCalendar();
        try { ar.DateTimeFormat.Calendar = greg; } catch (ArgumentOutOfRangeException) { }
        // ICU's ar-SA uses Arabic separators (U+066B / U+066C) and marks around signs: keep plain ASCII so figures read and parse
        // the same as in the documents
        var nf = ar.NumberFormat;
        nf.DigitSubstitution = DigitShapes.None;
        nf.NumberDecimalSeparator = nf.CurrencyDecimalSeparator = nf.PercentDecimalSeparator = ".";
        nf.NumberGroupSeparator = nf.CurrencyGroupSeparator = nf.PercentGroupSeparator = ",";
        nf.NegativeSign = "-";
        nf.PositiveSign = "+";
        nf.PercentSymbol = "%";
        nf.CurrencySymbol = "SAR";
        return ar;
    }

    /// <summary>
    /// The xml:lang WPF uses for bindings with StringFormat (it cannot take a customised culture): en-GB, or ar-AE for Arabic - an
    /// Arabic culture whose default calendar is Gregorian and whose separators are "." and "," (ar-SA would show Hijri dates).
    /// </summary>
    public static string WpfLanguageTag(string? lang = null) => Normalize(lang ?? Language) == Arabic ? "ar-AE" : "en-GB";

    public static string Number(double v, string format = "#,0.##", string? lang = null) => v.ToString(format, Culture(lang));
    public static string Date(DateTime d, string format = "dd MMM yyyy", string? lang = null) => d.ToString(format, Culture(lang));
    public static string Sar(double v, string? lang = null) => (Normalize(lang ?? Language) == Arabic ? "ر.س " : "SAR ") + v.ToString("#,0.00", Culture(lang));
}
