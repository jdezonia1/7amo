using System.Globalization;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Ledger;

public sealed record RoomBalance(string Room, string Stage, string Item, double ProjectQty, double Claimed, IReadOnlyDictionary<string, double> BySubcontractor)
{
    public double Remaining => ProjectQty - Claimed;
    public bool HasCap => ProjectQty > 0;
    public bool IsOver => Claimed > ProjectQty + 1e-9;
    public double UsedPct => ProjectQty <= 0 ? 0 : Claimed / ProjectQty;
}

public enum ClaimDecision { Accepted, AcceptedOver, Blocked }

public sealed record ClaimCheck(ClaimDecision Decision, double Remaining, double ProjectQty, string Message)
{
    public bool CanPost => Decision != ClaimDecision.Blocked;
    public bool IsOver => Decision == ClaimDecision.AcceptedOver;
}

/// <summary>
/// The room ledger: remaining = PROJECT QTY - sum of every subcontractor's claims for the same room x stage x item.
/// Stages are separate keys and are never summed. Claims above the remaining quantity are blocked unless a reason is given;
/// with a reason they post at full quantity and carry an OVER tag. Plan quantity (QTY), not the length-revised quantity, counts.
/// </summary>
public static class LedgerRules
{
    public const double Eps = 1e-9;

    public static Dictionary<string, RoomBalance> Balances(IEnumerable<RoomQty> project, IEnumerable<ClaimLine> claims)
    {
        var caps = project.GroupBy(q => q.Key).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        var byKey = claims.Where(c => !c.Rework).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.ToList());
        var keys = caps.Keys.Union(byKey.Keys);
        var res = new Dictionary<string, RoomBalance>();
        foreach (var k in keys)
        {
            var parts = k.Split('|');
            var list = byKey.GetValueOrDefault(k) ?? new List<ClaimLine>();
            res[k] = new RoomBalance(parts[0], parts[1], parts[2], caps.GetValueOrDefault(k), list.Sum(c => c.Qty),
                list.GroupBy(c => c.Subcontractor).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty)));
        }
        return res;
    }

    public static RoomBalance Balance(IEnumerable<RoomQty> project, IEnumerable<ClaimLine> claims, string room, string stage, string item)
    {
        var key = LedgerKeys.Key(room, stage, item);
        var cap = project.Where(q => q.Key == key).Sum(q => q.Qty);
        var list = claims.Where(c => c.Key == key && !c.Rework).ToList();
        return new RoomBalance(room, stage, item, cap, list.Sum(c => c.Qty), list.GroupBy(c => c.Subcontractor).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty)));
    }

    /// <summary>Decides whether a new claim of <paramref name="qty"/> may be posted against the balance.</summary>
    public static ClaimCheck Check(RoomBalance balance, double qty, string? overReason)
    {
        if (qty <= balance.Remaining + Eps)
            return new(ClaimDecision.Accepted, balance.Remaining, balance.ProjectQty,
                balance.HasCap ? $"OK - remaining after this claim {balance.Remaining - qty:N2}" : "OK");
        var excess = qty - Math.Max(0, balance.Remaining);
        var msg = balance.HasCap
            ? $"Claim {qty:N2} exceeds remaining {balance.Remaining:N2} of PROJECT QTY {balance.ProjectQty:N2} by {excess:N2}"
            : $"No PROJECT QTY for {balance.Room} {balance.Stage} {balance.Item}";
        return string.IsNullOrWhiteSpace(overReason)
            ? new(ClaimDecision.Blocked, balance.Remaining, balance.ProjectQty, msg + " - give a reason to post it (OVER).")
            : new(ClaimDecision.AcceptedOver, balance.Remaining, balance.ProjectQty, msg + " - posted at full qty, OVER: " + overReason.Trim());
    }

    /// <summary>A correction is a new line with the opposite quantity (the ledger is append-only).</summary>
    public static ClaimLine Reversal(ClaimLine original, string reason)
    {
        var r = Copy(original);
        r.Qty = -original.Qty;
        r.QtyAbove45 = -original.QtyAbove45;
        r.QtyAbove45Accepted = -original.QtyAbove45Accepted;
        r.LengthClaimedQty = -original.LengthClaimedQty;
        r.LengthRevisedQty = -original.LengthRevisedQty;
        r.LengthRevisedOverride = original.LengthRevisedOverride is double o ? -o : null;
        r.Notes = $"REVERSAL of line #{original.Id}: {reason}";
        r.Source = "REVERSAL";
        r.SourceKey = $"REV|{original.Id}";
        r.IsOver = false;
        r.OverReason = "";
        return r;
    }

    public static ClaimLine Copy(ClaimLine c) => new()
    {
        Building = c.Building, Subcontractor = c.Subcontractor, InvoiceNo = c.InvoiceNo, Stage = c.Stage, Floor = c.Floor, Room = c.Room, Item = c.Item, Unit = c.Unit,
        Qty = c.Qty, SitePct = c.SitePct, WirPct = c.WirPct, WirNo = c.WirNo, Notes = c.Notes, Rework = c.Rework, WorkType = c.WorkType, AreaType = c.AreaType,
        Source = c.Source, StatementNo = c.StatementNo, EnteredAt = DateTime.Now,
        QtyAbove45 = c.QtyAbove45, HeightStatus = c.HeightStatus, QtyAbove45Accepted = c.QtyAbove45Accepted, HeightCheckedBy = c.HeightCheckedBy, HeightCheckDate = c.HeightCheckDate, HeightNote = c.HeightNote,
        LengthApplies = c.LengthApplies, LengthClaimedQty = c.LengthClaimedQty, RouteLengthTotal = c.RouteLengthTotal, LengthGroups = c.LengthGroups, LengthRevisedQty = c.LengthRevisedQty,
        LengthRevisedOverride = c.LengthRevisedOverride, LengthStatus = c.LengthStatus, LengthNote = c.LengthNote, LengthAttachment = c.LengthAttachment,
    };
}

