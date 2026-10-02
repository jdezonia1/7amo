using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Ai;
using Raffaello.Core.Coding;

namespace Raffaello.Core.Boq;

/// <summary>
/// Categorises owner BOQ rows by system (POWER, LIGHTING, FIRE ALARM ...) and category (CABLE, CONDUIT, CONTAINMENT, PANEL ...):
/// learned corrections first (exact normalised description), then English / Arabic keyword rules, then the heading the row sits under,
/// and optionally Claude for what is left. Every manual correction is learned.
/// </summary>
public static class BoqCategorizer
{
    public static readonly string[] Systems =
    {
        "MV", "LV DISTRIBUTION", "POWER", "LIGHTING", "EMERGENCY LIGHTING", "LIGHTING CONTROL", "EARTHING & LIGHTNING", "CONTAINMENT", "FIRE ALARM", "PUBLIC ADDRESS",
        "DATA & TELEPHONE", "CCTV", "ACCESS CONTROL", "INTERCOM", "TV / AV", "BMS", "GRMS", "NURSE CALL / DISABLED", "EV CHARGING", "METERING", "GENERATOR / UPS", "SOLAR", "PRELIMINARIES", "OTHER",
    };

    public static readonly string[] Categories =
    {
        "CABLE", "WIRING", "CONDUIT", "CONTAINMENT", "PANEL / BOARD", "DEVICE / OUTLET", "LUMINAIRE", "EQUIPMENT", "ACCESSORY", "TESTING & COMMISSIONING", "DOCUMENTATION", "PROVISIONAL / PC SUM", "OTHER",
    };

    private sealed record Rule(Regex Pattern, string Value);

