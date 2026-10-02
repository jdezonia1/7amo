namespace Raffaello.Core.Domain;

/// <summary>Base for every stored row: identity, audit stamp and optimistic-concurrency version.</summary>
public abstract class Entity
{
    public long Id { get; set; }
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>A room / unit / public area on a level.</summary>
public sealed class Room : Entity
{
    public string Building { get; set; } = Buildings.Hotel;
    public string Level { get; set; } = "";
    public string Code { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string Zone { get; set; } = "";
}

/// <summary>
/// One line of the chain: a room x system x stage x BOQ item. QS / PROJECT QTY live here;
/// GIVEN, DONE, CLAIMED and DELIVERED are derived from allocations, WIRs, invoices and DNs.
/// </summary>
public sealed class QtyLine : Entity
{
    public string Building { get; set; } = Buildings.Hotel;
    public string Level { get; set; } = "";
    public string Room { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string System { get; set; } = "";
    public string Stage { get; set; } = Stages.First;
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "PT";
    public double QsQty { get; set; }
    /// <summary>Contract cap for subcontractor claims. Null = not yet filled (QS is used as the cap).</summary>
    public double? ProjectQty { get; set; }
    public double Rate { get; set; }
    /// <summary>Site progress % from the site statement (0..1). Comparisons are made after SITE %.</summary>
    public double SitePct { get; set; }
}

public sealed class Subcontractor : Entity
{
    public string Name { get; set; } = "";
    public string Trade { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Contact { get; set; } = "";
}

/// <summary>GIVEN: quantity handed to a subcontractor on a line.</summary>
public sealed class Allocation : Entity
{
    public long LineId { get; set; }
    public string Subcontractor { get; set; } = "";
    public double Qty { get; set; }
    public string Ref { get; set; } = "";
    public DateTime GivenAt { get; set; }
}

/// <summary>A work (WIR) or material (MIR) inspection request.</summary>
public sealed class Wir : Entity
{
    public string WirNo { get; set; } = "";
    public string Kind { get; set; } = "WIR";
    public string Subcontractor { get; set; } = "";
    public string Building { get; set; } = Buildings.Hotel;
    public string Level { get; set; } = "";
    public string System { get; set; } = "";
    public string Stage { get; set; } = Stages.First;
    public string Description { get; set; } = "";
    public string AconexNo { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public string Status { get; set; } = WirStatus.Open;
    public DateTime? ApprovedAt { get; set; }
}

public sealed class WirLine : Entity
{
    public long WirId { get; set; }
    public long LineId { get; set; }
    public double Qty { get; set; }
    /// <summary>EMT / rework: never counts as progress.</summary>
    public bool IsRework { get; set; }
}

/// <summary>A cumulative subcontractor statement / invoice.</summary>
public sealed class Invoice : Entity
{
    public string Subcontractor { get; set; } = "";
    public string InvoiceNo { get; set; } = "";
    public DateTime InvDate { get; set; }
    public string Status { get; set; } = InvoiceStatus.Received;
    public DateTime? CertifiedAt { get; set; }
    public double ClaimedAmount { get; set; }
    public double CertifiedAmount { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class InvoiceLine : Entity
{
    public long InvoiceId { get; set; }
    public long LineId { get; set; }
    /// <summary>Cumulative quantity claimed to date on this statement (statements are cumulative, never summed).</summary>
    public double CumQty { get; set; }
    public double Rate { get; set; }
    public double CertifiedQty { get; set; }
}

public sealed class PurchaseOrder : Entity
{
    public string PoNo { get; set; } = "";
    public string Supplier { get; set; } = "";
    public DateTime PoDate { get; set; }
    public double StatedTotal { get; set; }
    public string Status { get; set; } = "OPEN";
    public string Description { get; set; } = "";
}

public sealed class PoLine : Entity
{
    public long PoId { get; set; }
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "NO";
    public double Qty { get; set; }
    public double Rate { get; set; }
    public string System { get; set; } = "";
    /// <summary>For pipe/conduit lines bought in M but delivered in PCS: metres per piece (0 = use settings default).</summary>
    public double LengthPerPcs { get; set; }
}

public sealed class DeliveryNote : Entity
{
    public string DnNo { get; set; } = "";
    public long PoId { get; set; }
    public DateTime DnDate { get; set; }
    public string MirNo { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class DnLine : Entity
{
    public long DnId { get; set; }
    public long PoLineId { get; set; }
    /// <summary>Quantity in the PO unit (after any PCS to M conversion).</summary>
    public double Qty { get; set; }
    public double RawQty { get; set; }
    public string RawUnit { get; set; } = "";
    /// <summary>Optional link to a chain line (material issued to a room line).</summary>
    public long? LineId { get; set; }
}

public sealed class BoqItem : Entity
{
    public string ItemCode { get; set; } = "";
    public string Bill { get; set; } = "";
    public string Description { get; set; } = "";
    public string System { get; set; } = "";
    public string Stage { get; set; } = Stages.First;
    public string Unit { get; set; } = "PT";
    public double BoqQty { get; set; }
    public double? ProjectQty { get; set; }
    public double Rate { get; set; }
}

public sealed class Contract : Entity
{
    public string Subcontractor { get; set; } = "";
    public string ContractNo { get; set; } = "";
    public string Scope { get; set; } = "";
    public double Value { get; set; }
    public double RetentionPct { get; set; } = 0.10;
    public double AdvancePct { get; set; }
    public DateTime SignedAt { get; set; }
    public string Status { get; set; } = "ACTIVE";
}

public sealed class AconexDoc : Entity
{
    public string DocNo { get; set; } = "";
    public string Title { get; set; } = "";
    public string DocType { get; set; } = "WIR";
    public string Revision { get; set; } = "0";
    public string Status { get; set; } = "";
    public DateTime? DownloadedAt { get; set; }
    public string LocalPath { get; set; } = "";
    public bool Queued { get; set; }
}

public sealed class ImportBatch : Entity
{
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Rows { get; set; }
    public int Errors { get; set; }
    public DateTime ImportedAt { get; set; }
}

/// <summary>One row per change. Not an <see cref="Entity"/>: the log itself is append-only.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public string TableName { get; set; } = "";
    public long RowId { get; set; }
    public string Action { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Changes { get; set; } = "";
}

/// <summary>Heartbeat row: who has the shared data file open.</summary>
public sealed class PresenceRow
{
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public string Screen { get; set; } = "";
    public DateTime LastSeen { get; set; }
}
