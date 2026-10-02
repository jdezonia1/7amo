using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Cables;

/// <summary>
/// [cables] The cable claim sheets: the tracker's hidden 'CABLES BRANDED' / 'CABLES HOTEL' (the subcontractors' cable site statements per invoice)
/// and the CABLES sheet of the standard site statement, which mirrors them:
/// STAGE | SUBCONTRACTOR | INVOICE # | LOCATION | LEVEL | FROM | TO | CABLE SIZE | QTY | SITE % | WIR % | WIR NO (| NOTES).
/// </summary>
public static class CableSheets
{
    public const string StatementSheet = "CABLES";
    public const string RunsSheet = "CABLE RUNS";
    public static readonly string[] Columns = { "STAGE", "SUBCONTRACTOR", "INVOICE #", "LOCATION", "LEVEL", "FROM", "TO", "CABLE SIZE", "QTY", "SITE %", "WIR %", "WIR NO", "NOTES" };
    public static readonly string[] StageChoices = { "CABLE PULLING", "TERMINATION & TEST", "HANDOVER" };
    private const int HeaderRow = 4;
    private const int InputRows = 400;

    /// <summary>Tracker sheets that carry cable claims, with their building.</summary>
    public static IEnumerable<(string Sheet, string Building)> TrackerSheets(XlsxStreamReader x)
    {
        foreach (var name in x.Sheets.Keys)
        {
            var u = name.Trim().ToUpperInvariant();
            if (!u.StartsWith("CABLES")) continue;
            var b = u.Contains("HOTEL") ? Buildings.Hotel : u.Contains("BRANDED") ? Buildings.Branded : "";
            yield return (name, b);
        }
    }

    /// <summary>Reads every 'CABLES ...' sheet of the tracker as cable claims (source TRACKER).</summary>
    public static List<CableClaim> ReadTracker(XlsxStreamReader x, string fileName, List<ImportIssue>? issues = null)
    {
        var all = new List<CableClaim>();
        foreach (var (sheet, building) in TrackerSheets(x))
            all.AddRange(ReadSheet(x, sheet, building, "TRACKER", Path.GetFileName(fileName), "", 0, issues));
        return all;
    }

