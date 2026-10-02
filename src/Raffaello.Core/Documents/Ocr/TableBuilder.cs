namespace Raffaello.Core.Documents.Ocr;

public sealed class TableCell
{
    public int Row { get; init; }
    public int Col { get; init; }
    public Box Box { get; init; }
    public List<OcrWord> Words { get; } = new();
    public string Text => string.Join(" ", LayoutBuilder.Lines(Words).Select(l => l.Text));
    public double Confidence => Words.Count == 0 ? 0 : Words.Min(w => w.Confidence);
    public bool IsEmpty => Words.Count == 0;
}

/// <summary>A reconstructed table: column x-ranges and row y-ranges (page-image pixels) with the words of each cell.</summary>
public sealed class TableGrid
{
    /// <summary>RULINGS (from ruling lines), WHITESPACE (column gaps), TEMPLATE (learned column ranges).</summary>
    public string Method { get; init; } = "";
    public List<(double X0, double X1)> Columns { get; } = new();
    public List<(double Y0, double Y1)> Rows { get; } = new();
    public TableCell[,] Cells { get; set; } = new TableCell[0, 0];
    public Box Bounds { get; set; }
    public int RowCount => Rows.Count;
    public int ColCount => Columns.Count;
    public TableCell this[int r, int c] => Cells[r, c];
    public IEnumerable<TableCell> RowCells(int r) => Enumerable.Range(0, ColCount).Select(c => Cells[r, c]);
    public IEnumerable<TableCell> ColumnCells(int c) => Enumerable.Range(0, RowCount).Select(r => Cells[r, c]);
}

/// <summary>
/// Table reconstruction from an OCR page: a grid from ruling lines when the page has them (scanned schedules, forms), else columns
/// from whitespace alignment (supplier documents, screenshots), or the column ranges stored in a learned template.
/// </summary>
public static class TableBuilder
{
    /// <summary>Grids found from the page's rulings (one per ruled block), largest first.</summary>
    public static List<TableGrid> FromRulings(OcrPage page, double minRowHeight = 12)
    {
        var res = new List<TableGrid>();
        var hs = page.Rulings.Where(r => r.Horizontal && r.Length > page.Width * 0.25).ToList();
        var vs = page.Rulings.Where(r => !r.Horizontal && r.Length > page.Height * 0.04).ToList();
        if (hs.Count < 2 || vs.Count < 2) return res;
        // table body = horizontals that are crossed by at least two verticals
        var xs = Cluster(vs.Select(v => v.Pos), Math.Max(6, page.Width * 0.004));
        var ys = Cluster(hs.Select(h => h.Pos), Math.Max(4, page.Height * 0.002));
        if (xs.Count < 2 || ys.Count < 2) return res;
        // keep verticals that span a meaningful part of the horizontal band
        var top = ys.First(); var bottom = ys.Last();
        var colsX = xs.Where(x => vs.Where(v => Math.Abs(v.Pos - x) < Math.Max(6, page.Width * 0.004)).Sum(v => v.Length) > (bottom - top) * 0.25).ToList();
        if (colsX.Count < 2) return res;
        var left = colsX.First(); var right = colsX.Last();
        var rowsY = ys.Where(y => hs.Where(h => Math.Abs(h.Pos - y) < Math.Max(4, page.Height * 0.002)).Sum(h => h.Length) > (right - left) * 0.4).ToList();
        if (rowsY.Count < 2) return res;
        var g = new TableGrid { Method = "RULINGS", Bounds = new Box(left, rowsY.First(), right - left, rowsY.Last() - rowsY.First()) };
        for (var i = 0; i + 1 < colsX.Count; i++) if (colsX[i + 1] - colsX[i] > 8) g.Columns.Add((colsX[i], colsX[i + 1]));
        for (var i = 0; i + 1 < rowsY.Count; i++) if (rowsY[i + 1] - rowsY[i] >= minRowHeight) g.Rows.Add((rowsY[i], rowsY[i + 1]));
        if (g.Columns.Count < 2 || g.Rows.Count < 1) return res;
        Fill(g, page.Words);
        res.Add(g);
        return res;
    }

