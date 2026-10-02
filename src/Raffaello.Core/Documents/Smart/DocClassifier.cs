using System.Text.RegularExpressions;

namespace Raffaello.Core.Documents.Smart;

/// <summary>Document / page types the reader knows.</summary>
public static class DocTypes
{
    public const string Subcontract = "SUBCONTRACT";
    public const string RateSchedule = "RATE SCHEDULE";
    public const string Po = "PO";
    public const string Dn = "DN";
    public const string MirForm = "MIR FORM";
    public const string Mar = "MAR";
    public const string Checklist = "RECEIVING CHECKLIST";
    public const string QtyList = "QTY LIST";
    public const string TestCert = "TEST CERT";
    public const string DrumLabel = "DRUM LABEL";
    public const string Wir = "WIR";
    public const string SiteStatement = "SITE STATEMENT";
    public const string MarkedDrawing = "MARKED DRAWING";
    public const string AconexScreenshot = "ACONEX SCREENSHOT";
    public const string Invoice = "INVOICE";
    public const string Other = "OTHER";

    public static readonly string[] All =
    {
        Subcontract, RateSchedule, Po, Dn, MirForm, Mar, Checklist, QtyList, TestCert, DrumLabel, Wir, SiteStatement, MarkedDrawing, AconexScreenshot, Invoice, Other,
    };
}

/// <summary>What the classifier sees of a page besides its text.</summary>
public sealed class PageFeatures
{
    public bool IsPhoto { get; init; }
    public bool IsScan { get; init; }
    public int Words { get; init; }
    /// <summary>Ruled table grid with at least four columns.</summary>
    public bool HasGrid { get; init; }
    public double AspectWH { get; init; }
}

public sealed record Classification(string Type, double Confidence, string Reason)
{
    public static readonly Classification Unknown = new(DocTypes.Other, 0, "no keywords");
}

/// <summary>A run of pages of one type inside a bundle (e.g. the 46-page MIR: MIR FORM p1-2, MAR p3-4, ..., DRUM LABEL p26-46).</summary>
public sealed record DocSegment(string Type, int FirstPage, int LastPage, double Confidence)
{
    public string Pages => FirstPage == LastPage ? $"p{FirstPage}" : $"p{FirstPage}-{LastPage}";
    public override string ToString() => $"{Type} {Pages}";
}

/// <summary>
/// Page classifier: weighted English / Arabic keyword evidence on the normalised text plus layout features (photo, ruled grid,
/// word count). Deterministic and explainable - the reason names the cues that decided.
/// </summary>
public static class DocClassifier
{
    private sealed record Cue(string Type, Regex Rx, double Weight, string Name);

