using System.Globalization;
using ClosedXML.Excel;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] Everything needed to print one sheet's takeoff (PDF pages + Excel workbook).</summary>
public sealed class TakeoffOutputInput
{
    public required DwgSheet Sheet { get; init; }
    /// <summary>Unit / room type or drawing name in the title ("1BR-A").</summary>
    public string TypeLabel { get; init; } = "";
    /// <summary>Takeoff revision printed as [REV nn].</summary>
    public int Revision { get; init; }
    public List<DwgHit> Hits { get; init; } = new();
    public required IReadOnlyDictionary<long, DwgSymbol> Symbols { get; init; }
    public required DrawingSettings Settings { get; init; }
    public List<DwgRun> Runs { get; init; } = new();
    public IReadOnlyDictionary<long, DwgLinearClass> Classes { get; init; } = new Dictionary<long, DwgLinearClass>();
    public List<PointLength> Lengths { get; init; } = new();
    /// <summary>Raster of the sheet (for the SYMBOL crops and as the background of non-PDF sheets).</summary>
    public ColorImage? Picture { get; init; }
    /// <summary>Sheet units -> picture pixels (identity for PDF / image sheets; a scale for CAD / IFC models).</summary>
    public Affine2D SheetToPicture { get; init; } = Affine2D.Identity;
    /// <summary>DPI of the picture pixels (sheet DPI for PDF sheets).</summary>
    public int Dpi { get; init; } = 150;
    /// <summary>PDF kept as vector underneath (null = use the picture).</summary>
    public string? PdfPath { get; init; }
    public int PdfPage { get; init; } = 1;
}

/// <summary>[drawings] Builds the takeoff PDF pages (one per sheet x system + LENGTHS) and the companion Excel workbook.</summary>
public static class TakeoffOutput
{
    public static string Title(TakeoffOutputInput i, string system) =>
        $"QS TAKEOFF - {(i.TypeLabel.Length > 0 ? i.TypeLabel : i.Sheet.Title.Length > 0 ? i.Sheet.Title : i.Sheet.SheetNo)} - {system}  [REV {i.Revision:00}]";

    public static List<string> Notes(DrawingSettings s, string? workbookName = null) => new List<string>
    {
        "Dark red circle = WALL item (1st fix)  |  Amber circle = CEILING item (flexible)",
        $"Number on circle = MARK NO. (DETAIL sheet of {workbookName ?? s.TakeoffWorkbookName})",
    }.Concat(s.RuleNotes).ToList();

    public static List<TakeoffPage> Pages(TakeoffOutputInput i)
    {
        TakeoffCounter.NumberMarks(i.Hits, i.Symbols);
        var pages = new List<TakeoffPage>();
        var tables = TakeoffCounter.Tables(i.Hits, i.Symbols, i.Settings);
        var crops = new Dictionary<(long, string), byte[]?>();
        foreach (var t in tables)
        {
            var sysHits = i.Hits.Where(h => i.Symbols.TryGetValue(h.SymbolId, out var s) && TakeoffCounter.SystemOf(s) == t.System).ToList();
            var rows = new List<PanelRow>();
            foreach (var r in t.Rows)
            {
                var key = (r.SymbolId, r.Mount);
                if (!crops.ContainsKey(key))
                {
                    var example = sysHits.Where(h => h.SymbolId == r.SymbolId && DwgHitStatus.Counts(h.Status) && TakeoffCounter.MountOf(h, i.Symbols[h.SymbolId], i.Settings) == r.Mount)
                        .OrderByDescending(h => h.Score).FirstOrDefault();
                    crops[key] = example != null && i.Picture != null ? SymbolCrop(i, example, r.Mount == "C" ? TakeoffPdf.Amber : TakeoffPdf.DarkRed) : null;
                }
                var cells = new List<string> { "", r.Tag + (r.Excluded ? " (EXCLUDED)" : ""), r.System, r.Mount };
                cells.AddRange(t.Columns.Select(c => r.Excluded ? "-" : TakeoffPdf.Num(r.Columns.GetValueOrDefault(c))));
                rows.Add(new PanelRow(cells, crops[key]));
            }
            var total = new List<string> { "TOTAL THIS SHEET", "", "", "" };
            total.AddRange(t.Columns.Select(c => t.Totals.GetValueOrDefault(c).ToString("0.##", CultureInfo.InvariantCulture)));
            var counted = sysHits.Count(h => DwgHitStatus.Counts(h.Status) && !i.Symbols[h.SymbolId].Excluded);
            var table = new PanelTable
            {
                Headers = new List<string> { "SYMBOL", "ITEM / TAG", "SYSTEM", "C/W" }.Concat(t.Columns).ToList(),
                Widths = new List<double> { 1.1, 3.2, 1.6, 0.8 }.Concat(t.Columns.Select(_ => 0.85)).ToList(),
                Rows = rows, Total = new PanelRow(total), Footer = $"{counted} marks on this sheet", ImageColumn = 0,
                BoldColumns = Enumerable.Range(4, t.Columns.Count).ToHashSet(),
            };
            pages.Add(new TakeoffPage
            {
                Title = Title(i, t.System), Subtitle = i.Sheet.FileName, Notes = Notes(i.Settings), Tables = { table },
                Marks = sysHits.Where(h => DwgHitStatus.Counts(h.Status)).Select(h => Mark(i, h)).ToList(),
                PdfPath = i.PdfPath, PdfPage = i.PdfPage, ImagePng = i.PdfPath is null ? i.Picture?.ToPng() : null,
                ImageWidthPx = i.Picture?.Width ?? 0, ImageHeightPx = i.Picture?.Height ?? 0, Dpi = i.Dpi,
            });
        }
        if (i.Runs.Count > 0 || i.Lengths.Count > 0) pages.Add(LengthsPage(i));
        return pages;
    }

