namespace Raffaello.Core.Variations;

public sealed record VariationTotals(double Additions, double Omissions, double NewItems, int Lines)
{
    /// <summary>Additions + new items (positive).</summary>
    public double AddTotal => Additions + NewItems;
    /// <summary>Negative.</summary>
    public double OmitTotal => Omissions;
    public double Net => AddTotal + OmitTotal;
}

public sealed record AgeingRow(string Bucket, int Count, double Net);

/// <summary>Commercial proposal totals: omitted (negative) + additional = variance, then the compounding markups and VAT.</summary>
public sealed record ProposalTotals(double Omitted, double Additional, double Variance, double Gr, double Eng, double Oh, double ExclVat, double Vat)
{
    public double InclVat => Math.Round(ExclVat + Vat, 2);
}

public static class VariationMath
{
    /// <summary>NEW ITEM rate = (material + labour + equipment) x (1 + overhead %) x (1 + profit %).</summary>
    public static double BuildUpRate(double material, double labour, double equipment, double overheadPct, double profitPct)
    {
        var direct = material + labour + equipment;
        return Math.Round(direct * (1 + overheadPct) * (1 + profitPct), 2, MidpointRounding.AwayFromZero);
    }

    public static double RateOf(VariationLine l) =>
        l.Kind == VariationLineKinds.NewItem ? BuildUpRate(l.Material, l.Labour, l.Equipment, l.OverheadPct, l.ProfitPct) : l.Rate;

    /// <summary>Signed quantity: omissions are negative whatever was typed.</summary>
    public static double SignedQty(VariationLine l) => l.Kind == VariationLineKinds.Omission ? -Math.Abs(l.Qty) : Math.Abs(l.Qty);

    public static double Amount(VariationLine l) => Math.Round(SignedQty(l) * RateOf(l), 2, MidpointRounding.AwayFromZero);

    public static VariationTotals Totals(IEnumerable<VariationLine> lines)
    {
        double add = 0, omit = 0, nw = 0; var n = 0;
        foreach (var l in lines)
        {
            n++;
            var a = Amount(l);
            switch (l.Kind)
            {
                case VariationLineKinds.Omission: omit += a; break;
                case VariationLineKinds.NewItem: nw += a; break;
                default: add += a; break;
            }
        }
        return new VariationTotals(Math.Round(add, 2), Math.Round(omit, 2), Math.Round(nw, 2), n);
    }

    /// <summary>
    /// Variance = additional + omitted (omissions are negative). Markups compound on the running total in the order
    /// GR &amp; logistics, engineering &amp; logistics, overhead (EI-07: 9,898,846 + 8% + 9% = 11,652,921.74); VAT on top.
    /// </summary>
    public static ProposalTotals Proposal(Variation v, IEnumerable<VariationLine> lines)
    {
        var t = Totals(lines);
        static double R(double x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
        var variance = R(t.AddTotal + t.OmitTotal);
        var gr = R(variance * v.MarkupGrPct);
        var eng = R((variance + gr) * v.MarkupEngPct);
        var oh = R((variance + gr + eng) * v.MarkupOhPct);
        var excl = R(variance + gr + eng + oh);
        return new ProposalTotals(t.OmitTotal, t.AddTotal, variance, gr, eng, oh, excl, R(excl * v.VatPct));
    }

    private static readonly System.Text.RegularExpressions.Regex ProjectCode =
        new(@"^B(\d+)-\d{2}-\d{2}-\d{2}-\d+-(\d+)-([A-Z]+)-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Bill ("06"), section letter ("R") and BOQ reference ("AM", page 3) of a line from its project code.</summary>
    public static (string Bill, string Section, string Ref, string Page) BoqPlace(VariationLine l)
    {
        var m = ProjectCode.Match(l.ItemCode.Trim());
        if (!m.Success) return ("", "", "", "");
        var div = int.Parse(m.Groups[2].Value);
        var section = div is 26 or 27 or 28 ? "R" : div is 21 or 22 or 23 or 25 ? "Q" : "";
        return (int.Parse(m.Groups[1].Value).ToString("00"), section, m.Groups[3].Value.ToUpperInvariant(), m.Groups[4].Value);
    }

    public static string BillName(string bill) => bill switch
    {
        "02" => "Bill No. 02 - Hotel Basement", "03" => "Bill No. 03 - Hotel",
        "05" => "Bill No. 05 - Basement - Branded Residences", "06" => "Bill No. 06 - Main Building - Branded Residences",
        "" => "New items / other", _ => $"Bill No. {bill}",
    };

    public static string SectionName(string s) => s switch
    {
        "R" => "SECTION R - ELECTRICAL INSTALLATIONS", "Q" => "SECTION Q - MECHANICAL INSTALLATIONS", "" => "", _ => $"SECTION {s}",
    };

    /// <summary>Days a variation has been waiting: from submission (or its date while still a draft) to the decision or today.</summary>
    public static int AgeDays(Variation v, DateTime today)
    {
        var start = (v.SubmittedAt ?? v.Date).Date;
        var end = (VariationStatus.IsClosed(v.Status) ? v.DecidedAt ?? v.StatusChangedAt : today).Date;
        return Math.Max(0, (int)(end - start).TotalDays);
    }

    public static string AgeBucket(int days) => days switch
    {
        <= 30 => "0-30 DAYS",
        <= 60 => "31-60 DAYS",
        <= 90 => "61-90 DAYS",
        _ => "OVER 90 DAYS",
    };

    /// <summary>Open (not closed) variations by age bucket with their net value.</summary>
    public static List<AgeingRow> Ageing(IEnumerable<Variation> variations, Func<Variation, double> net, DateTime today)
    {
        var buckets = new[] { "0-30 DAYS", "31-60 DAYS", "61-90 DAYS", "OVER 90 DAYS" };
        var open = variations.Where(v => !VariationStatus.IsClosed(v.Status)).ToList();
        return buckets.Select(b =>
        {
            var inB = open.Where(v => AgeBucket(AgeDays(v, today)) == b).ToList();
            return new AgeingRow(b, inB.Count, Math.Round(inB.Sum(net), 2));
        }).ToList();
    }

    /// <summary>Validation messages for a line (empty = OK).</summary>
    public static List<string> Validate(VariationLine l)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(l.Description)) e.Add("description missing");
        if (string.IsNullOrWhiteSpace(l.Unit)) e.Add("unit missing");
        if (Math.Abs(l.Qty) < 1e-9) e.Add("quantity is zero");
        if (l.Kind is VariationLineKinds.Omission or VariationLineKinds.Addition)
        {
            if (string.IsNullOrWhiteSpace(l.ItemCode)) e.Add("existing item code missing");
            if (l.Rate <= 0) e.Add("contract rate missing");
        }
        else if (l.Material + l.Labour + l.Equipment <= 0) e.Add("rate build-up is empty");
        if (l.OverheadPct is < 0 or > 1 || l.ProfitPct is < 0 or > 1) e.Add("overhead / profit must be 0-100 %");
        return e;
    }
}
