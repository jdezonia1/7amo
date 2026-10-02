using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Insights;

/// <summary>How much an insight matters. Insights never block: HIGH = look before certifying, INFO = for the record.</summary>
public enum InsightSeverity { Info = 0, Low = 1, Medium = 2, High = 3 }

public static class AnomalyKinds
{
    public const string InvoiceJump = "INVOICE JUMP";
    public const string KeyOutlier = "QTY OUTLIER";
    public const string KeyRepeat = "RE-CLAIMED";
    public const string MultiSub = "SHARED KEY";
    public const string Length = "15 M LENGTH";
    public const string Height = ">4.5 M HEIGHT";
    public const string NoWir = "NO WIR";
    public const string BeforeWir = "BEFORE WIR";
    public const string WirNotApproved = "WIR NOT APPROVED";
    public const string WirUnknown = "WIR NOT FOUND";
    public const string DuplicateFile = "DUPLICATE FILE";
    public const string DuplicatePhoto = "REUSED PHOTO";
    public const string CopiedInvoice = "COPIED INVOICE";
    public const string Cumulative = "CUMULATIVE";
    public const string RoundNumbers = "ROUND NUMBERS";
    public const string Benford = "FIRST DIGITS";
    public const string Rate = "RATE";
    public const string Material = "MATERIAL";

    public static readonly string[] All =
    {
        InvoiceJump, KeyOutlier, KeyRepeat, MultiSub, Length, Height, NoWir, BeforeWir, WirNotApproved, WirUnknown, DuplicateFile, DuplicatePhoto,
        CopiedInvoice, Cumulative, RoundNumbers, Benford, Rate, Material,
    };
}

public static class EvidenceKinds
{
    public const string Ledger = "LEDGER", Invoice = "INVOICE", InvoiceLine = "INVOICE LINE", Statement = "STATEMENT", Wir = "WIR", Document = "DOCUMENT",
        ContractItem = "CONTRACT ITEM", Room = "ROOM", Dn = "DN", Po = "PO";
}

/// <summary>A pointer to the record behind an insight (ledger line, invoice, WIR, document ...). Path is set for files.</summary>
public sealed record Evidence(string Kind, long Id, string Label, string Path = "", string NavKey = "");

/// <summary>One unusual thing, explained in plain English, with the records that show it and what to do about it.</summary>
public sealed class Anomaly
{
    /// <summary>Stable identity (kind + subject) - a dismissal sticks to it across reloads.</summary>
    public required string Fingerprint { get; init; }
    public required string Kind { get; init; }
    public InsightSeverity Severity { get; init; }
    public required string Title { get; init; }
    public string Explanation { get; init; } = "";
    public string SuggestedAction { get; init; } = "";
    public string Subcontractor { get; init; } = "";
    public int InvoiceNo { get; init; }
    public string Room { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Item { get; init; } = "";
    public string Building { get; init; } = "";
    /// <summary>Sort weight within a severity (points / SAR at stake).</summary>
    public double Score { get; init; }
    public List<Evidence> Evidence { get; init; } = new();
    public InsightDismissal? Dismissal { get; set; }

    public bool IsDismissed => Dismissal is not null;
    public string SeverityText => Severity.ToString().ToUpperInvariant();
    public string Tag => Severity switch { InsightSeverity.High => "HIGH", InsightSeverity.Medium => "MEDIUM", InsightSeverity.Low => "LOW", _ => "INFO" };
    public string EvidenceText => string.Join("; ", Evidence.Take(12).Select(e => e.Label)) + (Evidence.Count > 12 ? $" ... (+{Evidence.Count - 12})" : "");
    public string InvoiceText => InvoiceNo > 0 ? $"INV-{InvoiceNo:00}" : "";
}

/// <summary>Everything the anomaly detector reads.</summary>
public sealed class AnomalyInputs
{
    public required ProjectSnapshot Project { get; init; }
    public InsightsData Data { get; init; } = new();
    public Materials.MaterialsSnapshot? Materials { get; init; }
    /// <summary>Documents / photos referenced by the data and their hashes (from <see cref="DocumentHashes.Hash"/>).</summary>
    public List<DocumentUse> Documents { get; init; } = new();
    public List<InsightFileHash> Hashes { get; init; } = new();
    public string? Building { get; init; }
    public DateTime Today { get; init; } = DateTime.Today;

    public bool In(string? building) => Building is null || string.IsNullOrEmpty(building) || string.Equals(building, Building, StringComparison.OrdinalIgnoreCase);
}