    private static MarkSpec Mark(TakeoffOutputInput i, DwgHit h)
    {
        var s = i.Symbols[h.SymbolId];
        var c = i.SheetToPicture.Apply(h.Center);
        var r = Math.Max(h.W, h.H) * i.SheetToPicture.Scale * 0.62;
        var ceiling = TakeoffCounter.MountOf(h, s, i.Settings) == "C";
        return new MarkSpec(c, r, ceiling ? TakeoffPdf.Amber : TakeoffPdf.DarkRed, h.MarkNo > 0 ? h.MarkNo.ToString(CultureInfo.InvariantCulture) : "", s.Excluded);
    }

    /// <summary>A small picture of one counted mark (symbol + its circle + number) for the SYMBOL column.</summary>
    public static byte[] SymbolCrop(TakeoffOutputInput i, DwgHit h, Rgb colour)
    {
        var pic = i.Picture!;
        var c = i.SheetToPicture.Apply(h.Center);
        var r = Math.Max(h.W, h.H) * i.SheetToPicture.Scale * 0.62;
        var size = (int)Math.Ceiling(Math.Max(24, r * 3.2));
        var crop = pic.Crop((int)(c.X - size / 2.0), (int)(c.Y - size / 2.0), size, size);
        crop.Circle(size / 2.0, size / 2.0, r, colour, Math.Max(1.5, size / 30.0));
        if (h.MarkNo > 0) { crop.FillRect(size / 2.0 + r * 0.5, size / 2.0 - r * 1.1, 4 * 2 * h.MarkNo.ToString(CultureInfo.InvariantCulture).Length + 2, 12, TakeoffPdf.TagYellow); crop.Text(size / 2.0 + r * 0.5 + 1, size / 2.0 - r * 1.1 + 1, h.MarkNo.ToString(CultureInfo.InvariantCulture), TakeoffPdf.DarkRed, 2); }
        return (crop.Width > 96 ? crop.Resize(96, 96) : crop).ToPng();
    }

