using System.Globalization;
using System.Text;

namespace Raffaello.Core.Drawings;

/// <summary>
/// [drawings] Minimal IFC (ISO 10303-21 / STEP physical file) reader - enough for the electrical takeoff of a Revit export:
/// spaces (IfcSpace) with their footprint, storeys, electrical elements (outlets, light fixtures, switches, flow terminals,
/// cable carrier segments / fittings, cable segments), spatial containment, property sets and quantities (length, size), placements.
/// Written in-house instead of Xbim.Essentials (CDDL licence, large) - the subset needed is small.
/// </summary>
public sealed class StepFile
{
    public sealed class Ent
    {
        public int Id { get; init; }
        public string Type { get; init; } = "";
        public List<object?> Args { get; init; } = new();
        public override string ToString() => $"#{Id}={Type}";
    }

    /// <summary>A typed value such as IFCLENGTHMEASURE(2.5).</summary>
    public sealed record Typed(string Type, object? Value);
    /// <summary>An enumeration .VALUE.</summary>
    public sealed record Enum(string Value);
    public readonly record struct Ref(int Id);

    public Dictionary<int, Ent> Entities { get; } = new();

    public IEnumerable<Ent> OfType(params string[] types) =>
        Entities.Values.Where(e => types.Any(t => e.Type.Equals(t, StringComparison.OrdinalIgnoreCase)));

    public Ent? Get(object? r) => r is Ref rr && Entities.TryGetValue(rr.Id, out var e) ? e : null;

