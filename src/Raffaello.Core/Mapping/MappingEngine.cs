using Raffaello.Core.Contracts;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Tracker;

namespace Raffaello.Core.Mapping;

public static class Confidence
{
    public const string Learned = "LEARNED";
    public const string Auto = "AUTO";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Guess = "GUESS";
    public const string Unmapped = "UNMAPPED";
    public static bool NeedsConfirmation(string c) => c is Ambiguous or Guess or Unmapped;
}

public sealed record ItemMatch(ContractItem? Item, string Confidence, string Explanation, IReadOnlyList<ContractItem> Candidates)
{
    public static ItemMatch None(string why) => new(null, Mapping.Confidence.Unmapped, why, Array.Empty<ContractItem>());
}

public sealed record BoqMatch(string BoqCode, string BoqDescription, string Confidence, string Explanation);

/// <summary>Everything the resolvers need for one contract: items, BOQ links, BOQ descriptions and learned rules.</summary>
/// <summary>
/// Named mapping defaults that may be flipped in one place (Settings). DATA / GRMS 1st fix outlets: the residence contract
/// has a ceiling item (182 / 199) and a wall item (183 / 200); which one the tracker's 1ST FIX means is still being confirmed.
/// </summary>
public sealed record MappingOptions
{
    public string Data1stFixMount { get; init; } = Mounts.Wall;
    public string Grms1stFixMount { get; init; } = Mounts.Wall;

    public static MappingOptions Default { get; } = new();

    /// <summary>Mount to look for at 1ST FIX for this system (null = the stage default).</summary>
    public string? FirstFixMount(string system) => system.ToUpperInvariant() switch
    {
        "DATA" => Data1stFixMount,
        "GRMS" => Grms1stFixMount,
        _ => null,
    };
}

public sealed class MappingContext
{
    public MappingOptions Options { get; init; } = MappingOptions.Default;

    public string ContractNo { get; }
    public IReadOnlyList<ContractItem> Items { get; }
    public IReadOnlyDictionary<string, List<ContractItemBoq>> LinksByItem { get; }
    public IReadOnlyDictionary<string, string> BoqDescriptions { get; }
    public IReadOnlyList<MappingRule> Rules { get; }

    public MappingContext(string contractNo, IEnumerable<ContractItem> items, IEnumerable<ContractItemBoq> links, IEnumerable<BoqItem> boq, IEnumerable<MappingRule> rules)
    {
        ContractNo = contractNo;
        Items = items.Where(i => i.ContractNo == contractNo).OrderBy(i => i.Order).ToList();
        LinksByItem = links.Where(l => l.ContractNo == contractNo).GroupBy(l => l.ItemNo).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Source == "TEMPLATE").ThenBy(l => l.Order).ToList());
        BoqDescriptions = boq.GroupBy(b => b.ItemCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.OrdinalIgnoreCase);
        Rules = rules.Where(r => r.ContractNo == contractNo || r.ContractNo.Length == 0).ToList();
    }

    public string DescriptionOf(ContractItemBoq l) => l.BoqDescription.Length > 0 ? l.BoqDescription : BoqDescriptions.GetValueOrDefault(l.BoqCode, "");
    public MappingRule? Rule(string kind, string key) =>
        Rules.Where(r => r.Kind == kind && string.Equals(r.MatchKey, key, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.ContractNo.Length).FirstOrDefault();
}

/// <summary>Ledger key (stage + system + height band) -> contract item.</summary>
public interface IItemResolver
{
    ItemMatch Resolve(string stage, string system, string band, MappingContext ctx);
}

/// <summary>Contract item + system + area type -> owner BOQ code row.</summary>
public interface IBoqResolver
{
    BoqMatch Resolve(ContractItem item, string system, string areaType, MappingContext ctx);
}

