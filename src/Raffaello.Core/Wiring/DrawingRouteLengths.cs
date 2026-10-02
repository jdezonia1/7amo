using System.Globalization;
using Raffaello.Core.Assemblies;
using Raffaello.Core.Data;
using Raffaello.Core.Drawings;

namespace Raffaello.Core.Wiring;

/// <summary>
/// Average route length per point from the Drawings module, for the Assemblies <c>route_len</c> parameter. Two sources:
/// <list type="bullet">
/// <item>cable lengths per point (<see cref="PointLength"/>, DB -> point over the measured routes) when the app has computed them - exact;</item>
/// <item>otherwise the stored takeoffs: measured linear runs of a system (containment / conduit classes with a SYSTEM) divided by the counted
/// points of the same system, per sheet (latest takeoff) and per room type.</item>
/// </list>
/// Lookup: item type's system + room type, then the system alone; no takeoff = null (the template default stays and the breakdown says so).
/// </summary>
public sealed class DrawingRouteLengths
{
    private sealed class Acc { public double Metres; public double Points; public HashSet<long> Sheets = new(); }

    private readonly Dictionary<string, Acc> _ratio = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _exact = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _ratio.Count == 0 && _exact.Count == 0;
    public int Sheets => _ratio.Values.SelectMany(a => a.Sheets).Distinct().Count();

    /// <summary>Drawing system for an assemblies item type (LIGHTING POINT -> LIGHT ...); the spec's own system wins.</summary>
    public static IEnumerable<string> SystemsFor(ItemSpec spec)
    {
        if (spec.System.Trim().Length > 0) yield return spec.System.Trim().ToUpperInvariant();
        var byType = spec.ItemType switch
        {
            ItemTypes.LightingPoint or ItemTypes.SwitchPoint or ItemTypes.LinearLight or ItemTypes.Luminaire => "LIGHT",
            ItemTypes.DaliPoint => "DALI",
            ItemTypes.SocketPoint or ItemTypes.Isolator or ItemTypes.FloorBox or ItemTypes.FinalConnection => "POWER",
            ItemTypes.DataPoint => "DATA",
            ItemTypes.GrmsPoint => "GRMS",
            ItemTypes.FireAlarmPoint => "FIRE",
            _ => "",
        };
        if (byType.Length > 0) yield return byType;
        if (spec.ItemType == ItemTypes.DaliPoint) yield return "LIGHT";
    }

    private static string Key(string system, string roomType = "") => system.Trim().ToUpperInvariant() + "|" + roomType.Trim().ToUpperInvariant();

    /// <summary>Builds the table from the stored takeoffs (latest takeoff of every sheet). Room types come from the project's room list.</summary>
    public static DrawingRouteLengths Load(IDrawingStore store, ProjectSnapshot? project, IEnumerable<PointLength>? lengths = null)
    {
        var t = new DrawingRouteLengths();
        var roomTypes = (project?.Rooms ?? new List<Domain.Room>()).Where(r => r.Code.Length > 0)
            .GroupBy(r => r.Code.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First().RoomType, StringComparer.OrdinalIgnoreCase);
        var symbols = store.Symbols().ToDictionary(s => s.Id);
        var classes = store.LinearClasses().ToDictionary(c => c.Id);
        foreach (var sheet in store.Sheets())
        {
            var take = store.Takeoffs(sheet.Id).OrderByDescending(x => x.Status == "APPLIED").ThenByDescending(x => x.RunAt).ThenByDescending(x => x.Id).FirstOrDefault();
            if (take is null) continue;
            t.AddTakeoff(sheet.Id, store.Hits(take.Id), store.Runs(take.Id), symbols, classes, roomTypes);
        }
        if (lengths != null) t.AddPointLengths(lengths);
        return t;
    }