/// <summary>
/// Height above 4.5 m: only the accepted quantity is priced at the &gt; 4.5 m item, the rest at the normal item.
/// Lines with a pending height check are held out of the invoice.
/// </summary>
public static class HeightCheck
{
    public static bool IsPending(ClaimLine c) => c.QtyAbove45 > LedgerRules.Eps && (c.HeightStatus is "" or CheckStatus.Pending);

    /// <summary>Accepted quantity above 4.5 m (0 when rejected, clamped to the claimed quantity).</summary>
    public static double AcceptedHigh(ClaimLine c) => c.HeightStatus switch
    {
        CheckStatus.Accepted => Math.Min(c.QtyAbove45, Math.Abs(c.Qty)) * Math.Sign(c.Qty == 0 ? 1 : c.Qty),
        CheckStatus.Partly => Math.Clamp(c.QtyAbove45Accepted, 0, Math.Min(c.QtyAbove45, Math.Abs(c.Qty))) * Math.Sign(c.Qty == 0 ? 1 : c.Qty),
        _ => 0,
    };

    /// <summary>Applies a check decision: ACCEPTED (all), PARTLY (qty), REJECTED (none).</summary>
    public static void Decide(ClaimLine c, string status, double? acceptedQty, string by, string note, DateTime? at = null)
    {
        c.HeightStatus = status;
        c.QtyAbove45Accepted = status switch
        {
            CheckStatus.Accepted => c.QtyAbove45,
            CheckStatus.Partly => Math.Clamp(acceptedQty ?? 0, 0, c.QtyAbove45),
            _ => 0,
        };
        if (status == CheckStatus.Partly && c.QtyAbove45Accepted >= c.QtyAbove45 - LedgerRules.Eps) c.HeightStatus = CheckStatus.Accepted;
        c.HeightCheckedBy = by;
        c.HeightCheckDate = at ?? DateTime.Now;
        c.HeightNote = note;
    }
}

/// <summary>
/// 15 m route-length rule on 2nd-fix pulling items: a point longer than 15 m counts proportionally with a minimum of one,
/// qty per point = max(1, L / 15). Only the revised quantity is invoiced; caps use the plan quantity.
/// </summary>
public static class LengthCheck
{
    public const double PointLength = 15.0;

    public static double PerPoint(double length, int decimals = 1) => Math.Round(Math.Max(1.0, length / PointLength), decimals, MidpointRounding.AwayFromZero);

    /// <summary>Quick mode: total route length for all points. Exact only when every run is at least 15 m.</summary>
    public static double FromTotal(double planQty, double totalLength, int decimals = 1) =>
        Math.Round(Math.Max(planQty, totalLength / PointLength), decimals, MidpointRounding.AwayFromZero);

