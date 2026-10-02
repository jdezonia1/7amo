using System.Globalization;
using Raffaello.Core.Documents;
using Raffaello.Core.Documents.Smart;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Contracts.Rules;

public sealed record Obligation(DateTime Date, string Kind, string ContractNo, string Subcontractor, string Text)
{
    public int DaysLeft(DateTime today) => (int)Math.Floor((Date.Date - today.Date).TotalDays);
}

public static class ObligationKinds
{
    public const string Handover = "HANDOVER", WarrantyEnd = "WARRANTY END", RetentionRelease = "RETENTION RELEASE", PenaltyStart = "PENALTY STARTS", PenaltyCap = "PENALTY CAP REACHED";
}

/// <summary>
/// Obligations calendar from the contract terms: handover / completion, warranty end (handover + warranty months), retention release,
/// delay penalty start and the week the penalty cap is reached. Feeds "Needs you today".
/// </summary>
public static class ObligationsCalendar
{
    public static List<Obligation> Build(IEnumerable<ContractTerms> terms, IEnumerable<ContractRule> rules, IReadOnlyDictionary<string, double>? contractValues = null)
    {
        var res = new List<Obligation>();
        var ruleList = rules.ToList();
        foreach (var t in terms.Where(t => t.ContractNo.Length > 0))
        {
            var sub = t.Subcontractor;
            double R(string type, string key) => ruleList.FirstOrDefault(r => r.ContractNo == t.ContractNo && r.RuleType == type && r.Enabled)?.Num(key) ?? 0;
            if (t.CompletionDate is DateTime due && t.HandoverDate is null)
                res.Add(new(due, ObligationKinds.Handover, t.ContractNo, sub, $"{t.ContractNo} {sub}: completion / handover due"));
            var months = t.WarrantyMonths ?? (int)R(RuleTypes.Warranty, "months");
            if (t.HandoverDate is DateTime ho)
            {
                if (months > 0)
                {
                    res.Add(new(ho.AddMonths(months), ObligationKinds.WarrantyEnd, t.ContractNo, sub, $"{t.ContractNo} {sub}: {months}-month warranty ends (handover {ho:dd-MMM-yyyy})"));
                    var ret = t.RetentionPct ?? R(RuleTypes.Retention, "pct");
                    if (ret > 0) res.Add(new(ho.AddMonths(months), ObligationKinds.RetentionRelease, t.ContractNo, sub, $"{t.ContractNo} {sub}: release retention {ret:P0} at the end of the warranty"));
                }
            }
            var perWeek = t.DelayPenaltyPerWeek ?? R(RuleTypes.DelayPenalty, "perWeek");
            if (t.CompletionDate is DateTime c && t.HandoverDate is null && perWeek > 0)
            {
                res.Add(new(c.AddDays(1), ObligationKinds.PenaltyStart, t.ContractNo, sub, $"{t.ContractNo} {sub}: delay penalty SAR {perWeek:N0} / week starts if not handed over"));
                var cap = t.DelayPenaltyCapPct ?? R(RuleTypes.PenaltyCap, "pct");
                if (cap > 0 && contractValues != null && contractValues.TryGetValue(t.ContractNo, out var value) && value > 0)
                {
                    var weeks = Math.Ceiling(cap * value / perWeek);
                    res.Add(new(c.AddDays(7 * weeks), ObligationKinds.PenaltyCap, t.ContractNo, sub, $"{t.ContractNo} {sub}: penalty cap {cap:P0} (SAR {cap * value:N0}) reached after {weeks:0} weeks"));
                }
            }
        }
        return res.OrderBy(o => o.Date).ToList();
    }

