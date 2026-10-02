using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Cables;
using Raffaello.Core.Drawings;

namespace Raffaello.Core.Wiring;

/// <summary>A text on a drawing sheet (sheet units: pixels of the sheet raster, or CAD model units).</summary>
public sealed record SheetLabel(string Text, PointD At);

/// <summary>One sheet's linear takeoff with its labels, for <see cref="CableMeasuredLengths.Propose"/>.</summary>
public sealed record SheetRoutes(DwgSheet Sheet, IReadOnlyList<DwgRun> Runs, IReadOnlyList<SheetLabel> Labels);

/// <summary>A measured route length proposed for a register cable run. Nothing is saved until the user confirms it.</summary>
public sealed class MeasuredLengthProposal
{
    public required CableRun Run { get; init; }
    public double MeasuredM { get; init; }
    public double? Current => Run.MeasuredLength;
    public double? Design => Run.DesignLength;
    public long SheetId { get; init; }
    public string Sheet { get; init; } = "";
    public List<long> DwgRunIds { get; init; } = new();
    public string FromLabel { get; init; } = "";
    public string ToLabel { get; init; } = "";
    public double Confidence { get; init; }
    public string How { get; init; } = "";
    /// <summary>Difference to the design length (share), null without a design length.</summary>
    public double? VsDesign => Run.DesignLength is > 0 ? (MeasuredM - Run.DesignLength.Value) / Run.DesignLength.Value : null;
    public bool Changes => Run.MeasuredLength is not double m || Math.Abs(m - MeasuredM) > 0.01;
}

/// <summary>
/// Cables <- Drawings: the measured route length of a register run from the Drawings linear takeoff. A traced run (tray / conduit / cable
/// polyline) whose two ends sit next to panel names on the sheet (or whose circuit text names FROM and TO) is matched to the register runs
/// on the same route (panel names normalised, aliases followed). Proposals only - the user confirms, then MeasuredLength is written
/// (audited) and the cable flags use it.
/// </summary>
public static class CableMeasuredLengths
{
    private static readonly Regex PairSplit = new(@"\s*(?:->|=>|>|\bTO\b|\bFROM\b|/|\s-\s|<>)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Words on one text line joined into labels ("SMDB" "HT-Z1-LB2-01" -> "SMDB HT-Z1-LB2-01").</summary>
    public static List<SheetLabel> MergeWords(IEnumerable<(string Text, RectD Box)> words)
    {
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => Math.Round(w.Box.Center.Y / Math.Max(1, w.Box.H))).ThenBy(w => w.Box.X).ToList();
        var res = new List<SheetLabel>();
        var used = new bool[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var text = list[i].Text.Trim();
            var box = list[i].Box;
            res.Add(new SheetLabel(text, box.Center));
            for (var j = 0; j < list.Count; j++)
            {
                if (used[j]) continue;
                var w = list[j];
                var h = Math.Max(box.H, w.Box.H);
                if (Math.Abs(w.Box.Center.Y - box.Center.Y) > h * 0.5 || w.Box.X < box.Right - h * 0.2 || w.Box.X - box.Right > h * 1.2) continue;
                used[j] = true;
                text += " " + w.Text.Trim();
                box = new RectD(box.X, Math.Min(box.Y, w.Box.Y), w.Box.Right - box.X, Math.Max(box.Bottom, w.Box.Bottom) - Math.Min(box.Y, w.Box.Y));
                res.Add(new SheetLabel(text, box.Center));
            }
        }
        return res;
    }

    /// <summary>Panel labels of a sheet: vector PDF words (merged per line) or CAD texts; scans have none (trace + type the names instead).</summary>
    public static List<SheetLabel> LabelsOf(DwgSheet s)
    {
        try
        {
            return s.SourceKind switch
            {
                DwgSourceKinds.Pdf => MergeWords(VectorPdfReader.Read(s.FilePath, s.Page, s.Dpi).Words.Select(w => (w.Text, w.Box))),
                DwgSourceKinds.Dxf or DwgSourceKinds.Dwg => CadReader.Read(s.FilePath).Texts.Select(t => new SheetLabel(t.Value, t.Point)).ToList(),
                _ => new List<SheetLabel>(),
            };
        }
        catch (Exception) { return new List<SheetLabel>(); }
    }

    /// <summary>The routes of every sheet's latest takeoff with their labels.</summary>
    public static List<SheetRoutes> Load(IDrawingStore store, Func<DwgSheet, IReadOnlyList<SheetLabel>>? labels = null)
    {
        var res = new List<SheetRoutes>();
        foreach (var s in store.Sheets())
        {
            var t = store.Takeoffs(s.Id).OrderByDescending(x => x.RunAt).ThenByDescending(x => x.Id).FirstOrDefault();
            if (t is null) continue;
            var runs = store.Runs(t.Id).Where(r => DwgHitStatus.Counts(r.Status) && r.LengthM > 0).ToList();
            if (runs.Count == 0) continue;
            res.Add(new SheetRoutes(s, runs, (labels ?? LabelsOf)(s)));
        }
        return res;
    }

