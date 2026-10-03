using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Contracts;

public static class BoqCodes
{
    /// <summary>Owner BOQ code: Bill-Section-Page-Rev-Item, e.g. B6-01-01-00-6-26-V-5.</summary>
    public static readonly Regex Pattern = new(@"^B\d+-\d{2}-\d{2}-\d{2}-[0-9A-Z]+(-[0-9A-Z]+)*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static bool IsCode(string? s) => !string.IsNullOrWhiteSpace(s) && Pattern.IsMatch(s.Trim());
    public static string Bill(string code) => code.Split('-')[0].ToUpperInvariant();

    /// <summary>Bills B5 / B6 are the branded residences, B2 / B3 the hotel (from the link tables).</summary>
    public static string BuildingOf(IEnumerable<string> codes)
    {
        var bills = codes.Select(Bill).ToList();
        var branded = bills.Count(b => b is "B5" or "B6");
        var hotel = bills.Count(b => b is "B2" or "B3");
        return branded >= hotel ? Buildings.Branded : Buildings.Hotel;
    }
}

public sealed class ContractImportResult
{
    public string FileName { get; init; } = "";
    public Contract Contract { get; init; } = new();
    public List<ContractItem> Items { get; } = new();
    public List<ContractItemBoq> Links { get; } = new();
    public List<string> Sections { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public int ItemsWithoutCodes => Items.Count(i => Links.All(l => l.ItemNo != i.ItemNo));
    public string Summary => $"{Items.Count} contract items in {Sections.Count} sections, {Links.Count} BOQ code links " +
        $"({Links.Select(l => l.BoqCode).Distinct().Count()} distinct codes, {Links.Count(l => l.BoqDescription.Length > 0)} with BOQ description), " +
        $"{ItemsWithoutCodes} items without codes";
}

/// <summary>
/// The contract link workbook (English schedule): item rows (A = Sr, B = description, C = unit, D = qty, E = rate) each followed
/// by rows whose B is an owner BOQ code (G = BOQ description in the hotel file). Section rows have only B text.
/// </summary>
public static class ContractLinkImporter
{
    public static ContractImportResult Read(string path, string contractNo, string subcontractor, string? building = null)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = x.Sheets.Keys.First();
        var res = new ContractImportResult
        {
            FileName = path,
            Contract = new Contract { ContractNo = contractNo, Subcontractor = subcontractor.Trim().ToUpperInvariant(), SourceFile = Path.GetFileName(path), Status = "ACTIVE", RetentionPct = 0.10 },
        };
        var section = "";
        ContractItem? current = null;
        var order = 0;
        var started = false;
        foreach (var r in x.ReadRows(sheet))
        {
            var a = r.Get("A").Trim();
            var b = r.Get("B").Trim();
            if (!started)
            {
                if (a.Equals("Sr.", StringComparison.OrdinalIgnoreCase) || a.Equals("Sr", StringComparison.OrdinalIgnoreCase) || a == "رقم") started = true;
                continue;
            }
            if (b.Length == 0 && a.Length == 0) continue;
            if (BoqCodes.IsCode(b))
            {
                if (current is null) { res.Issues.Add(new(r.Number, IssueLevel.Warning, $"BOQ code {b} before any item - skipped.")); continue; }
                res.Links.Add(new ContractItemBoq
                {
                    ContractNo = contractNo, ItemNo = current.ItemNo, BoqCode = b.ToUpperInvariant(), BoqDescription = r.Get("G").Trim(),
                    Order = res.Links.Count(l => l.ItemNo == current.ItemNo), Source = "LINK TABLE",
                });
                continue;
            }
            if (a.Length == 0)
            {
                section = b;
                if (!res.Sections.Contains(section)) res.Sections.Add(section);
                continue;
            }
            var item = new ContractItem
            {
                ContractNo = contractNo, ItemNo = NormalizeItemNo(a), Order = ++order, Section = section, Description = b,
                Unit = r.Get("C").Trim(), Qty = r.Num("D") ?? 0, Rate = r.Num("E") ?? 0,
            };
            ContractAttributeParser.Apply(item, ContractAttributeParser.Parse(item.Description, item.Unit));
            item.StagePct = ContractAttributeParser.DefaultStagePct(item);
            if (res.Items.Any(i => i.ItemNo == item.ItemNo)) res.Issues.Add(new(r.Number, IssueLevel.Warning, $"Item {item.ItemNo} appears twice."));
            res.Items.Add(item);
            current = item;
        }
        if (!started) res.Issues.Add(new(0, IssueLevel.Error, "Header row 'Sr.' not found."));
        var inferred = ContractAttributeParser.InferHeightPairs(res.Items);
        if (inferred > 0) res.Issues.Add(new(0, IssueLevel.Warning, $"{inferred} items had no height text - LOW/HIGH inferred from rate pairs."));
        res.Contract.Building = building ?? BoqCodes.BuildingOf(res.Links.Select(l => l.BoqCode));
        res.Contract.Value = Math.Round(res.Items.Sum(i => i.Qty * i.Rate), 2);
        res.Contract.Scope = string.Join(", ", res.Sections.Take(6));
        return res;
    }

