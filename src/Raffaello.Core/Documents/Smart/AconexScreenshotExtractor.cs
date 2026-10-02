using System.Text.RegularExpressions;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

public sealed class AconexScreenshotRead
{
    public string WorkflowNo { get; set; } = "";
    public string WorkflowName { get; set; } = "";
    public RawTable Table { get; set; } = new();
    public WorkflowLookupResult? Result { get; set; }
    public List<ExtractionIssue> Issues { get; } = new();
    public int Page { get; set; }
}

/// <summary>
/// Reads an Aconex "Search Workflows" screenshot (printed or scanned, any orientation - the OCR turns the page) into the same
/// <see cref="RawTable"/> the browser automation produces, so <see cref="WorkflowParser"/> gives the steps, current step, overdue
/// and outcome exactly as for a live lookup. Columns come from the header labels' x positions; rows are anchored on the dates.
/// </summary>
public static class AconexScreenshotExtractor
{
    private static readonly Regex DateRx = new(@"\b\d{1,2}/\d{1,2}/\d{4}\b", RegexOptions.Compiled);
    private static readonly string[] HeaderWords = { "STEP NAME", "ASSIGNED", "DATE IN", "DATE DUE", "STEP STATUS", "STEP OUTCOME", "DOCUMENT", "FILE NAME", "DATE COMPLETED", "ORIGINAL DUE", "ACTION" };

    public static AconexScreenshotRead Read(SmartPage page, AconexConfig? cfg = null, DateTime? today = null)
    {
        cfg ??= new AconexConfig();
        var res = new AconexScreenshotRead { Page = page.Number };
        var words = page.Words.ToList();
        var all = ArabicText.Fold(page.ReadingText + "\n" + page.Text);
        var wf = Regex.Match(all, @"WORKFLOW\s*NO\.?\s*:?\s*(WF\s*-?\s*\d{5,7})");
        if (wf.Success) res.WorkflowNo = Regex.Replace(wf.Groups[1].Value, @"\s", "").Replace("WF", "WF-").Replace("--", "-");
        var name = Regex.Match(page.ReadingText + "\n" + page.Text, @"Name\s*:\s*([A-Za-z0-9_]+(?:_[A-Za-z0-9]+)*)", RegexOptions.IgnoreCase);
        if (name.Success) res.WorkflowName = name.Groups[1].Value;
        if (words.Count == 0) { res.Issues.Add(new(IssueLevel.Error, "NO_TEXT", "page has no text", null, "")); return res; }

        var lines = LayoutBuilder.Lines(words);
        // header band: the line with the most header labels, plus the line below (two-line labels)
        var scored = lines.Select((l, i) => (i, Hits: HeaderWords.Count(h => ArabicText.Fold(l.Text).Contains(h)))).OrderByDescending(x => x.Hits).First();
        if (scored.Hits < 2) { res.Issues.Add(new(IssueLevel.Error, "NO_HEADER", "workflow table header (Step Name / Assigned To / Date Due) not found", null, "")); return res; }
        var hl = lines[scored.i];
        var lh = hl.Box.H;
        var headerWords = words.Where(w => w.Box.Cy >= hl.Box.Y - lh * 1.2 && w.Box.Cy <= hl.Box.Bottom + lh * 1.2).ToList();
        var cols = ColumnsFrom(headerWords);
        res.Table.Headers = cols.Select(c => c.Label).ToList();

        // data rows: anchored on lines with a date (dd/mm/yyyy) below the header
        var below = lines.Where(l => l.Box.Y > hl.Box.Bottom + lh * 0.5).ToList();
        var anchors = below.Where(l => DateRx.IsMatch(l.Text)).Select(l => l.Box.Cy).ToList();
        anchors = TableBuilder.Cluster(anchors, lh * 1.5);
        foreach (var gl in below.Where(l => Regex.IsMatch(ArabicText.Fold(l.Text), @"WORKFLOW\s*NO")))
            res.Table.Rows.Add(new RawRow { Cells = new List<string> { gl.Text }, IsGroup = true, Key = "group" });
        for (var i = 0; i < anchors.Count; i++)
        {
            var top = i == 0 ? anchors[i] - lh * 1.6 : (anchors[i - 1] + anchors[i]) / 2;
            var bottom = i + 1 < anchors.Count ? (anchors[i] + anchors[i + 1]) / 2 : anchors[i] + lh * 2.2;
            var cells = cols.Select(_ => new List<OcrWord>()).ToList();
            foreach (var w in words.Where(w => w.Box.Cy > top && w.Box.Cy <= bottom))
            {
                var c = cols.FindIndex(k => w.Box.Cx >= k.X0 && w.Box.Cx < k.X1);
                if (c >= 0) cells[c].Add(w);
            }
            var row = new RawRow { Key = $"row{i + 1}", Cells = cells.Select(ws => string.Join("\n", LayoutBuilder.Lines(ws).Select(l => l.Text))).ToList() };
            if (row.Cells.All(c => c.Length == 0)) continue;
            res.Table.Rows.Add(row);
        }
        if (res.WorkflowNo.Length == 0) { res.Issues.Add(new(IssueLevel.Warn, "WF_NO", "workflow number not found", null, "")); return res; }
        try { res.Result = WorkflowParser.Parse(res.WorkflowNo, res.Table, cfg, today ?? DateTime.Today); }
        catch (AconexPageChangedException ex) { res.Issues.Add(new(IssueLevel.Error, "COLUMNS", ex.Message, null, "")); }
        if (res.Result != null && res.WorkflowName.Length > 0 && res.Result.WorkflowName.Length == 0) res.Result.WorkflowName = res.WorkflowName;
        return res;
    }

    /// <summary>Columns from the header labels: labels whose boxes overlap horizontally are one column ("Date" over "Completed"); boundaries at the midpoints.</summary>
    private static List<(string Label, double X0, double X1)> ColumnsFrom(List<OcrWord> header)
    {
        var groups = new List<List<OcrWord>>();
        foreach (var w in header.OrderBy(w => w.Box.X))
        {
            var g = groups.FirstOrDefault(g => g.Any(x => x.Box.HorizontalOverlap(w.Box) > Math.Min(x.Box.W, w.Box.W) * 0.3));
            if (g != null) g.Add(w); else groups.Add(new List<OcrWord> { w });
        }
        var ordered = groups.Select(g => (Label: string.Join(" ", g.OrderBy(w => w.Box.Y).Select(w => w.Text)), Box: Box.Union(g.Select(w => w.Box)))).OrderBy(g => g.Box.X).ToList();
        var res = new List<(string, double, double)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var x0 = i == 0 ? double.MinValue : (ordered[i - 1].Box.Right + ordered[i].Box.X) / 2;
            var x1 = i + 1 < ordered.Count ? (ordered[i].Box.Right + ordered[i + 1].Box.X) / 2 : double.MaxValue;
            res.Add((ordered[i].Label, x0, x1));
        }
        return res;
    }
}
