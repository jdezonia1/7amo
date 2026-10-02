using Raffaello.Core.Contracts;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Tests;

/// <summary>Contract intelligence: rules from contract terms, warnings that never block, bypass with reason (audited), obligations, comparison.</summary>
public class ContractRulesTests
{
    private const string No = "C-028";

    private static ContractTerms Terms() => new()
    {
        ContractNo = No, Subcontractor = "ROOTS", ContractDate = new DateTime(2026, 5, 12), LabourOnly = true, VatTreatment = "EXCLUDED", VatPct = 0.15,
        DelayPenaltyPerWeek = 500, DelayPenaltyCapPct = 0.10, WarrantyMonths = 12, CompletionDate = new DateTime(2026, 9, 1),
    };

    private static List<ContractItem> Items() => new()
    {
        new() { ContractNo = No, ItemNo = "1", Description = "PVC 1st Fix لإرتفاع أقل 4.5 متر", FixStage = "1ST FIX", HeightBand = HeightBands.Low, Category = "OUTLET", Qty = 4000, Rate = 55, Unit = "عدد" },
        new() { ContractNo = No, ItemNo = "2", Description = "PVC 1st Fix لإرتفاع فوق 4.5 متر", FixStage = "1ST FIX", HeightBand = HeightBands.High, Category = "OUTLET", Qty = 400, Rate = 57, Unit = "عدد" },
        new() { ContractNo = No, ItemNo = "11", Description = "سحب سلك 2nd Fix اكثر من 15 متر يتم احتساب نقطه جديده كل 15 متر", FixStage = "2ND FIX", Category = "WIRING", Qty = 7000, Rate = 28, Unit = "عدد" },
    };

    private static List<ContractRule> Rules()
    {
        var clauses = new List<ContractClause> { new() { ContractNo = No, ClauseNo = "6", Title = "غرامات التأخير", TextAr = "غرامة تأخير 500 ريال عن كل أسبوع بحد أقصى 10%", Page = 3 } };
        var pays = new List<(string, string, double, string)> { ("MAIN", "1ST FIX", 0.9, ""), ("MAIN", "HANDOVER", 0.1, "") };
        var rules = ContractRuleBuilder.Build(Terms(), clauses, Items(), pays);
        for (var i = 0; i < rules.Count; i++) rules[i].Id = i + 1;
        return rules;
    }

    [Fact]
    public void Rules_are_built_from_terms_clauses_and_schedule()
    {
        var r = Rules();
        Assert.Contains(r, x => x.RuleType == RuleTypes.PaymentStage && x.Str("stage") == "1ST FIX");
        Assert.Contains(r, x => x.RuleType == RuleTypes.DelayPenalty && x.ClauseNo == "6" && x.SourcePage == 3);
        Assert.Contains(r, x => x.RuleType == RuleTypes.PenaltyCap && Math.Abs(x.Num("pct") - 0.1) < 1e-9);
        Assert.Contains(r, x => x.RuleType == RuleTypes.Warranty && x.Num("months") == 12);
        Assert.Contains(r, x => x.RuleType == RuleTypes.Vat && x.Str("treatment") == "EXCLUDED");
        Assert.Contains(r, x => x.RuleType == RuleTypes.ScopeExclusion);
        Assert.Contains(r, x => x.RuleType == RuleTypes.LengthRule && x.ItemNos == "11");
        Assert.Contains(r, x => x.RuleType == RuleTypes.HeightBand && x.ItemNos == "2");
        Assert.DoesNotContain(r, x => x.RuleType == RuleTypes.Retention);
    }

    [Fact]
    public void Claim_warnings_quote_the_rule_and_never_block()
    {
        var eng = new ContractRuleEngine(Rules());
        var claim = new ClaimLine { Subcontractor = "ROOTS", InvoiceNo = 3, Room = "P2-106", Stage = "2ND FIX", Item = "DATA", Qty = 40, LengthApplies = true, LengthClaimedQty = 150 };
        var w = eng.CheckClaim(No, claim, Items()[2]);
        Assert.Contains(w, x => x.Code == "LENGTH_PROOF" && x.RuleType == RuleTypes.LengthRule);
        claim.RouteLengthTotal = 900;   // 900 / 15 = 60 allowed
        w = eng.CheckClaim(No, claim, Items()[2]);
        Assert.Contains(w, x => x.Code == "LENGTH_EXCESS" && x.Message.Contains("60"));
        var high = new ClaimLine { Subcontractor = "ROOTS", Room = "P2-106", Stage = "1ST FIX", Item = "POWER", Qty = 10, QtyAbove45 = 12, HeightStatus = CheckStatus.Pending };
        w = eng.CheckClaim(No, high);
        Assert.Contains(w, x => x.Code == "HEIGHT_QTY");
        Assert.Contains(w, x => x.Code == "HEIGHT_CHECK");
        Assert.All(w, x => Assert.False(x.IsBypassed));
    }

