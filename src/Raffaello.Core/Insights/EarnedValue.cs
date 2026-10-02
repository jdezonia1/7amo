using Raffaello.Core.Analytics;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Insights;

public sealed class EvInputs
{
    public required ProjectSnapshot Project { get; init; }
    public InsightsData Data { get; init; } = new();
    public DateTime Today { get; init; } = DateTime.Today;
    public DateTime ProjectStart { get; init; }
    public DateTime PlannedFinish { get; init; }
    public string? Building { get; init; }
}

/// <summary>Planned vs actual for one stage x system (points within PROJECT QTY; stages are never added together).</summary>
public sealed record EvRow(string Stage, string System, double PlanPoints, double ActualPoints, double PlannedPct, double ActualPct)
{
    public double Spi => PlannedPct > 1e-9 ? ActualPct / PlannedPct : ActualPct > 0 ? 1 : 0;
    public double VariancePct => ActualPct - PlannedPct;
    public double EarnedPoints => ActualPoints;
    public double PlannedPoints => PlanPoints * PlannedPct;
}

public sealed record ProductivityPeriod(int InvoiceNo, DateTime? PeriodEnd, double Points, double? Weeks, double? PointsPerWeek);

public sealed class ProductivityRow
{
    public string Subcontractor { get; init; } = "";
    public List<ProductivityPeriod> Periods { get; init; } = new();
    public double TotalPoints => Periods.Sum(p => p.Points);
    public double? AvgPerWeek { get; init; }
    public double? LastPerWeek => Periods.LastOrDefault(p => p.PointsPerWeek.HasValue)?.PointsPerWeek;
    public double AvgPerInvoice => Periods.Count == 0 ? 0 : TotalPoints / Periods.Count;
    /// <summary>Change per period relative to the mean rate (-0.25 = falling 25 % per invoice period).</summary>
    public double Trend { get; init; }
    public string TrendText => Periods.Count < 3 ? "-" : Trend > 0.1 ? "RISING" : Trend < -0.1 ? "FALLING" : "STEADY";
    public bool Dated => Periods.Any(p => p.PeriodEnd.HasValue);
}

public sealed record AreaForecast(string Area, double PlanPoints, double ActualPct, DateTime PlannedFinish, DateTime? ForecastFinish, string Method, int Points)
{
    public double? SlipDays => ForecastFinish is DateTime f ? (f - PlannedFinish).TotalDays : null;
}

public sealed record EarlyWarning(InsightSeverity Severity, string Subject, string Message, string Action);

public sealed class EvResult
{
    public List<EvRow> Rows { get; } = new();
    public List<ProductivityRow> Productivity { get; } = new();
    public List<AreaForecast> Areas { get; } = new();
    public List<EarlyWarning> Warnings { get; } = new();
    /// <summary>Weekly planned vs actual (stages averaged, never added) - extends the dashboard S-curve to the ledger.</summary>
    public List<WeeklyPoint> Curve { get; } = new();
    public int UndatedLines { get; set; }
    public int DatedLines { get; set; }
    public List<string> Notes { get; } = new();
    public double Spi => Rows.Where(r => r.PlanPoints > 0).Select(r => r.Spi * r.PlanPoints).DefaultIfEmpty().Sum() / Math.Max(1e-9, Rows.Where(r => r.PlanPoints > 0).Sum(r => r.PlanPoints));
}

/// <summary>
/// Earned value and productivity (roadmap 10). Actual = ledger claims after SITE %, clamped to PROJECT QTY per room x stage x item;
/// planned = the programme (or the default spread). Claims are dated by invoice period (insights table / invoice revisions) because
/// the tracker ledger carries no dates; undated claims count at today.
/// </summary>
public static class EarnedValue
{
    private sealed record Pt(string Room, string Area, string Stage, string Item, string Sub, int Inv, double Qty, DateTime? At);

