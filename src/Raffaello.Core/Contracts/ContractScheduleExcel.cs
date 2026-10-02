using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Contracts;

/// <summary>
/// A contract rate schedule from Excel - the Arabic contract workbook (رقم / التوصيف / الوحدة / الكمية / سعر الوحدة / الاجمالي)
/// or an English one (Sr / Description / Unit / Qty / Rate / Amount). Columns are found from the header row, so the same reader
/// takes either layout. Used to cross-check a signed contract PDF against the Excel and by the reading benchmark.
/// </summary>
public static class ContractScheduleExcel
{
    public sealed class Result
    {
        public List<ContractItem> Items { get; } = new();
        public List<string> Sections { get; } = new();
        public double? StatedTotal { get; set; }
        public string Sheet { get; set; } = "";
        public Dictionary<string, int> Columns { get; } = new();
        public List<ImportIssue> Issues { get; } = new();
        /// <summary>Total of each item as printed in the total column (formula results), for the check qty x rate = total.</summary>
        public Dictionary<string, double> PrintedTotals { get; } = new();
    }

    public static Result Read(string path, string contractNo = "")
    {
        using var x = new XlsxStreamReader(path);
        var res = new Result();
        foreach (var sheet in x.Sheets.Keys)
        {
            var rows = x.ReadRows(sheet, 4000).ToList();
            var header = rows.Take(30).FirstOrDefault(r => Roles(r).Count >= 4);
            if (header is null) continue;
            res.Sheet = sheet;
            foreach (var kv in Roles(header)) res.Columns[kv.Key] = kv.Value;
            var section = "";
            foreach (var r in rows.Where(r => r.Number > header.Number))
            {
                string G(string role) => res.Columns.TryGetValue(role, out var c) ? r.Get(c).Trim() : "";
                double? N(string role) => res.Columns.TryGetValue(role, out var c) ? r.Number_(c) ?? ArabicText.ParseNumber(r.Get(c)) : null;
                var no = G("NO"); var desc = G("DESC");
                if (no.Length == 0 && desc.Length == 0) continue;
                var normDesc = ArabicText.Normalize(desc);
                if (no.Length == 0)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(normDesc, @"الاجمالي|^TOTAL|GRAND") && N("TOTAL") is double gt) { res.StatedTotal = gt; continue; }
                    if (N("QTY") is null && N("RATE") is null && desc.Length > 0) { section = desc; res.Sections.Add(desc); }
                    continue;
                }
                if (!no.Any(char.IsDigit))
                {
                    // "الاجمالي" / TOTAL written in the number column: the grand total row
                    if (System.Text.RegularExpressions.Regex.IsMatch(ArabicText.Normalize(no + " " + desc), @"الاجمالي|TOTAL|GRAND"))
                        res.StatedTotal = N("TOTAL") ?? N("RATE") ?? res.StatedTotal;
                    continue;
                }
                var itemNo = ContractLinkImporter.NormalizeItemNo(no);
                var item = new ContractItem
                {
                    ContractNo = contractNo, ItemNo = itemNo, Order = res.Items.Count + 1, Section = section, Description = desc,
                    Unit = G("UNIT"), Qty = N("QTY") ?? 0, Rate = N("RATE") ?? 0,
                };
                if (N("TOTAL") is double t) res.PrintedTotals[itemNo] = t;
                res.Items.Add(item);
            }
            if (res.Items.Count > 0) break;
        }
        if (res.Items.Count == 0) res.Issues.Add(new(0, Import.IssueLevel.Error, "No rate schedule (No / Description / Unit / Qty / Rate header) found in the workbook."));
        return res;
    }

    private static Dictionary<string, int> Roles(XlsxRow r)
    {
        var res = new Dictionary<string, int>();
        foreach (var (col, raw) in r.Values)
        {
            var t = ArabicText.Normalize(raw);
            if (t.Length == 0 || t.Length > 30) continue;
            string? role = t.Contains("سعر") || t is "RATE" or "UNIT RATE" or "PRICE" or "UNIT PRICE" ? "RATE"
                : t.Contains("الاجمالي") || t is "TOTAL" or "AMOUNT" or "TOTAL PRICE" ? "TOTAL"
                : t.Contains("الكميه") || t is "QTY" or "QTY." or "QUANTITY" ? "QTY"
                : t.Contains("الوحده") || t is "UNIT" or "UOM" ? "UNIT"
                : t.Contains("التوصيف") || t.Contains("البيان") || t is "DESCRIPTION" ? "DESC"
                : t is "رقم" or "م" or "SR" or "SR." or "NO" or "NO." or "ITEM" or "S/N" ? "NO"
                : null;
            if (role != null && !res.ContainsKey(role)) res[role] = col;
        }
        return res;
    }
}