    /// <summary>Reads one cable claim sheet (header row found by FROM / TO / QTY).</summary>
    public static List<CableClaim> ReadSheet(XlsxStreamReader x, string sheet, string building, string source, string fileName, string defaultSub, int defaultInvoice,
        List<ImportIssue>? issues = null, string statementNo = "")
    {
        var res = new List<CableClaim>();
        Dictionary<string, int>? h = null;
        foreach (var r in x.ReadRows(sheet))
        {
            if (h is null)
            {
                var cand = r.Values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).GroupBy(v => Norm(v.Value)).ToDictionary(g => g.Key, g => g.First().Key);
                if (cand.ContainsKey("FROM") && cand.ContainsKey("TO") && (cand.ContainsKey("QTY") || cand.ContainsKey("LENGTH"))) h = cand;
                continue;
            }
            string G(params string[] names) { foreach (var n in names) if (h.TryGetValue(Norm(n), out var c)) { var v = r.Get(c).Trim(); if (v.Length > 0) return v; } return ""; }
            double? N(params string[] names) { var s = G(names).Replace(",", "").Replace("%", ""); return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null; }
            var from = G("FROM");
            var to = G("TO");
            var qty = N("QTY", "LENGTH", "QTY (M)", "LENGTH (M)");
            var size = G("CABLE SIZE", "SIZE", "CABLE");
            if (from.Length == 0 && to.Length == 0 && qty is null) continue;
            if (qty is null) { issues?.Add(new(r.Number, IssueLevel.Warning, $"{sheet} row {r.Number}: no QTY - skipped ({from} -> {to}).")); continue; }
            var sub = G("SUBCONTRACTOR", "SUB");
            if (sub.Length == 0) sub = defaultSub;
            var invText = G("INVOICE #", "INVOICE", "INV", "INVOICE NO");
            var inv = int.TryParse(invText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i
                : double.TryParse(invText, NumberStyles.Float, CultureInfo.InvariantCulture, out var di) ? (int)di : defaultInvoice;
            var loc = G("LOCATION");
            var b = loc.Equals(Buildings.Hotel, StringComparison.OrdinalIgnoreCase) ? Buildings.Hotel : loc.Equals(Buildings.Branded, StringComparison.OrdinalIgnoreCase) ? Buildings.Branded : building;
            var site = Pct(N("SITE %", "SITE"));
            var wir = Pct(N("WIR %", "WIR"));
            var stage = G("STAGE");
            if (from.Length == 0 || to.Length == 0) issues?.Add(new(r.Number, IssueLevel.Warning, $"{sheet} row {r.Number}: FROM or TO missing ({from} -> {to})."));
            if (!CableSize.Parse(size).IsValid) issues?.Add(new(r.Number, IssueLevel.Warning, $"{sheet} row {r.Number}: cable size '{size}' not recognised."));
            res.Add(new CableClaim
            {
                Building = b, Subcontractor = sub.Trim().ToUpperInvariant(), InvoiceNo = inv, StatementNo = statementNo, RawStage = stage, Stage = CableStages.Normalize(stage),
                Location = loc, Level = G("LEVEL", "FLOOR"), FromRaw = from, ToRaw = to, SizeRaw = size, Qty = qty.Value, SitePct = site ?? 1, WirPct = wir ?? 1,
                WirNo = G("WIR NO", "WIR NO.", "WIR NUMBER", "WIR REF"), Notes = G("NOTES", "REMARKS"), Source = source,
                SourceKey = string.Join('|', source, fileName, sheet, statementNo, r.Number, sub, inv, from, to, size, qty.Value.ToString(CultureInfo.InvariantCulture)),
                EnteredAt = DateTime.Now,
            });
        }
        if (h is null) issues?.Add(new(0, IssueLevel.Warning, $"{sheet}: header row (FROM / TO / QTY) not found."));
        return res;
    }

    private static double? Pct(double? v) => v is null ? null : v > 1.0001 ? v / 100 : v;

