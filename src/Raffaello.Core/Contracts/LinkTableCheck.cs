using System.Text.RegularExpressions;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Contracts;

/// <summary>A contract item linked to a BOQ (project) code of another system.</summary>
public sealed record LinkWarning(string ContractNo, string ItemNo, string BoqCode, string ItemSystems, string CodeSystems, string Message);

/// <summary>
/// Link-table checks (03-Oct, Mohamed):
/// 1. Gas meters are done by the ELECTRICAL subcontractor. The link tables link the Metering System items to the BMS lump
///    sum ("BMS system": B6-..-6-22-X-18, B3-..-3-22-L-12); they belong to the gas-meter codes of the same bill
///    (B6-01-01-00-6-22-R-18 gas meter, B5-01-01-00-5-22-P-10 gas meter assembly allowance, B3-01-01-00-3-22-E-12 gas meter
///    assembly 32mm). <see cref="FixGasMeterLinks"/> replaces those links and logs every change.
/// 2. <see cref="Check"/> warns when an item's system (from its section / description) and the linked code's system (from
///    the code description, or a mechanical CSI division 21 / 22 / 23) do not overlap.
/// </summary>
public static class LinkTableCheck
{
    public static readonly IReadOnlyDictionary<string, (string Code, string Description)> GasMeterCodes = new Dictionary<string, (string, string)>
    {
        ["B6"] = ("B6-01-01-00-6-22-R-18", "gas meter"),
        ["B5"] = ("B5-01-01-00-5-22-P-10", "Gas Meter Assembly allowance"),
        ["B3"] = ("B3-01-01-00-3-22-E-12", "Gas Meter Assembly 32mm dia"),
    };

    /// <summary>The BMS lump-sum codes the link tables wrongly use for the metering items.</summary>
    public static readonly HashSet<string> BmsCodes = new(StringComparer.OrdinalIgnoreCase) { "B6-01-01-00-6-22-X-18", "B3-01-01-00-3-22-L-12" };

    private static readonly (string System, Regex Rx)[] Keywords =
    {
        ("METERING", new(@"gas\s*meter|\bmetering\b|kwh\s*meter", RegexOptions.IgnoreCase)),
        ("BMS", new(@"\bBMS\b|building management", RegexOptions.IgnoreCase)),
        ("FIRE", new(@"fire\s*alarm|smoke\s*detector|heat\s*detector|call\s*point|\bFACP\b|fire(man)?\s*telephone", RegexOptions.IgnoreCase)),
        ("EVACUATION", new(@"voice\s*evac|\bVES\b|evacuation|public\s*address", RegexOptions.IgnoreCase)),
        ("EMERGENCY LIGHT", new(@"emergency\s*light|exit\s*sign|central\s*battery", RegexOptions.IgnoreCase)),
        ("CCTV", new(@"\bCCTV\b|camera", RegexOptions.IgnoreCase)),
        ("ACCESS", new(@"access\s*control|card\s*reader", RegexOptions.IgnoreCase)),
        ("DATA", new(@"\bdata\b|\bRJ45\b|telephone|\bIT\b|\bIPTV\b|\bTV\b|wi-?fi|\bWAP\b", RegexOptions.IgnoreCase)),
        ("AV", new(@"\bAV\b|\bBGM\b|audio|speaker", RegexOptions.IgnoreCase)),
        ("GRMS", new(@"\bGRMS\b|guest\s*room\s*management", RegexOptions.IgnoreCase)),
        ("LIGHT", new(@"lighting|luminaire|light\s*fitting|downlight|led\s*strip|\bDALI\b", RegexOptions.IgnoreCase)),
        ("POWER", new(@"socket|\bpower\b|\b13A\b|isolator|fused\s*spur|connection\s*unit", RegexOptions.IgnoreCase)),
        ("FLOOR BOX", new(@"floor\s*(box|outlet)|\bFB\d*\b", RegexOptions.IgnoreCase)),
    };

