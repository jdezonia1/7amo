namespace Raffaello.Core.Insights;

/// <summary>
/// The simple programme behind the forecasts: planned start / finish per area x stage (x system) from the editable table, or a default
/// spread when the table is empty (1st fix / ceiling first, 2nd fix in the middle, final fix last, between the project start and the
/// planned finish). A delay scenario pushes every finish that is still ahead by x weeks (and stretches the remaining work).
/// </summary>
public sealed class ProgrammeModel
{
    public DateTime ProjectStart { get; }
    public DateTime PlannedFinish { get; }
    public DateTime Today { get; }
    public int DelayWeeks { get; }
    public IReadOnlyList<InsightProgramme> Rows { get; }
    public bool IsDefault => Rows.Count == 0;

    public ProgrammeModel(IEnumerable<InsightProgramme> rows, DateTime projectStart, DateTime plannedFinish, DateTime today, int delayWeeks = 0, string? building = null)
    {
        Rows = rows.Where(r => r.PlannedFinish > r.PlannedStart && (building is null || string.IsNullOrEmpty(r.Building) || string.Equals(r.Building, building, StringComparison.OrdinalIgnoreCase))).ToList();
        ProjectStart = projectStart.Date;
        PlannedFinish = plannedFinish.Date > projectStart.Date ? plannedFinish.Date : projectStart.Date.AddDays(7);
        Today = today.Date;
        DelayWeeks = Math.Max(0, delayWeeks);
    }

    /// <summary>Default phase of a ledger stage on the project span (fractions of the span start..finish).</summary>
    public static (double From, double To) DefaultPhase(string stage)
    {
        var s = (stage ?? "").Trim().ToUpperInvariant();
        if (s.StartsWith("1") || s.StartsWith("FIRST") || s == "CEILING" || s == "EMT" || s == "FLEXIBLE") return (0.0, 0.6);
        if (s.StartsWith("2") || s.StartsWith("SECOND") || s.Contains("PULLING") || s.Contains("TRAY")) return (0.25, 0.85);
        if (s.StartsWith("3") || s.StartsWith("FINAL") || s.StartsWith("THIRD") || s.Contains("PANEL") || s.Contains("HANDOVER")) return (0.55, 1.0);
        return (0.0, 1.0);
    }

    private DateTime Shift(DateTime d, bool isStart)
    {
        if (DelayWeeks == 0 || d <= Today) return d;
        var span = Math.Max(1, (PlannedFinish - Today).TotalDays);
        var frac = isStart ? Math.Clamp((d - Today).TotalDays / span, 0, 1) : 1;
        return d.AddDays(DelayWeeks * 7 * frac);
    }

    public DateTime Finish => Shift(PlannedFinish, false);

    /// <summary>Windows (start, finish, weight) that apply to an area x stage x system; weight shares the planned work among them.</summary>
    public List<(DateTime Start, DateTime Finish)> Windows(string stage, string system = "", string area = "")
    {
        static bool Eq(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        var rows = Rows.Where(r => (r.Stage.Length == 0 || Eq(r.Stage, stage)) && (r.System.Length == 0 || system.Length == 0 || Eq(r.System, system))).ToList();
        if (area.Length > 0)
        {
            var exact = rows.Where(r => AreaMatches(r.Area, area)).ToList();
            if (exact.Count > 0) rows = exact;
            else rows = rows.Where(r => r.Area.Length == 0 || Eq(r.Area, "ALL")).ToList();
        }
        if (rows.Count > 0) return rows.Select(r => (Shift(r.PlannedStart, true), Shift(r.PlannedFinish, false))).ToList();
        var (a, b) = DefaultPhase(stage);
        var span = (PlannedFinish - ProjectStart).TotalDays;
        return new() { (Shift(ProjectStart.AddDays(span * a), true), Shift(ProjectStart.AddDays(span * b), false)) };
    }

    /// <summary>Area of a programme row matches a ledger area: exact, "ALL", or a prefix (plot "P2" covers "P2-106").</summary>
    public static bool AreaMatches(string programmeArea, string area)
    {
        var p = (programmeArea ?? "").Trim().ToUpperInvariant();
        var a = (area ?? "").Trim().ToUpperInvariant();
        if (p.Length == 0 || p == "ALL") return true;
        return a == p || a.StartsWith(p + "-", StringComparison.Ordinal) || a.StartsWith(p + " ", StringComparison.Ordinal);
    }

    /// <summary>Planned fraction complete (0..1) at a date: linear within each window, windows averaged.</summary>
    public double PlannedFraction(string stage, DateTime at, string system = "", string area = "")
    {
        var w = Windows(stage, system, area);
        return w.Count == 0 ? 0 : w.Average(x => Linear(x.Start, x.Finish, at));
    }

    public static double Linear(DateTime start, DateTime finish, DateTime at)
    {
        if (at <= start) return 0;
        if (at >= finish) return 1;
        return (at - start).TotalDays / Math.Max(1, (finish - start).TotalDays);
    }

    /// <summary>Planned finish for an area (latest finish of its windows over all stages).</summary>
    public DateTime FinishOf(string area, IEnumerable<string> stages) =>
        stages.SelectMany(st => Windows(st, "", area)).Select(w => w.Finish).DefaultIfEmpty(Finish).Max();

    public static DateTime MonthOf(DateTime d) => new(d.Year, d.Month, 1);
}