/// <summary>How the tracker stages translate into contract attributes.</summary>
public sealed record StageProfile(string FixStage, string? Conduit, string? Mount, string? Category)
{
    public static StageProfile? For(string stage) => (stage ?? "").Trim().ToUpperInvariant() switch
    {
        "CEILING" => new(FixStages.First, Conduits.Pvc, Mounts.Ceiling, null),
        "1ST FIX" or "1STFIX" => new(FixStages.First, Conduits.Pvc, Mounts.Wall, null),
        "EMT" => new(FixStages.First, Conduits.Emt, null, null),
        "RS" => new(FixStages.First, Conduits.Rs, null, null),
        "FLEXIBLE" or "FLEX" => new(FixStages.Third, Conduits.Flex, null, null),
        "2ND FIX" or "2NDFIX" => new(FixStages.Second, null, null, null),
        "3RD FIX" or "3RDFIX" => new(FixStages.Third, Conduits.None, null, null),
        "DB PANELS" or "PANEL" => new("", null, null, "PANEL"),
        "CABLE PULLING" => new("", null, null, "CABLE"),
        "CABLE TRAY" => new("", null, null, "TRAY"),
        _ => null,
    };
}

public static class SystemNames
{
    /// <summary>Tracker item -> (contract system, forced area type). TERRACE items are balcony lighting / power.</summary>
    public static (string System, string? Area) Normalize(string item)
    {
        var s = (item ?? "").Trim().ToUpperInvariant();
        return s switch
        {
            "TERRACE LIGHT" => ("LIGHT", AreaTypes.Balcony),
            "TERRACE POWER" => ("POWER", AreaTypes.Balcony),
            "IT" => ("DATA", null),
            "EM" or "EMERGENCY" => ("EMERGENCY LIGHT", null),
            "EV" => ("EVACUATION", null),
            "CONTROL" => ("GRMS", null),
            "GAS" or "GAS METER" or "GAS METERING" => ("METERING", null),   // gas meter points -> Metering System items
            _ => (s, null),
        };
    }

    /// <summary>When two item groups cover a system, prefer the one that also lists this system (AV = TV outlet, with data).</summary>
    public static readonly IReadOnlyDictionary<string, string> PreferWith = new Dictionary<string, string> { ["AV"] = "DATA" };

    /// <summary>Description words that make an item the better fit for a system (gas meter items within Metering).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> PreferWords = new Dictionary<string, string[]> { ["METERING"] = new[] { "gas", "غاز" } };
}

/// <summary>Rule-based item resolver with learned overrides.</summary>
public sealed class DefaultItemResolver : IItemResolver
{
    public static string RuleKey(string stage, string system, string band) => $"{stage}|{system}|{band}".ToUpperInvariant();

