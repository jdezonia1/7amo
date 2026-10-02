using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Mapping;

namespace Raffaello.Core.Invoicing;

public sealed class InvoiceBuild
{
    public required SubInvoice Header { get; init; }
    public List<SubInvoiceLine> Lines { get; init; } = new();
    public MappingResult Mapping { get; init; } = new();
    public List<string> Warnings { get; } = new();
    public SubInvoice? PreviousApproved { get; init; }
    public InvoiceTotals Totals => InvoiceTotals.Of(Header, Lines);
}

/// <summary>Footer totals in the template layout (gross certified, retention, advance, discount, net, VAT 15 %).</summary>
public sealed record InvoiceTotals(double SubcontractValue, double PrevGross, double CurrGross, double CumGross, double RetentionPct,
    double PrevRetention, double CurrRetention, double AdvancePrev, double AdvanceCurr, double RecoveredAdvance, double Discount)
{
    public const double VatRate = 0.15;
    public double CumRetention => PrevRetention + CurrRetention;
    public double NetPrev => PrevGross + PrevRetention + AdvancePrev;
    public double NetCurr => CurrGross + CurrRetention + AdvanceCurr + RecoveredAdvance + Discount;
    public double NetCum => NetPrev + NetCurr;
    public double VatCurr => NetCurr * VatRate;
    public double VatCum => NetCum * VatRate;
    public double NetInclVatCurr => NetCurr + VatCurr;
    public double NetInclVatCum => NetCum + VatCum;

    public static InvoiceTotals Of(SubInvoice h, IEnumerable<SubInvoiceLine> lines)
    {
        var items = lines.Where(l => l.Kind == "ITEM").ToList();
        var prev = items.Sum(l => l.PrevAmount);
        var curr = items.Sum(l => l.CurrAmount);
        return new InvoiceTotals(items.Sum(l => l.Amount), prev, curr, prev + curr, h.RetentionPct,
            -prev * h.RetentionPct, -curr * h.RetentionPct, 0, 0, -Math.Abs(h.AdvanceRecovery), -Math.Abs(h.Discount));
    }
}

public sealed record RevisionDiff(string ItemNo, string BoqCode, string Description, double CumBefore, double CumAfter, double AmountBefore, double AmountAfter)
{
    public double QtyChange => CumAfter - CumBefore;
    public double AmountChange => AmountAfter - AmountBefore;
}

/// <summary>
/// Builds a subcontractor invoice in the template's row order: per item x BOQ row, CUM = mapped quantity of every claim line of the
/// subcontractor up to this invoice number (held checks excluded), PREV = CUM of the last approved invoice, CURR = CUM - PREV.
/// </summary>
public static class InvoiceBuilder
{
    public static InvoiceBuild Build(ProjectSnapshot s, string contractNo, string subcontractor, int invoiceNo, MappingEngine? engine = null, int revision = 0, MappingOptions? options = null)
    {
        engine ??= new MappingEngine();
        var contract = s.Contracts.FirstOrDefault(c => c.ContractNo == contractNo);
        var sub = subcontractor.Trim().ToUpperInvariant();
        var ctx = new MappingContext(contractNo, s.ContractItems, s.ItemBoqs, s.BoqItems, s.MappingRules) { Options = options ?? MappingOptions.Default };
        var areas = s.Rooms.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().AreaType, StringComparer.OrdinalIgnoreCase);
        var claims = s.Claims.Where(c => c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && c.InvoiceNo <= invoiceNo && c.InvoiceNo > 0).ToList();
        var mapping = engine.Map(claims, ctx, areas);

