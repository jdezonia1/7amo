using Raffaello.Core.Domain;

namespace Raffaello.Core.Materials;

// =====================================================================================================
//  Phase 3 - supplier POs, delivery notes, MIRs, 3-way match, supplier invoices, coding memory.
//  Own tables (Mat*), created by SqliteMaterialsStore in the same data file; same Entity conventions
//  (UpdatedBy / UpdatedAt / RowVersion, AuditLog rows).
// =====================================================================================================

public static class CodeStatus
{
    /// <summary>Filled automatically (high confidence).</summary>
    public const string Auto = "AUTO";
    /// <summary>Filled, flagged for review (medium confidence).</summary>
    public const string Review = "REVIEW";
    /// <summary>No confident suggestion - the user picks.</summary>
    public const string Ask = "ASK";
    public const string Confirmed = "CONFIRMED";
    public const string Manual = "MANUAL";
    /// <summary>Came with the document (e.g. PO scope-of-work sheet).</summary>
    public const string Document = "DOCUMENT";
    public const string None = "";
}

/// <summary>A supplier purchase order with its terms as read from the document.</summary>
public sealed class MatPo : Entity
{
    public string PoNo { get; set; } = "";
    public string Supplier { get; set; } = "";
    public DateTime? PoDate { get; set; }
    public string Scope { get; set; } = "";
    public string Currency { get; set; } = "SAR";
    public double StatedTotal { get; set; }
    public double StatedVat { get; set; }
    public double StatedGrandTotal { get; set; }
    public double AdvancePct { get; set; }
    public double RetentionPct { get; set; }
    /// <summary>Tolerance in the header block (e.g. 0 %).</summary>
    public double? ToleranceHeaderPct { get; set; }
    /// <summary>Tolerance in the conditions (e.g. clause 6: +/- 5 % per item).</summary>
    public double? ToleranceClausePct { get; set; }
    public double PenaltyPctPerWeek { get; set; }
    public double PenaltyMaxPct { get; set; }
    public string PenaltyText { get; set; } = "";
    public string PaymentTerms { get; set; } = "";
    public string DeliveryTerms { get; set; } = "";
    public bool Remeasurable { get; set; }
    public string ScopeRef { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Status { get; set; } = "OPEN";
    public string Notes { get; set; } = "";
    public DateTime ImportedAt { get; set; }

    /// <summary>Tolerance used by the OVER PO check: the clause wins over the header (the header is usually the template default).</summary>
    public double EffectiveTolerancePct => ToleranceClausePct ?? ToleranceHeaderPct ?? 0;
    public bool ToleranceConflict => ToleranceHeaderPct is double h && ToleranceClausePct is double c && Math.Abs(h - c) > 1e-9;
}

public sealed class MatPoLine : Entity
{
    public long PoId { get; set; }
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public double Rate { get; set; }
    public double Amount { get; set; }
    /// <summary>Metres per piece for conduit / pipe lines (0 = settings default).</summary>
    public double LengthPerPcs { get; set; }
    public string Fingerprint { get; set; } = "";
    // ---- coding (owner BOQ / ERP) with provenance
    public string BoqCode { get; set; } = "";
    public string CostCode { get; set; } = "";
    public string BudgetResourceCode { get; set; } = "";
    public string ResourceCode { get; set; } = "";
    public string CodeStatus { get; set; } = Materials.CodeStatus.None;
    public string CodeSource { get; set; } = "";
    public double CodeScore { get; set; }
}

/// <summary>Scope-of-work (ePROMIS) line attached to a PO: resource code + owner BOQ code + qty. Feeds auto-coding.</summary>
public sealed class MatPoScope : Entity
{
    public long PoId { get; set; }
    public int SrNo { get; set; }
    public string ResourceCode { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Qty { get; set; }
    public string Fingerprint { get; set; } = "";
}

public sealed class MatDn : Entity
{
    public string DnNo { get; set; } = "";
    public string Supplier { get; set; } = "";
    public DateTime? DnDate { get; set; }
    public string PoNo { get; set; } = "";
    public DateTime? PoDate { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerNo { get; set; } = "";
    public string TruckNo { get; set; } = "";
    public string SourceFile { get; set; } = "";
    /// <summary>TEXT / OCR / VISION / EXCEL / MANUAL.</summary>
    public string Source { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}

public sealed class MatDnLine : Entity
{
    public long DnId { get; set; }
    public int Order { get; set; }
    public string ItemNo { get; set; } = "";
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Batch { get; set; } = "";
    public double RawQty { get; set; }
    public string RawUnit { get; set; } = "";
    /// <summary>Quantity in the PO line unit (after KM -> M / PCS -> M).</summary>
    public double Qty { get; set; }
    public string Unit { get; set; } = "";
    public string ConversionNote { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long? PoLineId { get; set; }
    public string MatchStatus { get; set; } = "";
    public string MatchNote { get; set; } = "";
    // owner BOQ coding for MOS
    public string BoqCode { get; set; } = "";
    public string CodeStatus { get; set; } = Materials.CodeStatus.None;
    public string CodeSource { get; set; } = "";
    public double CodeScore { get; set; }
}

public sealed class MatMir : Entity
{
    public string MirNo { get; set; } = "";
    public string Revision { get; set; } = "";
    public DateTime? MirDate { get; set; }
    public string MarRef { get; set; } = "";
    public string Description { get; set; } = "";
    public string Supplier { get; set; } = "";
    /// <summary>OPEN / APPROVED / APPROVED WITH COMMENTS / REJECTED.</summary>
    public string Status { get; set; } = "OPEN";
    public string SourceFile { get; set; } = "";
    public string PageKinds { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}

/// <summary>DN (and PO) bundled in a MIR. A MIR can carry several DNs and POs.</summary>
public sealed class MatMirDn : Entity
{
    public long MirId { get; set; }
    public string DnNo { get; set; } = "";
    public string PoNo { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>Evidence inside a MIR: test certificate (drum + qty), drum label (batch), required quantity list.</summary>
public sealed class MatMirEvidence : Entity
{
    public long MirId { get; set; }
    /// <summary>CERT / LABEL / REQUIRED / DNQTY.</summary>
    public string Kind { get; set; } = "CERT";
    public int Page { get; set; }
    public string DrumNo { get; set; } = "";
    public string Batch { get; set; } = "";
    public string Description { get; set; } = "";
    public double Qty { get; set; }
    public string Unit { get; set; } = "";
    public string PoRef { get; set; } = "";
    public string DnNo { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>Hard lock: a DN line is invoiced in exactly one supplier invoice number (all revisions of that invoice share it).</summary>
public sealed class MatDnInvoiceLock : Entity
{
    public long DnLineId { get; set; }
    public string Supplier { get; set; } = "";
    public string PoNo { get; set; } = "";
    public int InvoiceNo { get; set; }
    public long SubInvoiceId { get; set; }
    public DateTime LockedAt { get; set; }
}

/// <summary>A learned code assignment (every confirmation is remembered).</summary>
public sealed class MatCodeMemory : Entity
{
    /// <summary>SUPPLIERCODE / DESC / FP.</summary>
    public string KeyType { get; set; } = "DESC";
    public string Key { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public string BoqCode { get; set; } = "";
    public string CostCode { get; set; } = "";
    public string BudgetResourceCode { get; set; } = "";
    public string ResourceCode { get; set; } = "";
    public int Uses { get; set; }
    public string ConfirmedBy { get; set; } = "";
}