    public ItemMatch Resolve(string stage, string system, string band, MappingContext ctx)
    {
        var key = RuleKey(stage, system, band);
        if (ctx.Rule("ITEM", key) is { } rule)
        {
            var it = ctx.Items.FirstOrDefault(i => i.ItemNo == rule.Target);
            if (it != null) return new(it, Confidence.Learned, $"learned rule {key} -> item {it.ItemNo}", new[] { it });
        }
        var profile = StageProfile.For(stage);
        if (profile is null) return ItemMatch.None($"stage '{stage}' is not mapped to the contract");
        if (profile.Mount == Mounts.Wall && profile.FixStage == FixStages.First && profile.Conduit == Conduits.Pvc
            && ctx.Options.FirstFixMount(system) is { Length: > 0 } mount && mount != profile.Mount)
            profile = profile with { Mount = mount };

        if (profile.Category is "PANEL" or "CABLE" or "TRAY") return BySize(profile.Category, system, band, ctx);

        var cands = ctx.Items.Where(i =>
                i.FixStage == profile.FixStage
                && !i.IsHomerun
                && Split(i.Systems).Contains(system)
                && (profile.Conduit is null ? i.ConduitType is Conduits.None or Conduits.Pvc or "" : i.ConduitType == profile.Conduit)
                && (i.HeightBand == band || i.HeightBand is HeightBands.Any or ""))
            .ToList();
        if (profile.FixStage == FixStages.Second) cands = cands.Where(i => i.Category is "WIRING" or "OTHER").ToList();
        if (cands.Count == 0)
        {
            if (band == HeightBands.High)
            {
                var low = Resolve(stage, system, HeightBands.Low, ctx);
                return low.Item is null ? low : low with { Confidence = Confidence.Guess, Explanation = low.Explanation + "; no >4.5 m item - priced at the normal item" };
            }
            return ItemMatch.None($"no contract item for {stage} / {system} / {band}");
        }
        double Score(ContractItem i)
        {
            var s = 0.0;
            if (profile.Mount != null) s += i.Mount == profile.Mount ? 2 : i.Mount == Mounts.Both ? 1 : 0;
            s += i.HeightBand == band ? 1 : 0;
            if (SystemNames.PreferWith.TryGetValue(system, out var with) && Split(i.Systems).Contains(with)) s += 0.75;
            if (SystemNames.PreferWords.TryGetValue(system, out var words) && words.Any(w => i.Description.Contains(w, StringComparison.OrdinalIgnoreCase))) s += 0.5;
            s -= Split(i.Systems).Count * 0.01;   // more specific item first
            return s;
        }
        var ranked = cands.OrderByDescending(Score).ThenBy(i => i.Order).ToList();
        var best = ranked[0];
        var ties = ranked.Where(i => Math.Abs(Score(i) - Score(best)) < 1e-9).ToList();
        var why = $"{stage} -> {profile.FixStage} {profile.Conduit ?? "any"} {profile.Mount ?? "any mount"}, {system}, {band}: item {best.ItemNo} ({best.ConduitType} {best.Mount} {best.HeightBand}, SAR {best.Rate:0.##})";
        return ties.Count > 1
            ? new(best, Confidence.Ambiguous, why + $"; also fits {string.Join(", ", ties.Skip(1).Select(t => t.ItemNo))}", ranked)
            : new(best, Confidence.Auto, why, ranked);
    }

    private static ItemMatch BySize(string category, string system, string band, MappingContext ctx)
    {
        var sizeKey = category switch
        {
            "PANEL" => system.StartsWith("PANEL ") ? system : system,
            "CABLE" => system.Replace(" ", ""),
            _ => TrayBand(system),
        };
        var cands = ctx.Items.Where(i => i.Category == category && string.Equals(i.SizeKey, sizeKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cands.Count == 0 && category == "PANEL" && int.TryParse(system.Replace("PANEL", "").Trim(), out var ways))
            cands = ctx.Items.Where(i => i.Category == "PANEL" && i.SizeKey.StartsWith("PANEL ") && InRange(i.SizeKey[6..], ways)).ToList();
        if (cands.Count == 0) return ItemMatch.None($"no {category.ToLowerInvariant()} item for {system}");
        var best = cands.OrderByDescending(i => i.HeightBand == band ? 1 : 0).ThenBy(i => i.Order).First();
        var conf = cands.Count(i => i.HeightBand == best.HeightBand) > 1 ? Confidence.Ambiguous : Confidence.Auto;
        return new(best, conf, $"{category} {system} -> item {best.ItemNo} ({best.SizeKey}, SAR {best.Rate:0.##})", cands);
    }

    private static bool InRange(string range, int n)
    {
        var p = range.Split('-');
        return p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) ? n >= a && n <= b : int.TryParse(range, out var x) && x == n;
    }

    /// <summary>Tracker tray widths ("150 MM") -> contract bands.</summary>
    public static string TrayBand(string item)
    {
        var digits = new string((item ?? "").Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var mm)) return "TRAY";
        return mm <= 300 ? "TRAY 50-300" : mm <= 800 ? "TRAY 400-800" : "TRAY 900+";
    }

    public static HashSet<string> Split(string systems) =>
        systems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Chooses the BOQ code row by keywords in the BOQ description (system + area type), with learned overrides.</summary>
