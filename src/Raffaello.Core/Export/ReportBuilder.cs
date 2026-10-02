using Raffaello.Core.Analytics;
using Raffaello.Core.Chain;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Export;

/// <summary>Builds the standard sheets: chain lines, weekly progress report, claim / certificate.</summary>
public static class ReportBuilder
{
    public static ExportSheet ChainSheet(string name, IEnumerable<ChainRow> rows, string? title = null)
    {
        var list = rows.ToList();
        return new ExportSheet
        {
            Name = name, Title = title,
            Columns = new()
            {
                new("BUILDING"), new("LEVEL"), new("ROOM"), new("SYSTEM"), new("STAGE"), new("ITEM"), new("QS", ColumnKind.Integer),
                new("PROJECT QTY", ColumnKind.Integer), new("GIVEN", ColumnKind.Integer), new("REMAINING", ColumnKind.Integer), new("DONE (WIR)", ColumnKind.Integer),
                new("SITE %", ColumnKind.Percent), new("WIR %", ColumnKind.Percent), new("CLAIMED", ColumnKind.Integer), new("CERTIFIED", ColumnKind.Integer),
                new("DELIVERED", ColumnKind.Integer), new("RATE", ColumnKind.Money), new("CLAIMED VALUE", ColumnKind.Money), new("SUBCONTRACTOR"), new("STATUS"), new("REASON", Width: 60),
            },
            Rows = list.Select(r => new object?[]
            {
                r.Building, r.Level, r.Room, r.System, r.Stage, r.ItemCode, r.Qs, r.ProjectQty, r.Given, r.Remaining, r.Done, r.SitePct, r.WirPct,
                r.Claimed, r.Certified, r.Delivered, r.Rate, r.ClaimedValue, r.Subcontractors, r.Status, r.Reason,
            }).ToList(),
        };
    }

    public static List<ExportSheet> WeeklyReport(ProjectSnapshot s, IReadOnlyList<ChainRow> rows, RuleOptions o, DateTime start, DateTime finish, string user)
    {
        var sheets = new List<ExportSheet>();
        var curve = ProjectAnalytics.SCurve(s, rows, start, finish, o.Today);
        var fc = ProjectAnalytics.ForecastCompletion(curve);
        var actualNow = curve.LastOrDefault(c => c.Actual.HasValue);
        var byStage = ChainMath.TotalsByStage(rows);

        var summary = new List<object?[]>
        {
            new object?[] { "REPORT DATE", o.Today.ToString("dd-MMM-yyyy") },
            new object?[] { "PREPARED BY", user },
            new object?[] { "ACTUAL PROGRESS (MEAN OF STAGES)", actualNow?.Actual?.ToString("P1") },
            new object?[] { "PLANNED PROGRESS", actualNow?.Planned.ToString("P1") },
            new object?[] { "FORECAST COMPLETION", fc.CompletionDate?.ToString("dd-MMM-yyyy") ?? "n/a" },
            new object?[] { "LINES OVER / CHECK / DUE", $"{rows.Count(r => r.Verdict == Verdict.Over)} / {rows.Count(r => r.Verdict == Verdict.Check)} / {rows.Count(r => r.Verdict == Verdict.Due)}" },
            new object?[] { "OPEN WIRS", s.Wirs.Count(w => w.Status == WirStatus.Open && w.Kind == "WIR").ToString() },
            new object?[] { "CERTIFIED TO DATE (SAR)", rows.Sum(r => r.CertifiedValue).ToString("N2") },
            new object?[] { "CLAIMED TO DATE (SAR)", rows.Sum(r => r.ClaimedValue).ToString("N2") },
        };
        sheets.Add(new ExportSheet { Name = "SUMMARY", Title = "RAFFAELLO - WEEKLY PROGRESS REPORT", Subtitle = "Every quantity. One chain.", Columns = new() { new("ITEM", Width: 40), new("VALUE", Width: 30) }, Rows = summary });

        sheets.Add(new ExportSheet
        {
            Name = "BY STAGE", Title = "CHAIN TOTALS BY STAGE (STAGES ARE NEVER SUMMED)",
            Columns = new() { new("STAGE"), new("LINES", ColumnKind.Integer), new("QS", ColumnKind.Integer), new("GIVEN", ColumnKind.Integer), new("DONE", ColumnKind.Integer), new("CLAIMED", ColumnKind.Integer), new("CERTIFIED", ColumnKind.Integer), new("GIVEN %", ColumnKind.Percent), new("DONE %", ColumnKind.Percent) },
            Rows = byStage.Values.Select(t => new object?[] { t.Stage, t.Lines, t.Qs, t.Given, t.Done, t.Claimed, t.Certified, t.GivenPct, t.DonePct }).ToList(),
        });

        sheets.Add(new ExportSheet
        {
            Name = "BY SYSTEM", Title = "PROGRESS BY SYSTEM AND STAGE",
            Columns = new() { new("SYSTEM"), new("STAGE"), new("QS", ColumnKind.Integer), new("GIVEN", ColumnKind.Integer), new("DONE", ColumnKind.Integer), new("CLAIMED", ColumnKind.Integer), new("DONE %", ColumnKind.Percent) },
            Rows = rows.GroupBy(r => (r.System, r.Stage)).OrderBy(g => g.Key.System).ThenBy(g => Array.IndexOf(Stages.All, g.Key.Stage))
                .Select(g => new object?[] { g.Key.System, g.Key.Stage, g.Sum(r => r.Qs), g.Sum(r => r.Given), g.Sum(r => r.Done), g.Sum(r => r.Claimed), g.Sum(r => r.Qs) <= 0 ? 0 : g.Sum(r => r.Done) / g.Sum(r => r.Qs) }).ToList(),
        });

        sheets.Add(new ExportSheet
        {
            Name = "S-CURVE", Title = "PLANNED VS ACTUAL (CUMULATIVE, WEEKLY)",
            Columns = new() { new("WEEK", ColumnKind.Date), new("PLANNED", ColumnKind.Percent), new("ACTUAL", ColumnKind.Percent) },
            Rows = curve.Select(c => new object?[] { c.WeekStart, c.Planned, c.Actual }).ToList(),
        });

        sheets.Add(new ExportSheet
        {
            Name = "NEEDS TODAY", Title = "NEEDS YOU TODAY",
            Columns = new() { new("FLAG"), new("CATEGORY"), new("ITEM", Width: 50), new("DETAIL", Width: 80) },
            Rows = NeedsTodayQueue.Build(s, rows, o).Select(q => new object?[] { q.Tag, q.Category, q.Title, q.Detail }).ToList(),
        });

        sheets.Add(ChainSheet("OVER-CHECK", rows.Where(r => r.Verdict is Verdict.Over or Verdict.Check).OrderByDescending(r => r.Verdict), "LINES FLAGGED OVER / CHECK"));

        sheets.Add(new ExportSheet
        {
            Name = "OPEN WIRS", Title = "OPEN WIRS",
            Columns = new() { new("WIR NO"), new("SUBCONTRACTOR"), new("BUILDING"), new("LEVEL"), new("SYSTEM"), new("STAGE"), new("SUBMITTED", ColumnKind.Date), new("DAYS", ColumnKind.Integer) },
            Rows = s.Wirs.Where(w => w.Status == WirStatus.Open).OrderBy(w => w.SubmittedAt)
                .Select(w => new object?[] { w.WirNo, w.Subcontractor, w.Building, w.Level, w.System, w.Stage, w.SubmittedAt, (o.Today - w.SubmittedAt.Date).TotalDays }).ToList(),
        });

        sheets.Add(new ExportSheet
        {
            Name = "MATERIALS", Title = "DELIVERED VS PO",
            Columns = new() { new("PO NO"), new("LINE", ColumnKind.Integer), new("DESCRIPTION", Width: 40), new("UNIT"), new("PO QTY", ColumnKind.Number), new("DELIVERED", ColumnKind.Number), new("DELIVERED %", ColumnKind.Percent), new("STATUS") },
            Rows = MaterialAnalysis.All(s).SelectMany(p => p.Lines.Select(l => new object?[] { p.Po.PoNo, l.Line.LineNo, l.Line.Description, l.Line.Unit, l.Line.Qty, l.Delivered, l.DeliveredPct, l.Status })).ToList(),
        });
        return sheets;
    }

