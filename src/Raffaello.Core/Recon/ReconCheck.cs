using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Recon;

/// <summary>One room x stage x item of the recon: 100% total, claimed (non-rework, effective lines) and remaining.</summary>
public sealed record ReconRow(string Room, string Stage, string Item, double Total, double Claimed, double Remaining)
{
    public bool IsOver => Remaining < -LedgerRules.Eps;
    public bool NoTotal => Total <= LedgerRules.Eps && Claimed > LedgerRules.Eps;
}

/// <summary>Totals by stage|item (summed over rooms; stages are never added together).</summary>
public sealed record ReconKeyTotal(string Stage, string Item, double Total, double Claimed, double Remaining, int OverRooms);

public sealed class ReconCheckResult
{
    public List<ReconRow> Rows { get; init; } = new();
    public List<ReconKeyTotal> ByStageItem { get; init; } = new();
    public double ProjectTotal { get; init; }
    /// <summary>Sum of the non-rework claim lines that count (cumulative supersession applied), computed independently of the balances.</summary>
    public double ClaimedTotal { get; init; }
    public double ReworkTotal { get; init; }
    public double RemainingTotal { get; init; }
    /// <summary>sum(project) - sum(non-rework claims) - sum(remaining); 0 when the ledger closes.</summary>
    public double ClosingDifference => ProjectTotal - ClaimedTotal - RemainingTotal;
    public bool Closes => Math.Abs(ClosingDifference) < 1e-6 * Math.Max(1, Math.Abs(ProjectTotal));
    public int OverKeys => Rows.Count(r => r.IsOver);
    public double OverQty => Rows.Where(r => r.IsOver).Sum(r => -r.Remaining);
    public int ClaimKeysWithoutTotal => Rows.Count(r => r.NoTotal);

    public string Summary =>
        $"total {ProjectTotal:N2}, claimed {ClaimedTotal:N2} (rework {ReworkTotal:N2} not counted), remaining {RemainingTotal:N2}; " +
        $"closing difference {ClosingDifference:N6} ({(Closes ? "OK" : "DOES NOT CLOSE")}); {OverKeys} keys over-claimed by {OverQty:N2}, " +
        $"{ClaimKeysWithoutTotal} claimed keys with no total";
}

/// <summary>
/// Pure check of the recon: per room x stage x item total / claimed / remaining from <see cref="LedgerRules.Balances"/>, and the closing
/// check sum(project) - sum(non-rework claims) == sum(remaining). Over-claims show as negative remaining.
/// </summary>
public static class ReconCheck
{
    public static ReconCheckResult Run(IEnumerable<RoomQty> project, IEnumerable<ClaimLine> claims)
    {
        var p = project.ToList();
        var effective = LedgerRules.Effective(claims.ToList()).ToList();
        var balances = LedgerRules.Balances(p, effective);
        var rows = balances.Values
            .Select(b => new ReconRow(b.Room, b.Stage, b.Item, b.ProjectQty, b.Claimed, b.Remaining))
            .OrderBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Stage).ThenBy(r => r.Item).ToList();
        var byKey = rows.GroupBy(r => (r.Stage, r.Item))
            .Select(g => new ReconKeyTotal(g.Key.Stage, g.Key.Item, g.Sum(r => r.Total), g.Sum(r => r.Claimed), g.Sum(r => r.Remaining), g.Count(r => r.IsOver)))
            .OrderBy(k => k.Stage).ThenBy(k => k.Item).ToList();
        return new ReconCheckResult
        {
            Rows = rows,
            ByStageItem = byKey,
            ProjectTotal = p.Sum(q => q.Qty),
            ClaimedTotal = effective.Where(c => !c.Rework).Sum(c => c.Qty),
            ReworkTotal = effective.Where(c => c.Rework).Sum(c => c.Qty),
            RemainingTotal = rows.Sum(r => r.Remaining),
        };
    }

    public static ReconCheckResult Run(HotelReconResult r) => Run(r.Quantities, r.Claims);
}