using Raffaello.Core.Domain;

namespace Raffaello.Core.Data;

/// <summary>Everything in the data file, loaded once into memory. Modules query this with LINQ.</summary>
public sealed class ProjectSnapshot
{
    public List<Room> Rooms { get; init; } = new();
    public List<QtyLine> Lines { get; init; } = new();
    public List<Subcontractor> Subcontractors { get; init; } = new();
    public List<Allocation> Allocations { get; init; } = new();
    public List<Wir> Wirs { get; init; } = new();
    public List<WirLine> WirLines { get; init; } = new();
    public List<Invoice> Invoices { get; init; } = new();
    public List<InvoiceLine> InvoiceLines { get; init; } = new();
    public List<PurchaseOrder> PurchaseOrders { get; init; } = new();
    public List<PoLine> PoLines { get; init; } = new();
    public List<DeliveryNote> DeliveryNotes { get; init; } = new();
    public List<DnLine> DnLines { get; init; } = new();
    public List<BoqItem> BoqItems { get; init; } = new();
    public List<Contract> Contracts { get; init; } = new();
    public List<AconexDoc> AconexDocs { get; init; } = new();
    public List<ImportBatch> Imports { get; init; } = new();
    // phase 1
    public List<RoomQty> RoomQtys { get; init; } = new();
    public List<ClaimLine> Claims { get; init; } = new();
    public List<ContractItem> ContractItems { get; init; } = new();
    public List<ContractItemBoq> ItemBoqs { get; init; } = new();
    public List<MappingRule> MappingRules { get; init; } = new();
    public List<SubInvoice> SubInvoices { get; init; } = new();
    public List<SubInvoiceLine> SubInvoiceLines { get; init; } = new();
    public List<InvoiceTemplateRow> TemplateRows { get; init; } = new();
    public List<SiteStatement> Statements { get; init; } = new();
    public List<RoomShape> RoomShapes { get; init; } = new();
    public List<Attachment> Attachments { get; init; } = new();
    public DateTime LoadedAt { get; init; } = DateTime.Now;

    public static ProjectSnapshot Load(IProjectStore db) => new()
    {
        Rooms = db.All<Room>(),
        Lines = db.All<QtyLine>(),
        Subcontractors = db.All<Subcontractor>(),
        Allocations = db.All<Allocation>(),
        Wirs = db.All<Wir>(),
        WirLines = db.All<WirLine>(),
        Invoices = db.All<Invoice>(),
        InvoiceLines = db.All<InvoiceLine>(),
        PurchaseOrders = db.All<PurchaseOrder>(),
        PoLines = db.All<PoLine>(),
        DeliveryNotes = db.All<DeliveryNote>(),
        DnLines = db.All<DnLine>(),
        BoqItems = db.All<BoqItem>(),
        Contracts = db.All<Contract>(),
        AconexDocs = db.All<AconexDoc>(),
        Imports = db.All<ImportBatch>(),
        RoomQtys = db.All<RoomQty>(),
        Claims = db.All<ClaimLine>(),
        ContractItems = db.All<ContractItem>(),
        ItemBoqs = db.All<ContractItemBoq>(),
        MappingRules = db.All<MappingRule>(),
        SubInvoices = db.All<SubInvoice>(),
        SubInvoiceLines = db.All<SubInvoiceLine>(),
        TemplateRows = db.All<InvoiceTemplateRow>(),
        Statements = db.All<SiteStatement>(),
        RoomShapes = db.All<RoomShape>(),
        Attachments = db.All<Attachment>(),
        LoadedAt = DateTime.Now,
    };

    public bool IsEmpty => Lines.Count == 0;
}
