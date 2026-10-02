using System.Globalization;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.General;
using UglyToad.PdfPig.Writer;

namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] Synthetic source files with known answers for tests and the CLI demo (no company data): a VECTOR PDF of a scene
/// (with dashed tray lines, H= notes and a title block scale), a DXF/DWG with blocks, exploded blocks, layers and room polylines,
/// an IFC model and a Revit add-in JSON.
/// </summary>
public static class SyntheticFiles
{
    public static readonly Rgb TrayMagenta = new(255, 0, 255);
    public static readonly Rgb ConduitBlue = new(30, 60, 200);

    /// <summary>Tray routes along the top of each room row (sheet px), length known.</summary>
    public static List<List<PointD>> Trays(Synthetic.Scene s)
    {
        var res = new List<List<PointD>>();
        foreach (var row in s.Rooms.GroupBy(r => Math.Round(r.Y)))
        {
            var first = row.OrderBy(r => r.X).First(); var last = row.OrderBy(r => r.X).Last();
            var y = first.Y + 22;
            res.Add(new List<PointD> { new(first.X + 30, y), new(last.X + last.W - 30, y), new(last.X + last.W - 30, y + 60) });
        }
        return res;
    }

    // ------------------------------------------------------------------ vector PDF

    /// <summary>
    /// Writes the scene as a vector PDF page (1 px = 72/dpi pt). Trays dashed magenta 2 pt, conduits blue, walls dark grey,
    /// symbols black, room labels and "H=1350mm" notes as text, title block "SCALE 1:{scale}".
    /// </summary>
    public static byte[] VectorPdf(Synthetic.Scene s, IReadOnlyList<List<PointD>>? trays = null, IReadOnlyDictionary<int, int>? heightNotesMm = null)
    {
        var k = 72.0 / s.Dpi;
        var w = s.Width * k; var h = s.Height * k;
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        var p = b.AddPage(w, h);
        PdfPoint P(PointD q) => new(q.X * k, h - q.Y * k);
        void Ln(PointD a, PointD c, Rgb col, double wpx) { p.SetStrokeColor(col.R, col.G, col.B); p.DrawLine(P(a), P(c), Math.Max(0.1, wpx * k)); }
        foreach (var (a, c, t, col) in s.Lines) Ln(a, c, col, t);
        foreach (var sym in s.Symbols)
            Synthetic.DrawSymbol((a, c) => Ln(a, c, Rgb.Black, 1.6), (c, r) => { p.SetTextAndFillColor(0, 0, 0); p.SetStrokeColor(0, 0, 0); p.DrawCircle(P(c), 2 * r * k, 0.1, true); },
                sym.Type, sym.Cx, sym.Cy, s.SymbolSize, sym.Quarter, sym.Mirror);
        p.SetTextAndFillColor(0, 0, 0);
        foreach (var (x, y, text) in s.Labels) p.AddText(text, 6, P(new PointD(x, y + 10)), font);
        if (heightNotesMm != null)
            foreach (var (idx, mm) in heightNotesMm)
            {
                var sym = s.Symbols[idx];
                p.AddText($"H={mm}mm", 4.5, P(new PointD(sym.Cx + s.SymbolSize * 0.7, sym.Cy - s.SymbolSize * 0.55)), font);
            }
        p.AddText($"SCALE 1:{s.ScaleDenominator.ToString("0", CultureInfo.InvariantCulture)}", 8, new PdfPoint(w - 120, 12), font);
        if (trays != null)
        {
            var ops = p.CurrentStream.Operations as List<IGraphicsStateOperation>;
            ops?.Add(new SetLineDashPattern(new double[] { 6, 3 }, 0));
            foreach (var t in trays) for (var i = 1; i < t.Count; i++) Ln(t[i - 1], t[i], TrayMagenta, 2 / k);
            ops?.Add(new SetLineDashPattern(Array.Empty<double>(), 0));
        }
        return b.Build();
    }

    // ------------------------------------------------------------------ DXF / DWG

    /// <summary>Block name per symbol type ("EL-TWIN-SOCKET").</summary>
    public static string BlockOf(string type) => "EL-" + type.Replace(' ', '-').Replace("/", "");

