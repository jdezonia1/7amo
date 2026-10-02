using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Drawings;

/// <summary>[drawings] A proposed PROJECT QTY change (room x stage x item). Never applied without the user ticking it.</summary>
public sealed class QtyProposal
{
    public string Building { get; init; } = "";
    public string Room { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Item { get; init; } = "";
    public string Unit { get; init; } = "no";
    public double? Current { get; init; }
    public double Proposed { get; init; }
    public double Diff => Proposed - (Current ?? 0);
    /// <summary>NEW (no PROJECT QTY yet) / CHANGE / SAME / NOT ON DRAWING (current &gt; 0, nothing found).</summary>
    public string Status { get; init; } = "";
    public bool Accept { get; set; }
    public string Note { get; init; } = "";
}

/// <summary>One row of the takeoff table (per symbol / tag).</summary>
public sealed class TakeoffRow
{
    public long SymbolId { get; init; }
    public string Tag { get; init; } = "";
    public string System { get; init; } = "";
    public string Mount { get; init; } = "W";
    public bool Excluded { get; init; }
    public int Marks { get; init; }
    public Dictionary<string, double> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The table printed in the left panel of a takeoff sheet (one sheet x system).</summary>
public sealed class TakeoffTable
{
    public string System { get; init; } = "";
    public List<string> Columns { get; init; } = new();
    public List<TakeoffRow> Rows { get; } = new();
    public Dictionary<string, double> Totals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Marks { get; set; }
}

/// <summary>[drawings] Counting rules applied to hits: per room x stage x item, per sheet table, and the PROJECT QTY diff.</summary>
public static class TakeoffCounter
{
    /// <summary>Mount used for a hit: C when a mounting height read on the drawing is at / above the ceiling rule (H &gt;= 3000).</summary>
    public static string MountOf(DwgHit h, DwgSymbol s, DrawingSettings settings) =>
        h.MountingHeightM > 0 && h.MountingHeightM >= settings.CeilingFromHeightM ? "C" : (s.Mount.Length > 0 ? s.Mount : "W");

    /// <summary>Quantities per (room, stage, item) from the counted hits.</summary>
    public static Dictionary<(string Room, string Stage, string Item), double> Count(IEnumerable<DwgHit> hits, IReadOnlyDictionary<long, DwgSymbol> symbols, DrawingSettings settings)
    {
        var res = new Dictionary<(string, string, string), double>();
        foreach (var h in hits.Where(h => DwgHitStatus.Counts(h.Status)))
        {
            if (!symbols.TryGetValue(h.SymbolId, out var s)) continue;
            foreach (var c in StageRules.Cells(s, settings.StageRules, MountOf(h, s, settings)))
            {
                var key = (Norm(h.Room), c.Stage, c.Item);
                res[key] = res.GetValueOrDefault(key) + c.Qty;
            }
        }
        return res;
    }

    /// <summary>Metres per (room, stage, item) from measured runs (split by room boundaries when given).</summary>
    public static Dictionary<(string Room, string Stage, string Item), double> Lengths(IEnumerable<DwgRun> runs, IReadOnlyDictionary<long, DwgLinearClass> classes, RoomAssigner? rooms, double metresPerUnit)
    {
        var res = new Dictionary<(string, string, string), double>();
        foreach (var r in runs.Where(r => DwgHitStatus.Counts(r.Status)))
        {
            if (!classes.TryGetValue(r.ClassId, out var c)) continue;
            var targets = QtyTarget.Parse(c.Targets);
            if (targets.Count == 0) continue;
            var pts = Poly.ParsePoints(r.Points);
            Dictionary<string, double> perRoom;
            if (pts.Count < 2 || rooms is null || rooms.IsEmpty || r.Room.Length > 0 && r.Origin is DwgOrigins.Ifc or DwgOrigins.Json)
                perRoom = new Dictionary<string, double> { [r.Room.Length > 0 ? r.Room : RoomAssigner.Unassigned] = pts.Count >= 2 ? Poly.PolylineLength(pts) : 1 };
            else perRoom = rooms.SplitLength(pts);
            var total = perRoom.Values.Sum();
            foreach (var (room, units) in perRoom)
            {
                // the stored LengthM may include manual corrections (or come from the model); share it by the geometric split
                var m = total > 0 && r.LengthM > 0 ? r.LengthM * units / total : units * metresPerUnit;
                foreach (var t in targets)
                {
                    var key = (Norm(room), t.Stage, t.Item);
                    res[key] = res.GetValueOrDefault(key) + m * t.Factor;
                }
            }
        }
        return res;
    }

