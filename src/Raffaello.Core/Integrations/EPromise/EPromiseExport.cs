using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClosedXML.Excel;
using Raffaello.Core.Contracts;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Integrations.EPromise;

/// <summary>Values a column of the E-Promise import file can take (see <see cref="EPromiseExportConfig.Columns"/>).</summary>
public static class EPromiseFields
{
    public const string JobNo = "JobNo";
    public const string Bill = "Bill";
    public const string Section = "Section";
    public const string Page = "Page";
    public const string Rev = "Rev";
    public const string Item = "Item";
    public const string BoqCode = "BoqCode";
    /// <summary>The owner BOQ description from the E-Promise budget list (falls back to the invoice line text).</summary>
    public const string BoqDescription = "BoqDescription";
    /// <summary>The subcontract item text of the invoice line.</summary>
    public const string Description = "Description";
    public const string Wbs = "Wbs";
    /// <summary>E-Promise 'Activity' column = cost code.</summary>
    public const string Activity = "Activity";
    public const string CostCode = "CostCode";
    public const string BudgetResourceCode = "BudgetResourceCode";
    public const string BudgetResource = "BudgetResource";
    public const string Unit = "Unit";
    public const string Qty = "Qty";
    public const string Rate = "Rate";
    public const string StagePct = "StagePct";
    public const string Amount = "Amount";
    public const string PrevQty = "PrevQty";
    public const string CumQty = "CumQty";
    public const string CumAmount = "CumAmount";
    public const string ContractQty = "ContractQty";
    public const string ItemNo = "ItemNo";
    public const string ContractNo = "ContractNo";
    public const string Subcontractor = "Subcontractor";
    public const string VendorNo = "VendorNo";
    public const string InvoiceNo = "InvoiceNo";
    public const string Revision = "Revision";
    /// <summary>"SUB-ELE-028-2026 INV-01 Rev 0".</summary>
    public const string InvoiceRef = "InvoiceRef";
    public const string PeriodTo = "PeriodTo";
    public const string AconexWorkflowNo = "AconexWorkflowNo";
    /// <summary>The fixed text in <see cref="EPromiseColumn.Constant"/>.</summary>
    public const string Constant = "Constant";

    public static readonly string[] All =
    {
        JobNo, Bill, Section, Page, Rev, Item, BoqCode, BoqDescription, Description, Wbs, Activity, CostCode, BudgetResourceCode, BudgetResource,
        Unit, Qty, Rate, StagePct, Amount, PrevQty, CumQty, CumAmount, ContractQty, ItemNo, ContractNo, Subcontractor, VendorNo, InvoiceNo, Revision,
        InvoiceRef, PeriodTo, AconexWorkflowNo, Constant,
    };

    public static readonly HashSet<string> Numeric = new() { Qty, Rate, StagePct, Amount, PrevQty, CumQty, CumAmount, ContractQty, InvoiceNo, Revision };
}

public sealed class EPromiseColumn
{
    public string Header { get; set; } = "";
    public string Field { get; set; } = "";
    public string Constant { get; set; } = "";
    public EPromiseColumn() { }
    public EPromiseColumn(string header, string field, string constant = "") { Header = header; Field = field; Constant = constant; }
}