    /// <summary>Adds one takeoff: measured run metres per system / counted points per system (overall and per room type).</summary>
    public void AddTakeoff(long sheetId, IEnumerable<DwgHit> hits, IEnumerable<DwgRun> runs, IReadOnlyDictionary<long, DwgSymbol> symbols,
        IReadOnlyDictionary<long, DwgLinearClass> classes, IReadOnlyDictionary<string, string> roomTypes)
    {
        string RoomType(string room) => room.Length > 0 && roomTypes.TryGetValue(room.Trim(), out var rt) ? rt : "";
        var points = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var metres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(Dictionary<string, double> d, string sys, string room, double v)
        {
            d[Key(sys)] = d.GetValueOrDefault(Key(sys)) + v;
            var rt = RoomType(room);
            if (rt.Length > 0) d[Key(sys, rt)] = d.GetValueOrDefault(Key(sys, rt)) + v;
        }
        foreach (var h in hits.Where(h => DwgHitStatus.Counts(h.Status)))
        {
            if (!symbols.TryGetValue(h.SymbolId, out var s) || s.Excluded) continue;
            Add(points, TakeoffCounter.SystemOf(s), h.Room, 1);
        }
        foreach (var r in runs.Where(r => DwgHitStatus.Counts(r.Status) && r.LengthM > 0))
        {
            if (!classes.TryGetValue(r.ClassId, out var c) || c.System.Trim().Length == 0) continue;
            Add(metres, c.System, r.Room, r.LengthM);
        }
        foreach (var (k, m) in metres)
        {
            if (!points.TryGetValue(k, out var n) || n <= 0) continue;
            if (!_ratio.TryGetValue(k, out var a)) _ratio[k] = a = new Acc();
            a.Metres += m; a.Points += n; a.Sheets.Add(sheetId);
        }
    }

    /// <summary>Adds computed cable lengths per point (exact: DB -> point over the measured routes).</summary>
    public void AddPointLengths(IEnumerable<PointLength> lengths)
    {
        foreach (var l in lengths.Where(l => l.Traceable && l.TotalM > 0))
        {
            var sys = l.System.Length > 0 ? l.System : l.Item;
            if (sys.Length == 0) continue;
            foreach (var k in new[] { Key(sys), l.RoomType.Length > 0 ? Key(sys, l.RoomType) : "" }.Where(k => k.Length > 0))
            {
                if (!_exact.TryGetValue(k, out var list)) _exact[k] = list = new List<double>();
                list.Add(l.TotalM);
            }
        }
    }

    /// <summary>Average route length per point for the spec (room type optional), with its source; null when no takeoff covers it.</summary>
    public RouteLengthHit? Find(ItemSpec spec, string? roomType = null)
    {
        foreach (var sys in SystemsFor(spec).Distinct())
        {
            foreach (var rt in new[] { roomType ?? "", "" }.Distinct())
            {
                var k = Key(sys, rt);
                var where = rt.Length > 0 ? $" in {rt} rooms" : "";
                if (_exact.TryGetValue(k, out var list) && list.Count > 0)
                    return new RouteLengthHit(Math.Round(list.Average(), 2), $"drawings cable lengths - average of {list.Count} {sys} point(s){where} (DB to point over the measured routes)");
                if (_ratio.TryGetValue(k, out var a) && a.Points > 0 && a.Metres > 0)
                    return new RouteLengthHit(Math.Round(a.Metres / a.Points, 2),
                        $"drawings takeoff - {a.Metres.ToString("0.#", CultureInfo.InvariantCulture)} m of {sys} routes / {a.Points:0} {sys} point(s){where} on {a.Sheets.Count} sheet(s)");
            }
        }
        return null;
    }

    /// <summary>A hook for <see cref="AssemblyService.RouteLengthFor"/> that rebuilds the table at most every <paramref name="maxAge"/>.</summary>
    public static Func<ItemSpec, RouteLengthHit?> Hook(Func<IDrawingStore?> store, Func<ProjectSnapshot?> project, TimeSpan? maxAge = null, Func<DateTime>? clock = null)
    {
        DrawingRouteLengths? cached = null;
        var at = DateTime.MinValue;
        var age = maxAge ?? TimeSpan.FromMinutes(2);
        var now = clock ?? (() => DateTime.Now);
        var gate = new object();
        return spec =>
        {
            lock (gate)
            {
                if (cached is null || now() - at > age)
                {
                    try { cached = store() is { } s ? Load(s, project()) : new DrawingRouteLengths(); }
                    catch (Exception) { cached = new DrawingRouteLengths(); }   // drawings not available: template defaults
                    at = now();
                }
                return cached.Find(spec);
            }
        };
    }
}