    /// <summary>Systems named in a text (section heading, item or code description).</summary>
    public static HashSet<string> SystemsOf(string? text)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (var (s, rx) in Keywords) if (rx.IsMatch(text)) set.Add(s);
        if (set.Contains("METERING")) set.Remove("POWER");                 // "metering" outlets are not small power
        if (set.Contains("FIRE") && Regex.IsMatch(text, @"fire(man)?\s*telephone", RegexOptions.IgnoreCase)) set.Remove("DATA");
        return set;
    }

    /// <summary>Neighbouring systems share a family - only a link across families is suspicious. A floor box carries power and data.</summary>
    public static string FamilyOf(string system) => system switch
    {
        "LIGHT" or "POWER" or "EMERGENCY LIGHT" => "POWER & LIGHTING",
        "DATA" or "AV" or "CCTV" or "ACCESS" or "GRMS" => "LOW CURRENT",
        "FIRE" or "EVACUATION" => "FIRE & EVACUATION",
        _ => system,
    };

    private static HashSet<string> Families(HashSet<string> systems)
    {
        var f = systems.Where(s => s != "FLOOR BOX").Select(FamilyOf).ToHashSet();
        if (systems.Contains("FLOOR BOX")) { f.Add("POWER & LIGHTING"); f.Add("LOW CURRENT"); }
        return f;
    }

    private static readonly Regex Division = new(@"^B\d+-\d{2}-\d{2}-\d{2}-\d+-(\d+)-", RegexOptions.Compiled);
    private static int DivisionOf(string code) { var m = Division.Match(code); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }
    private static string BillOf(string code) => code.Split('-')[0].ToUpperInvariant();

    private static bool IsMetering(ContractItem i) => SystemsOf(i.Section).Contains("METERING") || SystemsOf(i.Description).Contains("METERING");

    /// <summary>Replaces BMS-lump-sum links on metering items with the gas-meter code of the same bill. Returns the changes.</summary>
    public static List<string> FixGasMeterLinks(IEnumerable<ContractItem> items, IList<ContractItemBoq> links)
    {
        var log = new List<string>();
        var metering = items.Where(IsMetering).Select(i => (i.ContractNo, i.ItemNo)).ToHashSet();
        foreach (var l in links)
        {
            if (!metering.Contains((l.ContractNo, l.ItemNo)) || !BmsCodes.Contains(l.BoqCode)) continue;
            if (!GasMeterCodes.TryGetValue(BillOf(l.BoqCode), out var gas)) continue;
            log.Add($"item {l.ItemNo}: {l.BoqCode} (BMS system) -> {gas.Code} ({gas.Description}) - gas meters are done by the electrical subcontractor");
            l.BoqCode = gas.Code; l.BoqDescription = gas.Description; l.Source = "LINK TABLE (GAS METER FIX)";
        }
        // the same item could now be linked twice to the gas-meter code: keep one
        foreach (var dup in links.GroupBy(l => (l.ContractNo, l.ItemNo, l.BoqCode)).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)).ToList()) links.Remove(dup);
        return log;
    }

    /// <summary>Warnings for links whose code belongs to another system. <paramref name="codeDescription"/> gives the project code's description.</summary>
    public static List<LinkWarning> Check(IEnumerable<ContractItem> items, IEnumerable<ContractItemBoq> links, Func<string, string>? codeDescription = null)
    {
        var byItem = items.GroupBy(i => (i.ContractNo, i.ItemNo)).ToDictionary(g => g.Key, g => g.First());
        var res = new List<LinkWarning>();
        foreach (var l in links)
        {
            if (!byItem.TryGetValue((l.ContractNo, l.ItemNo), out var item)) continue;
            var itemSys = SystemsOf(item.Section);
            if (itemSys.Count == 0) itemSys = SystemsOf(item.Description);
            var desc = l.BoqDescription.Length > 0 ? l.BoqDescription : codeDescription?.Invoke(l.BoqCode) ?? "";
            var codeSys = SystemsOf(desc);
            var div = DivisionOf(l.BoqCode);
            string? why = null;
            if (codeSys.Contains("BMS") && !itemSys.Contains("BMS"))
                why = $"linked to the BMS code \"{desc}\" but the item is {Join(itemSys)}";
            else if (itemSys.Count > 0 && codeSys.Count > 0 && !Families(itemSys).Overlaps(Families(codeSys)))
                why = $"item is {Join(itemSys)}, the code is {Join(codeSys)} (\"{Short(desc)}\")";
            else if (div is 21 or 22 or 23 && !codeSys.Contains("METERING") && !itemSys.Contains("BMS"))
                why = $"mechanical code (CSI division {div}{(desc.Length > 0 ? $", \"{Short(desc)}\"" : "")}) on a {Join(itemSys, "electrical")} item";
            if (why != null) res.Add(new(l.ContractNo, l.ItemNo, l.BoqCode, Join(itemSys), Join(codeSys), $"Item {l.ItemNo} -> {l.BoqCode}: {why}"));
        }
        return res;
    }

    private static string Join(HashSet<string> s, string empty = "unknown") => s.Count == 0 ? empty : string.Join(" / ", s.OrderBy(x => x));
    private static string Short(string s) => s.Length > 50 ? s[..50] + "..." : s;
}