        var prevApproved = s.SubInvoices.Where(i => i.ContractNo == contractNo && i.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase)
                                                    && i.InvoiceNo < invoiceNo && i.Status == SubInvoiceStatus.Approved)
            .OrderByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision).FirstOrDefault();
        var prevCum = prevApproved is null ? new Dictionary<string, double>()
            : OccurrenceKeys(s.SubInvoiceLines.Where(l => l.SubInvoiceId == prevApproved.Id)).ToDictionary(x => x.Key, x => x.Line.CumQty);

        var header = new SubInvoice
        {
            Subcontractor = sub, ContractNo = contractNo, InvoiceNo = invoiceNo, Revision = revision, Status = SubInvoiceStatus.Draft, CreatedAt = DateTime.Now,
            RetentionPct = contract?.RetentionPct ?? 0.10, AdvancePct = contract?.AdvancePct ?? 0,
        };
        var build = new InvoiceBuild { Header = header, Mapping = mapping, PreviousApproved = prevApproved };
        var lines = Layout(s, contractNo, ctx);
        var rowIndex = new Dictionary<string, SubInvoiceLine>();
        foreach (var l in lines.Where(l => l.Kind == "ITEM"))
            rowIndex.TryAdd($"{l.ItemNo}|{l.BoqCode}", l);

        foreach (var (key, (qty, parts)) in mapping.ByRow())
        {
            if (!rowIndex.TryGetValue(key, out var row))
            {
                var itemNo = key.Split('|')[0];
                row = lines.FirstOrDefault(l => l.Kind == "ITEM" && l.ItemNo == itemNo && l.BoqCode.Length == 0)
                      ?? lines.FirstOrDefault(l => l.Kind == "ITEM" && l.ItemNo == itemNo);
                if (row is null) { build.Warnings.Add($"Item {itemNo} is not in the invoice layout - {qty:0.##} not invoiced."); continue; }
                build.Warnings.Add($"{key} has no row of its own - {qty:0.##} placed on row {row.ItemNo} {row.BoqCode}.");
            }
            row.CumQty += qty;
            row.Explanation = string.Join("\n", new[] { row.Explanation }.Where(e => e.Length > 0)
                .Concat(parts.GroupBy(p => (p.Line.Room, p.Line.Stage, p.Line.Item, p.Line.InvoiceNo)).Select(g => $"{g.Key.Room} {g.Key.Stage} {g.Key.Item} INV{g.Key.InvoiceNo}: {g.Sum(p => p.Qty):0.##} [{MappingEngine.Worst(g.First().Confidence, g.Last().Confidence)}]")));
        }
        foreach (var (k, l) in OccurrenceKeys(lines))
        {
            l.CumQty = Math.Round(l.CumQty, 4);
            l.PrevQty = Math.Round(prevCum.GetValueOrDefault(k), 4);
            l.CurrQty = Math.Round(l.CumQty - l.PrevQty, 4);
            if (l.CurrQty < -1e-6) build.Warnings.Add($"Row {l.ItemNo} {l.BoqCode}: cumulative {l.CumQty:0.##} is below the approved previous {l.PrevQty:0.##}.");
        }
        foreach (var g in mapping.Parts.Where(p => p.Item is null).GroupBy(p => (p.Line.Stage, p.Line.Item, Why: p.Explanation.Split("; ").Last())))
            build.Warnings.Add($"Not mapped: {g.Key.Stage} {g.Key.Item} - {g.Count()} lines, qty {g.Sum(p => p.Qty):0.##} ({g.Key.Why})");
        foreach (var blk in CumulativeSplit.Pending(claims))
            build.Warnings.Add($"{blk.Sub} INV 1-{blk.InvoiceNo} is one CUMULATIVE block ({blk.Lines} lines) - awaiting invoice files to split; it counts once, as INV {blk.InvoiceNo}.");
        if (mapping.Held.Count > 0) build.Warnings.Add($"{mapping.Held.Count} claim lines held out (pending height / length checks).");
        build.Lines.AddRange(lines);
        return build;
    }

    /// <summary>Item rows keyed "item|code#n" (n = occurrence), so templates that repeat an item x code row stay aligned.</summary>
    public static IEnumerable<(string Key, SubInvoiceLine Line)> OccurrenceKeys(IEnumerable<SubInvoiceLine> lines)
    {
        var seen = new Dictionary<string, int>();
        foreach (var l in lines.Where(l => l.Kind == "ITEM").OrderBy(l => l.RowOrder))
        {
            var k = $"{l.ItemNo}|{l.BoqCode}";
            var n = seen.GetValueOrDefault(k);
            seen[k] = n + 1;
            yield return ($"{k}#{n}", l);
        }
    }

    /// <summary>Invoice rows in order: the imported template when present, otherwise contract items x BOQ links (qty split evenly).</summary>
    public static List<SubInvoiceLine> Layout(ProjectSnapshot s, string contractNo, MappingContext ctx)
    {
        var template = s.TemplateRows.Where(r => r.ContractNo == contractNo).OrderBy(r => r.RowOrder).ToList();
        var items = ctx.Items.ToDictionary(i => i.ItemNo);
        var lines = new List<SubInvoiceLine>();
        var order = 0;
        if (template.Count > 0)
        {
            foreach (var t in template)
            {
                items.TryGetValue(t.ItemNo, out var it);
                lines.Add(new SubInvoiceLine
                {
                    RowOrder = ++order, Kind = t.Kind, ItemNo = t.ItemNo, BoqCode = t.BoqCode, CostCode = t.CostCode, BudgetResourceCode = t.BudgetResourceCode,
                    BoqDescription = t.BoqCode.Length > 0 ? ctx.BoqDescriptions.GetValueOrDefault(t.BoqCode, "") : "",
                    Description = t.Description.Length > 0 ? t.Description : it?.Description ?? "", Unit = t.Unit.Length > 0 ? t.Unit : it?.Unit ?? "",
                    ContractQty = t.Qty, Rate = t.Rate > 0 ? t.Rate : it?.Rate ?? 0, StagePct = t.StagePct > 0 ? t.StagePct : it?.StagePct ?? 0,
                });
            }
            return lines;
        }
        var section = "";
        foreach (var it in ctx.Items)
        {
            if (it.Section != section && it.Section.Length > 0)
            {
                section = it.Section;
                lines.Add(new SubInvoiceLine { RowOrder = ++order, Kind = "SECTION", Description = section });
            }
            var links = ctx.LinksByItem.GetValueOrDefault(it.ItemNo) ?? new List<ContractItemBoq>();
            if (links.Count == 0)
            {
                lines.Add(Row(it, null, it.Qty, ++order, ctx));
                continue;
            }
            foreach (var l in links) lines.Add(Row(it, l, it.Qty / links.Count, ++order, ctx));
        }
        return lines;
    }

    private static SubInvoiceLine Row(ContractItem it, ContractItemBoq? l, double qty, int order, MappingContext ctx)
    {
        var desc = l is null ? "" : ctx.DescriptionOf(l);
        var budget = l is null ? null : ctx.BoqDescriptions.ContainsKey(l.BoqCode) ? l.BoqCode : null;
        _ = budget;
        return new SubInvoiceLine
        {
            RowOrder = order, Kind = "ITEM", ItemNo = it.ItemNo, BoqCode = l?.BoqCode ?? "", BoqDescription = desc, Description = it.Description,
            Unit = it.Unit, ContractQty = qty, Rate = it.Rate, StagePct = it.StagePct,
        };
    }

    public static List<RevisionDiff> Diff(IEnumerable<SubInvoiceLine> before, IEnumerable<SubInvoiceLine> after)
    {
        var a = before.Where(l => l.Kind == "ITEM").GroupBy(l => $"{l.ItemNo}|{l.BoqCode}").ToDictionary(g => g.Key, g => g.First());
        var b = after.Where(l => l.Kind == "ITEM").GroupBy(l => $"{l.ItemNo}|{l.BoqCode}").ToDictionary(g => g.Key, g => g.First());
        var diffs = new List<RevisionDiff>();
        foreach (var k in a.Keys.Union(b.Keys))
        {
            a.TryGetValue(k, out var x); b.TryGetValue(k, out var y);
            var cx = x?.CumQty ?? 0; var cy = y?.CumQty ?? 0;
            if (Math.Abs(cx - cy) < 1e-6) continue;
            var src = y ?? x!;
            diffs.Add(new RevisionDiff(src.ItemNo, src.BoqCode, src.BoqDescription.Length > 0 ? src.BoqDescription : src.Description, cx, cy, x?.CumAmount ?? 0, y?.CumAmount ?? 0));
        }
        return diffs.OrderBy(d => d.ItemNo.PadLeft(6, '0')).ThenBy(d => d.BoqCode).ToList();
    }
}

