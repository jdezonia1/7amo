using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Export;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Insights;

public sealed class CashFlowInputs
{
    public required ProjectSnapshot Project { get; init; }
    public MaterialsSnapshot? Materials { get; init; }
    public InsightsData Data { get; init; } = new();
    public DateTime Today { get; init; } = DateTime.Today;
    public DateTime ProjectStart { get; init; }
    public DateTime PlannedFinish { get; init; }
    /// <summary>Scenario: every finish still ahead moves by this many weeks.</summary>
    public int DelayWeeks { get; init; }
    public string? Building { get; init; }
}

public static class CashKinds
{
    public const string Sub = "SUBCONTRACTORS", Supplier = "SUPPLIERS", Owner = "OWNER";
}

/// <summary>One forecast payment / receipt.</summary>
public sealed record CashEvent(DateTime Month, string Kind, string Party, string Ref, double Amount, string Note);

public sealed record CashMonth(DateTime Month, double SubPayables, double SupplierPayables, double OwnerReceipts, double Net, double Cumulative)
{
    public double Payables => SubPayables + SupplierPayables;
    public string Label => Month.ToString("MMM yy");
}

public sealed class CashFlowResult
{
    public List<CashMonth> Months { get; } = new();
    public List<CashEvent> Events { get; } = new();
    public List<string> Assumptions { get; } = new();
    public DateTime Finish { get; init; }
    public double TotalPayables => Months.Sum(m => m.Payables);
    public double TotalReceipts => Months.Sum(m => m.OwnerReceipts);
    public double PeakNegative => Months.Select(m => m.Cumulative).DefaultIfEmpty().Min();
    public DateTime? PeakNegativeMonth => Months.Count == 0 ? null : Months.OrderBy(m => m.Cumulative).First().Month;
}

