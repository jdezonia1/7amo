using System.Globalization;
using Raffaello.Core.Assistant;
using Raffaello.Core.Cables;
using Raffaello.Core.Domain;
using Raffaello.Core.Drawings;

namespace Raffaello.Core.Wiring;

/// <summary>
/// The text a page puts in the shared "selected record" (SelectionService.SelectedRecord in the app) so the assistant knows what the user
/// is looking at. One line: what it is, the key figures, and the citation token of the record (the assistant can repeat it).
/// </summary>
public static class SelectedRecords
{
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Room(string code, string building = "", string roomType = "") =>
        string.IsNullOrWhiteSpace(code) ? "" : $"room {code.Trim().ToUpperInvariant()}{(building.Length > 0 ? " " + building : "")}{(roomType.Length > 0 ? " (" + roomType + ")" : "")} {Citation.Room(code.Trim().ToUpperInvariant()).Token}";

    public static string LedgerLine(ClaimLine c) =>
        $"ledger line #{c.Id}: {c.Subcontractor} INV {c.InvoiceNo} {c.Room} {c.Stage} {c.Item} qty {N(c.Qty)} (SITE {c.SitePct:P0}, WIR {c.WirPct:P0}{(c.WirNo.Length > 0 ? " " + c.WirNo : "")}){(c.IsOver ? " OVER" : "")} {Citation.Room(c.Room.Trim().ToUpperInvariant()).Token}";

    public static string Invoice(SubInvoice i) =>
        $"subcontractor invoice {i.Title} rev {i.Revision} ({i.Status}){(i.AconexWorkflowNo.Length > 0 ? " Aconex " + i.AconexWorkflowNo : "")} {Citation.Invoice(i.ContractNo, i.Subcontractor, i.InvoiceNo).Token}";

    public static string Invoice(string contractNo, string sub, int invoiceNo, string status = "") =>
        $"subcontractor invoice {sub} INV-{invoiceNo:00} ({contractNo}){(status.Length > 0 ? " " + status : "")} {Citation.Invoice(contractNo, sub, invoiceNo).Token}";

    public static string Po(string poNo, string supplier = "") =>
        string.IsNullOrWhiteSpace(poNo) ? "" : $"purchase order {poNo}{(supplier.Length > 0 ? " " + supplier : "")} {Citation.Po(poNo).Token}";

    public static string Dn(string dnNo, string supplier = "", string poNo = "") =>
        string.IsNullOrWhiteSpace(dnNo) ? "" : $"delivery note {dnNo}{(supplier.Length > 0 ? " " + supplier : "")}{(poNo.Length > 0 ? " PO " + poNo : "")} {Citation.Dn(supplier, dnNo).Token}";

    public static string Variation(string number, string title = "", string status = "") =>
        string.IsNullOrWhiteSpace(number) ? "" : $"variation {number}{(title.Length > 0 ? " " + title : "")}{(status.Length > 0 ? " (" + status + ")" : "")} {Citation.Variation(number).Token}";

    public static string CableRun(CableRun r) =>
        $"cable run {r.Ref} {r.FromName} -> {r.ToName} {r.SizeKey}, design {(r.DesignLength is double d ? N(d) + " m" : "-")}, measured {(r.MeasuredLength is double m ? N(m) + " m" : "-")} ({r.Status}) [[cable:{(r.Ref.Length > 0 ? r.Ref : r.Id.ToString(CultureInfo.InvariantCulture))}]]";

    public static string Takeoff(DwgSheet s, DwgTakeoff t) =>
        $"drawing takeoff #{t.Id} on sheet {s.SheetNo} rev {s.Revision} ({s.Building} {s.Level}): {t.Hits} symbols, {t.Runs} linear runs, {t.Status} [[takeoff:{t.Id.ToString(CultureInfo.InvariantCulture)}]]";
}