/// <summary>Invoice lifecycle: draft, submit (Aconex workflow no.), reject (reason), approve (locks), next revision.</summary>
public static class InvoiceWorkflow
{
    public static SubInvoice SaveDraft(IProjectStore store, InvoiceBuild build)
    {
        var h = build.Header;
        if (h.Locked) throw new InvalidOperationException($"{h.Title} is approved and locked.");
        var existing = store.All<SubInvoice>().FirstOrDefault(i => i.ContractNo == h.ContractNo && i.Subcontractor == h.Subcontractor && i.InvoiceNo == h.InvoiceNo && i.Revision == h.Revision);
        if (existing is { Locked: true }) throw new InvalidOperationException($"{existing.Title} is approved and locked.");
        var oldLines = existing is null ? new List<SubInvoiceLine>() : store.All<SubInvoiceLine>().Where(l => l.SubInvoiceId == existing.Id).ToList();
        store.Batch(w =>
        {
            if (existing != null)
            {
                existing.RetentionPct = h.RetentionPct; existing.AdvancePct = h.AdvancePct; existing.Discount = h.Discount; existing.AdvanceRecovery = h.AdvanceRecovery;
                existing.Notes = h.Notes;
                existing.Kind = h.Kind;
                w.Update(existing);
                foreach (var l in oldLines) w.Delete(l);
                h.Id = existing.Id; h.RowVersion = existing.RowVersion; h.Status = existing.Status;
            }
            else w.Insert(h);
            foreach (var l in build.Lines) { l.Id = 0; l.SubInvoiceId = h.Id; }
            w.InsertMany(build.Lines);
        }, $"{h.Title} saved: SAR {build.Totals.CurrGross:N2} this period");
        return h;
    }

