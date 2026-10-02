using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Assistant;

public sealed record OfflineAnswer(string Intent, string Text, List<Citation> Citations);

/// <summary>
/// Answers simple questions from local data without Claude (no API key, offline, or the API failed): deterministic intent matching in
/// English and Arabic over the same read tools the model uses. Examples: "remaining in P2-106 2nd fix light", "where is DN 81064344",
/// "status of ROOTS INV 3", "P2-106", "what needs me today", "explain the 15 m rule", "المتبقي في P2-106 التمديد الثاني إنارة".
/// </summary>
public sealed class OfflineAssistant
{
    private readonly AssistantData _d;
    private readonly AssistantTools _tools;

    public OfflineAssistant(AssistantData d, AssistantTools? tools = null)
    {
        _d = d;
        _tools = tools ?? new AssistantTools(d);
    }

    private static readonly Regex RoomRx = new(@"\b([A-Z]{1,3}\d{0,2}-[A-Z]?\d{1,4}[A-Z]?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DnRx = new(@"(?:\bD\.?N\.?|delivery\s*note|إشعار\s*(?:ال)?تسليم|اشعار\s*(?:ال)?تسليم|سند\s*(?:ال)?تسليم)\s*(?:no\.?|number|#|رقم)?\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]{3,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InvRx = new(@"(?:\bINV(?:OICE)?|فاتورة|الفاتورة)\s*[-#.]?\s*(?:no\.?|رقم)?\s*(\d{1,3})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PoRx = new(@"\b(RAF-?P\.?O[-A-Z0-9.]+|[A-Z]{2,5}-PO-[A-Z0-9-]+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly (string Item, string[] Words)[] ItemWords =
    {
        ("EMERGENCY LIGHT", new[] { "emergency", "طوارئ", "الطوارئ" }),
        ("LIGHT", new[] { "light", "lighting", "إنارة", "انارة", "الإنارة", "الانارة", "اضاءة", "إضاءة", "الاضاءة" }),
        ("POWER", new[] { "power", "socket", "باور", "قوى", "القوى", "أفياش", "افياش" }),
        ("DATA", new[] { "data", "بيانات", "البيانات", "داتا" }),
        ("GRMS", new[] { "grms" }),
        ("AV", new[] { " av ", "audio" }),
        ("GAS", new[] { "gas", "غاز", "الغاز" }),
        ("DALI", new[] { "dali" }),
        ("FIRE", new[] { "fire", "حريق" }),
        ("CCTV", new[] { "cctv", "كاميرات" }),
        ("ACCESS", new[] { "access" }),
        ("BMS", new[] { "bms" }),
        ("EVACUATION", new[] { "evacuation", "إخلاء", "اخلاء" }),
    };

    public static bool IsArabic(string s) => s.Any(c => c >= '؀' && c <= 'ۿ');

    public static string? Stage(string q)
    {
        var s = q.ToLowerInvariant();
        if (Regex.IsMatch(s, @"\b(1st|first)\b") || s.Contains("التمديد الأول") || s.Contains("الاول") || s.Contains("الأول") || s.Contains("اول")) return Stages.First;
        if (Regex.IsMatch(s, @"\b(2nd|second)\b") || s.Contains("الثاني") || s.Contains("ثاني")) return Stages.Second;
        if (Regex.IsMatch(s, @"\b(3rd|third|final)\b") || s.Contains("النهائي") || s.Contains("نهائي") || s.Contains("الثالث")) return Stages.Final;
        return null;
    }

    public string? Item(string q)
    {
        var padded = " " + q.ToLowerInvariant() + " ";
        // items that exist in the data first (longest name wins), then the synonyms
        var known = _d.Project.Snapshot.RoomQtys.Select(x => x.Item).Concat(_d.Project.Snapshot.Claims.Select(c => c.Item)).Select(x => x.Trim().ToUpperInvariant())
            .Where(x => x.Length > 1).Distinct().OrderByDescending(x => x.Length);
        foreach (var k in known)
            if (Regex.IsMatch(padded, @"(?<![a-z])" + Regex.Escape(k.ToLowerInvariant()) + @"(?![a-z])")) return k;
        foreach (var (item, words) in ItemWords)
            if (words.Any(w => padded.Contains(w, StringComparison.OrdinalIgnoreCase))) return item;
        return null;
    }

    private string? Subcontractor(string q)
    {
        var names = _d.Project.Snapshot.SubInvoices.Select(i => i.Subcontractor).Concat(_d.Project.Snapshot.Claims.Select(c => c.Subcontractor))
            .Concat(_d.Project.Snapshot.Subcontractors.Select(s => s.Name)).Where(n => n.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.OrderByDescending(n => n.Length))
        {
            if (q.Contains(n, StringComparison.OrdinalIgnoreCase)) return n;
            var first = n.Split(' ')[0];
            if (first.Length >= 4 && Regex.IsMatch(q, @"\b" + Regex.Escape(first) + @"\b", RegexOptions.IgnoreCase)) return n;
        }
        return null;
    }

    private static bool Any(string q, params string[] words) => words.Any(w => q.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Which intent the question matches (for tests and the "offline" chip).</summary>
    public string Classify(string q)
    {
        if (DnRx.IsMatch(q) || (Any(q, "where is", "وين", "أين", "اين") && Regex.IsMatch(q, @"\b\d{7,}\b"))) return "dn";
        var room = RoomRx.Match(q);
        if (room.Success && (Any(q, "remain", "left", "balance", "how many", "how much", "claimed", "متبقي", "المتبقي", "باقي", "الباقي", "كم") || Stage(q) != null || Item(q) != null)) return "remaining";
        if (InvRx.IsMatch(q)) return "invoice";
        if (PoRx.IsMatch(q)) return "dn";
        if (room.Success) return "room";
        if (Any(q, "today", "needs me", "need me", "to do", "todo", "what should i", "queue", "اليوم", "المطلوب", "مهام")) return "today";
        if (Any(q, "anomal", "suspicious", "wrong", "unusual", "copy", "duplicate", "مشبوه", "غريب", "مكرر")) return "anomalies";
        if (Any(q, "rule", "why", "explain", "what is", "what does", "15 m", "15m", "4.5", "قاعدة", "لماذا", "ليش", "اشرح", "ما هو", "ما هي")) return "rule";
        if (Any(q, "find", "search", "where", "document", "ابحث", "بحث", "مستند", "وين", "أين")) return "search";
        return "help";
    }

    public OfflineAnswer Answer(string question)
    {
        var q = question.Trim();
        var ar = IsArabic(q) || _d.Settings.IsArabic;
        var intent = Classify(q);
        try
        {
            return intent switch
            {
                "remaining" => Remaining(q, ar),
                "dn" => Dn(q, ar),
                "invoice" => Invoice(q, ar),
                "room" => Room(RoomRx.Match(q).Groups[1].Value.ToUpperInvariant(), ar),
                "today" => Today(ar),
                "anomalies" => Anomalies(ar),
                "rule" => Rule(q, ar),
                "search" => Search(q, ar),
                _ => Help(ar),
            };
        }
        catch (Exception ex)
        {
            return new OfflineAnswer(intent, (ar ? "تعذر الرد من البيانات المحلية: " : "Could not answer from local data: ") + ex.Message, new());
        }
    }

    private (JsonObject? Json, string Error, List<Citation> Cites) Run(string tool, JsonObject input)
    {
        var o = _tools.Execute(tool, input, "offline", 0);
        return o.IsError ? (null, o.Content, o.Citations) : (JsonNode.Parse(o.Content) as JsonObject, "", o.Citations);
    }

    private static string N(JsonNode? n) => n is null ? "-" : ((double)n).ToString("#,0.##", CultureInfo.InvariantCulture);

    private OfflineAnswer Remaining(string q, bool ar)
    {
        var room = RoomRx.Match(q).Groups[1].Value.ToUpperInvariant();
        var stage = Stage(q);
        var item = Item(q);
        var input = new JsonObject { ["room"] = room };
        if (stage != null) input["stage"] = stage;
        if (item != null) input["item"] = item;
        if (item is null) input["group_by"] = "item";
        var (json, error, cites) = Run(AssistantTools.QueryLedger, input);
        if (json is null) return new("remaining", error, cites);
        var sb = new StringBuilder();
        var stages = json["by_stage"]!.AsArray().OfType<JsonObject>().ToList();
        if (stages.Count == 0)
            return new("remaining", ar ? $"لا توجد كمية مشروع ولا مطالبات للغرفة {room}{(stage is null ? "" : " " + stage)}{(item is null ? "" : " " + item)}." :
                $"No PROJECT QTY and no claims for {room}{(stage is null ? "" : " " + stage)}{(item is null ? "" : " " + item)}.", cites);
        sb.AppendLine(ar ? $"الغرفة {room}{(item is null ? "" : " - " + item)}:" : $"{room}{(item is null ? "" : " " + item)}:");
        foreach (var s in stages)
        {
            if (item != null)
                sb.AppendLine(ar
                    ? $"- {s["stage"]}: المتبقي {N(s["remaining"])} من كمية المشروع {N(s["project_qty"])} (المطالب به من كل المقاولين {N(s["claimed_all_subcontractors"])})"
                    : $"- {s["stage"]}: REMAINING {N(s["remaining"])} of PROJECT QTY {N(s["project_qty"])} (claimed by all subcontractors {N(s["claimed_all_subcontractors"])})");
            else
            {
                sb.AppendLine($"- {s["stage"]}:");
                foreach (var g in (s["groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    sb.AppendLine(ar ? $"    {g["group"]}: المتبقي {N(g["remaining"])} من {N(g["project_qty"])}" : $"    {g["group"]}: REMAINING {N(g["remaining"])} of {N(g["project_qty"])}");
                if ((s["groups"] as JsonArray)?.Count is null or 0)
                    sb.AppendLine(ar ? $"    المتبقي {N(s["remaining"])} من {N(s["project_qty"])}" : $"    REMAINING {N(s["remaining"])} of {N(s["project_qty"])}");
            }
            if ((int?)s["over_cap_keys"] is > 0) sb.AppendLine(ar ? $"    تنبيه: {s["over_cap_keys"]} بند فوق السقف (OVER)" : $"    {s["over_cap_keys"]} key(s) OVER the cap");
        }
        sb.Append(ar ? "المراحل لا تُجمع مع بعضها." : "Stages are never added together.");
        return new("remaining", sb.ToString(), cites.Where(c => c.Kind == "room").Take(3).ToList());
    }

    private OfflineAnswer Dn(string q, bool ar)
    {
        var m = DnRx.Match(q);
        var query = m.Success ? m.Groups[1].Value : PoRx.Match(q) is { Success: true } p ? p.Groups[1].Value : Regex.Match(q, @"\b\d{6,}\b").Value;
        var (json, error, cites) = Run(AssistantTools.FindDn, new JsonObject { ["query"] = query });
        if (json is null) return new("dn", ar ? $"لم يتم العثور على إشعار التسليم {query}." : error, cites);
        var sb = new StringBuilder();
        foreach (var r in json["delivery_notes"]!.AsArray().OfType<JsonObject>().Take(8))
        {
            sb.AppendLine(ar
                ? $"إشعار التسليم {r["dn_no"]} ({r["supplier"]}، {r["dn_date"]}): أمر الشراء {Or(r["po_no"])}، الفاتورة {Or(r["invoice"])} {Or(r["invoice_status"], "")}، طلب المواد MIR {Or(r["mir_no"])} {Or(r["mir_status"], "")}"
                : $"DN {r["dn_no"]} ({r["supplier"]}, {r["dn_date"]}): PO {Or(r["po_no"])}, invoice {Or(r["invoice"])} {Or(r["invoice_status"], "")}, MIR {Or(r["mir_no"])} {Or(r["mir_status"], "")}");
            if (((string?)r["match"])?.Length > 0) sb.AppendLine((ar ? "  المطابقة: " : "  match: ") + r["match"]);
        }
        return new("dn", sb.ToString().TrimEnd(), cites);
    }

    private static string Or(JsonNode? n, string empty = "-") => n is null || ((string?)n.ToString())?.Length == 0 ? empty : n.ToString();

    private OfflineAnswer Invoice(string q, bool ar)
    {
        var no = int.Parse(InvRx.Match(q).Groups[1].Value, CultureInfo.InvariantCulture);
        var sub = Subcontractor(q);
        var input = new JsonObject { ["invoice_no"] = no };
        if (sub != null) input["subcontractor"] = sub;
        var (json, error, cites) = Run(AssistantTools.GetInvoice, input);
        if (json is null) return new("invoice", error, cites);
        var sb = new StringBuilder();
        foreach (var inv in json["invoices"]!.AsArray().OfType<JsonObject>().Take(6))
        {
            sb.AppendLine($"{inv["subcontractor"]} INV-{(int)inv["invoice_no"]!:00} ({inv["contract_no"]})");
            foreach (var r in inv["revisions"]!.AsArray().OfType<JsonObject>())
                sb.AppendLine(ar
                    ? $"- المراجعة {r["revision"]}: {r["status"]}، Aconex {Or(r["aconex_workflow"])} - {r["aconex_status"]}، هذه الفترة SAR {N(r["gross_this_period_sar"])}{(((string?)r["rejection_reason"])?.Length > 0 ? "، سبب الرفض: " + r["rejection_reason"] : "")}"
                    : $"- Rev {r["revision"]}: {r["status"]}, Aconex {Or(r["aconex_workflow"])} - {r["aconex_status"]}, this period SAR {N(r["gross_this_period_sar"])}{(((string?)r["rejection_reason"])?.Length > 0 ? ", rejected: " + r["rejection_reason"] : "")}");
        }
        return new("invoice", sb.ToString().TrimEnd(), cites);
    }

    private OfflineAnswer Room(string room, bool ar)
    {
        var (json, error, cites) = Run(AssistantTools.GetRoom, new JsonObject { ["room"] = room });
        if (json is null) return new("room", error, cites);
        var sb = new StringBuilder();
        sb.AppendLine($"{room} - {Or(json["building"])} {Or(json["level"], "")} {Or(json["type"], "")} {(json["area_type"] is { } at && at.ToString().Length > 0 ? "(" + at + ")" : "")}".Trim());
        foreach (var b in json["balances"]!.AsArray().OfType<JsonObject>())
            sb.AppendLine(ar
                ? $"- {b["stage"]} {b["item"]}: المتبقي {N(b["remaining"])} من {N(b["project_qty"])}{((bool?)b["over"] == true ? " OVER" : "")}"
                : $"- {b["stage"]} {b["item"]}: REMAINING {N(b["remaining"])} of {N(b["project_qty"])}{((bool?)b["over"] == true ? " OVER" : "")}");
        var pending = json["pending_checks"]!.AsArray().Count;
        if (pending > 0) sb.AppendLine(ar ? $"فحوصات معلقة: {pending}" : $"Pending checks: {pending}");
        return new("room", sb.ToString().TrimEnd(), cites);
    }

    private OfflineAnswer Today(bool ar)
    {
        var (json, error, cites) = Run(AssistantTools.ListNeedsToday, new JsonObject { ["limit"] = 8 });
        if (json is null) return new("today", error, cites);
        var sb = new StringBuilder(ar ? $"المطلوب منك اليوم ({json["count_total"]} بند):\n" : $"Needs you today ({json["count_total"]} items):\n");
        foreach (var i in json["items"]!.AsArray().OfType<JsonObject>()) sb.AppendLine($"- [{i["severity"]}] {i["title"]}: {i["detail"]}");
        foreach (var r in json["reminders_due"]!.AsArray().OfType<JsonObject>()) sb.AppendLine((ar ? "- تذكير: " : "- REMINDER: ") + r["text"] + " (" + r["due"] + ")");
        return new("today", sb.ToString().TrimEnd(), cites.Take(10).ToList());
    }

    private OfflineAnswer Anomalies(bool ar)
    {
        var (json, error, cites) = Run(AssistantTools.ListAnomalies, new JsonObject { ["limit"] = 10 });
        if (json is null) return new("anomalies", error, cites);
        var items = json["items"]!.AsArray().OfType<JsonObject>().ToList();
        if (items.Count == 0) return new("anomalies", ar ? "لا توجد مطالبات غير عادية في البيانات." : "No unusual claims found.", cites);
        var sb = new StringBuilder(ar ? "مطالبات غير عادية:\n" : "Unusual claims:\n");
        foreach (var i in items) sb.AppendLine($"- [{i["severity"]}] {i["title"]} - {i["detail"]}");
        return new("anomalies", sb.ToString().TrimEnd(), cites);
    }

    private OfflineAnswer Rule(string q, bool ar)
    {
        var rules = HouseRuleCatalog.Find(q, _d.Project.Engine.Rules.Select(r => (r.Code, r.Title, r.Description))).Take(2).ToList();
        if (rules.Count == 0) return Help(ar);
        var text = string.Join("\n\n", rules.Select(r => $"{r.Title}: {(ar ? r.TextAr : r.Text)}"));
        return new("rule", text, rules.Select(r => Citation.Rule(r.Code, r.Title)).ToList());
    }

    private OfflineAnswer Search(string q, bool ar)
    {
        var words = Regex.Replace(q, @"\b(find|search|for|where|is|the|a|document|documents|ابحث|عن|بحث|وين|أين|اين|مستند)\b", " ", RegexOptions.IgnoreCase).Trim();
        if (words.Length == 0) return Help(ar);
        var (json, error, cites) = Run(AssistantTools.SearchDocuments, new JsonObject { ["query"] = words, ["limit"] = 8 });
        if (json is null) return new("search", error, cites);
        var hits = json["hits"]!.AsArray().OfType<JsonObject>().ToList();
        if (hits.Count == 0) return new("search", ar ? $"لا توجد نتائج لـ \"{words}\"." : $"Nothing found for \"{words}\".", cites);
        var sb = new StringBuilder();
        foreach (var h in hits) sb.AppendLine($"- {h["title"]}{(h["page"] is null ? "" : " p." + h["page"])}: {h["snippet"]}");
        return new("search", sb.ToString().TrimEnd(), cites);
    }

    private static OfflineAnswer Help(bool ar) => new("help", ar
        ? "المساعد يعمل بدون اتصال (لا يوجد مفتاح API). يمكنني الإجابة على أسئلة مثل:\n- المتبقي في P2-106 التمديد الثاني إنارة\n- أين إشعار التسليم 81064344\n- حالة فاتورة ROOTS رقم 3\n- ما المطلوب اليوم\n- اشرح قاعدة 15 م"
        : "The assistant is offline (no API key or no connection). I can answer from local data, for example:\n- remaining in P2-106 2nd fix light\n- where is DN 81064344\n- status of ROOTS INV 3\n- P2-106\n- what needs me today\n- explain the 15 m rule\nAdd an API key in Settings for full answers.", new());
}