    /// <summary>Queue items for obligations due within <paramref name="horizonDays"/> days (overdue ones first).</summary>
    public static IEnumerable<QueueItem> Queue(IEnumerable<Obligation> obligations, DateTime today, int horizonDays = 30)
    {
        foreach (var o in obligations)
        {
            var d = o.DaysLeft(today);
            if (d > horizonDays) continue;
            if (d < -365) continue;
            var sev = d < 0 ? Verdict.Due : d <= 7 ? Verdict.Check : Verdict.Open;
            yield return new QueueItem(sev, "CONTRACT", $"{o.Kind}: {o.ContractNo} {o.Subcontractor}",
                $"{o.Text} - {(d < 0 ? $"{-d} days ago" : d == 0 ? "today" : $"in {d} days")} ({o.Date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)}).",
                new NavTarget("Contracts", Key: o.ContractNo), 2.2e8 - d);
        }
    }
}

/// <summary>Side-by-side comparison of subcontractors' contracts: terms, and the rate for the same kind of item.</summary>
public static class ContractComparison
{
    public sealed record TermRow(string Term, Dictionary<string, string> ByContract);
    public sealed record RateRow(string Key, string Description, string Unit, Dictionary<string, double> Rates)
    {
        public double Min => Rates.Values.DefaultIfEmpty(0).Min();
        public double Max => Rates.Values.DefaultIfEmpty(0).Max();
        public double SpreadPct => Min <= 0 ? 0 : (Max - Min) / Min;
    }

    public static List<TermRow> Terms(IReadOnlyList<ContractTerms> terms, IReadOnlyList<ContractRule> rules)
    {
        string Pct(double? v) => v is double d ? d.ToString("P0", CultureInfo.InvariantCulture) : "-";
        var rows = new List<(string, Func<ContractTerms, string>)>
        {
            ("Subcontractor", t => t.Subcontractor),
            ("Contract date", t => t.ContractDate?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) ?? "-"),
            ("Labour only", t => t.LabourOnly ? "YES" : "NO"),
            ("VAT", t => t.VatTreatment.Length == 0 ? "-" : $"{t.VatTreatment} {t.VatPct:P0}"),
            ("Payment (outlets)", t => t.PaymentTerms.Length == 0 ? "-" : t.PaymentTerms),
            ("Payment (tray / pulling / panels)", t => t.TrayPaymentTerms.Length == 0 ? "-" : t.TrayPaymentTerms),
            ("Retention", t => Pct(t.RetentionPct)),
            ("Advance", t => Pct(t.AdvancePct)),
            ("Delay penalty / week", t => t.DelayPenaltyPerWeek is double p ? $"SAR {p:N0}" : "-"),
            ("Penalty cap", t => Pct(t.DelayPenaltyCapPct)),
            ("Warranty", t => t.WarrantyMonths is int m ? $"{m} months" : "-"),
            ("15 m rule", t => rules.Any(r => r.ContractNo == t.ContractNo && r.RuleType == RuleTypes.LengthRule) ? "YES" : "-"),
            ("4.5 m height bands", t => rules.Any(r => r.ContractNo == t.ContractNo && r.RuleType == RuleTypes.HeightBand) ? "YES" : "-"),
        };
        return rows.Select(r => new TermRow(r.Item1, terms.ToDictionary(t => t.ContractNo, r.Item2))).ToList();
    }

    /// <summary>Items matched across contracts by their rate-driving attributes (stage, conduit, mount, height, category, systems, size) - item numbers differ between contracts.</summary>
    public static List<RateRow> Rates(IEnumerable<ContractItem> items)
    {
        return items.Where(i => i.Rate > 0)
            .GroupBy(Key)
            .Where(g => g.Select(i => i.ContractNo).Distinct().Count() >= 1)
            .Select(g => new RateRow(g.Key, g.First().Description, g.First().Unit,
                g.GroupBy(i => i.ContractNo).ToDictionary(x => x.Key, x => x.Min(i => i.Rate))))
            .OrderByDescending(r => r.Rates.Count).ThenByDescending(r => r.SpreadPct).ToList();
    }

    public static string Key(ContractItem i) =>
        string.Join("|", new[] { i.Category, i.FixStage, i.ConduitType, i.Mount, i.HeightBand, i.Systems, i.SizeKey, DocValidators.Unit(i.Unit) ?? i.Unit }.Select(x => (x ?? "").ToUpperInvariant()));
}
