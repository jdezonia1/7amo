using Raffaello.Core.Domain;

namespace Raffaello.Core.Insights;

// =====================================================================================================
//  INSIGHTS - claim anomalies, material reconciliation, rate benchmarking, cash-flow forecast, earned value.
//  Insights are WARNINGS / INFO with an explanation and evidence; they never block anything. Each anomaly can be
//  dismissed with a reason (recorded). The editable inputs (thresholds, consumption norms, programme, payment terms,
//  invoice period dates) and the file-hash cache live in the Insight* tables (IInsightsStore).
// =====================================================================================================

/// <summary>A dismissed anomaly (by its stable fingerprint) with the reason. Restoring sets Active = false (the row stays as a record).</summary>
public sealed class InsightDismissal : Entity
{
    public string Fingerprint { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string DismissedBy { get; set; } = "";
    public DateTime DismissedAt { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>An editable detection threshold (key -> value). Missing keys use <see cref="InsightThresholds.Defaults"/>.</summary>
public sealed class InsightThreshold : Entity
{
    public string Key { get; set; } = "";
    public double Value { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Consumption norm: how much of a material one installed point uses (e.g. 12 m of 3x2.5 cable per POWER point at 2ND FIX).
/// Material is matched against DN / PO descriptions: "CABLE|3X2.5" (cable fingerprint prefix) or words that must all appear
/// ("CONDUIT 20MM PVC").
/// </summary>
public sealed class InsightNorm : Entity
{
    public string Material { get; set; } = "";
    public string Unit { get; set; } = "M";
    /// <summary>Ledger item / system (POWER, LIGHT, DATA ...).</summary>
    public string System { get; set; } = "";
    /// <summary>Ledger stage (1ST FIX, 2ND FIX, CEILING ...); empty = every stage.</summary>
    public string Stage { get; set; } = "";
    /// <summary>Room type (1BR-A, 2BR, Public ...) or area type (APARTMENT / BOH / FOH); empty = any.</summary>
    public string RoomType { get; set; } = "";
    public double PerPoint { get; set; }
    /// <summary>Allowance for normal wastage (0.05 = 5 %) used for the over-delivery check.</summary>
    public double WastageAllowance { get; set; } = 0.05;
    /// <summary>DEFAULT / MANUAL / LEARNED.</summary>
    public string Source { get; set; } = "MANUAL";
    public string Note { get; set; } = "";
}

/// <summary>Simple programme: planned start / finish per area (level, plot or zone) x stage (x system).</summary>
public sealed class InsightProgramme : Entity
{
    public string Building { get; set; } = "";
    /// <summary>Area as the ledger knows it: floor ("Level 01"), plot ("P2") or "ALL".</summary>
    public string Area { get; set; } = "ALL";
    public string Stage { get; set; } = "";
    /// <summary>Empty = every system.</summary>
    public string System { get; set; } = "";
    public DateTime PlannedStart { get; set; }
    public DateTime PlannedFinish { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Payment terms per contract / PO / owner (used by the cash-flow forecast).</summary>
public sealed class InsightPaymentTerm : Entity
{
    /// <summary>SUBCONTRACT / SUPPLIER / OWNER.</summary>
    public string Party { get; set; } = PaymentParties.Subcontract;
    /// <summary>Contract no., PO no. or "OWNER"; "*" = default for the party.</summary>
    public string Ref { get; set; } = "*";
    public string Name { get; set; } = "";
    /// <summary>90/10, 70/20/10, 100, LC (100 % before delivery by LC).</summary>
    public string Scheme { get; set; } = PaymentSchemes.S90_10;
    public double RetentionPct { get; set; } = 0.10;
    public double AdvancePct { get; set; }
    /// <summary>Days from the end of the invoiced month to payment.</summary>
    public int PayDays { get; set; } = 30;
    /// <summary>LC: payment this many days before delivery.</summary>
    public int LcDaysBeforeDelivery { get; set; } = 30;
    /// <summary>Months after the planned finish when retention / handover parts are released.</summary>
    public int HandoverMonths { get; set; } = 2;
}

/// <summary>Date of a subcontractor invoice period (the tracker ledger carries no dates): end of the period covered by INV n.</summary>
public sealed class InsightInvoicePeriod : Entity
{
    public string Subcontractor { get; set; } = "";
    public int InvoiceNo { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Cache of file hashes (SHA-256 + perceptual hash) so documents are hashed once.</summary>
public sealed class InsightFileHash : Entity
{
    public string FilePath { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>64-bit difference hash as 16 hex chars; empty when the file is not an image this build can decode.</summary>
    public string PHash { get; set; } = "";
    public DateTime HashedAt { get; set; }
}

public static class PaymentParties
{
    public const string Subcontract = "SUBCONTRACT", Supplier = "SUPPLIER", Owner = "OWNER";
}

public static class PaymentSchemes
{
    public const string S90_10 = "90/10", S70_20_10 = "70/20/10", S100 = "100", Lc = "LC";
    public static readonly string[] All = { S90_10, S70_20_10, S100, Lc };
}

public static class InsightsEntities
{
    public static readonly Type[] All =
    {
        typeof(InsightDismissal), typeof(InsightThreshold), typeof(InsightNorm), typeof(InsightProgramme), typeof(InsightPaymentTerm),
        typeof(InsightInvoicePeriod), typeof(InsightFileHash),
    };
}

/// <summary>Detection thresholds with defaults (every one editable on the Anomalies page).</summary>
public static class InsightThresholds
{
    public const string RobustZ = "ROBUST_Z";
    public const string MinRatio = "MIN_RATIO";
    public const string MinExcess = "MIN_EXCESS";
    public const string JumpRatio = "JUMP_RATIO";
    public const string LengthRatioHigh = "LENGTH_RATIO_HIGH";
    public const string HighShareApartment = "HIGH_SHARE_APARTMENT";
    public const string CopySimilarity = "COPY_SIMILARITY";
    public const string RateTolerance = "RATE_TOLERANCE";
    public const string PhashDistance = "PHASH_DISTANCE";
    public const string BenfordMinN = "BENFORD_MIN_N";
    public const string RoundShare = "ROUND_SHARE";
    public const string EarlyWarningSpi = "EARLY_WARNING_SPI";
    public const string OwnerMarkup = "OWNER_MARKUP";
    public const string MosPct = "MOS_PCT";

    public static readonly IReadOnlyDictionary<string, (double Value, string Note)> Defaults = new Dictionary<string, (double, string)>
    {
        [RobustZ] = (3.5, "robust z-score (median / MAD) above which a quantity is unusual"),
        [MinRatio] = (1.5, "a flagged quantity must also be at least this multiple of the typical value"),
        [MinExcess] = (5, "and at least this many points above the typical value"),
        [JumpRatio] = (2.5, "invoice total vs the subcontractor's previous invoices (median) - jump when above this multiple"),
        [LengthRatioHigh] = (2.0, "15 m claims: claimed / plan quantity above this ratio is high severity"),
        [HighShareApartment] = (0.0, ">4.5 m share accepted without question in apartments (ceilings < 3 m) - 0 = any is unusual"),
        [CopySimilarity] = (0.9, "share of identical lines (room x stage x item x qty) for a copied invoice"),
        [RateTolerance] = (0.005, "invoice rate vs contract rate tolerance (0.005 = 0.5 %)"),
        [PhashDistance] = (6, "perceptual-hash bits that may differ for two photos to count as the same photo"),
        [BenfordMinN] = (60, "minimum number of lines before the first-digit (Benford) test is run"),
        [RoundShare] = (0.6, "share of quantities that are multiples of 10 above which it is reported (info)"),
        [EarlyWarningSpi] = (0.85, "schedule performance index below which an area / stage is an early warning"),
        [OwnerMarkup] = (0.15, "cash flow: owner value = cost x (1 + markup) when the owner BOQ has no rates"),
        [MosPct] = (0.75, "cash flow: owner pays this share of delivered material value as MOS"),
    };

    public static double Get(IEnumerable<InsightThreshold> rows, string key)
    {
        var r = rows.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        return r?.Value ?? (Defaults.TryGetValue(key, out var d) ? d.Value : 0);
    }
}
