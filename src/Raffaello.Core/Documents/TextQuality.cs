using System.Text.RegularExpressions;

namespace Raffaello.Core.Documents;

/// <summary>Plausibility of a page's text (PDF text layer or OCR): 0..1 with the reasons it was marked down.</summary>
public sealed class TextQualityReport
{
    public double Score { get; init; }
    public List<string> Reasons { get; } = new();
    public int Tokens { get; init; }
    public int ArabicForwardHits { get; init; }
    public int ArabicReversedHits { get; init; }
    public bool ReversedArabic => ArabicReversedHits >= 2 && ArabicReversedHits > ArabicForwardHits;
    /// <summary>The text should not be used (garbled scanner OCR layer, reversed Arabic, digit/letter confusions).</summary>
    public bool IsGarbage => Score < TextQuality.GarbageBelow;
    public override string ToString() => $"{Score:0.00}{(Reasons.Count > 0 ? " (" + string.Join("; ", Reasons) + ")" : "")}";
}

/// <summary>
/// Scores text plausibility so a garbled layer (scanner OCR that reversed the Arabic, "O28-2O26" with letter O for zero,
/// "MPBCO" for MOBCO, runs of Arabic-Indic digits that are really glyph garbage) is discarded and the page is OCR'd instead.
/// Signals: Arabic word lexicon hits in forward vs reversed order, digit/letter confusions inside numbers and codes, known names
/// one edit away, share of tokens that look like words / numbers / codes, junk characters.
/// </summary>
public static class TextQuality
{
    public const double GarbageBelow = 0.55;

    /// <summary>Frequent words of contracts, schedules, statements and supplier documents (normalised spelling).</summary>
    private static readonly HashSet<string> ArabicLexicon = new(new[]
    {
        "مشروع", "شركه", "عقد", "اتفاقيه", "الكهرباء", "الكهربائيه", "اعمال", "الاعمال", "تركيب", "تسليم", "مخرج", "النوع", "من", "في", "علي", "على", "الي", "الى",
        "او", "و", "مع", "طبقا", "لاصول", "الصناعه", "مواصفات", "المشروع", "اعتماد", "المهندس", "الاستشاري", "مالك", "السعر", "شامل", "لارتفاع", "اقل", "فوق",
        "متر", "سحب", "سلك", "مفتاح", "انارة", "اناره", "بريزه", "جداري", "سقفي", "رقم", "التوصيف", "الوحده", "الكميه", "سعر", "الاجمالي", "عدد", "البند", "بند",
        "الطرف", "الاول", "الثاني", "المقاول", "الرئيسي", "الباطن", "مقاول", "تاريخ", "يوم", "الموافق", "توقيع", "هذا", "بين", "الدفع", "شروط", "دفعه", "تصرف",
        "بمستخلص", "التسليم", "الابتدائي", "للمشروع", "غرامات", "التاخير", "الضمان", "الضريبه", "المضافه", "غير", "شامله", "الاسعار", "الكميات", "نطاق", "العمل",
        "الجوده", "السلامه", "السريه", "التزامات", "الغاء", "مسؤوليه", "الكابلات", "الكابل", "تراي", "حوامل", "كابلات", "نقطه", "اكثر", "يتم", "احتساب", "جديده",
        "كل", "اضافيه", "ذلك", "الترقيم", "نظافه", "داخليا", "وخارجيا", "المخارج", "لوحه", "لوحات", "توزيع", "بيان", "مستخلص", "حتي", "حتى", "نسبه", "الانجاز",
        "الملاحظات", "التنفيذ", "مدير", "الرياض", "المملكه", "العربيه", "السعوديه", "ريال", "اسعار", "مصنعيات", "الشروط", "التجاريه", "المرفق", "جدول", "هو", "ان",
        "لا", "ما", "التي", "الذي", "به", "له", "لها", "عن", "قبل", "بعد", "عند", "حسب", "وفقا", "خلال", "مده", "سنه", "اسبوع", "يوما", "بدون", "ملاحظات",
    }.Select(ArabicText.Normalize));

    /// <summary>Names that appear on project documents; a token one edit away from one of these is a recognition error.</summary>
    private static readonly string[] KnownNames = { "MOBCO", "RAFFLES", "ACONEX", "RIYADH", "CABLES", "ROOTS", "HOTEL", "BRANDED", "RESIDENCES", "ELECTRICAL", "DELIVERY", "INVOICE" };