    public sealed record CadTruth(List<Synthetic.SymbolPlacement> Inserted, List<Synthetic.SymbolPlacement> Exploded, double TrayM, double ConduitM, double MillimetresPerPx);

    /// <summary>
    /// The scene as a DXF in millimetres: walls on A-WALL, conduits on E-CONDUIT-20, trays on E-TRAY-300, rooms as closed polylines on
    /// A-ROOM with names on A-ROOM-NAME, symbols as blocks (EL-SOCKET ...) - every 4th one EXPLODED into loose lines / circles.
    /// </summary>
    public static CadTruth Dxf(Synthetic.Scene s, string path, IReadOnlyList<List<PointD>>? trays = null, bool dwg = false)
    {
        var mmPerPx = s.MetresPerPixel * 1000;
        PointD M(PointD px) => new(px.X * mmPerPx, (s.Height - px.Y) * mmPerPx);
        var doc = new CadDocument();
        doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        Layer L(string name) { var l = new Layer(name); doc.Layers.Add(l); return l; }
        var wall = L("A-WALL"); var conduit = L("E-CONDUIT-20"); var tray = L("E-TRAY-300"); var room = L("A-ROOM"); var roomName = L("A-ROOM-NAME"); var sym = L("E-SYMBOL");
        foreach (var (a, c, t, col) in s.Lines)
            doc.Entities.Add(new Line(new CSMath.XYZ(M(a).X, M(a).Y, 0), new CSMath.XYZ(M(c).X, M(c).Y, 0)) { Layer = col == SyntheticFiles.ConduitBlue || t < 2 && col.B > 150 ? conduit : wall });
        double trayMm = 0, conduitMm = 0;
        foreach (var c in s.Conduits) conduitMm += Poly.PolylineLength(c) * mmPerPx;
        if (trays != null)
            foreach (var t in trays)
                for (var i = 1; i < t.Count; i++)
                {
                    doc.Entities.Add(new Line(new CSMath.XYZ(M(t[i - 1]).X, M(t[i - 1]).Y, 0), new CSMath.XYZ(M(t[i]).X, M(t[i]).Y, 0)) { Layer = tray });
                    trayMm += t[i].DistanceTo(t[i - 1]) * mmPerPx;
                }
        foreach (var r in s.Rooms)
        {
            var pl = new LwPolyline { Layer = room, IsClosed = true };
            foreach (var q in r.Polygon) pl.Vertices.Add(new LwPolyline.Vertex(new CSMath.XY(M(q).X, M(q).Y)));
            doc.Entities.Add(pl);
            var c = M(new PointD(r.X + r.W / 2, r.Y + r.H / 2));
            doc.Entities.Add(new TextEntity { Value = r.Name, InsertPoint = new CSMath.XYZ(c.X, c.Y, 0), Height = 250, Layer = roomName });
        }
        // block definitions (geometry around 0,0 in mm, y up)
        var size = s.SymbolSize * mmPerPx;
        var records = new Dictionary<string, BlockRecord>();
        foreach (var type in Synthetic.SymbolTypes)
        {
            var br = new BlockRecord(BlockOf(type));
            Synthetic.DrawSymbol((a, c) => br.Entities.Add(new Line(new CSMath.XYZ(a.X, -a.Y, 0), new CSMath.XYZ(c.X, -c.Y, 0)) { Layer = sym }),
                (c, rr) => br.Entities.Add(new Circle { Center = new CSMath.XYZ(c.X, -c.Y, 0), Radius = rr, Layer = sym }), type, 0, 0, size, 0, false);
            doc.BlockRecords.Add(br);
            records[type] = br;
        }
        var inserted = new List<Synthetic.SymbolPlacement>(); var exploded = new List<Synthetic.SymbolPlacement>();
        var n = 0;
        foreach (var p in s.Symbols)
        {
            var c = M(new PointD(p.Cx, p.Cy));
            if (n++ % 4 == 3)
            {
                // exploded: the same geometry as loose entities in model space
                Synthetic.DrawSymbol((a, b) => doc.Entities.Add(new Line(new CSMath.XYZ(M(a).X, M(a).Y, 0), new CSMath.XYZ(M(b).X, M(b).Y, 0)) { Layer = sym }),
                    (cc, rr) => doc.Entities.Add(new Circle { Center = new CSMath.XYZ(M(cc).X, M(cc).Y, 0), Radius = rr * mmPerPx, Layer = sym }), p.Type, p.Cx, p.Cy, s.SymbolSize, p.Quarter, p.Mirror);
                exploded.Add(p);
                continue;
            }
            if (p.Mirror) { exploded.Add(p); Synthetic.DrawSymbol((a, b) => doc.Entities.Add(new Line(new CSMath.XYZ(M(a).X, M(a).Y, 0), new CSMath.XYZ(M(b).X, M(b).Y, 0)) { Layer = sym }),
                    (cc, rr) => doc.Entities.Add(new Circle { Center = new CSMath.XYZ(M(cc).X, M(cc).Y, 0), Radius = rr * mmPerPx, Layer = sym }), p.Type, p.Cx, p.Cy, s.SymbolSize, p.Quarter, p.Mirror); continue; }
            // screen clockwise quarter turns = CAD clockwise = negative rotation
            var ins = new Insert(records[p.Type]) { InsertPoint = new CSMath.XYZ(c.X, c.Y, 0), Rotation = -p.Quarter * Math.PI / 2, Layer = sym };
            ins.Attributes.Add(new AttributeEntity { Tag = "MOUNTING", Value = p.Type.Contains("SWITCH") ? "H=1200" : "H=450" });
            doc.Entities.Add(ins);
            inserted.Add(p);
        }
        if (dwg) DwgWriter.Write(path, doc);
        else DxfWriter.Write(path, doc, false);
        return new CadTruth(inserted, exploded, trayMm / 1000, conduitMm / 1000, mmPerPx);
    }

