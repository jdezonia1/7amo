using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Contracts.Rules;

public static class RuleTypes
{
    public const string PaymentStage = "PAYMENT_STAGE";
    public const string Retention = "RETENTION";
    public const string AdvanceRecovery = "ADVANCE_RECOVERY";
    /// <summary>2nd-fix pulling: a point longer than N m counts one more point per N m (15 m rule).</summary>
    public const string LengthRule = "LENGTH_RULE";
    /// <summary>Home-run: a new point after N m of horizontal route (30 m).</summary>
    public const string HomerunRule = "HOMERUN_RULE";
    /// <summary>Rates split below / above a height (4.5 m bands).</summary>
    public const string HeightBand = "HEIGHT_BAND";
    public const string DelayPenalty = "DELAY_PENALTY";
    public const string PenaltyCap = "PENALTY_CAP";
    public const string PoTolerance = "PO_TOLERANCE";
    public const string Warranty = "WARRANTY";
    public const string Vat = "VAT";
    /// <summary>Scope exclusions, e.g. labour only (materials by MOBCO).</summary>
    public const string ScopeExclusion = "SCOPE_EXCLUSION";

    public static readonly string[] All = { PaymentStage, Retention, AdvanceRecovery, LengthRule, HomerunRule, HeightBand, DelayPenalty, PenaltyCap, PoTolerance, Warranty, Vat, ScopeExclusion };
}

