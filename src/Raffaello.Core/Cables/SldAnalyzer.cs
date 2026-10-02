using System.Globalization;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Cables;

/// <summary>A piece of text on a drawing page (box in page units, origin top-left, y down).</summary>
public sealed record SldText(string Text, double X, double Y, double W, double H)
{
    public double Cx => X + W / 2;
    public double Cy => Y + H / 2;
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>A straight line piece of a drawing (feeder, bus bar, box edge ...).</summary>
public readonly record struct SldSegment(double X1, double Y1, double X2, double Y2)
{
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    public double MinX => Math.Min(X1, X2);
    public double MaxX => Math.Max(X1, X2);
    public double MinY => Math.Min(Y1, Y2);
    public double MaxY => Math.Max(Y1, Y2);
}

public readonly record struct SldRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Contains(double x, double y, double pad = 0) => x >= X - pad && x <= Right + pad && y >= Y - pad && y <= Bottom + pad;
    public SldRect Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
}

/// <summary>One page (PDF page, CAD model space, OCR'd scan) as text + line geometry.</summary>
public sealed class SldPage
{
    public int Number { get; init; } = 1;
    public double Width { get; init; }
    public double Height { get; init; }
    public List<SldText> Texts { get; } = new();
    public List<SldSegment> Segments { get; } = new();
    public List<SldRect> Rects { get; } = new();
    /// <summary>Text that came from block attributes (CAD) with its tag - panel name tags are trusted more.</summary>
    public Dictionary<SldText, string> AttributeTags { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>A sheet with a drawing frame (PDF / scan): lines longer than 90 % of the sheet are the frame, not wiring. CAD model space has none.</summary>
    public bool HasFrame { get; init; } = true;
    /// <summary>OCR page: geometry is less reliable (confidence lowered).</summary>
    public bool FromScan { get; init; }
}

/// <summary>A panel / equipment label found on a page.</summary>
public sealed record SldNode(string Name, PanelName Parsed, SldRect Box, bool Boxed, SldText Label);

/// <summary>A FROM -> TO connection found on a page, before it becomes a <see cref="CableRun"/>.</summary>
public sealed class SldEdge
{
    public required SldNode From { get; init; }
    public required SldNode To { get; init; }
    public List<CableSize> Sizes { get; } = new();
    public string Breaker { get; set; } = "";
    public double? Length { get; set; }
    public double Confidence { get; set; }
    public string How { get; set; } = "";
    public int Component { get; init; } = -1;
}

public sealed class SldPageResult
{
    public List<SldNode> Nodes { get; } = new();
    public List<SldEdge> Edges { get; } = new();
    public List<string> Issues { get; } = new();
    /// <summary>Panel -> feeder parent found as "FED FROM ..." text.</summary>
    public List<(SldNode Panel, string ParentName)> FedFrom { get; } = new();
}

/// <summary>
/// [cables] Reads single line diagrams from text + geometry, independent of the source (PDF vector text, DWG/DXF entities, OCR words):
/// panel labels (normalised names) inside boxes or at feeder ends, feeder lines joined into wires (end-to-end and T-junctions, crossings without
/// a junction stay apart), wires touching two or more panels become FROM -> TO edges (feeding side by panel rank, then by position: upper / left),
/// cable size annotations (4x16, 4C x 16mm², 4x240+1x120 E ...) and breaker ratings next to a wire are attached to it. Table-like rows
/// (two panel names + a size on one line, e.g. a cable schedule printed on the SLD) are read as runs too. Every edge carries a confidence.
/// </summary>
public static class SldAnalyzer
{
    private static readonly Regex BreakerRx = new(@"(?<![\d.])(\d{1,4})\s*A(?:T|F)?\b(?!\w)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LengthRx = new(@"(?:\bL\s*=\s*(\d+(?:\.\d+)?)\s*M?\b)|(?:(?<![\dX.])(\d+(?:\.\d+)?)\s*(?:M|MTR|MTRS|METERS?|METRES?|LM)\b(?!M))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FedFromRx = new(@"\b(?:FED|SUPPLY|SUPPLIED|INCOMING)\s+FROM\b[:\s-]*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SldPageResult Analyze(SldPage page)
    {
        var res = new SldPageResult();
        if (page.Texts.Count == 0) { res.Issues.Add($"page {page.Number}: no text"); return res; }
        var hMed = Median(page.Texts.Select(t => Math.Max(t.H, 0.01)));
        var phrases = Phrases(page.Texts, hMed, page.AttributeTags);
        var tol = hMed * 0.35;

        // ---- nodes and annotations
        var sizes = new List<(SldText T, List<CableSize> S)>();
        var breakers = new List<(SldText T, string B)>();
        var lengths = new List<(SldText T, double L)>();
        foreach (var p in phrases)
        {
            var fed = FedFromRx.Match(p.Text);
            var found = CableSize.FindAll(p.Text).ToList();
            var rest = found.Count > 0 ? CableSize.Strip(p.Text) : p.Text;
            if (found.Count > 0) sizes.Add((p, found));
            var b = BreakerRx.Match(rest);
            if (b.Success && !PanelNames.LooksLikePanel(rest)) breakers.Add((p, p.Text.Trim()));
            var lm = LengthRx.Match(rest);
            if (lm.Success && found.Count == 0 && double.TryParse(lm.Groups[1].Success ? lm.Groups[1].Value : lm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var len) && len > 0 && len < 5000)
                lengths.Add((p, len));
            var candidate = fed.Success ? p.Text[..fed.Index] : rest;
            var tagged = page.AttributeTags.TryGetValue(p, out var tag) && Regex.IsMatch(tag, "PANEL|NAME|TAG|DB|BOARD|EQUIP|ID", RegexOptions.IgnoreCase);
            foreach (var name in PanelCandidates(candidate))
            {
                var parsed = PanelNames.Parse(name);
                if (!PanelNames.LooksLikePanel(name) && !(tagged && parsed.Tokens.Count > 0)) continue;
                var rect = page.Rects.Where(r => r.Contains(p.Cx, p.Cy) && r.W <= Math.Max(hMed * 80, p.W * 1.5) && r.H <= hMed * 40)
                    .OrderBy(r => r.W * r.H).Cast<SldRect?>().FirstOrDefault();
                var box = rect ?? new SldRect(p.X, p.Y, p.W, p.H);
                var node = new SldNode(PanelNames.Tidy(name), parsed, box, rect != null, p);
                res.Nodes.Add(node);
                if (fed.Success && PanelCandidates(fed.Groups[1].Value).FirstOrDefault(PanelNames.LooksLikePanel) is { } parent)
                    res.FedFrom.Add((node, PanelNames.Tidy(parent)));
            }
        }

        // "FED FROM ..." written under / next to a panel label
        foreach (var p in phrases)
        {
            var fed = FedFromRx.Match(p.Text);
            if (!fed.Success || PanelCandidates(p.Text[..fed.Index]).Any(PanelNames.LooksLikePanel)) continue;
            var parent = PanelCandidates(fed.Groups[1].Value).FirstOrDefault(PanelNames.LooksLikePanel);
            var near = res.Nodes.Where(n => n.Label != p).Select(n => (n, d: Dist(n.Box, p.Cx, p.Cy))).Where(x => x.d <= hMed * 6).OrderBy(x => x.d).Select(x => x.n).FirstOrDefault();
            if (parent != null && near != null && near.Parsed.Key != PanelNames.KeyOf(parent)) res.FedFrom.Add((near, PanelNames.Tidy(parent)));
        }

        // ---- wires
        var segs = page.Segments.Where(s => s.Length > tol * 0.5 && !OnRectBorder(s, page.Rects, tol) && !(page.HasFrame && s.Length > Math.Max(page.Width, page.Height) * 0.9)).ToList();
        var comp = Components(segs, tol);
        var byComp = new Dictionary<int, List<int>>();
        for (var i = 0; i < segs.Count; i++) { if (!byComp.TryGetValue(comp[i], out var l)) byComp[comp[i]] = l = new(); l.Add(i); }
        var edgeByKey = new Dictionary<string, SldEdge>();
        foreach (var (cid, idx) in byComp)
        {
            if (LooksLikeGrid(idx.Select(i => segs[i]).ToList(), hMed)) continue;
            var touched = new List<SldNode>();
            foreach (var n in res.Nodes)
            {
                var pad = n.Boxed ? tol * 2 : hMed * 1.2;
                var area = n.Box.Inflate(pad);
                if (idx.Any(i => Touches(segs[i], area))) touched.Add(n);
            }
            var distinct = touched.GroupBy(n => n.Parsed.Key).Select(g => g.OrderByDescending(n => n.Boxed).First()).ToList();
            if (distinct.Count < 2) continue;
            var source = distinct.OrderByDescending(n => PanelNames.Rank(n.Parsed)).ThenBy(n => n.Box.Y).ThenBy(n => n.Box.X).First();
            var rankDecided = distinct.Count(n => PanelNames.Rank(n.Parsed) == PanelNames.Rank(source.Parsed)) == 1;
            if (distinct.Count > 2 && !rankDecided && distinct.Count > 6) { res.Issues.Add($"page {page.Number}: a line touches {distinct.Count} panels of the same rank - skipped (table / title block?)"); continue; }
            foreach (var t in distinct.Where(n => n != source))
            {
                var key = source.Parsed.Key + ">" + t.Parsed.Key;
                if (edgeByKey.ContainsKey(key)) continue;
                var e = new SldEdge
                {
                    From = source, To = t, Component = cid, How = distinct.Count == 2 ? "feeder line" : $"bus with {distinct.Count - 1} outgoing",
                    Confidence = (distinct.Count == 2 ? 0.6 : 0.5) + (rankDecided ? 0.1 : 0) + (source.Boxed || t.Boxed ? 0.05 : 0) - (page.FromScan ? 0.15 : 0),
                };
                edgeByKey[key] = e;
                res.Edges.Add(e);
            }
        }

        // ---- annotations to edges (nearest wire of an edge, then nearest target on a bus)
        var edgeComps = res.Edges.Select(e => e.Component).ToHashSet();
        var compSegs = byComp.Where(kv => edgeComps.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Select(i => segs[i]).ToList());
        SldEdge? Nearest(SldText t, double maxDist)
        {
            var best = (Comp: -1, D: double.MaxValue);
            foreach (var (cid, list) in compSegs)
                foreach (var s in list)
                {
                    var d = DistToBox(s, t);
                    if (d < best.D) best = (cid, d);
                }
            if (best.Comp < 0 || best.D > maxDist) return null;
            return res.Edges.Where(e => e.Component == best.Comp).OrderBy(e => Dist(e.To.Box, t.Cx, t.Cy)).FirstOrDefault();
        }
        var used = new HashSet<SldText>(ReferenceEqualityComparer.Instance);
        foreach (var (t, s) in sizes)
            if (Nearest(t, hMed * 3) is { } e)
            {
                e.Sizes.AddRange(s);
                used.Add(t);
            }
        foreach (var (t, b) in breakers) if (Nearest(t, hMed * 3) is { } e && e.Breaker.Length == 0) e.Breaker = b;
        foreach (var (t, l) in lengths) if (Nearest(t, hMed * 3) is { } e && e.Length is null) e.Length = l;
        foreach (var e in res.Edges) if (e.Sizes.Count > 0) e.Confidence = Math.Min(0.95, e.Confidence + 0.2);

        // ---- table rows: two panel names + a size on one line
        foreach (var row in Rows(phrases, hMed))
        {
            var rowNodes = row.SelectMany(p => PanelCandidates(RemoveSizes(p.Text)).Where(PanelNames.LooksLikePanel).Select(n => (p, n))).ToList();
            var rowSizes = row.SelectMany(p => CableSize.FindAll(p.Text)).ToList();
            if (rowNodes.Count < 2 || rowSizes.Count == 0) continue;
            var a = rowNodes[0]; var b = rowNodes.First(x => PanelNames.KeyOf(x.n) != PanelNames.KeyOf(a.n));
            if (PanelNames.KeyOf(b.n) == PanelNames.KeyOf(a.n)) continue;
            var fromNode = new SldNode(PanelNames.Tidy(a.n), PanelNames.Parse(a.n), new SldRect(a.p.X, a.p.Y, a.p.W, a.p.H), false, a.p);
            var toNode = new SldNode(PanelNames.Tidy(b.n), PanelNames.Parse(b.n), new SldRect(b.p.X, b.p.Y, b.p.W, b.p.H), false, b.p);
            var key = fromNode.Parsed.Key + ">" + toNode.Parsed.Key;
            if (edgeByKey.ContainsKey(key) || edgeByKey.ContainsKey(toNode.Parsed.Key + ">" + fromNode.Parsed.Key)) continue;
            var e = new SldEdge { From = fromNode, To = toNode, How = "table row", Confidence = 0.8 };
            e.Sizes.AddRange(rowSizes);
            var lenText = row.Where(p => CableSize.FindAll(p.Text).Count() == 0 && !PanelNames.LooksLikePanel(p.Text))
                .Select(p => (p, m: Regex.Match(p.Text.Trim(), @"^(\d+(?:\.\d+)?)\s*(?:M|MTR|LM)?$", RegexOptions.IgnoreCase))).Where(x => x.m.Success).ToList();
            if (lenText.Count > 0 && double.TryParse(lenText[^1].m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var len) && len > 0 && len < 5000) { e.Length = len; e.Confidence = 0.9; }
            var br = row.Select(p => BreakerRx.Match(p.Text)).FirstOrDefault(m => m.Success);
            if (br != null) e.Breaker = br.Value;
            edgeByKey[key] = e;
            res.Edges.Add(e);
        }
        return res;
    }

    private static string RemoveSizes(string text) => CableSize.Contains(text) ? CableSize.Strip(text) : text;

    /// <summary>Panel-name candidates in a phrase: the whole phrase, else the pieces between wide gaps / separators.</summary>
    public static IEnumerable<string> PanelCandidates(string text)
    {
        var t = (text ?? "").Trim().Trim(':', ',', ';', '(', ')');
        if (t.Length == 0) yield break;
        if (PanelNames.LooksLikePanel(t)) { yield return t; yield break; }
        var prefix = Regex.Match(t, @"^(?:TO|FROM|FEEDER\s+TO|FDR\s+TO)\s*[:\-]?\s*(.+)$", RegexOptions.IgnoreCase);
        if (prefix.Success && PanelNames.LooksLikePanel(prefix.Groups[1].Value)) { yield return prefix.Groups[1].Value.Trim(); yield break; }
        foreach (var piece in Regex.Split(t, @"\s{2,}|[,;/]\s*|\s+\|\s+"))
            if (piece.Trim().Length > 0 && PanelNames.LooksLikePanel(piece.Trim())) yield return piece.Trim();
    }

    // ------------------------------------------------------------------ text

    /// <summary>Words on one baseline with small gaps joined into phrases (CAD texts / attributes are kept as they are).</summary>
    public static List<SldText> Phrases(List<SldText> words, double hMed, Dictionary<SldText, string>? keep = null)
    {
        var res = new List<SldText>();
        var pool = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => Math.Round(w.Cy / Math.Max(hMed * 0.5, 1e-6))).ThenBy(w => w.X).ToList();
        var done = new HashSet<SldText>(ReferenceEqualityComparer.Instance);
        foreach (var w in pool)
        {
            if (done.Contains(w)) continue;
            done.Add(w);
            if (keep != null && keep.ContainsKey(w)) { res.Add(w); continue; }
            var cur = w;
            while (true)
            {
                var gap = Math.Max(cur.H, hMed) * 0.9;
                var next = pool.Where(o => !done.Contains(o) && (keep == null || !keep.ContainsKey(o)) && Math.Abs(o.Cy - cur.Cy) < Math.Max(cur.H, o.H) * 0.45
                                           && o.X >= cur.Right - cur.H * 0.3 && o.X - cur.Right <= gap)
                    .OrderBy(o => o.X).FirstOrDefault();
                if (next is null) break;
                done.Add(next);
                var x0 = Math.Min(cur.X, next.X); var y0 = Math.Min(cur.Y, next.Y);
                cur = new SldText(cur.Text + " " + next.Text, x0, y0, Math.Max(cur.Right, next.Right) - x0, Math.Max(cur.Bottom, next.Bottom) - y0);
            }
            res.Add(cur);
            if (keep != null && keep.TryGetValue(w, out var tag)) keep[cur] = tag;
        }
        return res;
    }

    private static IEnumerable<List<SldText>> Rows(List<SldText> phrases, double hMed)
    {
        foreach (var g in phrases.GroupBy(p => Math.Round(p.Cy / (hMed * 0.6))))
        {
            var row = g.OrderBy(p => p.X).ToList();
            if (row.Count >= 3) yield return row;
        }
    }

    // ------------------------------------------------------------------ geometry

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 1 : l[l.Count / 2];
    }

