using Raffaello.Core.Domain;
using Raffaello.Core.Import;

namespace Raffaello.Core.Ledger;

/// <summary>Executed cumulative quantity per invoice row ("item|code") of one past invoice.</summary>
public sealed record InvoiceCum(int InvoiceNo, IReadOnlyDictionary<string, double> CumByRow);

/// <summary>Per row check: what the files say vs what the cumulative ledger lines hold.</summary>
public sealed record SplitRowCheck(string RowKey, int Lines, double LedgerPlanQty, double LedgerInvoiceQty, double FileCum, IReadOnlyDictionary<int, double> PlanQtyByInvoice, string Status);

public sealed class SplitResult
{
    public string Subcontractor { get; init; } = "";
    public int CumulativeInvoice { get; init; }
    public List<ClaimLine> NewLines { get; } = new();
    public List<ClaimLine> Replaced { get; } = new();
    public List<ClaimLine> NotSplit { get; } = new();
    public List<SplitRowCheck> Rows { get; } = new();
    public List<ImportIssue> Issues { get; } = new();
    public string Summary => $"{Subcontractor} INV {CumulativeInvoice} cumulative: {Replaced.Count} lines split into {NewLines.Count} lines over invoices " +
                             $"{string.Join(", ", NewLines.Select(l => l.InvoiceNo).Distinct().OrderBy(n => n))}; {NotSplit.Count} lines left cumulative";
}

/// <summary>
/// Splits a subcontractor's cumulative invoice block (ledger lines flagged IsCumulative) into invoices 1..N using the executed
/// cumulative quantities of his past invoice files:
/// per invoice row, current(n) = cum(n) - cum(n-1); each row's ledger lines receive current(n) / cum(N) of their quantity,
/// rounded with the largest-remainder method so every invoice total and every line total reconcile exactly
/// (sum over the invoices of a line's new lines = the cumulative line).
/// Lines whose row has no file quantities, or that carry height / length checks, stay cumulative (reported).
/// </summary>
public static class CumulativeSplit
{
    public const string SplitSource = "SPLIT";

    /// <summary>Cumulative blocks still waiting for the invoice files: (sub, invoice no, lines, invoice qty).</summary>
    public static List<(string Sub, int InvoiceNo, int Lines, double Qty)> Pending(IEnumerable<ClaimLine> claims) =>
        claims.Where(c => c.IsCumulative && !c.ReplacedBySplit).GroupBy(c => (c.Subcontractor, c.InvoiceNo))
            .Select(g => (g.Key.Subcontractor, g.Key.InvoiceNo, g.Count(), g.Sum(c => c.QtyAfterWir))).OrderBy(x => x.Subcontractor).ToList();

    /// <summary>"CONCRETE PLUS: INV 1-9 CUMULATIVE (1,511 lines) - awaiting invoice files to split".</summary>
    public static string Banner(IEnumerable<ClaimLine> claims) =>
        string.Join("   |   ", Pending(claims).Select(p => $"{p.Sub}: INV 1-{p.InvoiceNo} CUMULATIVE ({p.Lines:N0} lines) - awaiting invoice files to split"));

    /// <summary>Invoice label for a ledger line: "1-9 CUM" for an unsplit cumulative line.</summary>
    public static string InvoiceLabel(ClaimLine c) => c.IsCumulative && !c.ReplacedBySplit ? $"1-{c.InvoiceNo} CUM" : c.ReplacedBySplit ? $"{c.InvoiceNo} (SPLIT)" : c.InvoiceNo.ToString();

    public static SplitResult Split(IEnumerable<ClaimLine> ledger, string subcontractor, IReadOnlyList<InvoiceCum> invoices, Func<ClaimLine, string?> rowKeyOf)
    {
        var sub = subcontractor.Trim().ToUpperInvariant();
        var block = ledger.Where(c => c.IsCumulative && !c.ReplacedBySplit && c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase)).ToList();
        var n = block.Count == 0 ? 0 : block.Max(c => c.InvoiceNo);
        var res = new SplitResult { Subcontractor = sub, CumulativeInvoice = n };
        if (block.Count == 0) { res.Issues.Add(new(0, IssueLevel.Error, $"{sub} has no cumulative ledger lines to split.")); return res; }
        if (block.Any(c => c.InvoiceNo != n)) res.Issues.Add(new(0, IssueLevel.Warning, $"{sub}: cumulative lines on more than one invoice - only INV {n} is split."));
        block = block.Where(c => c.InvoiceNo == n).ToList();

        var files = invoices.Where(i => i.InvoiceNo >= 1 && i.InvoiceNo <= n).GroupBy(i => i.InvoiceNo).Select(g => g.Last()).OrderBy(i => i.InvoiceNo).ToList();
        if (files.All(f => f.InvoiceNo != n)) { res.Issues.Add(new(0, IssueLevel.Error, $"The invoice file for INV {n} (the cumulative one) is missing - nothing split.")); res.NotSplit.AddRange(block); return res; }
        foreach (var missing in Enumerable.Range(1, n).Except(files.Select(f => f.InvoiceNo)))
            res.Issues.Add(new(0, IssueLevel.Warning, $"INV {missing} file missing - its quantities fall into the next invoice that has a file."));

        foreach (var c in block.Where(c => c.QtyAbove45 != 0 || c.LengthApplies))
            res.Issues.Add(new(0, IssueLevel.Warning, $"{c.Room} {c.Stage} {c.Item}: has a height / length check - left cumulative."));
        var groups = block.Where(c => c.QtyAbove45 == 0 && !c.LengthApplies).GroupBy(c => rowKeyOf(c) ?? "").ToList();
        res.NotSplit.AddRange(block.Where(c => c.QtyAbove45 != 0 || c.LengthApplies));

