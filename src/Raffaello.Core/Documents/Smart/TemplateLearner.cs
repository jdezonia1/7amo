using System.Text.RegularExpressions;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

/// <summary>
/// Template learning: when the user confirms an extraction, the layout is stored per (document type, issuer) - anchor words that identify
/// the issuer, header field zones and table column ranges as page fractions. The next document from the same issuer is read with those
/// zones / columns (deterministic) and its fields gain confidence when the template reading agrees.
/// </summary>
public static class TemplateLearner
{
    /// <summary>Learns (or refines) a rate-schedule template from the column layout found on the confirmed pages.</summary>
    public static ReadTemplate LearnSchedule(ReadTemplate? existing, string issuer, ScheduleRead read, IEnumerable<SmartPage> pages)
    {
        var t = existing ?? new ReadTemplate { DocType = DocTypes.RateSchedule, Issuer = issuer };
        var cols = new Dictionary<string, (double X0, double X1)>();
        foreach (var role in ColumnRoles.All)
        {
            var spans = read.Columns.Where(c => c.Roles.ContainsKey(role)).Select(c => c.Roles[role]).ToList();
            if (spans.Count == 0) continue;
            cols[role] = (Median(spans.Select(s => s.X0)), Median(spans.Select(s => s.X1)));
        }
        var old = t.ColumnRanges();
        // refine: average with what was learned before
        foreach (var (k, v) in old) cols[k] = cols.TryGetValue(k, out var n) ? ((n.X0 + v.X0) / 2, (n.X1 + v.X1) / 2) : v;
        t.SetColumns(cols);
        if (t.Anchors.Length == 0) t.Anchors = string.Join("|", Anchors(pages.FirstOrDefault()));
        t.Confirmations++;
        return t;
    }

    /// <summary>Learns header zones from confirmed fields (boxes as page fractions, slightly padded).</summary>
    public static ReadTemplate LearnZones(ReadTemplate? existing, string docType, string issuer, IEnumerable<DocField> confirmed, SmartPage? firstPage)
    {
        var t = existing ?? new ReadTemplate { DocType = docType, Issuer = issuer };
        var zones = t.Zones();
        foreach (var f in confirmed.Where(f => f.RowKey.Length == 0 && f.W > 0 && f.H > 0))
            zones[f.Field] = new Box(Math.Max(0, f.X - 0.01), Math.Max(0, f.Y - 0.005), Math.Min(1, f.W + 0.02), Math.Min(1, f.H + 0.01));
        t.SetZones(zones);
        if (t.Anchors.Length == 0 && firstPage != null) t.Anchors = string.Join("|", Anchors(firstPage));
        t.Confirmations++;
        return t;
    }

    /// <summary>Reads the learned zones of a page: field -> words inside the zone (a second, deterministic reading for the vote).</summary>
    public static Dictionary<string, FieldCandidate> ReadZones(ReadTemplate t, SmartPage page)
    {
        var res = new Dictionary<string, FieldCandidate>();
        var w = page.Ocr?.Width ?? page.Base.WidthPt;
        var h = page.Ocr?.Height ?? page.Base.HeightPt;
        if (w <= 0 || h <= 0) return res;
        foreach (var (field, rel) in t.Zones())
        {
            var box = rel.Absolute(w, h);
            var words = page.Words.Where(x => box.Contains(x.Box.Cx, x.Box.Cy)).ToList();
            if (words.Count == 0) continue;
            var text = string.Join(" ", LayoutBuilder.Lines(words).Select(l => l.Text)).Trim();
            res[field] = new FieldCandidate(text, Math.Min(0.99, words.Average(x => x.Confidence) + 0.05), "template:" + t.Issuer, box, page.Number);
        }
        return res;
    }

    /// <summary>Distinctive words at the top of the page (logo / title / company) used to recognise the issuer next time.</summary>
    public static IEnumerable<string> Anchors(SmartPage? page)
    {
        if (page is null) return Array.Empty<string>();
        var h = page.Ocr?.Height ?? page.Base.HeightPt;
        var top = page.Words.Where(w => w.Box.Cy < h * 0.18 && w.Confidence >= 0.8 && w.Text.Trim().Length >= 4)
            .Select(w => ArabicText.Normalize(w.Text)).Where(t => !Regex.IsMatch(t, @"\d{3,}")).Distinct().Take(3);
        return top;
    }

    private static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }
}
