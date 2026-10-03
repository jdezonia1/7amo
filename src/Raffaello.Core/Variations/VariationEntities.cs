using Raffaello.Core.Domain;

namespace Raffaello.Core.Variations;

public static class VariationTypes
{
    /// <summary>Variation order.</summary>
    public const string Vo = "VO";
    /// <summary>Engineer's instruction.</summary>
    public const string Ei = "EI";
    /// <summary>Site instruction.</summary>
    public const string Si = "SI";
    public static readonly string[] All = { Vo, Ei, Si };
}

public static class VariationStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string UnderReview = "UNDER REVIEW";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Withdrawn = "WITHDRAWN";
    public static readonly string[] All = { Draft, Submitted, UnderReview, Approved, Rejected, Withdrawn };

    public static bool IsClosed(string s) => s is Approved or Rejected or Withdrawn;

    /// <summary>Allowed moves. A rejected variation can be revised (back to DRAFT) and resubmitted.</summary>
    public static IReadOnlyList<string> Next(string from) => from switch
    {
        Draft => new[] { Submitted, Withdrawn },
        Submitted => new[] { UnderReview, Approved, Rejected, Withdrawn, Draft },
        UnderReview => new[] { Approved, Rejected, Withdrawn, Submitted },
        Rejected => new[] { Draft, Withdrawn },
        Approved => Array.Empty<string>(),
        Withdrawn => new[] { Draft },
        _ => new[] { Draft },
    };

    public static bool CanMove(string from, string to) => Next(from).Contains(to);
}

public static class VariationLineKinds
{
    /// <summary>Existing item, quantity taken out (amount negative), contract rate.</summary>
    public const string Omission = "OMISSION";
    /// <summary>Existing item, extra quantity, contract rate.</summary>
    public const string Addition = "ADDITION";
    /// <summary>New item priced from a rate build-up.</summary>
    public const string NewItem = "NEW ITEM";
    public static readonly string[] All = { Omission, Addition, NewItem };
}

/// <summary>A variation order / engineer's instruction / site instruction and its submission.</summary>
public sealed class Variation : Entity
{
    public string Number { get; set; } = "";
    public string Type { get; set; } = VariationTypes.Vo;
    public DateTime Date { get; set; }
    /// <summary>Consultant / client reference (EI or SI number, letter ref).</summary>
    public string ConsultantRef { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Building { get; set; } = "";
    /// <summary>Which rates priced the existing items (owner BOQ, or a subcontract number).</summary>
    public string RateSource { get; set; } = "BOQ";
    public string Status { get; set; } = VariationStatus.Draft;
    public DateTime StatusChangedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string AconexWorkflowNo { get; set; } = "";
    /// <summary>Amount the consultant approved (may differ from the net submitted).</summary>
    public double? ApprovedAmount { get; set; }
    public string Notes { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    // MOBCO "Commercial Proposal" header (03-Oct, from the real EI-04 / EI-13 proposals)
    public string Client { get; set; } = "Diriyah Company";
    public string Consultant { get; set; } = "Mirage";
    public string ContractNo { get; set; } = "DD-2022-370";
    /// <summary>Aconex letter of the instruction, e.g. L0135.</summary>
    public string LetterRef { get; set; } = "";
    public string Vendor { get; set; } = "";
    /// <summary>Markups on the variance, applied one after the other (compounding): General Requirements &amp; Logistics,
    /// Engineering &amp; Logistics, Overhead. 0 = not applied.</summary>
    public double MarkupGrPct { get; set; } = 0.05;
    public double MarkupEngPct { get; set; } = 0.08;
    public double MarkupOhPct { get; set; } = 0.09;
    public double VatPct { get; set; } = 0.15;
}

public sealed class VariationLine : Entity
{
    public long VariationId { get; set; }
    public int Order { get; set; }
    public string Kind { get; set; } = VariationLineKinds.Addition;
    /// <summary>BOQ / CONTRACT / "" (new item).</summary>
    public string SourceKind { get; set; } = "";
    public long SourceId { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>Always entered positive; the kind gives the sign.</summary>
    public double Qty { get; set; }
    /// <summary>Contract rate (OMISSION / ADDITION) - for NEW ITEM it is computed from the build-up.</summary>
    public double Rate { get; set; }
    public double Material { get; set; }
    public double Labour { get; set; }
    public double Equipment { get; set; }
    /// <summary>0.10 = 10 %.</summary>
    public double OverheadPct { get; set; }
    public double ProfitPct { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>A consultant document attached to a variation (text kept for search / suggestions).</summary>
public sealed class VariationDoc : Entity
{
    public long VariationId { get; set; }
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int Pages { get; set; }
    /// <summary>TEXT / SCANNED (no text layer) / NOT PDF / ERROR.</summary>
    public string TextStatus { get; set; } = "";
    public string ExtractedText { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

public sealed class VariationStatusChange : Entity
{
    public long VariationId { get; set; }
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public DateTime At { get; set; }
    public string By { get; set; } = "";
    public string Note { get; set; } = "";
}