    private static TakeoffPage LengthsPage(TakeoffOutputInput i)
    {
        var tables = new List<PanelTable>();
        // runs by class (system / item / size)
        var byClass = i.Runs.Where(r => DwgHitStatus.Counts(r.Status)).GroupBy(r => r.ClassId).Select(g =>
        {
            var c = i.Classes.GetValueOrDefault(g.Key);
            return new List<string> { c?.System ?? "", c?.Name ?? g.First().ClassName, c?.Size ?? "", g.Count().ToString(CultureInfo.InvariantCulture), g.Sum(r => r.LengthM).ToString("0.0", CultureInfo.InvariantCulture) };
        }).ToList();
        if (byClass.Count > 0)
            tables.Add(new PanelTable
            {
                Headers = new() { "SYSTEM", "ITEM / SIZE", "SIZE", "RUNS", "LENGTH m" }, Widths = new() { 1.4, 3, 1.2, 0.8, 1.2 },
                Rows = byClass.Select(c => new PanelRow(c)).ToList(), BoldColumns = new() { 4 },
                Total = new PanelRow(new List<string> { "TOTAL MEASURED", "", "", "", i.Runs.Where(r => DwgHitStatus.Counts(r.Status)).Sum(r => r.LengthM).ToString("0.0", CultureInfo.InvariantCulture) }),
            });
        if (i.Lengths.Count > 0)
        {
            tables.Add(new PanelTable
            {
                Headers = new() { "MARK", "TO", "HORIZ m", "RISE m", "DROP m", "RISER m", "TERM m", "SPARE", "TOTAL m" },
                Widths = new() { 0.6, 2.4, 0.9, 0.8, 0.8, 0.8, 0.8, 0.7, 0.9 },
                Rows = i.Lengths.Take(28).Select(l => new PanelRow(new List<string>
                {
                    l.MarkNo.ToString(CultureInfo.InvariantCulture), l.To, F(l.HorizontalM), F(l.RiseM), F(l.DropM), F(l.RiserM), F(l.TerminationM),
                    (l.SparePct * 100).ToString("0", CultureInfo.InvariantCulture) + "%", l.Traceable ? F(l.TotalM) : "n/a",
                }, Highlight: !l.Traceable)).ToList(),
                BoldColumns = new() { 8 },
                Footer = i.Lengths.Count > 28 ? $"... {i.Lengths.Count - 28} more in the LENGTHS sheet of the workbook" : "",
            });
            var avg = CableLengths.Averages(i.Lengths, l => $"{l.System} {l.Item}".Trim());
            tables.Add(new PanelTable
            {
                Headers = new() { "AVERAGE BY ITEM", "POINTS", "MIN m", "AVG m", "MAX m", "TOTAL m" }, Widths = new() { 2.6, 0.8, 0.8, 0.8, 0.8, 1 },
                Rows = avg.Select(a => new PanelRow(new List<string> { a.Group, a.Count.ToString(CultureInfo.InvariantCulture), F(a.Min), F(a.Avg), F(a.Max), F(a.Total) })).ToList(),
                BoldColumns = new() { 3 },
            });
        }
        var colours = new Dictionary<long, Rgb>();
        var runs = i.Runs.Where(r => DwgHitStatus.Counts(r.Status)).Select(r =>
        {
            if (!colours.TryGetValue(r.ClassId, out var c)) colours[r.ClassId] = c = i.Classes.TryGetValue(r.ClassId, out var cl) && cl.ColourHex.Length > 0 ? Rgb.Parse(cl.ColourHex, Rgb.Palette(colours.Count + 1)) : Rgb.Palette(colours.Count + 1);
            return new RunSpec(Poly.ParsePoints(r.Points).Select(i.SheetToPicture.Apply).ToList(), c, $"{r.LengthM:0.0} m");
        }).ToList();
        runs.AddRange(i.Lengths.Where(l => l.Traceable && l.Path.Count > 1).Select(l => new RunSpec(l.Path.Select(i.SheetToPicture.Apply).ToList(), new Rgb(0, 0x66, 0xCC), "")));
        return new TakeoffPage
        {
            Title = Title(i, "LENGTHS"), Subtitle = i.Sheet.FileName,
            Notes = new List<string>
            {
                $"Length = horizontal route + rise/drop to the device (ceiling + half void - mounting height; H= on the drawing wins) + drop at the DB + riser + 2 x termination {i.Settings.TerminationAllowanceM:0.##} m, plus spare {i.Settings.SparePct * 100:0} %",
                $"15 m rule: per point qty = max(1, L / {i.Settings.LengthRuleM:0}) - groups for the length check: {Trunc(CableLengths.LengthGroups(i.Lengths), 140)}",
            },
            Tables = tables, Runs = runs,
            PdfPath = i.PdfPath, PdfPage = i.PdfPage, ImagePng = i.PdfPath is null ? i.Picture?.ToPng() : null,
            ImageWidthPx = i.Picture?.Width ?? 0, ImageHeightPx = i.Picture?.Height ?? 0, Dpi = i.Dpi,
        };
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + " ...";
    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ Excel

    private static readonly XLColor Grey = XLColor.FromHtml("#A6A6A6");

    private static void Header(IXLWorksheet ws, int row, IReadOnlyList<string> cols)
    {
        for (var c = 0; c < cols.Count; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = cols[c];
            cell.Style.Fill.BackgroundColor = Grey;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.Black;
        }
    }

    /// <summary>DETAIL (one row per mark), SUMMARY (type x system x item x stage columns), PROPOSED QTY (room x stage x item), LENGTHS, RUNS.</summary>
    public static void Workbook(string path, IReadOnlyList<TakeoffOutputInput> sheets, IReadOnlyList<QtyProposal>? proposals = null)
    {
        using var wb = new XLWorkbook();
        var settings = sheets.FirstOrDefault()?.Settings ?? new DrawingSettings();
        var cols = settings.StageRules.Select(r => r.Column).Distinct().ToList();
        var d = wb.AddWorksheet("DETAIL");
        Header(d, 1, new[] { "MARK NO", "SHEET / PAGE", "DRAWING", "ROOM", "ITEM / TAG", "SYSTEM", "C/W" }.Concat(cols).Concat(new[] { "X", "Y", "CONFIDENCE", "SOURCE", "STATUS" }).ToList());
        var row = 2;
        foreach (var i in sheets)
        {
            TakeoffCounter.NumberMarks(i.Hits, i.Symbols);
            foreach (var h in i.Hits.Where(h => i.Symbols.ContainsKey(h.SymbolId)).OrderBy(h => TakeoffCounter.SystemOf(i.Symbols[h.SymbolId])).ThenBy(h => h.MarkNo))
            {
                var s = i.Symbols[h.SymbolId];
                var mount = TakeoffCounter.MountOf(h, s, i.Settings);
                var cells = StageRules.Cells(s, i.Settings.StageRules, mount);
                var col = 1;
                d.Cell(row, col++).Value = h.MarkNo;
                d.Cell(row, col++).Value = $"{i.Sheet.SheetNo} p{i.Sheet.Page} {TakeoffCounter.SystemOf(s)}";
                d.Cell(row, col++).Value = i.Sheet.FileName;
                d.Cell(row, col++).Value = h.Room;
                d.Cell(row, col++).Value = TakeoffCounter.TagOf(s) + (s.Excluded ? " (EXCLUDED)" : "");
                d.Cell(row, col++).Value = TakeoffCounter.SystemOf(s);
                d.Cell(row, col++).Value = mount;
                foreach (var c in cols)
                {
                    var q = DwgHitStatus.Counts(h.Status) ? cells.Where(x => x.Column == c).Sum(x => x.Qty) : 0;
                    d.Cell(row, col++).Value = q;
                }
                d.Cell(row, col++).Value = Math.Round(h.Center.X, 1);
                d.Cell(row, col++).Value = Math.Round(h.Center.Y, 1);
                d.Cell(row, col++).Value = Math.Round(h.Score, 3);
                d.Cell(row, col++).Value = h.Origin == DwgOrigins.Manual || h.Status == DwgHitStatus.Added ? "manual" : $"auto ({h.Origin.ToLowerInvariant()})";
                d.Cell(row, col).Value = h.Status;
                row++;
            }
        }
        d.SheetView.FreezeRows(1);
        d.Columns().AdjustToContents(1, Math.Min(row, 500));

        var sm = wb.AddWorksheet("SUMMARY");
        Header(sm, 1, new[] { "TYPE", "SYSTEM", "ITEM / TAG", "C/W", "MARKS" }.Concat(cols).ToList());
        row = 2;
        foreach (var i in sheets)
            foreach (var t in TakeoffCounter.Tables(i.Hits, i.Symbols, i.Settings))
            {
                foreach (var r in t.Rows)
                {
                    var col = 1;
                    sm.Cell(row, col++).Value = i.TypeLabel.Length > 0 ? i.TypeLabel : i.Sheet.SheetNo;
                    sm.Cell(row, col++).Value = t.System;
                    sm.Cell(row, col++).Value = r.Tag + (r.Excluded ? " (EXCLUDED)" : "");
                    sm.Cell(row, col++).Value = r.Mount;
                    sm.Cell(row, col++).Value = r.Marks;
                    foreach (var c in cols) sm.Cell(row, col++).Value = r.Excluded ? 0 : r.Columns.GetValueOrDefault(c);
                    row++;
                }
                sm.Cell(row, 1).Value = "TOTAL"; sm.Cell(row, 2).Value = t.System; sm.Cell(row, 5).Value = t.Marks;
                for (var k = 0; k < cols.Count; k++) sm.Cell(row, 6 + k).Value = t.Totals.GetValueOrDefault(cols[k]);
                sm.Range(row, 1, row, 5 + cols.Count).Style.Font.Bold = true;
                sm.Range(row, 1, row, 5 + cols.Count).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2A8");
                row += 2;
            }
        sm.Columns().AdjustToContents();

        if (proposals != null)
        {
            var pq = wb.AddWorksheet("PROPOSED QTY");
            Header(pq, 1, new[] { "BUILDING", "ROOM", "STAGE", "ITEM", "UNIT", "CURRENT PROJECT QTY", "PROPOSED", "DIFF", "STATUS", "NOTE" });
            row = 2;
            foreach (var p in proposals)
            {
                pq.Cell(row, 1).Value = p.Building; pq.Cell(row, 2).Value = p.Room; pq.Cell(row, 3).Value = p.Stage; pq.Cell(row, 4).Value = p.Item; pq.Cell(row, 5).Value = p.Unit;
                if (p.Current is double cur) pq.Cell(row, 6).Value = cur;
                pq.Cell(row, 7).Value = p.Proposed; pq.Cell(row, 8).Value = p.Diff; pq.Cell(row, 9).Value = p.Status; pq.Cell(row, 10).Value = p.Note;
                row++;
            }
            pq.Columns().AdjustToContents();
        }

        var lengths = sheets.SelectMany(s => s.Lengths).ToList();
        if (lengths.Count > 0)
        {
            var ls = wb.AddWorksheet("LENGTHS");
            Header(ls, 1, new[] { "MARK / RUN", "FROM", "TO", "ROOM", "ROOM TYPE", "SYSTEM", "ITEM", "CABLE SIZE", "HORIZONTAL m", "RISE m", "DROP m", "RISER m", "TERMINATION m", "SPARE %", "TOTAL m", "15 m POINTS", "NOTE" });
            row = 2;
            foreach (var l in lengths)
            {
                var col = 1;
                ls.Cell(row, col++).Value = l.MarkNo; ls.Cell(row, col++).Value = l.From; ls.Cell(row, col++).Value = l.To; ls.Cell(row, col++).Value = l.Room; ls.Cell(row, col++).Value = l.RoomType;
                ls.Cell(row, col++).Value = l.System; ls.Cell(row, col++).Value = l.Item; ls.Cell(row, col++).Value = l.CableSize;
                ls.Cell(row, col++).Value = l.HorizontalM; ls.Cell(row, col++).Value = l.RiseM; ls.Cell(row, col++).Value = l.DropM; ls.Cell(row, col++).Value = l.RiserM;
                ls.Cell(row, col++).Value = l.TerminationM; ls.Cell(row, col++).Value = l.SparePct * 100; ls.Cell(row, col++).Value = l.Traceable ? l.TotalM : 0;
                ls.Cell(row, col++).Value = l.Traceable ? l.PointsEquivalent : 0; ls.Cell(row, col).Value = l.Note;
                row++;
            }
            row += 1;
            foreach (var (title, key) in new (string, Func<PointLength, string>)[] { ("AVERAGE BY ITEM", l => $"{l.System} {l.Item}"), ("AVERAGE BY ROOM TYPE", l => l.RoomType), ("AVERAGE BY SYSTEM", l => l.System), ("TOTAL BY CABLE SIZE", l => l.CableSize) })
            {
                Header(ls, row, new[] { title, "POINTS", "MIN m", "AVG m", "MAX m", "TOTAL m" });
                row++;
                foreach (var a in CableLengths.Averages(lengths, key))
                {
                    ls.Cell(row, 1).Value = a.Group; ls.Cell(row, 2).Value = a.Count; ls.Cell(row, 3).Value = a.Min; ls.Cell(row, 4).Value = a.Avg; ls.Cell(row, 5).Value = a.Max; ls.Cell(row, 6).Value = a.Total;
                    row++;
                }
                row++;
            }
            ls.Columns().AdjustToContents();
        }

        var runs = sheets.SelectMany(s => s.Runs.Select(r => (s, r))).ToList();
        if (runs.Count > 0)
        {
            var rs = wb.AddWorksheet("RUNS");
            Header(rs, 1, new[] { "SHEET", "CLASS", "SYSTEM", "SIZE", "ROOM", "LENGTH m", "SOURCE", "STATUS", "NOTE" });
            row = 2;
            foreach (var (s, r) in runs)
            {
                var c = s.Classes.GetValueOrDefault(r.ClassId);
                rs.Cell(row, 1).Value = s.Sheet.SheetNo; rs.Cell(row, 2).Value = r.ClassName; rs.Cell(row, 3).Value = c?.System ?? ""; rs.Cell(row, 4).Value = c?.Size ?? "";
                rs.Cell(row, 5).Value = r.Room; rs.Cell(row, 6).Value = r.LengthM; rs.Cell(row, 7).Value = r.Origin; rs.Cell(row, 8).Value = r.Status; rs.Cell(row, 9).Value = r.Note;
                row++;
            }
            rs.Columns().AdjustToContents();
        }
        wb.SaveAs(path);
    }
}
