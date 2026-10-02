using System.Globalization;
using System.Text.RegularExpressions;
using Raffaello.Core.Contracts;

namespace Raffaello.Core.Assemblies;

/// <summary>A price found for a component (or a labour rate from the contract).</summary>
public sealed record PriceHit(double Price, string Source, string Ref, DateTime? Date = null, string Supplier = "", double Score = 1, string Note = "");

/// <summary>Finds a unit price for a material / equipment specification.</summary>
public interface IPriceResolver
{
    PriceHit? Find(string spec, string unit, string kind);
}

/// <summary>Finds the subcontract labour rate for an item and a labour category (1ST FIX, 2ND FIX, 3RD FIX, FLEX, TERMINATION, SELF).</summary>
public interface ILabourRateResolver
{
    PriceHit? Find(ItemSpec spec, string category);
}

/// <summary>Overhead / profit / labour mode for the rate build-up.</summary>
public sealed class AssemblySettings
{
    /// <summary>0.10 = 10 %.</summary>
    public double OverheadPct { get; set; } = 0.10;
    public double ProfitPct { get; set; } = 0.10;
    /// <summary>AUTO (contract items: HOURS, BOQ / text: SUBCONTRACT), SUBCONTRACT or HOURS.</summary>
    public string LabourMode { get; set; } = "AUTO";
    /// <summary>Minimum fingerprint score for a PO line to price a component.</summary>
    public double PoMatchScore { get; set; } = 0.75;
    public double PriceListMatchScore { get; set; } = 0.6;
    public string Currency { get; set; } = "SAR";

    public static readonly string[] LabourModes = { "AUTO", LabourBases.Subcontract, LabourBases.Hours };

    public string ResolveLabourMode(ItemSourceKind source) =>
        LabourMode is LabourBases.Subcontract or LabourBases.Hours ? LabourMode : source == ItemSourceKind.Contract ? LabourBases.Hours : LabourBases.Subcontract;
}