public sealed class DefaultBoqResolver : IBoqResolver
{
    public static string RuleKey(string itemNo, string system, string area) => $"{itemNo}|{system}|{area}".ToUpperInvariant();

    public static string AreaWord(string area) => (area ?? "").ToUpperInvariant() switch
    {
        AreaTypes.Boh => "boh",
        AreaTypes.Foh => "foh",
        AreaTypes.Balcony => "balcon",
        AreaTypes.Guestroom => "guestroom",
        AreaTypes.Facade => "façade",
        _ => "apartment",
    };

    private static readonly IReadOnlyDictionary<string, string[]> SystemWords = new Dictionary<string, string[]>
    {
        ["POWER"] = new[] { "small power" },
        ["EMERGENCY LIGHT"] = new[] { "emergency lighting point", "emergency light" },
        ["FIRE"] = new[] { "fdas", "fire alarm" },
        ["EVACUATION"] = new[] { "ves", "voice evac", "speaker" },
        ["AV"] = new[] { "av/bgm points", "av/bgm", "iptv", "television", "tv outlet", "tv point" },
        ["DATA"] = new[] { "it / telecom", "telecom", "data" },
        ["CCTV"] = new[] { "cctv" },
        ["GRMS"] = new[] { "grms" },
        ["BMS"] = new[] { "bms", "automatic controls" },
        ["ACCESS"] = new[] { "access" },
        ["SWITCH"] = new[] { "to switches", "switch" },
        ["FACADE LIGHT"] = new[] { "façade", "facade", "external" },
        ["LIGHTING CONTROL"] = new[] { "lighting control", "automatic controls" },
        ["DISABLED"] = new[] { "disabled" },
        ["INTERCOM"] = new[] { "intercom" },
        ["ISOLATOR"] = new[] { "isolator" },
    };

    public BoqMatch Resolve(ContractItem item, string system, string areaType, MappingContext ctx)
    {
        var links = ctx.LinksByItem.GetValueOrDefault(item.ItemNo) ?? new List<ContractItemBoq>();
        var key = RuleKey(item.ItemNo, system, areaType);
        if (ctx.Rule("BOQ", key) is { } rule && links.FirstOrDefault(l => l.BoqCode == rule.Target) is { } ruled)
            return new(ruled.BoqCode, ctx.DescriptionOf(ruled), Confidence.Learned, $"learned rule {key} -> {ruled.BoqCode}");
        if (links.Count == 0) return new("", "", Confidence.Unmapped, $"item {item.ItemNo} has no BOQ code");
        if (links.Count == 1) return new(links[0].BoqCode, ctx.DescriptionOf(links[0]), Confidence.Auto, $"only BOQ row of item {item.ItemNo}");

        var withDesc = links.Select(l => (l, d: ctx.DescriptionOf(l).ToLowerInvariant())).ToList();
        string Orig(string code) => ctx.DescriptionOf(links.First(l => l.BoqCode == code));
        if (item.Category is "CABLE" or "CABLE TERMINATION" && SizeMatch(withDesc, CableNeedles(system, item.SizeKey)) is { } cable)
            return new(cable.Code, Orig(cable.Code), cable.Unique ? Confidence.Auto : Confidence.Ambiguous, $"cable {system} -> '{Orig(cable.Code)}'");
        if (item.Category is "TRAY" or "TRAY COVER" && SizeMatch(withDesc, TrayNeedles(system)) is { } tray)
            return new(tray.Code, Orig(tray.Code), tray.Unique ? Confidence.Auto : Confidence.Ambiguous, $"tray {system} -> '{Orig(tray.Code)}'");
        if (system is "LIGHT" or "DALI" or "LINEAR LIGHT")
        {
            var area = AreaWord(areaType);
            var lighting = withDesc.Where(x => x.d.Contains("lighting points")).ToList();
            var linearWanted = system == "LINEAR LIGHT";
            var inArea = lighting.Where(x => x.d.Contains(area)).ToList();
            var pick = inArea.Where(x => linearWanted ? x.d.Contains("linear") : !x.d.Contains("linear") && !x.d.Contains("(id") && !x.d.Contains("id light"))
                             .OrderByDescending(x => x.d.Contains("number")).FirstOrDefault();
            if (pick.l != null) return new(pick.l.BoqCode, ctx.DescriptionOf(pick.l), Confidence.Auto, $"LIGHT in {areaType.ToLowerInvariant()} -> '{ctx.DescriptionOf(pick.l)}'");
            var fallback = lighting.FirstOrDefault(x => x.d.Contains("number")).l ?? lighting.FirstOrDefault().l
                           ?? withDesc.FirstOrDefault(x => x.d.Contains("light fixture") || x.d.Contains("light")).l;
            if (fallback != null) return new(fallback.BoqCode, ctx.DescriptionOf(fallback), Confidence.Guess, $"no lighting row for area {areaType} - used '{ctx.DescriptionOf(fallback)}'");
        }
        if (SystemWords.TryGetValue(system, out var words))
            foreach (var w in words)
            {
                var hit = withDesc.FirstOrDefault(x => x.d.Contains(w));
                if (hit.l != null) return new(hit.l.BoqCode, ctx.DescriptionOf(hit.l), Confidence.Auto, $"{system} -> '{ctx.DescriptionOf(hit.l)}'");
            }
        var first = links[0];
        return new(first.BoqCode, ctx.DescriptionOf(first), Confidence.Guess, $"no BOQ row matches {system}; first row of item {item.ItemNo} used");
    }

