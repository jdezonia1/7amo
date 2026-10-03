using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Tracker;

/// <summary>Status of a subcontractor invoice against the marked-up drawings / statement (claim lines).</summary>
public static class DrawingStatus
{
    public const string Done = "DRAWING DONE";
    public const string Pending = "DRAWING PENDING";
    public const string Differs = "DIFFERS FROM INVOICE";
    public static readonly string[] All = { Done, Pending, Differs };
    /// <summary>Manual status kept as a rule (Kind INVSTATUS, ContractNo SUB:&lt;sub&gt;|&lt;building&gt;, MatchKey INV n, Target PENDING / DONE).</summary>
    public const string RuleKind = "INVSTATUS";
}

/// <summary>Invoiced / certified quantities of one subcontractor invoice from an invoice-check workbook (reference, read-only).</summary>
public sealed record InvoiceReference(string Subcontractor, int InvoiceNo, double? Invoiced, double? Certified, string RefStatus, string File);

public sealed record InvoiceStatusRow(string Building, string Subcontractor, int InvoiceNo, int Lines, double DrawnQty, double? Invoiced, double? Certified, double? Diff,
    string Status, string Manual, string RefStatus, string Note)
{
    public string StatusStripe => Status switch { DrawingStatus.Done => "OK", DrawingStatus.Differs => "CHECK", _ => "OPEN" };
}

/// <summary>
/// One list per subcontractor x invoice: DRAWING DONE (claim lines exist), DRAWING PENDING (invoice known but no claim lines yet - work in progress,
/// not counted, not an error), DIFFERS FROM INVOICE (drawn qty vs invoiced qty differ by more than 2 points and 2 %). PENDING / DONE can be set by hand.
/// </summary>
public static class InvoiceStatusList
{
    public const double PointTolerance = 2;
    public const double PctTolerance = 0.02;

    public static bool Differs(double drawn, double invoiced) => Math.Abs(drawn - invoiced) > Math.Max(PointTolerance, PctTolerance * Math.Abs(invoiced));

    public static string RuleScope(string sub, string building) => $"SUB:{sub.Trim().ToUpperInvariant()}|{building.Trim().ToUpperInvariant()}";
    public static string RuleKey(int invoiceNo) => $"INV {invoiceNo}";

