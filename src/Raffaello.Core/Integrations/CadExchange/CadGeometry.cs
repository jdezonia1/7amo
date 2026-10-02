// [trust] CAD exchange - plain C# with no Raffaello dependencies: the AutoCAD / Revit add-ins (tools/cad) compile this file
// into themselves (linked source), so the same rules count blocks on the CAD side and check the file on the Raffaello side.
// Keep it free of newer APIs (it also builds for .NET Framework 4.8).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Integrations.CadExchange
{
    /// <summary>A 2-D point in drawing units.</summary>
    public struct P2
    {
        public double X;
        public double Y;
        public P2(double x, double y) { X = x; Y = y; }
        public override string ToString() => X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Polygon helpers (ray casting, shoelace area, centroid, bounding box).</summary>
    public static class CadGeometry
    {
        /// <summary>True when the point is inside the ring (boundary counts as inside within <paramref name="tolerance"/>).</summary>
        public static bool Inside(IReadOnlyList<P2> ring, P2 p, double tolerance = 1e-6)
        {
            var n = ring.Count;
            if (n < 3) return false;
            var inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if (OnSegment(a, b, p, tolerance)) return true;
                if ((a.Y > p.Y) != (b.Y > p.Y))
                {
                    var x = (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X;
                    if (p.X < x) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Inside the outer ring and in none of the holes.</summary>
        public static bool Inside(IReadOnlyList<P2> outer, IEnumerable<IReadOnlyList<P2>>? holes, P2 p)
        {
            if (!Inside(outer, p)) return false;
            return holes == null || !holes.Any(h => h.Count >= 3 && Inside(h, p, -1));
        }

        private static bool OnSegment(P2 a, P2 b, P2 p, double tol)
        {
            if (tol < 0) return false;
            var cross = (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
            var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (len < 1e-12) return Math.Abs(p.X - a.X) <= tol && Math.Abs(p.Y - a.Y) <= tol;
            if (Math.Abs(cross) / len > Math.Max(tol, 1e-9)) return false;
            return p.X >= Math.Min(a.X, b.X) - tol && p.X <= Math.Max(a.X, b.X) + tol && p.Y >= Math.Min(a.Y, b.Y) - tol && p.Y <= Math.Max(a.Y, b.Y) + tol;
        }

        /// <summary>Signed shoelace area (positive = counter-clockwise).</summary>
        public static double SignedArea(IReadOnlyList<P2> ring)
        {
            double s = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) s += (ring[j].X * ring[i].Y) - (ring[i].X * ring[j].Y);
            return s / 2;
        }

        public static double Area(IReadOnlyList<P2> ring) => Math.Abs(SignedArea(ring));

        public static P2 Centroid(IReadOnlyList<P2> ring)
        {
            var a = SignedArea(ring);
            if (Math.Abs(a) < 1e-12) return new P2(ring.Average(p => p.X), ring.Average(p => p.Y));
            double cx = 0, cy = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var f = ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
                cx += (ring[j].X + ring[i].X) * f;
                cy += (ring[j].Y + ring[i].Y) * f;
            }
            return new P2(cx / (6 * a), cy / (6 * a));
        }

        public static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IEnumerable<P2> pts)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in pts) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            return (x0, y0, x1, y1);
        }

        /// <summary>Removes a repeated closing vertex and consecutive duplicates.</summary>
        public static List<P2> Clean(IEnumerable<P2> ring, double tol = 1e-6)
        {
            var list = new List<P2>();
            foreach (var p in ring)
                if (list.Count == 0 || Math.Abs(list[list.Count - 1].X - p.X) > tol || Math.Abs(list[list.Count - 1].Y - p.Y) > tol) list.Add(p);
            if (list.Count > 1 && Math.Abs(list[0].X - list[list.Count - 1].X) <= tol && Math.Abs(list[0].Y - list[list.Count - 1].Y) <= tol) list.RemoveAt(list.Count - 1);
            return list;
        }
    }

    /// <summary>A room outline for point assignment.</summary>
    public sealed class RoomOutline
    {
        public string Id = "";
        public string Level = "";
        public List<P2> Polygon = new List<P2>();
        public List<List<P2>> Holes = new List<List<P2>>();
        private double? _area;
        public double Area => _area ?? (_area = CadGeometry.Area(Polygon)).Value;
    }

    /// <summary>Finds the room that contains a point: the smallest containing room wins (a bathroom inside a suite).</summary>
    public sealed class RoomLocator
    {
        private readonly List<RoomOutline> _rooms;
        private readonly List<(double X0, double Y0, double X1, double Y1)> _boxes;

        public RoomLocator(IEnumerable<RoomOutline> rooms)
        {
            _rooms = rooms.Where(r => r.Polygon.Count >= 3).OrderBy(r => r.Area).ToList();
            _boxes = _rooms.Select(r => CadGeometry.Bounds(r.Polygon)).ToList();
        }

        public RoomOutline? Find(P2 p, string? level = null)
        {
            for (var i = 0; i < _rooms.Count; i++)
            {
                var b = _boxes[i];
                if (p.X < b.X0 || p.X > b.X1 || p.Y < b.Y0 || p.Y > b.Y1) continue;
                var r = _rooms[i];
                if (level != null && r.Level.Length > 0 && !string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                if (CadGeometry.Inside(r.Polygon, r.Holes.Select(h => (IReadOnlyList<P2>)h), p)) return r;
            }
            return null;
        }
    }

    /// <summary>One block-name / layer rule: which system and item a block counts as, and how many points it is.</summary>
    public sealed class BlockRule
    {
        /// <summary>Wildcard on the (effective) block name: * and ?, case-insensitive. Empty = any block.</summary>
        public string Block = "";
        /// <summary>Wildcard on the layer. Empty = any layer.</summary>
        public string Layer = "";
        public string System = "";
        public string Item = "";
        /// <summary>Points per block (twin socket = 1, twin data = 2 ...).</summary>
        public double Qty = 1;
        /// <summary>Optional stage the count belongs to (empty = PROJECT QTY for every stage chosen at import).</summary>
        public string Stage = "";
        /// <summary>Block attribute whose value multiplies the quantity (e.g. "QTY"); empty = none.</summary>
        public string QtyAttribute = "";
        public bool Ignore;
    }

    /// <summary>Maps block references to system / item (first matching rule wins; layer rules catch the rest).</summary>
    public sealed class BlockMapping
    {
        public List<BlockRule> Rules = new List<BlockRule>();

        public BlockRule? Match(string blockName, string layer)
        {
            foreach (var r in Rules)
            {
                if (r.Block.Length > 0 && !Wildcard(r.Block, blockName)) continue;
                if (r.Layer.Length > 0 && !Wildcard(r.Layer, layer)) continue;
                return r;
            }
            return null;
        }

        public static bool Wildcard(string pattern, string text) =>
            Regex.IsMatch(text ?? "", "^" + Regex.Escape(pattern ?? "").Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A starting rule set for MOBCO's electrical layers (edit blockmap.json for the real block names).</summary>
        public static BlockMapping Default()
        {
            var m = new BlockMapping();
            void Add(string block, string layer, string system, string item, double qty = 1) =>
                m.Rules.Add(new BlockRule { Block = block, Layer = layer, System = system, Item = item, Qty = qty });
            Add("*TWIN*DATA*", "", "DATA", "TWIN DATA OUTLET", 2);
            Add("*TWIN*SOCKET*", "", "POWER", "TWIN SOCKET", 1);
            Add("*SOCKET*", "", "POWER", "SOCKET OUTLET");
            Add("*ISOLATOR*", "", "POWER", "ISOLATOR");
            Add("*SWITCH*", "", "LIGHT", "SWITCH");
            Add("*DOWNLIGHT*", "", "LIGHT", "DOWNLIGHT");
            Add("*EMERG*", "", "EMERGENCY LIGHT", "EMERGENCY LUMINAIRE");
            Add("*EXIT*", "", "EMERGENCY LIGHT", "EXIT SIGN");
            Add("*DATA*", "", "DATA", "DATA OUTLET");
            Add("*TV*", "", "AV", "TV OUTLET");
            Add("*THERMOSTAT*", "", "GRMS", "THERMOSTAT (CP-4)");
            Add("*GRMS*", "", "GRMS", "GRMS DEVICE");
            Add("*SMOKE*", "", "FIRE", "SMOKE DETECTOR");
            Add("*CCTV*", "", "CCTV", "CAMERA");
            Add("", "*LIGHT*", "LIGHT", "LIGHTING POINT");
            Add("", "*POWER*", "POWER", "POWER POINT");
            return m;
        }
    }

    /// <summary>A primitive of an exploded symbol (what is left when a block was exploded): circle, line, arc, polyline, text.</summary>
    public sealed class CadPrimitive
    {
        /// <summary>CIRCLE / LINE / ARC / PLINE / TEXT / HATCH.</summary>
        public string Kind = "";
        public string Layer = "";
        public double MinX, MinY, MaxX, MaxY;
        /// <summary>Radius for circles / arcs, length for lines, vertex count for polylines.</summary>
        public double Size;
        public P2 Center => new P2((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    }

    /// <summary>The shape fingerprint of a symbol: how many of each primitive kind and its overall size.</summary>
    public sealed class SymbolSignature
    {
        public string Name = "";
        public string System = "";
        public string Item = "";
        public double Qty = 1;
        public SortedDictionary<string, int> Kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public double Width;
        public double Height;
        /// <summary>Largest circle / arc radius (0 when none) - a socket and a downlight differ here.</summary>
        public double MaxRadius;

        public static SymbolSignature Of(IEnumerable<CadPrimitive> prims)
        {
            var list = prims.ToList();
            var s = new SymbolSignature();
            foreach (var g in list.Where(p => p.Kind != "TEXT").GroupBy(p => p.Kind)) s.Kinds[g.Key] = g.Count();
            if (list.Count > 0)
            {
                s.Width = list.Max(p => p.MaxX) - list.Min(p => p.MinX);
                s.Height = list.Max(p => p.MaxY) - list.Min(p => p.MinY);
                s.MaxRadius = list.Where(p => p.Kind is "CIRCLE" or "ARC").Select(p => p.Size).DefaultIfEmpty(0).Max();
            }
            return s;
        }

        /// <summary>0..1 similarity: same primitive counts, size within tolerance (rotation-independent: width/height may swap).</summary>
        public double Similarity(SymbolSignature other, double sizeTolerance = 0.15)
        {
            var keys = Kinds.Keys.Union(other.Kinds.Keys).ToList();
            if (keys.Count == 0) return 0;
            if (keys.Any(k => (Kinds.TryGetValue(k, out var a) ? a : 0) != (other.Kinds.TryGetValue(k, out var b) ? b : 0))) return 0;
            double Rel(double a, double b) => Math.Max(a, b) < 1e-9 ? 0 : Math.Abs(a - b) / Math.Max(a, b);
            var straight = Math.Max(Rel(Width, other.Width), Rel(Height, other.Height));
            var turned = Math.Max(Rel(Width, other.Height), Rel(Height, other.Width));
            var size = Math.Min(straight, turned);
            var radius = Rel(MaxRadius, other.MaxRadius);
            var worst = Math.Max(size, radius);
            return worst > sizeTolerance ? 0 : 1 - worst;
        }
    }

    /// <summary>
    /// Exploded-geometry heuristics: when a symbol block was exploded, its circles / lines remain on the electrical layers. The
    /// primitives are clustered (touching / near boxes), each cluster is fingerprinted and compared with the signatures of the
    /// intact blocks of the same drawing (learned from their definitions). Matches above <see cref="MinSimilarity"/> count as
    /// that symbol, flagged method EXPLODED so the QS can review them.
    /// </summary>
    public sealed class ExplodedSymbolMatcher
    {
        public List<SymbolSignature> Templates = new List<SymbolSignature>();
        public double MinSimilarity = 0.8;
        /// <summary>Primitives closer than this (drawing units) belong to the same cluster.</summary>
        public double ClusterGap = 50;
        /// <summary>Clusters bigger than this are drawing linework, not a symbol.</summary>
        public double MaxSymbolSize = 1500;

        public void Learn(string blockName, string system, string item, double qty, IEnumerable<CadPrimitive> definition)
        {
            if (Templates.Any(t => string.Equals(t.Name, blockName, StringComparison.OrdinalIgnoreCase))) return;
            var s = SymbolSignature.Of(definition);
            if (s.Kinds.Count == 0) return;
            s.Name = blockName; s.System = system; s.Item = item; s.Qty = qty;
            Templates.Add(s);
        }

        public List<List<CadPrimitive>> Cluster(IReadOnlyList<CadPrimitive> prims)
        {
            var n = prims.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var order = Enumerable.Range(0, n).OrderBy(i => prims[i].MinX).ToArray();
            for (var a = 0; a < n; a++)
            {
                var pa = prims[order[a]];
                for (var b = a + 1; b < n; b++)
                {
                    var pb = prims[order[b]];
                    if (pb.MinX > pa.MaxX + ClusterGap) break;
                    if (pb.MinY > pa.MaxY + ClusterGap || pb.MaxY < pa.MinY - ClusterGap) continue;
                    parent[Find(order[a])] = Find(order[b]);
                }
            }
            return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.Select(i => prims[i]).ToList()).ToList();
        }

        /// <summary>Recognised symbols (template, position) and the clusters left over.</summary>
        public (List<(SymbolSignature Template, P2 At, double Score)> Found, int Unrecognised) Match(IReadOnlyList<CadPrimitive> prims)
        {
            var found = new List<(SymbolSignature, P2, double)>();
            var left = 0;
            foreach (var c in Cluster(prims))
            {
                var sig = SymbolSignature.Of(c);
                if (sig.Width > MaxSymbolSize || sig.Height > MaxSymbolSize || sig.Kinds.Count == 0) { left++; continue; }
                var best = Templates.Select(t => (T: t, S: t.Similarity(sig))).OrderByDescending(x => x.S).FirstOrDefault();
                if (best.T != null && best.S >= MinSimilarity)
                {
                    var b = CadGeometry.Bounds(c.SelectMany(p => new[] { new P2(p.MinX, p.MinY), new P2(p.MaxX, p.MaxY) }));
                    found.Add((best.T, new P2((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2), best.S));
                }
                else left++;
            }
            return (found, left);
        }
    }
}