    [Fact]
    public void Invoice_warnings_stage_pct_retention_vat()
    {
        var eng = new ContractRuleEngine(Rules());
        var inv = new SubInvoice { ContractNo = No, Subcontractor = "ROOTS", InvoiceNo = 2, RetentionPct = 0.10 };
        var lines = new List<SubInvoiceLine> { new() { ItemNo = "1", CurrQty = 100, Rate = 55, StagePct = 1.0 }, new() { ItemNo = "2", CurrQty = 5, Rate = 57, StagePct = 0.9 } };
        var w = eng.CheckInvoice(inv, lines, Items(), vatPctOnInvoice: 0.05);
        Assert.Contains(w, x => x.Code == "STAGE_PCT" && x.ContextKey.EndsWith("|1"));
        Assert.DoesNotContain(w, x => x.Code == "STAGE_PCT" && x.ContextKey.EndsWith("|2"));
        Assert.Contains(w, x => x.Code == "RETENTION_NOT_IN_CONTRACT");
        Assert.Contains(w, x => x.Code == "VAT");
        var delay = eng.CheckDelay(Terms(), 1_000_000, new DateTime(2026, 9, 20));
        Assert.Contains(delay, x => x.Code == "DELAY" && x.Message.Contains("1,500"));
    }

    [Fact]
    public void Bypass_needs_a_reason_is_audited_and_marks_the_warning()
    {
        var db = new Db(Path.Combine(TestData.TempDir(), "rules.db"), "mohamed", "PC1");
        db.EnsureSchema();
        var docs = new SqliteDocumentStore(db);
        var rules = Rules();
        foreach (var r in rules) { r.Id = 0; docs.Insert(r); }
        var eng = docs.RuleEngine();
        var inv = new SubInvoice { ContractNo = No, Subcontractor = "ROOTS", InvoiceNo = 2, RetentionPct = 0 };
        var lines = new List<SubInvoiceLine> { new() { ItemNo = "1", CurrQty = 100, Rate = 55, StagePct = 1.0 } };
        var w = eng.CheckInvoice(inv, lines, Items()).Single(x => x.Code == "STAGE_PCT");
        Assert.Throws<ArgumentException>(() => docs.RecordBypass(w, " ", DateTime.Now));
        var b = docs.RecordBypass(w, "Final 10% agreed with PM for this item (email 12-Sep)", new DateTime(2026, 9, 12));
        Assert.Equal("mohamed", b.BypassedBy);
        var again = docs.RuleEngine().CheckInvoice(inv, lines, Items()).Single(x => x.Code == "STAGE_PCT");
        Assert.True(again.IsBypassed);
        Assert.Contains(db.RecentAudit(20), a => a.Summary.Contains("BYPASS") && a.Summary.Contains("PM"));
        var report = ContractRuleEngine.ReportLines(new[] { again }).Single();
        Assert.StartsWith("BYPASSED", report);
        Assert.Contains("mohamed", report);
    }

    [Fact]
    public void Po_tolerance_is_a_warning()
    {
        var po = new MatPo { PoNo = "RAF-P.O-E-045-2026", ToleranceHeaderPct = 0, ToleranceClausePct = 0.05 };
        var w = ContractRuleEngine.CheckPoDeliveries(po, new[] { (new MatPoLine { LineNo = 1, Qty = 1000, Unit = "M" }, 1040.0), (new MatPoLine { LineNo = 2, Qty = 1000, Unit = "M" }, 1080.0) });
        var one = Assert.Single(w);
        Assert.Contains("line 02", one.Message);
    }

    [Fact]
    public void Obligations_calendar_and_queue()
    {
        var t = Terms();
        var ob = ObligationsCalendar.Build(new[] { t }, Rules(), new Dictionary<string, double> { [No] = 1_000_000 });
        Assert.Contains(ob, o => o.Kind == ObligationKinds.Handover && o.Date == new DateTime(2026, 9, 1));
        Assert.Contains(ob, o => o.Kind == ObligationKinds.PenaltyStart);
        Assert.Contains(ob, o => o.Kind == ObligationKinds.PenaltyCap && o.Date == new DateTime(2026, 9, 1).AddDays(7 * 200));
        var q = ObligationsCalendar.Queue(ob, new DateTime(2026, 8, 25)).ToList();
        Assert.Contains(q, x => x.Category == "CONTRACT" && x.Title.Contains("HANDOVER"));
        t.HandoverDate = new DateTime(2026, 9, 10);
        t.RetentionPct = 0.05;
        ob = ObligationsCalendar.Build(new[] { t }, Rules());
        Assert.Contains(ob, o => o.Kind == ObligationKinds.WarrantyEnd && o.Date == new DateTime(2027, 9, 10));
        Assert.Contains(ob, o => o.Kind == ObligationKinds.RetentionRelease);
        Assert.DoesNotContain(ob, o => o.Kind == ObligationKinds.PenaltyStart);
    }

    [Fact]
    public void Contracts_compare_side_by_side()
    {
        var a = Items();
        var b = Items().Select(i => new ContractItem { ContractNo = "C-099", ItemNo = "X" + i.ItemNo, Description = i.Description, FixStage = i.FixStage, HeightBand = i.HeightBand, Category = i.Category, Unit = i.Unit, Rate = i.Rate + 5 }).ToList();
        var rates = ContractComparison.Rates(a.Concat(b));
        Assert.Contains(rates, r => r.Rates.Count == 2 && r.Rates["C-028"] == 55 && r.Rates["C-099"] == 60);
        var t2 = Terms(); t2.ContractNo = "C-099"; t2.DelayPenaltyPerWeek = 1000;
        var terms = ContractComparison.Terms(new[] { Terms(), t2 }, Rules());
        var pen = terms.Single(r => r.Term.StartsWith("Delay"));
        Assert.Equal("SAR 500", pen.ByContract["C-028"]);
        Assert.Equal("SAR 1,000", pen.ByContract["C-099"]);
    }
}
