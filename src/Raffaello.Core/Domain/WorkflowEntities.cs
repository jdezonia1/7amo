namespace Raffaello.Core.Domain;

// =====================================================================================================
//  Phase 1 - the real workflow: room ledger, contract items + BOQ links, mapping, subcontractor invoices
// =====================================================================================================

/// <summary>PROJECT QTY per room x stage x item (tracker key "STAGE|ITEM"). The cap for every subcontractor claim.</summary>
public sealed class RoomQty : Entity
{
    public string Building { get; set; } = Buildings.Branded;
    public string Room { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    public string Unit { get; set; } = "no";
    public double Qty { get; set; }
    public string Source { get; set; } = "";

    public string Key => LedgerKeys.Key(Room, Stage, Item);
}

public static class CheckStatus
{
    public const string None = "";
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Partly = "PARTLY";
    public const string Revised = "REVISED";
    public const string Rejected = "REJECTED";

    public static bool IsPending(string? s) => s == Pending;
}

/// <summary>
/// One claim line in the room ledger (append-only: a correction is a new line, usually negative).
/// QTY is the plan quantity claimed for the room - that is what counts against the room cap.
/// </summary>
public sealed class ClaimLine : Entity
{
    public string Building { get; set; } = Buildings.Branded;
    public string Subcontractor { get; set; } = "";
    /// <summary>Subcontractor invoice number the line belongs to (1, 2, 3 ...).</summary>
    public int InvoiceNo { get; set; }
    public string Stage { get; set; } = "";
    public string Floor { get; set; } = "";
    public string Room { get; set; } = "";
    public string Item { get; set; } = "";
    public string Unit { get; set; } = "no";
    public double Qty { get; set; }
    public double SitePct { get; set; } = 1;
    public double WirPct { get; set; } = 1;
    public string WirNo { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Rework { get; set; }
    public string WorkType { get; set; } = "";
    /// <summary>Reason given when the claim exceeds the remaining room quantity (posted at full qty, OVER).</summary>
    public string OverReason { get; set; } = "";
    public bool IsOver { get; set; }
    public string AreaType { get; set; } = "";
    public string Source { get; set; } = "MANUAL";
    /// <summary>Stable key of the source row (import dedupe).</summary>
    public string SourceKey { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public DateTime EnteredAt { get; set; }

    // ---- height above 4.5 m check
    public double QtyAbove45 { get; set; }
    public string HeightStatus { get; set; } = CheckStatus.None;
    public double QtyAbove45Accepted { get; set; }
    public string HeightCheckedBy { get; set; } = "";
    public DateTime? HeightCheckDate { get; set; }
    public string HeightNote { get; set; } = "";
    public string HeightPhoto { get; set; } = "";

    // ---- 15 m route-length check (2nd-fix pulling items)
    public bool LengthApplies { get; set; }
    /// <summary>Quantity the subcontractor claims including length extras (QTY is the plan qty).</summary>
    public double LengthClaimedQty { get; set; }
    public double RouteLengthTotal { get; set; }
    /// <summary>Groups "n x length" separated by ';' e.g. "10x20;5x35".</summary>
    public string LengthGroups { get; set; } = "";
    public double LengthRevisedQty { get; set; }
    public double? LengthRevisedOverride { get; set; }
    public string LengthStatus { get; set; } = CheckStatus.None;
    public string LengthCheckedBy { get; set; } = "";
    public DateTime? LengthCheckDate { get; set; }
    public string LengthNote { get; set; } = "";
    public string LengthAttachment { get; set; } = "";

    public string Key => LedgerKeys.Key(Room, Stage, Item);
    public double QtyAfterSite => Qty * SitePct;
    public double QtyAfterWir => Qty * SitePct * WirPct;
}

public static class LedgerKeys
{
    public static string Key(string room, string stage, string item) =>
        $"{(room ?? "").Trim().ToUpperInvariant()}|{(stage ?? "").Trim().ToUpperInvariant()}|{(item ?? "").Trim().ToUpperInvariant()}";
}

/// <summary>A subcontract schedule line (labour rate) with attributes parsed from its description.</summary>
public sealed class ContractItem : Entity
{
    public string ContractNo { get; set; } = "";
    public string ItemNo { get; set; } = "";
    public int Order { get; set; }
    public string Section { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public double Rate { get; set; }
    /// <summary>1ST FIX / 2ND FIX / 3RD FIX / NONE.</summary>
    public string FixStage { get; set; } = "";
    /// <summary>PVC / EMT / RS / FLEX / NONE.</summary>
    public string ConduitType { get; set; } = "";
    /// <summary>WALL / CEILING / BOTH.</summary>
    public string Mount { get; set; } = "";
    /// <summary>LOW (below 4.5 m) / HIGH (above 4.5 m) / ANY.</summary>
    public string HeightBand { get; set; } = "";
    public bool Is2ndFixPulling { get; set; }
    public bool IsHomerun { get; set; }
    /// <summary>Comma list of tracker systems this item covers (POWER,LIGHT,DALI ...).</summary>
    public string Systems { get; set; } = "";
    /// <summary>OUTLET / WIRING / CABLE / CABLE TERMINATION / TRAY / PANEL / EARTHING / ISOLATOR / DEVICE / OTHER.</summary>
    public string Category { get; set; } = "";
    /// <summary>Extra key for size-driven items: panel ways (42), cable "4X16", tray size band.</summary>
    public string SizeKey { get; set; } = "";
    /// <summary>Stage payment % used by the invoice template (0.9 / 0.7 / 0.1 ...).</summary>
    public double StagePct { get; set; }
    public bool AttributesConfirmed { get; set; }
    public string ParseNotes { get; set; } = "";
}

/// <summary>Link contract item -> owner BOQ code (from the contract link workbook or the invoice template).</summary>
public sealed class ContractItemBoq : Entity
{
    public string ContractNo { get; set; } = "";
    public string ItemNo { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string BoqDescription { get; set; } = "";
    public int Order { get; set; }
    public string Source { get; set; } = "";
}

/// <summary>A learned / manual mapping override. Kind ITEM: ledger key -> contract item. Kind BOQ: item+system+area -> BOQ code.</summary>
public sealed class MappingRule : Entity
{
    public string Kind { get; set; } = "ITEM";
    public string ContractNo { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public string Target { get; set; } = "";
    public string Note { get; set; } = "";
    public int UseCount { get; set; }
}

public static class SubInvoiceStatus
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Rejected = "REJECTED";
    public const string Approved = "APPROVED";
}

/// <summary>A subcontractor invoice revision (INV-01 Rev 0, Rev 1 ...). Approved revisions are locked.</summary>
public sealed class SubInvoice : Entity
{
    public string Subcontractor { get; set; } = "";
    public string ContractNo { get; set; } = "";
    public int InvoiceNo { get; set; }
    public int Revision { get; set; }
    public string Status { get; set; } = SubInvoiceStatus.Draft;
    public string AconexWorkflowNo { get; set; } = "";
    public string RejectionReason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PeriodTo { get; set; }
    public double RetentionPct { get; set; } = 0.10;
    public double AdvancePct { get; set; }
    public double AdvanceRecovery { get; set; }
    public double Discount { get; set; }
    public string Notes { get; set; } = "";
    public bool Locked { get; set; }

    public string Title => $"{Subcontractor} INV-{InvoiceNo:00} Rev {Revision}";
}

public sealed class SubInvoiceLine : Entity
{
    public long SubInvoiceId { get; set; }
    public int RowOrder { get; set; }
    /// <summary>ITEM / SECTION / NOTE.</summary>
    public string Kind { get; set; } = "ITEM";
    public string ItemNo { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string BoqDescription { get; set; } = "";
    public string CostCode { get; set; } = "";
    public string BudgetResourceCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double ContractQty { get; set; }
    public double Rate { get; set; }
    public double StagePct { get; set; }
    public double PrevQty { get; set; }
    public double CurrQty { get; set; }
    public double CumQty { get; set; }
    /// <summary>How the quantity was built (room x stage x system -> item -> BOQ row).</summary>
    public string Explanation { get; set; } = "";

    public double Amount => ContractQty * Rate * StagePct;
    public double PrevAmount => PrevQty * Rate * StagePct;
    public double CurrAmount => CurrQty * Rate * StagePct;
    public double CumAmount => CumQty * Rate * StagePct;
}

/// <summary>One row of the subcontract invoice template ('ROOTS INV 1' layout), kept in order.</summary>
public sealed class InvoiceTemplateRow : Entity
{
    public string ContractNo { get; set; } = "";
    public int RowOrder { get; set; }
    public string Kind { get; set; } = "ITEM";
    public string ItemNo { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string CostCode { get; set; } = "";
    public string BudgetResourceCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public double Rate { get; set; }
    public double StagePct { get; set; }
    /// <summary>Executed cumulative quantity found in the imported file (column Q).</summary>
    public double ImportedExecutedCum { get; set; }
}

/// <summary>A standard site statement issued to / received from a subcontractor.</summary>
public sealed class SiteStatement : Entity
{
    public string Subcontractor { get; set; } = "";
    public string StatementNo { get; set; } = "";
    public string Direction { get; set; } = "OUT";
    public string ContentHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Lines { get; set; }
    public DateTime At { get; set; }
}

/// <summary>A level plan image from the tracker PLANS sheet.</summary>
public sealed class PlanImage : Entity
{
    public string Building { get; set; } = Buildings.Branded;
    public string Plan { get; set; } = "";
    public string Name { get; set; } = "";
    public string Caption { get; set; } = "";
    public byte[]? Png { get; set; }
    public long X { get; set; }
    public long Y { get; set; }
    public long Width { get; set; }
    public long Height { get; set; }
}

/// <summary>A room outline on a plan: polygons in 0..1 coordinates of the plan image.</summary>
public sealed class RoomShape : Entity
{
    public string Building { get; set; } = Buildings.Branded;
    public string Room { get; set; } = "";
    public string ShapeName { get; set; } = "";
    public string Plan { get; set; } = "";
    /// <summary>"x,y x,y x,y|x,y ..." normalised to the plan image (one polygon per '|').</summary>
    public string Polygons { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
    public string Description { get; set; } = "";
}
