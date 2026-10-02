using System.Globalization;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Plans;

/// <summary>Plan view / head-office export filter. Stage and item narrow both PROJECT QTY and claims; subcontractor and invoice narrow claims only.</summary>
public sealed record PlanFilter(string? Stage = null, string? Item = null, string? Subcontractor = null, int? UpToInvoice = null)
{
    public static PlanFilter All { get; } = new();
    public bool Matches(string stage, string item) =>
        (string.IsNullOrEmpty(Stage) || stage.Equals(Stage, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(Item) || item.Equals(Item, StringComparison.OrdinalIgnoreCase));
}

public static class RoomStatusKinds
{
    public const string OverCap = "OVER CAP";
    public const string InProgress = "IN PROGRESS";
    public const string Done = "DONE";
    public const string NotStarted = "NOT STARTED";
    public const string NoProjectQty = "NO PROJECT QTY";
    public static readonly string[] All = { OverCap, InProgress, Done, NotStarted, NoProjectQty };

    /// <summary>House palette: dark red / yellow / green / grey / black.</summary>
    public static string Colour(string status) => status switch
    {
        OverCap => "8B0000",
        InProgress => "FFFF00",
        Done => "2E7D4F",
        NotStarted => "A6A6A6",
        _ => "404040",
    };
}

/// <summary>One room on the plan: totals over its stage x item keys (stages are never added for "% used" - it is capped per key).</summary>
public sealed record RoomPlanInfo(string Code, Room? Room, double ProjectQty, double Claimed, double ClaimedWithinCap, int Keys, int OverKeys, int PendingChecks,
    IReadOnlyDictionary<string, double> BySubcontractor, string Status)
{
    public double Remaining => ProjectQty - ClaimedWithinCap;
    public double UsedPct => ProjectQty <= 0 ? 0 : ClaimedWithinCap / ProjectQty;
    public string DominantSub => BySubcontractor.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).FirstOrDefault() ?? "";
    public string Tip => FormattableString.Invariant(
        $"{Code}  |  {Room?.RoomType} {Room?.AreaType}\nPROJECT {ProjectQty:N1}  CLAIMED {Claimed:N1}  REMAINING {Remaining:N1}  ({UsedPct:P0})\n{Status}{(PendingChecks > 0 ? $", {PendingChecks} checks pending" : "")}\n") +
        string.Join("  ", BySubcontractor.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N1}"));
}

public static class RoomStatusCalc
{
    public static Dictionary<string, RoomPlanInfo> Compute(ProjectSnapshot s, PlanFilter? filter = null)
    {
        filter ??= PlanFilter.All;
        var claims = s.Claims.Where(c => filter.Matches(c.Stage, c.Item)
                                         && (string.IsNullOrEmpty(filter.Subcontractor) || c.Subcontractor.Equals(filter.Subcontractor, StringComparison.OrdinalIgnoreCase))
                                         && (filter.UpToInvoice is null || c.InvoiceNo <= filter.UpToInvoice)).ToList();
        var qty = s.RoomQtys.Where(q => filter.Matches(q.Stage, q.Item)).ToList();
        var bal = LedgerRules.Balances(qty, claims);
        var pending = claims.Where(c => HeightCheck.IsPending(c) || LengthCheck.IsPending(c)).GroupBy(c => c.Room, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rooms = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var res = new Dictionary<string, RoomPlanInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in bal.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase))
            res[g.Key] = Info(g.Key, rooms.GetValueOrDefault(g.Key), g.ToList(), pending.GetValueOrDefault(g.Key));
        foreach (var r in rooms.Values.Where(r => !res.ContainsKey(r.Code)))
            res[r.Code] = Info(r.Code, r, new List<RoomBalance>(), pending.GetValueOrDefault(r.Code));
        return res;
    }

    private static RoomPlanInfo Info(string code, Room? room, List<RoomBalance> keys, int pending)
    {
        var cap = keys.Sum(k => k.ProjectQty);
        var claimed = keys.Sum(k => k.Claimed);
        var within = keys.Where(k => k.HasCap).Sum(k => Math.Clamp(k.Claimed, 0, k.ProjectQty));
        var over = keys.Count(k => k.HasCap && k.IsOver);
        var subs = keys.SelectMany(k => k.BySubcontractor).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        var status = over > 0 ? RoomStatusKinds.OverCap
            : cap <= 0 ? RoomStatusKinds.NoProjectQty
            : claimed <= 1e-9 ? RoomStatusKinds.NotStarted
            : within >= cap - 1e-6 ? RoomStatusKinds.Done
            : RoomStatusKinds.InProgress;
        return new RoomPlanInfo(code, room, cap, claimed, within, keys.Count, over, pending, subs, status);
    }

    /// <summary>Heat ramp white -> dark red for % used (0..1, capped).</summary>
    public static string HeatColour(double pct)
    {
        var t = Math.Clamp(pct, 0, 1);
        int L(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return $"{L(0xFF, 0x8B):X2}{L(0xFF, 0x00):X2}{L(0xFF, 0x00):X2}";
    }

    /// <summary>Stable categorical colours for subcontractors (alphabetical order).</summary>
    public static IReadOnlyDictionary<string, string> SubColours(IEnumerable<string> subs)
    {
        string[] palette = { "8B0000", "1F4E79", "2E7D4F", "C55A11", "7030A0", "BF9000", "00808C", "595959", "C00000", "4472C4" };
        return subs.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select((x, i) => (x, palette[i % palette.Length])).ToDictionary(t => t.x, t => t.Item2, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Parses "x,y x,y|x,y ..." (normalised 0..1 to the plan image) into polygons.</summary>
    public static List<List<(double X, double Y)>> Polygons(string text)
    {
        var res = new List<List<(double, double)>>();
        foreach (var poly in (text ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var pts = new List<(double, double)>();
            foreach (var p in poly.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = p.Split(',');
                if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    pts.Add((x, y));
            }
            if (pts.Count >= 3) res.Add(pts);
        }
        return res;
    }

    /// <summary>Polygon centroid (area-weighted; falls back to the vertex mean).</summary>
    public static (double X, double Y) Centroid(IReadOnlyList<(double X, double Y)> p)
    {
        double a = 0, cx = 0, cy = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var (x0, y0) = p[i];
            var (x1, y1) = p[(i + 1) % p.Count];
            var f = x0 * y1 - x1 * y0;
            a += f; cx += (x0 + x1) * f; cy += (y0 + y1) * f;
        }
        if (Math.Abs(a) < 1e-12) return (p.Average(q => q.X), p.Average(q => q.Y));
        return (cx / (3 * a), cy / (3 * a));
    }
}