    private sealed record SizeHit(string Code, string Desc, bool Unique);

    /// <summary>First needle (most specific) that hits; unique when exactly one distinct BOQ description matches it.</summary>
    private static SizeHit? SizeMatch(List<(ContractItemBoq l, string d)> rows, IEnumerable<string> needles)
    {
        foreach (var n in needles)
        {
            var hits = rows.Where(x => x.d.StartsWith(n) || x.d.Contains(" " + n)).ToList();
            if (hits.Count == 0) continue;
            return new(hits[0].l.BoqCode, hits[0].d, hits.Select(h => h.d).Distinct().Count() == 1);
        }
        return null;
    }

    /// <summary>"4X16" -> "4c 16mm2", then "16mm2". Single-core "1X16" also tries "1c 16mm2".</summary>
    public static IEnumerable<string> CableNeedles(string system, string sizeKey)
    {
        var key = (system.Contains('X') ? system : sizeKey).ToUpperInvariant().Replace(" ", "").TrimStart('E');
        var p = key.Split('X');
        if (p.Length == 2) { yield return $"{p[0]}c {p[1]}mm2"; yield return $"{p[0]}c {p[1]}mm"; yield return $"{p[1]}mm2"; }
        else if (key.Length > 0) yield return $"{key}mm2";
    }

    /// <summary>"150 MM" -> "150mm", "150x", "150 mm".</summary>
    public static IEnumerable<string> TrayNeedles(string system)
    {
        var mm = new string((system ?? "").Where(char.IsDigit).ToArray());
        if (mm.Length == 0) yield break;
        yield return mm + "mm";
        yield return mm + "x";
        yield return mm + " mm";
    }
}

/// <summary>One mapped piece of a claim line.</summary>
public sealed record MappedPart(ClaimLine Line, string Band, double Qty, ContractItem? Item, string BoqCode, string BoqDescription, string Confidence, string Explanation,
    string System = "", string Area = "")
{
    /// <summary>Key a learned ITEM rule would use for this part.</summary>
    public string ItemRuleKey => DefaultItemResolver.RuleKey(Line.Stage, System, Band);
    /// <summary>Key a learned BOQ rule would use for this part (needs the item).</summary>
    public string BoqRuleKey(string itemNo) => DefaultBoqResolver.RuleKey(itemNo, System, Area);
    public string RowKey => $"{Item?.ItemNo}|{BoqCode}";
}

