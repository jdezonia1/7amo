using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Analytics;

public sealed record WeeklyPoint(DateTime WeekStart, double Planned, double? Actual);
public sealed record Forecast(double SlopePerWeek, double Intercept, DateTime? CompletionDate, double RSquared, int PointsUsed);
public sealed record HeatCell(string Level, string System, double Pct, double Qs);
public sealed record AgeBucket(string Label, int Count);
public sealed record MonthFlow(DateTime Month, double Claimed, double Certified, double CumulativeCertified);
public sealed record NamedValue(string Name, double Value);

/// <summary>Number crunching for the charts: S-curve, regression forecast, heatmap, ageing, cash flow.</summary>
public static class ProjectAnalytics
{
    public static readonly string[] LevelOrder = { "B1", "GF", "L1", "L2", "L3", "L4", "L5", "L6", "RF" };

    public static int LevelRank(string level)
    {
        var i = Array.IndexOf(LevelOrder, level);
        return i < 0 ? 99 : i;
    }

    public static DateTime WeekStart(DateTime d)
    {
        var diff = ((int)d.DayOfWeek - (int)DayOfWeek.Sunday + 7) % 7; // Riyadh work week starts Sunday
        return d.Date.AddDays(-diff);
    }

    /// <summary>
    /// Planned vs actual cumulative progress per week. Actual = approved WIR quantity (rework excluded) / QS,
    /// computed per stage and averaged across the stages in scope (stages are never summed).
    /// </summary>
    public static List<WeeklyPoint> SCurve(ProjectSnapshot s, IReadOnlyCollection<ChainRow> rows, DateTime start, DateTime plannedFinish, DateTime today)
    {
        var lineStage = rows.ToDictionary(r => r.Id, r => r.Stage);
        var qsByStage = rows.GroupBy(r => r.Stage).ToDictionary(g => g.Key, g => g.Sum(r => r.Qs));
        var wirs = s.Wirs.ToDictionary(w => w.Id);
        var events = new List<(DateTime At, string Stage, double Qty)>();
        foreach (var wl in s.WirLines)
        {
            if (!lineStage.TryGetValue(wl.LineId, out var stage)) continue;
            if (!wirs.TryGetValue(wl.WirId, out var w) || !ProgressRules.CountsAsProgress(w, wl)) continue;
            events.Add((w.ApprovedAt ?? w.SubmittedAt, stage, wl.Qty));
        }
        events.Sort((a, b) => a.At.CompareTo(b.At));

        var result = new List<WeeklyPoint>();
        var w0 = WeekStart(start);
        var end = WeekStart(plannedFinish > today ? plannedFinish : today).AddDays(7);
        var totalWeeks = Math.Max(1, (plannedFinish - start).TotalDays / 7.0);
        var cum = qsByStage.Keys.ToDictionary(k => k, _ => 0.0);
        var idx = 0;
        for (var wk = w0; wk <= end; wk = wk.AddDays(7))
        {
            var weekEnd = wk.AddDays(7);
            while (idx < events.Count && events[idx].At < weekEnd)
            {
                cum[events[idx].Stage] += events[idx].Qty;
                idx++;
            }
            var t = (wk - w0).TotalDays / 7.0 / totalWeeks;
            var planned = Logistic(t);
            double? actual = null;
            if (wk <= today && qsByStage.Count > 0)
                actual = qsByStage.Average(kv => kv.Value <= 0 ? 0 : Math.Min(1.0, cum[kv.Key] / kv.Value));
            result.Add(new WeeklyPoint(wk, planned, actual));
        }
        return result;
    }

    /// <summary>Normalised logistic S-curve on [0,1].</summary>
    public static double Logistic(double t, double k = 9.0)
    {
        t = Math.Clamp(t, 0, 1);
        double f(double x) => 1.0 / (1.0 + Math.Exp(-k * (x - 0.5)));
        return (f(t) - f(0)) / (f(1) - f(0));
    }

    /// <summary>Least-squares line through (week index, cumulative %) over the last <paramref name="window"/> actual weeks; extrapolated to 100%.</summary>
    public static Forecast ForecastCompletion(IReadOnlyList<WeeklyPoint> curve, int window = 8)
    {
        var pts = curve.Select((p, i) => (i, p)).Where(x => x.p.Actual.HasValue).ToList();
        if (pts.Count < 2) return new Forecast(0, 0, null, 0, pts.Count);
        var use = pts.Skip(Math.Max(0, pts.Count - window)).ToList();
        var xs = use.Select(u => (double)u.i).ToArray();
        var ys = use.Select(u => u.p.Actual!.Value).ToArray();
        var (slope, intercept, r2) = LinearRegression(xs, ys);
        DateTime? done = null;
        var last = use[^1];
        if (last.p.Actual >= 0.9999) done = last.p.WeekStart;
        else if (slope > 1e-9)
        {
            var weekIdx = (1.0 - intercept) / slope;
            var w0 = curve[0].WeekStart;
            var dt = w0.AddDays(weekIdx * 7);
            if (dt < DateTime.MaxValue.AddYears(-1) && weekIdx < 52 * 20) done = dt.Date;
        }
        return new Forecast(slope, intercept, done, r2, use.Count);
    }

