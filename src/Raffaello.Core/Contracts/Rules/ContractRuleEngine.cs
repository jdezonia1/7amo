using System.Globalization;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Contracts.Rules;

/// <summary>
/// Evaluates contract rules for the other modules (ledger entry, invoice build, supplier invoice, PO / DN, package build) and returns
/// WARNINGS ONLY. A warning carries the rule, the clause text and page; the user can BYPASS it with a short reason, which is recorded
/// (who / when / why) and printed in the invoice package checks report. Data-integrity locks (a DN line invoiced once, an approved
/// invoice is locked) are not contract terms and stay where they are.
/// </summary>
public sealed class ContractRuleEngine
{
    private readonly IReadOnlyList<ContractRule> _rules;
    private readonly IReadOnlyList<RuleBypass> _bypasses;
    private readonly IReadOnlyList<ContractClause> _clauses;

    public ContractRuleEngine(IEnumerable<ContractRule> rules, IEnumerable<RuleBypass>? bypasses = null, IEnumerable<ContractClause>? clauses = null)
    {
        _rules = rules.Where(r => r.Enabled).ToList();
        _bypasses = (bypasses ?? Array.Empty<RuleBypass>()).ToList();
        _clauses = (clauses ?? Array.Empty<ContractClause>()).ToList();
    }

    public IEnumerable<ContractRule> RulesOf(string contractNo, string? type = null) =>
        _rules.Where(r => string.Equals(r.ContractNo, contractNo, StringComparison.OrdinalIgnoreCase) && (type is null || r.RuleType == type));

    private RuleWarning W(ContractRule r, string code, string message, string context, string key)
    {
        var clause = _clauses.FirstOrDefault(c => c.ContractNo == r.ContractNo && c.ClauseNo == r.ClauseNo && r.ClauseNo.Length > 0);
        var w = new RuleWarning(r.ContractNo, r.Id, r.RuleType, code, message, r.ClauseNo, clause?.TextAr ?? r.SourceText, r.SourcePage, context, key);
        var b = _bypasses.FirstOrDefault(x => x.ContractNo == r.ContractNo && (x.RuleId == r.Id && r.Id != 0 || x.RuleType == r.RuleType) && x.Context == context && x.ContextKey == key && x.Code == code);
        return w with { Bypass = b };
    }

    // ------------------------------------------------------------------ ledger entry