    public static string NormalizeItemNo(string s)
    {
        s = s.Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(d - Math.Round(d)) < 1e-9
            ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture) : s;
    }

    /// <summary>Replaces the contract (by number) with the imported items and links; keeps learned mapping rules.</summary>
    public static void Commit(ContractImportResult res, IProjectStore store)
    {
        var no = res.Contract.ContractNo;
        var oldItems = store.All<ContractItem>().Where(i => i.ContractNo == no).ToList();
        var oldLinks = store.All<ContractItemBoq>().Where(l => l.ContractNo == no && l.Source == "LINK TABLE").ToList();
        var oldContract = store.All<Contract>().FirstOrDefault(c => c.ContractNo == no);
        // keep user-confirmed attributes
        var confirmed = oldItems.Where(i => i.AttributesConfirmed).ToDictionary(i => i.ItemNo);
        foreach (var i in res.Items)
            if (confirmed.TryGetValue(i.ItemNo, out var c))
            {
                i.FixStage = c.FixStage; i.ConduitType = c.ConduitType; i.Mount = c.Mount; i.HeightBand = c.HeightBand; i.Is2ndFixPulling = c.Is2ndFixPulling;
                i.Systems = c.Systems; i.Category = c.Category; i.SizeKey = c.SizeKey; i.StagePct = c.StagePct; i.AttributesConfirmed = true;
            }
            else if (oldItems.FirstOrDefault(o => o.ItemNo == i.ItemNo) is { StagePct: > 0 } prev) i.StagePct = prev.StagePct;
        store.Batch(w =>
        {
            foreach (var i in oldItems) w.Delete(i);
            foreach (var l in oldLinks) w.Delete(l);
            if (oldContract != null)
            {
                oldContract.Subcontractor = res.Contract.Subcontractor; oldContract.Building = res.Contract.Building; oldContract.Value = res.Contract.Value;
                oldContract.Scope = res.Contract.Scope; oldContract.SourceFile = res.Contract.SourceFile;
                w.Update(oldContract);
            }
            else w.Insert(res.Contract);
            w.InsertMany(res.Items);
            w.InsertMany(res.Links);
        }, $"Contract {no} ({res.Contract.Subcontractor}): {res.Items.Count} items, {res.Links.Count} BOQ links from {Path.GetFileName(res.FileName)}");
    }
}

public sealed class EPromiseImportResult
{
    public List<BoqItem> Items { get; } = new();
    public int RowsRead { get; set; }
    public int Duplicates { get; set; }
    public string Summary => $"{RowsRead} budget rows, {Items.Count} distinct BOQ codes ({Duplicates} repeated rows), bills {string.Join(" ", Items.GroupBy(i => i.Bill).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}";
}

