using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Assemblies;

/// <summary>
/// [assemblies] Subcontract labour rates from the contract schedules: every contract item is parsed with <see cref="ItemParser"/>
/// and the labour line of a breakdown takes the best matching item for its category - a point stage (same point type or an item that
/// also covers it, same stage, conduit type at 1st fix, height band, mounting), the flexible drop, a cable termination of the same
/// size, or the item type itself (cable pulling by size, tray by width band, isolator by rating and mounting, DB by ways ...).
/// </summary>
public sealed class ContractLabour : ILabourRateResolver
{
    public sealed record Entry(ContractItem Item, ItemSpec Spec);

    private readonly List<Entry> _entries;

    public ContractLabour(IEnumerable<ContractItem> items, string? contractNo = null)
    {
        _entries = items.Where(i => i.Rate > 0 && (contractNo is null || i.ContractNo == contractNo))
            .Select(i => new Entry(i, ItemParser.Parse(i.Description, i.Unit, ItemSourceKind.Contract))).ToList();
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public PriceHit? Find(ItemSpec spec, string category)
    {
        var best = Candidates(spec, category).OrderByDescending(x => x.Score).ThenBy(x => x.E.Item.Order).FirstOrDefault();
        if (best.E is null) return null;
        var it = best.E.Item;
        return new PriceHit(it.Rate, PriceSources.Contract, $"{it.ContractNo} item {it.ItemNo} ({it.Unit})", null, "", best.Score,
            best.Score < 0.8 ? "closest contract item - check" : "");
    }

    private IEnumerable<(Entry E, double Score)> Candidates(ItemSpec spec, string category)
    {
        foreach (var e in _entries)
        {
            var s = Score(spec, e.Spec, category);
            if (s > 0) yield return (e, s);
        }
    }

    private static string Family(string type) => type switch
    {
        ItemTypes.LightingPoint or ItemTypes.SocketPoint or ItemTypes.SwitchPoint or ItemTypes.DaliPoint => "POWER",
        _ => type,
    };

    private static bool Covers(ItemSpec c, string type) => c.ItemType == type || c.AlsoCovers.Contains(type);

    /// <summary>0 = not usable; higher = better.</summary>
    public static double Score(ItemSpec want, ItemSpec c, string category)
    {
        double s;
        switch (category.ToUpperInvariant())
        {
            case "1ST FIX":
            case "2ND FIX":
            case "3RD FIX":
            {
                var stage = category.ToUpperInvariant() switch { "1ST FIX" => StageNames.First, "2ND FIX" => StageNames.Second, _ => StageNames.Third };
                if (!c.Stages.Contains(stage)) return 0;
                if (!ItemTypes.IsPoint(c.ItemType) && !(want.ItemType is ItemTypes.Accessory or ItemTypes.Luminaire)) return 0;
                if (want.ItemType is ItemTypes.Accessory or ItemTypes.Luminaire)
                {
                    // devices: 3rd fix item of the same system family
                    if (stage != StageNames.Third) return 0;
                    var sys = want.System switch { "POWER" => ItemTypes.SocketPoint, "LIGHT" => ItemTypes.LightingPoint, "DATA" => ItemTypes.DataPoint, "GRMS" => ItemTypes.GrmsPoint, "FIRE" or "EVACUATION" => ItemTypes.FireAlarmPoint, _ => ItemTypes.ElvPoint };
                    if (!Covers(c, sys)) return 0;
                    s = 0.85;
                }
                else if (Covers(c, want.ItemType)) s = c.ItemType == want.ItemType ? 1.0 : 0.95;
                else if (Family(c.ItemType) == Family(want.ItemType) && Family(want.ItemType) == "POWER") s = 0.7;
                else return 0;
                if ((c.System == "EV") != (want.System == "EV")) s *= 0.5;
                if (want.ItemType is ItemTypes.ElvPoint or ItemTypes.DataPoint or ItemTypes.FireAlarmPoint && c.System.Length > 0 && want.System.Length > 0)
                    s *= c.System == want.System || want.System == "ELV" ? 1.0 : 0.85;
                if (stage == StageNames.First)
                {
                    var wc = want.Conduit.Length > 0 ? want.Conduit : Conduits.Pvc;
                    var cc = c.Conduit.Length > 0 ? c.Conduit : Conduits.Pvc;
                    if (wc != cc) return 0;
                    s *= MountFit(want, c);
                }
                s *= HeightFit(want, c);
                s *= want.Facade == c.Facade ? 1.0 : 0.8;
                if (c.Homerun) return 0;
                return s;
            }
            case "FLEX":
                if (c.ItemType != ItemTypes.FlexDrop) return 0;
                s = c.System == want.System || c.System is "LIGHT" or "" && want.System is "LIGHT" or "DALI" or "FACADE LIGHT" or "" ? 1.0 : 0.7;
                s *= want.Facade == c.Facade ? 1.0 : 0.8;
                return s * HeightFit(want, c);
            case "TERMINATION":
                if (c.ItemType != ItemTypes.CableTermination) return 0;
                return CableFit(want, c);
            case "SELF":
            default:
                return SelfScore(want, c);
        }
    }

    private static double SelfScore(ItemSpec want, ItemSpec c)
    {
        if (c.ItemType != want.ItemType) return 0;
        switch (want.ItemType)
        {
            case ItemTypes.CableRun:
            case ItemTypes.CableTermination:
                return CableFit(want, c);
            case ItemTypes.Tray:
            case ItemTypes.TrayCover:
                return (Band(want.TrayWidthMm) == Band(c.TrayWidthMm) ? 1.0 : 0.5) * HeightFit(want, c);
            case ItemTypes.Isolator:
            {
                var s = Math.Abs(want.Amps - c.Amps) < 0.5 ? 1.0 : want.Amps > 0 && c.Amps >= want.Amps ? 0.8 : 0.4;
                return s * ((want.Mount == "STAND") == (c.Mount == "STAND") ? 1.0 : 0.6);
            }
            case ItemTypes.Db:
                return want.Ways <= 0 ? 0.5 : c.Ways >= want.Ways ? 1.0 / (1 + (c.Ways - want.Ways) / 100.0) : 0.2;
            case ItemTypes.FloorBox:
                return want.Compartments == c.Compartments ? 1.0 : 0.6;
            case ItemTypes.EarthBar:
                return want.Ways == c.Ways ? 1.0 : want.Ways > 0 && c.Ways >= want.Ways ? 0.8 : 0.4;
            case ItemTypes.SystemPanel:
            case ItemTypes.FinalConnection:
            case ItemTypes.Homerun:
                return want.System == c.System ? 1.0 : 0.6;
            case ItemTypes.ConduitRun:
            {
                var wc = want.Conduit.Length > 0 ? want.Conduit : Conduits.Pvc;
                return (wc == (c.Conduit.Length > 0 ? c.Conduit : Conduits.Pvc) ? 1.0 : 0.3) * HeightFit(want, c);
            }
            case ItemTypes.LinearLight:
                return (want.Facade == c.Facade ? 1.0 : 0.6) * HeightFit(want, c);
            case ItemTypes.FlexDrop:
                return (want.System == c.System ? 1.0 : 0.7) * HeightFit(want, c);
            default:
                return 1.0 * HeightFit(want, c);
        }
    }

    private static int Band(int w) => w <= 0 ? 0 : w <= 300 ? 1 : w <= 800 ? 2 : 3;

    private static double CableFit(ItemSpec want, ItemSpec c)
    {
        if (want.CableSizeMm2 <= 0 || Math.Abs(want.CableSizeMm2 - c.CableSizeMm2) > 1e-6) return 0;
        static int Group(ItemSpec x) => x.EarthCable || x.CableCores == 1 ? 1 : x.CableCores == 2 ? 2 : 4;
        if (Group(want) != Group(c)) return 0;
        return want.EarthCable == c.EarthCable ? 1.0 : 0.85;
    }

    private static double HeightFit(ItemSpec want, ItemSpec c)
    {
        var w = want.Height == HeightBands.High ? HeightBands.High : HeightBands.Low;
        var h = c.Height is HeightBands.High or HeightBands.Low ? c.Height : HeightBands.Any;
        return h == HeightBands.Any ? 0.9 : h == w ? 1.0 : 0.3;
    }

    private static double MountFit(ItemSpec want, ItemSpec c)
    {
        if (c.Mount is "" or Mounts.Both || want.Mount is "" or Mounts.Both) return 0.95;
        return c.Mount == want.Mount ? 1.0 : 0.6;
    }
}
