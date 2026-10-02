using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace Raffaello.Core.Cables;

/// <summary>
/// [cables] SLD / riser drawings from DWG or DXF (ACadSharp, MIT): block attributes (panel name tags), TEXT / MTEXT, lines and polylines
/// (closed 4-corner polylines and block outlines = panel boxes) of the model space, then the same geometry analysis as the PDF reader.
/// Drawing units are not assumed - every tolerance is relative to the median text height.
/// </summary>
public static class CadSld
{
    public static CableReadResult Read(string path, CableReadOptions o)
    {
        var res = new CableReadResult { FileName = path, Kind = CableSources.Drawing, Pages = 1 };
        CadDocument doc = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ? DwgReader.Read(path) : DxfReader.Read(path);
        var page = Page(doc);
        if (page.Texts.Count == 0) { res.Issues.Add("no text in the model space"); return res; }
        CableReaders.AddPage(res, SldAnalyzer.Analyze(page), 1, CableSources.Drawing, Path.GetFileName(path), o);
        return res;
    }

    public static SldPage Page(CadDocument doc)
    {
        var texts = new List<SldText>();
        var tags = new Dictionary<SldText, string>(ReferenceEqualityComparer.Instance);
        var segs = new List<SldSegment>();
        var rects = new List<SldRect>();
        void Add(Entity e, int depth)
        {
            switch (e)
            {
                case AttributeEntity a when !string.IsNullOrWhiteSpace(a.Value):
                    var at = Text(a.Value, a.InsertPoint.X, a.InsertPoint.Y, a.Height);
                    texts.Add(at);
                    tags[at] = a.Tag ?? "";
                    break;
                case TextEntity t when !string.IsNullOrWhiteSpace(t.Value):
                    texts.Add(Text(t.Value, t.InsertPoint.X, t.InsertPoint.Y, t.Height));
                    break;
                case MText m:
                    var lines = m.GetPlainTextLines().Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    for (var i = 0; i < lines.Count; i++)
                        texts.Add(Text(lines[i], m.InsertPoint.X, m.InsertPoint.Y - i * m.Height * 1.4 - m.Height, m.Height));
                    break;
                case Line l:
                    segs.Add(new SldSegment(l.StartPoint.X, -l.StartPoint.Y, l.EndPoint.X, -l.EndPoint.Y));
                    break;
                case LwPolyline p:
                    Poly(p.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList(), p.IsClosed);
                    break;
                case Polyline2D p2:
                    Poly(p2.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList(), p2.IsClosed);
                    break;
                case Insert ins:
                    if (ins.Attributes != null)
                        foreach (var a in ins.Attributes) Add(a, depth);
                    if (depth < 3)
                    {
                        try
                        {
                            foreach (var x in ins.Explode())
                                if (x is not AttributeDefinition) Add(x, depth + 1);
                        }
                        catch { /* blocks that cannot be exploded keep only their attributes */ }
                    }
                    break;
            }
        }
        void Poly(List<(double X, double Y)> v, bool closed)
        {
            if (v.Count < 2) return;
            for (var i = 0; i + 1 < v.Count; i++) segs.Add(new SldSegment(v[i].X, -v[i].Y, v[i + 1].X, -v[i + 1].Y));
            if (closed) segs.Add(new SldSegment(v[^1].X, -v[^1].Y, v[0].X, -v[0].Y));
            var axis = v.Zip(v.Skip(1)).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-6 || Math.Abs(p.First.Y - p.Second.Y) < 1e-6);
            var isBox = (closed && v.Count == 4) || (v.Count == 5 && Math.Abs(v[0].X - v[4].X) < 1e-6 && Math.Abs(v[0].Y - v[4].Y) < 1e-6);
            if (isBox && axis)
            {
                var x0 = v.Min(p => p.X); var x1 = v.Max(p => p.X); var y0 = v.Min(p => -p.Y); var y1 = v.Max(p => -p.Y);
                if (x1 - x0 > 0 && y1 - y0 > 0) rects.Add(new SldRect(x0, y0, x1 - x0, y1 - y0));
            }
        }
        foreach (var e in doc.Entities) Add(e, 0);
        // 4 lines forming a box are a box too
        var page = new SldPage
        {
            Number = 1,
            Width = segs.Count + texts.Count == 0 ? 1 : Math.Max(1, segs.Select(s => s.MaxX).Concat(texts.Select(t => t.Right)).Max() - segs.Select(s => s.MinX).Concat(texts.Select(t => t.X)).Min()),
            Height = segs.Count + texts.Count == 0 ? 1 : Math.Max(1, segs.Select(s => s.MaxY).Concat(texts.Select(t => t.Bottom)).Max() - segs.Select(s => s.MinY).Concat(texts.Select(t => t.Y)).Min()),
        };
        page.Texts.AddRange(texts);
        page.Segments.AddRange(segs);
        page.Rects.AddRange(rects);
        foreach (var (k, v) in tags) page.AttributeTags[k] = v;
        return page;
    }

    /// <summary>CAD text at its insertion point (baseline, left): box spans upwards by the height (y negated -> top-left origin).</summary>
    private static SldText Text(string value, double x, double y, double h)
    {
        var s = value.Trim();
        h = h > 0 ? h : 2.5;
        return new SldText(s, x, -y - h, Math.Max(h, s.Length * h * 0.75), h);
    }
}