    /// <summary>Columns from whitespace: x-intervals covered by words, separated by gaps that run through most lines.</summary>
    public static TableGrid? FromWhitespace(OcrPage page, Box? region = null, double minGapChars = 1.5)
    {
        var words = page.Words.Where(w => region is null || region.Value.Contains(w.Box.Cx, w.Box.Cy)).ToList();
        if (words.Count < 4) return null;
        var lines = LayoutBuilder.Lines(words).Where(l => l.Words.Count >= 2).ToList();
        if (lines.Count < 2) return null;
        var cw = LayoutBuilder.CharWidth(words);
        var x0 = words.Min(w => w.Box.X); var x1 = words.Max(w => w.Box.Right);
        var n = (int)Math.Ceiling(x1 - x0) + 1;
        var cover = new int[n];
        foreach (var l in lines)
            foreach (var w in l.Words)
                for (var x = (int)(w.Box.X - x0); x < Math.Min(n, (int)(w.Box.Right - x0)); x++) cover[x]++;
        var thresh = Math.Max(1, lines.Count * 0.15);
        var cols = new List<(double, double)>();
        var start = -1; var gap = 0;
        for (var x = 0; x < n; x++)
        {
            if (cover[x] >= thresh) { if (start < 0) start = x; gap = 0; }
            else if (start >= 0)
            {
                gap++;
                if (gap >= cw * minGapChars) { cols.Add((x0 + start, x0 + x - gap)); start = -1; gap = 0; }
            }
        }
        if (start >= 0) cols.Add((x0 + start, x1));
        if (cols.Count < 2) return null;
        // widen columns to the midpoints of the gaps so stray words still land somewhere
        var widened = new List<(double, double)>();
        for (var i = 0; i < cols.Count; i++)
        {
            var l = i == 0 ? x0 - 1 : (cols[i - 1].Item2 + cols[i].Item1) / 2;
            var r = i == cols.Count - 1 ? x1 + 1 : (cols[i].Item2 + cols[i + 1].Item1) / 2;
            widened.Add((l, r));
        }
        return FromColumns(words, widened, "WHITESPACE");
    }

    /// <summary>A table from given column x-ranges (a learned template), rows = text lines.</summary>
    public static TableGrid FromColumns(IEnumerable<OcrWord> words, IReadOnlyList<(double X0, double X1)> columns, string method = "TEMPLATE")
    {
        var list = words.ToList();
        var lines = LayoutBuilder.Lines(list);
        var g = new TableGrid { Method = method };
        g.Columns.AddRange(columns);
        foreach (var l in lines) g.Rows.Add((l.Box.Y, l.Box.Bottom));
        g.Bounds = Box.Union(list.Select(w => w.Box));
        g.Cells = new TableCell[g.Rows.Count, g.Columns.Count];
        for (var r = 0; r < g.Rows.Count; r++)
            for (var c = 0; c < g.Columns.Count; c++)
                g.Cells[r, c] = new TableCell { Row = r, Col = c, Box = new Box(g.Columns[c].X0, g.Rows[r].Y0, g.Columns[c].X1 - g.Columns[c].X0, g.Rows[r].Y1 - g.Rows[r].Y0) };
        for (var r = 0; r < lines.Count; r++)
            foreach (var w in lines[r].Words)
            {
                var c = BestColumn(g.Columns, w.Box);
                if (c >= 0) g.Cells[r, c].Words.Add(w);
            }
        return g;
    }

    private static void Fill(TableGrid g, IEnumerable<OcrWord> words)
    {
        g.Cells = new TableCell[g.Rows.Count, g.Columns.Count];
        for (var r = 0; r < g.Rows.Count; r++)
            for (var c = 0; c < g.Columns.Count; c++)
                g.Cells[r, c] = new TableCell { Row = r, Col = c, Box = new Box(g.Columns[c].X0, g.Rows[r].Y0, g.Columns[c].X1 - g.Columns[c].X0, g.Rows[r].Y1 - g.Rows[r].Y0) };
        foreach (var w in words)
        {
            var r = g.Rows.FindIndex(y => w.Box.Cy >= y.Y0 && w.Box.Cy <= y.Y1);
            if (r < 0) continue;
            var c = BestColumn(g.Columns, w.Box);
            if (c >= 0) g.Cells[r, c].Words.Add(w);
        }
    }

    private static int BestColumn(IReadOnlyList<(double X0, double X1)> cols, Box b)
    {
        var best = -1; double ov = 0;
        for (var i = 0; i < cols.Count; i++)
        {
            var o = Math.Max(0, Math.Min(cols[i].X1, b.Right) - Math.Max(cols[i].X0, b.X));
            if (o > ov) { ov = o; best = i; }
        }
        if (best < 0)
            for (var i = 0; i < cols.Count; i++) if (b.Cx >= cols[i].X0 && b.Cx <= cols[i].X1) return i;
        return best;
    }

    /// <summary>1-D clustering of positions (mean of each cluster), sorted.</summary>
    public static List<double> Cluster(IEnumerable<double> values, double tol)
    {
        var res = new List<List<double>>();
        foreach (var v in values.OrderBy(x => x))
        {
            if (res.Count > 0 && v - res[^1][^1] <= tol) res[^1].Add(v);
            else res.Add(new List<double> { v });
        }
        return res.Select(c => c.Average()).ToList();
    }
}