    private static string Norm(string h) => new string(h.Trim().ToUpperInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '%' or '#').ToArray());

    /// <summary>Text for the statement content hash (so a cable-only statement is not mistaken for another one).</summary>
    public static string HashInput(IEnumerable<CableClaim> claims)
    {
        var sb = new StringBuilder();
        foreach (var c in claims)
            sb.Append($"CBL|{c.Stage}|{PanelNames.KeyOf(c.FromRaw)}|{PanelNames.KeyOf(c.ToRaw)}|{CableSize.KeyOf(c.SizeRaw)}|{c.Qty.ToString(CultureInfo.InvariantCulture)}|{c.SitePct}|{c.WirPct};");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ statement template

    /// <summary>Adds the CABLES input sheet (and a CABLE RUNS reference list of the register) to a site statement workbook.</summary>
    public static void AddStatementSheets(XLWorkbook wb, string subcontractor, string statementNo, IReadOnlyList<CableRun>? runs = null, string sheetPassword = "raffaello")
    {
        var ws = wb.Worksheets.Add(StatementSheet);
        ws.Cell("A1").Value = "CABLES - SITE STATEMENT";
        ws.Cell("A1").Style.Font.Bold = true; ws.Cell("A1").Style.Font.FontSize = 14; ws.Cell("A1").Style.Font.FontColor = XLColor.FromHtml("#8E1B22");
        ws.Cell("A2").Value = $"SUBCONTRACTOR: {subcontractor}    STATEMENT NO: {statementNo}";
        ws.Cell("A3").Value = "One row per cable (phase cable and earth on separate rows). FROM / TO = panel or equipment names as on the SLD (see CABLE RUNS). QTY in metres. STAGE: CABLE PULLING / TERMINATION & TEST / HANDOVER.";
        for (var i = 0; i < Columns.Length; i++) ws.Cell(HeaderRow, i + 1).Value = Columns[i];
        var hdr = ws.Range(HeaderRow, 1, HeaderRow, Columns.Length);
        hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#A6A6A6"); hdr.Style.Font.Bold = true; hdr.Style.Font.FontColor = XLColor.Black; hdr.Style.Alignment.WrapText = true;
        var input = ws.Range(HeaderRow + 1, 1, HeaderRow + InputRows, Columns.Length);
        input.Style.Protection.Locked = false;
        input.Style.Fill.BackgroundColor = XLColor.White;
        input.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        input.Style.Border.OutsideBorder = XLBorderStyleValues.Hair;
        ws.Range(HeaderRow + 1, 2, HeaderRow + InputRows, 2).Value = subcontractor;
        ws.Range(HeaderRow + 1, 1, HeaderRow + InputRows, 1).CreateDataValidation().List("\"" + string.Join(",", StageChoices) + "\"", true);
        ws.Range(HeaderRow + 1, 10, HeaderRow + InputRows, 11).Style.NumberFormat.Format = "0%";
        ws.Range(HeaderRow + 1, 9, HeaderRow + InputRows, 9).Style.NumberFormat.Format = "#,##0.0";
        ws.Columns(1, Columns.Length).Width = 13;
        ws.Column(6).Width = 30; ws.Column(7).Width = 30; ws.Column(13).Width = 30;
        ws.SheetView.FreezeRows(HeaderRow);
        ws.Protect(sheetPassword).AllowElement(XLSheetProtectionElements.FormatColumns);

        if (runs is { Count: > 0 })
        {
            var rs = wb.Worksheets.Add(RunsSheet);
            var head = new[] { "REF", "BUILDING", "LEVEL", "FROM", "TO", "SIZE", "EARTH", "DESIGN LENGTH M", "STATUS" };
            for (var i = 0; i < head.Length; i++) rs.Cell(1, i + 1).Value = head[i];
            var hr = rs.Range(1, 1, 1, head.Length);
            hr.Style.Fill.BackgroundColor = XLColor.FromHtml("#A6A6A6"); hr.Style.Font.Bold = true; hr.Style.Font.FontColor = XLColor.Black;
            var row = 2;
            foreach (var r in runs.Where(r => r.Status != CableStatus.Rejected).OrderBy(r => r.Building).ThenBy(r => r.FromName).ThenBy(r => r.ToName))
            {
                rs.Cell(row, 1).Value = r.Ref; rs.Cell(row, 2).Value = r.Building; rs.Cell(row, 3).Value = r.Level; rs.Cell(row, 4).Value = r.FromName; rs.Cell(row, 5).Value = r.ToName;
                rs.Cell(row, 6).Value = r.SizeKey; rs.Cell(row, 7).Value = r.EarthSizeKey;
                if (r.DesignLength is { } d) rs.Cell(row, 8).Value = d;
                rs.Cell(row, 9).Value = r.Status;
                row++;
            }
            rs.Columns(1, head.Length).AdjustToContents(1, Math.Min(row, 300));
            rs.SheetView.FreezeRows(1);
            rs.RangeUsed()?.SetAutoFilter();
            rs.Protect(sheetPassword).AllowElement(XLSheetProtectionElements.AutoFilter).AllowElement(XLSheetProtectionElements.Sort);
        }
    }

    /// <summary>Reads the CABLES sheet of a filled site statement (absent sheet = no cable claims).</summary>
    public static List<CableClaim> ReadStatement(XlsxStreamReader x, string subcontractor, string statementNo, int invoiceNo, string building, List<ImportIssue>? issues = null)
    {
        var sheet = x.FindSheet(n => n.Trim().Equals(StatementSheet, StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return new List<CableClaim>();
        var list = ReadSheet(x, sheet, building, "STATEMENT", "", subcontractor, invoiceNo, issues, statementNo);
        foreach (var c in list)
        {
            if (c.InvoiceNo <= 0) c.InvoiceNo = invoiceNo;
            c.SourceKey = $"ST|{subcontractor}|{statementNo}|CBL|{c.Stage}|{c.FromRaw}|{c.ToRaw}|{c.SizeRaw}|{c.Qty.ToString(CultureInfo.InvariantCulture)}|{list.IndexOf(c)}";
        }
        return list;
    }
}
