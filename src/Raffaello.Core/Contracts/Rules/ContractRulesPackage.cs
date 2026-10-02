using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Packaging;

namespace Raffaello.Core.Contracts.Rules;

/// <summary>
/// Every invoice package gets "09_Contract_checks.pdf": the contract-rule warnings for the invoice (stage %, retention, VAT, height and
/// 15 m claims, delay exposure) with the clause each comes from, and every BYPASS with who / when / why.
/// </summary>
public static class ContractRulesPackage
{
    public static List<RuleWarning> WarningsFor(InvoiceBuild b, ProjectSnapshot s, IDocumentStore docs, DateTime today)
    {
        var eng = docs.RuleEngine();
        var h = b.Header;
        if (!eng.RulesOf(h.ContractNo).Any()) return new();
        var items = s.ContractItems.Where(i => i.ContractNo == h.ContractNo).ToList();
        var res = eng.CheckInvoice(h, b.Lines, items, InvoiceTotals.VatRate);
        var byItem = items.GroupBy(i => i.ItemNo).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in s.Claims.Where(c => c.Subcontractor == h.Subcontractor && c.InvoiceNo == h.InvoiceNo))
            res.AddRange(eng.CheckClaim(h.ContractNo, c));
        var terms = docs.All<ContractTerms>().FirstOrDefault(t => t.ContractNo == h.ContractNo);
        if (terms != null) res.AddRange(eng.CheckDelay(terms, items.Sum(i => i.Qty * i.Rate), today, RuleContexts.Package, h.Title));
        // bypasses recorded on this invoice that no longer raise a warning are still listed
        var shown = res.Select(w => w.Bypass?.Id).Where(id => id != null).ToHashSet();
        foreach (var bp in docs.All<RuleBypass>().Where(x => x.ContractNo == h.ContractNo && x.ContextKey.StartsWith(h.Title, StringComparison.Ordinal) && !shown.Contains(x.Id)))
            res.Add(new RuleWarning(bp.ContractNo, bp.RuleId, bp.RuleType, bp.Code, bp.Message, "", "", 0, bp.Context, bp.ContextKey) { Bypass = bp });
        return res;
    }

    public static Func<PackageRequest, string, DateTime, IEnumerable<(string Name, string Path, string Note)>> Section(Func<IDocumentStore?> docs) =>
        (req, work, stamp) =>
        {
            var store = docs();
            if (store is null) return Array.Empty<(string, string, string)>();
            List<RuleWarning> list;
            try { list = WarningsFor(req.Build, req.Snapshot, store, stamp); }
            catch (Exception) { return Array.Empty<(string, string, string)>(); }
            if (list.Count == 0) return Array.Empty<(string, string, string)>();
            var path = Path.Combine(work, "09_Contract_checks.pdf");
            Pdf(path, req.Build.Header, list, stamp);
            return new[] { ("09_Contract_checks.pdf", path, $"{list.Count(w => !w.IsBypassed)} contract warning(s), {list.Count(w => w.IsBypassed)} bypassed") };
        };

    private static void Pdf(string path, SubInvoice h, List<RuleWarning> list, DateTime stamp)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(c => c.Page(p =>
        {
            p.Size(PageSizes.A4.Landscape());
            p.Margin(28);
            p.DefaultTextStyle(t => t.FontSize(9));
            p.Header().Column(col =>
            {
                col.Item().Text($"CONTRACT CHECKS - {h.Title}").FontSize(14).Bold().FontColor("#8B0000");
                col.Item().Text($"{h.ContractNo}  |  generated {stamp:dd-MMM-yyyy}  |  warnings never block the invoice; a bypass needs a reason and is recorded").FontSize(8).FontColor("#555555");
            });
            p.Content().PaddingTop(8).Table(t =>
            {
                t.ColumnsDefinition(cd => { cd.ConstantColumn(70); cd.ConstantColumn(95); cd.RelativeColumn(3); cd.RelativeColumn(2); cd.RelativeColumn(2); });
                t.Header(hd =>
                {
                    foreach (var x in new[] { "STATUS", "RULE", "WARNING", "SOURCE (CLAUSE)", "BYPASS" })
                        hd.Cell().Background("#A6A6A6").Padding(3).Text(x).Bold().FontColor("#000000");
                });
                foreach (var w in list.OrderBy(w => w.IsBypassed).ThenBy(w => w.RuleType))
                {
                    t.Cell().BorderBottom(0.5f).BorderColor("#CCCCCC").Padding(3).Text(w.IsBypassed ? "BYPASSED" : "WARNING").Bold().FontColor(w.IsBypassed ? "#555555" : "#8B0000");
                    t.Cell().BorderBottom(0.5f).BorderColor("#CCCCCC").Padding(3).Text(w.RuleType);
                    t.Cell().BorderBottom(0.5f).BorderColor("#CCCCCC").Padding(3).Text(w.Message);
                    t.Cell().BorderBottom(0.5f).BorderColor("#CCCCCC").Padding(3).Text(w.Source + (w.ClauseText.Length > 0 ? ": " + Short(w.ClauseText) : ""));
                    t.Cell().BorderBottom(0.5f).BorderColor("#CCCCCC").Padding(3).Text(w.Bypass is { } b ? $"{b.BypassedBy} {b.BypassedAt:dd-MMM-yyyy HH:mm}: {b.Reason}" : "-");
                }
            });
            p.Footer().AlignRight().Text(x => { x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
        })).GeneratePdf(path);
    }

    private static string Short(string s) => s.Length <= 160 ? s : s[..160] + " ...";
}