    // ------------------------------------------------------------------ IFC

    public sealed record IfcTruth(Dictionary<string, Dictionary<string, int>> CountsBySpace, double TrayM, double CableM);

    /// <summary>
    /// A small IFC4 model in millimetres: one storey, the scene's rooms as IfcSpace (extruded rectangles), outlets / light fixtures /
    /// switches placed in them (half contained in the space, half only in the storey - found by footprint), cable carrier segments with
    /// a Qto length and a Size property, and cable segments.
    /// </summary>
    public static IfcTruth Ifc(Synthetic.Scene s, string path, int seed = 1)
    {
        var rnd = new Random(seed);
        var mmPerPx = s.MetresPerPixel * 1000;
        var sb = new StringBuilder();
        var id = 0;
        int E(string body) { sb.Append('#').Append(++id).Append('=').Append(body).Append(";\n"); return id; }
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + (Math.Abs(v % 1) < 1e-12 ? "." : "");
        string G() => "'" + Guid.NewGuid().ToString("N")[..22] + "'";
        sb.Append("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('synthetic.ifc','2026-10-02T00:00:00',(''),(''),'Raffaello synthetic','Raffaello','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n");
        var unit = E("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)");
        var units = E($"IFCUNITASSIGNMENT((#{unit}))");
        var origin = E("IFCCARTESIANPOINT((0.,0.,0.))");
        var axis0 = E($"IFCAXIS2PLACEMENT3D(#{origin},$,$)");
        var project = E($"IFCPROJECT({G()},$,'SYNTHETIC',$,$,$,$,$,#{units})");
        var sitePl = E($"IFCLOCALPLACEMENT($,#{axis0})");
        var site = E($"IFCSITE({G()},$,'SITE',$,$,#{sitePl},$,$,.ELEMENT.,$,$,$,$,$)");
        var bldPl = E($"IFCLOCALPLACEMENT(#{sitePl},#{axis0})");
        var bld = E($"IFCBUILDING({G()},$,'BRANDED',$,$,#{bldPl},$,$,.ELEMENT.,$,$,$)");
        var stPt = E("IFCCARTESIANPOINT((0.,0.,3900.))");
        var stAx = E($"IFCAXIS2PLACEMENT3D(#{stPt},$,$)");
        var stPl = E($"IFCLOCALPLACEMENT(#{bldPl},#{stAx})");
        var storey = E($"IFCBUILDINGSTOREY({G()},$,'L01',$,$,#{stPl},$,$,.ELEMENT.,3900.)");
        E($"IFCRELAGGREGATES({G()},$,$,$,#{project},(#{site}))");
        E($"IFCRELAGGREGATES({G()},$,$,$,#{site},(#{bld}))");
        E($"IFCRELAGGREGATES({G()},$,$,$,#{bld},(#{storey}))");
        var spaceIds = new List<int>();
        var spaceByRoom = new Dictionary<string, int>();
        PointD Mm(double xPx, double yPx) => new(xPx * mmPerPx, (s.Height - yPx) * mmPerPx);
        foreach (var r in s.Rooms)
        {
            var o = Mm(r.X, r.Y + r.H);
            var pt = E($"IFCCARTESIANPOINT(({F(o.X)},{F(o.Y)},0.))");
            var ax = E($"IFCAXIS2PLACEMENT3D(#{pt},$,$)");
            var pl = E($"IFCLOCALPLACEMENT(#{stPl},#{ax})");
            double w = r.W * mmPerPx, hh = r.H * mmPerPx;
            var pts = new[] { (0.0, 0.0), (w, 0.0), (w, hh), (0.0, hh), (0.0, 0.0) }.Select(q => E($"IFCCARTESIANPOINT(({F(q.Item1)},{F(q.Item2)}))")).ToList();
            var poly = E($"IFCPOLYLINE(({string.Join(",", pts.Select(x => "#" + x))}))");
            var prof = E($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,#{poly})");
            var dir = E("IFCDIRECTION((0.,0.,1.))");
            var solid = E($"IFCEXTRUDEDAREASOLID(#{prof},#{axis0},#{dir},3000.)");
            var rep = E($"IFCSHAPEREPRESENTATION($,'Body','SweptSolid',(#{solid}))");
            var shape = E($"IFCPRODUCTDEFINITIONSHAPE($,$,(#{rep}))");
            var sp = E($"IFCSPACE({G()},$,'{r.Name}',$,$,#{pl},#{shape},'ROOM {r.Name}',.ELEMENT.,.SPACE.,$)");
            spaceIds.Add(sp); spaceByRoom[r.Name] = sp;
        }
        E($"IFCRELAGGREGATES({G()},$,$,$,#{storey},({string.Join(",", spaceIds.Select(x => "#" + x))}))");
        var counts = new Dictionary<string, Dictionary<string, int>>();
        var inSpace = new Dictionary<int, List<int>>(); var inStorey = new List<int>();
        string ClassOf(string type) => type switch
        {
            "SOCKET" or "TWIN SOCKET" => "IFCOUTLET(" + "{0}" + ",.POWEROUTLET.)",
            "DATA" => "IFCOUTLET({0},.DATAOUTLET.)",
            "LIGHT" or "DOWNLIGHT" => "IFCLIGHTFIXTURE({0},.POINTSOURCE.)",
            "SWITCH 1G" or "SWITCH 2G" => "IFCSWITCHINGDEVICE({0},.TOGGLESWITCH.)",
            _ => "IFCFLOWTERMINAL({0})",
        };
        foreach (var p in s.Symbols)
        {
            var o = Mm(p.Cx, p.Cy);
            var pt = E($"IFCCARTESIANPOINT(({F(o.X)},{F(o.Y)},450.))");
            var ax = E($"IFCAXIS2PLACEMENT3D(#{pt},$,$)");
            var pl = E($"IFCLOCALPLACEMENT(#{stPl},#{ax})");
            var args = $"{G()},$,'{p.Type}',$,'{p.Type}',#{pl},$,'{p.Type}'";
            var el = E(string.Format(CultureInfo.InvariantCulture, ClassOf(p.Type), args));
            if (rnd.Next(2) == 0) { if (!inSpace.TryGetValue(spaceByRoom[p.Room], out var l)) inSpace[spaceByRoom[p.Room]] = l = new(); l.Add(el); }
            else inStorey.Add(el);
            if (!counts.TryGetValue(p.Room, out var d)) counts[p.Room] = d = new();
            d[p.Type] = d.GetValueOrDefault(p.Type) + 1;
        }
        // trays: one carrier segment per room with a Length quantity and a Size property; cables one per room
        double trayMm = 0, cableMm = 0;
        foreach (var r in s.Rooms)
        {
            var o = Mm(r.X + r.W / 2, r.Y + 30);
            var pt = E($"IFCCARTESIANPOINT(({F(o.X)},{F(o.Y)},3200.))");
            var ax = E($"IFCAXIS2PLACEMENT3D(#{pt},$,$)");
            var pl = E($"IFCLOCALPLACEMENT(#{stPl},#{ax})");
            var len = Math.Round(r.W * mmPerPx * 0.8);
            var tray = E($"IFCCABLECARRIERSEGMENT({G()},$,'Cable Tray 300x50',$,'Cable Tray',#{pl},$,$,.CABLETRAYSEGMENT.)");
            var q = E($"IFCQUANTITYLENGTH('Length',$,$,{F(len)},$)");
            var qs = E($"IFCELEMENTQUANTITY({G()},$,'Qto_CableCarrierSegmentBaseQuantities',$,$,(#{q}))");
            E($"IFCRELDEFINESBYPROPERTIES({G()},$,$,$,(#{tray}),#{qs})");
            var sz = E("IFCPROPERTYSINGLEVALUE('Size',$,IFCLABEL('300'),$)");
            var ps = E($"IFCPROPERTYSET({G()},$,'Pset_Raffaello',$,(#{sz}))");
            E($"IFCRELDEFINESBYPROPERTIES({G()},$,$,$,(#{tray}),#{ps})");
            trayMm += len;
            var cl = Math.Round(r.H * mmPerPx * 1.5);
            var cable = E($"IFCCABLESEGMENT({G()},$,'Cable 4C 16',$,'Cable',#{pl},$,$,.CABLESEGMENT.)");
            var q2 = E($"IFCQUANTITYLENGTH('Length',$,$,{F(cl)},$)");
            var qs2 = E($"IFCELEMENTQUANTITY({G()},$,'Qto_CableSegmentBaseQuantities',$,$,(#{q2}))");
            E($"IFCRELDEFINESBYPROPERTIES({G()},$,$,$,(#{cable}),#{qs2})");
            cableMm += cl;
            if (!inSpace.TryGetValue(spaceByRoom[r.Name], out var l)) inSpace[spaceByRoom[r.Name]] = l = new();
            l.Add(tray); l.Add(cable);
        }
        foreach (var (sp, els) in inSpace) E($"IFCRELCONTAINEDINSPATIALSTRUCTURE({G()},$,$,$,({string.Join(",", els.Select(x => "#" + x))}),#{sp})");
        if (inStorey.Count > 0) E($"IFCRELCONTAINEDINSPATIALSTRUCTURE({G()},$,$,$,({string.Join(",", inStorey.Select(x => "#" + x))}),#{storey})");
        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        File.WriteAllText(path, sb.ToString());
        return new IfcTruth(counts, trayMm / 1000, cableMm / 1000);
    }

    // ------------------------------------------------------------------ Revit add-in JSON

    public static string RevitJson(Synthetic.Scene s)
    {
        var mPerPx = s.MetresPerPixel;
        var sb = new StringBuilder("{\"units\":\"m\",\"rooms\":[");
        sb.Append(string.Join(",", s.Rooms.Select(r => $"{{\"id\":\"{r.Name}\",\"level\":\"L01\",\"polygon\":[{string.Join(",", r.Polygon.Select(p => $"[{(p.X * mPerPx).ToString("0.###", CultureInfo.InvariantCulture)},{((s.Height - p.Y) * mPerPx).ToString("0.###", CultureInfo.InvariantCulture)}]"))}]}}")));
        sb.Append("],\"counts\":[");
        sb.Append(string.Join(",", s.Symbols.GroupBy(p => (p.Room, p.Type)).Select(g => $"{{\"room\":\"{g.Key.Room}\",\"system\":\"{Synthetic.SystemOf(g.Key.Type)}\",\"item\":\"{g.Key.Type}\",\"qty\":{g.Count()}}}")));
        sb.Append("],\"linear\":[");
        sb.Append(string.Join(",", s.Rooms.Select(r => $"{{\"room\":\"{r.Name}\",\"system\":\"TRAY\",\"item\":\"CABLE TRAY\",\"size\":\"300\",\"length_m\":{(r.W * mPerPx * 0.8).ToString("0.###", CultureInfo.InvariantCulture)}}}")));
        sb.Append("]}");
        return sb.ToString();
    }
}
