using Raffaello.Core.Cables;
using Raffaello.Core.Contracts.Rules;
using Raffaello.Core.Data;
using Raffaello.Core.Documents;
using Raffaello.Core.Domain;
using Raffaello.Core.Statements;

namespace Raffaello.Core.Wiring;

/// <summary>
/// Site statement import preview: the cable flags of the CABLES rows and the contract-rule warnings of the claim lines, with BYPASS WITH
/// REASON right in the preview. A bypass is recorded exactly as on the Cables page (<see cref="CableService.Bypass"/>, a
/// <see cref="CableFlagDecision"/> keyed by the flag) or on the invoice WARNINGS tab (<see cref="DocumentStoreExtensions.RecordBypass"/>,
/// a <see cref="RuleBypass"/> keyed by the claim) - audited, and it applies to the same claim once it is posted.
/// </summary>
public static class StatementPreviewChecks
{
    /// <summary>Contract numbers of a subcontractor (project contracts for the building first, then contracts read from signed PDFs).</summary>
    public static List<string> ContractsOf(ProjectSnapshot s, IDocumentStore? docs, string subcontractor, string building)
    {
        var res = s.Contracts.Where(c => c.Subcontractor.Equals(subcontractor, StringComparison.OrdinalIgnoreCase) && (c.Building.Length == 0 || building.Length == 0 || c.Building.Equals(building, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.ContractNo).ToList();
        if (docs != null)
            try { res.AddRange(docs.All<ContractTerms>().Where(t => t.Subcontractor.Equals(subcontractor, StringComparison.OrdinalIgnoreCase)).Select(t => t.ContractNo)); }
            catch (Exception) { /* contract intelligence not available */ }
        return res.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Contract-rule warnings of every claim line in the preview (bypasses already recorded are attached).</summary>
    public static List<RuleWarning> RuleWarnings(StatementImportResult res, ProjectSnapshot s, IDocumentStore? docs)
    {
        res.RuleWarnings.Clear();
        if (docs is null || res.Claims.Count == 0) return res.RuleWarnings;
        try
        {
            var eng = docs.RuleEngine();
            var building = res.Claims.FirstOrDefault()?.Building ?? "";
            foreach (var no in ContractsOf(s, docs, res.Subcontractor, building))
            {
                if (!eng.RulesOf(no).Any()) continue;
                foreach (var c in res.Claims) res.RuleWarnings.AddRange(eng.CheckClaim(no, c));
            }
        }
        catch (Exception) { /* checks are advisory */ }
        return res.RuleWarnings;
    }

    /// <summary>Cable flags + contract warnings again (after a bypass).</summary>
    public static void Refresh(StatementImportResult res, IProjectStore store, ProjectSnapshot s, IDocumentStore? docs)
    {
        try { CableHooks.StatementFlags(res, store); } catch (Exception) { /* cable checks are advisory */ }
        RuleWarnings(res, s, docs);
    }

    /// <summary>Lets cable flags of the preview through with a reason (same record and audit as the Cables page).</summary>
    public static int BypassCableFlags(IProjectStore store, IEnumerable<CableFlag> flags, string reason)
    {
        Require(reason);
        var list = flags.Where(f => !f.IsBypassed).ToList();
        if (list.Count == 0) return 0;
        var svc = new CableService(CableStore.For(store));
        if (list.Count == 1) svc.Bypass(list[0], reason.Trim());
        else svc.BypassAll(list, reason.Trim());
        return list.Count;
    }

    /// <summary>Lets contract-rule warnings of the preview through with a reason (same record and audit as the invoice WARNINGS tab).</summary>
    public static int BypassRuleWarnings(IDocumentStore docs, IEnumerable<RuleWarning> warnings, string reason, DateTime now)
    {
        Require(reason);
        var n = 0;
        foreach (var w in warnings.Where(w => !w.IsBypassed))
        {
            docs.RecordBypass(w, reason.Trim(), now);
            n++;
        }
        return n;
    }

    private static void Require(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3) throw new ArgumentException("A bypass needs a short reason (who agreed, why) - at least 3 characters.", nameof(reason));
    }
}
