using ClosedXML.Excel;

namespace Raffaello.Core.Materials;

/// <summary>
/// MIR tracker workbook per PO: one row per PO line, DN quantity columns grouped under their MIR (DNs without a MIR last),
/// DELIVERED = SUM of the DN columns, REMAINING, %, value, an OVER PO alarm (tolerance from the PO terms), and a total row with
/// SUMPRODUCT(DN qty, rate) per DN = delivered value per DN. Plus MIR SUMMARY and DN LINES (match status) sheets.
/// </summary>
public static class MirTrackerExporter
{
    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");
    private static readonly XLColor Red = XLColor.FromHtml("#8B0000");
    private static readonly XLColor Yellow = XLColor.FromHtml("#FFFF00");

    public sealed record DnColumn(MatDn Dn, string MirNo, string MirStatus);

    public static List<DnColumn> Columns(MaterialsSnapshot m, MatPo po)
    {
        var poKey = MaterialsSnapshot.PoKey(po.PoNo);
        var poLineIds = m.LinesOf(po).Select(l => l.Id).ToHashSet();
        var dns = m.Dns.Where(d => MaterialsSnapshot.PoKey(d.PoNo) == poKey || m.LinesOf(d).Any(l => l.PoLineId is long id && poLineIds.Contains(id))).ToList();
        var cols = new List<DnColumn>();
        foreach (var dn in dns)
        {
            var link = m.MirDns.FirstOrDefault(x => x.DnNo.Equals(dn.DnNo, StringComparison.OrdinalIgnoreCase));
            var mir = link is null ? null : m.Mirs.FirstOrDefault(x => x.Id == link.MirId);
            cols.Add(new DnColumn(dn, mir?.MirNo ?? "NO MIR", mir?.Status ?? ""));
        }
        return cols.OrderBy(c => c.MirNo == "NO MIR").ThenBy(c => c.MirNo, StringComparer.Ordinal).ThenBy(c => c.Dn.DnDate ?? DateTime.MaxValue).ThenBy(c => c.Dn.DnNo, StringComparer.Ordinal).ToList();
    }

    public static void Export(string path, MaterialsSnapshot m, MaterialsSettings settings, MatPo po)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("MIR TRACKER");
        var cols = Columns(m, po);
        var lines = m.LinesOf(po).ToList();
        var tol = settings.ToleranceFor(po);
        ws.Cell(1, 1).Value = $"PO {po.PoNo}  |  {po.Supplier}  |  {po.Scope}  |  tolerance ±{tol:P0}  |  printed {DateTime.Now:dd-MMM-yyyy HH:mm}";
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontColor = Red;
        const int hr = 3; // header rows 3 (MIR), 4 (DN), 5 (DN date)
        string[] fixedHeads = { "NO", "DESCRIPTION", "UNIT", "PO QTY", "RATE" };
        for (var i = 0; i < fixedHeads.Length; i++) { ws.Cell(hr, i + 1).Value = fixedHeads[i]; ws.Range(hr, i + 1, hr + 2, i + 1).Merge(); }
        const int firstDn = 6;
        for (var c = 0; c < cols.Count; c++)
        {
            ws.Cell(hr, firstDn + c).Value = cols[c].MirNo + (cols[c].MirStatus.Length > 0 && cols[c].MirStatus != "OPEN" ? $" ({cols[c].MirStatus})" : "");
            ws.Cell(hr + 1, firstDn + c).Value = "DN " + cols[c].Dn.DnNo;
            if (cols[c].Dn.DnDate is DateTime d) { ws.Cell(hr + 2, firstDn + c).Value = d; ws.Cell(hr + 2, firstDn + c).Style.NumberFormat.Format = "dd-mmm-yy"; }
        }
        // merge MIR group headers
        for (var c = 0; c < cols.Count;)
        {
            var e = c;
            while (e + 1 < cols.Count && cols[e + 1].MirNo == cols[c].MirNo) e++;
            if (e > c) ws.Range(hr, firstDn + c, hr, firstDn + e).Merge();
            c = e + 1;
        }
        var after = firstDn + cols.Count;
        string[] tail = { "DELIVERED", "REMAINING", "% DELIVERED", "VALUE DELIVERED", "ALARM" };
        for (var i = 0; i < tail.Length; i++) { ws.Cell(hr, after + i).Value = tail[i]; ws.Range(hr, after + i, hr + 2, after + i).Merge(); }
        var head = ws.Range(hr, 1, hr + 2, after + tail.Length - 1);
        head.Style.Fill.BackgroundColor = Grey; head.Style.Font.Bold = true; head.Style.Font.FontColor = XLColor.Black;
        head.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; head.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center; head.Style.Alignment.WrapText = true;