    /// <summary>Group mode: "n x length" groups, e.g. "10x20;5x35" = 10 points of 20 m + 5 of 35 m.</summary>
    public static double FromGroups(string groups, int decimals = 1)
    {
        double total = 0;
        foreach (var (n, len) in ParseGroups(groups)) total += n * PerPoint(len, decimals);
        return Math.Round(total, decimals, MidpointRounding.AwayFromZero);
    }

    public static IEnumerable<(double Count, double Length)> ParseGroups(string groups)
    {
        foreach (var part in (groups ?? "").Split(new[] { ';', ',', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim().ToLowerInvariant().Replace("×", "x").Replace("*", "x").Replace("m", "");
            var xy = p.Split('x', StringSplitOptions.TrimEntries);
            if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
                yield return (n, l);
            else if (xy.Length == 1 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var single))
                yield return (1, single);
        }
    }

    /// <summary>Recalculates the revised quantity from groups (preferred) or total length.</summary>
    public static double Recalculate(ClaimLine c, int decimals = 1)
    {
        var plan = Math.Abs(c.Qty);
        double revised;
        if (!string.IsNullOrWhiteSpace(c.LengthGroups)) revised = FromGroups(c.LengthGroups, decimals);
        else if (c.RouteLengthTotal > 0) revised = FromTotal(plan, c.RouteLengthTotal, decimals);
        else revised = plan;
        c.LengthRevisedQty = revised * Math.Sign(c.Qty == 0 ? 1 : c.Qty);
        c.LengthNote = string.IsNullOrWhiteSpace(c.LengthNote) || c.LengthNote.StartsWith("plan qty")
            ? $"plan qty {plan:0.#}, claimed {c.LengthClaimedQty:0.#}, " +
              (!string.IsNullOrWhiteSpace(c.LengthGroups) ? $"groups {c.LengthGroups}" : $"total length {c.RouteLengthTotal:0.#} m") + $" -> revised {revised:0.#}"
            : c.LengthNote;
        return c.LengthRevisedQty;
    }

    public static bool IsPending(ClaimLine c) => c.LengthApplies && (c.LengthStatus is "" or CheckStatus.Pending)
                                                  && Math.Abs(c.LengthClaimedQty) > Math.Abs(c.Qty) + LedgerRules.Eps;

    /// <summary>Quantity to invoice for the line before SITE % / WIR %: revised (or override) when checked, plan qty otherwise.</summary>
    public static double InvoiceBaseQty(ClaimLine c)
    {
        if (!c.LengthApplies || Math.Abs(c.LengthClaimedQty) <= Math.Abs(c.Qty) + LedgerRules.Eps) return c.Qty;
        return c.LengthStatus switch
        {
            CheckStatus.Accepted => c.LengthClaimedQty,
            CheckStatus.Revised => c.LengthRevisedOverride ?? c.LengthRevisedQty,
            _ => c.Qty,
        };
    }

    public static double RejectedExtra(ClaimLine c) =>
        c.LengthApplies && Math.Abs(c.LengthClaimedQty) > Math.Abs(c.Qty) ? c.LengthClaimedQty - InvoiceBaseQty(c) : 0;
}

/// <summary>What a claim line contributes to an invoice after the checks.</summary>
public sealed record InvoiceableQty(ClaimLine Line, bool Held, string HeldReason, double LowQty, double HighQty)
{
    public double Total => LowQty + HighQty;
}

public static class Invoiceable
{
    /// <summary>
    /// Invoice quantity = base qty (length-revised where checked) x SITE % x WIR %. Accepted &gt; 4.5 m quantity goes to the high item.
    /// Pending height or length checks hold the whole line out of the invoice.
    /// </summary>
    public static InvoiceableQty Of(ClaimLine c)
    {
        if (HeightCheck.IsPending(c)) return new(c, true, "height > 4.5 m check pending", 0, 0);
        if (LengthCheck.IsPending(c)) return new(c, true, "15 m length check pending", 0, 0);
        var factor = c.SitePct * c.WirPct;
        var baseQty = LengthCheck.InvoiceBaseQty(c);
        var high = Math.Abs(c.Qty) < LedgerRules.Eps ? 0 : HeightCheck.AcceptedHigh(c) * (baseQty / c.Qty);
        var low = baseQty - high;
        return new(c, false, "", low * factor, high * factor);
    }
}
