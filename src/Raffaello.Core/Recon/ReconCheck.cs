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

/// <summary>A point quantity (unit "no") that is not a whole number - in PROJECT QTY or in a claim line.</summary>
public sealed record FractionalPoint(string Where, string Room, string Stage, string Item, double Qty, string Who)
{
    public override string ToString() => $"{Where} {Room} {Stage}|{Item} {Qty:0.###}{(Who.Length > 0 ? " (" + Who + ")" : "")}";
}

public sealed class ReconCheckResult
{
    public List<ReconRow> Rows { get; init; } = new();
    public List<ReconKeyTotal> ByStageItem { get; init; } = new();
    public double ProjectTotal { get; init; }
    /// <summary>Sum of the non-rework, compared claim lines that count (cumulative supersession applied), computed independently of the balances.</summary>
    public double ClaimedTotal { get; init; }
    public double ReworkTotal { get; init; }
    /// <summary>NOT COMPARED lines (cable pulling / cable tray): kept in the ledger, never against a total.</summary>
    public double NotComparedTotal { get; init; }
    public int NotComparedLines { get; init; }
    /// <summary>2nd-fix extra points kept for the 15 m route-length check (LengthClaimedQty - Qty); not against the room totals.</summary>
    public double LengthExtraTotal { get; init; }
    public int LengthPendingLines { get; init; }
    public double RemainingTotal { get; init; }
    /// <summary>Point quantities (unit "no") that are not whole numbers. Metres may be fractional.</summary>
    public List<FractionalPoint> FractionalPoints { get; init; } = new();
    /// <summary>sum(project) - sum(non-rework compared claims) - sum(remaining); 0 when the ledger closes.</summary>
    public double ClosingDifference => ProjectTotal - ClaimedTotal - RemainingTotal;
    public bool Closes => Math.Abs(ClosingDifference) < 1e-6 * Math.Max(1, Math.Abs(ProjectTotal));
    public int OverKeys => Rows.Count(r => r.IsOver);
    public double OverQty => Rows.Where(r => r.IsOver).Sum(r => -r.Remaining);
    public int ClaimKeysWithoutTotal => Rows.Count(r => r.NoTotal);

    /// <summary>Warnings to show before an import (fractional point quantities ...).</summary>
    public List<string> Warnings
    {
        get
        {
            var w = new List<string>();
            if (FractionalPoints.Count > 0)
                w.Add($"WARNING: {FractionalPoints.Count} point quantities (unit no) are not whole numbers - points must be whole: " +
                      string.Join("; ", FractionalPoints.Take(10)) + (FractionalPoints.Count > 10 ? $"; ... {FractionalPoints.Count - 10} more" : ""));
            return w;
        }
    }

    public string Summary =>
        $"total {ProjectTotal:N2}, claimed {ClaimedTotal:N2} (rework {ReworkTotal:N2} not counted), remaining {RemainingTotal:N2}; " +
        $"15 m rule extras {LengthExtraTotal:N2} on {LengthPendingLines} lines (length check pending, not against the totals); " +
        $"not compared {NotComparedLines} lines {NotComparedTotal:N2} (cable pulling / cable tray, not against the totals); " +
        $"closing difference {ClosingDifference:N6} ({(Closes ? "OK" : "DOES NOT CLOSE")}); {OverKeys} keys over-claimed by {OverQty:N2}, " +
        $"{ClaimKeysWithoutTotal} claimed keys with no total; {FractionalPoints.Count} fractional point quantities";
}

/// <summary>
/// Pure check of the recon: per room x stage x item total / claimed / remaining from <see cref="LedgerRules.Balances"/>, and the closing
/// check sum(project) - sum(non-rework compared claims) == sum(remaining). Over-claims show as negative remaining. NOT COMPARED lines
/// (cable pulling / cable tray) are counted apart and never against a total. Point quantities (unit "no") must be whole numbers:
/// every fractional one is listed in <see cref="ReconCheckResult.FractionalPoints"/>.
/// </summary>
public static class ReconCheck
{
    public static bool IsPointUnit(string? unit) => string.IsNullOrWhiteSpace(unit) || unit.Trim().Equals("no", StringComparison.OrdinalIgnoreCase)
                                                    || unit.Trim().Equals("nr", StringComparison.OrdinalIgnoreCase) || unit.Trim().Equals("pcs", StringComparison.OrdinalIgnoreCase);

    public static bool IsWhole(double q) => Math.Abs(q - Math.Round(q)) < 1e-6;

    public static ReconCheckResult Run(IEnumerable<RoomQty> project, IEnumerable<ClaimLine> claims)
    {
        var p = project.ToList();
        var all = claims.ToList();
        var effective = LedgerRules.Effective(all).ToList();
        var balances = LedgerRules.Balances(p, effective);
        var rows = balances.Values
            .Select(b => new ReconRow(b.Room, b.Stage, b.Item, b.ProjectQty, b.Claimed, b.Remaining))
            .OrderBy(r => r.Room, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Stage).ThenBy(r => r.Item).ToList();
        var byKey = rows.GroupBy(r => (r.Stage, r.Item))
            .Select(g => new ReconKeyTotal(g.Key.Stage, g.Key.Item, g.Sum(r => r.Total), g.Sum(r => r.Claimed), g.Sum(r => r.Remaining), g.Count(r => r.IsOver)))
            .OrderBy(k => k.Stage).ThenBy(k => k.Item).ToList();
        var compared = effective.Where(c => !LedgerRules.IsNotCompared(c)).ToList();
        var fractional = p.Where(q => IsPointUnit(q.Unit) && !IsWhole(q.Qty))
            .Select(q => new FractionalPoint("PROJECT QTY", q.Room, q.Stage, q.Item, q.Qty, ""))
            .Concat(all.Where(c => IsPointUnit(c.Unit) && !IsWhole(c.Qty))
                .Select(c => new FractionalPoint("CLAIM", c.Room, c.Stage, c.Item, c.Qty, $"{c.Subcontractor} INV {c.InvoiceNo}")))
            .ToList();
        return new ReconCheckResult
        {
            Rows = rows,
            ByStageItem = byKey,
            ProjectTotal = p.Sum(q => q.Qty),
            ClaimedTotal = compared.Where(c => !c.Rework).Sum(c => c.Qty),
            ReworkTotal = compared.Where(c => c.Rework).Sum(c => c.Qty),
            NotComparedTotal = effective.Where(LedgerRules.IsNotCompared).Sum(c => c.Qty),
            NotComparedLines = effective.Count(LedgerRules.IsNotCompared),
            LengthExtraTotal = compared.Where(c => !c.Rework && c.LengthApplies).Sum(c => c.LengthClaimedQty - c.Qty),
            LengthPendingLines = compared.Count(c => !c.Rework && c.LengthApplies && c.LengthStatus == CheckStatus.Pending),
            RemainingTotal = rows.Sum(r => r.Remaining),
            FractionalPoints = fractional,
        };
    }

    public static ReconCheckResult Run(ReconImportResult r) => Run(r.Quantities, r.Claims);
}