using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;

namespace Raffaello.Core.Invoicing;

/// <summary>Header block values of the QS summary sheet. Names come from settings, never from code.</summary>
public sealed class InvoiceHeaderInfo
{
    public string ProjectName { get; set; } = "Raffles Hotel & Branded Residence";
    public string ProjectCode { get; set; } = "";
    public string Location { get; set; } = "RIYADH";
    public string ProjectDirector { get; set; } = "";
    public string VendorNo { get; set; } = "";
    public string Category { get; set; } = " ELEC WORKS ";
    public string ScopeOfWork { get; set; } = "INSTALLATION OF ELECTRICAL WORKS";
    public string CoveredPeriod { get; set; } = "";
    public string[] SignatureRoles { get; set; } =
        { "Commercial Engineer", "Site Engineer", " QA/QC  Manager", "HSE Manager", "SR. Commercial Manager ", "MEP  Manager", " Project Manager", "Project Director", "Technical Office", "Cost Control", "Finance", "Top Management" };
    public string[] SignatureNames { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Writes an invoice in the subcontract template layout ('ROOTS INV 1'), column for column: header block rows 1-12, lines from row 14,
/// footer from the 'Subcontract Value' row with SUM ranges that reach the real last line, retention % filled, VAT and signatures.
/// Adds a 'QTY BACKUP' sheet (room x stage x system lines behind every BOQ row) and a 'WARNINGS' sheet.
/// </summary>
public static class InvoiceExcelExporter
{
    public const int FirstLineRow = 14;
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");
    private static readonly XLColor SectionFill = XLColor.FromHtml("#F4E3E3");

    /// <param name="stamp">Fixed document date: makes the file reproducible (package builds).</param>
    public static void Export(string path, InvoiceBuild build, InvoiceHeaderInfo? info = null, string? sheetName = null, DateTime? stamp = null)
    {
        info ??= new InvoiceHeaderInfo();
        var h = build.Header;
        using var wb = new XLWorkbook();
        var name = Safe(sheetName ?? $"{h.Subcontractor} INV {h.InvoiceNo}" + (h.Revision > 0 ? $" R{h.Revision}" : ""));
        var ws = wb.Worksheets.Add(name);
        var footer = WriteSheet(ws, build, info);
        WriteBackup(wb.Worksheets.Add("QTY BACKUP"), build);
        WriteWarnings(wb.Worksheets.Add("WARNINGS"), build);
        wb.CalculateMode = XLCalculateMode.Auto;
        wb.FullCalculationOnLoad = true;
        _ = footer;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (stamp is { } st) { wb.Properties.Created = st; wb.Properties.Modified = st; }
        wb.SaveAs(path);
        if (stamp is { } fixedStamp) Packaging.Deterministic.NormalizeZip(path, fixedStamp);
    }

    /// <summary>Returns the footer ('Subcontract Value') row number.</summary>
    public static int WriteSheet(IXLWorksheet ws, InvoiceBuild build, InvoiceHeaderInfo info)
    {
        var h = build.Header;
        void S(string cell, object? v) { if (v is null) return; ws.Cell(cell).Value = v switch { double d => d, int i => i, DateTime dt => dt, _ => v.ToString() }; }

        S("G1", "Quantity Surveyor (Q.S) Summary Sheet"); ws.Cell("G1").Style.Font.Bold = true; ws.Cell("G1").Style.Font.FontSize = 14;
        S("X1", "Date :"); ws.Cell("Y1").FormulaA1 = "TODAY()"; ws.Cell("Y1").Style.NumberFormat.Format = "dd-mmm-yyyy";
        S("C2", "Project Name"); S("E2", info.ProjectName); S("L2", "Vendor Name "); S("M2", h.Subcontractor); S("S2", "Subcontract No"); S("U2", h.ContractNo); S("W2", "Site Hand Over Date ");
        S("C3", "Project Code"); S("E3", info.ProjectCode); S("L3", "Vendor No "); S("M3", info.VendorNo); S("S3", "Contract Value "); S("W3", "Contract Issue Date ");
        S("C4", "Location "); S("E4", info.Location); S("L4", "Category "); S("M4", info.Category); S("S4", "Variation Order"); S("W4", "Contract Duration ");
        S("C5", "Project Director"); S("E5", info.ProjectDirector); S("L5", "Scope Of Work "); S("M5", info.ScopeOfWork); S("S5", "Revised Contract Value "); S("W5", "Finish Date ");
        S("L6", "Subcon.Inv.No"); S("M6", $"INV-{h.InvoiceNo:00}" + (h.Revision > 0 ? $" Rev {h.Revision}" : "")); S("S6", "Retention%"); S("U6", h.RetentionPct); S("W6", "PC No"); S("Y6", h.InvoiceNo.ToString("00"));
        ws.Cell("U6").Style.NumberFormat.Format = "0%";
        S("L7", "Subcon.Inv.Date"); S("M7", h.CreatedAt); ws.Cell("M7").Style.NumberFormat.Format = "dd-mmm-yyyy"; S("S7", "Advanced  %"); S("T7", h.AdvancePct); S("W7", "PC Covered Period"); S("Y7", info.CoveredPeriod);
        S("C9", "Contract Details "); S("G9", "Contract Breakdown  "); S("O9", "Executed Work"); S("U9", "Cont. Remaining Work "); S("W9", "Forecast");
        S("C10", "Recharge Summary "); S("G10", "Agreed BOQ "); S("L10", "Billing To Client "); S("O10", "Executed Work"); S("U10", "Cont. Remaining Work "); S("W10", "Forecast");
        var h11 = new (string c, string v)[] { ("A", "BOQ Description"), ("B", "Item"), ("C", "BOQ item No "), ("D", "Cost Code "), ("E", "Budget Resource Code"), ("F", "Contract  Description "), ("G", "Unit "), ("H", "QTY"), ("I", "Unit Rate"), ("J", "Percentage"), ("K", " Amount "), ("L", "Billed Quantity"), ("O", "Executed Quantity "), ("R", "Executed Amount "), ("U", "Qty"), ("V", "Amnt"), ("W", "Forecasted  Qty") };
        foreach (var (c, v) in h11) S(c + "11", v);
        var h12 = new (string c, string v)[] { ("L", "Prev."), ("M", "Curr."), ("N", "Cum"), ("O", "Prev."), ("P", "Curr."), ("Q", "Cum"), ("R", "Prev."), ("S", "Curr."), ("T", "Cum"), ("U", "Curr."), ("V", "Curr."), ("W", "Actual rem. Qty."), ("X", "Forecasted Qty"), ("Y", "Actual rem. Amount"), ("Z", "Forecasted Amt.") };
        foreach (var (c, v) in h12) S(c + "12", v);
        var hdr = ws.Range("A9:Z12");
        hdr.Style.Fill.BackgroundColor = Grey;
        hdr.Style.Font.Bold = true;
        hdr.Style.Font.FontColor = XLColor.Black;
        hdr.Style.Alignment.WrapText = true;

        var r = FirstLineRow;
        foreach (var l in build.Lines.OrderBy(l => l.RowOrder))
        {
            if (l.Kind == "SECTION")
            {
                S($"C{r}", l.Description);
                ws.Range($"A{r}:Z{r}").Style.Fill.BackgroundColor = SectionFill;
                ws.Cell($"C{r}").Style.Font.Bold = true;
            }
            else if (l.Kind == "NOTE")
            {
                S($"F{r}", l.Description);
                ws.Cell($"F{r}").Style.Font.Italic = true;
            }
            else
            {
                S($"A{r}", l.BoqDescription);
                if (double.TryParse(l.ItemNo, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var no)) ws.Cell($"B{r}").Value = no; else S($"B{r}", l.ItemNo);
                S($"C{r}", l.BoqCode); S($"D{r}", l.CostCode); S($"E{r}", l.BudgetResourceCode); S($"F{r}", l.Description); S($"G{r}", l.Unit);
                ws.Cell($"H{r}").Value = l.ContractQty; ws.Cell($"I{r}").Value = l.Rate; ws.Cell($"J{r}").Value = l.StagePct;
                ws.Cell($"K{r}").FormulaA1 = $"J{r}*I{r}*H{r}";
                if (Math.Abs(l.PrevQty) > 1e-9) ws.Cell($"O{r}").Value = l.PrevQty;
                ws.Cell($"P{r}").FormulaA1 = $"Q{r}-O{r}";
                if (Math.Abs(l.CumQty) > 1e-9) ws.Cell($"Q{r}").Value = l.CumQty;
                ws.Cell($"R{r}").FormulaA1 = $"O{r}*J{r}*I{r}";
                ws.Cell($"S{r}").FormulaA1 = $"T{r}-R{r}";
                ws.Cell($"T{r}").FormulaA1 = $"Q{r}*J{r}*I{r}";
                ws.Cell($"U{r}").FormulaA1 = $"H{r}-Q{r}";
                ws.Cell($"V{r}").FormulaA1 = $"K{r}-T{r}";
                ws.Cell($"W{r}").FormulaA1 = $"U{r}";
                ws.Cell($"X{r}").FormulaA1 = $"W{r}+Q{r}";
                ws.Cell($"Y{r}").FormulaA1 = $"W{r}*J{r}*I{r}";
                ws.Cell($"Z{r}").FormulaA1 = $"X{r}*J{r}*I{r}";
                if (Math.Abs(l.CurrQty) > 1e-9) ws.Range($"O{r}:T{r}").Style.Font.Bold = true;
            }
            r++;
        }
        var last = Math.Max(FirstLineRow, r - 1);
        ws.Range($"H{FirstLineRow}:H{last}").Style.NumberFormat.Format = "#,##0.00";
        ws.Range($"I{FirstLineRow}:I{last}").Style.NumberFormat.Format = "#,##0.00";
        ws.Range($"J{FirstLineRow}:J{last}").Style.NumberFormat.Format = "0%";
        ws.Range($"K{FirstLineRow}:Z{last}").Style.NumberFormat.Format = "#,##0.00";

        // ---- footer (SUM ranges reach the real last line)
        var f = last + 1;
        S($"C{f}", " Subcontract Value");
        foreach (var c in new[] { "K", "R", "S", "T", "V", "Y", "Z" }) ws.Cell($"{c}{f}").FormulaA1 = $"SUM({c}{FirstLineRow}:{c}{last})";
        S($"C{f + 1}", "Delay Penalty If Applicable "); S($"G{f + 1}", "Forecasted At Completion ( FAC )"); S($"P{f + 1}", "Certified Work -Actual To Date "); ws.Cell($"T{f + 1}").FormulaA1 = $"T{f}";
        S($"C{f + 2}", "Contract Finish Date "); S($"G{f + 2}", "Total Contract Amount "); S($"L{f + 2}", "Contract %"); ws.Cell($"M{f + 2}").FormulaA1 = $"K{f}"; ws.Cell($"N{f + 2}").Value = 1;
        S($"R{f + 2}", "Prev."); S($"S{f + 2}", "Curr."); S($"T{f + 2}", "Cum.");
        S($"C{f + 3}", "Ahead /Behind -\nSchedule  ( Days )"); S($"G{f + 3}", "Executed Amount  "); S($"L{f + 3}", "Contract %");
        ws.Cell($"M{f + 3}").FormulaA1 = $"T{f}"; ws.Cell($"N{f + 3}").FormulaA1 = $"IF(M{f + 2}=0,0,M{f + 3}/M{f + 2})";
        S($"P{f + 3}", "Gross Certified Amount"); ws.Cell($"R{f + 3}").FormulaA1 = $"R{f}"; ws.Cell($"S{f + 3}").FormulaA1 = $"S{f}"; ws.Cell($"T{f + 3}").FormulaA1 = $"S{f + 3}+R{f + 3}"; S($"U{f + 3}", "Contract %");
        S($"C{f + 4}", "Penalty Per Day "); S($"G{f + 4}", "Remaining Amount (as per contract)"); S($"L{f + 4}", "Contract %");
        ws.Cell($"M{f + 4}").FormulaA1 = $"M{f + 2}-M{f + 3}"; ws.Cell($"N{f + 4}").FormulaA1 = $"IF(M{f + 2}=0,0,M{f + 4}/M{f + 2})";
        S($"P{f + 4}", "Retention "); ws.Cell($"R{f + 4}").FormulaA1 = $"R{f + 3}*-$U$6"; ws.Cell($"S{f + 4}").FormulaA1 = $"S{f + 3}*-$U$6"; ws.Cell($"T{f + 4}").FormulaA1 = $"S{f + 4}+R{f + 4}"; S($"U{f + 4}", "Contract %");
        S($"C{f + 5}", "Not Exceed 10% "); S($"G{f + 5}", "Forecasted at Completion ( as per act )"); S($"P{f + 5}", "Released of Retention");
        ws.Cell($"R{f + 5}").Value = 0; ws.Cell($"S{f + 5}").Value = 0; ws.Cell($"T{f + 5}").FormulaA1 = $"R{f + 5}+S{f + 5}";
        S($"C{f + 6}", "Penalty Amount "); S($"G{f + 6}", "Est. Addendum Value. "); S($"P{f + 6}", "Advanced Payment");
        ws.Cell($"R{f + 6}").Value = 0; ws.Cell($"S{f + 6}").Value = 0; ws.Cell($"T{f + 6}").FormulaA1 = $"R{f + 6}+S{f + 6}";
        S($"C{f + 7}", "Remarks:"); S($"P{f + 7}", "Recovered Adv. Payment"); ws.Cell($"R{f + 7}").Value = 0; ws.Cell($"S{f + 7}").Value = -Math.Abs(h.AdvanceRecovery); ws.Cell($"T{f + 7}").FormulaA1 = $"R{f + 7}+S{f + 7}";
        S($"P{f + 8}", "Discount"); ws.Cell($"R{f + 8}").Value = 0; ws.Cell($"S{f + 8}").Value = -Math.Abs(h.Discount); ws.Cell($"T{f + 8}").FormulaA1 = $"R{f + 8}+S{f + 8}"; S($"U{f + 8}", "Remark:");
        S($"P{f + 9}", "Net Payment");
        foreach (var c in new[] { "R", "S", "T" }) ws.Cell($"{c}{f + 9}").FormulaA1 = $"{c}{f + 3}+{c}{f + 4}+{c}{f + 5}+{c}{f + 6}+{c}{f + 7}+{c}{f + 8}";
        S($"P{f + 10}", "VAT @ 15%"); foreach (var c in new[] { "R", "S", "T" }) ws.Cell($"{c}{f + 10}").FormulaA1 = $"{c}{f + 9}*0.15";
        S($"P{f + 11}", " - Advance VAT @ 15%");
        S($"P{f + 12}", "Net VAT"); foreach (var c in new[] { "R", "S", "T" }) ws.Cell($"{c}{f + 12}").FormulaA1 = $"{c}{f + 10}+{c}{f + 11}";
        S($"P{f + 13}", "Net Payment Incl. VAT");
        ws.Cell($"R{f + 13}").FormulaA1 = $"R{f + 9}+R{f + 12}"; ws.Cell($"T{f + 13}").FormulaA1 = $"T{f + 9}+T{f + 12}"; ws.Cell($"S{f + 13}").FormulaA1 = $"T{f + 13}-R{f + 13}";
        ws.Range($"M{f}:Z{f + 13}").Style.NumberFormat.Format = "#,##0.00";
        ws.Range($"N{f + 2}:N{f + 4}").Style.NumberFormat.Format = "0.0%";
        ws.Range($"C{f}:Z{f}").Style.Font.Bold = true;
        ws.Range($"P{f + 9}:T{f + 9}").Style.Font.Bold = true;
        ws.Range($"P{f + 13}:T{f + 13}").Style.Font.Bold = true;
        ws.Cell("U3").FormulaA1 = $"M{f + 2}";
        ws.Cell("U3").Style.NumberFormat.Format = "#,##0.00";

        var sigCols = new[] { "C", "D", "F", "G", "I", "L", "N", "Q", "S", "U", "W", "Y" };
        for (var i = 0; i < sigCols.Length && i < info.SignatureRoles.Length; i++)
        {
            S($"{sigCols[i]}{f + 15}", info.SignatureRoles[i]);
            if (i < info.SignatureNames.Length) S($"{sigCols[i]}{f + 16}", info.SignatureNames[i]);
        }
        ws.Range($"C{f + 15}:Z{f + 15}").Style.Font.Bold = true;

        ws.Column("A").Width = 30; ws.Column("B").Width = 6; ws.Column("C").Width = 24; ws.Column("D").Width = 9; ws.Column("E").Width = 14;
        ws.Column("F").Width = 60; ws.Column("G").Width = 6;
        foreach (var c in "HIJKLMNOPQRSTUVWXYZ") ws.Column(c.ToString()).Width = 12;
        ws.Range($"F{FirstLineRow}:F{last}").Style.Alignment.WrapText = false;
        ws.SheetView.FreezeRows(12);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize = XLPaperSize.A3Paper;
        ws.PageSetup.SetRowsToRepeatAtTop(9, 12);
        ws.PageSetup.FitToPages(1, 0);
        return f;
    }

    private static void WriteBackup(IXLWorksheet ws, InvoiceBuild build)
    {
        var headers = new[] { "SUBCONTRACTOR", "INVOICE", "ROOM", "FLOOR", "STAGE", "SYSTEM / ITEM", "AREA", "QTY (PLAN)", "SITE %", "WIR %", "BAND", "INVOICE QTY", "CONTRACT ITEM", "RATE", "BOQ CODE", "BOQ DESCRIPTION", "CONFIDENCE", "EXPLANATION" };
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        var hr = ws.Range(1, 1, 1, headers.Length);
        hr.Style.Fill.BackgroundColor = Grey; hr.Style.Font.Bold = true; hr.Style.Font.FontColor = XLColor.Black;
        var r = 2;
        foreach (var p in build.Mapping.Parts.OrderBy(p => p.Item?.Order ?? 9999).ThenBy(p => p.BoqCode).ThenBy(p => p.Line.Room))
        {
            var c = p.Line;
            object?[] v = { c.Subcontractor, c.InvoiceNo, c.Room, c.Floor, c.Stage, c.Item, c.AreaType, c.Qty, c.SitePct, c.WirPct, p.Band, p.Qty, p.Item?.ItemNo, p.Item?.Rate, p.BoqCode, p.BoqDescription, p.Confidence, p.Explanation };
            for (var i = 0; i < v.Length; i++)
                ws.Cell(r, i + 1).Value = v[i] switch { null => Blank.Value, double d => d, int n => n, _ => v[i]!.ToString() };
            r++;
        }
        foreach (var hq in build.Mapping.Held)
        {
            var c = hq.Line;
            object?[] v = { c.Subcontractor, c.InvoiceNo, c.Room, c.Floor, c.Stage, c.Item, c.AreaType, c.Qty, c.SitePct, c.WirPct, "HELD", 0.0, "", null, "", "", "HELD", hq.HeldReason };
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = v[i] switch { null => Blank.Value, double d => d, int n => n, _ => v[i]!.ToString() };
            ws.Row(r).Style.Font.Italic = true;
            r++;
        }
        ws.Range(2, 9, Math.Max(2, r), 10).Style.NumberFormat.Format = "0%";
        ws.Range(2, 8, Math.Max(2, r), 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(2, 12, Math.Max(2, r), 12).Style.NumberFormat.Format = "#,##0.00";
        ws.SheetView.FreezeRows(1);
        if (r > 2) ws.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        ws.Columns(1, 17).AdjustToContents(1, Math.Min(r, 400));
        ws.Column(18).Width = 120;
    }

    private static void WriteWarnings(IXLWorksheet ws, InvoiceBuild build)
    {
        ws.Cell(1, 1).Value = "WARNING";
        ws.Cell(1, 1).Style.Fill.BackgroundColor = Grey; ws.Cell(1, 1).Style.Font.Bold = true;
        var r = 2;
        foreach (var w in build.Warnings) ws.Cell(r++, 1).Value = w;
        foreach (var p in build.Mapping.NeedsConfirmation.GroupBy(p => (p.Line.Stage, p.Line.Item, p.Confidence)).Select(g => g.First()))
            ws.Cell(r++, 1).Value = $"CONFIRM [{p.Confidence}] {p.Line.Stage} {p.Line.Item}: {p.Explanation}";
        ws.Column(1).Width = 160;
    }

    private static string Safe(string name)
    {
        var bad = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var n = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray());
        return n.Length > 31 ? n[..31] : n;
    }
}