    private static readonly Regex Token = new(@"\S+", RegexOptions.Compiled);
    private static readonly Regex DigitLetterConfusion = new(@"(?<![A-Za-z])(?:\d+[Oo]+\d*|[Oo]\d{2,}|\d+[Oo]\b|\d+[lI]\d+)(?![A-Za-z]{2})", RegexOptions.Compiled);
    private static readonly Regex RepeatedIndic = new(@"([٠-٩۰-۹])\1{2,}", RegexOptions.Compiled);
    private static readonly Regex Numberish = new(@"^[\(\[]?[-+]?[\d٠-٩][\d٠-٩.,/:%\-xX×*]*[\)\]]?[.,:;]?$", RegexOptions.Compiled);
    private static readonly Regex Codeish = new(@"^[A-Za-z0-9][A-Za-z0-9._/\-²³]*[A-Za-z0-9]$", RegexOptions.Compiled);

    public static TextQualityReport Score(string? text)
    {
        text ??= "";
        var tokens = Token.Matches(text).Select(m => m.Value).ToList();
        if (tokens.Count == 0) return new TextQualityReport { Score = 0, Tokens = 0 };

        var arabic = tokens.Select(t => ArabicText.Normalize(Regex.Replace(t, @"[^؀-ۿ]", ""))).Where(t => t.Length >= 2).ToList();
        int fwd = 0, rev = 0;
        foreach (var t in arabic)
        {
            var stripped = t.StartsWith('و') && t.Length > 2 && !ArabicLexicon.Contains(t) ? t[1..] : t;
            if (ArabicLexicon.Contains(t) || ArabicLexicon.Contains(stripped)) fwd++;
            var r = new string(t.Reverse().ToArray());
            var rs = r.StartsWith('و') && r.Length > 2 && !ArabicLexicon.Contains(r) ? r[1..] : r;
            if (!ArabicLexicon.Contains(t) && (ArabicLexicon.Contains(r) || ArabicLexicon.Contains(rs))) rev++;
        }

        var reasons = new List<string>();
        double score = 1;
        if (rev >= 2 && rev > fwd)
        {
            reasons.Add($"reversed Arabic ({rev} words read backwards, {fwd} forwards)");
            score -= 0.6;
        }
        else if (arabic.Count >= 8 && fwd == 0)
        {
            reasons.Add("Arabic words not recognised");
            score -= 0.3;
        }

        var confusions = tokens.Count(t => DigitLetterConfusion.IsMatch(t));
        if (confusions > 0)
        {
            reasons.Add($"letter/digit confusion in {confusions} number(s) ({string.Join(", ", tokens.Where(t => DigitLetterConfusion.IsMatch(t)).Distinct().Take(3))})");
            score -= Math.Min(0.4, 0.2 + 0.05 * confusions);
        }
        var indic = RepeatedIndic.Matches(text).Count;
        if (indic > 0)
        {
            reasons.Add($"{indic} run(s) of repeated Arabic-Indic digits (glyph garbage)");
            score -= Math.Min(0.4, 0.15 * indic);
        }
        var near = tokens.Select(t => Regex.Replace(t.ToUpperInvariant(), @"[^A-Z]", "")).Where(t => t.Length >= 5)
            .Where(t => KnownNames.Any(n => n != t && n.Length == t.Length && ArabicText.Levenshtein(n, t) == 1)).Distinct().ToList();
        if (near.Count > 0)
        {
            reasons.Add($"misspelt names: {string.Join(", ", near.Take(3))}");
            score -= Math.Min(0.3, 0.15 * near.Count);
        }
        var junk = text.Count(c => c == '�' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.PrivateUse || (char.IsControl(c) && c is not ('\n' or '\r' or '\t')));
        if (junk > 0)
        {
            reasons.Add($"{junk} undecodable character(s)");
            score -= Math.Min(0.4, junk / (double)Math.Max(1, text.Length) * 20);
        }
        var plausible = tokens.Count(t => Numberish.IsMatch(t) || Codeish.IsMatch(t) || ArabicText.HasArabic(t) || t.All(c => char.IsLetter(c) || c is '.' or ',' or ':' or '\'' or '-' or '(' or ')'));
        var ratio = plausible / (double)tokens.Count;
        if (ratio < 0.8)
        {
            reasons.Add($"only {ratio:P0} of tokens look like words / numbers");
            score -= (0.8 - ratio);
        }
        var singles = tokens.Count(t => t.Length == 1 && !char.IsDigit(t[0]));
        if (tokens.Count >= 20 && singles / (double)tokens.Count > 0.35)
        {
            reasons.Add("broken into single characters");
            score -= 0.25;
        }
        var rep = new TextQualityReport { Score = Math.Clamp(score, 0, 1), Tokens = tokens.Count, ArabicForwardHits = fwd, ArabicReversedHits = rev };
        rep.Reasons.AddRange(reasons);
        return rep;
    }
}