    private static bool OnRectBorder(SldSegment s, List<SldRect> rects, double tol)
    {
        foreach (var r in rects)
        {
            bool H(double y) => Math.Abs(s.Y1 - y) < tol && Math.Abs(s.Y2 - y) < tol && s.MinX >= r.X - tol && s.MaxX <= r.Right + tol;
            bool V(double x) => Math.Abs(s.X1 - x) < tol && Math.Abs(s.X2 - x) < tol && s.MinY >= r.Y - tol && s.MaxY <= r.Bottom + tol;
            if (H(r.Y) || H(r.Bottom) || V(r.X) || V(r.Right)) return true;
        }
        return false;
    }

    /// <summary>Union-find over segments: joined when an end point meets another end point or lies on another segment (T-junction).</summary>
    public static int[] Components(List<SldSegment> segs, double tol)
    {
        var parent = Enumerable.Range(0, segs.Count).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        var cell = Math.Max(tol * 4, 1e-6);
        var grid = new Dictionary<(long, long), List<int>>();
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            long x0 = (long)Math.Floor((s.MinX - tol) / cell), x1 = (long)Math.Floor((s.MaxX + tol) / cell);
            long y0 = (long)Math.Floor((s.MinY - tol) / cell), y1 = (long)Math.Floor((s.MaxY + tol) / cell);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 20000) { x1 = x0 + 100; y1 = y0 + 200; }
            for (var x = x0; x <= x1; x++)
                for (var y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((x, y), out var l)) grid[(x, y)] = l = new List<int>();
                    l.Add(i);
                }
        }
        for (var i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            foreach (var (px, py) in new[] { (s.X1, s.Y1), (s.X2, s.Y2) })
            {
                if (!grid.TryGetValue(((long)Math.Floor(px / cell), (long)Math.Floor(py / cell)), out var near)) continue;
                foreach (var j in near)
                    if (j != i && PointSegDist(px, py, segs[j]) <= tol) Union(i, j);
            }
        }
        return Enumerable.Range(0, segs.Count).Select(Find).ToArray();
    }

    /// <summary>A component of long horizontal and vertical lines crossing many times = a table / title block, not wiring.</summary>
    private static bool LooksLikeGrid(List<SldSegment> segs, double hMed)
    {
        var hs = segs.Count(s => Math.Abs(s.Y1 - s.Y2) < 1e-6 && s.Length > hMed * 8);
        var vs = segs.Count(s => Math.Abs(s.X1 - s.X2) < 1e-6 && s.Length > hMed * 4);
        return hs >= 4 && vs >= 4 && segs.Count < (hs + vs) * 1.3;
    }

    private static bool Touches(SldSegment s, SldRect r)
    {
        if (r.Contains(s.X1, s.Y1) || r.Contains(s.X2, s.Y2)) return true;
        // segment crossing the box
        if (s.MaxX < r.X || s.MinX > r.Right || s.MaxY < r.Y || s.MinY > r.Bottom) return false;
        var cx = r.X + r.W / 2; var cy = r.Y + r.H / 2;
        return PointSegDist(cx, cy, s) <= Math.Max(r.W, r.H) / 2;
    }

    public static double PointSegDist(double px, double py, SldSegment s)
    {
        double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
        var l2 = dx * dx + dy * dy;
        var t = l2 <= 0 ? 0 : Math.Clamp(((px - s.X1) * dx + (py - s.Y1) * dy) / l2, 0, 1);
        double qx = s.X1 + t * dx - px, qy = s.Y1 + t * dy - py;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    /// <summary>Shortest distance between a segment and a text box (0 when the segment crosses the box).</summary>
    private static double DistToBox(SldSegment s, SldText t)
    {
        var r = new SldRect(t.X, t.Y, t.W, t.H);
        if (Touches(s, r)) return 0;
        var d = Math.Min(Dist(r, s.X1, s.Y1), Dist(r, s.X2, s.Y2));
        foreach (var (x, y) in new[] { (t.X, t.Y), (t.Right, t.Y), (t.X, t.Bottom), (t.Right, t.Bottom) }) d = Math.Min(d, PointSegDist(x, y, s));
        return d;
    }

    private static double Dist(SldRect r, double x, double y)
    {
        var dx = Math.Max(Math.Max(r.X - x, 0), x - r.Right);
        var dy = Math.Max(Math.Max(r.Y - y, 0), y - r.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