        var r0 = hr + 3;
        var r = r0;
        foreach (var pl in lines)
        {
            ws.Cell(r, 1).Value = pl.LineNo; ws.Cell(r, 2).Value = pl.Description; ws.Cell(r, 3).Value = Units.Normalize(pl.Unit); ws.Cell(r, 4).Value = pl.Qty; ws.Cell(r, 5).Value = pl.Rate;
            for (var c = 0; c < cols.Count; c++)
            {
                var q = m.LinesOf(cols[c].Dn).Where(l => l.PoLineId == pl.Id).Sum(l => l.Qty);
                if (Math.Abs(q) > 1e-9) ws.Cell(r, firstDn + c).Value = q;
            }
            var dnRange = cols.Count == 0 ? null : $"{Col(firstDn)}{r}:{Col(after - 1)}{r}";
            ws.Cell(r, after).FormulaA1 = dnRange is null ? "0" : $"SUM({dnRange})";
            ws.Cell(r, after + 1).FormulaA1 = $"D{r}-{Col(after)}{r}";
            ws.Cell(r, after + 2).FormulaA1 = $"IF(D{r}=0,0,{Col(after)}{r}/D{r})";
            ws.Cell(r, after + 3).FormulaA1 = $"{Col(after)}{r}*E{r}";
            ws.Cell(r, after + 4).FormulaA1 = $"IF({Col(after)}{r}>D{r}*(1+{tol.ToString(System.Globalization.CultureInfo.InvariantCulture)}),\"OVER PO\",\"\")";
            r++;
        }
        var last = r - 1;
        // total row: SUMPRODUCT per DN column = value delivered per DN
        ws.Cell(r, 2).Value = "VALUE (SAR) = SUMPRODUCT(QTY, RATE)";
        ws.Cell(r, 4).FormulaA1 = $"SUMPRODUCT(D{r0}:D{last},E{r0}:E{last})";
        for (var c = 0; c < cols.Count; c++)
            ws.Cell(r, firstDn + c).FormulaA1 = $"SUMPRODUCT({Col(firstDn + c)}{r0}:{Col(firstDn + c)}{last},$E${r0}:$E${last})";
        ws.Cell(r, after).FormulaA1 = $"SUMPRODUCT({Col(after)}{r0}:{Col(after)}{last},E{r0}:E{last})";
        ws.Cell(r, after + 3).FormulaA1 = $"SUM({Col(after + 3)}{r0}:{Col(after + 3)}{last})";
        ws.Cell(r, after + 2).FormulaA1 = $"IF(D{r}=0,0,{Col(after + 3)}{r}/D{r})";
        var tot = ws.Range(r, 1, r, after + tail.Length - 1);
        tot.Style.Font.Bold = true; tot.Style.Fill.BackgroundColor = XLColor.FromHtml("#F4E3E3");
        if (last >= r0)
        {
            ws.Range(r0, 4, r, after + 1).Style.NumberFormat.Format = "#,##0.##";
            ws.Range(r0, 5, last, 5).Style.NumberFormat.Format = "#,##0.000";
            ws.Range(r0, after + 2, r, after + 2).Style.NumberFormat.Format = "0.0%";
            ws.Range(r0, after + 3, r, after + 3).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(r, firstDn, r, Math.Max(firstDn, after - 1)).Style.NumberFormat.Format = "#,##0.00";
            var alarm = ws.Range(r0, after + 4, last, after + 4);
            alarm.AddConditionalFormat().WhenEquals("\"OVER PO\"").Fill.SetBackgroundColor(Red).Font.SetFontColor(XLColor.White).Font.SetBold();
            var pct = ws.Range(r0, after + 2, last, after + 2);
            pct.AddConditionalFormat().WhenGreaterThan(1).Fill.SetBackgroundColor(Yellow);
        }
        ws.Column(1).Width = 6; ws.Column(2).Width = 38; ws.Column(3).Width = 7; ws.Column(4).Width = 12; ws.Column(5).Width = 10;
        for (var c = firstDn; c < after + tail.Length; c++) ws.Column(c).Width = 13;
        ws.SheetView.Freeze(hr + 2, 2);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);

        Summary(wb.Worksheets.Add("MIR SUMMARY"), m, po, cols);
        DnLines(wb.Worksheets.Add("DN LINES"), m, po, cols);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    private static void Summary(IXLWorksheet ws, MaterialsSnapshot m, MatPo po, List<DnColumn> cols)
    {
        string[] h = { "MIR", "MIR DATE", "STATUS", "DNs", "DN LINES", "QTY (PO UNITS)", "VALUE (SAR)" };
        Header(ws, h);
        var r = 2;
        var rate = m.LinesOf(po).ToDictionary(l => l.Id, l => l.Rate);
        foreach (var g in cols.GroupBy(c => c.MirNo))
        {
            var mir = m.Mirs.FirstOrDefault(x => x.MirNo == g.Key);
            var lines = g.SelectMany(c => m.LinesOf(c.Dn)).Where(l => l.PoLineId != null).ToList();
            ws.Cell(r, 1).Value = g.Key;
            if (mir?.MirDate is DateTime d) { ws.Cell(r, 2).Value = d; ws.Cell(r, 2).Style.NumberFormat.Format = "dd-mmm-yy"; }
            ws.Cell(r, 3).Value = mir?.Status ?? "NO MIR";
            ws.Cell(r, 4).Value = string.Join(", ", g.Select(c => c.Dn.DnNo));
            ws.Cell(r, 5).Value = lines.Count;
            ws.Cell(r, 6).Value = lines.Sum(l => l.Qty);
            ws.Cell(r, 7).Value = Math.Round(lines.Sum(l => l.Qty * rate.GetValueOrDefault(l.PoLineId!.Value)), 2);
            if (mir is null) ws.Range(r, 1, r, h.Length).Style.Fill.BackgroundColor = Yellow;
            r++;
        }
        ws.Range(2, 6, Math.Max(2, r), 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Columns().AdjustToContents();
    }

    private static void DnLines(IXLWorksheet ws, MaterialsSnapshot m, MatPo po, List<DnColumn> cols)
    {
        string[] h = { "MIR", "DN", "DN DATE", "ITEM", "MATERIAL CODE", "DESCRIPTION", "BATCH / DRUM", "DN QTY", "DN UNIT", "QTY (PO UNIT)", "UNIT", "PO LINE", "STATUS", "NOTE", "INVOICE" };
        Header(ws, h);
        var poLines = m.LinesOf(po).ToDictionary(l => l.Id);
        var locks = m.Locks.ToDictionary(k => k.DnLineId);
        var r = 2;
        foreach (var c in cols)
            foreach (var l in m.LinesOf(c.Dn))
            {
                object?[] v = { c.MirNo, c.Dn.DnNo, c.Dn.DnDate, l.ItemNo, l.ItemCode, l.Description, l.Batch, l.RawQty, l.RawUnit, l.Qty, l.Unit,
                    l.PoLineId is long id && poLines.TryGetValue(id, out var pl) ? pl.LineNo : null, l.MatchStatus, l.MatchNote,
                    locks.TryGetValue(l.Id, out var k) ? $"INV-{k.InvoiceNo:00}" : "" };
                for (var i = 0; i < v.Length; i++)
                    ws.Cell(r, i + 1).Value = v[i] switch { null => Blank.Value, DateTime d => d, double d => d, int n => n, _ => v[i]!.ToString() };
                if (l.MatchStatus is MatchStatus.OverPo or MatchStatus.NotOnPo) ws.Cell(r, 13).Style.Fill.BackgroundColor = Red;
                else if (l.MatchStatus is MatchStatus.QtyMismatch or MatchStatus.DnWithoutMir) ws.Cell(r, 13).Style.Fill.BackgroundColor = Yellow;
                r++;
            }
        ws.Column(3).Style.NumberFormat.Format = "dd-mmm-yy";
        if (r > 2) ws.Range(1, 1, r - 1, h.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(r, 300));
    }

    private static void Header(IXLWorksheet ws, string[] h)
    {
        for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        var hr = ws.Range(1, 1, 1, h.Length);
        hr.Style.Fill.BackgroundColor = Grey; hr.Style.Font.Bold = true; hr.Style.Font.FontColor = XLColor.Black;
    }

    private static string Col(int c) => XLHelper.GetColumnLetterFromNumber(c);
}