public sealed class MappingResult
{
    public List<MappedPart> Parts { get; } = new();
    public List<InvoiceableQty> Held { get; } = new();
    public double TotalQty => Parts.Sum(p => p.Qty);
    public double MappedQty => Parts.Where(p => p.Item != null && p.BoqCode.Length > 0).Sum(p => p.Qty);
    public double AutoQty => Parts.Where(p => p.Item != null && p.BoqCode.Length > 0 && !Confidence.NeedsConfirmation(p.Confidence)).Sum(p => p.Qty);
    public double CoveragePct => TotalQty <= 0 ? 1 : MappedQty / TotalQty;
    public double AutoPct => TotalQty <= 0 ? 1 : AutoQty / TotalQty;
    public IEnumerable<MappedPart> NeedsConfirmation => Parts.Where(p => Confidence.NeedsConfirmation(p.Confidence));

    public IReadOnlyDictionary<string, (double Qty, List<MappedPart> Parts)> ByRow() =>
        Parts.Where(p => p.Item != null).GroupBy(p => p.RowKey).ToDictionary(g => g.Key, g => (g.Sum(p => p.Qty), g.ToList()));
}

/// <summary>Maps invoiceable claim quantities to contract item x BOQ code rows. Resolvers are pluggable.</summary>
public sealed class MappingEngine
{
    private readonly IItemResolver _items;
    private readonly IBoqResolver _boq;

    public MappingEngine(IItemResolver? items = null, IBoqResolver? boq = null)
    {
        _items = items ?? new DefaultItemResolver();
        _boq = boq ?? new DefaultBoqResolver();
    }

    public MappingResult Map(IEnumerable<ClaimLine> claims, MappingContext ctx, IReadOnlyDictionary<string, string>? roomAreas = null)
    {
        var res = new MappingResult();
        foreach (var c in claims)
        {
            var q = Invoiceable.Of(c);
            if (q.Held) { res.Held.Add(q); continue; }
            var (system, forcedArea) = SystemNames.Normalize(c.Item);
            var area = forcedArea ?? (roomAreas != null && roomAreas.TryGetValue(c.Room, out var a) && a.Length > 0 ? a : c.AreaType.Length > 0 ? c.AreaType : AreaTypes.Apartment);
            foreach (var (band, qty) in new[] { (HeightBands.Low, q.LowQty), (HeightBands.High, q.HighQty) })
            {
                if (Math.Abs(qty) < LedgerRules.Eps) continue;
                var im = _items.Resolve(c.Stage, system, band, ctx);
                if (im.Item is null) { res.Parts.Add(new(c, band, qty, null, "", "", Confidence.Unmapped, im.Explanation, system, area)); continue; }
                var bm = _boq.Resolve(im.Item, system, area, ctx);
                var conf = Worst(im.Confidence, bm.Confidence);
                res.Parts.Add(new(c, band, qty, im.Item, bm.BoqCode, bm.BoqDescription, conf,
                    $"{c.Room} {c.Stage} {c.Item} {c.Subcontractor} INV {c.InvoiceNo}: {c.Qty:0.##} x site {c.SitePct:P0} x WIR {c.WirPct:P0}{(band == HeightBands.High ? " (>4.5 m accepted)" : "")} = {qty:0.##}; {im.Explanation}; {bm.Explanation}", system, area));
            }
        }
        return res;
    }

    private static readonly string[] Order = { Confidence.Learned, Confidence.Auto, Confidence.Ambiguous, Confidence.Guess, Confidence.Unmapped };
    public static string Worst(string a, string b) => Array.IndexOf(Order, a) >= Array.IndexOf(Order, b) ? a : b;

    /// <summary>Stores (or updates) a manual decision so the same key maps the same way next time.</summary>
    public static MappingRule Learn(string kind, string contractNo, string matchKey, string target, string note = "") =>
        new() { Kind = kind, ContractNo = contractNo, MatchKey = matchKey.ToUpperInvariant(), Target = target, Note = note, UseCount = 1 };
}