    private static string Norm(string room) => string.IsNullOrWhiteSpace(room) ? RoomAssigner.Unassigned : room.Trim().ToUpperInvariant();

    /// <summary>One table per system for the takeoff sheet (columns from the stage rules).</summary>
    public static List<TakeoffTable> Tables(IEnumerable<DwgHit> hits, IReadOnlyDictionary<long, DwgSymbol> symbols, DrawingSettings settings)
    {
        var cols = settings.StageRules.Select(r => r.Column).Distinct().ToList();
        var res = new List<TakeoffTable>();
        var counted = hits.Where(h => DwgHitStatus.Counts(h.Status) && symbols.ContainsKey(h.SymbolId)).ToList();
        foreach (var sys in counted.Select(h => SystemOf(symbols[h.SymbolId])).Distinct().OrderBy(SystemOrder).ThenBy(s => s))
        {
            var t = new TakeoffTable { System = sys, Columns = cols };
            foreach (var g in counted.Where(h => SystemOf(symbols[h.SymbolId]) == sys).GroupBy(h => (h.SymbolId, Mount: MountOf(h, symbols[h.SymbolId], settings))))
            {
                var s = symbols[g.Key.SymbolId];
                var row = new TakeoffRow { SymbolId = s.Id, Tag = TagOf(s) + (g.Key.Mount != s.Mount && g.Key.Mount == "C" ? $" (H>={settings.CeilingFromHeightM * 1000:0})" : ""), System = sys, Mount = g.Key.Mount, Excluded = s.Excluded, Marks = g.Count() };
                foreach (var c in cols) row.Columns[c] = 0;
                foreach (var h in g)
                    foreach (var cell in StageRules.Cells(s, settings.StageRules, g.Key.Mount))
                        row.Columns[cell.Column] = row.Columns.GetValueOrDefault(cell.Column) + cell.Qty;
                t.Rows.Add(row);
            }
            t.Rows.Sort((a, b) => string.CompareOrdinal(b.Mount, a.Mount) != 0 ? string.CompareOrdinal(a.Mount, b.Mount) : string.CompareOrdinal(a.Tag, b.Tag));
            foreach (var c in cols) t.Totals[c] = t.Rows.Where(r => !r.Excluded).Sum(r => r.Columns.GetValueOrDefault(c));
            t.Marks = t.Rows.Sum(r => r.Marks);
            res.Add(t);
        }
        return res;
    }

    public static string SystemOf(DwgSymbol s) => (s.System.Length > 0 ? s.System : s.Item.Length > 0 ? s.Item : "OTHER").Trim().ToUpperInvariant();
    public static string TagOf(DwgSymbol s) => (s.Tag.Length > 0 ? s.Tag : s.Name).Trim().ToUpperInvariant();

    private static int SystemOrder(string s) => s switch { "POWER" => 0, "LIGHT" => 1, "GRMS" => 2, "DATA" => 3, "AV" => 4, _ => 9 };