/// <summary>One row of the breakdown (per unit of the BOQ item).</summary>
public sealed class BreakdownLine
{
    public int Order { get; init; }
    public string Key { get; init; } = "";
    public string Component { get; init; } = "";
    public string Spec { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Kind { get; init; } = "";
    public double QtyPerUnit { get; init; }
    public double WastePct { get; init; }
    /// <summary>Quantity per unit of the item including waste.</summary>
    public double TotalQty { get; init; }
    public double UnitPrice { get; init; }
    public double Amount { get; init; }
    public string PriceSource { get; init; } = "";
    public string PriceRef { get; init; } = "";
    public DateTime? PriceDate { get; init; }
    /// <summary>Counted in the built-up rate (false: free issue for labour-only items, labour for supply-only items).</summary>
    public bool Included { get; init; } = true;
    /// <summary>"" / PRICE UNKNOWN / DEFAULT PRICE / FORMULA ERROR / FREE ISSUE.</summary>
    public string Flag { get; init; } = "";
    public string Note { get; init; } = "";
    public string PriceDateText => PriceDate is { } d ? d.ToString("dd-MMM-yy", CultureInfo.InvariantCulture) : "";
}

/// <summary>Breakdown and built-up rate of one item.</summary>
public sealed class Breakdown
{
    public ItemSpec Spec { get; init; } = new();
    public string TemplateCode { get; init; } = "";
    public string TemplateName { get; init; } = "";
    public string Unit { get; init; } = "";
    public string LabourMode { get; init; } = "";
    public List<BreakdownLine> Lines { get; init; } = new();
    public Dictionary<string, double> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double Material { get; init; }
    public double Labour { get; init; }
    public double Equipment { get; init; }
    public double Direct => Material + Labour + Equipment;
    public double OverheadPct { get; init; }
    public double ProfitPct { get; init; }
    public double Overhead => Math.Round(Direct * OverheadPct, 2, MidpointRounding.AwayFromZero);
    public double Profit => Math.Round((Direct + Overhead) * ProfitPct, 2, MidpointRounding.AwayFromZero);
    /// <summary>Built-up unit rate = direct x (1 + OH) x (1 + profit), 2 decimals (same rule as the Variations NEW ITEM rate).</summary>
    public double Rate { get; init; }
    /// <summary>BOQ / contract rate the build-up is compared with.</summary>
    public double? ReferenceRate { get; init; }
    public string ReferenceLabel { get; init; } = "";
    public double? Margin => ReferenceRate is { } r ? Math.Round(r - Rate, 2) : null;
    public double? MarginPct => ReferenceRate is { } r && r > 0 ? (r - Rate) / r : null;
    /// <summary>Free-issue materials (labour-only items) - listed for the requirements, not in the rate.</summary>
    public double FreeIssueMaterial { get; init; }
    public List<string> Flags { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public int UnknownPrices => Lines.Count(l => l.Included && l.Flag == BreakdownFlags.Unknown);
    public int DefaultPrices => Lines.Count(l => l.Included && l.Flag == BreakdownFlags.Default);

    public string Verdict => ReferenceRate is not { } r ? "NO REFERENCE RATE"
        : r + 1e-9 < Direct ? "CONTRACT RATE BELOW COST"
        : r + 1e-9 < Rate ? "BELOW BUILT-UP RATE"
        : "OK";
}

public static class BreakdownFlags
{
    public const string Unknown = "PRICE UNKNOWN";
    public const string Default = "DEFAULT PRICE";
    public const string Error = "FORMULA ERROR";
    public const string FreeIssue = "FREE ISSUE";
    public const string Excluded = "NOT IN SCOPE";
}

/// <summary>Everything the calculator needs besides the spec and the template.</summary>
public sealed class CalcContext
{
    public IReadOnlyList<AsmParam> Globals { get; init; } = Array.Empty<AsmParam>();
    public IPriceResolver? Prices { get; init; }
    public ILabourRateResolver? LabourRates { get; init; }
    public AssemblySettings Settings { get; init; } = new();
    public ItemSourceKind Source { get; init; } = ItemSourceKind.Text;
    /// <summary>Per-item parameter overrides (route_len ...).</summary>
    public IReadOnlyDictionary<string, double>? Overrides { get; init; }
    /// <summary>Average route length per point for the item type (e.g. from the Drawings module) - replaces route_len unless overridden.</summary>
    public Func<string, double?>? RouteLength { get; init; }
    public double? ReferenceRate { get; init; }
    public string ReferenceLabel { get; init; } = "";
}

/// <summary>
/// [assemblies] Evaluates a template for a parsed item: quantity formulas -> quantities per unit (+ waste), price per component
/// (manual / PO / price list / default formula / unknown), labour from the subcontract rates or hours, overhead + profit -> built-up rate,
/// compared with the BOQ / contract rate.
/// </summary>
public static class AssemblyCalculator
{
    public static Breakdown Calculate(ItemSpec spec, AssemblyTemplate template, CalcContext ctx)
    {
        var warnings = new List<string>();
        var vars = Variables(spec, template, ctx, warnings);
        var mode = ctx.Settings.ResolveLabourMode(ctx.Source);
        var lines = new List<BreakdownLine>();
        double material = 0, labour = 0, equipment = 0, freeIssue = 0;

        foreach (var c in template.Components.OrderBy(c => c.Order))
        {
            double qty;
            var flag = "";
            try { qty = Math.Max(0, Formula.Eval(c.QtyFormula, vars)); }
            catch (FormulaException ex) { qty = 0; flag = BreakdownFlags.Error; warnings.Add($"{c.Name}: {ex.Message}"); }
            if (c.Key.Length > 0) vars[c.Key] = qty;

            if (c.Kind == ComponentKinds.Labour && c.LabourBasis.Length > 0 && !c.LabourBasis.Equals(mode, StringComparison.OrdinalIgnoreCase)) continue;
            if (!spec.Covers(c.Stage)) continue;
            if (qty <= 1e-9 && flag.Length == 0) continue;

            var total = qty * (1 + Math.Max(0, c.WastePct));
            var specText = FillSpec(c.Spec, spec, vars);
            PriceHit? hit = null;
            if (flag.Length == 0)
            {
                if (c.Kind == ComponentKinds.Labour && c.LabourBasis == LabourBases.Subcontract && ctx.LabourRates != null)
                    hit = ctx.LabourRates.Find(spec, c.LabourCategory);
                if (hit is null && c.Kind != ComponentKinds.Labour && ctx.Prices != null)
                    hit = ctx.Prices.Find(specText, c.Unit, c.Kind);
                if (hit is null && c.DefaultPrice.Trim().Length > 0)
                {
                    try
                    {
                        var p = Formula.Eval(c.DefaultPrice, vars);
                        var isRate = c.Kind == ComponentKinds.Labour;
                        hit = new PriceHit(p, PriceSources.Default, isRate && c.LabourBasis == LabourBases.Subcontract ? "template default (SUB-ELE-028-2026 schedule)" : "template default", Note: "indicative - confirm");
                    }
                    catch (FormulaException ex) { warnings.Add($"{c.Name} price: {ex.Message}"); }
                }
            }
            var price = hit?.Price ?? 0;
            var included = c.Kind switch
            {
                ComponentKinds.Labour => spec.Supply != SupplyScopes.SupplyOnly,
                _ => spec.Supply != SupplyScopes.LabourOnly,
            };
            if (flag.Length == 0)
                flag = !included ? (c.Kind == ComponentKinds.Labour ? BreakdownFlags.Excluded : BreakdownFlags.FreeIssue)
                     : hit is null ? BreakdownFlags.Unknown
                     : hit.Source == PriceSources.Default ? BreakdownFlags.Default : "";
            var amount = Math.Round(total * price, 4);
            if (included)
            {
                switch (c.Kind)
                {
                    case ComponentKinds.Labour: labour += amount; break;
                    case ComponentKinds.Equipment: equipment += amount; break;
                    default: material += amount; break;
                }
            }
            else if (c.Kind != ComponentKinds.Labour) freeIssue += amount;
            lines.Add(new BreakdownLine
            {
                Order = c.Order, Key = c.Key, Component = c.Name, Spec = specText, Unit = c.Unit, Stage = c.Stage, Kind = c.Kind,
                QtyPerUnit = Math.Round(qty, 4), WastePct = c.WastePct, TotalQty = Math.Round(total, 4), UnitPrice = Math.Round(price, 4), Amount = Math.Round(amount, 2),
                PriceSource = hit?.Source ?? (flag == BreakdownFlags.Error ? "" : PriceSources.Unknown), PriceRef = hit?.Ref ?? "", PriceDate = hit?.Date,
                Included = included, Flag = flag, Note = string.Join("; ", new[] { c.Notes, hit?.Note ?? "" }.Where(s => s.Length > 0)),
            });
        }

        material = Math.Round(material, 2); labour = Math.Round(labour, 2); equipment = Math.Round(equipment, 2);
        var rate = Variations.VariationMath.BuildUpRate(material, labour, equipment, ctx.Settings.OverheadPct, ctx.Settings.ProfitPct);
        var flags = new List<string>();
        var unknown = lines.Count(l => l.Included && l.Flag == BreakdownFlags.Unknown);
        var defaults = lines.Count(l => l.Included && l.Flag == BreakdownFlags.Default);
        if (unknown > 0) flags.Add($"{unknown} component price(s) unknown - rate understated");
        if (defaults > 0) flags.Add($"{defaults} default (indicative) price(s) - confirm");
        if (!spec.Recognised) flags.Add("item type not recognised - pick the type / template");
        if (spec.Confidence is > 0 and < 0.6) flags.Add("low parse confidence - check the spec");
        if (warnings.Count > 0) flags.Add($"{warnings.Count} formula problem(s)");
        var b = new Breakdown
        {
            Spec = spec, TemplateCode = template.Code, TemplateName = template.Header.Name, Unit = spec.Unit.Length > 0 ? spec.Unit : template.Header.Unit, LabourMode = mode,
            Lines = lines, Variables = vars, Material = material, Labour = labour, Equipment = equipment,
            OverheadPct = ctx.Settings.OverheadPct, ProfitPct = ctx.Settings.ProfitPct, Rate = rate,
            ReferenceRate = ctx.ReferenceRate is > 0 ? ctx.ReferenceRate : null, ReferenceLabel = ctx.ReferenceLabel,
            FreeIssueMaterial = Math.Round(freeIssue, 2), Flags = flags, Warnings = warnings,
        };
        if (b.Verdict == "CONTRACT RATE BELOW COST") flags.Insert(0, $"{(ctx.ReferenceLabel.Length > 0 ? ctx.ReferenceLabel : "reference")} rate {b.ReferenceRate:N2} is below the direct cost {b.Direct:N2}");
        else if (b.Verdict == "BELOW BUILT-UP RATE") flags.Insert(0, $"{(ctx.ReferenceLabel.Length > 0 ? ctx.ReferenceLabel : "reference")} rate {b.ReferenceRate:N2} is below the built-up rate {b.Rate:N2}");
        return b;
    }