    public static (double slope, double intercept, double r2) LinearRegression(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n == 0) return (0, 0, 0);
        var mx = x.Average(); var my = y.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); syy += (y[i] - my) * (y[i] - my); }
        if (sxx == 0) return (0, my, 0);
        var slope = sxy / sxx;
        var intercept = my - slope * mx;
        var r2 = syy == 0 ? 1 : sxy * sxy / (sxx * syy);
        return (slope, intercept, r2);
    }

    /// <summary>Level x System heat map of % GIVEN (mean of stage percentages).</summary>
    public static List<HeatCell> GivenHeatmap(IEnumerable<ChainRow> rows) =>
        rows.GroupBy(r => (r.Level, r.System))
            .Select(g => new HeatCell(g.Key.Level, g.Key.System, ChainMath.MeanStagePct(g, t => t.GivenPct), g.Where(r => r.Stage == g.First().Stage).Sum(r => r.Qs)))
            .OrderBy(c => LevelRank(c.Level)).ThenBy(c => c.System).ToList();

    /// <summary>Points by system: QS of one stage (the same points are measured at every stage, so stages are not added).</summary>
    public static List<NamedValue> PointsBySystem(IEnumerable<ChainRow> rows) =>
        rows.GroupBy(r => r.System)
            .Select(g => new NamedValue(g.Key, g.GroupBy(r => r.Stage).Max(st => st.Sum(r => r.Qs))))
            .OrderByDescending(v => v.Value).ToList();

    public static List<AgeBucket> WirAgeing(IEnumerable<Wir> wirs, DateTime today)
    {
        var open = wirs.Where(w => w.Status == WirStatus.Open).Select(w => (today - w.SubmittedAt.Date).TotalDays).ToList();
        return new List<AgeBucket>
        {
            new("0-7 DAYS", open.Count(d => d <= 7)),
            new("8-14 DAYS", open.Count(d => d > 7 && d <= 14)),
            new("15-30 DAYS", open.Count(d => d > 14 && d <= 30)),
            new("30+ DAYS", open.Count(d => d > 30)),
        };
    }

    /// <summary>Claimed (increment of each cumulative statement) vs certified per month, with the cumulative certified line.</summary>
    public static List<MonthFlow> CashFlow(IEnumerable<Invoice> invoices)
    {
        var list = invoices.Where(i => i.Status != InvoiceStatus.Rejected).ToList();
        var claimedInc = new List<(DateTime Month, double Amt)>();
        foreach (var sub in list.GroupBy(i => i.Subcontractor))
        {
            double prev = 0;
            foreach (var inv in sub.OrderBy(i => i.InvDate))
            {
                claimedInc.Add((new DateTime(inv.InvDate.Year, inv.InvDate.Month, 1), Math.Max(0, inv.ClaimedAmount - prev)));
                prev = Math.Max(prev, inv.ClaimedAmount);
            }
        }
        var certInc = new List<(DateTime Month, double Amt)>();
        foreach (var sub in list.Where(i => i.Status == InvoiceStatus.Certified && i.CertifiedAt.HasValue).GroupBy(i => i.Subcontractor))
        {
            double prev = 0;
            foreach (var inv in sub.OrderBy(i => i.CertifiedAt))
            {
                certInc.Add((new DateTime(inv.CertifiedAt!.Value.Year, inv.CertifiedAt.Value.Month, 1), Math.Max(0, inv.CertifiedAmount - prev)));
                prev = Math.Max(prev, inv.CertifiedAmount);
            }
        }
        var months = claimedInc.Select(c => c.Month).Concat(certInc.Select(c => c.Month)).Distinct().OrderBy(m => m).ToList();
        if (months.Count == 0) return new();
        var all = new List<DateTime>();
        for (var m = months[0]; m <= months[^1]; m = m.AddMonths(1)) all.Add(m);
        double cum = 0;
        return all.Select(m =>
        {
            var cl = claimedInc.Where(c => c.Month == m).Sum(c => c.Amt);
            var ce = certInc.Where(c => c.Month == m).Sum(c => c.Amt);
            cum += ce;
            return new MonthFlow(m, cl, ce, cum);
        }).ToList();
    }

    /// <summary>Weekly approved WIR quantity for the last N weeks (sparkline).</summary>
    public static List<double> WeeklyDone(ProjectSnapshot s, IReadOnlyCollection<ChainRow> rows, DateTime today, int weeks = 12)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        var wirs = s.Wirs.ToDictionary(w => w.Id);
        var start = WeekStart(today).AddDays(-7 * (weeks - 1));
        var buckets = new double[weeks];
        foreach (var wl in s.WirLines)
        {
            if (!ids.Contains(wl.LineId) || !wirs.TryGetValue(wl.WirId, out var w) || !ProgressRules.CountsAsProgress(w, wl)) continue;
            var at = w.ApprovedAt ?? w.SubmittedAt;
            var i = (int)Math.Floor((at.Date - start).TotalDays / 7.0);
            if (i >= 0 && i < weeks) buckets[i] += wl.Qty;
        }
        return buckets.ToList();
    }

    /// <summary>Weekly count of submitted WIRs (sparkline).</summary>
    public static List<double> WeeklyWirCount(IEnumerable<Wir> wirs, DateTime today, int weeks = 12)
    {
        var start = WeekStart(today).AddDays(-7 * (weeks - 1));
        var b = new double[weeks];
        foreach (var w in wirs)
        {
            var i = (int)Math.Floor((w.SubmittedAt.Date - start).TotalDays / 7.0);
            if (i >= 0 && i < weeks) b[i]++;
        }
        return b.ToList();
    }

    public static double AvgCertificationDays(IEnumerable<Invoice> invoices)
    {
        var d = invoices.Where(i => i.CertifiedAt.HasValue).Select(i => (i.CertifiedAt!.Value - i.InvDate).TotalDays).ToList();
        return d.Count == 0 ? 0 : d.Average();
    }
}
