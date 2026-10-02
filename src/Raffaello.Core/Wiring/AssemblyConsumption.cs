using Raffaello.Core.Assemblies;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Insights;
using Raffaello.Core.Ledger;
using Raffaello.Core.Materials;

namespace Raffaello.Core.Wiring;

/// <summary>
/// Theoretical material consumption for the Insights reconciliation from the Assemblies templates: the ledger's installed points
/// (after SITE %) and the PROJECT QTY points per system x stage are broken down with the point template of that system
/// (<see cref="BulkRequirements.Reconcile"/>), giving material per point instead of the Insights default norms.
/// </summary>
public static class AssemblyConsumption
{
    /// <summary>Ledger system -> assemblies item type of its point template (null = no point template, e.g. GAS, CABLE PULLING).</summary>
    public static string? ItemTypeOf(string system) => (system ?? "").Trim().ToUpperInvariant() switch
    {
        "LIGHT" or "LIGHTING" or "EMERGENCY LIGHT" or "EM LIGHT" => ItemTypes.LightingPoint,
        "POWER" or "SOCKET" or "SOCKETS" => ItemTypes.SocketPoint,
        "DATA" => ItemTypes.DataPoint,
        "GRMS" => ItemTypes.GrmsPoint,
        "DALI" => ItemTypes.DaliPoint,
        "FIRE" or "FIRE ALARM" => ItemTypes.FireAlarmPoint,
        "AV" or "TV" or "CCTV" or "ACCESS" or "BMS" or "EVACUATION" or "SOUND" or "ELV" => ItemTypes.ElvPoint,
        _ => null,
    };

    /// <summary>Ledger stage -> template stage (FINAL FIX = 3RD FIX); null for stages that are not point stages (EMT, CEILING ...).</summary>
    public static string? StageOf(string stage) => Stages.Normalize(stage) switch
    {
        Stages.First => StageNames.First,
        Stages.Second => StageNames.Second,
        Stages.Final => StageNames.Third,
        _ => null,
    };

    /// <summary>
    /// Material lines (theoretical installed, whole-project need, delivered via the PO lines) for the reconciliation. Empty when no template
    /// covers the ledger systems. <paramref name="breakdown"/> computes a template breakdown for a spec (normally <see cref="AssemblyService.Run"/>).
    /// </summary>
    public static List<ConsumptionLine> Build(ProjectSnapshot s, MaterialsSnapshot? materials, Func<ItemSpec, Breakdown?> breakdown, string? building = null, double poMatchScore = 0.75)
    {
        bool In(string? b) => building is null || string.IsNullOrEmpty(b) || string.Equals(b, building, StringComparison.OrdinalIgnoreCase);
        var claims = LedgerRules.Effective(s.Claims.Where(c => In(c.Building))).Where(c => !c.Rework).ToList();
        var installed = claims.Select(c => (c.Item, c.Stage, Qty: c.Qty * c.SitePct));
        var planned = s.RoomQtys.Where(q => In(q.Building)).Select(q => (q.Item, q.Stage, q.Qty));

        var keys = installed.Concat(planned)
            .Select(x => (Type: ItemTypeOf(x.Item), Stage: StageOf(x.Stage), System: x.Item.Trim().ToUpperInvariant()))
            .Where(k => k.Type != null && k.Stage != null).Distinct().ToList();
        if (keys.Count == 0) return new();

        var templates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perKey = new Dictionary<(string System, string Stage), Breakdown>();
        foreach (var k in keys)
        {
            var spec = new ItemSpec { ItemType = k.Type!, System = k.System, Stages = new List<string> { k.Stage! }, Confidence = 1, Unit = "No" };
            Breakdown? b;
            try { b = breakdown(spec); } catch (Exception) { b = null; }
            if (b is null || b.TemplateCode.Length == 0 || b.Lines.Count == 0) continue;
            // components without a stage are counted once, with the 1st fix (the point is the same at every stage)
            var lines = b.Lines.Where(l => string.Equals(l.Stage, k.Stage, StringComparison.OrdinalIgnoreCase) || l.Stage.Length == 0 && k.Stage == StageNames.First).ToList();
            if (lines.Count == 0) continue;
            perKey[(k.System, k.Stage!)] = new Breakdown { Spec = b.Spec, TemplateCode = b.TemplateCode, TemplateName = b.TemplateName, Unit = b.Unit, Lines = lines };
            templates.Add(b.TemplateCode);
        }
        if (perKey.Count == 0) return new();

        static IEnumerable<(Breakdown, double, string)> Rows(IEnumerable<(string Item, string Stage, double Qty)> pts, Dictionary<(string System, string Stage), Breakdown> map) =>
            pts.GroupBy(x => (System: x.Item.Trim().ToUpperInvariant(), Stage: StageOf(x.Stage) ?? ""))
               .Where(g => map.ContainsKey(g.Key))
               .Select(g => (map[g.Key], g.Sum(x => x.Qty), $"{g.Key.System} {g.Key.Stage}"));

        var inst = Rows(installed, perKey).ToList();
        var plan = Rows(planned, perKey).ToList();
        var done = BulkRequirements.Reconcile(inst, materials, poMatchScore);
        var need = BulkRequirements.Reconcile(plan, null, poMatchScore).ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);
        // materials only in the plan (nothing installed yet) still show their project need
        var keysAll = done.Select(l => l.Key).Concat(need.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byKey = done.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);
        if (materials != null)
        {
            var missing = need.Values.Where(l => !byKey.ContainsKey(l.Key)).Select(l => new RequirementLine { Key = l.Key, Spec = l.Spec, Unit = l.Unit, Kind = l.Kind }).ToList();
            BulkRequirements.CompareWithPo(missing, materials, poMatchScore);
            foreach (var l in missing) byKey[l.Key] = l;
        }
        var source = "ASSEMBLY " + string.Join("/", templates.OrderBy(x => x));
        var res = new List<ConsumptionLine>();
        foreach (var k in keysAll)
        {
            byKey.TryGetValue(k, out var d);
            need.TryGetValue(k, out var n);
            var any = d ?? n!;
            res.Add(new ConsumptionLine(any.Spec, any.Unit, d?.InstalledQty ?? 0, n?.InstalledQty ?? 0, d?.DeliveredQty ?? 0, source)
            {
                Uses = (d?.Items ?? new()).Concat(n?.Items ?? new()).Distinct().ToList(),
                InstalledPoints = inst.Where(r => (d?.Items ?? new()).Contains(r.Item3)).Sum(r => r.Item2),
                PlannedPoints = plan.Where(r => (n?.Items ?? new()).Contains(r.Item3)).Sum(r => r.Item2),
                PoRefs = d?.PoRefs.ToList() ?? new(),
            });
        }
        return res.OrderBy(r => r.Material, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Same, with an <see cref="AssemblyService"/> (its library, prices and globals).</summary>
    public static List<ConsumptionLine> Build(AssemblyService svc, ProjectSnapshot s, MaterialsSnapshot? materials, string? building = null)
    {
        if (svc.Library.Templates.Count == 0) svc.Reload();
        if (svc.Library.Templates.Count == 0) return new();
        return Build(s, materials, spec => svc.Library.ForType(spec.ItemType) is null ? null : svc.Run(AssemblySource.FromText(spec.ItemType, "No"), spec), building, svc.Settings.PoMatchScore);
    }
}