    // ------------------------------------------------------------------ variables

    /// <summary>Names every formula can use: base defaults, global parameters, template parameters, values from the spec, per-item overrides.</summary>
    public static Dictionary<string, double> Variables(ItemSpec spec, AssemblyTemplate template, CalcContext ctx, List<string>? warnings = null)
    {
        var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["conduit_size"] = 20, ["wire_size"] = 1.5, ["wire_cores"] = 3, ["cable_size"] = 0, ["cable_cores"] = 0, ["gangs"] = 1, ["ways"] = 0, ["amps"] = 0,
            ["tray_width"] = 300, ["compartments"] = 0, ["watts"] = 0, ["route_len"] = 0, ["drop_len"] = 0, ["bends"] = 0, ["with_term"] = 1, ["with_cpc"] = 0,
        };
        foreach (var p in ctx.Globals) v[p.Name] = p.Value;
        foreach (var p in template.Params) v[p.Name] = p.Value;

        if (ctx.RouteLength?.Invoke(spec.ItemType) is double rl && rl > 0 && template.Params.Any(p => p.Name == "route_len")) v["route_len"] = rl;
        if (spec.RouteLengthM > 0) v["route_len"] = spec.RouteLengthM;

        var conduit = spec.Conduit.Length > 0 ? spec.Conduit : Conduits.Pvc;
        v["is_pvc"] = conduit == Conduits.Pvc ? 1 : 0;
        v["is_emt"] = conduit == Conduits.Emt ? 1 : 0;
        v["is_rs"] = conduit == Conduits.Rs ? 1 : 0;
        v["is_flex"] = conduit == Conduits.Flex ? 1 : 0;
        var lighting = spec.ItemType is ItemTypes.LightingPoint or ItemTypes.DaliPoint;
        v["is_ceiling"] = spec.Mount == Mounts.Ceiling || lighting && spec.Mount is "" or Mounts.Both ? 1 : 0;
        v["is_wall"] = spec.Mount == Mounts.Wall ? 1 : 0;
        v["mount_stand"] = spec.Mount == "STAND" ? 1 : 0;
        v["is_high"] = spec.Height == HeightBands.High ? 1 : 0;
        v["is_concealed"] = spec.Installation == "CONCEALED" ? 1 : 0;
        v["is_fr"] = spec.FireRated ? 1 : 0;
        v["is_earth"] = spec.EarthCable ? 1 : 0;
        v["is_armoured"] = spec.Armoured || spec.CableCores > 1 && spec.Sheath != "PVC" && spec.CableSizeMm2 >= 4 ? 1 : 0;
        v["facade"] = spec.Facade ? 1 : 0;
        if (spec.ConduitSizeMm > 0) v["conduit_size"] = spec.ConduitSizeMm;
        if (spec.WireSizeMm2 > 0) v["wire_size"] = spec.WireSizeMm2;
        if (spec.WireCores > 0) v["wire_cores"] = spec.WireCores;
        if (spec.CableSizeMm2 > 0) v["cable_size"] = spec.CableSizeMm2;
        if (spec.CableCores > 0) v["cable_cores"] = spec.CableCores;
        if (spec.Gangs > 0) v["gangs"] = spec.Gangs;
        if (spec.Ways > 0) v["ways"] = spec.Ways;
        if (spec.Amps > 0) v["amps"] = spec.Amps;
        if (spec.TrayWidthMm > 0) v["tray_width"] = spec.TrayWidthMm;
        if (spec.Compartments > 0) v["compartments"] = spec.Compartments;
        if (spec.Watts > 0) v["watts"] = spec.Watts;
        if (spec.Accessories.Any(a => a.Equals("CAT6", StringComparison.OrdinalIgnoreCase)) && spec.ItemType == ItemTypes.DataPoint && spec.Gangs >= 2) v["wire_cores"] = spec.Gangs;
        // cable runs: terminations are part of the item unless it is a labour-only pulling item that does not mention them
        if (spec.ItemType == ItemTypes.CableRun)
            v["with_term"] = spec.Supply != SupplyScopes.LabourOnly || spec.Accessories.Contains("TERMINATIONS") ? 1 : 0;
        v["cpc_size"] = CpcSize(v["cable_size"]);
        if (spec.CpcSizeMm2 > 0) { v["cpc_size"] = spec.CpcSizeMm2; v["with_cpc"] = 1; }

