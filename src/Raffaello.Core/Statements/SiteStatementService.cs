using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Statements;

public sealed class StatementImportResult
{
    public string Subcontractor { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public string Hash { get; set; } = "";
    public List<ClaimLine> Claims { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public List<(ClaimLine Line, ClaimCheck Check)> Checks { get; } = new();
    public bool IsDuplicate { get; set; }
    public int Blocked => Checks.Count(c => !c.Check.CanPost);
    public string Summary => $"{Subcontractor} statement {StatementNo}: {Claims.Count} claim lines, {Blocked} over remaining" + (IsDuplicate ? " - DUPLICATE" : "");
}

/// <summary>
/// The standard site statement: a protected workbook per subcontractor listing his rooms x stages with one qty column per system,
/// SITE %, WIR %, WIR no., &gt; 4.5 m and 15 m length request columns and hidden keys. The filled file is imported back into the
/// ledger; the same statement number or identical content is detected as a duplicate.
/// </summary>
public static class SiteStatementService
{
    public static readonly string[] DefaultSystems = { "POWER", "LIGHT", "DATA", "GRMS", "AV", "EMERGENCY LIGHT", "FIRE", "DALI" };
    public static readonly string[] DefaultStages = { "1ST FIX", "2ND FIX", "EMT", "FLEXIBLE", "CEILING" };
    private const string Sheet = "STATEMENT";
    private const string Meta = "META";
    private const int FirstRow = 6;

    private static readonly string[] Tail = { "SITE %", "WIR %", "WIR NO", ">4.5 M SYSTEM", ">4.5 M QTY", "15 M SYSTEM", "15 M CLAIMED QTY", "ROUTE LENGTH M", "NOTES" };

    /// <summary>Rooms in the subcontractor's scope: rooms he already claimed in, or every room of the building when he has none.</summary>
    public static List<Room> ScopeRooms(ProjectSnapshot s, string subcontractor, string building)
    {
        var claimed = s.Claims.Where(c => c.Subcontractor.Equals(subcontractor, StringComparison.OrdinalIgnoreCase)).Select(c => c.Room).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rooms = s.Rooms.Where(r => r.Building == building).ToList();
        var mine = rooms.Where(r => claimed.Contains(r.Code)).ToList();
        return (mine.Count > 0 ? mine : rooms).OrderBy(r => r.Plot).ThenBy(r => r.Floor).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Generate(string path, string subcontractor, string statementNo, IEnumerable<Room> rooms, IEnumerable<string>? stages = null, IEnumerable<string>? systems = null,
        IReadOnlyDictionary<string, RoomBalance>? balances = null)
    {
        var sys = (systems ?? DefaultSystems).ToList();
        var stg = (stages ?? DefaultStages).ToList();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(Sheet);
        ws.Cell("B1").Value = "SITE STATEMENT";
        ws.Cell("B1").Style.Font.Bold = true; ws.Cell("B1").Style.Font.FontSize = 16; ws.Cell("B1").Style.Font.FontColor = XLColor.FromHtml("#8E1B22");
        ws.Cell("B2").Value = $"SUBCONTRACTOR: {subcontractor}";
        ws.Cell("B3").Value = $"STATEMENT NO: {statementNo}";
        ws.Cell("F2").Value = "Type quantities in the white cells only (number of points for this room and stage). Leave blank if nothing.";
        ws.Cell("F3").Value = "Grey figures under each header are the remaining quantity at the time of issue.";
        var headers = new List<string> { "KEY", "LOCATION", "FLOOR", "UNIT TYPE", "STAGE" };
        headers.AddRange(sys);
        headers.AddRange(Tail);
        for (var i = 0; i < headers.Count; i++) ws.Cell(FirstRow - 1, i + 1).Value = headers[i];
        var hdr = ws.Range(FirstRow - 1, 1, FirstRow - 1, headers.Count);
        hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#A6A6A6"); hdr.Style.Font.Bold = true; hdr.Style.Font.FontColor = XLColor.Black; hdr.Style.Alignment.WrapText = true;
        var r = FirstRow;
        foreach (var room in rooms)
            foreach (var st in stg)
            {
                ws.Cell(r, 1).Value = $"{room.Code}|{st}";
                ws.Cell(r, 2).Value = room.Code; ws.Cell(r, 3).Value = room.Level; ws.Cell(r, 4).Value = room.RoomType; ws.Cell(r, 5).Value = st;
                for (var i = 0; i < sys.Count; i++)
                {
                    var cell = ws.Cell(r, 6 + i);
                    cell.Style.Protection.Locked = false;
                    cell.Style.Fill.BackgroundColor = XLColor.White;
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Hair;
                    if (balances != null && balances.TryGetValue(LedgerKeys.Key(room.Code, st, sys[i]), out var b) && b.HasCap)
                        cell.CreateComment().AddText($"remaining {b.Remaining:0.##} of {b.ProjectQty:0.##}");
                }
                for (var i = 0; i < Tail.Length; i++)
                {
                    var cell = ws.Cell(r, 6 + sys.Count + i);
                    cell.Style.Protection.Locked = false;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FBF1CF");
                }
                ws.Cell(r, 6 + sys.Count).Value = 1;
                ws.Cell(r, 6 + sys.Count + 1).Value = 1;
                ws.Range(r, 6 + sys.Count, r, 7 + sys.Count).Style.NumberFormat.Format = "0%";
                r++;
            }
        ws.Range(FirstRow, 1, Math.Max(FirstRow, r - 1), 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#F6F4F3");
        ws.Column(1).Hide();
        ws.Columns(2, 5).Width = 14;
        ws.Columns(6, headers.Count).Width = 11;
        ws.SheetView.FreezeRows(FirstRow - 1);
        ws.SheetView.FreezeColumns(5);
        ws.Protect("raffaello").AllowElement(XLSheetProtectionElements.FormatColumns);

        var meta = wb.Worksheets.Add(Meta);
        meta.Cell("A1").Value = "SUBCONTRACTOR"; meta.Cell("B1").Value = subcontractor;
        meta.Cell("A2").Value = "STATEMENT NO"; meta.Cell("B2").Value = statementNo;
        meta.Cell("A3").Value = "SYSTEMS"; meta.Cell("B3").Value = string.Join(";", sys);
        meta.Cell("A4").Value = "GENERATED"; meta.Cell("B4").Value = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
        meta.Cell("A5").Value = "FORMAT"; meta.Cell("B5").Value = "RAFFAELLO-STATEMENT-1";
        meta.Visibility = XLWorksheetVisibility.VeryHidden;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    public static StatementImportResult Read(string path, ProjectSnapshot s, int invoiceNo, string building = Buildings.Branded)
    {
        using var x = new XlsxStreamReader(path);
        var res = new StatementImportResult();
        var meta = x.ReadRows(Meta).ToDictionary(r => r.Get("A"), r => r.Get("B"));
        res.Subcontractor = meta.GetValueOrDefault("SUBCONTRACTOR", "").ToUpperInvariant();
        res.StatementNo = meta.GetValueOrDefault("STATEMENT NO", "");
        var sys = meta.GetValueOrDefault("SYSTEMS", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (res.Subcontractor.Length == 0 || sys.Length == 0) { res.Issues.Add(new(0, IssueLevel.Error, "Not a Raffaello site statement (META sheet missing).")); return res; }
        var rooms = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var hashInput = new StringBuilder();
        var sitePctCol = 6 + sys.Length;
        foreach (var row in x.ReadRows(Sheet))
        {
            if (row.Number < FirstRow) continue;
            var key = row.Get(1);
            if (!key.Contains('|')) continue;
            var parts = key.Split('|');
            var (roomCode, stage) = (parts[0], parts[1]);
            var site = row.Number_(sitePctCol) ?? 1;
            var wir = row.Number_(sitePctCol + 1) ?? 1;
            var wirNo = row.Get(sitePctCol + 2).Trim();
            var highSys = row.Get(sitePctCol + 3).Trim().ToUpperInvariant();
            var highQty = row.Number_(sitePctCol + 4) ?? 0;
            var lenSys = row.Get(sitePctCol + 5).Trim().ToUpperInvariant();
            var lenQty = row.Number_(sitePctCol + 6) ?? 0;
            var lenM = row.Number_(sitePctCol + 7) ?? 0;
            var notes = row.Get(sitePctCol + 8).Trim();
            for (var i = 0; i < sys.Length; i++)
            {
                var q = row.Number_(6 + i);
                if (q is null || Math.Abs(q.Value) < 1e-9) continue;
                hashInput.Append($"{key}|{sys[i]}|{q.Value.ToString(CultureInfo.InvariantCulture)}|{site}|{wir};");
                rooms.TryGetValue(roomCode, out var room);
                if (room is null) res.Issues.Add(new(row.Number, IssueLevel.Warning, $"{roomCode} is not a known room."));
                var line = new ClaimLine
                {
                    Building = room?.Building ?? building, Subcontractor = res.Subcontractor, InvoiceNo = invoiceNo, Stage = stage, Floor = room?.Level ?? "",
                    Room = roomCode, Item = sys[i], Qty = q.Value, SitePct = site, WirPct = wir, WirNo = wirNo, Notes = notes, AreaType = room?.AreaType ?? "",
                    Source = "STATEMENT", StatementNo = res.StatementNo, SourceKey = $"ST|{res.Subcontractor}|{res.StatementNo}|{key}|{sys[i]}", EnteredAt = DateTime.Now,
                };
                if (highQty > 0 && (highSys.Length == 0 || highSys == sys[i]))
                {
                    line.QtyAbove45 = Math.Min(highQty, q.Value);
                    line.HeightStatus = CheckStatus.Pending;
                }
                if (lenQty > 0 && (lenSys.Length == 0 || lenSys == sys[i]))
                {
                    line.LengthApplies = true;
                    line.LengthClaimedQty = lenQty;
                    line.RouteLengthTotal = lenM;
                    line.LengthStatus = CheckStatus.Pending;
                    LengthCheck.Recalculate(line);
                }
                res.Claims.Add(line);
            }
        }
        res.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(res.Subcontractor + "#" + hashInput)));
        var dupNo = s.Statements.FirstOrDefault(t => t.Direction == "IN" && t.Subcontractor == res.Subcontractor && t.StatementNo == res.StatementNo);
        var dupHash = s.Statements.FirstOrDefault(t => t.Direction == "IN" && t.ContentHash == res.Hash);
        if (dupNo != null) { res.IsDuplicate = true; res.Issues.Add(new(0, IssueLevel.Error, $"Statement {res.StatementNo} of {res.Subcontractor} was already imported on {dupNo.At:dd-MMM-yyyy}.")); }
        else if (dupHash != null) { res.IsDuplicate = true; res.Issues.Add(new(0, IssueLevel.Error, $"Same content as statement {dupHash.StatementNo} imported on {dupHash.At:dd-MMM-yyyy}.")); }

        // balances against the room caps (plan qty), adding lines of this statement one by one
        var pending = new List<ClaimLine>(s.Claims);
        foreach (var c in res.Claims)
        {
            var bal = LedgerRules.Balance(s.RoomQtys, pending, c.Room, c.Stage, c.Item);
            var check = LedgerRules.Check(bal, c.Qty, null);
            res.Checks.Add((c, check));
            if (!check.CanPost) res.Issues.Add(new(0, IssueLevel.Warning, $"{c.Room} {c.Stage} {c.Item}: {check.Message}"));
            pending.Add(c);
        }
        return res;
    }

    /// <summary>Posts the statement. Lines over the remaining qty need a reason (one reason for all, or per key) or are skipped.</summary>
    public static int Commit(StatementImportResult res, IProjectStore store, string fileName, string? overReason = null)
    {
        if (res.IsDuplicate) throw new InvalidOperationException("Duplicate statement - not imported.");
        var post = new List<ClaimLine>();
        foreach (var (line, check) in res.Checks)
        {
            if (!check.CanPost)
            {
                if (string.IsNullOrWhiteSpace(overReason)) continue;
                line.IsOver = true;
                line.OverReason = overReason;
            }
            post.Add(line);
        }
        store.Batch(w =>
        {
            w.InsertMany(post);
            w.Insert(new SiteStatement { Subcontractor = res.Subcontractor, StatementNo = res.StatementNo, Direction = "IN", ContentHash = res.Hash, FileName = Path.GetFileName(fileName), Lines = post.Count, At = DateTime.Now });
        }, $"Site statement {res.StatementNo} ({res.Subcontractor}): {post.Count} claim lines");
        return post.Count;
    }
}