/// <summary>
/// The import layout of MOBCO's ERP (E-Promise). The default columns mirror the 'E promise - Resource' budget sheet (Job, Bill,
/// Section, Page, Rev, Item, BOQ No, Description, WBS, Activity, Budget Resource Code, Budget Resource) followed by the
/// certified quantity / rate / amount and the invoice reference. Everything is configurable in
/// %APPDATA%\Raffaello\epromise-export.json (header text, which field, order, constants) because the import template is
/// owned by finance and may change.
/// </summary>
public sealed class EPromiseExportConfig
{
    public int Version { get; set; } = 1;
    /// <summary>Job number when the BOQ row does not carry one (E-Promise list column A, e.g. the Raffles job).</summary>
    public string JobNo { get; set; } = "";
    /// <summary>Only head-office approved (locked) revisions are exported unless the user overrides.</summary>
    public bool OnlyCertified { get; set; } = true;
    /// <summary>CURRENT = this invoice's quantity (default), CUMULATIVE = to date.</summary>
    public string QtyBasis { get; set; } = "CURRENT";
    /// <summary>Quantity column multiplied by the stage % (certified points) instead of the executed points.</summary>
    public bool QtyTimesStagePct { get; set; }
    /// <summary>One row per BOQ code + cost code + budget resource (quantities and amounts summed) instead of one per invoice line.</summary>
    public bool GroupByBoqCode { get; set; }
    public bool SkipZeroLines { get; set; } = true;
    public string SheetName { get; set; } = "E promise - Resource";
    public string CsvDelimiter { get; set; } = ",";
    public bool CsvUtf8Bom { get; set; } = true;
    public int AmountDecimals { get; set; } = 2;
    public List<EPromiseColumn> Columns { get; set; } = DefaultColumns();

    public static List<EPromiseColumn> DefaultColumns() => new()
    {
        new("Job", EPromiseFields.JobNo), new("Bill", EPromiseFields.Bill), new("Section", EPromiseFields.Section), new("Page", EPromiseFields.Page),
        new("Rev", EPromiseFields.Rev), new("Item", EPromiseFields.Item), new("BOQ No", EPromiseFields.BoqCode), new("Description", EPromiseFields.BoqDescription),
        new("WBS", EPromiseFields.Wbs), new("Activity", EPromiseFields.Activity), new("Budget Resource Code", EPromiseFields.BudgetResourceCode),
        new("Budget Resource", EPromiseFields.BudgetResource), new("Unit", EPromiseFields.Unit), new("Qty", EPromiseFields.Qty), new("Rate", EPromiseFields.Rate),
        new("Amount", EPromiseFields.Amount), new("Contract No", EPromiseFields.ContractNo), new("Subcontractor", EPromiseFields.Subcontractor),
        new("Invoice Ref", EPromiseFields.InvoiceRef),
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello", "epromise-export.json");

    public static EPromiseExportConfig Load(string? path = null, bool writeIfMissing = true)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            var d = new EPromiseExportConfig();
            if (writeIfMissing) d.Save(path);
            return d;
        }
        var c = JsonSerializer.Deserialize<EPromiseExportConfig>(File.ReadAllText(path), Json) ?? new EPromiseExportConfig();
        if (c.Columns.Count == 0) c.Columns = DefaultColumns();
        return c;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public List<string> Validate()
    {
        var p = new List<string>();
        if (Columns.Count == 0) p.Add("No columns configured.");
        foreach (var c in Columns.Where(c => !EPromiseFields.All.Contains(c.Field)))
            p.Add($"Column '{c.Header}': unknown field '{c.Field}' (use one of {string.Join(", ", EPromiseFields.All)}).");
        if (QtyBasis is not ("CURRENT" or "CUMULATIVE")) p.Add("QtyBasis must be CURRENT or CUMULATIVE.");
        if (CsvDelimiter.Length != 1) p.Add("CsvDelimiter must be one character.");
        return p;
    }
}

/// <summary>One row of the ERP import (values by field name).</summary>
public sealed class EPromiseRow
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    public object? this[string field] => Values.GetValueOrDefault(field);
    public double Amount => Values.GetValueOrDefault(EPromiseFields.Amount) is double d ? d : 0;
    public double Qty => Values.GetValueOrDefault(EPromiseFields.Qty) is double d ? d : 0;
}