        if (ctx.Overrides != null)
            foreach (var (k, val) in ctx.Overrides) v[k] = val;
        if (template.ItemType == ItemTypes.CableRun && v["cable_size"] <= 0) warnings?.Add("cable size unknown - set it in the spec");
        return v;
    }

    /// <summary>Protective conductor size for a phase size (BS 7671 table 54.7: S &lt;= 16 -> S, 16 &lt; S &lt;= 35 -> 16, S &gt; 35 -> S / 2 rounded to a standard size).</summary>
    public static double CpcSize(double s)
    {
        if (s <= 0) return 0;
        if (s <= 16) return s;
        if (s <= 35) return 16;
        var half = s / 2;
        var std = new[] { 25, 35, 50, 70, 95, 120, 150, 185, 240 };
        return std.FirstOrDefault(x => x >= half - 1e-9, 240);
    }

    // ------------------------------------------------------------------ spec text

    public static string ConduitName(string conduit) => conduit switch
    {
        Conduits.Emt => "EMT",
        Conduits.Rs => "RS (GI rigid steel)",
        Conduits.Flex => "Flexible",
        _ => "PVC",
    };

    /// <summary>Fills {placeholders} in a component specification.</summary>
    public static string FillSpec(string template, ItemSpec spec, IReadOnlyDictionary<string, double> vars)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template;
        string N(string k) => vars.TryGetValue(k, out var x) ? x.ToString("0.##", CultureInfo.InvariantCulture) : "";
        var conduit = spec.Conduit.Length > 0 ? spec.Conduit : Conduits.Pvc;
        var gangs = vars.TryGetValue("gangs", out var g) ? (int)Math.Max(1, g) : 1;
        return Regex.Replace(template, @"\{(\w+)\}", m => m.Groups[1].Value switch
        {
            "conduit" => ConduitName(conduit),
            "box" => BoxName(spec, conduit, gangs, vars),
            "gangs_txt" => gangs >= 2 ? $"{gangs}G (twin)" : "1G",
            "system" => spec.System.Length > 0 ? spec.System : "system",
            "mount_txt" => spec.Mount == "STAND" ? "stand-mounted" : "wall-mounted",
            "cable" => CableText(spec, vars),
            "cpc" => $"1C x {N("cpc_size")}mm2 CU/LSOH G/Y earth cable",
            "cable_core_size" => $"{(spec.CableCores > 0 ? spec.CableCores : 4)}X{N("cable_size")}mm2",
            "tie" => vars.TryGetValue("cable_size", out var cs) && cs >= 95 ? "Cable cleat (trefoil / single)" : "Cable tie 300 mm",
            var k => N(k),
        });
    }

