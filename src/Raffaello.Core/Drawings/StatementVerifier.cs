using System.Globalization;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] A claimed quantity (ledger line or imported statement row).</summary>
public sealed record ClaimedQty(string Room, string Stage, string Item, double Qty, string Source = "");

/// <summary>[drawings] Inputs for one marked-up statement page.</summary>
public sealed class StatementPageInput
{
    public required ColorImage Page { get; init; }
    public int PageNo { get; init; } = 1;
    public int Dpi { get; init; } = 150;
    /// <summary>Clean drawing (optional): its hits are checked for highlighter after aligning the page to it.</summary>
    public GrayImage? Clean { get; init; }
    public List<DwgHit>? CleanHits { get; init; }
    /// <summary>Page -> clean sheet transform from clicked point pairs (skips the automatic alignment).</summary>
    public Affine2D? PageToClean { get; init; }
    /// <summary>Library templates for detecting symbols on the page itself (used when there is no clean drawing).</summary>
    public List<SymbolTemplate>? Templates { get; init; }
    public MatchOptions? Match { get; init; }
    public required IReadOnlyDictionary<long, DwgSymbol> Symbols { get; init; }
    /// <summary>Room boundaries in clean-sheet units (or page units when there is no clean sheet).</summary>
    public RoomAssigner? Rooms { get; init; }
    /// <summary>Typical unit: the page's counts apply to each of these rooms ("P2-106, P3-103, P4-04").</summary>
    public List<string> RoomList { get; init; } = new();
    public HighlightOptions Highlight { get; init; } = new();
    public required DrawingSettings Settings { get; init; }
    /// <summary>Metres per page pixel (scale) for highlighted run lengths; 0 = unknown.</summary>
    public double MetresPerPx { get; init; }
    /// <summary>Only count targets of this stage (the statement's stage), "" = all.</summary>
    public string Stage { get; init; } = "";
}

public sealed class StatementSymbol
{
    public required DwgHit Hit { get; init; }
    /// <summary>Centre on the statement page (pixels).</summary>
    public PointD PageCenter { get; init; }
    public double PageRadius { get; init; }
    public bool Highlighted { get; init; }
    public double Coverage { get; init; }
}

