using System.Globalization;
using System.Security;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Plans;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Raffaello.Core.HeadOffice;

/// <summary>What the head-office tracker covers.</summary>
public sealed class TrackerExportScope
{
    /// <summary>Subcontractor of the invoice (null = all subcontractors).</summary>
    public string? Subcontractor { get; init; }
    /// <summary>Invoice no. ("this invoice" column, ledger up to it). Null = everything to date.</summary>
    public int? InvoiceNo { get; init; }
    /// <summary>LEDGER sheet lists every subcontractor's lines (true) or only the invoice's subcontractor (false).</summary>
    public bool LedgerAllSubcontractors { get; init; }
    public string ContractNo { get; init; } = "";
    public string ProjectName { get; init; } = "Raffles Hotel & Branded Residence";
    /// <summary>Stamp written in the file (also the document properties) - fixed so the same inputs give the same file.</summary>
    public DateTime AsOf { get; init; } = DateTime.Today;
    public string Password { get; init; } = "RAFFAELLO";
    /// <summary>[phase6] Total budget for the plan images (re-encoded greyscale / smaller when over it). 0 = keep the originals.</summary>
    public long MaxImageBytes { get; init; } = DefaultMaxImageBytes;
    public const long DefaultMaxImageBytes = 1_400_000;
    public string Title => Subcontractor is null ? "ALL SUBCONTRACTORS" : $"{Subcontractor}{(InvoiceNo is { } n ? $" INV-{n:00}" : "")}";
}

public sealed record TrackerExportResult(string Path, long Bytes, int Plans, int Shapes, int RoomsWithoutShape, int RoomBlocks, int LedgerLines, int ControlIssues);

/// <summary>
/// Head-office copy of the BRANDED_MEP_TRACKER: values only, macro-free .xlsx, every sheet protected (select / filter / sort allowed),
/// workbook structure protected. PLANS carries each level image with one DrawingML shape per room (RM_&lt;room&gt;-&lt;level&gt;) filled with
/// the status colour at export time and hyperlinked to the room's block in ROOM DETAILS (which links back to the plan).
/// </summary>
public static class TrackerExporter
{
    public const string Dashboard = "DASHBOARD", PlansSheet = "PLANS", RoomDetails = "ROOM DETAILS", LedgerSheet = "LEDGER", ProjectQty = "PROJECT QTY", Control = "CONTROL", Summary = "INVOICE SUMMARY";
    private const long RowEmu = 190500;      // 15 pt rows
    private const double RowPt = 15;
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");
    private static readonly XLColor Red = XLColor.FromHtml("#8B0000");

