using Raffaello.Core.Chain;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Rules;

public sealed record PostResult(double Posted, double Cap, bool IsOver, double Excess);

/// <summary>Exceeded lines are posted at their full quantity, never trimmed to the cap, and flagged OVER.</summary>
public static class PostingRules
{
    public static PostResult Post(double qty, double cap, double eps = 0.0001)
    {
        var over = qty > cap + eps;
        return new PostResult(qty, cap, over, over ? qty - cap : 0);
    }
}

public static class ProgressRules
{
    /// <summary>EMT is rework: it never counts as progress, and neither does any WIR line marked rework.</summary>
    public static bool CountsAsProgress(Wir wir, WirLine line) =>
        !line.IsRework
        && !string.Equals(wir.System, Systems.Emt, StringComparison.OrdinalIgnoreCase)
        && wir.Status == WirStatus.Approved
        && wir.Kind == "WIR";

    /// <summary>Quantity the site statement says is installed (QS x SITE %).</summary>
    public static double SiteQty(double qs, double sitePct) => qs * Math.Clamp(sitePct, 0, 1.5);

    /// <summary>Positive = site ahead of approved WIRs.</summary>
    public static double SiteGap(double sitePct, double wirPct) => sitePct - wirPct;
}

public static class ClaimRules
{
    /// <summary>What may be certified: never above DONE (WIR) and never above the PROJECT QTY cap.</summary>
    public static double CertifiableQty(double claimed, double done, double cap) =>
        Math.Max(0, Math.Min(claimed, Math.Min(done, cap)));

    /// <summary>Invoices are valued on WIR %, not on site %.</summary>
    public static double InvoiceQtyFromWirPct(double qs, double wirPct) => qs * Math.Clamp(wirPct, 0, 1);

    public static bool NeedsHold(double claimed, double done, double eps = 0.0001) => claimed > done + eps;
}

/// <summary>Pipes and conduits are bought in metres and delivered in pieces.</summary>
public static class UnitConverter
{
    public const double DefaultPipeLength = 3.0;   // Mohamed 02-Oct: conduit stick = 3 m

    public static double PcsToM(double pcs, double lengthPerPcs = DefaultPipeLength) =>
        pcs * (lengthPerPcs > 0 ? lengthPerPcs : DefaultPipeLength);

    public static double MToPcs(double metres, double lengthPerPcs = DefaultPipeLength) =>
        metres / (lengthPerPcs > 0 ? lengthPerPcs : DefaultPipeLength);

    public static bool IsPieces(string? unit) => (unit ?? "").Trim().ToUpperInvariant() is "PCS" or "PC" or "PCE" or "LENGTH" or "LENGTHS" or "NOS LENGTH";
    public static bool IsMetres(string? unit) => (unit ?? "").Trim().ToUpperInvariant() is "M" or "LM" or "MTR" or "MTRS" or "METER" or "METRE" or "RM";

    /// <summary>Converts a delivered quantity into the PO unit. Returns the input when no conversion applies.</summary>
    public static double ToPoUnit(double qty, string? fromUnit, string? poUnit, double lengthPerPcs = DefaultPipeLength)
    {
        if (IsPieces(fromUnit) && IsMetres(poUnit)) return PcsToM(qty, lengthPerPcs);
        if (IsMetres(fromUnit) && IsPieces(poUnit)) return MToPcs(qty, lengthPerPcs);
        return qty;
    }
}

public sealed record PoTotalCheck(double LinesTotal, double StatedTotal, double Difference, bool Matches);

public static class MaterialRules
{
    /// <summary>Delivered above the PO quantity raises an OVER alarm.</summary>
    public static bool IsOverPo(double delivered, double poQty, double eps = 0.0001) => delivered > poQty + eps;

    /// <summary>The PO lines (qty x rate) must add up to the stated PO total.</summary>
    public static PoTotalCheck CheckPoTotal(IEnumerable<(double Qty, double Rate)> lines, double statedTotal, double tolerance = 1.0)
    {
        var sum = Math.Round(lines.Sum(l => l.Qty * l.Rate), 2);
        var diff = Math.Round(sum - statedTotal, 2);
        return new PoTotalCheck(sum, statedTotal, diff, Math.Abs(diff) <= tolerance);
    }

    /// <summary>SUMPRODUCT of delivered quantities and PO rates.</summary>
    public static double DeliveredValue(IEnumerable<(double Qty, double Rate)> lines) => lines.Sum(l => l.Qty * l.Rate);
}