public sealed class StatementPageResult
{
    public int PageNo { get; init; }
    public required HighlightResult Highlights { get; init; }
    public Affine2D? PageToClean { get; init; }
    public double AlignScore { get; init; }
    public List<StatementSymbol> Symbols { get; } = new();
    public Dictionary<(string Room, string Stage, string Item), double> Highlighted { get; } = new();
    public Dictionary<(string Room, string Stage, string Item), double> DrawingTotal { get; } = new();
    public List<HighlightBlob> UnmatchedSpots { get; } = new();
    public double RunLengthPx { get; set; }
    public double RunLengthM { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// [drawings] Marked-up statement verification: highlighter masks (HSV) on each scanned page, symbols under the highlights (from the
/// aligned clean drawing's takeoff, or detected on the page with the library), counts per room x stage x item, highlighted runs
/// (length for the 15 m checks), then claimed vs highlighted vs drawing total with the differences flagged.
/// </summary>
public static class StatementVerifier
{
    public static StatementPageResult VerifyPage(StatementPageInput i)
    {
        var hl = HighlightDetector.Detect(i.Page, i.Highlight);
        var hlMask = hl.Mask.Dilate(2);
        Affine2D? pageToClean = i.PageToClean;
        double score = 0;
        var res0 = new List<StatementSymbol>();
        var notes = new List<string>();
        if (i.Clean != null && i.CleanHits != null)
        {
            if (pageToClean is null)
            {
                var reg = Registration.Align(i.Clean, i.Page.ToInkGray());
                pageToClean = reg.MovingToFixed; score = reg.Score;
                if (!reg.Reliable) notes.Add($"p{i.PageNo}: automatic alignment is weak (score {reg.Score:0.00}) - click 2-3 matching points to calibrate.");
            }
            var cleanToPage = pageToClean.Value.Inverse();
            foreach (var h in i.CleanHits.Where(h => DwgHitStatus.Counts(h.Status)))
            {
                var c = cleanToPage.Apply(h.Center);
                var r = Math.Max(h.W, h.H) * cleanToPage.Scale / 2;
                var cov = hlMask.Coverage((int)(c.X - r), (int)(c.Y - r), (int)(2 * r), (int)(2 * r));
                res0.Add(new StatementSymbol { Hit = h, PageCenter = c, PageRadius = r, Coverage = cov, Highlighted = cov >= i.Settings.HighlightCoverage });
            }
        }
        else if (i.Templates is { Count: > 0 })
        {
            var gray = i.Page.ToInkGray();
            foreach (var d in TemplateMatcher.Match(gray, i.Templates, i.Match ?? new MatchOptions()))
            {
                var h = new DwgHit { SymbolId = d.SymbolId, SymbolName = d.Name, X = d.X, Y = d.Y, W = d.W, H = d.H, Score = d.Score, Rotation = d.Rotation, Mirrored = d.Mirrored, Origin = DwgOrigins.Template };
                var r = Math.Max(d.W, d.H) / 2;
                var cov = hlMask.Coverage((int)d.X, (int)d.Y, (int)d.W, (int)d.H);
                res0.Add(new StatementSymbol { Hit = h, PageCenter = d.Center, PageRadius = r, Coverage = cov, Highlighted = cov >= i.Settings.HighlightCoverage });
            }
        }
        else notes.Add($"p{i.PageNo}: no symbol library and no clean drawing - only the highlighter marks were measured.");

        var res = new StatementPageResult { PageNo = i.PageNo, Highlights = hl, PageToClean = pageToClean, AlignScore = score };
        res.Symbols.AddRange(res0);
        res.Notes.AddRange(notes);
        // rooms: the typical-unit list wins, else boundaries (clean coordinates when aligned)
        foreach (var s in res.Symbols)
        {
            if (!i.Symbols.TryGetValue(s.Hit.SymbolId, out var sym)) continue;
            var rooms = i.RoomList.Count > 0 ? i.RoomList.Select(r => r.Trim().ToUpperInvariant()).ToList()
                : new List<string> { i.Rooms is { IsEmpty: false } ? i.Rooms.RoomAt(i.Clean != null ? s.Hit.Center : s.PageCenter) : RoomAssigner.Unassigned };
            foreach (var cell in StageRules.Cells(sym, i.Settings.StageRules, TakeoffCounter.MountOf(s.Hit, sym, i.Settings)))
            {
                if (i.Stage.Length > 0 && !cell.Stage.Equals(i.Stage, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var room in rooms)
                {
                    var key = (room, cell.Stage, cell.Item);
                    res.DrawingTotal[key] = res.DrawingTotal.GetValueOrDefault(key) + cell.Qty;
                    if (s.Highlighted) res.Highlighted[key] = res.Highlighted.GetValueOrDefault(key) + cell.Qty;
                }
            }
        }
        // spots with no symbol under them (a symbol missing from the library, or a highlighted note)
        foreach (var b in hl.Spots)
            if (!res.Symbols.Any(s => s.Highlighted && b.Box.IntersectionArea(new RectD(s.PageCenter.X - s.PageRadius, s.PageCenter.Y - s.PageRadius, 2 * s.PageRadius, 2 * s.PageRadius)) > 0))
                res.UnmatchedSpots.Add(b);
        res.RunLengthPx = hl.Strokes.Sum(s => s.LengthPx);
        var mpp = i.MetresPerPx > 0 ? i.MetresPerPx : 0;
        res.RunLengthM = Math.Round(res.RunLengthPx * mpp, 2);
        return res;
    }

    /// <summary>
    /// Claimed vs highlighted vs drawing total per room x stage x item over all pages. Flags: OVER CLAIM (claimed &gt; highlighted),
    /// ABOVE DRAWING (claimed &gt; drawing total), UNDER CLAIM, NOT CLAIMED (highlighted, nothing claimed), NOT HIGHLIGHTED, OK.
    /// </summary>
    public static List<DwgStatementLine> Compare(IEnumerable<StatementPageResult> pages, IEnumerable<ClaimedQty> claimed, double tolerance = 0.01)
    {
        var hl = new Dictionary<(string, string, string), double>();
        var total = new Dictionary<(string, string, string), double>();
        var pageOf = new Dictionary<(string, string, string), int>();
        foreach (var p in pages)
        {
            foreach (var (k, v) in p.Highlighted) { hl[k] = hl.GetValueOrDefault(k) + v; pageOf.TryAdd(k, p.PageNo); }
            foreach (var (k, v) in p.DrawingTotal) { total[k] = total.GetValueOrDefault(k) + v; pageOf.TryAdd(k, p.PageNo); }
        }
        var cl = claimed.GroupBy(c => (c.Room.Trim().ToUpperInvariant(), c.Stage.Trim().ToUpperInvariant(), c.Item.Trim().ToUpperInvariant())).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        var claimedStages = cl.Keys.Select(k => k.Item2).ToHashSet();
        var keys = hl.Keys.Concat(total.Keys).Concat(cl.Keys).Distinct()
            .Where(k => cl.Count == 0 || claimedStages.Contains(k.Item2))   // compare the stages that were claimed
            .OrderBy(k => k.Item1).ThenBy(k => k.Item2).ThenBy(k => k.Item3).ToList();
        var res = new List<DwgStatementLine>();
        foreach (var k in keys)
        {
            double c = cl.GetValueOrDefault(k), h = hl.GetValueOrDefault(k), t = total.GetValueOrDefault(k);
            string flag;
            if (c > t + tolerance && t > 0) flag = "ABOVE DRAWING";
            else if (c > tolerance && t <= tolerance) flag = "NOT ON DRAWING";
            else if (c > h + tolerance) flag = "OVER CLAIM";
            else if (h > c + tolerance && c <= tolerance) flag = "NOT CLAIMED";
            else if (h > c + tolerance) flag = "UNDER CLAIM";
            else if (c <= tolerance && h <= tolerance) flag = "NOT HIGHLIGHTED";
            else flag = "OK";
            res.Add(new DwgStatementLine { Page = pageOf.GetValueOrDefault(k), Room = k.Item1, Stage = k.Item2, Item = k.Item3, Claimed = c, Highlighted = h, DrawingTotal = t, Diff = Math.Round(c - h, 3), Flag = flag });
        }
        return res;
    }

    public static bool IsFlag(DwgStatementLine l) => l.Flag is "ABOVE DRAWING" or "NOT ON DRAWING" or "OVER CLAIM" or "UNDER CLAIM" or "NOT CLAIMED";

    /// <summary>Claims of one subcontractor (optionally one invoice / statement no.) from the ledger.</summary>
    public static List<ClaimedQty> FromLedger(IEnumerable<ClaimLine> claims, string subcontractor, int? invoiceNo = null, string? statementNo = null) =>
        Raffaello.Core.Ledger.LedgerRules.Effective(claims)
            .Where(c => c.Subcontractor.Equals(subcontractor, StringComparison.OrdinalIgnoreCase)
                        && (invoiceNo is null || c.InvoiceNo == invoiceNo) && (string.IsNullOrWhiteSpace(statementNo) || c.StatementNo.Equals(statementNo, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new ClaimedQty(c.Room, c.Stage, c.Item, c.Qty, $"{c.Subcontractor} INV {c.InvoiceNo}")).ToList();

    /// <summary>CSV ROOM, STAGE, ITEM, QTY (an imported site statement).</summary>
    public static List<ClaimedQty> FromCsv(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return new();
        var head = Csv.Split(lines[0]).Select(h => h.Trim().ToUpperInvariant()).ToList();
        int iR = head.IndexOf("ROOM"), iS = head.IndexOf("STAGE"), iI = head.IndexOf("ITEM"), iQ = head.FindIndex(h => h is "QTY" or "CLAIMED" or "QUANTITY");
        if (iR < 0 || iS < 0 || iI < 0 || iQ < 0) throw new InvalidDataException("The claimed CSV needs columns ROOM, STAGE, ITEM, QTY.");
        return lines.Skip(1).Select(Csv.Split).Where(c => c.Count > Math.Max(Math.Max(iR, iS), Math.Max(iI, iQ)))
            .Select(c => new ClaimedQty(c[iR].Trim().ToUpperInvariant(), c[iS].Trim().ToUpperInvariant(), c[iI].Trim().ToUpperInvariant(),
                double.TryParse(c[iQ], NumberStyles.Float, CultureInfo.InvariantCulture, out var q) ? q : 0, "CSV")).ToList();
    }

    /// <summary>The statement page in the house layout: page + left panel with the comparison and numbered marks on highlighted symbols.</summary>
    public static TakeoffPage Page(StatementPageResult r, IReadOnlyDictionary<long, DwgSymbol> symbols, IReadOnlyList<DwgStatementLine> lines, string title, string fileName,
        string? pdfPath, int pdfPage, ColorImage picture, int dpi)
    {
        var marks = new List<MarkSpec>();
        var n = 0;
        foreach (var s in r.Symbols.OrderBy(s => Math.Floor(s.PageCenter.Y / 60)).ThenBy(s => s.PageCenter.X))
        {
            var sym = symbols.GetValueOrDefault(s.Hit.SymbolId);
            var ceiling = sym != null && sym.Mount.StartsWith("C", StringComparison.OrdinalIgnoreCase);
            marks.Add(new MarkSpec(s.PageCenter, Math.Max(6, s.PageRadius * 1.25), s.Highlighted ? ceiling ? TakeoffPdf.Amber : TakeoffPdf.DarkRed : TakeoffPdf.ExclGrey,
                s.Highlighted ? (++n).ToString(CultureInfo.InvariantCulture) : ""));
        }
        foreach (var b in r.UnmatchedSpots) marks.Add(new MarkSpec(new PointD(b.Cx, b.Cy), Math.Max(b.W, b.H) / 2.0, new Rgb(0, 0x66, 0xCC), "?"));
        var pageLines = lines.Where(l => l.Page == r.PageNo || l.Page == 0).ToList();
        var table = new PanelTable
        {
            Headers = new() { "ROOM", "STAGE", "ITEM", "CLAIMED", "HIGHLIGHTED", "DRAWING", "DIFF", "FLAG" },
            Widths = new() { 1.4, 1.1, 1.3, 1, 1.1, 1, 0.8, 1.6 },
            Rows = pageLines.Take(30).Select(l => new PanelRow(new List<string>
            {
                l.Room, l.Stage, l.Item, TakeoffPdf.Num(l.Claimed), TakeoffPdf.Num(l.Highlighted), TakeoffPdf.Num(l.DrawingTotal), TakeoffPdf.Num(l.Diff), l.Flag,
            }, Highlight: IsFlag(l))).ToList(),
            BoldColumns = new() { 3, 4, 6 },
            Total = new PanelRow(new List<string> { "TOTAL", "", "", TakeoffPdf.Num(pageLines.Sum(l => l.Claimed)), TakeoffPdf.Num(pageLines.Sum(l => l.Highlighted)), TakeoffPdf.Num(pageLines.Sum(l => l.DrawingTotal)), TakeoffPdf.Num(pageLines.Sum(l => l.Diff)), $"{pageLines.Count(IsFlag)} flags" }),
            Footer = $"{n} highlighted symbols, {r.Symbols.Count(s => !s.Highlighted)} not highlighted (grey), {r.UnmatchedSpots.Count} highlight spots without a library symbol (blue ?), highlighted runs {(r.RunLengthM > 0 ? r.RunLengthM.ToString("0.0", CultureInfo.InvariantCulture) + " m" : r.RunLengthPx.ToString("0", CultureInfo.InvariantCulture) + " px (set the scale)")}",
        };
        var runs = r.Highlights.Strokes.Select(s => new RunSpec(Polylines.Simplify(OrderSkeleton(s.Skeleton), 2), new Rgb(0xFF, 0x00, 0xFF), s.LengthPx > 0 && r.RunLengthM > 0 ? $"{s.LengthPx * r.RunLengthM / Math.Max(1, r.RunLengthPx):0.0} m" : "")).ToList();
        return new TakeoffPage
        {
            Title = title, Subtitle = fileName,
            Notes = new()
            {
                "Dark red / amber circle = highlighted symbol counted  |  grey circle = symbol on the drawing, not highlighted  |  blue ? = highlight without a library symbol",
                "Magenta = highlighted run (route length for the 15 m check)",
            }.Concat(r.Notes).ToList(),
            Tables = { table }, Marks = marks, Runs = runs,
            PdfPath = pdfPath, PdfPage = pdfPage, ImagePng = pdfPath is null ? picture.ToPng() : null, ImageWidthPx = picture.Width, ImageHeightPx = picture.Height, Dpi = dpi,
        };
    }

    /// <summary>Nearest-neighbour ordering of skeleton pixels so the overlay polyline follows the stroke.</summary>
    private static List<PointD> OrderSkeleton(List<PointD> pts)
    {
        if (pts.Count < 3) return pts.ToList();
        var left = pts.ToList();
        var start = left.OrderBy(p => p.X + p.Y).First();
        var res = new List<PointD> { start }; left.Remove(start);
        while (left.Count > 0)
        {
            var last = res[^1];
            var next = left.MinBy(p => p.DistanceTo(last))!;
            if (next.DistanceTo(last) > 6) break;
            res.Add(next); left.Remove(next);
        }
        return res;
    }
}