    public static string RoomAnchorName(string room) => "RD_" + new string(room.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    public static TrackerExportResult Export(string path, ProjectSnapshot s, IReadOnlyList<PlanImage> plans, TrackerExportScope scope, InvoiceBuild? invoice = null)
    {
        var upTo = scope.InvoiceNo;
        var claimsToDate = LedgerRules.Effective(s.Claims.Where(c => upTo is null || c.InvoiceNo <= upTo)).ToList();
        var info = RoomStatusCalc.Compute(new ProjectSnapshot { Rooms = s.Rooms, RoomQtys = s.RoomQtys, Claims = claimsToDate });
        var bal = LedgerRules.Balances(s.RoomQtys, claimsToDate);
        var thisInv = claimsToDate.Where(c => scope.Subcontractor != null && c.Subcontractor.Equals(scope.Subcontractor, StringComparison.OrdinalIgnoreCase) && (upTo is null || c.InvoiceNo == upTo))
            .GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty));
        var roomRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shapesByPlan = s.RoomShapes.GroupBy(r => r.Plan, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int controlIssues;
        int ledgerLines;

        using (var wb = new XLWorkbook())
        {
            wb.Properties.Author = "Raffaello";
            wb.Properties.Title = $"Head-office tracker - {scope.Title}";
            wb.Properties.Created = scope.AsOf;
            wb.Properties.Modified = scope.AsOf;
            var dash = wb.Worksheets.Add(Dashboard);
            var plan = wb.Worksheets.Add(PlansSheet);
            var rd = wb.Worksheets.Add(RoomDetails);
            var led = wb.Worksheets.Add(LedgerSheet);
            var pq = wb.Worksheets.Add(ProjectQty);
            var ctl = wb.Worksheets.Add(Control);
            var sum = wb.Worksheets.Add(Summary);

            WriteRoomDetails(rd, s, info, bal, thisInv, scope, roomRows);
            foreach (var (room, row) in roomRows) wb.DefinedNames.Add(RoomAnchorName(room), rd.Cell(row, 1).AsRange());
            WriteDashboard(dash, claimsToDate, bal, info, scope);
            WritePlansHeader(plan, scope);
            ledgerLines = WriteLedger(led, claimsToDate, scope);
            WriteProjectQty(pq, s);
            controlIssues = WriteControl(ctl, s, claimsToDate, bal);
            WriteInvoiceSummary(sum, invoice, scope);
            // plan captions: rows below each image are computed the same way the drawing is laid out
            var y = 4 * RowEmu;
            foreach (var p in plans.Where(p => p.Png != null && p.Width > 0 && p.Height > 0).OrderBy(p => p.Plan))
            {
                var row = (int)(y / RowEmu) + 1;
                plan.Cell(row, 1).Value = $"{p.Plan}  -  {p.Caption}";
                plan.Cell(row, 1).Style.Font.Bold = true; plan.Cell(row, 1).Style.Font.FontSize = 12;
                y += RowEmu + Scale(p).Cy + 2 * RowEmu;
            }
            var noShape = info.Keys.Where(k => s.Rooms.Any(r => r.Code.Equals(k, StringComparison.OrdinalIgnoreCase)) && !s.RoomShapes.Any(r => r.Room.Equals(k, StringComparison.OrdinalIgnoreCase))).OrderBy(k => k).ToList();
            plan.Cell(2, 12).Value = noShape.Count == 0 ? "" : "ROOMS WITHOUT A SHAPE: " + string.Join(", ", noShape);

            foreach (var ws in wb.Worksheets)
            {
                ws.Protect(scope.Password)
                    .AllowElement(XLSheetProtectionElements.SelectEverything)
                    .AllowElement(XLSheetProtectionElements.AutoFilter)
                    .AllowElement(XLSheetProtectionElements.Sort);
            }
            wb.Protect(scope.Password);
            wb.SaveAs(path);
        }

        var drawPlans = ShrinkPlans(plans, scope.MaxImageBytes);
        var (planCount, shapeCount) = AddPlanDrawing(path, drawPlans, shapesByPlan, info);
        var noShapes = s.Rooms.Count(r => !s.RoomShapes.Any(x => x.Room.Equals(r.Code, StringComparison.OrdinalIgnoreCase)));
        Packaging.Deterministic.NormalizeZip(path, scope.AsOf);
        return new TrackerExportResult(path, new FileInfo(path).Length, planCount, shapeCount, noShapes, roomRows.Count, ledgerLines, controlIssues);
    }

    // ------------------------------------------------------------------ sheets

    private static void Title(IXLWorksheet ws, string title, TrackerExportScope scope)
    {
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 16; ws.Cell(1, 1).Style.Font.FontColor = Red;
        ws.Cell(2, 1).Value = $"{scope.ProjectName}  |  {scope.ContractNo}  |  {scope.Title}  |  as of {scope.AsOf:dd MMM yyyy}  |  values only - read-only copy for head office";
        ws.Cell(2, 1).Style.Font.Italic = true;
    }

    private static int Header(IXLWorksheet ws, int row, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(row, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = Grey; c.Style.Font.FontColor = XLColor.Black;
            c.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }
        return row + 1;
    }