/// <summary>MOBCO ERP budget list ('E promise - Resource'): Job, Bill, Section, Page, Rev, Item, BOQ No, Description, WBS, Activity, Budget Resource Code, Budget Resource.</summary>
public static class EPromiseImporter
{
    public static string? FindSheet(XlsxStreamReader x) => x.FindSheet(n => n.Contains("promise", StringComparison.OrdinalIgnoreCase));

    public static EPromiseImportResult Read(string path)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = FindSheet(x) ?? throw new InvalidDataException("No 'E promise' sheet in this workbook.");
        var res = new EPromiseImportResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in x.ReadRows(sheet))
        {
            if (r.Number == 1) continue;
            var bill = r.Get("B").Trim();
            if (bill.Length == 0) continue;
            res.RowsRead++;
            var code = r.Get("G").Trim();
            if (!BoqCodes.IsCode(code)) code = $"{bill}-{r.Get("C").Trim()}-{r.Get("D").Trim()}-{r.Get("E").Trim()}-{r.Get("F").Trim()}";
            if (!seen.Add(code)) { res.Duplicates++; continue; }
            res.Items.Add(new BoqItem
            {
                ItemCode = code.ToUpperInvariant(), Bill = bill, Description = r.Get("H").Trim(), Job = r.Get("A").Trim(), Wbs = r.Get("I").Trim(),
                CostCode = r.Get("J").Trim(), BudgetResourceCode = r.Get("K").Trim(), BudgetResource = r.Get("L").Trim(), Unit = "", Stage = "",
                Rate = r.Num("R") ?? 0,
            });
        }
        return res;
    }

    public static int Commit(EPromiseImportResult res, IProjectStore store)
    {
        var existing = store.All<BoqItem>().GroupBy(b => b.ItemCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var n = 0;
        store.Batch(w =>
        {
            foreach (var b in res.Items)
            {
                if (existing.TryGetValue(b.ItemCode, out var old))
                {
                    if (old.Description == b.Description && old.CostCode == b.CostCode && old.BudgetResourceCode == b.BudgetResourceCode) continue;
                    old.Description = b.Description; old.Bill = b.Bill; old.Job = b.Job; old.Wbs = b.Wbs; old.CostCode = b.CostCode;
                    old.BudgetResourceCode = b.BudgetResourceCode; old.BudgetResource = b.BudgetResource;
                    w.Update(old);
                }
                else w.Insert(b);
                n++;
            }
        }, $"Project code list: {n} project codes added / updated");
        return n;
    }
}

public sealed class TemplateImportResult
{
    public string FileName { get; init; } = "";
    public string SheetName { get; set; } = "";
    public string VendorName { get; set; } = "";
    public List<InvoiceTemplateRow> Rows { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public int ItemRows => Rows.Count(r => r.Kind == "ITEM");
    public int LastLineRow { get; set; }
    public string Summary => $"{SheetName}: {Rows.Count} rows ({ItemRows} item x BOQ rows, {Rows.Select(r => r.ItemNo).Where(s => s.Length > 0).Distinct().Count()} items, " +
                             $"{Rows.Select(r => r.BoqCode).Where(s => s.Length > 0).Distinct().Count()} BOQ codes), vendor {VendorName}, lines end at row {LastLineRow}";
}

/// <summary>
/// The subcontract invoice template ('ROOTS INV 1' layout): header rows 1-12, lines from row 14 (B item, C BOQ code, D cost code,
/// E budget resource code, F description, G unit, H qty, I rate, J stage %, Q executed cum) up to the 'Subcontract Value' row.
/// </summary>
public static class InvoiceTemplateImporter
{
    public static string? FindSheet(XlsxStreamReader x) =>
        x.FindSheet(n => Regex.IsMatch(n, @"\bINV\b", RegexOptions.IgnoreCase) && !n.Contains("QTY", StringComparison.OrdinalIgnoreCase))
        ?? x.FindSheet(n => n.Contains("INV", StringComparison.OrdinalIgnoreCase) && !n.Contains("QTY", StringComparison.OrdinalIgnoreCase));

