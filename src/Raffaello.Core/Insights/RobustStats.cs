namespace Raffaello.Core.Insights;

/// <summary>Median-based statistics that a few wild values cannot drag (robust z-score = 0.6745 (x - median) / MAD).</summary>
public static class RobustStats
{
    public static double Median(IEnumerable<double> values)
    {
        var a = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    /// <summary>Median absolute deviation.</summary>
    public static double Mad(IEnumerable<double> values)
    {
        var list = values.ToList();
        var m = Median(list);
        return Median(list.Select(v => Math.Abs(v - m)));
    }

    /// <summary>
    /// Robust z-score of <paramref name="x"/> against <paramref name="sample"/>. When the MAD is 0 (most values identical) the
    /// mean absolute deviation is used (x 1.2533), and when that is 0 too any different value scores +/- infinity (capped at 99).
    /// </summary>
    public static double Z(double x, IReadOnlyCollection<double> sample)
    {
        if (sample.Count == 0) return 0;
        var med = Median(sample);
        var mad = Mad(sample);
        double scale;
        if (mad > 1e-12) scale = mad / 0.6745;
        else
        {
            var meanAd = sample.Average(v => Math.Abs(v - med));
            scale = meanAd > 1e-12 ? meanAd * 1.2533 : 0;
        }
        if (scale <= 0) return Math.Abs(x - med) < 1e-9 ? 0 : Math.Sign(x - med) * 99;
        return Math.Clamp((x - med) / scale, -99, 99);
    }

    /// <summary>First significant digit (1..9) of a non-zero number, 0 for zero.</summary>
    public static int FirstDigit(double v)
    {
        v = Math.Abs(v);
        if (v < 1e-9 || double.IsInfinity(v) || double.IsNaN(v)) return 0;
        while (v >= 10) v /= 10;
        while (v < 1) v *= 10;
        return Math.Clamp((int)Math.Floor(v), 1, 9);
    }

    /// <summary>Benford first-digit test: chi-square statistic (8 degrees of freedom) and whether it exceeds the 1 % critical value 20.09.</summary>
    public static (double Chi2, bool Deviates, int N, int[] Counts) Benford(IEnumerable<double> values)
    {
        var counts = new int[10];
        foreach (var v in values) { var d = FirstDigit(v); if (d > 0) counts[d]++; }
        var n = counts.Sum();
        if (n == 0) return (0, false, 0, counts);
        double chi = 0;
        for (var d = 1; d <= 9; d++)
        {
            var e = n * Math.Log10(1 + 1.0 / d);
            chi += (counts[d] - e) * (counts[d] - e) / e;
        }
        return (chi, chi > 20.09, n, counts);
    }

    /// <summary>Least squares (slope per x unit, intercept, r2).</summary>
    public static (double Slope, double Intercept, double R2) Regression(IReadOnlyList<double> x, IReadOnlyList<double> y) =>
        Analytics.ProjectAnalytics.LinearRegression(x, y);
}