    /// <summary>Checks one claim line against the contract (height bands, 15 m rule). Never throws, never blocks.</summary>
    public List<RuleWarning> CheckClaim(string contractNo, ClaimLine c, ContractItem? item = null)
    {
        var res = new List<RuleWarning>();
        var key = $"{c.Subcontractor}|INV-{c.InvoiceNo}|{c.Key}";
        foreach (var r in RulesOf(contractNo, RuleTypes.HeightBand))
        {
            if (c.QtyAbove45 <= 0) continue;
            if (c.QtyAbove45 > c.Qty + 1e-9)
                res.Add(W(r, "HEIGHT_QTY", $"{c.Room} {c.Stage} {c.Item}: {c.QtyAbove45:0.##} claimed above 4.5 m but the line is only {c.Qty:0.##}", RuleContexts.Ledger, key));
            if (c.HeightStatus is CheckStatus.Pending or CheckStatus.None)
                res.Add(W(r, "HEIGHT_CHECK", $"{c.Room} {c.Stage} {c.Item}: {c.QtyAbove45:0.##} claimed above 4.5 m - priced at the above-4.5 m rate only after the site check ({r.Summary})", RuleContexts.Ledger, key));
        }
        foreach (var r in RulesOf(contractNo, RuleTypes.LengthRule))
        {
            if (!c.LengthApplies || c.LengthClaimedQty <= c.Qty + 1e-9) continue;
            if (item != null && !r.AppliesTo(item.ItemNo)) continue;
            var meters = r.Num("meters", 15);
            if (c.RouteLengthTotal <= 0 && c.LengthGroups.Length == 0)
                res.Add(W(r, "LENGTH_PROOF", $"{c.Room} {c.Item}: {c.LengthClaimedQty:0.##} claimed for {c.Qty:0.##} plan points - the {meters:0} m rule needs the route lengths (marked drawing)", RuleContexts.Ledger, key));
            else
            {
                var allowed = Math.Max(c.Qty, c.RouteLengthTotal / meters);
                if (c.LengthClaimedQty > allowed + 0.05)
                    res.Add(W(r, "LENGTH_EXCESS", $"{c.Room} {c.Item}: claimed {c.LengthClaimedQty:0.##} but {c.RouteLengthTotal:0} m of route allows {allowed:0.#} (max(plan, L / {meters:0}))", RuleContexts.Ledger, key));
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ subcontractor invoice

    /// <summary>Stage % used on the invoice lines vs the payment terms, VAT treatment, retention and advance recovery.</summary>
    public List<RuleWarning> CheckInvoice(SubInvoice inv, IReadOnlyList<SubInvoiceLine> lines, IReadOnlyList<ContractItem> items, double? vatPctOnInvoice = null)
    {
        var res = new List<RuleWarning>();
        var key = inv.Title;
        var stages = RulesOf(inv.ContractNo, RuleTypes.PaymentStage).ToList();
        var byItem = items.Where(i => i.ContractNo == inv.ContractNo).GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var l in lines.Where(l => l.Kind == "ITEM" && l.CurrQty != 0))
        {
            if (!byItem.TryGetValue(l.ItemNo, out var item)) continue;
            var tray = item.Category is "TRAY" or "CABLE" or "PANEL" || item.Is2ndFixPulling && item.Category == "CABLE";
            var group = tray ? "TRAY" : "MAIN";
            var stage = tray ? "INSTALLATION" : item.FixStage;
            var rule = stages.FirstOrDefault(r => r.Str("group") == group && r.Str("stage") == stage);
            if (rule is null) continue;
            var pct = rule.Num("pct");
            if (l.StagePct > pct + 1e-6)
                res.Add(W(rule, "STAGE_PCT", $"item {l.ItemNo} ({stage}) invoiced at {l.StagePct:P0} - the contract pays {pct:P0} at this stage", RuleContexts.Invoice, $"{key}|{l.ItemNo}"));
        }
        foreach (var r in RulesOf(inv.ContractNo, RuleTypes.Retention))
            if (Math.Abs(inv.RetentionPct - r.Num("pct")) > 1e-6)
                res.Add(W(r, "RETENTION", $"retention on the invoice {inv.RetentionPct:P0}, contract {r.Num("pct"):P0}", RuleContexts.Invoice, key));
        if (!RulesOf(inv.ContractNo, RuleTypes.Retention).Any() && inv.RetentionPct > 0 && RulesOf(inv.ContractNo).Any())
            res.Add(new RuleWarning(inv.ContractNo, 0, RuleTypes.Retention, "RETENTION_NOT_IN_CONTRACT", $"retention {inv.RetentionPct:P0} deducted but the signed contract has no retention clause", "", "", 0, RuleContexts.Invoice, key)
                { Bypass = _bypasses.FirstOrDefault(b => b.RuleType == RuleTypes.Retention && b.ContextKey == key && b.Code == "RETENTION_NOT_IN_CONTRACT") });
        foreach (var r in RulesOf(inv.ContractNo, RuleTypes.AdvanceRecovery))
            if (Math.Abs(inv.AdvancePct - r.Num("pct")) > 1e-6)
                res.Add(W(r, "ADVANCE", $"advance on the invoice {inv.AdvancePct:P0}, contract {r.Num("pct"):P0}", RuleContexts.Invoice, key));
        foreach (var r in RulesOf(inv.ContractNo, RuleTypes.Vat))
            if (vatPctOnInvoice is double v && r.Str("treatment") == "EXCLUDED" && Math.Abs(v - r.Num("pct")) > 1e-6)
                res.Add(W(r, "VAT", $"contract prices exclude VAT {r.Num("pct"):P0}: the invoice applies {v:P0}", RuleContexts.Invoice, key));
        foreach (var r in RulesOf(inv.ContractNo, RuleTypes.ScopeExclusion))
            foreach (var l in lines.Where(l => l.Kind == "ITEM" && l.CurrQty != 0 && byItem.TryGetValue(l.ItemNo, out var it) && it.Category == "MATERIAL"))
                res.Add(W(r, "SCOPE", $"item {l.ItemNo} looks like material supply on a labour-only contract", RuleContexts.Invoice, $"{key}|{l.ItemNo}"));
        return res;
    }

    // ------------------------------------------------------------------ penalties (programme)

    /// <summary>Delay penalty exposure when the completion date has passed without handover (information for the invoice reviewer).</summary>
    public List<RuleWarning> CheckDelay(ContractTerms t, double contractValue, DateTime today, string context = RuleContexts.Invoice, string? key = null)
    {
        var res = new List<RuleWarning>();
        if (t.CompletionDate is not DateTime due || t.HandoverDate != null || today <= due) return res;
        var weeks = Math.Ceiling((today - due).TotalDays / 7);
        foreach (var r in RulesOf(t.ContractNo, RuleTypes.DelayPenalty))
        {
            var amount = weeks * r.Num("perWeek");
            var cap = RulesOf(t.ContractNo, RuleTypes.PenaltyCap).FirstOrDefault();
            var capAmount = cap is null ? double.MaxValue : cap.Num("pct") * contractValue;
            res.Add(W(r, "DELAY", $"{weeks:0} week(s) late (completion {due:dd-MMM-yyyy}): penalty SAR {Math.Min(amount, capAmount):N0}" +
                                   (cap != null ? $" (cap {cap.Num("pct"):P0} = SAR {capAmount:N0}{(amount >= capAmount ? ", REACHED" : "")})" : ""), context, key ?? t.ContractNo));
        }
        return res;
    }

    // ------------------------------------------------------------------ supplier side

    /// <summary>PO lines delivered above the PO tolerance (warning; the DN-line lock stays a hard rule).</summary>
    public static List<RuleWarning> CheckPoDeliveries(MatPo po, IEnumerable<(MatPoLine Line, double Delivered)> lines, IReadOnlyList<RuleBypass>? bypasses = null)
    {
        var rule = ContractRuleBuilder.PoTolerance(po);
        var tol = rule.Num("pct");
        var res = new List<RuleWarning>();
        foreach (var (l, d) in lines)
        {
            if (l.Qty <= 0 || d <= l.Qty * (1 + tol) + 1e-9) continue;
            var key = $"{po.PoNo}|{l.LineNo}";
            var w = new RuleWarning(po.PoNo, 0, RuleTypes.PoTolerance, "OVER_PO", $"{po.PoNo} line {l.LineNo:00}: delivered {d:N2} {l.Unit} of {l.Qty:N2} (tolerance ±{tol:P0})", "", rule.Summary, 0, RuleContexts.Po, key);
            res.Add(w with { Bypass = bypasses?.FirstOrDefault(b => b.RuleType == RuleTypes.PoTolerance && b.ContextKey == key && b.Code == "OVER_PO") });
        }
        return res;
    }

    // ------------------------------------------------------------------ bypass

    /// <summary>Records a bypass. A short reason is required; the row is audited by the store like every other write.</summary>
    public static RuleBypass Bypass(RuleWarning w, string reason, string user, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ArgumentException("A bypass needs a short reason (at least 3 characters).", nameof(reason));
        return new RuleBypass
        {
            ContractNo = w.ContractNo, RuleId = w.RuleId, RuleType = w.RuleType, Context = w.Context, ContextKey = w.ContextKey, Code = w.Code,
            Message = w.Message, Reason = reason.Trim(), BypassedBy = user, BypassedAt = now,
        };
    }

    /// <summary>Lines for the package checks report: every warning, bypassed or not, with the clause it comes from.</summary>
    public static IEnumerable<string> ReportLines(IEnumerable<RuleWarning> warnings) =>
        warnings.Select(w => $"{(w.IsBypassed ? "BYPASSED" : "WARNING")}  {w.RuleType}  {w.Message}  [{w.Source}]" +
                             (w.IsBypassed ? $"  - {w.Bypass!.BypassedBy} {w.Bypass.BypassedAt.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture)}: {w.Bypass.Reason}" : ""));
}