    /// <summary>One PO with one column per DN, value per DN by SUMPRODUCT, and totals.</summary>
    public static ExportSheet PoProgressSheet(PoProgress p)
    {
        var cols = new List<ExportColumn> { new("LINE", ColumnKind.Integer), new("ITEM"), new("DESCRIPTION", Width: 36), new("UNIT"), new("PO QTY", ColumnKind.Number), new("RATE", ColumnKind.Money) };
        cols.AddRange(p.Dns.Select(d => new ExportColumn($"{d.DnNo} {d.DnDate:dd-MMM}", ColumnKind.Number)));
        cols.AddRange(new[] { new ExportColumn("DELIVERED", ColumnKind.Number), new ExportColumn("REMAINING", ColumnKind.Number), new ExportColumn("DELIVERED VALUE", ColumnKind.Money), new ExportColumn("STATUS") });
        var rows = p.Lines.Select(l =>
        {
            var r = new List<object?> { l.Line.LineNo, l.Line.ItemCode, l.Line.Description, l.Line.Unit, l.Line.Qty, l.Line.Rate };
            r.AddRange(p.Dns.Select(d => (object?)(l.ByDn.TryGetValue(d.Id, out var q) ? q : null)));
            r.AddRange(new object?[] { l.Delivered, l.Remaining, l.DeliveredValue, l.Status });
            return r.ToArray();
        }).ToList();
        var total = new List<object?> { null, "TOTAL (SAR)", $"PO TOTAL CHECK: {(p.TotalCheck.Matches ? "OK" : "DIFF " + p.TotalCheck.Difference.ToString("N2"))}", null, null, p.PoValue };
        total.AddRange(p.Dns.Select(d => (object?)p.DnValue(d.Id)));
        total.AddRange(new object?[] { null, null, p.DeliveredValue, p.OverCount > 0 ? "OVER" : "" });
        return new ExportSheet { Name = p.Po.PoNo, Title = $"{p.Po.PoNo} - {p.Po.Supplier}", Subtitle = p.Po.Description, Columns = cols, Rows = rows, TotalRow = total.ToArray() };
    }
}