    public static EvResult Build(EvInputs i)
    {
        var s = i.Project;
        var today = i.Today.Date;
        bool In(string? b) => i.Building is null || string.IsNullOrEmpty(b) || string.Equals(b, i.Building, StringComparison.OrdinalIgnoreCase);
        var prog = new ProgrammeModel(i.Data.Programme, i.ProjectStart, i.PlannedFinish, today, 0, i.Building);
        var dating = new ClaimDating(s, i.Data.InvoicePeriods);
        var rooms = s.Rooms.GroupBy(r => r.Code.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        string AreaOf(string room, string floor) => rooms.TryGetValue(room.Trim().ToUpperInvariant(), out var r) && r.Level.Length > 0 ? r.Level : floor.Length > 0 ? floor : "OTHER";
        var res = new EvResult();

        var plan = s.RoomQtys.Where(q => In(q.Building) && q.Qty > 0).ToList();
        var caps = plan.GroupBy(q => q.Key).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        var claims = LedgerRules.Effective(s.Claims.Where(c => In(c.Building))).Where(c => !c.Rework).ToList();
        // date each line, then clamp to the cap per key in date order (claims beyond PROJECT QTY earn nothing)
        var pts = new List<Pt>();
        foreach (var key in claims.GroupBy(c => c.Key))
        {
            var cap = caps.GetValueOrDefault(key.Key);
            if (cap <= 0) continue;
            double used = 0;
            foreach (var c in key.Select(c => (C: c, At: dating.Of(c)?.Date)).OrderBy(x => x.At ?? DateTime.MaxValue).ThenBy(x => x.C.InvoiceNo))
            {
                var q = c.C.Qty * c.C.SitePct;
                var take = Math.Clamp(q, -used, Math.Max(0, cap - used));
                used += take;
                if (Math.Abs(take) < 1e-9) continue;
                pts.Add(new Pt(c.C.Room, AreaOf(c.C.Room, c.C.Floor), c.C.Stage.Trim().ToUpperInvariant(), c.C.Item.Trim().ToUpperInvariant(), c.C.Subcontractor.Trim(), c.C.InvoiceNo, take, c.At));
                if (c.At.HasValue) res.DatedLines++; else res.UndatedLines++;
            }
        }
        if (res.UndatedLines > 0)
            res.Notes.Add($"{res.UndatedLines:N0} ledger lines have no date (tracker import without invoice dates) and count at today. Enter the invoice period dates to see the trend.");
        if (prog.IsDefault) res.Notes.Add("Planned progress uses the default spread (no programme entered).");

        // ------------------------------------------------ planned vs actual per stage x system
        foreach (var g in plan.GroupBy(q => (Stage: q.Stage.Trim().ToUpperInvariant(), Item: q.Item.Trim().ToUpperInvariant())).OrderBy(g => g.Key.Stage).ThenBy(g => g.Key.Item))
        {
            var planPts = g.Sum(q => q.Qty);
            var act = pts.Where(p => p.Stage == g.Key.Stage && p.Item == g.Key.Item).Sum(p => p.Qty);
            res.Rows.Add(new EvRow(g.Key.Stage, g.Key.Item, planPts, act, prog.PlannedFraction(g.Key.Stage, today, g.Key.Item), planPts > 0 ? Math.Clamp(act / planPts, 0, 1) : 0));
        }

        // ------------------------------------------------ S-curve (weekly, stages averaged)
        var stagePlan = plan.GroupBy(q => q.Stage.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        if (stagePlan.Count > 0)
        {
            var w0 = ProjectAnalytics.WeekStart(prog.ProjectStart);
            var end = ProjectAnalytics.WeekStart(prog.Finish > today ? prog.Finish : today).AddDays(7);
            for (var wk = w0; wk <= end; wk = wk.AddDays(7))
            {
                var at = wk.AddDays(7);
                var planned = stagePlan.Average(kv => prog.PlannedFraction(kv.Key, at));
                double? actual = null;
                if (wk <= today)
                    actual = stagePlan.Average(kv => kv.Value <= 0 ? 0 : Math.Clamp(pts.Where(p => p.Stage == kv.Key && (p.At ?? today) < at).Sum(p => p.Qty) / kv.Value, 0, 1));
                res.Curve.Add(new WeeklyPoint(wk, planned, actual));
            }
        }

        // ------------------------------------------------ productivity per subcontractor (points per week per invoice period)
        foreach (var sub in pts.GroupBy(p => ClaimDating.Norm(p.Sub)))
        {
            var name = sub.First().Sub;
            var periods = new List<ProductivityPeriod>();
            DateTime? prev = null;
            var invs = sub.GroupBy(p => p.Inv).OrderBy(g => g.Key).Select(g => (Inv: g.Key, Pts: g.ToList(), End: dating.Invoice(name, g.Key)?.Date ?? g.Where(p => p.At.HasValue).Select(p => p.At).Max())).ToList();
            // the first period has no previous invoice: use the typical gap between invoices (else since the project start)
            var ends = invs.Where(x => x.End.HasValue).Select(x => x.End!.Value).ToList();
            var gaps = ends.Zip(ends.Skip(1), (x, y) => (y - x).TotalDays).Where(g => g > 0).ToList();
            var firstGap = gaps.Count > 0 ? RobustStats.Median(gaps) : (double?)null;
            foreach (var inv in invs.Select(x => (Key: x.Inv, Pts: x.Pts, x.End)))
            {
                var end = inv.End;
                double? weeks = null;
                if (end is DateTime e)
                {
                    var from = prev ?? (firstGap is double fg ? e.AddDays(-fg) : prog.ProjectStart < e ? prog.ProjectStart : e.AddDays(-28));
                    weeks = Math.Max(1, (e - from).TotalDays / 7.0);
                    prev = e;
                }
                var points = inv.Pts.Sum(p => p.Qty);
                periods.Add(new ProductivityPeriod(inv.Key, end, points, weeks, weeks is double w ? points / w : null));
            }
            var rates = periods.Select(p => p.PointsPerWeek ?? (periods.Any(x => x.PointsPerWeek.HasValue) ? double.NaN : p.Points)).Where(v => !double.IsNaN(v)).ToList();
            var trend = 0.0;
            if (rates.Count >= 3)
            {
                var (slope, _, _) = RobustStats.Regression(Enumerable.Range(0, rates.Count).Select(x => (double)x).ToList(), rates);
                var mean = rates.Average();
                trend = mean > 1e-9 ? slope / mean : 0;
            }
            var dated = periods.Where(p => p.PointsPerWeek.HasValue).ToList();
            res.Productivity.Add(new ProductivityRow
            {
                Subcontractor = name, Periods = periods, Trend = trend,
                AvgPerWeek = dated.Count == 0 ? null : dated.Sum(p => p.Points) / dated.Sum(p => p.Weeks!.Value),
            });
        }
        res.Productivity.Sort((a, b) => b.TotalPoints.CompareTo(a.TotalPoints));

        // ------------------------------------------------ forecast finish per area (regression on the dated progress, else average rate)
        foreach (var area in plan.GroupBy(q => AreaOf(q.Room, "")).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var areaPlan = area.GroupBy(q => q.Stage.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
            var areaPts = pts.Where(p => p.Area == area.Key).ToList();
            double PctAt(DateTime at) => areaPlan.Average(kv => kv.Value <= 0 ? 0 : Math.Clamp(areaPts.Where(p => p.Stage == kv.Key && (p.At ?? today) <= at).Sum(p => p.Qty) / kv.Value, 0, 1));
            var nowPct = PctAt(today);
            var plannedFinish = prog.FinishOf(area.Key, areaPlan.Keys);
            DateTime? forecast = null;
            var method = "";
            var dates = areaPts.Where(p => p.At.HasValue).Select(p => p.At!.Value).Distinct().OrderBy(d => d).ToList();
            if (nowPct >= 0.9999) { forecast = dates.Count > 0 ? dates[^1] : today; method = "COMPLETE"; }
            else if (dates.Count >= 2)
            {
                var xs = dates.Select(d => (d - prog.ProjectStart).TotalDays).ToList();
                var ys = dates.Select(PctAt).ToList();
                var (slope, intercept, _) = RobustStats.Regression(xs, ys);
                if (slope > 1e-9) { var x = (1 - intercept) / slope; if (x < 365 * 20) { forecast = prog.ProjectStart.AddDays(x); method = $"REGRESSION ({dates.Count} dates)"; } }
                else method = "NO PROGRESS TREND";
            }
            if (forecast is null && nowPct > 1e-6 && method.Length == 0)
            {
                var elapsed = Math.Max(7, (today - prog.ProjectStart).TotalDays);
                forecast = today.AddDays(elapsed * (1 - nowPct) / nowPct);
                method = "AVERAGE RATE SINCE START";
            }
            if (forecast is null && method.Length == 0) method = "NOT STARTED";
            res.Areas.Add(new AreaForecast(area.Key, area.Sum(q => q.Qty), nowPct, plannedFinish, forecast, method, areaPts.Count));
        }

        // ------------------------------------------------ early warnings
        var spiLimit = i.Data.Threshold(InsightThresholds.EarlyWarningSpi);
        // against the default spread (no programme entered) a schedule warning is information only
        InsightSeverity Sched(InsightSeverity sev) => prog.IsDefault ? InsightSeverity.Low : sev;
        var basis = prog.IsDefault ? " vs the default spread - enter the programme" : "";
        foreach (var r in res.Rows.Where(r => r.PlanPoints > 0 && r.PlannedPct >= 0.1 && r.Spi < spiLimit).OrderBy(r => r.Spi).Take(15))
            res.Warnings.Add(new EarlyWarning(Sched(r.Spi < spiLimit * 0.6 ? InsightSeverity.High : InsightSeverity.Medium), $"{r.Stage} {r.System}",
                $"{r.ActualPct:P0} done vs {r.PlannedPct:P0} planned (SPI {r.Spi:0.00}; {r.PlanPoints * (r.PlannedPct - r.ActualPct):N0} points behind){basis}.", "Agree a recovery with the subcontractor(s) or re-plan the stage."));
        foreach (var a in res.Areas.Where(a => a.SlipDays > 14).OrderByDescending(a => a.SlipDays).Take(15))
            res.Warnings.Add(new EarlyWarning(Sched(a.SlipDays > 60 ? InsightSeverity.High : InsightSeverity.Medium), a.Area,
                $"Forecast finish {a.ForecastFinish:dd MMM yyyy} vs planned {a.PlannedFinish:dd MMM yyyy} ({a.SlipDays:N0} days late, {a.Method.ToLowerInvariant()}); {a.ActualPct:P0} done.", "Add crews / start the next stage earlier in this area."));
        foreach (var p in res.Productivity.Where(p => p.Periods.Count >= 3 && p.Trend < -0.2))
            res.Warnings.Add(new EarlyWarning(InsightSeverity.Medium, p.Subcontractor, $"Productivity falling {(-p.Trend):P0} per invoice period ({string.Join(" -> ", p.Periods.TakeLast(4).Select(x => (x.PointsPerWeek ?? x.Points).ToString("N0")))} {(p.Dated ? "points / week" : "points / invoice")}).",
                "Ask for the manpower plan; check whether rooms are available to work in."));
        foreach (var a in res.Areas.Where(a => a.Method == "NOT STARTED" && today > a.PlannedFinish.AddDays(-(a.PlannedFinish - prog.ProjectStart).TotalDays * 0.5)).Take(10))
            res.Warnings.Add(new EarlyWarning(InsightSeverity.Low, a.Area, $"No progress claimed yet; planned finish {a.PlannedFinish:dd MMM yyyy}.", "Confirm the area is released to the subcontractor."));
        return res;
    }

    /// <summary>Early warnings as insights (feed the Anomalies page and Needs-today).</summary>
    public static IEnumerable<Anomaly> ToAnomalies(EvResult r) => r.Warnings.Select(w => new Anomaly
    {
        Fingerprint = $"EV|{w.Subject}|{w.Message.Split('(')[0].Trim()}", Kind = "EARLY WARNING", Severity = w.Severity, Title = $"{w.Subject}: {w.Message}",
        Explanation = w.Message, SuggestedAction = w.Action, Score = (int)w.Severity,
    });

    public static List<ExportSheet> Sheets(EvResult r, string scope) => new()
    {
        new()
        {
            Name = "PLANNED VS ACTUAL", Title = "EARNED VALUE - PLANNED VS ACTUAL PER STAGE AND SYSTEM", Subtitle = scope + "  |  points within PROJECT QTY, after SITE %",
            Columns = new() { new("STAGE"), new("SYSTEM"), new("PROJECT QTY", ColumnKind.Number), new("PLANNED %", ColumnKind.Percent), new("ACTUAL %", ColumnKind.Percent), new("PLANNED POINTS", ColumnKind.Number),
                new("EARNED POINTS", ColumnKind.Number), new("VARIANCE %", ColumnKind.Percent), new("SPI", ColumnKind.Number) },
            Rows = r.Rows.Select(x => new object?[] { x.Stage, x.System, x.PlanPoints, x.PlannedPct, x.ActualPct, x.PlannedPoints, x.EarnedPoints, x.VariancePct, x.Spi }).ToList(),
        },
        new()
        {
            Name = "PRODUCTIVITY", Title = "PRODUCTIVITY PER SUBCONTRACTOR", Subtitle = scope + "  |  points per week per invoice period (points per invoice when the periods have no dates)",
            Columns = new() { new("SUBCONTRACTOR", ColumnKind.Text, 26), new("INVOICES", ColumnKind.Integer), new("POINTS", ColumnKind.Number), new("AVG / WEEK", ColumnKind.Number), new("LAST / WEEK", ColumnKind.Number),
                new("AVG / INVOICE", ColumnKind.Number), new("TREND", ColumnKind.Percent), new("TREND TEXT"), new("PERIODS", ColumnKind.Text, 60) },
            Rows = r.Productivity.Select(p => new object?[] { p.Subcontractor, p.Periods.Count, p.TotalPoints, p.AvgPerWeek, p.LastPerWeek, p.AvgPerInvoice, p.Trend, p.TrendText,
                string.Join("  ", p.Periods.Select(x => $"INV {x.InvoiceNo}: {x.Points:N0}" + (x.PointsPerWeek is double w ? $" ({w:N0}/wk)" : ""))) }).ToList(),
        },
        new()
        {
            Name = "AREA FORECAST", Title = "FORECAST FINISH PER AREA", Subtitle = scope,
            Columns = new() { new("AREA", ColumnKind.Text, 22), new("PROJECT QTY", ColumnKind.Number), new("DONE %", ColumnKind.Percent), new("PLANNED FINISH", ColumnKind.Date), new("FORECAST FINISH", ColumnKind.Date),
                new("SLIP DAYS", ColumnKind.Integer), new("METHOD", ColumnKind.Text, 30) },
            Rows = r.Areas.Select(a => new object?[] { a.Area, a.PlanPoints, a.ActualPct, a.PlannedFinish, a.ForecastFinish, a.SlipDays, a.Method }).ToList(),
        },
        new()
        {
            Name = "EARLY WARNINGS", Title = "EARLY WARNINGS", Subtitle = scope,
            Columns = new() { new("SEVERITY"), new("SUBJECT", ColumnKind.Text, 24), new("WARNING", ColumnKind.Text, 90), new("ACTION", ColumnKind.Text, 50) },
            Rows = r.Warnings.Select(w => new object?[] { w.Severity.ToString().ToUpperInvariant(), w.Subject, w.Message, w.Action }).ToList(),
        },
        new()
        {
            Name = "S-CURVE", Title = "S-CURVE - LEDGER PLANNED VS ACTUAL (WEEKLY)", Subtitle = scope + "  |  stages averaged, never added",
            Columns = new() { new("WEEK", ColumnKind.Date), new("PLANNED", ColumnKind.Percent), new("ACTUAL", ColumnKind.Percent) },
            Rows = r.Curve.Select(c => new object?[] { c.WeekStart, c.Planned, c.Actual }).ToList(),
        },
    };
}