/// <summary>
/// Cash-flow forecast (roadmap 8). Remaining work value per subcontract (contract qty x rate less the last approved cumulative), spread
/// over the programme windows of its stage, invoiced monthly and paid per the payment terms (stage % now, the rest at handover,
/// retention and advance recovery withheld); undelivered PO value spread before the 2nd-fix window and paid on delivery terms (LC before
/// delivery); owner receipts = the same work and materials at owner value (owner BOQ rates where linked, else cost x (1 + markup)),
/// MOS on delivery, retention released at handover. Payables are negative in the net.
/// </summary>
public static class CashFlowForecast
{
    public static InsightPaymentTerm Term(InsightsData d, string party, string reference)
    {
        var t = d.Terms.FirstOrDefault(x => x.Party == party && string.Equals(x.Ref.Trim(), reference.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? d.Terms.FirstOrDefault(x => x.Party == party && x.Ref.Trim() == "*");
        if (t != null) return t;
        return party switch
        {
            PaymentParties.Supplier => new InsightPaymentTerm { Party = party, Ref = "*", Scheme = PaymentSchemes.S100, RetentionPct = 0, PayDays = 60, Name = "default supplier terms" },
            PaymentParties.Owner => new InsightPaymentTerm { Party = party, Ref = "*", Scheme = PaymentSchemes.S100, RetentionPct = 0.10, PayDays = 56, HandoverMonths = 3, Name = "default owner terms" },
            _ => new InsightPaymentTerm { Party = party, Ref = "*", Scheme = PaymentSchemes.S90_10, RetentionPct = 0.10, PayDays = 30, Name = "default subcontract terms (90 % per stage, 10 % at handover)" },
        };
    }

    private static int LagMonths(int days) => Math.Max(0, (int)Math.Ceiling(days / 30.0));

    /// <summary>Spreads a value evenly per day over the parts of the windows still ahead (overdue work lands in the current month).</summary>
    public static Dictionary<DateTime, double> Spread(double value, IReadOnlyList<(DateTime Start, DateTime Finish)> windows, DateTime today)
    {
        var res = new Dictionary<DateTime, double>();
        if (Math.Abs(value) < 1e-9 || windows.Count == 0) return res;
        var share = value / windows.Count;
        foreach (var (s0, f) in windows)
        {
            var s = s0 < today ? today : s0;
            if (f <= s) { Add(res, ProgrammeModel.MonthOf(s), share); continue; }
            var days = (f - s).TotalDays;
            for (var m = ProgrammeModel.MonthOf(s); m <= f; m = m.AddMonths(1))
            {
                var a = m < s ? s : m; var b = m.AddMonths(1) > f ? f : m.AddMonths(1);
                if (b > a) Add(res, m, share * (b - a).TotalDays / days);
            }
        }
        return res;
    }

    private static void Add(Dictionary<DateTime, double> d, DateTime k, double v) => d[k] = d.GetValueOrDefault(k) + v;

    public static string StageOf(ContractItem c)
    {
        var fs = (c.FixStage ?? "").ToUpperInvariant();
        if (fs.StartsWith("1")) return Stages.First;
        if (fs.StartsWith("2")) return Stages.Second;
        if (fs.StartsWith("3")) return Stages.Final;
        var cat = (c.Category ?? "").ToUpperInvariant();
        if (cat.Contains("TRAY") || cat == "CABLE" || cat.Contains("WIRING")) return Stages.Second;
        if (cat.Contains("PANEL") || cat.Contains("TERMINATION") || cat == "DEVICE") return Stages.Final;
        return "";
    }

    public static CashFlowResult Build(CashFlowInputs i)
    {
        var s = i.Project;
        var d = i.Data;
        var today = i.Today.Date;
        var now = ProgrammeModel.MonthOf(today);
        var prog = new ProgrammeModel(d.Programme, i.ProjectStart, i.PlannedFinish, today, i.DelayWeeks, i.Building);
        var res = new CashFlowResult { Finish = prog.Finish };
        var markup = d.Threshold(InsightThresholds.OwnerMarkup);
        var mosPct = d.Threshold(InsightThresholds.MosPct);
        var ownerTerm = Term(d, PaymentParties.Owner, "OWNER");
        var ownerLag = LagMonths(ownerTerm.PayDays);
        var handover = ProgrammeModel.MonthOf(prog.Finish);
        res.Assumptions.Add(prog.IsDefault
            ? $"Programme: default spread between {i.ProjectStart:dd MMM yyyy} and {prog.Finish:dd MMM yyyy} (1st fix first, 2nd fix middle, final fix last) - enter planned dates per area / stage for a real programme."
            : $"Programme: {prog.Rows.Count} planned windows" + (i.DelayWeeks > 0 ? $", delayed {i.DelayWeeks} weeks" : "") + ".");
        if (i.DelayWeeks > 0) res.Assumptions.Add($"Scenario: every finish still ahead moves {i.DelayWeeks} weeks later (handover {prog.Finish:dd MMM yyyy}).");
        res.Assumptions.Add("Work is invoiced at the end of each month; payments follow the terms (pay days, stage %, retention, advance recovery).");

        // ------------------------------------------------ subcontracts
        var approvedBy = s.SubInvoices.Where(x => InvoiceKinds.IsSubcontractor(x) && x.Status == SubInvoiceStatus.Approved)
            .GroupBy(x => x.ContractNo.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.InvoiceNo).ThenByDescending(x => x.Revision).First());
        var contracts = s.Contracts.GroupBy(c => c.ContractNo.Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());
        var ownerRates = OwnerRates(s, i.Materials);
        var links = s.ItemBoqs.GroupBy(l => (l.ContractNo.Trim().ToUpperInvariant(), l.ItemNo.Trim().ToUpperInvariant())).ToDictionary(g => g.Key, g => g.Select(x => x.BoqCode).ToList());
        var ownerRateUsed = 0;
        foreach (var g in s.ContractItems.Where(c => c.Rate > 0 && c.Qty > 0).GroupBy(c => c.ContractNo.Trim().ToUpperInvariant()))
        {
            contracts.TryGetValue(g.Key, out var con);
            if (con != null && !string.IsNullOrEmpty(i.Building) && !string.IsNullOrEmpty(con.Building) && !string.Equals(con.Building, i.Building, StringComparison.OrdinalIgnoreCase)) continue;
            var party = con?.Subcontractor is { Length: > 0 } sub ? sub : g.Key;
            var term = Term(d, PaymentParties.Subcontract, g.Key);
            var retention = !d.Terms.Any(t => t.Party == PaymentParties.Subcontract && t.Ref.Trim() is var r && (r == "*" || r.Equals(g.Key, StringComparison.OrdinalIgnoreCase))) && con != null ? con.RetentionPct : term.RetentionPct;
            var cert = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (approvedBy.TryGetValue(g.Key, out var last))
                foreach (var l in s.SubInvoiceLines.Where(l => l.SubInvoiceId == last.Id && l.Kind == "ITEM")) cert[l.ItemNo.Trim()] = cert.GetValueOrDefault(l.ItemNo.Trim()) + l.CumQty;
            // no approved invoice in the app yet: take the work already claimed in the ledger (within PROJECT QTY, per stage) as done
            var estimated = last is null ? LedgerProgress(s, con?.Building is { Length: > 0 } cb ? cb : i.Building) : null;
            var lagS = LagMonths(term.PayDays);
            var advance = term.AdvancePct > 0 ? term.AdvancePct : con?.AdvancePct ?? 0;
            double remainingTotal = 0;
            foreach (var item in g)
            {
                var stage = StageOf(item);
                var done = estimated is null ? cert.GetValueOrDefault(item.ItemNo.Trim())
                    : item.Qty * (stage.Length > 0 ? estimated.GetValueOrDefault(stage) : estimated.Values.DefaultIfEmpty().Average());
                var remQty = Math.Max(0, item.Qty - done);
                var value = remQty * item.Rate;
                if (value < 0.01) continue;
                remainingTotal += value;
                var windows = stage.Length > 0 ? prog.Windows(stage) : new List<(DateTime, DateTime)> { (prog.ProjectStart, prog.Finish) };
                var stagePct = item.StagePct > 0 && item.StagePct < 1 ? item.StagePct : term.Scheme == PaymentSchemes.S70_20_10 ? 0.7 : term.Scheme == PaymentSchemes.S100 ? 1.0 : 0.9;
                var testPct = stagePct <= 0.7 + 1e-9 && stagePct < 1 ? Math.Min(0.2, 1 - stagePct) : 0;
                var handPct = Math.Max(0, 1 - stagePct - testPct);
                var codes = links.GetValueOrDefault((g.Key, item.ItemNo.Trim().ToUpperInvariant())) ?? new();
                var ownerRate = codes.Select(c => ownerRates.TryGetValue(c.Trim(), out var r) ? (double?)r : null).FirstOrDefault(r => r.HasValue);
                if (ownerRate.HasValue) ownerRateUsed++;
                var ownerValue = ownerRate.HasValue ? remQty * ownerRate.Value : value * (1 + markup);
                foreach (var (month, v) in Spread(value, windows, today))
                {
                    var net = 1 - retention - advance;
                    Pay(res, month.AddMonths(lagS), CashKinds.Sub, party, g.Key, -v * stagePct * net, "stage %");
                    if (testPct > 0) Pay(res, month.AddMonths(1 + lagS), CashKinds.Sub, party, g.Key, -v * testPct * net, "test & termination");
                    if (handPct > 0) Pay(res, handover.AddMonths(term.HandoverMonths), CashKinds.Sub, party, g.Key, -v * handPct * net, "handover %");
                    if (retention > 0) Pay(res, handover.AddMonths(term.HandoverMonths), CashKinds.Sub, party, g.Key, -v * retention, "retention release");
                    // owner side for the same work
                    var ov = v / value * ownerValue;
                    Pay(res, month.AddMonths(ownerLag), CashKinds.Owner, "OWNER", g.Key, ov * (1 - ownerTerm.RetentionPct), "work done");
                    if (ownerTerm.RetentionPct > 0) Pay(res, handover.AddMonths(ownerTerm.HandoverMonths), CashKinds.Owner, "OWNER", g.Key, ov * ownerTerm.RetentionPct, "retention release");
                }
            }
            res.Assumptions.Add($"{party} ({g.Key}): remaining SAR {remainingTotal:N0} of contract SAR {g.Sum(x => x.Qty * x.Rate):N0}" + (last != null ? $" after {last.Title}" : estimated is { Count: > 0 } ? $" (no approved invoice: ledger progress {string.Join(", ", estimated.Select(kv => $"{kv.Key} {kv.Value:P0}"))} taken as done)" : " (no approved invoice yet)") +
                                $"; terms {term.Scheme}, retention {retention:P0}, advance {advance:P0}, paid {term.PayDays} days after the month.");
        }
        var subsWithoutContract = s.Claims.Select(c => c.Subcontractor.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(sub => !s.Contracts.Any(c => c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase)) || !s.ContractItems.Any(ci => s.Contracts.Any(c => c.Subcontractor.Equals(sub, StringComparison.OrdinalIgnoreCase) && c.ContractNo == ci.ContractNo))).ToList();
        if (subsWithoutContract.Count > 0)
            res.Assumptions.Add($"Not in the forecast (no contract schedule imported): {string.Join(", ", subsWithoutContract)}.");

        // ------------------------------------------------ suppliers (undelivered PO value)
        var deliverBy = prog.Windows(Stages.Second).Select(w => w.Finish).DefaultIfEmpty(prog.Finish).Max().AddMonths(-1);
        var deliveryWindow = new List<(DateTime, DateTime)> { (today, deliverBy > today ? deliverBy : today.AddMonths(1)) };
        if (i.Materials is { } m)
        {
            var delivered = m.DnLines.Where(l => l.PoLineId.HasValue).GroupBy(l => l.PoLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
            var approvedSupplier = s.SubInvoices.Where(x => x.Status == SubInvoiceStatus.Approved).Select(x => x.Id).ToHashSet();
            foreach (var po in m.Pos)
            {
                var lc = po.PaymentTerms.Contains("LC", StringComparison.OrdinalIgnoreCase) || po.PaymentTerms.Contains("before delivery", StringComparison.OrdinalIgnoreCase);
                var term = d.Terms.FirstOrDefault(t => t.Party == PaymentParties.Supplier && string.Equals(t.Ref.Trim(), po.PoNo.Trim(), StringComparison.OrdinalIgnoreCase))
                           ?? (lc ? new InsightPaymentTerm { Party = PaymentParties.Supplier, Ref = po.PoNo, Scheme = PaymentSchemes.Lc, RetentionPct = po.RetentionPct, PayDays = 0, LcDaysBeforeDelivery = 30 } : Term(d, PaymentParties.Supplier, po.PoNo));
                var lines = m.PoLines.Where(l => l.PoId == po.Id).ToList();
                var undelivered = lines.Sum(l => Math.Max(0, l.Qty - delivered.GetValueOrDefault(l.Id)) * l.Rate);
                var party = $"{po.Supplier} ({po.PoNo})";
                foreach (var (month, v) in Spread(undelivered, deliveryWindow, today))
                {
                    var payMonth = term.Scheme == PaymentSchemes.Lc ? month.AddMonths(-LagMonths(term.LcDaysBeforeDelivery)) : month.AddMonths(LagMonths(term.PayDays));
                    Pay(res, payMonth < now ? now : payMonth, CashKinds.Supplier, party, po.PoNo, -v * (1 - term.RetentionPct), term.Scheme == PaymentSchemes.Lc ? "LC before delivery" : "on delivery terms");
                    if (term.RetentionPct > 0) Pay(res, handover.AddMonths(term.HandoverMonths), CashKinds.Supplier, party, po.PoNo, -v * term.RetentionPct, "retention release");
                    var ov = v * (1 + markup);
                    Pay(res, month.AddMonths(ownerLag), CashKinds.Owner, "OWNER", po.PoNo, ov * mosPct, "MOS on delivery");
                    Pay(res, ProgrammeModel.MonthOf(deliverBy).AddMonths(ownerLag), CashKinds.Owner, "OWNER", po.PoNo, ov * (1 - mosPct), "material installed (MOS recovered)");
                }
                // delivered but not yet invoiced / paid (non-LC)
                if (term.Scheme != PaymentSchemes.Lc)
                {
                    var lockIds = m.Locks.Where(k => approvedSupplier.Contains(k.SubInvoiceId)).Select(k => k.DnLineId).ToHashSet();
                    var poLineIds = lines.Select(l => l.Id).ToHashSet();
                    var unpaid = m.DnLines.Where(l => l.PoLineId is long id && poLineIds.Contains(id) && !lockIds.Contains(l.Id)).Sum(l => l.Qty * (lines.First(x => x.Id == l.PoLineId).Rate));
                    if (unpaid > 0.01) Pay(res, now.AddMonths(LagMonths(term.PayDays)), CashKinds.Supplier, party, po.PoNo, -unpaid, "delivered, not yet paid");
                }
                if (undelivered > 0.01) res.Assumptions.Add($"{party}: undelivered SAR {undelivered:N0} spread to {deliverBy:MMM yyyy}, {(term.Scheme == PaymentSchemes.Lc ? $"LC {term.LcDaysBeforeDelivery} days before delivery" : $"paid {term.PayDays} days after delivery")}.");
            }
        }
        res.Assumptions.Add(ownerRateUsed > 0 ? $"Owner value from owner BOQ rates on {ownerRateUsed} contract items, cost x (1 + {markup:P0}) elsewhere."
                                              : $"Owner value = cost x (1 + {markup:P0}) (no owner BOQ rates linked); MOS {mosPct:P0} of delivered material, owner retention {ownerTerm.RetentionPct:P0}, paid {ownerTerm.PayDays} days after the month.");

        // ------------------------------------------------ months
        if (res.Events.Count == 0) return res;
        var first = now;
        var lastM = res.Events.Max(e => e.Month);
        double cum = 0;
        for (var mth = first; mth <= lastM; mth = mth.AddMonths(1))
        {
            var ev = res.Events.Where(e => e.Month == mth).ToList();
            double sp = ev.Where(e => e.Kind == CashKinds.Sub).Sum(e => e.Amount), su = ev.Where(e => e.Kind == CashKinds.Supplier).Sum(e => e.Amount), ow = ev.Where(e => e.Kind == CashKinds.Owner).Sum(e => e.Amount);
            cum += sp + su + ow;
            res.Months.Add(new CashMonth(mth, -sp, -su, ow, sp + su + ow, cum));
        }
        return res;
    }

    private static void Pay(CashFlowResult r, DateTime month, string kind, string party, string reference, double amount, string note)
    {
        if (Math.Abs(amount) < 0.005) return;
        var m = ProgrammeModel.MonthOf(month);
        var existing = r.Events.FindIndex(e => e.Month == m && e.Kind == kind && e.Party == party && e.Note == note);
        if (existing >= 0) r.Events[existing] = r.Events[existing] with { Amount = r.Events[existing].Amount + amount };
        else r.Events.Add(new CashEvent(m, kind, party, reference, amount, note));
    }

    /// <summary>Share of PROJECT QTY claimed (within the cap, after SITE %) per contract stage (1ST FIX / 2ND FIX / FINAL FIX) for a building.</summary>
    public static Dictionary<string, double> LedgerProgress(ProjectSnapshot s, string? building)
    {
        bool In(string? b) => building is null || string.IsNullOrEmpty(b) || string.Equals(b, building, StringComparison.OrdinalIgnoreCase);
        var plan = s.RoomQtys.Where(q => In(q.Building) && q.Qty > 0).GroupBy(q => q.Key).ToDictionary(g => g.Key, g => (Stage: g.First().Stage, Qty: g.Sum(q => q.Qty)));
        if (plan.Count == 0) return new();
        var claimed = Ledger.LedgerRules.Effective(s.Claims.Where(c => In(c.Building))).Where(c => !c.Rework).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty * c.SitePct));
        static string Contract(string ledgerStage)
        {
            var st = Stages.Normalize(ledgerStage);
            if (st.Length > 0) return st;
            var u = (ledgerStage ?? "").ToUpperInvariant();
            return u is "CEILING" or "EMT" or "FLEXIBLE" ? Stages.First : u.Contains("PULLING") || u.Contains("TRAY") ? Stages.Second : u.Contains("PANEL") ? Stages.Final : "";
        }
        return plan.GroupBy(kv => Contract(kv.Value.Stage)).Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.Sum(kv => Math.Clamp(claimed.GetValueOrDefault(kv.Key), 0, kv.Value.Qty)) / g.Sum(kv => kv.Value.Qty));
    }

    private static Dictionary<string, double> OwnerRates(ProjectSnapshot s, MaterialsSnapshot? m)
    {
        var owner = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (m != null) foreach (var b in m.BoqLines.Where(b => !b.IsHeading && b.Rate > 0 && b.BoqCode.Length > 0)) owner.TryAdd(b.BoqCode.Trim(), b.Rate);
        foreach (var b in s.BoqItems.Where(b => b.Rate > 0 && b.ItemCode.Length > 0)) owner.TryAdd(b.ItemCode.Trim(), b.Rate);
        return owner;
    }

    public static List<ExportSheet> Sheets(CashFlowResult r, string scope)
    {
        var parties = r.Events.GroupBy(e => (e.Kind, e.Party)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Party).ToList();
        var months = r.Months.Select(m => m.Month).ToList();
        var byParty = new ExportSheet
        {
            Name = "BY PARTY", Title = "CASH FLOW FORECAST - BY PARTY", Subtitle = scope + "  |  payables negative, receipts positive",
            Columns = new List<ExportColumn> { new("KIND"), new("PARTY", ColumnKind.Text, 34) }.Concat(months.Select(m => new ExportColumn(m.ToString("MMM yy"), ColumnKind.Money))).Append(new("TOTAL", ColumnKind.Money)).ToList(),
            Rows = parties.Select(g => new object?[] { g.Key.Kind, g.Key.Party }.Concat(months.Select(m => (object?)g.Where(e => e.Month == m).Sum(e => e.Amount))).Append(g.Sum(e => e.Amount)).ToArray()).ToList(),
        };
        return new List<ExportSheet>
        {
            new()
            {
                Name = "MONTHLY", Title = "CASH FLOW FORECAST - PAYABLES VS RECEIVABLES", Subtitle = scope,
                Columns = new() { new("MONTH"), new("SUBCONTRACTORS", ColumnKind.Money), new("SUPPLIERS", ColumnKind.Money), new("TOTAL PAYABLES", ColumnKind.Money), new("OWNER RECEIPTS", ColumnKind.Money), new("NET", ColumnKind.Money), new("CUMULATIVE", ColumnKind.Money) },
                Rows = r.Months.Select(m => new object?[] { m.Label, m.SubPayables, m.SupplierPayables, m.Payables, m.OwnerReceipts, m.Net, m.Cumulative }).ToList(),
                TotalRow = new object?[] { "TOTAL", r.Months.Sum(m => m.SubPayables), r.Months.Sum(m => m.SupplierPayables), r.TotalPayables, r.TotalReceipts, r.Months.Sum(m => m.Net), null },
            },
            byParty,
            new() { Name = "ASSUMPTIONS", Title = "CASH FLOW FORECAST - ASSUMPTIONS", Columns = new() { new("ASSUMPTION", ColumnKind.Text, 140) }, Rows = r.Assumptions.Select(a => new object?[] { a }).ToList() },
        };
    }
}