    /// <summary>Two panel names written in a circuit / note text ("SMDB-HT-Z1-LB2-01 TO DB-HT-L02-01").</summary>
    public static (string From, string To)? PairFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = PairSplit.Split(text.Trim()).Where(p => p.Trim().Length > 0).ToList();
        return parts.Count == 2 && PanelNames.LooksLikePanel(parts[0]) && PanelNames.LooksLikePanel(parts[1]) ? (parts[0].Trim(), parts[1].Trim()) : null;
    }

    /// <summary>
    /// Proposals for register runs: per sheet, the traced runs between the same two panels are added up (a route drawn in pieces).
    /// <paramref name="radiusM"/> = how far (site metres) a label may sit from the end of the traced route.
    /// </summary>
    public static List<MeasuredLengthProposal> Propose(CableSnapshot register, IEnumerable<SheetRoutes> sheets, double radiusM = 3.0)
    {
        var resolver = register.Resolver();
        var byRoute = register.Runs.Where(r => r.Status != CableStatus.Rejected && r.FromKey.Length > 0 && r.ToKey.Length > 0)
            .GroupBy(r => CableKeys.Route(resolver.Canonical(r.FromKey), resolver.Canonical(r.ToKey))).ToDictionary(g => g.Key, g => g.ToList());
        var res = new List<MeasuredLengthProposal>();
        foreach (var sh in sheets)
        {
            var s = sh.Sheet;
            var radius = s.MetresPerUnit > 0 ? radiusM / s.MetresPerUnit : 60;
            var panelLabels = sh.Labels.Where(l => PanelNames.LooksLikePanel(l.Text)).ToList();
            string Key(string text) => resolver.Canonical(PanelNames.KeyOf(text, s.Building));
            SheetLabel? Near(PointD p) => panelLabels.Select(l => (l, d: l.At.DistanceTo(p))).Where(x => x.d <= radius).OrderBy(x => x.d).Select(x => x.l).FirstOrDefault();

            var matched = new Dictionary<string, (double M, List<long> Ids, string From, string To, double Conf, List<string> How)>();
            foreach (var r in sh.Runs)
            {
                string from, to; double conf; string how;
                var pair = PairFromText(r.Circuit) ?? PairFromText(r.Note);
                if (pair is { } c)
                {
                    (from, to) = c; conf = 0.95; how = $"circuit text '{(PairFromText(r.Circuit) != null ? r.Circuit : r.Note)}'";
                }
                else
                {
                    var pts = Poly.ParsePoints(r.Points);
                    if (pts.Count < 2) continue;
                    var a = Near(pts[0]); var b = Near(pts[^1]);
                    if (a is null || b is null || Key(a.Text) == Key(b.Text)) continue;
                    (from, to) = (a.Text, b.Text); conf = 0.85; how = $"labels at the route ends ('{a.Text}' / '{b.Text}')";
                }
                var route = CableKeys.Route(Key(from), Key(to));
                if (!byRoute.ContainsKey(route)) continue;
                if (!matched.TryGetValue(route, out var m)) m = (0, new List<long>(), from, to, conf, new List<string>());
                m.M += r.LengthM; m.Ids.Add(r.Id); m.Conf = Math.Min(m.Conf, conf); if (!m.How.Contains(how)) m.How.Add(how);
                matched[route] = m;
            }
            foreach (var (route, m) in matched)
                foreach (var run in byRoute[route])
                    res.Add(new MeasuredLengthProposal
                    {
                        Run = run, MeasuredM = Math.Round(m.M, 2), SheetId = s.Id, Sheet = $"{s.SheetNo} rev {s.Revision}", DwgRunIds = m.Ids, FromLabel = m.From, ToLabel = m.To,
                        Confidence = m.Ids.Count > 1 ? Math.Min(m.Conf, 0.8) : m.Conf,
                        How = $"{m.Ids.Count} traced run(s) on sheet {s.SheetNo} rev {s.Revision}, {string.Join("; ", m.How.Take(3))}",
                    });
        }
        // a run matched on several sheets: keep the most confident (then the longest) proposal, the others are listed in How
        return res.GroupBy(p => p.Run.Id).Select(g =>
        {
            var best = g.OrderByDescending(p => p.Confidence).ThenByDescending(p => p.MeasuredM).First();
            return g.Count() == 1 ? best : new MeasuredLengthProposal
            {
                Run = best.Run, MeasuredM = best.MeasuredM, SheetId = best.SheetId, Sheet = best.Sheet, DwgRunIds = best.DwgRunIds, FromLabel = best.FromLabel, ToLabel = best.ToLabel,
                Confidence = best.Confidence, How = best.How + $" (also on {string.Join(", ", g.Where(p => p != best).Select(p => $"{p.Sheet}: {p.MeasuredM:0.##} m"))})",
            };
        }).OrderBy(p => p.Run.Ref).ToList();
    }

    /// <summary>Writes the confirmed measured lengths (one audited update per run). Returns how many runs changed.</summary>
    public static int Apply(CableService svc, IEnumerable<MeasuredLengthProposal> confirmed, string user)
    {
        var n = 0;
        foreach (var p in confirmed.Where(p => p.Changes))
        {
            var r = p.Run;
            var old = r.MeasuredLength;
            r.MeasuredLength = p.MeasuredM;
            var note = $"measured {p.MeasuredM.ToString("0.##", CultureInfo.InvariantCulture)} m from drawing {p.Sheet} ({p.FromLabel} -> {p.ToLabel}), confirmed by {user} {DateTime.Now:dd-MMM-yy}";
            r.Notes = string.Join("; ", new[] { r.Notes, note }.Where(x => x.Length > 0));
            svc.UpdateRun(r, $"Cable run {r.Ref} {r.Title}: measured length {(old is double o ? o.ToString("0.##", CultureInfo.InvariantCulture) : "-")} -> {p.MeasuredM:0.##} m from drawing {p.Sheet} takeoff");
            n++;
        }
        return n;
    }
}
