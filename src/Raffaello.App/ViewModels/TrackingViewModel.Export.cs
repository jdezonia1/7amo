using System.Data;
using Raffaello.Core.Export;
using Raffaello.Core.Tracker;

namespace Raffaello.App.ViewModels;

/// <summary>TRACKING: Excel export of the current tab (Ctrl+E / EXPORT), always with RATE and AMOUNT columns and the totals.</summary>
public sealed partial class TrackingViewModel
{
    public override IEnumerable<ExportSheet> ExportCurrentView()
    {
        var M = ColumnKind.Money; var N = ColumnKind.Number; var I = ColumnKind.Integer; var P = ColumnKind.Percent;
        switch (Tab)
        {
            case TLedger:
            case TCables:
            {
                var rows = (Tab == TLedger ? LedgerRows : CableRows).ToList();
                yield return new ExportSheet
                {
                    Name = Tab, Title = $"{Tab} - {BuildingFilter ?? "ALL"}", Subtitle = Tab == TLedger ? LedgerSummary : CablesInfo,
                    Columns = new() { new("SUBCONTRACTOR"), new("INVOICE", I), new("STAGE"), new("FLOOR"), new("LOCATION"), new("ITEM"), new("UNIT"), new("QTY", N), new("SITE %", P), new("QTY AFTER SITE %", N),
                        new("WIR %", P), new("QTY AFTER WIR %", N), new("RATE", M), new("INVOICE AMOUNT", M), new("STAGE %", P), new("PAYABLE", M), new("RATE STATUS"), new("RATE SOURCE / REASON", Width: 40),
                        new("NOTES", Width: 40), new("REWORK?"), new("WORK TYPE"), new("ENTRY CHECK"), new("PROJECT QTY", N), new("ALL SUBCONTRACTORS", N), new("REMAINING", N), new("STATEMENT") },
                    Rows = rows.Select(l => new object?[] { l.Sub, l.Invoice, l.Stage, l.Floor, l.Location, l.Item, l.Unit, l.Qty, l.SitePct, l.QtyAfterSite, l.WirPct, l.QtyAfterWir,
                        l.Rate is double r ? r : "MISSING", l.Amount, l.StagePct, l.Payable, l.RateStatus, l.RateStatus == RateStatus.Missing ? l.RateReason : l.RateSource, l.Notes, l.Rework, l.WorkType, l.EntryCheck,
                        l.ProjectQty, l.AllSubs, l.Remaining, l.Statement }).ToList(),
                    TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, rows.Sum(r => r.Qty), null, rows.Sum(r => r.QtyAfterSite), null, rows.Sum(r => r.QtyAfterWir), null, rows.Sum(r => r.Amount), null, rows.Sum(r => r.Payable) },
                };
                foreach (var by in GroupByOptions) yield return TotalsSheet($"BY {by}", Totals(rows, by));
                break;
            }
            case TRates:
                yield return new ExportSheet
                {
                    Name = "RATES", Title = $"RATES - {BuildingFilter ?? "ALL"}", Subtitle = RatesInfo,
                    Columns = new() { new("SUBCONTRACTOR"), new("BUILDING"), new("STAGE"), new("ITEM"), new("INVOICES"), new("LINES", I), new("QTY", N), new("QTY AFTER WIR", N), new("RATE", M), new("STAGE %", P),
                        new("STATUS"), new("SOURCE", Width: 30), new("REASON", Width: 60), new("AMOUNT", M), new("PAYABLE", M) },
                    Rows = RateRows.Select(r => new object?[] { r.Sub, r.Building, r.Stage, r.Item, r.Invoices, r.Lines, r.Qty, r.QtyAfterWir, r.Rate is double x ? x : "MISSING", r.StagePct, r.RateStatus, r.Source, r.Reason, r.Amount, r.Payable }).ToList(),
                    TotalRow = new object?[] { "TOTAL", null, null, null, null, RateRows.Sum(r => r.Lines), RateRows.Sum(r => r.Qty), RateRows.Sum(r => r.QtyAfterWir), null, null, null, null, null, RateRows.Sum(r => r.Amount), RateRows.Sum(r => r.Payable) },
                };
                break;
            case TEntry:
                foreach (var s in ComparisonSheets()) yield return s;
                break;
            case TRooms:
                yield return new ExportSheet
                {
                    Name = "ROOMS", Title = $"ROOMS - {BuildingFilter ?? "ALL"}",
                    Columns = new() { new("LOCATION"), new("PART"), new("FLOOR"), new("LEVEL"), new("UNIT"), new("UNIT TYPE"), new("PLAN"), new("PROJECT QTY TOTAL", N), new("ALL SUBCONTRACTORS", N), new("REMAINING", N), new("% USED", P),
                        new("STATUS"), new("SUBCONTRACTORS", Width: 40), new("STAGES ACTIVE"), new("OVER CAP", I), new("NO CAP", I), new("AMOUNT", M) },
                    Rows = RoomRows.Select(r => new object?[] { r.Location, r.Part, r.Floor, r.Level, r.Unit, r.UnitType, r.Plan, r.ProjectQty, r.AllSubs, r.Remaining, r.UsedPct, r.State, r.Subs, r.StagesActive, r.OverCap, r.NoCap, r.Amount }).ToList(),
                    TotalRow = new object?[] { "TOTAL", null, null, null, null, null, null, RoomRows.Sum(r => r.ProjectQty), RoomRows.Sum(r => r.AllSubs), RoomRows.Sum(r => r.Remaining), null, null, null, null, RoomRows.Sum(r => r.OverCap), RoomRows.Sum(r => r.NoCap), RoomRows.Sum(r => r.Amount) },
                };
                break;
            case TRoom:
                yield return new ExportSheet
                {
                    Name = "ROOM", Title = $"ROOM - {RoomLocation}", Subtitle = RoomHeader,
                    Columns = new() { new("#", I), new("STAGE"), new("ITEM"), new("PROJECT QTY", N), new("SUBCONTRACTORS QTY", N), new("REMAINING", N), new("% USED", P), new("STATUS"), new("TOP SUBCONTRACTOR"), new("BY SUBCONTRACTOR", Width: 50), new("AMOUNT", M) },
                    Rows = RoomKeys.Select(r => new object?[] { r.No, r.Stage, r.Item, r.ProjectQty, r.SubsQty, r.Remaining, r.UsedPct, r.State, r.TopSub, r.BySubs, r.Amount }).ToList(),
                };
                if (RoomSubsView != null) yield return FromView("ROOM BY SUBCONTRACTOR", RoomSubsView);
                break;
            case TControl:
                yield return new ExportSheet
                {
                    Name = "CONTROL", Title = $"CONTROL - {SelectedCheck?.Check}",
                    Columns = new() { new("LOCATION"), new("STAGE"), new("ITEM"), new("PROJECT QTY", N), new("SUBCONTRACTORS QTY", N), new("REMAINING", N), new("SUBCONTRACTOR"), new("INVOICE"), new("NOTE", Width: 60) },
                    Rows = ControlRows.Select(r => new object?[] { r.Location, r.Stage, r.Item, r.ProjectQty, r.SubsQty, r.Remaining, r.Sub, r.Invoice, r.Note }).ToList(),
                };
                break;
            case TPlans:
                yield return new ExportSheet
                {
                    Name = "PLANS", Title = "PLANS",
                    Columns = new() { new("PLAN"), new("LEVEL"), new("ROOMS", I), new("PROJECT QTY", N), new("CLAIMED", N), new("REMAINING", N), new("% USED", P), new("ROOMS OVER CAP", I), new("ROOMS NOT STARTED", I) },
                    Rows = PlanRows.Select(r => new object?[] { r.Plan, r.Level, r.Rooms, r.ProjectQty, r.Claimed, r.Remaining, r.UsedPct, r.OverRooms, r.NotStarted }).ToList(),
                };
                break;
            case TCap:
                yield return new ExportSheet
                {
                    Name = "CAP", Title = "CAP",
                    Columns = new() { new("KEY"), new("LOCATION"), new("STAGE"), new("ITEM"), new("PROJECT QTY", N), new("SOURCE") },
                    Rows = CapRows.Select(r => new object?[] { r.Key, r.Location, r.Stage, r.Item, r.ProjectQty, r.Source }).ToList(),
                };
                break;
            case TDashboard:
                yield return new ExportSheet
                {
                    Name = "DASHBOARD", Title = $"DASHBOARD - {BuildingFilter ?? "ALL"}", Subtitle = $"PROJECT {KpiProject} | CLAIMED {KpiClaimed} | REMAINING {KpiRemaining} | {KpiPct} | {KpiAmount}",
                    Columns = new() { new("STAGE"), new("PROJECT QTY", N), new("SUBCONTRACTORS QTY", N), new("REMAINING", N), new("% COMPLETE", P), new("COMPLETE", I), new("IN PROGRESS", I), new("NOT STARTED", I), new("OVER CAP", I) },
                    Rows = StageRows.Select(r => new object?[] { r.Stage, r.ProjectQty, r.SubsQty, r.Remaining, r.UsedPct, r.Complete, r.InProgress, r.NotStarted, r.ItemsOver }).ToList(),
                };
                if (SubPerfView != null) yield return FromView("SUBCONTRACTORS", SubPerfView);
                if (PartView != null) yield return FromView("BY PART", PartView);
                break;
            case TProject:
                if (ProjectQtyView != null) yield return FromView("PROJECT QTY", ProjectQtyView);
                break;
            case TSummary:
                if (SummaryView != null) yield return FromView("SUMMARY", SummaryView);
                break;
        }
    }

    private static ExportSheet TotalsSheet(string name, List<TrkTotalRow> rows) => new()
    {
        Name = name, Title = name,
        Columns = new() { new(name.Replace("BY ", "")), new("LINES", ColumnKind.Integer), new("QTY", ColumnKind.Number), new("QTY AFTER WIR", ColumnKind.Number), new("AMOUNT (QTY x RATE)", ColumnKind.Money),
            new("AMOUNT AFTER SITE % x WIR %", ColumnKind.Money), new("PAYABLE (x STAGE %)", ColumnKind.Money), new("LINES MISSING RATE", ColumnKind.Integer) },
        Rows = rows.Where(r => r.Group != "TOTAL").Select(r => new object?[] { r.Group, r.Lines, r.Qty, r.QtyAfterWir, r.Amount, r.AfterWir, r.Payable, r.MissingRate }).ToList(),
        TotalRow = rows.Where(r => r.Group == "TOTAL").Select(r => new object?[] { "TOTAL", r.Lines, r.Qty, r.QtyAfterWir, r.Amount, r.AfterWir, r.Payable, r.MissingRate }).FirstOrDefault(),
    };

    private static ExportSheet FromView(string name, DataView view)
    {
        var cols = view.Table!.Columns.Cast<DataColumn>().ToList();
        return new ExportSheet
        {
            Name = name, Title = name,
            Columns = cols.Select(c => new ExportColumn(c.Caption, c.DataType == typeof(double) ? ColumnKind.Number : c.DataType == typeof(int) ? ColumnKind.Integer : ColumnKind.Text)).ToList(),
            Rows = view.Cast<DataRowView>().Select(r => cols.Select(c => r[c.ColumnName] is DBNull ? null : r[c.ColumnName]).ToArray()).ToList(),
        };
    }

    private IEnumerable<ExportSheet> ComparisonSheets()
    {
        var h = _draft.Header;
        var rows = StatementRows.ToList();
        var head = $"{h.Subcontractor}  |  INV {h.InvoiceNo}  |  STATEMENT {h.StatementNo}  |  {h.Date:dd MMM yyyy}  |  {h.Building}  |  SITE {h.SitePct:P0}  WIR {h.WirPct:P0}  |  {StatementSummary}";
        var M = ColumnKind.Money; var I = ColumnKind.Integer; var N = ColumnKind.Number;
        yield return new ExportSheet
        {
            Name = "COMPARISON", Title = $"STATEMENT {h.StatementNo} - SUBCONTRACTOR CLAIM vs CERTIFIED", Subtitle = head,
            Columns = new() { new("LOCATION"), new("STAGE"), new("ITEM"), new("SUBCONTRACTOR CLAIMED", I), new("REMAINING BEFORE", N), new("MY CERTIFIED", I), new("DIFFERENCE", I), new("STATUS"), new("REASON", Width: 60),
                new("RATE"), new("CLAIMED AMOUNT", M), new("CERTIFIED AMOUNT", M), new("DIFF AMOUNT", M) },
            Rows = rows.Select(r => new object?[] { r.Location, r.Stage, r.Item, r.Claimed, r.RemainingBefore, r.Certified, r.Difference, r.State, r.Why, r.RateText, r.ClaimedAmount, r.CertifiedAmount, r.DiffAmount }).ToList(),
            TotalRow = new object?[] { "TOTAL", null, null, rows.Sum(r => r.Claimed), null, rows.Sum(r => r.Certified), rows.Sum(r => r.Difference), null, null, null, rows.Sum(r => r.ClaimedAmount), rows.Sum(r => r.CertifiedAmount), rows.Sum(r => r.DiffAmount) },
        };
        foreach (var (name, key) in new (string, Func<TrkStatementRow, string>)[] { ("BY LOCATION", r => r.Location), ("BY STAGE", r => r.Stage) })
        {
            var g = rows.GroupBy(key).OrderBy(x => x.Key).ToList();
            yield return new ExportSheet
            {
                Name = name, Title = $"STATEMENT {h.StatementNo} - {name}", Subtitle = head,
                Columns = new() { new(name.Replace("BY ", "")), new("CLAIMED", I), new("CERTIFIED", I), new("DIFFERENCE", I), new("CLAIMED AMOUNT", M), new("CERTIFIED AMOUNT", M), new("DIFF AMOUNT", M) },
                Rows = g.Select(x => new object?[] { x.Key, x.Sum(r => r.Claimed), x.Sum(r => r.Certified), x.Sum(r => r.Difference), x.Sum(r => r.ClaimedAmount), x.Sum(r => r.CertifiedAmount), x.Sum(r => r.DiffAmount) }).ToList(),
                TotalRow = new object?[] { "TOTAL", rows.Sum(r => r.Claimed), rows.Sum(r => r.Certified), rows.Sum(r => r.Difference), rows.Sum(r => r.ClaimedAmount), rows.Sum(r => r.CertifiedAmount), rows.Sum(r => r.DiffAmount) },
            };
        }
    }
}