public sealed class EPromiseExportResult
{
    public SubInvoice Invoice { get; init; } = new();
    public List<EPromiseRow> Rows { get; } = new();
    /// <summary>Lines that will import badly: no BOQ code, code not in the E-Promise list, no cost code / budget resource ...</summary>
    public List<string> Issues { get; } = new();
    /// <summary>The revision is not head-office approved and the export was not forced.</summary>
    public bool Blocked { get; set; }
    public double TotalAmount => Math.Round(Rows.Sum(r => r.Amount), 2);
    public string Summary => Blocked ? $"{Invoice.Title}: not certified - export blocked" :
        $"{Invoice.Title}: {Rows.Count} ERP rows, amount {TotalAmount:N2}" + (Issues.Count > 0 ? $", {Issues.Count} issue(s)" : "");
}

/// <summary>
/// Certified subcontractor invoice -&gt; E-Promise import file (Excel or CSV). The owner BOQ code (Bill-Section-Page-Rev-Item)
/// is split into its parts; WBS / cost code / budget resource come from the invoice line first and the E-Promise budget
/// list (imported from the 'E promise - Resource' sheet) second.
/// </summary>
public static class EPromiseExporter
{
    /// <summary>Bill / Section / Page / Rev / Item of an owner BOQ code; Item keeps the rest ("6-26-V-5").</summary>
    public static (string Bill, string Section, string Page, string Rev, string Item) Split(string? code)
    {
        var parts = (code ?? "").Trim().ToUpperInvariant().Split('-');
        if (parts.Length < 5 || !BoqCodes.IsCode(code)) return ("", "", "", "", "");
        return (parts[0], parts[1], parts[2], parts[3], string.Join("-", parts.Skip(4)));
    }

    public static bool IsCertified(SubInvoice inv) => inv.Locked || inv.Status == SubInvoiceStatus.Approved;

