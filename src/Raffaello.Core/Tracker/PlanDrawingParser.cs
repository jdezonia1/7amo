using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Tracker;

public sealed class PlanDrawing
{
    public List<PlanImage> Plans { get; } = new();
    public List<RoomShape> Shapes { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
}

/// <summary>
/// Parses a SpreadsheetML drawing (xdr): pictures named PlanImage&lt;PLAN&gt; are level plans; shapes named RM_&lt;room&gt;
/// (custom geometry or rectangles) are room outlines. Outlines are converted to polygons in 0..1 coordinates of the
/// plan image they sit on, so the app can draw and colour them over the image.
/// </summary>
public static class PlanDrawingParser
{
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly record struct Rect(double X, double Y, double W, double H)
    {
        public bool Contains(double px, double py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
    }

    public static PlanDrawing Parse(XDocument doc, Func<string, byte[]?> imageById, string building)
    {
        var res = new PlanDrawing();
        var planRects = new List<(PlanImage plan, Rect rect)>();
        var shapes = new List<(string name, string descr, Rect rect, List<List<(double x, double y)>> polys)>();
        var captions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.Root!.Elements().Where(e => e.Name.Namespace == Xdr && e.Name.LocalName.EndsWith("Anchor")))
        {
            var rect = AnchorRect(anchor);
            var pic = anchor.Element(Xdr + "pic");
            var sp = anchor.Element(Xdr + "sp");
            if (pic != null)
            {
                var name = (string?)pic.Element(Xdr + "nvPicPr")?.Element(Xdr + "cNvPr")?.Attribute("name") ?? "";
                var embed = (string?)pic.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
                var plan = name.StartsWith("PlanImage", StringComparison.OrdinalIgnoreCase) ? name["PlanImage".Length..] : name;
                var img = new PlanImage
                {
                    Building = building, Plan = plan, Name = name, Png = embed is null ? null : imageById(embed),
                    X = (long)rect.X, Y = (long)rect.Y, Width = (long)rect.W, Height = (long)rect.H,
                };
                res.Plans.Add(img);
                planRects.Add((img, rect));
            }
            else if (sp != null)
            {
                var nv = sp.Element(Xdr + "nvSpPr")?.Element(Xdr + "cNvPr");
                var name = (string?)nv?.Attribute("name") ?? "";
                var descr = (string?)nv?.Attribute("descr") ?? "";
                if (name.StartsWith("LVL_", StringComparison.OrdinalIgnoreCase)) { captions[name[4..]] = descr; continue; }
                if (!name.StartsWith("RM_", StringComparison.OrdinalIgnoreCase)) continue;
                var polys = ShapePolygons(sp, rect);
                shapes.Add((name, descr, rect, polys));
            }
        }

        foreach (var (plan, _) in planRects)
            if (captions.TryGetValue(plan.Plan, out var cap)) plan.Caption = cap;

        foreach (var (name, descr, rect, polys) in shapes)
        {
            var cx = rect.X + rect.W / 2; var cy = rect.Y + rect.H / 2;
            var host = planRects.FirstOrDefault(p => p.rect.Contains(cx, cy));
            if (host.plan is null)
            {
                res.Issues.Add(new(0, IssueLevel.Warning, $"Shape {name} is not over a plan image."));
                continue;
            }
            var pr = host.rect;
            string N(double v, double o, double s) => Math.Clamp((v - o) / s, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            foreach (var poly in polys)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(string.Join(' ', poly.Select(p => $"{N(p.x, pr.X, pr.W)},{N(p.y, pr.Y, pr.H)}")));
            }
            res.Shapes.Add(new RoomShape
            {
                Building = building, ShapeName = name, Room = RoomFromShapeName(name), Plan = host.plan.Plan, Polygons = sb.ToString(),
                Left = (rect.X - pr.X) / pr.W, Top = (rect.Y - pr.Y) / pr.H, Right = (rect.X + rect.W - pr.X) / pr.W, Bottom = (rect.Y + rect.H - pr.Y) / pr.H,
                Description = descr,
            });
        }
        return res;
    }

    /// <summary>RM_P2-106 -> P2-106. A trailing duplicate suffix like " 2" is removed.</summary>
    public static string RoomFromShapeName(string name)
    {
        var s = name.StartsWith("RM_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
        var sp = s.IndexOf(' ');
        return (sp > 0 ? s[..sp] : s).Trim();
    }

    private static Rect AnchorRect(XElement anchor)
    {
        // prefer the absolute xfrm of the shape / picture (Excel writes sheet EMU there for every anchor type)
        var xfrm = anchor.Descendants(A + "xfrm").FirstOrDefault();
        var off = xfrm?.Element(A + "off");
        var ext = xfrm?.Element(A + "ext");
        double D(XElement? e, string a) => e is null ? 0 : double.Parse((string?)e.Attribute(a) ?? "0", CultureInfo.InvariantCulture);
        double x = D(off, "x"), y = D(off, "y"), w = D(ext, "cx"), h = D(ext, "cy");
        var pos = anchor.Element(Xdr + "pos");
        var aext = anchor.Element(Xdr + "ext");
        if (x == 0 && y == 0 && pos != null) { x = D(pos, "x"); y = D(pos, "y"); }
        if ((w == 0 || h == 0) && aext != null) { w = D(aext, "cx"); h = D(aext, "cy"); }
        return new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    private static List<List<(double x, double y)>> ShapePolygons(XElement sp, Rect rect)
    {
        var result = new List<List<(double, double)>>();
        var cust = sp.Descendants(A + "custGeom").FirstOrDefault();
        if (cust is null)
        {
            result.Add(new() { (rect.X, rect.Y), (rect.X + rect.W, rect.Y), (rect.X + rect.W, rect.Y + rect.H), (rect.X, rect.Y + rect.H) });
            return result;
        }
        foreach (var path in cust.Descendants(A + "path"))
        {
            var pw = double.Parse((string?)path.Attribute("w") ?? rect.W.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            var ph = double.Parse((string?)path.Attribute("h") ?? rect.H.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (pw <= 0) pw = rect.W;
            if (ph <= 0) ph = rect.H;
            var poly = new List<(double, double)>();
            foreach (var cmd in path.Elements())
            {
                if (cmd.Name.LocalName == "close") continue;
                if (cmd.Name.LocalName == "moveTo" && poly.Count > 2) { result.Add(poly); poly = new(); }
                var pt = cmd.Elements(A + "pt").LastOrDefault();
                if (pt is null) continue;
                var px = double.Parse((string?)pt.Attribute("x") ?? "0", CultureInfo.InvariantCulture);
                var py = double.Parse((string?)pt.Attribute("y") ?? "0", CultureInfo.InvariantCulture);
                poly.Add((rect.X + px / pw * rect.W, rect.Y + py / ph * rect.H));
            }
            if (poly.Count > 2) result.Add(poly);
        }
        return result;
    }
}
