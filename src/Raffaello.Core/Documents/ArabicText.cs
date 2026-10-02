using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Documents;

/// <summary>
/// Arabic text helpers for the reader: visual -> logical order (recognisers and broken PDF layers return Arabic left-to-right),
/// Arabic-Indic digits and separators, and a normalised form for search and comparison (alef / ya / ta marbuta variants,
/// diacritics, tatweel).
/// </summary>
public static class ArabicText
{
    public static bool IsArabicLetter(char c) => c is >= 'ء' and <= 'ي' or >= 'ٱ' and <= 'ۓ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';
    public static bool IsDiacritic(char c) => c is >= 'ً' and <= 'ٟ' or 'ٰ';
    public static bool HasArabic(string? s) => !string.IsNullOrEmpty(s) && s.Any(IsArabicLetter);
    public static int ArabicLetters(string s) => s.Count(IsArabicLetter);

    /// <summary>Strong left-to-right characters: Latin letters and digits (Western and Arabic-Indic digits are written LTR inside Arabic).</summary>
    private static bool IsLtr(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or >= '٠' and <= '٩' or >= '۰' and <= '۹' or '²' or '³' or 'µ' or '×';

    private static char Mirror(char c) => c switch { '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<', '«' => '»', '»' => '«', _ => c };

    /// <summary>
    /// Visual (left-to-right as drawn) to logical order for a line that contains Arabic: the line is reversed, then every run of
    /// left-to-right text (Latin words, numbers, codes such as "4.5" or "SUB-ELE-028") is put back in its own order; brackets are mirrored.
    /// A line without Arabic letters is returned unchanged.
    /// </summary>
    public static string VisualToLogical(string visual)
    {
        if (!HasArabic(visual)) return visual;
        var rev = visual.Reverse().Select(Mirror).ToArray();
        var sb = new StringBuilder(rev.Length);
        var i = 0;
        while (i < rev.Length)
        {
            if (!IsLtr(rev[i])) { sb.Append(rev[i]); i++; continue; }
            // an LTR run: LTR chars plus neutrals that sit between two LTR chars ("4.5", "B04_BK", "Linear lighting")
            var j = i;
            var lastLtr = i;
            while (j < rev.Length)
            {
                if (IsLtr(rev[j])) { lastLtr = j; j++; continue; }
                if (IsArabicLetter(rev[j])) break;
                j++;
            }
            var run = new string(rev, i, lastLtr - i + 1).Select(Mirror).Reverse();
            sb.Append(run.ToArray());
            i = lastLtr + 1;
        }
        return sb.ToString();
    }

    /// <summary>Logical -> visual is the same transformation (it is its own inverse for these runs).</summary>
    public static string LogicalToVisual(string logical) => VisualToLogical(logical);

    /// <summary>Arabic-Indic and extended digits to 0-9, Arabic decimal / thousands separators and comma to '.' / ','.</summary>
    public static string NormalizeDigits(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is >= '٠' and <= '٩') sb.Append((char)('0' + (c - '٠')));
            else if (c is >= '۰' and <= '۹') sb.Append((char)('0' + (c - '۰')));
            else if (c == '٫') sb.Append('.');       // Arabic decimal separator
            else if (c == '٬') sb.Append(',');       // Arabic thousands separator
            else if (c == '،') sb.Append(',');       // Arabic comma
            else if (c == '٪') sb.Append('%');       // Arabic percent
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Search / comparison form: digits normalised, diacritics and tatweel removed, alef variants -> ا, ى -> ي, ة -> ه, ؤ -> و, ئ -> ي,
    /// presentation forms decomposed, Latin upper-cased, whitespace collapsed.
    /// </summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = NormalizeDigits(s.Normalize(NormalizationForm.FormKC));
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (IsDiacritic(c) || c == 'ـ' || c == '‏' || c == '‎' || c == '‫' || c == '‬' || c == '‪') continue;
            sb.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                _ => char.ToUpperInvariant(c),
            });
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Length-preserving fold for matching: alef / ya / ta-marbuta / hamza-seat variants mapped one-to-one, Arabic-Indic digits to 0-9,
    /// Latin upper-cased. Match on the folded text, cut the value from the original with the same indexes.
    /// </summary>
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var a = s.ToCharArray();
        for (var i = 0; i < a.Length; i++)
        {
            var c = a[i];
            a[i] = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ـ' => ' ',
                >= '\u0660' and <= '\u0669' => (char)('0' + (c - '\u0660')),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + (c - '\u06F0')),
                _ => char.ToUpperInvariant(c),
            };
        }
        return new string(a);
    }

    /// <summary>Number from a cell: Arabic-Indic digits, thousands separators, a stray letter O / l / I read for 0 / 1 inside a number.</summary>
    public static double? ParseNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = NormalizeDigits(s).Trim();
        t = FixDigitConfusions(t);
        t = Regex.Replace(t, @"[^\d.,\-]", "");
        if (t.Length == 0 || !t.Any(char.IsDigit)) return null;
        // "36,000" / "1,920.50" / "1.047" ; a lone comma followed by exactly 3 digits is a thousands separator
        if (t.Contains(',') && t.Contains('.')) t = t.Replace(",", "");
        else if (Regex.IsMatch(t, @"^\d{1,3}(,\d{3})+$")) t = t.Replace(",", "");
        else t = t.Replace(',', '.');
        if (t.Count(c => c == '.') > 1) { var k = t.LastIndexOf('.'); t = t[..k].Replace(".", "") + t[k..]; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Letters that scanners and recognisers confuse with digits, only when the token is otherwise numeric ("2O26" -> "2026", "l5" -> "15").</summary>
    public static string FixDigitConfusions(string token)
    {
        if (token.Length == 0) return token;
        var digits = token.Count(char.IsDigit);
        var letters = token.Count(c => c is 'O' or 'o' or 'l' or 'I' or 'S' or 'B' or 'Z');
        if (digits == 0 || letters == 0 || digits < letters) return token;
        if (token.Any(c => char.IsLetter(c) && c is not ('O' or 'o' or 'l' or 'I' or 'S' or 'B' or 'Z'))) return token;
        return new string(token.Select(c => c switch { 'O' or 'o' => '0', 'l' or 'I' => '1', 'S' => '5', 'B' => '8', 'Z' => '2', _ => c }).ToArray());
    }

    /// <summary>Character accuracy 1 - (edit distance / reference length) on the normalised forms (spaces ignored).</summary>
    public static double CharAccuracy(string reference, string hypothesis)
    {
        var a = Normalize(reference).Replace(" ", "");
        var b = Normalize(hypothesis).Replace(" ", "");
        if (a.Length == 0) return b.Length == 0 ? 1 : 0;
        return Math.Max(0, 1 - (double)Levenshtein(a, b) / a.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