    public static StepFile Parse(string text)
    {
        var f = new StepFile();
        var data = text.IndexOf("DATA;", StringComparison.OrdinalIgnoreCase);
        var i = data < 0 ? 0 : data + 5;
        var sb = new StringBuilder();
        var inStr = false;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (inStr)
            {
                sb.Append(ch);
                if (ch == '\'') { if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; } else inStr = false; }
                continue;
            }
            if (ch == '\'') { inStr = true; sb.Append(ch); continue; }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? text.Length : end + 1; continue; }
            if (ch == ';')
            {
                var stmt = sb.ToString().Trim(); sb.Clear();
                if (stmt.StartsWith("#")) f.AddStatement(stmt);
                else if (stmt.StartsWith("ENDSEC", StringComparison.OrdinalIgnoreCase)) { }
                continue;
            }
            if (ch is '\r' or '\n') continue;
            sb.Append(ch);
        }
        return f;
    }

    private void AddStatement(string s)
    {
        var eq = s.IndexOf('=');
        if (eq < 0) return;
        if (!int.TryParse(s[1..eq].Trim(), out var id)) return;
        var rest = s[(eq + 1)..].Trim();
        var p = rest.IndexOf('(');
        if (p < 0) return;
        var type = rest[..p].Trim();
        var pos = p;
        var args = ParseList(rest, ref pos);
        Entities[id] = new Ent { Id = id, Type = type, Args = args };
    }

    private static List<object?> ParseList(string s, ref int pos)
    {
        var res = new List<object?>();
        pos++; // (
        while (pos < s.Length)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) break;
            if (s[pos] == ')') { pos++; break; }
            res.Add(ParseValue(s, ref pos));
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',') pos++;
        }
        return res;
    }

    private static void SkipWs(string s, ref int pos) { while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++; }

    private static object? ParseValue(string s, ref int pos)
    {
        var ch = s[pos];
        if (ch == '(') return ParseList(s, ref pos);
        if (ch == '$' || ch == '*') { pos++; return null; }
        if (ch == '#')
        {
            var st = ++pos; while (pos < s.Length && char.IsDigit(s[pos])) pos++;
            return new Ref(int.Parse(s[st..pos], CultureInfo.InvariantCulture));
        }
        if (ch == '\'')
        {
            var sb = new StringBuilder(); pos++;
            while (pos < s.Length)
            {
                if (s[pos] == '\'') { if (pos + 1 < s.Length && s[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; } pos++; break; }
                sb.Append(s[pos++]);
            }
            return DecodeIfcString(sb.ToString());
        }
        if (ch == '.')
        {
            var end = s.IndexOf('.', pos + 1);
            var v = s[(pos + 1)..end]; pos = end + 1;
            return new Enum(v);
        }
        if (char.IsLetter(ch))
        {
            var st = pos; while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            var name = s[st..pos];
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '(')
            {
                var inner = ParseList(s, ref pos);
                return new Typed(name, inner.Count == 1 ? inner[0] : inner);
            }
            return name;
        }
        {
            var st = pos; while (pos < s.Length && "+-0123456789.Ee".IndexOf(s[pos]) >= 0) pos++;
            return double.TryParse(s[st..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }

    /// <summary>\X2\...\X0\ (UTF-16 hex) escapes used by Revit for non-ASCII names.</summary>
    private static string DecodeIfcString(string s)
    {
        if (!s.Contains("\\X2\\", StringComparison.Ordinal)) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s.AsSpan(i).StartsWith("\\X2\\"))
            {
                var end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                if (end < 0) break;
                var hex = s[(i + 4)..end];
                for (var k = 0; k + 3 < hex.Length; k += 4) sb.Append((char)Convert.ToInt32(hex.Substring(k, 4), 16));
                i = end + 3;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

public sealed record IfcSpaceInfo(int Id, string Name, string LongName, string Storey, List<PointD> Footprint);

public sealed record IfcElementInfo(int Id, string IfcClass, string Name, string ObjectType, string PredefinedType, string Tag, string Space, string Storey, PointD? Position, double LengthM, string Size);

/// <summary>[drawings] Electrical content of an IFC model in metres (plan coordinates x, y).</summary>
public sealed class IfcModel
{
    public double LengthUnitM { get; set; } = 1;
    public List<IfcSpaceInfo> Spaces { get; } = new();
    public List<IfcElementInfo> Elements { get; } = new();

    public static readonly string[] ElectricalClasses =
    {
        "IFCOUTLET", "IFCLIGHTFIXTURE", "IFCSWITCHINGDEVICE", "IFCFLOWTERMINAL", "IFCELECTRICAPPLIANCE", "IFCCABLECARRIERSEGMENT", "IFCCABLECARRIERFITTING",
        "IFCCABLESEGMENT", "IFCCABLEFITTING", "IFCFLOWSEGMENT", "IFCFLOWFITTING", "IFCFLOWCONTROLLER", "IFCELECTRICDISTRIBUTIONBOARD", "IFCSENSOR",
        "IFCCOMMUNICATIONSAPPLIANCE", "IFCAUDIOVISUALAPPLIANCE", "IFCDISTRIBUTIONCONTROLELEMENT", "IFCJUNCTIONBOX",
    };

    public static IfcModel Read(string path) => FromStep(StepFile.Parse(File.ReadAllText(path)));

    public static IfcModel FromStep(StepFile f)
    {
        var m = new IfcModel();
        foreach (var u in f.OfType("IFCSIUNIT"))
            if (u.Args.Count >= 4 && u.Args[1] is StepFile.Enum { Value: "LENGTHUNIT" })
                m.LengthUnitM = (u.Args[2] as StepFile.Enum)?.Value switch { "MILLI" => 0.001, "CENTI" => 0.01, "KILO" => 1000, _ => 1 };

        // storey of each space / element through IfcRelAggregates and IfcRelContainedInSpatialStructure
        var parentOf = new Dictionary<int, int>();
        foreach (var r in f.OfType("IFCRELAGGREGATES"))
            if (r.Args.Count >= 6 && r.Args[4] is StepFile.Ref parent && r.Args[5] is List<object?> kids)
                foreach (var k in kids.OfType<StepFile.Ref>()) parentOf[k.Id] = parent.Id;
        var containedIn = new Dictionary<int, int>();
        foreach (var r in f.OfType("IFCRELCONTAINEDINSPATIALSTRUCTURE", "IFCRELREFERENCEDINSPATIALSTRUCTURE"))
            if (r.Args.Count >= 6 && r.Args[4] is List<object?> els && r.Args[5] is StepFile.Ref st)
                foreach (var e in els.OfType<StepFile.Ref>())
                    if (!containedIn.ContainsKey(e.Id) || f.Get(st)?.Type.Equals("IFCSPACE", StringComparison.OrdinalIgnoreCase) == true) containedIn[e.Id] = st.Id;
        string StoreyOf(int id)
        {
            var guard = 0;
            var cur = id;
            while (guard++ < 20)
            {
                var p = containedIn.TryGetValue(cur, out var c) ? c : parentOf.TryGetValue(cur, out var a) ? a : -1;
                if (p < 0) return "";
                var e = f.Entities.GetValueOrDefault(p);
                if (e?.Type.Equals("IFCBUILDINGSTOREY", StringComparison.OrdinalIgnoreCase) == true) return Str(e.Args, 2);
                cur = p;
            }
            return "";
        }

        foreach (var s in f.OfType("IFCSPACE"))
        {
            var place = Placement(f, s.Args.ElementAtOrDefault(5));
            var fp = Footprint(f, s.Args.ElementAtOrDefault(6)).Select(p => place.Apply(p) * m.LengthUnitM).ToList();
            m.Spaces.Add(new IfcSpaceInfo(s.Id, Str(s.Args, 2), Str(s.Args, 7), StoreyOf(s.Id), fp));
        }

        // property sets and quantities
        var props = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var r in f.OfType("IFCRELDEFINESBYPROPERTIES"))
        {
            if (r.Args.Count < 6 || r.Args[4] is not List<object?> objs) continue;
            var def = f.Get(r.Args[5]);
            if (def is null) continue;
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (def.Type.Equals("IFCPROPERTYSET", StringComparison.OrdinalIgnoreCase) && def.Args.ElementAtOrDefault(4) is List<object?> ps)
                foreach (var pv in ps.Select(f.Get).Where(x => x?.Type.Equals("IFCPROPERTYSINGLEVALUE", StringComparison.OrdinalIgnoreCase) == true))
                    values[Str(pv!.Args, 0)] = pv.Args.ElementAtOrDefault(2) is StepFile.Typed t ? t.Value : pv.Args.ElementAtOrDefault(2);
            if (def.Type.Equals("IFCELEMENTQUANTITY", StringComparison.OrdinalIgnoreCase) && def.Args.ElementAtOrDefault(5) is List<object?> qs)
                foreach (var q in qs.Select(f.Get).Where(x => x != null))
                    if (q!.Type.Equals("IFCQUANTITYLENGTH", StringComparison.OrdinalIgnoreCase)) values[Str(q.Args, 0)] = q.Args.ElementAtOrDefault(3);
            foreach (var o in objs.OfType<StepFile.Ref>())
            {
                if (!props.TryGetValue(o.Id, out var d)) props[o.Id] = d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in values) d[kv.Key] = kv.Value;
            }
        }

        var spaceById = m.Spaces.ToDictionary(s => s.Id);
        foreach (var e in f.Entities.Values.Where(e => ElectricalClasses.Contains(e.Type.ToUpperInvariant())))
        {
            var pr = props.GetValueOrDefault(e.Id) ?? new Dictionary<string, object?>();
            var place = Placement(f, e.Args.ElementAtOrDefault(5));
            var pos = place.Apply(new PointD(0, 0)) * m.LengthUnitM;
            var len = Num(pr, "Length") ?? Num(pr, "NominalLength") ?? AxisLength(f, e.Args.ElementAtOrDefault(6));
            var size = Text(pr, "Size") ?? (Num(pr, "Width") is double w ? (w * m.LengthUnitM * 1000).ToString("0", CultureInfo.InvariantCulture) : Text(pr, "NominalWidth") ?? "");
            var space = containedIn.TryGetValue(e.Id, out var sid) && spaceById.TryGetValue(sid, out var sp) ? sp.Name : "";
            if (space.Length == 0)
                space = m.Spaces.Where(x => x.Footprint.Count >= 3 && Poly.Contains(x.Footprint, pos)).OrderBy(x => Poly.Area(x.Footprint)).Select(x => x.Name).FirstOrDefault() ?? "";
            m.Elements.Add(new IfcElementInfo(e.Id, e.Type.ToUpperInvariant(), Str(e.Args, 2), Str(e.Args, 4), PredefinedType(e), Str(e.Args, 7), space, StoreyOf(e.Id), pos,
                (len ?? 0) * m.LengthUnitM, size));
        }
        return m;
    }

    private static string PredefinedType(StepFile.Ent e) => e.Args.LastOrDefault() is StepFile.Enum en ? en.Value : "";

    private static string Str(List<object?> a, int i) => a.ElementAtOrDefault(i) as string ?? "";
    private static double? Num(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v switch { double x => x, StepFile.Typed { Value: double y } => y, _ => null } : null;
    private static string? Text(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v switch { string s => s, double x => x.ToString(CultureInfo.InvariantCulture), _ => null } : null;

    /// <summary>World transform of an IfcLocalPlacement chain (plan part only: location + rotation about Z).</summary>
    private static Affine2D Placement(StepFile f, object? placementRef, int depth = 0)
    {
        var p = f.Get(placementRef);
        if (p is null || depth > 30 || !p.Type.Equals("IFCLOCALPLACEMENT", StringComparison.OrdinalIgnoreCase)) return Affine2D.Identity;
        var parent = Placement(f, p.Args.ElementAtOrDefault(0), depth + 1);
        return parent.After(Axis(f, p.Args.ElementAtOrDefault(1)));
    }

    private static Affine2D Axis(StepFile f, object? axisRef)
    {
        var a = f.Get(axisRef);
        if (a is null) return Affine2D.Identity;
        var loc = Point(f, a.Args.ElementAtOrDefault(0));
        var refDir = a.Type.Equals("IFCAXIS2PLACEMENT3D", StringComparison.OrdinalIgnoreCase) ? f.Get(a.Args.ElementAtOrDefault(2)) : f.Get(a.Args.ElementAtOrDefault(1));
        double dx = 1, dy = 0;
        if (refDir?.Args.ElementAtOrDefault(0) is List<object?> r && r.Count >= 2) { dx = r[0] as double? ?? 1; dy = r[1] as double? ?? 0; }
        var n = Math.Sqrt(dx * dx + dy * dy); if (n < 1e-12) { dx = 1; dy = 0; n = 1; }
        dx /= n; dy /= n;
        return new Affine2D(dx, -dy, loc.X, dy, dx, loc.Y);
    }

    private static PointD Point(StepFile f, object? r)
    {
        var p = f.Get(r);
        return p?.Args.ElementAtOrDefault(0) is List<object?> c && c.Count >= 2 ? new PointD(c[0] as double? ?? 0, c[1] as double? ?? 0) : new PointD(0, 0);
    }

    /// <summary>Footprint of a space from its swept-solid body (IfcArbitraryClosedProfileDef + IfcPolyline / IfcIndexedPolyCurve) in placement coordinates.</summary>
    private static List<PointD> Footprint(StepFile f, object? shapeRef)
    {
        var shape = f.Get(shapeRef);
        if (shape?.Args.ElementAtOrDefault(2) is not List<object?> reps) return new();
        foreach (var rep in reps.Select(f.Get).Where(x => x != null))
            if (rep!.Args.ElementAtOrDefault(3) is List<object?> items)
                foreach (var it in items.Select(f.Get).Where(x => x?.Type.Equals("IFCEXTRUDEDAREASOLID", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var prof = f.Get(it!.Args.ElementAtOrDefault(0));
                    var pos = Axis(f, it.Args.ElementAtOrDefault(1));
                    var curve = f.Get(prof?.Args.ElementAtOrDefault(2));
                    if (curve?.Type.Equals("IFCPOLYLINE", StringComparison.OrdinalIgnoreCase) == true && curve.Args.ElementAtOrDefault(0) is List<object?> pts)
                    {
                        var res = pts.Select(x => Point(f, x)).Select(pos.Apply).ToList();
                        if (res.Count > 1 && res[0].DistanceTo(res[^1]) < 1e-9) res.RemoveAt(res.Count - 1);
                        return res;
                    }
                    if (prof?.Type.Equals("IFCRECTANGLEPROFILEDEF", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var at = Axis(f, prof.Args.ElementAtOrDefault(2));
                        var w = prof.Args.ElementAtOrDefault(3) as double? ?? 0; var h = prof.Args.ElementAtOrDefault(4) as double? ?? 0;
                        return new[] { new PointD(-w / 2, -h / 2), new PointD(w / 2, -h / 2), new PointD(w / 2, h / 2), new PointD(-w / 2, h / 2) }.Select(p => pos.Apply(at.Apply(p))).ToList();
                    }
                }
        return new();
    }

    /// <summary>Length of an 'Axis' polyline representation (model units), used when no Length quantity is exported.</summary>
    private static double? AxisLength(StepFile f, object? shapeRef)
    {
        var shape = f.Get(shapeRef);
        if (shape?.Args.ElementAtOrDefault(2) is not List<object?> reps) return null;
        foreach (var rep in reps.Select(f.Get).Where(x => x != null))
            if (Str(rep!.Args, 1).Equals("Axis", StringComparison.OrdinalIgnoreCase) && rep.Args.ElementAtOrDefault(3) is List<object?> items)
                foreach (var it in items.Select(f.Get).Where(x => x?.Type.Equals("IFCPOLYLINE", StringComparison.OrdinalIgnoreCase) == true))
                    if (it!.Args.ElementAtOrDefault(0) is List<object?> pts)
                        return Poly.PolylineLength(pts.Select(x => Point(f, x)).ToList());
        return null;
    }

    /// <summary>"IfcOutlet;IfcFlowTerminal:*SOCKET*" - class, optionally a wildcard on predefined type / object type / name / size.</summary>
    public static bool Matches(IfcElementInfo e, string patterns)
    {
        foreach (var p in (patterns ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = p.Split(':', 2);
            if (!LinearTakeoff.Like(e.IfcClass, parts[0])) continue;
            if (parts.Length == 1) return true;
            var f = parts[1];
            if (LinearTakeoff.Like(e.PredefinedType, f) || LinearTakeoff.Like(e.ObjectType, f) || LinearTakeoff.Like(e.Name, f) || LinearTakeoff.Like(e.Size, f)) return true;
        }
        return false;
    }
}