    public static void Submit(IProjectStore store, SubInvoice inv, string aconexWorkflowNo)
    {
        Guard(inv);
        inv.Status = SubInvoiceStatus.Submitted;
        inv.AconexWorkflowNo = aconexWorkflowNo;
        inv.SubmittedAt = DateTime.Now;
        store.Update(inv, $"{inv.Title} submitted (Aconex {aconexWorkflowNo})");
    }

    public static void Reject(IProjectStore store, SubInvoice inv, string reason)
    {
        Guard(inv);
        inv.Status = SubInvoiceStatus.Rejected;
        inv.RejectionReason = reason;
        store.Update(inv, $"{inv.Title} rejected: {reason}");
    }

    public static void Approve(IProjectStore store, SubInvoice inv)
    {
        Guard(inv);
        inv.Status = SubInvoiceStatus.Approved;
        inv.ApprovedAt = DateTime.Now;
        inv.Locked = true;
        store.Update(inv, $"{inv.Title} approved and locked");
    }

    private static void Guard(SubInvoice inv)
    {
        if (inv.Locked) throw new InvalidOperationException($"{inv.Title} is approved and locked.");
    }

    /// <summary>Next revision number for an invoice (after a rejection).</summary>
    public static int NextRevision(IEnumerable<SubInvoice> all, string contractNo, string sub, int invoiceNo) =>
        all.Where(i => i.ContractNo == contractNo && i.Subcontractor == sub && i.InvoiceNo == invoiceNo).Select(i => i.Revision + 1).DefaultIfEmpty(0).Max();
}