    private static Cue C(string type, string rx, double w, string? name = null) =>
        new(type, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), w, name ?? rx);

    // patterns run on ArabicText.Normalize(text): alef / ya / ta-marbuta unified, Latin upper-cased
    private static readonly Cue[] Cues =
    {
        C(DocTypes.RateSchedule, @"سعر\s*الوحده", 3, "سعر الوحدة"), C(DocTypes.RateSchedule, @"الكميه", 1.5, "الكمية"), C(DocTypes.RateSchedule, @"التوصيف", 1.5, "التوصيف"),
        C(DocTypes.RateSchedule, @"الاجمالي", 1, "الاجمالي"), C(DocTypes.RateSchedule, @"اسعار\s*مصنعيات", 3, "اسعار مصنعيات"),
        C(DocTypes.RateSchedule, @"\bSR\.?\s+DESCRIPTION\b.*\bRATE\b", 3, "Sr/Description/Rate"), C(DocTypes.RateSchedule, @"\bUNIT\s+RATE\b", 1.5, "unit rate"),

        C(DocTypes.Subcontract, @"(المقاول\s*الرييسي|مقاول\s*الباطن)", 2.5, "المقاول الرئيسي / مقاول الباطن"), C(DocTypes.Subcontract, @"الطرف\s*(الاول|الثاني)", 2, "الطرف الاول/الثاني"),
        C(DocTypes.Subcontract, @"\bبند\s*\d+", 1.5, "بند n"), C(DocTypes.Subcontract, @"(عقد\s*اتفاق|اتفاقيه\s*العقد|تم\s*توقيع\s*هذا\s*العقد)", 3, "عقد اتفاق"),
        C(DocTypes.Subcontract, @"شروط\s*الدفع|غرامات\s*التاخير|الضمان", 1.5, "شروط الدفع / غرامات / الضمان"), C(DocTypes.Subcontract, @"\bSUBCONTRACT\s+AGREEMENT\b", 3),

        C(DocTypes.Po, @"\bPURCHASE\s+ORDER\b", 3, "purchase order"), C(DocTypes.Po, @"\bP\.?\s?O\.?\s*REF\b", 2, "P.O Ref"), C(DocTypes.Po, @"\b[A-Z]{2,6}-P\.?O\.?-[A-Z0-9]+-\d{2,4}", 1, "PO number"),
        C(DocTypes.Po, @"\bGRAND\s+TOTAL\b", 1), C(DocTypes.Po, @"\bPAYMENT\s+TERMS\b", 1),

        C(DocTypes.Dn, @"\bDELIVERY\s+NOTE\b", 3, "delivery note"), C(DocTypes.Dn, @"\bSUB-?\s*TOTAL\b", 1.5, "sub-total"), C(DocTypes.Dn, @"\bBATCH\b", 1, "batch"),
        C(DocTypes.Dn, @"\bTRUCK\s+NO\b", 1.5, "truck no"), C(DocTypes.Dn, @"\bMATERIAL\s+CODE\b", 1),

        C(DocTypes.MirForm, @"MATERIAL\s+INSPECTION\s+REQUEST|\(MIR\)|-MIR-[A-Z]{1,4}-\d", 3, "MIR"),
        C(DocTypes.Mar, @"MATERIAL\s+APPROVAL\s+REQUEST|\(MAR\)", 3, "MAR"),
        C(DocTypes.Checklist, @"RECEIVING\s+CHECKLIST|ACCEPTANCE\s+CRITERIA", 3, "receiving checklist"),
        C(DocTypes.QtyList, @"QTY\s*/\s*M\b|REQUIRED\s+QTY", 2, "qty list"),
        C(DocTypes.TestCert, @"TEST\s+(CERTIFICATE|REPORT)|ROUTINE\s+TEST|CONDUCTOR\s+RESISTANCE", 3, "test certificate"), C(DocTypes.TestCert, @"\bDRUM\s+NO\b", 1.5, "drum no"),
        C(DocTypes.DrumLabel, @"\bBATCH\s*:", 2, "Batch:"), C(DocTypes.DrumLabel, @"STORAGE\s+LOC", 2, "storage loc"), C(DocTypes.DrumLabel, @"\bQUANTITY\s*:", 1),

        C(DocTypes.Wir, @"WORK\s+INSPECTION\s+REQUEST|\(WIR\)|-WIR-[A-Z]{1,4}-\d", 3, "WIR"),
        C(DocTypes.SiteStatement, @"بيان\s*اعمال|مستخلص\s*رقم|اعمال\s*حتي", 3, "بيان اعمال / مستخلص"), C(DocTypes.SiteStatement, @"MAIN\s+CONTRACTOR", 1),
        C(DocTypes.SiteStatement, @"\bINSTALL\s+(FIRST|1ST|2ND|SECOND)\s+FIX", 2, "install fix"), C(DocTypes.SiteStatement, @"\bWIR\s+NUMBER\b|نسبه\s*الانجاز", 1.5),
        C(DocTypes.MarkedDrawing, @"\b\d+\s*POINTS?\s+(SOCKET|LIGHTING|IT|GRMS|POWER)", 3, "n point(s) ..."), C(DocTypes.MarkedDrawing, @"\bTOTAL\s+\d+\s*POINT", 2),
        C(DocTypes.MarkedDrawing, @"\b\d+\s*[X×]\s*\d+\s*=\s*\d+", 1.5, "n x m = t"),
        C(DocTypes.AconexScreenshot, @"\bACONEX\b", 2, "Aconex"), C(DocTypes.AconexScreenshot, @"WORKFLOW\s+NO", 2, "Workflow No"), C(DocTypes.AconexScreenshot, @"\bSTEP\s+(NAME|STATUS|OUTCOME)\b", 2, "Step Name"),
        C(DocTypes.AconexScreenshot, @"SEARCH\s+WORKFLOWS|DATE\s+DUE|ASSIGNED\s+TO", 1),
        C(DocTypes.Invoice, @"\bTAX\s+INVOICE\b|فاتوره\s*ضريبيه|\bINVOICE\s+NO\b", 3, "invoice"), C(DocTypes.Invoice, @"PAYMENT\s+CERTIFICATE|SUBCONTRACT\s+VALUE", 2),
    };

    public static Classification Classify(string text, PageFeatures? f = null)
    {
        f ??= new PageFeatures();
        var norm = ArabicText.Normalize(text);
        var scores = DocTypes.All.ToDictionary(t => t, _ => 0.0);
        var why = DocTypes.All.ToDictionary(t => t, _ => new List<string>());
        foreach (var c in Cues)
        {
            var n = Math.Min(3, c.Rx.Matches(norm).Count);
            if (n == 0) continue;
            scores[c.Type] += c.Weight * (1 + 0.25 * (n - 1));
            why[c.Type].Add(c.Name);
        }
        // layout evidence
        if (f.HasGrid && scores[DocTypes.RateSchedule] > 0) { scores[DocTypes.RateSchedule] += 2; why[DocTypes.RateSchedule].Add("ruled table"); }
        var itemRows = Regex.Matches(norm, @"(?m)^\s*\d{1,3}\s+\S.*\s\d[\d,]*(\.\d+)?\s*$").Count;
        if (itemRows >= 5 && scores[DocTypes.RateSchedule] > 0) { scores[DocTypes.RateSchedule] += 1.5; why[DocTypes.RateSchedule].Add($"{itemRows} numbered rows"); }
        if (f.IsPhoto && scores[DocTypes.DrumLabel] > 0 && f.Words < 60) { scores[DocTypes.DrumLabel] += 2; why[DocTypes.DrumLabel].Add("photo with few words"); }
        if (f.IsPhoto && scores[DocTypes.Dn] > 0) { scores[DocTypes.Dn] += 1; why[DocTypes.Dn].Add("photo"); }
        // a rate schedule page also mentions contract words in its header; a contract body has no numbered price rows
        if (scores[DocTypes.Subcontract] > 0 && scores[DocTypes.RateSchedule] >= 4) scores[DocTypes.Subcontract] *= 0.5;
        // MIR forms quote the DN / PO numbers; the form wins over the DN when both are present
        if (scores[DocTypes.MirForm] > 0 && scores[DocTypes.Dn] > 0 && scores[DocTypes.Dn] < 4) scores[DocTypes.Dn] *= 0.5;

        var ranked = scores.Where(kv => kv.Key != DocTypes.Other).OrderByDescending(kv => kv.Value).ToList();
        var best = ranked[0];
        if (best.Value < 2)
        {
            if (f.IsPhoto && f.Words < 40) return new Classification(DocTypes.DrumLabel, 0.3, "photo with little text (guess)");
            if (f.Words > 150 && f.AspectWH > 1.2) return new Classification(DocTypes.MarkedDrawing, 0.3, "landscape page dense with small labels (guess)");
            return Classification.Unknown;
        }
        var second = ranked[1].Value;
        var conf = Math.Round(best.Value / (best.Value + second + 1.5), 2);
        return new Classification(best.Key, conf, string.Join(", ", why[best.Key].Distinct()));
    }

    /// <summary>Consecutive pages of one type become one segment; pages of type OTHER join the previous segment when it is a contract or schedule.</summary>
    public static List<DocSegment> Segments(IReadOnlyList<(int Page, Classification C)> pages)
    {
        var res = new List<DocSegment>();
        foreach (var (p, c) in pages.OrderBy(x => x.Page))
        {
            var type = c.Type;
            if (type == DocTypes.Other && res.Count > 0 && res[^1].Type is DocTypes.RateSchedule or DocTypes.Subcontract && res[^1].LastPage == p - 1) type = res[^1].Type;
            if (res.Count > 0 && res[^1].Type == type && res[^1].LastPage == p - 1)
                res[^1] = res[^1] with { LastPage = p, Confidence = Math.Min(res[^1].Confidence, Math.Max(c.Confidence, 0.3)) };
            else res.Add(new DocSegment(type, p, p, c.Confidence));
        }
        return res;
    }
}