        foreach (var g in groups.OrderBy(g => g.Key))
        {
            var lines = g.ToList();
            if (g.Key.Length == 0)
            {
                res.NotSplit.AddRange(lines);
                res.Issues.Add(new(0, IssueLevel.Warning, $"{lines.Count} lines not mapped to an invoice row ({string.Join(", ", lines.Select(l => $"{l.Stage} {l.Item}").Distinct().Take(5))}) - left cumulative."));
                continue;
            }
            var cums = files.Select(f => (f.InvoiceNo, Cum: f.CumByRow.GetValueOrDefault(g.Key))).ToList();
            var total = cums.Last().Cum;
            var plan = lines.Sum(l => l.Qty);
            var inv = lines.Sum(l => l.QtyAfterWir);
            if (Math.Abs(total) < 1e-9)
            {
                res.NotSplit.AddRange(lines);
                res.Rows.Add(new(g.Key, lines.Count, plan, inv, total, new Dictionary<int, double>(), "NO FILE QTY"));
                res.Issues.Add(new(0, IssueLevel.Warning, $"Row {g.Key}: no executed quantity in INV {n} file - {lines.Count} lines left cumulative."));
                continue;
            }
            var prev = 0.0;
            var shares = new List<(int Inv, double Share)>();
            var bad = false;
            foreach (var (no, cum) in cums)
            {
                var cur = cum - prev;
                if (cur < -1e-6) { bad = true; res.Issues.Add(new(0, IssueLevel.Error, $"Row {g.Key}: cumulative drops from {prev:0.##} to {cum:0.##} at INV {no} - not split.")); }
                if (Math.Abs(cur) > 1e-9) shares.Add((no, cur / total));
                prev = cum;
            }
            if (bad) { res.NotSplit.AddRange(lines); continue; }
            var status = Math.Abs(inv - total) <= Math.Max(0.01, Math.Abs(total) * 1e-6) ? "OK" : "CHECK";
            if (status == "CHECK")
                res.Issues.Add(new(0, IssueLevel.Warning, $"Row {g.Key}: ledger lines hold {inv:0.##} (after site % x WIR %) but INV {n} file says cum {total:0.##} - split by the file's proportions."));

            var allocations = Allocate(lines, shares);
            var byInvoice = new Dictionary<int, double>();
            foreach (var (line, perInvoice) in allocations)
            {
                foreach (var (no, qty) in perInvoice)
                {
                    if (Math.Abs(qty) < 1e-12) continue;
                    var c = LedgerRules.Copy(line);
                    c.Id = 0;
                    c.InvoiceNo = no;
                    c.Qty = qty;
                    c.IsCumulative = false;
                    c.ReplacedBySplit = false;
                    c.Source = SplitSource;
                    c.SourceKey = $"{SplitSource}|{line.SourceKey}|{line.Id}|INV{no}";
                    c.Notes = $"INV-{no} of cumulative INV-{n} (split by invoice files, row {g.Key}). {line.Notes}".Trim();
                    c.EnteredAt = DateTime.Now;
                    res.NewLines.Add(c);
                    byInvoice[no] = byInvoice.GetValueOrDefault(no) + qty;
                }
                res.Replaced.Add(line);
            }
            res.Rows.Add(new(g.Key, lines.Count, plan, inv, total, byInvoice.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 6)), status));
        }
        return res;
    }

    /// <summary>
    /// Largest-remainder allocation. Works in whole units (0.01 when any quantity has decimals). Invoice targets are rounded on the
    /// cumulative share so they add up to the total; each invoice is shared over the lines in proportion to what they still hold;
    /// the last invoice takes every line's remainder, so line totals are exact.
    /// </summary>
    public static List<(ClaimLine Line, List<(int Inv, double Qty)> PerInvoice)> Allocate(IReadOnlyList<ClaimLine> lines, IReadOnlyList<(int Inv, double Share)> shares)
    {
        var unit = lines.All(l => Math.Abs(l.Qty - Math.Round(l.Qty)) < 1e-9) ? 1.0 : 0.01;
        var rem = lines.Select(l => (long)Math.Round(l.Qty / unit, MidpointRounding.AwayFromZero)).ToArray();
        var total = rem.Sum();
        var result = lines.Select(l => (Line: l, PerInvoice: new List<(int Inv, double Qty)>())).ToList();
        var cumShare = 0.0;
        long done = 0;
        for (var k = 0; k < shares.Count; k++)
        {
            var (inv, share) = shares[k];
            if (k == shares.Count - 1)
            {
                for (var i = 0; i < lines.Count; i++) { result[i].PerInvoice.Add((inv, rem[i] * unit)); rem[i] = 0; }
                break;
            }
            cumShare += share;
            var target = (long)Math.Round(total * cumShare, MidpointRounding.AwayFromZero) - done;
            var remTotal = rem.Sum();
            if (remTotal == 0 || target == 0) { continue; }
            target = Math.Clamp(target, Math.Min(0, remTotal), Math.Max(0, remTotal));
            var ideal = rem.Select(r => (double)r * target / remTotal).ToArray();
            var take = ideal.Select(x => (long)Math.Floor(x)).ToArray();
            var left = target - take.Sum();
            foreach (var i in Enumerable.Range(0, lines.Count).OrderByDescending(i => ideal[i] - take[i]).ThenBy(i => i).Take((int)Math.Max(0, left)))
                take[i]++;
            for (var i = 0; i < lines.Count; i++)
            {
                if (take[i] != 0) result[i].PerInvoice.Add((inv, take[i] * unit));
                rem[i] -= take[i];
            }
            done += target;
        }
        return result;
    }
}