    public static EPromiseExportResult Build(ProjectSnapshot s, SubInvoice inv, EPromiseExportConfig cfg, bool force = false)
    {
        var res = new EPromiseExportResult { Invoice = inv };
        if (cfg.OnlyCertified && !IsCertified(inv) && !force)
        {
            res.Blocked = true;
            res.Issues.Add($"{inv.Title} is {inv.Status}: only head-office approved (certified) revisions go to E-Promise.");
            return res;
        }
        var boq = s.BoqItems.Where(b => b.ItemCode.Length > 0).GroupBy(b => b.ItemCode.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var contract = s.Contracts.FirstOrDefault(c => c.ContractNo.Equals(inv.ContractNo, StringComparison.OrdinalIgnoreCase));
        var lines = s.SubInvoiceLines.Where(l => l.SubInvoiceId == inv.Id && l.Kind == "ITEM").OrderBy(l => l.RowOrder).ToList();
        var cum = cfg.QtyBasis == "CUMULATIVE";
        var invoiceRef = $"{inv.ContractNo} INV-{inv.InvoiceNo:00} Rev {inv.Revision}".Trim();

        var raw = new List<(SubInvoiceLine Line, string Code, BoqItem? Boq, double Qty, double Amount, double Prev, double Cum, double CumAmount)>();
        foreach (var l in lines)
        {
            var qty = cum ? l.CumQty : l.CurrQty;
            if (cfg.SkipZeroLines && Math.Abs(qty) < 1e-9) continue;
            var amount = (cum ? l.CumAmount : l.CurrAmount);
            if (cfg.QtyTimesStagePct) qty *= l.StagePct == 0 ? 1 : l.StagePct;
            var code = (l.BoqCode ?? "").Trim().ToUpperInvariant();
            boq.TryGetValue(code, out var b);
            if (code.Length == 0) res.Issues.Add($"Row {l.RowOrder} item {l.ItemNo}: no BOQ code - E-Promise cannot book it.");
            else if (b is null) res.Issues.Add($"Row {l.RowOrder}: BOQ code {code} is not in the E-Promise budget list (import the 'E promise - Resource' sheet).");
            if (FirstNonEmpty(l.CostCode, b?.CostCode).Length == 0) res.Issues.Add($"Row {l.RowOrder} {code}: no cost code (Activity).");
            if (FirstNonEmpty(l.BudgetResourceCode, b?.BudgetResourceCode).Length == 0) res.Issues.Add($"Row {l.RowOrder} {code}: no budget resource code.");
            raw.Add((l, code, b, qty, amount, l.PrevQty, l.CumQty, l.CumAmount));
        }

        if (cfg.GroupByBoqCode)
        {
            foreach (var g in raw.GroupBy(r => (r.Code, Cost: FirstNonEmpty(r.Line.CostCode, r.Boq?.CostCode), Res: FirstNonEmpty(r.Line.BudgetResourceCode, r.Boq?.BudgetResourceCode))))
            {
                var first = g.First();
                var qty = g.Sum(x => x.Qty);
                var amount = g.Sum(x => x.Amount);
                var rate = Math.Abs(qty) > 1e-9 ? amount / qty : first.Line.Rate;
                res.Rows.Add(Row(cfg, inv, contract, invoiceRef, first.Line, first.Code, first.Boq, qty, rate, amount, g.Sum(x => x.Prev), g.Sum(x => x.Cum), g.Sum(x => x.CumAmount),
                    string.Join(", ", g.Select(x => x.Line.ItemNo).Distinct())));
            }
        }
        else
        {
            foreach (var r in raw)
            {
                var rate = cfg.QtyTimesStagePct ? r.Line.Rate : r.Line.Rate * (r.Line.StagePct == 0 ? 1 : r.Line.StagePct);
                res.Rows.Add(Row(cfg, inv, contract, invoiceRef, r.Line, r.Code, r.Boq, r.Qty, rate, r.Amount, r.Prev, r.Cum, r.CumAmount, r.Line.ItemNo));
            }
        }
        if (res.Rows.Count == 0) res.Issues.Add($"{inv.Title} has no line with a {(cum ? "cumulative" : "current")} quantity.");
        if (cfg.JobNo.Length == 0 && res.Rows.Any(r => string.IsNullOrEmpty(r[EPromiseFields.JobNo] as string)))
            res.Issues.Add("No job number: set JobNo in epromise-export.json or import the E-Promise list (column Job).");
        return res;
    }

    private static EPromiseRow Row(EPromiseExportConfig cfg, SubInvoice inv, Contract? contract, string invoiceRef, SubInvoiceLine l, string code, BoqItem? b,
        double qty, double rate, double amount, double prev, double cum, double cumAmount, string itemNo)
    {
        var (bill, section, page, rev, item) = Split(code);
        var cost = FirstNonEmpty(l.CostCode, b?.CostCode);
        var row = new EPromiseRow();
        var v = row.Values;
        v[EPromiseFields.JobNo] = FirstNonEmpty(b?.Job, cfg.JobNo);
        v[EPromiseFields.Bill] = FirstNonEmpty(b?.Bill, bill);
        v[EPromiseFields.Section] = section;
        v[EPromiseFields.Page] = page;
        v[EPromiseFields.Rev] = rev;
        v[EPromiseFields.Item] = item;
        v[EPromiseFields.BoqCode] = code;
        v[EPromiseFields.BoqDescription] = FirstNonEmpty(b?.Description, l.BoqDescription, l.Description);
        v[EPromiseFields.Description] = l.Description;
        v[EPromiseFields.Wbs] = b?.Wbs ?? "";
        v[EPromiseFields.Activity] = cost;
        v[EPromiseFields.CostCode] = cost;
        v[EPromiseFields.BudgetResourceCode] = FirstNonEmpty(l.BudgetResourceCode, b?.BudgetResourceCode);
        v[EPromiseFields.BudgetResource] = b?.BudgetResource ?? "";
        v[EPromiseFields.Unit] = l.Unit;
        v[EPromiseFields.Qty] = Math.Round(qty, 4);
        v[EPromiseFields.Rate] = Math.Round(rate, 4);
        v[EPromiseFields.StagePct] = l.StagePct;
        v[EPromiseFields.Amount] = Math.Round(amount, cfg.AmountDecimals);
        v[EPromiseFields.PrevQty] = Math.Round(prev, 4);
        v[EPromiseFields.CumQty] = Math.Round(cum, 4);
        v[EPromiseFields.CumAmount] = Math.Round(cumAmount, cfg.AmountDecimals);
        v[EPromiseFields.ContractQty] = l.ContractQty;
        v[EPromiseFields.ItemNo] = itemNo;
        v[EPromiseFields.ContractNo] = inv.ContractNo;
        v[EPromiseFields.Subcontractor] = inv.Subcontractor;
        v[EPromiseFields.VendorNo] = contract?.VendorNo ?? "";
        v[EPromiseFields.InvoiceNo] = (double)inv.InvoiceNo;
        v[EPromiseFields.Revision] = (double)inv.Revision;
        v[EPromiseFields.InvoiceRef] = invoiceRef;
        v[EPromiseFields.PeriodTo] = inv.PeriodTo;
        v[EPromiseFields.AconexWorkflowNo] = inv.AconexWorkflowNo;
        return row;
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";

    private static object? Value(EPromiseRow r, EPromiseColumn c) => c.Field == EPromiseFields.Constant ? c.Constant : r[c.Field];

    /// <summary>Excel in the import layout: header row 1 (#A6A6A6, bold black), one row per ERP line, no title rows, no formulas.</summary>
    public static void WriteXlsx(string path, EPromiseExportResult res, EPromiseExportConfig cfg)
    {
        if (res.Blocked) throw new InvalidOperationException(res.Issues.FirstOrDefault() ?? "Export blocked.");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheet(cfg.SheetName));
        for (var i = 0; i < cfg.Columns.Count; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = cfg.Columns[i].Header;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#A6A6A6");
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.Black;
        }
        var r = 2;
        foreach (var row in res.Rows)
        {
            for (var i = 0; i < cfg.Columns.Count; i++)
            {
                var col = cfg.Columns[i];
                var cell = ws.Cell(r, i + 1);
                switch (Value(row, col))
                {
                    case double d:
                        cell.Value = d;
                        cell.Style.NumberFormat.Format = col.Field is EPromiseFields.Amount or EPromiseFields.CumAmount ? "#,##0.00" : "0.####";
                        break;
                    case DateTime dt: cell.Value = dt; cell.Style.NumberFormat.Format = "dd-mmm-yyyy"; break;
                    case null: break;
                    case var o:
                        cell.Value = Convert.ToString(o, CultureInfo.InvariantCulture);
                        cell.Style.NumberFormat.Format = "@";   // codes like 01 / 00 stay text
                        break;
                }
            }
            r++;
        }
        ws.SheetView.FreezeRows(1);
        if (res.Rows.Count > 0) ws.Range(1, 1, r - 1, cfg.Columns.Count).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(r, 200));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        wb.SaveAs(path);
    }

    /// <summary>CSV with the same columns (invariant numbers, quoted text when needed, optional UTF-8 BOM for Excel).</summary>
    public static void WriteCsv(string path, EPromiseExportResult res, EPromiseExportConfig cfg)
    {
        if (res.Blocked) throw new InvalidOperationException(res.Issues.FirstOrDefault() ?? "Export blocked.");
        var d = cfg.CsvDelimiter.Length == 1 ? cfg.CsvDelimiter[0] : ',';
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(d, cfg.Columns.Select(c => Csv(c.Header, d))));
        foreach (var row in res.Rows)
            sb.AppendLine(string.Join(d, cfg.Columns.Select(c => Csv(Value(row, c) switch
            {
                double x => x.ToString("0.########", CultureInfo.InvariantCulture),
                DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                null => "",
                var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "",
            }, d))));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(cfg.CsvUtf8Bom));
    }

    private static string Csv(string s, char d) =>
        s.IndexOfAny(new[] { d, '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string SafeSheet(string name)
    {
        var n = new string((name ?? "E promise").Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        return n.Length == 0 ? "E promise" : n.Length > 31 ? n[..31] : n;
    }
}