    /// <summary>Numbers the counted marks of each system page in reading order (bands of rows, then left to right).</summary>
    public static void NumberMarks(IList<DwgHit> hits, IReadOnlyDictionary<long, DwgSymbol> symbols)
    {
        foreach (var g in hits.Where(h => symbols.ContainsKey(h.SymbolId)).GroupBy(h => SystemOf(symbols[h.SymbolId])))
        {
            var band = Math.Max(10, g.Select(h => Math.Max(h.W, h.H)).DefaultIfEmpty(20).Average() * 3);
            var n = 0;
            foreach (var h in g.OrderBy(h => Math.Floor(h.Center.Y / band)).ThenBy(h => h.Center.X))
                h.MarkNo = DwgHitStatus.Counts(h.Status) ? ++n : 0;
        }
    }
}

/// <summary>[drawings] Takeoff counts vs the current PROJECT QTY -> rows the user accepts one by one.</summary>
public static class QtyDiff
{
    /// <summary>
    /// Rows for every counted key, plus keys of the rooms on this sheet that have PROJECT QTY for a counted item but nothing was
    /// found (proposed 0). Rooms outside every boundary are listed as "(NO ROOM)" and cannot be accepted.
    /// </summary>
    public static List<QtyProposal> Build(IReadOnlyDictionary<(string Room, string Stage, string Item), double> counts, IEnumerable<RoomQty> current, string building, string unit = "no")
    {
        var cur = current.Where(q => q.Building.Equals(building, StringComparison.OrdinalIgnoreCase))
            .GroupBy(q => (q.Room.Trim().ToUpperInvariant(), q.Stage.Trim().ToUpperInvariant(), q.Item.Trim().ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        var rooms = counts.Keys.Select(k => k.Room).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = counts.Keys.ToHashSet();
        var stageItems = counts.Keys.Select(k => (k.Stage, k.Item)).ToHashSet();
        foreach (var k in cur.Keys.Where(k => rooms.Contains(k.Item1) && stageItems.Contains((k.Item2, k.Item3)))) keys.Add(k);
        var res = new List<QtyProposal>();
        foreach (var k in keys.OrderBy(k => k.Room).ThenBy(k => k.Stage).ThenBy(k => k.Item))
        {
            var proposed = Math.Round(counts.GetValueOrDefault(k), 2);
            double? c = cur.TryGetValue(k, out var v) ? v : null;
            var status = c is null ? "NEW" : Math.Abs(c.Value - proposed) < 0.005 ? "SAME" : proposed == 0 ? "NOT ON DRAWING" : "CHANGE";
            res.Add(new QtyProposal
            {
                Building = building, Room = k.Room, Stage = k.Stage, Item = k.Item, Unit = unit, Current = c, Proposed = proposed, Status = status,
                Note = k.Room == RoomAssigner.Unassigned ? "outside every room boundary - calibrate / import rooms" : "",
            });
        }
        return res;
    }

    public sealed record ApplyResult(int Updated, int Inserted, int Rejected, string Summary);

    /// <summary>
    /// Writes the ticked rows to PROJECT QTY (one audited batch) and records every decision (accepted and rejected) in the
    /// drawings store. Rows without a room, or unticked, are recorded as REJECTED and change nothing.
    /// </summary>
    public static ApplyResult Apply(IProjectStore store, IDrawingStore drawings, IReadOnlyList<QtyProposal> rows, DwgSheet sheet, long takeoffId, string origin = "TAKEOFF")
    {
        var accepted = rows.Where(r => r.Accept && r.Room != RoomAssigner.Unassigned && r.Status != "SAME").ToList();
        var existing = store.All<RoomQty>();
        int upd = 0, ins = 0;
        var source = $"DRAWING {sheet.SheetNo} REV {sheet.Revision}".Trim();
        if (accepted.Count > 0)
            store.Batch(b =>
            {
                foreach (var r in accepted)
                {
                    var match = existing.Where(q => q.Building.Equals(r.Building, StringComparison.OrdinalIgnoreCase) && LedgerKeys.Key(q.Room, q.Stage, q.Item) == LedgerKeys.Key(r.Room, r.Stage, r.Item)).ToList();
                    if (match.Count > 0)
                    {
                        var first = match[0];
                        first.Qty = r.Proposed; first.Source = source;
                        b.Update(first);
                        foreach (var extra in match.Skip(1)) b.Delete(extra);
                        upd++;
                    }
                    else
                    {
                        b.Insert(new RoomQty { Building = r.Building, Room = r.Room, Stage = r.Stage, Item = r.Item, Unit = r.Unit, Qty = r.Proposed, Source = source });
                        ins++;
                    }
                }
            }, $"PROJECT QTY from drawing {sheet.Label}: {upd} changed, {ins} new");
        var now = drawings.Clock();
        var decisions = rows.Where(r => r.Status != "SAME").Select(r => new DwgQtyDecision
        {
            Building = r.Building, Room = r.Room, Stage = r.Stage, Item = r.Item, Unit = r.Unit, Current = r.Current, Proposed = r.Proposed,
            Decision = accepted.Contains(r) ? "ACCEPTED" : "REJECTED", SheetId = sheet.Id, TakeoffId = takeoffId, Origin = origin,
            DecidedBy = drawings.User, DecidedAt = now, Note = r.Note,
        }).ToList();
        if (decisions.Count > 0) drawings.AddDecisions(decisions);
        var rejected = decisions.Count - accepted.Count;
        return new ApplyResult(upd, ins, rejected, $"{upd} PROJECT QTY cells changed, {ins} added, {rejected} not applied");
    }
}