/// <summary>
/// A machine-readable contract term (from the signed contract, the schedule or entered by the user). Parameters are JSON
/// (e.g. {"stage":"1ST FIX","pct":0.9}); the source clause, its text and page are kept so every warning can quote them.
/// Rules only ever produce WARNINGS - they never block an entry.
/// </summary>
public sealed class ContractRule : Entity
{
    public string ContractNo { get; set; } = "";
    public string RuleType { get; set; } = "";
    public string ParamsJson { get; set; } = "{}";
    public string Summary { get; set; } = "";
    public string ClauseNo { get; set; } = "";
    public int SourcePage { get; set; }
    public string SourceText { get; set; } = "";
    public long SourceDocId { get; set; }
    /// <summary>Contract items the rule applies to (comma list), empty = whole contract.</summary>
    public string ItemNos { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>READ (from the document) / USER (entered or edited).</summary>
    public string Origin { get; set; } = "READ";
    public string Status { get; set; } = "DRAFT";

    public JsonElement Params => JsonDocument.Parse(string.IsNullOrWhiteSpace(ParamsJson) ? "{}" : ParamsJson).RootElement;
    public double Num(string key, double fallback = 0) => Params.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    public string Str(string key) => Params.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public IEnumerable<string> Items => ItemNos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool AppliesTo(string itemNo) => ItemNos.Length == 0 || Items.Contains(itemNo);
    public static string ToJson(object o) => JsonSerializer.Serialize(o);
}

/// <summary>A warning was bypassed: who, when, why (audited, shown in the invoice package checks report).</summary>
public sealed class RuleBypass : Entity
{
    public string ContractNo { get; set; } = "";
    public long RuleId { get; set; }
    public string RuleType { get; set; } = "";
    /// <summary>LEDGER / INVOICE / SUPPLIER INVOICE / PO / DN / PACKAGE.</summary>
    public string Context { get; set; } = "";
    /// <summary>The record the warning was raised on (claim key, invoice title, PO line ...).</summary>
    public string ContextKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string Reason { get; set; } = "";
    public string BypassedBy { get; set; } = "";
    public DateTime BypassedAt { get; set; }
}

public static class RuleContexts
{
    public const string Ledger = "LEDGER", Invoice = "INVOICE", SupplierInvoice = "SUPPLIER INVOICE", Po = "PO", Dn = "DN", Package = "PACKAGE";
}

/// <summary>A warning raised by a contract rule. Never blocks; can be bypassed with a reason.</summary>
public sealed record RuleWarning(string ContractNo, long RuleId, string RuleType, string Code, string Message, string ClauseNo, string ClauseText, int Page, string Context, string ContextKey)
{
    public RuleBypass? Bypass { get; init; }
    public bool IsBypassed => Bypass != null;
    public string Source => ClauseNo.Length > 0 ? $"clause {ClauseNo}{(Page > 0 ? $", page {Page}" : "")}" : Page > 0 ? $"page {Page}" : "contract";
    public override string ToString() => $"[{RuleType}] {Message} ({Source}){(IsBypassed ? $" - BYPASSED by {Bypass!.BypassedBy}: {Bypass.Reason}" : "")}";
}

/// <summary>Turns the terms, clauses and schedule read from a contract into rules.</summary>
public static class ContractRuleBuilder
{
    public static List<ContractRule> Build(ContractTerms t, IReadOnlyList<ContractClause> clauses, IReadOnlyList<ContractItem> items, IReadOnlyList<(string Group, string Stage, double Pct, string Source)>? payments = null)
    {
        var rules = new List<ContractRule>();
        ContractClause? ClauseWith(string rx) => clauses.FirstOrDefault(c => Regex.IsMatch(ArabicText.Fold(c.Title + " " + c.TextAr), rx));
        ContractRule R(string type, object p, string summary, ContractClause? c, string items = "", string? text = null) => new()
        {
            ContractNo = t.ContractNo, RuleType = type, ParamsJson = ContractRule.ToJson(p), Summary = summary, ClauseNo = c?.ClauseNo ?? "", SourcePage = c?.Page ?? 0,
            SourceText = Excerpt(text ?? c?.TextAr ?? ""), SourceDocId = t.SourceDocId, ItemNos = items,
        };
        var pay = ClauseWith(@"شروط\s*الدفع|PAYMENT");
        foreach (var p in payments ?? Array.Empty<(string, string, double, string)>())
            rules.Add(R(RuleTypes.PaymentStage, new { group = p.Group, stage = p.Stage, pct = p.Pct }, $"{(p.Group == "TRAY" ? "Cable tray / pulling / panels" : "Outlets")}: {p.Stage} {p.Pct:P0}", pay));
        if (t.RetentionPct is double ret) rules.Add(R(RuleTypes.Retention, new { pct = ret }, $"Retention {ret:P0}", ClauseWith(@"محتجزات|ضمان\s*حسن|RETENTION")));
        if (t.AdvancePct is double adv) rules.Add(R(RuleTypes.AdvanceRecovery, new { pct = adv }, $"Advance {adv:P0}, recovered pro-rata", ClauseWith(@"دفعه\s*مقدمه|ADVANCE")));
        var delay = ClauseWith(@"غرام|التاخير|PENALT");
        if (t.DelayPenaltyPerWeek is double pw) rules.Add(R(RuleTypes.DelayPenalty, new { perWeek = pw, currency = "SAR" }, $"Delay penalty SAR {pw:N0} per week", delay));
        if (t.DelayPenaltyCapPct is double cap) rules.Add(R(RuleTypes.PenaltyCap, new { pct = cap }, $"Penalties capped at {cap:P0} of the contract value", delay));
        if (t.WarrantyMonths is int wm) rules.Add(R(RuleTypes.Warranty, new { months = wm }, $"Warranty {wm} months from handover", ClauseWith(@"الضمان|WARRANTY")));
        if (t.VatTreatment.Length > 0) rules.Add(R(RuleTypes.Vat, new { treatment = t.VatTreatment, pct = t.VatPct }, $"Prices {t.VatTreatment.ToLowerInvariant()} VAT {t.VatPct:P0}", ClauseWith(@"ضريبه|VAT")));
        if (t.LabourOnly) rules.Add(R(RuleTypes.ScopeExclusion, new { exclusion = "MATERIALS", labourOnly = true }, "Labour only - materials are supplied by MOBCO (no material items on this subcontract)", ClauseWith(@"مصنعيات|LABOU?R")));

        // schedule-driven rules: 15 m / 30 m route rules and 4.5 m height bands, with the items they cover
        var len = items.Where(i => Regex.IsMatch(ArabicText.Fold(i.Description), @"15\s*متر|15\s*M\b")).Select(i => i.ItemNo).ToList();
        if (len.Count > 0) rules.Add(R(RuleTypes.LengthRule, new { meters = 15.0, minPerPoint = 1.0 }, $"Point longer than 15 m: one extra point per extra 15 m (qty per point = max(1, L / 15)) - items {Compact(len)}", null, string.Join(",", len), items.First(i => i.ItemNo == len[0]).Description));
        var home = items.Where(i => Regex.IsMatch(ArabicText.Fold(i.Description), @"30\s*متر|HOMERUN")).Select(i => i.ItemNo).ToList();
        if (home.Count > 0) rules.Add(R(RuleTypes.HomerunRule, new { meters = 30.0 }, $"Home-run longer than 30 m (horizontal): a new point - items {Compact(home)}", null, string.Join(",", home), items.First(i => i.ItemNo == home[0]).Description));
        var high = items.Where(i => i.HeightBand == HeightBands.High).Select(i => i.ItemNo).ToList();
        var low = items.Where(i => i.HeightBand == HeightBands.Low).Select(i => i.ItemNo).ToList();
        if (high.Count > 0) rules.Add(R(RuleTypes.HeightBand, new { meters = 4.5, high = high, low = low }, $"Rates split at 4.5 m: {low.Count} items below, {high.Count} above (above-4.5 m qty must be checked on site)", null, string.Join(",", high)));
        return rules;
    }

    /// <summary>PO tolerance as a rule (from the PO terms: the clause wins over the header).</summary>
    public static ContractRule PoTolerance(Materials.MatPo po) => new()
    {
        ContractNo = po.PoNo, RuleType = RuleTypes.PoTolerance, ParamsJson = ContractRule.ToJson(new { pct = po.EffectiveTolerancePct, header = po.ToleranceHeaderPct, clause = po.ToleranceClausePct }),
        Summary = $"Delivered quantity per PO line within ±{po.EffectiveTolerancePct:P0}", SourceText = po.PenaltyText, Origin = "READ",
    };

    private static string Excerpt(string s) => s.Length <= 600 ? s : s[..600] + " ...";

    public static string Compact(IEnumerable<string> nos)
    {
        var l = nos.Select(n => int.TryParse(n, out var v) ? v : -1).Where(v => v >= 0).Distinct().OrderBy(v => v).ToList();
        var parts = new List<string>();
        for (var i = 0; i < l.Count;)
        {
            var j = i;
            while (j + 1 < l.Count && l[j + 1] == l[j] + 1) j++;
            parts.Add(i == j ? l[i].ToString(CultureInfo.InvariantCulture) : $"{l[i]}-{l[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