    public static List<InvoiceStatusRow> Build(string building, IEnumerable<ClaimLine> claims, IEnumerable<InvoiceReference> references, IEnumerable<MappingRule> rules)
    {
        static string N(string s) => (s ?? "").Trim().ToUpperInvariant();
        var drawn = claims.Where(c => !c.Rework && !LedgerRules.IsNotCompared(c) && c.InvoiceNo > 0)
            .GroupBy(c => (Sub: N(c.Subcontractor), c.InvoiceNo)).ToDictionary(g => g.Key, g => (Lines: g.Count(), Qty: g.Sum(c => c.Qty)));
        var refs = references.GroupBy(r => (Sub: N(r.Subcontractor), r.InvoiceNo)).ToDictionary(g => g.Key, g => g.Last());
        var manual = rules.Where(r => r.Kind == DrawingStatus.RuleKind)
            .Select(r => (Parts: r.ContractNo.Split('|'), r))
            .Where(x => x.Parts.Length == 2 && x.Parts[1] == N(building) && x.Parts[0].StartsWith("SUB:") && int.TryParse(x.r.MatchKey.Replace("INV", "").Trim(), out _))
            .GroupBy(x => (Sub: x.Parts[0][4..], Inv: int.Parse(x.r.MatchKey.Replace("INV", "").Trim())))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.r.UpdatedAt).ThenByDescending(x => x.r.Id).First().r.Target);
        var keys = drawn.Keys.Concat(refs.Keys).Concat(manual.Keys.Select(k => (k.Sub, k.Inv))).Distinct();
        var res = new List<InvoiceStatusRow>();
        foreach (var k in keys.OrderBy(k => k.Sub).ThenBy(k => k.Item2))
        {
            var d = drawn.GetValueOrDefault(k);
            refs.TryGetValue(k, out var rf);
            manual.TryGetValue(k, out var m);
            m ??= "";
            double? diff = rf?.Invoiced is double inv && d.Lines > 0 ? d.Qty - inv : null;
            string status, note;
            if (m == "PENDING") { status = DrawingStatus.Pending; note = "set PENDING by hand - work in progress, not counted"; }
            else if (d.Lines == 0 && m != "DONE") { status = DrawingStatus.Pending; note = rf is null ? "no claim lines yet" : "invoice known, no marked-up drawing / claim lines yet - work in progress, not counted"; }
            else if (rf?.Invoiced is double iv && Differs(d.Qty, iv) && m != "DONE") { status = DrawingStatus.Differs; note = $"drawn {d.Qty:0.##} vs invoiced {iv:0.##} (more than {PointTolerance:0} points and {PctTolerance:P0})"; }
            else { status = DrawingStatus.Done; note = m == "DONE" ? "set DONE by hand" : rf?.Invoiced is null ? "claim lines exist (no invoice reference imported)" : "drawn qty agrees with the invoice"; }
            res.Add(new InvoiceStatusRow(building, k.Sub, k.Item2, d.Lines, d.Qty, rf?.Invoiced, rf?.Certified, diff, status, m, rf?.RefStatus ?? "", note));
        }
        return res;
    }

    // ------------------------------------------------------------------ reference workbooks (read-only)

    /// <summary>
    /// Reads the SUMMARY sheet of HOTEL_CLAIMS_vs_INVOICES.xlsx / BRANDED_LEDGER_vs_INVOICES.xlsx: SUBCONTRACTOR, INV / INVOICE,
    /// the first INVOICED ... column, the first CERTIFIED ... column and STATUS (header row found by text).
    /// </summary>
    public static (List<InvoiceReference> Rows, List<string> Issues) ReadReference(string path)
    {
        var rows = new List<InvoiceReference>();
        var issues = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(fs);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals("SUMMARY", StringComparison.OrdinalIgnoreCase)) ?? wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used is null) { issues.Add("SUMMARY is empty"); return (rows, issues); }
        int hdr = 0, cSub = 0, cInv = 0, cInvoiced = 0, cCert = 0, cStatus = 0;
        for (var r = used.FirstRow().RowNumber(); r <= used.FirstRow().RowNumber() + 15 && hdr == 0; r++)
        {
            foreach (var cell in ws.Row(r).CellsUsed())
            {
                var t = cell.GetString().Trim().ToUpperInvariant();
                var n = cell.Address.ColumnNumber;
                if (t == "SUBCONTRACTOR") cSub = n;
                else if ((t == "INV" || t == "INVOICE" || t == "INV NO" || t == "INVOICE NO") && cInv == 0) cInv = n;
                else if (t.StartsWith("INVOICED") && cInvoiced == 0) cInvoiced = n;
                else if (t.StartsWith("CERTIFIED") && cCert == 0) cCert = n;
                else if (t == "STATUS" && cStatus == 0) cStatus = n;
            }
            if (cSub > 0 && cInv > 0) hdr = r; else { cSub = cInv = cInvoiced = cCert = cStatus = 0; }
        }
        if (hdr == 0) { issues.Add("no header row with SUBCONTRACTOR and INV"); return (rows, issues); }
        double? Num(int row, int col)
        {
            if (col == 0) return null;
            var cell = ws.Cell(row, col);
            if (cell.TryGetValue<double>(out var d)) return d;
            return double.TryParse(cell.GetString().Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
        }
        for (var r = hdr + 1; r <= used.LastRow().RowNumber(); r++)
        {
            var sub = ws.Cell(r, cSub).GetString().Trim().ToUpperInvariant();
            if (sub.Length == 0 || sub.StartsWith("TOTAL")) continue;
            var inv = Num(r, cInv);
            if (inv is null) { issues.Add($"row {r}: {sub} has no invoice number"); continue; }
            rows.Add(new InvoiceReference(sub, (int)inv.Value, Num(r, cInvoiced), Num(r, cCert), cStatus > 0 ? ws.Cell(r, cStatus).GetString().Trim() : "", Path.GetFileName(path)));
        }
        return (rows, issues);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ReferenceFile(string folder, string building) => Path.Combine(folder, $"invoice_reference_{building.Trim().ToUpperInvariant()}.json");

    public static void SaveReference(string folder, string building, List<InvoiceReference> rows)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(ReferenceFile(folder, building), JsonSerializer.Serialize(rows, Json));
    }

    public static List<InvoiceReference> LoadReference(string folder, string building)
    {
        try
        {
            var f = ReferenceFile(folder, building);
            return File.Exists(f) ? JsonSerializer.Deserialize<List<InvoiceReference>>(File.ReadAllText(f), Json) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }
}