    private static Rule R(string pattern, string value) => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), value);

    // order matters: first match wins
    private static readonly Rule[] SystemRules =
    {
        R(@"\b(mv|medium\s+voltage|11\s*kv|13\.8\s*kv|17\.5\s*kv|ring\s+main|rmu)\b|جهد\s+متوسط", "MV"),
        R(@"emergency\s+(light|luminaire|lighting)|exit\s+sign|central\s+battery|إنارة\s+طوارئ|طوارئ", "EMERGENCY LIGHTING"),
        R(@"\b(dali|lighting\s+control|dimm|occupancy\s+sensor|keypad)\b|تحكم\s+الإنارة", "LIGHTING CONTROL"),
        R(@"\b(fire\s+alarm|smoke\s+detector|heat\s+detector|call\s+point|sounder|fa\s+panel|facp)\b|إنذار\s+حريق|كاشف", "FIRE ALARM"),
        R(@"\b(public\s+address|pa\s+system|evacuation|speaker|loudspeaker|voice\s+alarm)\b|إخلاء|سماعات", "PUBLIC ADDRESS"),
        R(@"\b(cctv|camera|nvr|vms)\b|كاميرا|مراقبة", "CCTV"),
        R(@"\b(access\s+control|card\s+reader|door\s+contact|maglock|exit\s+button)\b|التحكم\s+بالدخول", "ACCESS CONTROL"),
        R(@"\b(intercom|video\s+door|door\s+phone)\b|انتركم", "INTERCOM"),
        R(@"\b(data|cat\s*6a?|cat6|fibre|fiber|telephone|rj45|patch|structured\s+cabling|wap|wifi|network)\b|بيانات|شبكة|هاتف", "DATA & TELEPHONE"),
        R(@"\b(tv|satellite|iptv|av\b|audio\s*visual|soundbar|hdmi)\b|تلفزيون", "TV / AV"),
        R(@"\b(bms|building\s+management)\b", "BMS"),
        R(@"\b(grms|guest\s+room\s+management|room\s+controller|dnd|mur)\b", "GRMS"),
        R(@"\b(nurse\s+call|disabled\s+(toilet|alarm)|pull\s+cord)\b|ذوي\s+الاحتياجات", "NURSE CALL / DISABLED"),
        R(@"\b(ev\s+charg|electric\s+vehicle|wallbox)\b|شحن\s+السيارات", "EV CHARGING"),
        R(@"\b(meter|metering|energy\s+meter|kwh)\b|عداد", "METERING"),
        R(@"\b(generator|genset|ups|ats|diesel)\b|مولد", "GENERATOR / UPS"),
        R(@"\b(solar|photovoltaic|pv\s+panel)\b|شمسي", "SOLAR"),
        R(@"\b(earth(ing)?|lightning|air\s+terminal|down\s+conductor|bonding|ecc)\b|تأريض|صواعق", "EARTHING & LIGHTNING"),
        R(@"\b(cable\s+tray|ladder|trunking|basket|unistrut|cable\s+trench)\b|حامل\s+كابلات|سلم\s+كابلات", "CONTAINMENT"),
        R(@"\b(luminaire|downlight|light\s+fitting|lighting|lamp|led\s+strip|linear\s+light|bollard|uplight|chandelier|spot\s*light)\b|إنارة|كشاف", "LIGHTING"),
        R(@"\b(socket|outlet|power\s+point|isolator|fused\s+spur|fcu|switch\s+socket|floor\s+box|small\s+power|usb)\b|بريزة|مقبس|قوى", "POWER"),
        R(@"\b(mdb|smdb|sdb|db|distribution\s+board|panel\s*board|switchboard|lv\s+panel|busbar|bus\s+duct|feeder|sub-?main)\b|لوحة\s+توزيع|لوحة", "LV DISTRIBUTION"),
        R(@"\b(preliminar|general\s+requirement|mobilis|shop\s+drawing|as\s+built|o\s*&\s*m|training|warranty)\b", "PRELIMINARIES"),
    };

    private static readonly Rule[] CategoryRules =
    {
        R(@"\b(provisional|pc\s+sum|prime\s+cost|allowance)\b", "PROVISIONAL / PC SUM"),
        R(@"\b(test(ing)?|commission|inspection)\b|اختبار", "TESTING & COMMISSIONING"),
        R(@"\b(shop\s+drawing|as\s+built|o\s*&\s*m|manual|documentation)\b", "DOCUMENTATION"),
        R(@"\b(cable\s+tray|ladder|trunking|basket)\b|حامل\s+كابلات", "CONTAINMENT"),
        R(@"\b(conduit|pvc\s+pipe|emt|flexible\s+conduit|gi\s+conduit)\b|مواسير|ماسورة", "CONDUIT"),
        R(@"\b(wiring|point\s+wiring|wired)\b|تمديد", "WIRING"),
        R(@"\d\s*c\b|\d\s*core|\bmm2\b|\bxlpe\b|\blszh\b|\bswa\b|\bcable\b|\bwire\b|كابل|سلك", "CABLE"),
        R(@"\b(mdb|smdb|sdb|db|distribution\s+board|panel|switchboard|busbar|bus\s+duct)\b|لوحة", "PANEL / BOARD"),
        R(@"\b(luminaire|downlight|light\s+fitting|lamp|led\s+strip|bollard|uplight|spot\s*light)\b|كشاف", "LUMINAIRE"),
        R(@"\b(socket|outlet|switch|isolator|spur|detector|sensor|call\s+point|camera|reader|speaker|keypad|thermostat|point)\b|مفتاح|بريزة|مقبس", "DEVICE / OUTLET"),
        R(@"\b(gland|lug|termination|saddle|box|bracket|support|fixing|label)\b", "ACCESSORY"),
        R(@"\b(generator|ups|transformer|rmu|charger|controller|server|nvr|panel\s+unit)\b", "EQUIPMENT"),
    };

    public sealed record Result(string System, string Category, string Source, double Score);

    public static Result Categorize(string description, string? headingContext, IReadOnlyDictionary<string, BoqCatRule> learned)
    {
        var key = Fingerprints.NormalizeDescription(description);
        if (key.Length > 0 && learned.TryGetValue(key, out var rule)) return new(rule.System, rule.Category, "LEARNED", 1.0);
        var sys = SystemRules.FirstOrDefault(r => r.Pattern.IsMatch(description))?.Value;
        var cat = CategoryRules.FirstOrDefault(r => r.Pattern.IsMatch(description))?.Value;
        var src = "RULE"; var score = sys != null && cat != null ? 0.9 : sys != null || cat != null ? 0.7 : 0;
        if (sys is null && !string.IsNullOrWhiteSpace(headingContext))
        {
            sys = SystemRules.FirstOrDefault(r => r.Pattern.IsMatch(headingContext))?.Value;
            if (sys != null) { src = "HEADING"; score = Math.Max(score, 0.6); }
        }
        if (cat is null && Fingerprints.Cable(description) != null) { cat = "CABLE"; score = Math.Max(score, 0.75); }
        return new(sys ?? "", cat ?? "", sys is null && cat is null ? "" : src, score);
    }

    /// <summary>Categorises all rows (headings carry their context down to the rows below them).</summary>
    public static void Apply(IList<BoqLine> rows, IEnumerable<BoqCatRule> learned)
    {
        var map = learned.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Uses).First());
        string? heading = null;
        foreach (var r in rows)
        {
            if (r.Confirmed) continue;
            if (r.IsHeading) { heading = r.Description; var hres = Categorize(r.Description, null, map); r.System = hres.System; r.Category = ""; r.CatSource = "HEADING"; r.CatScore = hres.Score; continue; }
            var res = Categorize(r.Description, heading, map);
            r.System = res.System; r.Category = res.Category; r.CatSource = res.Source; r.CatScore = res.Score;
        }
    }

    /// <summary>A correction becomes a rule keyed on the normalised description.</summary>
    public static BoqCatRule Learn(BoqLine row, string system, string category, IEnumerable<BoqCatRule> existing)
    {
        var key = Fingerprints.NormalizeDescription(row.Description);
        var rule = existing.FirstOrDefault(r => r.Key == key) ?? new BoqCatRule { Key = key };
        rule.System = system; rule.Category = category; rule.Uses++;
        row.System = system; row.Category = category; row.CatSource = "MANUAL"; row.CatScore = 1; row.Confirmed = true;
        return rule;
    }

    /// <summary>Optional Claude fallback for rows no rule covers. Returns how many rows were filled.</summary>
    public static async Task<int> AiFallbackAsync(IList<BoqLine> rows, AnthropicClient client, CancellationToken ct = default)
    {
        var todo = rows.Where(r => !r.IsHeading && !r.Confirmed && (r.System.Length == 0 || r.Category.Length == 0)).Take(200).ToList();
        if (todo.Count == 0) return 0;
        var items = new JsonArray();
        for (var i = 0; i < todo.Count; i++) items.Add(new JsonObject { ["id"] = i, ["description"] = todo[i].Description, ["unit"] = todo[i].Unit });
        var system = "You categorise electrical BOQ rows for a hotel and residences project. Answer with JSON only: an array of {\"id\":n,\"system\":...,\"category\":...}. " +
                     $"system must be one of: {string.Join(" | ", Systems)}. category must be one of: {string.Join(" | ", Categories)}.";
        var text = await client.CompleteAsync(system, new[] { new ChatMessage("user", items.ToJsonString()) }, ct).ConfigureAwait(false);
        var i0 = text.IndexOf('['); var i1 = text.LastIndexOf(']');
        if (i0 < 0 || i1 <= i0) return 0;
        var n = 0;
        foreach (var it in JsonNode.Parse(text[i0..(i1 + 1)])?.AsArray() ?? new JsonArray())
        {
            var id = it?["id"]?.GetValue<int>() ?? -1;
            if (id < 0 || id >= todo.Count) continue;
            var s = it!["system"]?.GetValue<string>() ?? ""; var c = it["category"]?.GetValue<string>() ?? "";
            if (!Systems.Contains(s)) s = ""; if (!Categories.Contains(c)) c = "";
            if (s.Length == 0 && c.Length == 0) continue;
            if (todo[id].System.Length == 0) todo[id].System = s;
            if (todo[id].Category.Length == 0) todo[id].Category = c;
            todo[id].CatSource = "AI"; todo[id].CatScore = 0.6; n++;
        }
        return n;
    }
}