    public static TemplateImportResult Read(string path, string contractNo, string? sheetName = null)
    {
        using var x = new XlsxStreamReader(path);
        var sheet = sheetName ?? FindSheet(x) ?? throw new InvalidDataException("No invoice sheet found.");
        var res = new TemplateImportResult { FileName = path, SheetName = sheet };
        var order = 0;
        foreach (var r in x.ReadRows(sheet))
        {
            if (r.Number == 2) res.VendorName = r.Get("M").Trim();
            if (r.Number < 14) continue;
            var c = r.Get("C").Trim();
            if (c.Contains("Subcontract Value", StringComparison.OrdinalIgnoreCase)) break;
            var b = r.Get("B").Trim();
            var f = r.Get("F").Trim();
            if (b.Length == 0 && c.Length == 0 && f.Length == 0) continue;
            var row = new InvoiceTemplateRow
            {
                ContractNo = contractNo, RowOrder = ++order, ItemNo = b.Length > 0 ? ContractLinkImporter.NormalizeItemNo(b) : "",
                BoqCode = BoqCodes.IsCode(c) ? c.ToUpperInvariant() : "", CostCode = r.Get("D").Trim(), BudgetResourceCode = r.Get("E").Trim(),
                Description = f, Unit = r.Get("G").Trim(), Qty = r.Num("H") ?? 0, Rate = r.Num("I") ?? 0, StagePct = r.Num("J") ?? 0,
                ImportedExecutedCum = r.Num("Q") ?? 0, ImportedExecutedPrev = r.Num("O") ?? 0, ImportedExecutedCurr = r.Num("P") ?? 0,
            };
            row.Kind = row.ItemNo.Length > 0 ? "ITEM" : c.Length > 0 && !BoqCodes.IsCode(c) ? "SECTION" : "NOTE";
            if (row.Kind == "SECTION") row.Description = c;
            res.Rows.Add(row);
            res.LastLineRow = r.Number;
        }
        if (res.Rows.Count == 0) res.Issues.Add(new(0, IssueLevel.Error, $"No lines found in '{sheet}' from row 15."));
        return res;
    }

    public static void Commit(TemplateImportResult res, IProjectStore store, string contractNo)
    {
        var old = store.All<InvoiceTemplateRow>().Where(r => r.ContractNo == contractNo).ToList();
        var items = store.All<ContractItem>().Where(i => i.ContractNo == contractNo).ToDictionary(i => i.ItemNo);
        var links = store.All<ContractItemBoq>().Where(l => l.ContractNo == contractNo).Select(l => (l.ItemNo, l.BoqCode)).ToHashSet();
        var boqDesc = store.All<BoqItem>().GroupBy(b => b.ItemCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);
        store.Batch(w =>
        {
            foreach (var o in old) w.Delete(o);
            w.InsertMany(res.Rows);
            // stage % from the template; template-only item x code pairs become links too
            foreach (var g in res.Rows.Where(r => r.Kind == "ITEM").GroupBy(r => r.ItemNo))
            {
                if (items.TryGetValue(g.Key, out var it) && g.First().StagePct > 0 && Math.Abs(it.StagePct - g.First().StagePct) > 1e-9)
                {
                    it.StagePct = g.First().StagePct;
                    w.Update(it);
                }
                var n = 100;
                foreach (var r in g.Where(r => r.BoqCode.Length > 0 && !links.Contains((r.ItemNo, r.BoqCode))))
                {
                    links.Add((r.ItemNo, r.BoqCode));
                    w.Insert(new ContractItemBoq { ContractNo = contractNo, ItemNo = r.ItemNo, BoqCode = r.BoqCode, BoqDescription = boqDesc.GetValueOrDefault(r.BoqCode, ""), Order = n++, Source = "TEMPLATE" });
                }
            }
        }, $"Invoice template {res.SheetName}: {res.Rows.Count} rows for {contractNo}");
    }
}