/// <summary>How many points a QS block counts for.</summary>
public static class PointRules
{
    /// <summary>
    /// Twin socket = 1 point. Twin data = 2 points at 2ND FIX (1 box at 1ST FIX, 1 faceplate at FINAL FIX).
    /// TV + soundbar = 1 point. Everything else = 1 point per block.
    /// </summary>
    public static int PointsFor(string blockName, string stage)
    {
        var b = (blockName ?? "").ToUpperInvariant().Replace("_", " ").Replace("-", " ");
        if (b.Contains("TWIN DATA") || b.Contains("DOUBLE DATA") || b.Contains("DATA 2") || b.Contains("2 DATA"))
            return stage == Stages.Second ? 2 : 1;
        return 1;
    }
}

/// <summary>Maps CAD layer / block system names to the project systems.</summary>
public static class SystemMap
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CONTROL"] = Systems.Grms, ["GRMS"] = Systems.Grms, ["THERMOSTAT"] = Systems.Grms, ["CP-4"] = Systems.Grms,
        ["EM"] = Systems.Emergency, ["EMERGENCY"] = Systems.Emergency, ["EMERGENCY LIGHT"] = Systems.Emergency,
        ["EV"] = Systems.Evacuation, ["EVACUATION"] = Systems.Evacuation,
        ["LIGHT"] = Systems.Light, ["LIGHTING"] = Systems.Light, ["SWITCH"] = Systems.Light, ["SWITCHES"] = Systems.Light,
        ["POWER"] = Systems.Power, ["SOCKET"] = Systems.Power, ["SOCKETS"] = Systems.Power, ["SMALL POWER"] = Systems.Power,
        ["DATA"] = Systems.Data, ["TEL"] = Systems.Data, ["WAP"] = Systems.Data,
        ["AV"] = Systems.Av, ["TV"] = Systems.Av, ["DISABLED"] = Systems.Disabled, ["EMT"] = Systems.Emt,
    };

    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "";
        if (Map.TryGetValue(s, out var v)) return v;
        foreach (var kv in Map.OrderByDescending(k => k.Key.Length))
            if (s.StartsWith(kv.Key + "-", StringComparison.OrdinalIgnoreCase) || s.StartsWith(kv.Key + " ", StringComparison.OrdinalIgnoreCase) || s.StartsWith(kv.Key + "_", StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return s.ToUpperInvariant();
    }
}

/// <summary>Raised when code tries to add quantities from different stages together.</summary>
public sealed class StageMixException : InvalidOperationException
{
    public StageMixException(IEnumerable<string> stages)
        : base($"Stages are never summed together ({string.Join(" + ", stages)}). Filter to one stage or use totals by stage.") { }
}

public sealed record ChainTotals(string Stage, double Qs, double Cap, double Given, double Done, double Claimed, double Certified, double? Delivered, int Lines)
{
    public double GivenPct => Qs <= 0 ? 0 : Given / Qs;
    public double DonePct => Qs <= 0 ? 0 : Done / Qs;
    public double ClaimedPct => Qs <= 0 ? 0 : Claimed / Qs;
}

/// <summary>Aggregation that respects the stage rule: totals are per stage, never across stages.</summary>
public static class ChainMath
{
    public static IReadOnlyDictionary<string, ChainTotals> TotalsByStage(IEnumerable<ChainRow> rows) =>
        rows.GroupBy(r => r.Stage)
            .OrderBy(g => Array.IndexOf(Stages.All, g.Key))
            .ToDictionary(g => g.Key, g => Sum(g.Key, g.ToList()));

    /// <summary>Totals for rows that all belong to one stage. Throws <see cref="StageMixException"/> otherwise.</summary>
    public static ChainTotals SingleStageTotal(IEnumerable<ChainRow> rows)
    {
        var list = rows.ToList();
        var stages = list.Select(r => r.Stage).Distinct().ToList();
        if (stages.Count > 1) throw new StageMixException(stages);
        return Sum(stages.FirstOrDefault() ?? "", list);
    }

    /// <summary>Mean of per-stage ratios: the only valid "all stages" progress figure.</summary>
    public static double MeanStagePct(IEnumerable<ChainRow> rows, Func<ChainTotals, double> pct)
    {
        var t = TotalsByStage(rows);
        return t.Count == 0 ? 0 : t.Values.Average(pct);
    }

    private static ChainTotals Sum(string stage, List<ChainRow> g)
    {
        var tracked = g.Where(r => r.Delivered.HasValue).ToList();
        return new ChainTotals(stage, g.Sum(r => r.Qs), g.Sum(r => r.Cap), g.Sum(r => r.Given), g.Sum(r => r.Done),
            g.Sum(r => r.Claimed), g.Sum(r => r.Certified), tracked.Count == 0 ? null : tracked.Sum(r => r.Delivered!.Value), g.Count);
    }
}
