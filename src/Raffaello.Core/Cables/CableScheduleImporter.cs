using System.Globalization;
using System.Text;
using Raffaello.Core.Import;

namespace Raffaello.Core.Cables;

/// <summary>
/// [cables] Cable schedule import (Excel / CSV). The header row is found anywhere in the first 40 rows of each sheet; columns are mapped by
/// header text (synonyms below), or by the mapping remembered for the same header layout (<see cref="CableImportProfile"/>), or by the user's
/// mapping. Sizes are parsed from one column ("4C x 16mm²") or from CORES + MM² columns.
/// </summary>
public static class CableScheduleImporter
{
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["FROM"] = new[] { "FROM", "SOURCE", "FROM PANEL", "FED FROM", "FEEDER FROM", "ORIGIN", "SUPPLY FROM", "FROM BOARD", "FROM DB", "START" },
        ["TO"] = new[] { "TO", "DESTINATION", "TO PANEL", "LOAD", "FEEDING", "TO BOARD", "TO DB", "TO EQUIPMENT", "EQUIPMENT", "END" },
        ["SIZE"] = new[] { "CABLE SIZE", "SIZE", "CABLE", "CABLE TYPE AND SIZE", "CABLE SIZE MM2", "SIZE MM2", "CORES X SIZE", "CONDUCTOR SIZE", "CSA", "CABLE SPECIFICATION" },
        ["CORES"] = new[] { "CORES", "NO OF CORES", "CORE", "NO CORES" },
        ["MM2"] = new[] { "MM2", "CROSS SECTION", "SQ MM", "SECTION" },
        ["LENGTH"] = new[] { "LENGTH", "LENGTH M", "CABLE LENGTH", "APPROX LENGTH", "EST LENGTH", "ESTIMATED LENGTH", "DESIGN LENGTH", "ROUTE LENGTH", "LEN", "LENGTH MTR" },
        ["MEASURED"] = new[] { "MEASURED LENGTH", "ACTUAL LENGTH", "MEASURED", "AS BUILT LENGTH" },
        ["REF"] = new[] { "CABLE REF", "CABLE NO", "CABLE TAG", "CABLE ID", "TAG", "REF", "REF NO", "CABLE NUMBER", "CABLE NO." },
        ["CIRCUIT"] = new[] { "CIRCUIT", "CIRCUIT REF", "CCT", "FEEDER", "FEEDER NO", "WAY", "CIRCUIT NO" },
        ["BREAKER"] = new[] { "BREAKER", "MCCB", "CB", "PROTECTION", "RATING", "CB RATING", "BREAKER RATING", "AMPS", "IN A", "PROTECTIVE DEVICE" },
        ["INSULATION"] = new[] { "TYPE", "CABLE TYPE", "INSULATION" },
        ["CONDUCTOR"] = new[] { "CONDUCTOR", "CU AL", "MATERIAL" },
        ["EARTH"] = new[] { "EARTH", "ECC", "CPC", "EARTH CABLE", "EARTH SIZE", "E" },
        ["LEVEL"] = new[] { "LEVEL", "FLOOR" },
        ["BUILDING"] = new[] { "BUILDING", "BLDG" },
        ["NOTES"] = new[] { "REMARKS", "NOTES", "COMMENTS" },
    };

    public static string Norm(string h) => new string((h ?? "").ToUpperInvariant().Replace("²", "2").Where(char.IsLetterOrDigit).ToArray());

    private static readonly Dictionary<string, string> Synonyms = Fields.SelectMany(kv => kv.Value.Select(v => (Norm(v), kv.Key)))
        .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Key);

    public static CableReadResult Read(string path, CableReadOptions o)
    {
        var res = new CableReadResult { FileName = path, Kind = CableSources.Schedule };
        foreach (var (sheet, rows) in Grids(path))
        {
            var (hr, headers) = FindHeader(rows);
            if (hr < 0) continue;
            res.Headers.AddRange(headers.Values);
            var sig = string.Join("|", headers.OrderBy(h => h.Key).Select(h => Norm(h.Value)));
            res.HeaderSignature = sig;
            var map = o.Mapping?.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase)
                      ?? Profile(o.Profiles, sig)
                      ?? Detect(headers);
            foreach (var kv in map) res.Mapping[kv.Key] = kv.Value;
            var colOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, header) in map)
            {
                var col = headers.FirstOrDefault(h => Norm(h.Value) == Norm(header)).Key;
                if (col > 0) colOf[field] = col;
            }
            if (!colOf.ContainsKey("FROM") || !colOf.ContainsKey("TO")) { res.Issues.Add($"{sheet}: FROM / TO columns not mapped."); continue; }
            string G(Dictionary<int, string> r, string f) => colOf.TryGetValue(f, out var c) && r.TryGetValue(c, out var v) ? v.Trim() : "";
            double? N(Dictionary<int, string> r, string f)
            {
                var s = new string(G(r, f).Replace(",", "").TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ' ').ToArray()).Trim();
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
            foreach (var (n, r) in rows.Where(x => x.Row > hr))
            {
                var from = G(r, "FROM"); var to = G(r, "TO");
                if (from.Length == 0 || to.Length == 0) continue;
                var sizeText = G(r, "SIZE");
                if (sizeText.Length == 0 && N(r, "CORES") is { } cores && N(r, "MM2") is { } mm2) sizeText = $"{cores}X{mm2}";
                else if (N(r, "CORES") is { } c2 && sizeText.Length > 0 && !sizeText.Contains('X', StringComparison.OrdinalIgnoreCase) && double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) sizeText = $"{c2}X{sizeText}";
                var type = G(r, "INSULATION");
                var size = CableSize.Parse(sizeText + " " + type + " " + G(r, "CONDUCTOR"));
                if (!size.IsValid) size = CableSize.Parse(sizeText);
                if (!size.IsValid) res.Issues.Add($"{sheet} row {n}: size '{sizeText}' not recognised ({from} -> {to}).");
                var earthText = G(r, "EARTH");
                var earth = size.EarthKey.Length > 0 ? size.EarthKey : CableSize.Parse(earthText.Contains('X', StringComparison.OrdinalIgnoreCase) ? earthText : earthText.Length > 0 ? "1X" + earthText : "").Key;
                var f0 = PanelNames.Parse(from); var t0 = PanelNames.Parse(to);
                var building = G(r, "BUILDING") is { Length: > 0 } b ? b.ToUpperInvariant() : PanelNames.BuildingOf(f0) is { Length: > 0 } fb ? fb : PanelNames.BuildingOf(t0) is { Length: > 0 } tb ? tb : o.Building;
                var f = PanelNames.Parse(from, building); var t = PanelNames.Parse(to, building);
                res.Runs.Add(new CableRun
                {
                    Ref = G(r, "REF"), Building = building, Level = G(r, "LEVEL") is { Length: > 0 } lv ? lv : t.Level.Length > 0 ? t.Level : f.Level,
                    FromKey = f.Key, FromName = PanelNames.Tidy(from), ToKey = t.Key, ToName = PanelNames.Tidy(to),
                    Cores = size.Cores, SizeMm2 = size.Mm2, SizeKey = size.IsValid ? size.Key : "", Conductor = size.Conductor.Length > 0 ? size.Conductor : "CU", Insulation = size.Insulation,
                    EarthSizeKey = earth, DesignLength = N(r, "LENGTH"), MeasuredLength = N(r, "MEASURED"), CircuitRef = G(r, "CIRCUIT"), Breaker = G(r, "BREAKER"),
                    Status = CableStatus.Proposed, SourceKind = CableSources.Schedule, SourceDoc = Path.GetFileName(path) + " / " + sheet, SourcePage = n, Confidence = 1, Notes = G(r, "NOTES"),
                });
            }
        }
        res.Pages = 1;
        if (res.Runs.Count == 0 && res.Issues.Count == 0) res.Issues.Add("No sheet with FROM / TO headers found.");
        return res;
    }

    /// <summary>FIELD -> header from the header texts (exact synonym first, then "contains").</summary>
    public static Dictionary<string, string> Detect(IReadOnlyDictionary<int, string> headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, h) in headers.OrderBy(h => h.Key))
            if (Synonyms.TryGetValue(Norm(h), out var f) && !map.ContainsKey(f)) map[f] = h;
        foreach (var (_, h) in headers.OrderBy(h => h.Key))
        {
            if (map.ContainsValue(h)) continue;
            var n = Norm(h);
            foreach (var (field, syn) in Fields)
            {
                if (map.ContainsKey(field)) continue;
                if (syn.Where(s => Norm(s).Length >= 4).Any(s => n.Contains(Norm(s)))) { map[field] = h; break; }
            }
        }
        return map;
    }

    private static Dictionary<string, string>? Profile(IReadOnlyList<CableImportProfile> profiles, string sig)
    {
        var p = profiles.Where(x => x.Signature == sig).OrderByDescending(x => x.LastUsed).FirstOrDefault();
        if (p is null) return null;
        return p.Mapping.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public static string MappingText(IReadOnlyDictionary<string, string> map) => string.Join(";", map.Select(kv => $"{kv.Key}={kv.Value}"));

    private static (int Row, Dictionary<int, string> Headers) FindHeader(List<(int Row, Dictionary<int, string> Cells)> rows)
    {
        var best = (Row: -1, Score: 0, H: new Dictionary<int, string>());
        foreach (var (n, cells) in rows.Take(40))
        {
            var hits = cells.Where(c => c.Value.Trim().Length > 0).Select(c => Synonyms.GetValueOrDefault(Norm(c.Value))).Where(f => f != null).ToHashSet();
            if (!hits.Contains("FROM") || !hits.Contains("TO")) continue;
            if (hits.Count > best.Score) best = (n, hits.Count, cells.Where(c => c.Value.Trim().Length > 0).ToDictionary(c => c.Key, c => c.Value.Trim()));
        }
        return (best.Row, best.H);
    }

    private static IEnumerable<(string Sheet, List<(int Row, Dictionary<int, string> Cells)> Rows)> Grids(string path)
    {
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, true);
            var lines = TableReader.ParseCsv(sr.ReadToEnd());
            yield return ("CSV", lines.Select((l, i) => (i + 1, l.Select((v, c) => (c + 1, v)).ToDictionary(x => x.Item1, x => x.v))).ToList());
            yield break;
        }
        using var x = new XlsxStreamReader(path);
        foreach (var sheet in x.Sheets.Keys.ToList())
            yield return (sheet, x.ReadRows(sheet).Select(r => (r.Number, r.Values.ToDictionary(v => v.Key, v => v.Value))).ToList());
    }
}