    private static void WriteDashboard(IXLWorksheet ws, List<ClaimLine> claims, Dictionary<string, RoomBalance> bal, Dictionary<string, RoomPlanInfo> info, TrackerExportScope scope)
    {
        Title(ws, "DASHBOARD", scope);
        var r = Header(ws, 4, "SUBCONTRACTOR", "LINES", "PLAN QTY", "QTY AFTER SITE % x WIR %");
        var first = r;
        foreach (var g in claims.GroupBy(c => c.Subcontractor).OrderBy(g => g.Key))
        {
            ws.Cell(r, 1).Value = g.Key; ws.Cell(r, 2).Value = g.Count(); ws.Cell(r, 3).Value = Math.Round(g.Sum(c => c.Qty), 2); ws.Cell(r, 4).Value = Math.Round(g.Sum(c => c.QtyAfterWir), 2);
            r++;
        }
        if (r > first) ws.Range(first, 4, r - 1, 4).AddConditionalFormat().DataBar(XLColor.FromHtml("#8B0000")).LowestValue().HighestValue();

        r += 1;
        r = Header(ws, r, "STAGE", "PROJECT QTY", "CLAIMED (ALL SUBS)", "REMAINING", "% USED", "KEYS OVER CAP");
        first = r;
        foreach (var g in bal.Values.GroupBy(b => b.Stage).OrderBy(g => g.Key))
        {
            var cap = g.Sum(b => b.ProjectQty);
            var within = g.Where(b => b.HasCap).Sum(b => Math.Clamp(b.Claimed, 0, b.ProjectQty));
            ws.Cell(r, 1).Value = g.Key; ws.Cell(r, 2).Value = Math.Round(cap, 2); ws.Cell(r, 3).Value = Math.Round(g.Sum(b => b.Claimed), 2);
            ws.Cell(r, 4).Value = Math.Round(cap - within, 2); ws.Cell(r, 5).Value = cap <= 0 ? 0 : within / cap; ws.Cell(r, 5).Style.NumberFormat.Format = "0.0%";
            ws.Cell(r, 6).Value = g.Count(b => b.HasCap && b.IsOver);
            r++;
        }
        if (r > first) ws.Range(first, 5, r - 1, 5).AddConditionalFormat().DataBar(XLColor.FromHtml("#A6A6A6")).Minimum(XLCFContentType.Number, 0).Maximum(XLCFContentType.Number, 1);
        ws.Cell(r, 1).Value = "Stages are separate quantities - never added together.";
        ws.Cell(r, 1).Style.Font.Italic = true;

        r += 2;
        r = Header(ws, r, "SYSTEM / ITEM", "PROJECT QTY", "CLAIMED (ALL SUBS)", "% USED");
        first = r;
        foreach (var g in bal.Values.GroupBy(b => b.Item).OrderBy(g => g.Key))
        {
            var cap = g.Sum(b => b.ProjectQty);
            var within = g.Where(b => b.HasCap).Sum(b => Math.Clamp(b.Claimed, 0, b.ProjectQty));
            ws.Cell(r, 1).Value = g.Key; ws.Cell(r, 2).Value = Math.Round(cap, 2); ws.Cell(r, 3).Value = Math.Round(g.Sum(b => b.Claimed), 2);
            ws.Cell(r, 4).Value = cap <= 0 ? 0 : within / cap; ws.Cell(r, 4).Style.NumberFormat.Format = "0.0%";
            r++;
        }
        if (r > first) ws.Range(first, 4, r - 1, 4).AddConditionalFormat().DataBar(XLColor.FromHtml("#A6A6A6")).Minimum(XLCFContentType.Number, 0).Maximum(XLCFContentType.Number, 1);

        r += 1;
        r = Header(ws, r, "ROOM STATUS", "ROOMS");
        foreach (var st in RoomStatusKinds.All)
        {
            ws.Cell(r, 1).Value = st; ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + RoomStatusKinds.Colour(st));
            ws.Cell(r, 1).Style.Font.FontColor = st is RoomStatusKinds.InProgress or RoomStatusKinds.NotStarted ? XLColor.Black : XLColor.White;
            ws.Cell(r, 2).Value = info.Values.Count(i => i.Status == st);
            r++;
        }
        ws.Column(1).Width = 26; ws.Columns(2, 6).Width = 18;
    }

    private static void WritePlansHeader(IXLWorksheet ws, TrackerExportScope scope)
    {
        Title(ws, "PLANS  -  click a room to open its details", scope);
        var c = 3;
        foreach (var st in RoomStatusKinds.All)
        {
            var cell = ws.Cell(3, c);
            cell.Value = st;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#" + RoomStatusKinds.Colour(st));
            cell.Style.Font.FontColor = st is RoomStatusKinds.InProgress or RoomStatusKinds.NotStarted ? XLColor.Black : XLColor.White;
            cell.Style.Font.Bold = true;
            c += 2;
        }
        ws.Cell(3, 1).Value = "LEGEND"; ws.Cell(3, 1).Style.Font.Bold = true;
        ws.RowHeight = RowPt;
    }

    private static void WriteRoomDetails(IXLWorksheet ws, ProjectSnapshot s, Dictionary<string, RoomPlanInfo> info, Dictionary<string, RoomBalance> bal,
        Dictionary<string, double> thisInv, TrackerExportScope scope, Dictionary<string, int> roomRows)
    {
        Title(ws, "ROOM DETAILS", scope);
        var rooms = s.Rooms.OrderBy(r => r.Plot).ThenBy(r => r.Floor).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase).Select(r => r.Code)
            .Concat(info.Keys.Where(k => s.Rooms.All(r => !r.Code.Equals(k, StringComparison.OrdinalIgnoreCase))).OrderBy(k => k)).ToList();
        var byRoom = bal.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var r = 4;
        foreach (var code in rooms)
        {
            var i = info.GetValueOrDefault(code);
            var room = s.Rooms.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            roomRows[code] = r;
            var head = ws.Cell(r, 1);
            head.Value = code;
            head.Style.Font.Bold = true; head.Style.Font.FontSize = 12; head.Style.Font.FontColor = XLColor.White; head.Style.Fill.BackgroundColor = Red;
            ws.Cell(r, 2).Value = $"{room?.Level}  |  {room?.RoomType}  |  {room?.AreaType}{(room is null ? "NOT IN ROOMS" : "")}";
            ws.Cell(r, 6).Value = i?.Status ?? RoomStatusKinds.NoProjectQty;
            ws.Cell(r, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#" + RoomStatusKinds.Colour(i?.Status ?? RoomStatusKinds.NoProjectQty));
            ws.Cell(r, 6).Style.Font.FontColor = i?.Status is RoomStatusKinds.InProgress or RoomStatusKinds.NotStarted ? XLColor.Black : XLColor.White;
            var back = ws.Cell(r, 8);
            back.Value = "back to plan";
            back.SetHyperlink(new XLHyperlink($"'{PlansSheet}'!A1"));
            back.Style.Font.Underline = XLFontUnderlineValues.Single; back.Style.Font.FontColor = XLColor.Blue;
            r = Header(ws, r + 1, "STAGE", "ITEM", "PROJECT QTY", "ALL SUBS", "THIS INVOICE", "REMAINING", "% USED", "STATUS");
            foreach (var b in (byRoom.GetValueOrDefault(code) ?? new List<RoomBalance>()).OrderBy(b => StageRank(b.Stage)).ThenBy(b => b.Item))
            {
                ws.Cell(r, 1).Value = b.Stage; ws.Cell(r, 2).Value = b.Item; ws.Cell(r, 3).Value = Math.Round(b.ProjectQty, 2); ws.Cell(r, 4).Value = Math.Round(b.Claimed, 2);
                ws.Cell(r, 5).Value = Math.Round(thisInv.GetValueOrDefault(b.Room + "|" + b.Stage + "|" + b.Item, thisInv.GetValueOrDefault(LedgerKeys.Key(b.Room, b.Stage, b.Item))), 2);
                ws.Cell(r, 6).Value = Math.Round(b.Remaining, 2); ws.Cell(r, 7).Value = b.UsedPct; ws.Cell(r, 7).Style.NumberFormat.Format = "0.0%";
                var st = b.HasCap ? b.IsOver ? "OVER" : b.Remaining <= 1e-9 ? "DONE" : b.Claimed > 0 ? "OPEN" : "NOT STARTED" : "NO PROJECT QTY";
                ws.Cell(r, 8).Value = st;
                if (st == "OVER") { ws.Cell(r, 8).Style.Font.FontColor = Red; ws.Cell(r, 8).Style.Font.Bold = true; }
                r++;
            }
            r++;
        }
        ws.Column(1).Width = 16; ws.Column(2).Width = 22; ws.Columns(3, 7).Width = 13; ws.Column(8).Width = 16;
    }

    private static int StageRank(string s) => Array.IndexOf(new[] { "CEILING", "1ST FIX", "EMT", "FLEXIBLE", "2ND FIX", "3RD FIX", "DB PANELS", "CABLE PULLING", "CABLE TRAY" }, s) is var i && i < 0 ? 99 : i;

    private static int WriteLedger(IXLWorksheet ws, List<ClaimLine> claims, TrackerExportScope scope)
    {
        Title(ws, "LEDGER", scope);
        var rows = claims.Where(c => scope.LedgerAllSubcontractors || scope.Subcontractor is null || c.Subcontractor.Equals(scope.Subcontractor, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Subcontractor).ThenBy(c => c.InvoiceNo).ThenBy(c => c.Room).ThenBy(c => StageRank(c.Stage)).ThenBy(c => c.Item).ToList();
        var h = new[] { "SUBCONTRACTOR", "INVOICE", "STAGE", "FLOOR", "LOCATION", "ITEM", "UNIT", "QTY", "SITE %", "QTY AFTER SITE %", "WIR %", "QTY AFTER WIR %", "WIR NO", "NOTES", "OVER", "OVER REASON",
            ">4.5 M QTY", "HEIGHT STATUS", ">4.5 M ACCEPTED", "15 M APPLIES", "15 M CLAIMED QTY", "ROUTE LENGTH M", "LENGTH GROUPS", "LENGTH REVISED", "LENGTH STATUS", "LENGTH NOTE" };
        var r = Header(ws, 4, h);
        foreach (var c in rows)
        {
            object?[] v = { c.Subcontractor, CumulativeSplit.InvoiceLabel(c), c.Stage, c.Floor, c.Room, c.Item, c.Unit, c.Qty, c.SitePct, c.QtyAfterSite, c.WirPct, c.QtyAfterWir, c.WirNo, c.Notes,
                c.IsOver ? "OVER" : "", c.OverReason, c.QtyAbove45, c.HeightStatus, c.QtyAbove45Accepted, c.LengthApplies ? "YES" : "", c.LengthClaimedQty, c.RouteLengthTotal, c.LengthGroups,
                c.LengthRevisedOverride ?? c.LengthRevisedQty, c.LengthStatus, c.LengthNote };
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i] is double d ? Math.Round(d, 4) : v[i]);
            r++;
        }
        ws.Columns(9, 9).Style.NumberFormat.Format = "0%"; ws.Columns(11, 11).Style.NumberFormat.Format = "0%";
        if (r > 5) ws.Range(4, 1, r - 1, h.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, h.Length).Width = 14; ws.Column(14).Width = 40;
        return rows.Count;
    }

    private static void WriteProjectQty(IXLWorksheet ws, ProjectSnapshot s)
    {
        Title(ws, "PROJECT QTY", new TrackerExportScope());
        ws.Cell(2, 1).Value = "Project quantity per room x stage x item - the cap on all subcontractors together.";
        var r = Header(ws, 4, "LOCATION", "STAGE", "ITEM", "UNIT", "PROJECT QTY");
        foreach (var q in s.RoomQtys.OrderBy(q => q.Room, StringComparer.OrdinalIgnoreCase).ThenBy(q => StageRank(q.Stage)).ThenBy(q => q.Item))
        {
            ws.Cell(r, 1).Value = q.Room; ws.Cell(r, 2).Value = q.Stage; ws.Cell(r, 3).Value = q.Item; ws.Cell(r, 4).Value = q.Unit; ws.Cell(r, 5).Value = q.Qty;
            r++;
        }
        if (r > 5) ws.Range(4, 1, r - 1, 5).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, 5).Width = 16;
    }

    private static int WriteControl(IXLWorksheet ws, ProjectSnapshot s, List<ClaimLine> claims, Dictionary<string, RoomBalance> bal)
    {
        Title(ws, "CONTROL", new TrackerExportScope());
        ws.Cell(2, 1).Value = "Exceptions at export time: over cap, unknown location, item not in stage, site % missing, pending checks.";
        var r = Header(ws, 4, "CHECK", "LOCATION", "STAGE", "ITEM", "SUBCONTRACTOR", "INVOICE", "VALUE", "DETAIL");
        var start = r;
        void Add(string check, string loc, string stage, string item, string sub, string inv, double value, string detail)
        {
            object?[] v = { check, loc, stage, item, sub, inv, Math.Round(value, 2), detail };
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
            r++;
        }
        foreach (var b in bal.Values.Where(b => b.HasCap && b.IsOver).OrderByDescending(b => b.Claimed - b.ProjectQty))
            Add("OVER CAP", b.Room, b.Stage, b.Item, string.Join(" ", b.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}")), "", b.Claimed - b.ProjectQty, $"claimed {b.Claimed:0.##} of {b.ProjectQty:0.##}");
        var rooms = s.Rooms.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var g in claims.Where(c => !rooms.Contains(c.Room)).GroupBy(c => (c.Room, c.Subcontractor)))
            Add("UNKNOWN LOCATION", g.Key.Room, "", "", g.Key.Subcontractor, "", g.Sum(c => c.Qty), $"{g.Count()} lines");
        var stageItems = s.RoomQtys.Select(q => (q.Stage.ToUpperInvariant(), q.Item.ToUpperInvariant())).ToHashSet();
        if (stageItems.Count > 0)
            foreach (var g in claims.Where(c => c.Qty != 0 && !stageItems.Contains((c.Stage.ToUpperInvariant(), c.Item.ToUpperInvariant()))).GroupBy(c => (c.Stage, c.Item, c.Subcontractor)))
                Add("ITEM NOT IN STAGE", "", g.Key.Stage, g.Key.Item, g.Key.Subcontractor, "", g.Sum(c => c.Qty), $"{g.Count()} lines - no PROJECT QTY column for this stage | item");
        foreach (var c in claims.Where(c => c.SitePct <= 0 && c.Qty != 0))
            Add("SITE % MISSING", c.Room, c.Stage, c.Item, c.Subcontractor, CumulativeSplit.InvoiceLabel(c), c.Qty, "");
        foreach (var c in claims.Where(c => HeightCheck.IsPending(c)))
            Add("HEIGHT CHECK PENDING", c.Room, c.Stage, c.Item, c.Subcontractor, CumulativeSplit.InvoiceLabel(c), c.QtyAbove45, "held out of the invoice");
        foreach (var c in claims.Where(c => LengthCheck.IsPending(c)))
            Add("LENGTH CHECK PENDING", c.Room, c.Stage, c.Item, c.Subcontractor, CumulativeSplit.InvoiceLabel(c), c.LengthClaimedQty, "held out of the invoice");
        if (r > start) ws.Range(4, 1, r - 1, 8).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, 7).Width = 18; ws.Column(8).Width = 50;
        return r - start;
    }

    private static void WriteInvoiceSummary(IXLWorksheet ws, InvoiceBuild? b, TrackerExportScope scope)
    {
        Title(ws, "INVOICE SUMMARY", scope);
        if (b is null) { ws.Cell(4, 1).Value = "No invoice in this export (tracker exported on demand)."; return; }
        var t = b.Totals;
        object[][] head =
        {
            new object[] { "INVOICE", b.Header.Title }, new object[] { "STATUS", b.Header.Status }, new object[] { "ACONEX WORKFLOW", b.Header.AconexWorkflowNo },
            new object[] { "SUBCONTRACT VALUE", Math.Round(t.SubcontractValue, 2) }, new object[] { "PREVIOUS GROSS", Math.Round(t.PrevGross, 2) },
            new object[] { "THIS PERIOD GROSS", Math.Round(t.CurrGross, 2) }, new object[] { "CUMULATIVE GROSS", Math.Round(t.CumGross, 2) },
            new object[] { $"RETENTION {t.RetentionPct:P0}", Math.Round(t.CurrRetention, 2) }, new object[] { "NET THIS PERIOD", Math.Round(t.NetCurr, 2) },
            new object[] { "VAT 15 %", Math.Round(t.VatCurr, 2) }, new object[] { "NET INCL. VAT", Math.Round(t.NetInclVatCurr, 2) },
        };
        var r = 4;
        foreach (var h in head)
        {
            ws.Cell(r, 1).Value = (string)h[0]; ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = XLCellValue.FromObject(h[1]); if (h[1] is double) ws.Cell(r, 2).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }
        r++;
        r = Header(ws, r, "ITEM", "BOQ CODE", "DESCRIPTION", "UNIT", "RATE", "STAGE %", "PREV QTY", "CURR QTY", "CUM QTY", "CURR SAR", "CUM SAR");
        foreach (var l in InvoicePdfExporter.FilteredRows(b.Lines).Where(l => l.Kind == "ITEM"))
        {
            object?[] v = { l.ItemNo, l.BoqCode, l.BoqDescription.Length > 0 ? l.BoqDescription : l.Description, l.Unit, l.Rate, l.StagePct, Math.Round(l.PrevQty, 4), Math.Round(l.CurrQty, 4), Math.Round(l.CumQty, 4), Math.Round(l.CurrAmount, 2), Math.Round(l.CumAmount, 2) };
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
            r++;
        }
        ws.Column(1).Width = 22; ws.Column(2).Width = 26; ws.Column(3).Width = 50; ws.Columns(4, 11).Width = 13;
    }

    /// <summary>[phase6] Same plans with their PNGs recompressed to share <paramref name="budget"/> (extent / aspect kept: shapes stay aligned).</summary>
    public static List<PlanImage> ShrinkPlans(IReadOnlyList<PlanImage> plans, long budget)
    {
        var withPng = plans.Where(p => p.Png != null && p.Width > 0 && p.Height > 0).ToList();
        if (budget <= 0 || withPng.Sum(p => (long)p.Png!.Length) <= budget) return plans.ToList();
        var each = budget / Math.Max(1, withPng.Count);
        return plans.Select(p => p.Png is null ? p : new PlanImage
        {
            Id = p.Id, Building = p.Building, Plan = p.Plan, Name = p.Name, Caption = p.Caption, X = p.X, Y = p.Y, Width = p.Width, Height = p.Height,
            Png = Imaging.PngLite.Shrink(p.Png, each),
        }).ToList();
    }

    // ------------------------------------------------------------------ DrawingML plans

    private static (long Cx, long Cy) Scale(PlanImage p)
    {
        const long maxWidth = 9_000_000;   // ~25 cm on the sheet
        var k = Math.Min(1.0, maxWidth / (double)p.Width);
        return ((long)(p.Width * k), (long)(p.Height * k));
    }

    private static (int Plans, int Shapes) AddPlanDrawing(string path, IReadOnlyList<PlanImage> plans, Dictionary<string, List<RoomShape>> shapesByPlan, Dictionary<string, RoomPlanInfo> info)
    {
        using var doc = SpreadsheetDocument.Open(path, true);
        var wbPart = doc.WorkbookPart!;
        var sheet = wbPart.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().First(x => x.Name == PlansSheet);
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
        var dp = wsPart.DrawingsPart ?? wsPart.AddNewPart<DrawingsPart>("rIdPlans");
        dp.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
        var wd = dp.WorksheetDrawing;
        if (wsPart.Worksheet.Elements<DocumentFormat.OpenXml.Spreadsheet.Drawing>().FirstOrDefault() is null)
        {
            var drawing = new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = wsPart.GetIdOfPart(dp) };
            var before = wsPart.Worksheet.ChildElements.FirstOrDefault(e => e is DocumentFormat.OpenXml.Spreadsheet.LegacyDrawing or DocumentFormat.OpenXml.Spreadsheet.LegacyDrawingHeaderFooter
                or DocumentFormat.OpenXml.Spreadsheet.Picture or DocumentFormat.OpenXml.Spreadsheet.OleObjects or DocumentFormat.OpenXml.Spreadsheet.Controls
                or DocumentFormat.OpenXml.Spreadsheet.WebPublishItems or DocumentFormat.OpenXml.Spreadsheet.TableParts or DocumentFormat.OpenXml.Spreadsheet.WorksheetExtensionList);
            if (before != null) wsPart.Worksheet.InsertBefore(drawing, before); else wsPart.Worksheet.Append(drawing);
        }
        const string ns = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        uint id = 2;
        int planCount = 0, shapeCount = 0;
        var y = 4 * RowEmu;
        foreach (var p in plans.Where(p => p.Png != null && p.Width > 0 && p.Height > 0).OrderBy(p => p.Plan))
        {
            var (cx, cy) = Scale(p);
            var top = y + RowEmu;
            var img = dp.AddImagePart(ImagePartType.Png, $"rIdImg{planCount + 1}");
            using (var ms = new MemoryStream(p.Png!)) img.FeedData(ms);
            var imgId = dp.GetIdOfPart(img);
            wd.Append(new Xdr.AbsoluteAnchor($"<xdr:absoluteAnchor {ns}><xdr:pos x=\"0\" y=\"{top}\"/><xdr:ext cx=\"{cx}\" cy=\"{cy}\"/>" +
                $"<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"{id++}\" name=\"PLAN_{Esc(p.Plan)}\" descr=\"{Esc(p.Caption)}\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>" +
                $"<xdr:blipFill><a:blip r:embed=\"{imgId}\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>" +
                $"<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"{top}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:absoluteAnchor>"));
            planCount++;
            foreach (var shape in (shapesByPlan.GetValueOrDefault(p.Plan) ?? new List<RoomShape>()).OrderBy(x => x.Room, StringComparer.OrdinalIgnoreCase))
            {
                var polys = RoomStatusCalc.Polygons(shape.Polygons);
                if (polys.Count == 0) continue;
                var pts = polys.SelectMany(q => q).ToList();
                double minX = pts.Min(q => q.X), minY = pts.Min(q => q.Y), maxX = pts.Max(q => q.X), maxY = pts.Max(q => q.Y);
                long ox = (long)(minX * cx), oy = top + (long)(minY * cy), w = Math.Max(1, (long)((maxX - minX) * cx)), h = Math.Max(1, (long)((maxY - minY) * cy));
                var st = info.GetValueOrDefault(shape.Room)?.Status ?? RoomStatusKinds.NoProjectQty;
                var link = dp.AddHyperlinkRelationship(new Uri("#" + RoomAnchorName(shape.Room), UriKind.Relative), false, $"rIdL{shapeCount + 1}");
                var geo = new StringBuilder();
                foreach (var poly in polys)
                {
                    geo.Append($"<a:path w=\"{w}\" h=\"{h}\">");
                    for (var i = 0; i < poly.Count; i++)
                    {
                        var px = (long)((poly[i].X - minX) * cx);
                        var py = (long)((poly[i].Y - minY) * cy);
                        geo.Append(i == 0 ? $"<a:moveTo><a:pt x=\"{px}\" y=\"{py}\"/></a:moveTo>" : $"<a:lnTo><a:pt x=\"{px}\" y=\"{py}\"/></a:lnTo>");
                    }
                    geo.Append("<a:close/></a:path>");
                }
                var name = $"RM_{shape.Room}-{p.Plan}";
                wd.Append(new Xdr.AbsoluteAnchor($"<xdr:absoluteAnchor {ns}><xdr:pos x=\"{ox}\" y=\"{oy}\"/><xdr:ext cx=\"{w}\" cy=\"{h}\"/>" +
                    $"<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"{id++}\" name=\"{Esc(name)}\" descr=\"{Esc(shape.Room + " " + st)}\"><a:hlinkClick r:id=\"{link.Id}\" tooltip=\"{Esc(shape.Room + " - " + st)}\"/></xdr:cNvPr><xdr:cNvSpPr/></xdr:nvSpPr>" +
                    $"<xdr:spPr><a:xfrm><a:off x=\"{ox}\" y=\"{oy}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm>" +
                    $"<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst>{geo}</a:pathLst></a:custGeom>" +
                    $"<a:solidFill><a:srgbClr val=\"{RoomStatusKinds.Colour(st)}\"><a:alpha val=\"55000\"/></a:srgbClr></a:solidFill>" +
                    $"<a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></xdr:spPr>" +
                    $"<xdr:txBody><a:bodyPr vertOverflow=\"overflow\" horzOverflow=\"overflow\" rtlCol=\"0\" anchor=\"ctr\"/><a:lstStyle/><a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"en-US\" sz=\"600\" b=\"1\"/><a:t>{Esc(shape.Room)}</a:t></a:r></a:p></xdr:txBody>" +
                    $"</xdr:sp><xdr:clientData/></xdr:absoluteAnchor>"));
                shapeCount++;
            }
            y += RowEmu + cy + 2 * RowEmu;
        }
        dp.WorksheetDrawing.Save();
        wsPart.Worksheet.Save();
        return (planCount, shapeCount);
    }

    private static string Esc(string s) => SecurityElement.Escape(s ?? "") ?? "";
}