    public static string CableText(ItemSpec spec, IReadOnlyDictionary<string, double> vars)
    {
        var size = vars.TryGetValue("cable_size", out var s) ? s : spec.CableSizeMm2;
        var cores = vars.TryGetValue("cable_cores", out var c) && c > 0 ? (int)c : Math.Max(1, spec.CableCores);
        var sz = size.ToString("0.##", CultureInfo.InvariantCulture);
        var cond = spec.Conductor == "AL" ? "AL" : "CU";
        if (spec.EarthCable || cores == 1 && spec.Sheath != "PVC" && !spec.Armoured)
            return $"1C x {sz}mm2 {cond}/{(spec.FireRated ? "MICA/" : "")}LSOH{(spec.EarthCable ? " G/Y earth" : "")} cable";
        var sheath = spec.Sheath == "PVC" ? "PVC" : "LSOH";
        var armour = spec.Armoured || cores > 1 && size >= 4 ? "/SWA" : "";
        return $"{cores}X{sz}mm2 {cond}/{(spec.FireRated ? "MICA/" : "")}XLPE{armour}/{sheath} cable";
    }

    public static string BoxName(ItemSpec spec, string conduit, int gangs, IReadOnlyDictionary<string, double> vars)
    {
        var size = vars.TryGetValue("conduit_size", out var cs) ? cs.ToString("0", CultureInfo.InvariantCulture) : "20";
        var metal = conduit is Conduits.Emt or Conduits.Rs;
        if (spec.BoxType.Length > 0 && spec.BoxType != "BACK BOX") return $"{(metal ? "GI" : "PVC")} {spec.BoxType.ToLowerInvariant()} {size} mm";
        var ceiling = vars.TryGetValue("is_ceiling", out var ic) && ic > 0;
        if (spec.ItemType == ItemTypes.FloorBox) return "Floor box";
        if (ceiling && spec.ItemType is ItemTypes.LightingPoint or ItemTypes.DaliPoint or ItemTypes.FireAlarmPoint or ItemTypes.ElvPoint or ItemTypes.GrmsPoint)
            return $"{(metal ? "GI" : "PVC")} circular box {size} mm (looping)";
        return $"{(metal ? "GI" : "PVC")} back box {(gangs >= 2 ? "2G" : "1G")} 47 mm";
    }
}
