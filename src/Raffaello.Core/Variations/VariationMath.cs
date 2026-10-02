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
