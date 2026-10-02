using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Raffaello.Core.Assistant;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Localization;
using Raffaello.Core.Queue;

namespace Raffaello.Core.Notify;

public sealed record BriefItem(string Severity, string Title, string Detail, string Module = "", string Key = "", bool IsNew = false);

public sealed record BriefSection(string Code, string TitleKey, int Count, int? Delta, List<BriefItem> Items)
{
    public string Title(string lang) => Loc.Get(TitleKey, lang);
    public bool HasNews => Items.Any(i => i.IsNew) || Delta is > 0;
}

/// <summary>The morning brief of one user: what changed since the previous brief and what needs attention today.</summary>
public sealed class Brief
{
    public string Owner { get; init; } = "";
    public DateTime Date { get; init; }
    public DateTime Since { get; init; }
    public DateTime BuiltAt { get; init; }
    public string Language { get; init; } = Loc.English;
    public List<BriefSection> Sections { get; init; } = new();
    public Dictionary<string, double> Metrics { get; init; } = new();
    public Dictionary<string, List<string>> Keys { get; init; } = new();
    /// <summary>Natural-language summary written by Claude (optional).</summary>
    public string Summary { get; set; } = "";

    public int Attention => Sections.Sum(s => s.Items.Count(i => i.Severity is "OVER" or "CHECK" or "DUE"));

    /// <summary>Headline for a notification: "3 new claims, 2 over the cap, 1 Aconex step overdue".</summary>
    public string Headline()
    {
        var parts = Sections.Where(s => s.Count > 0).Select(s => $"{s.Count} {s.Title(Language).ToLower(Loc.Culture(Language))}");
        var t = string.Join(", ", parts);
        return t.Length == 0 ? Loc.Get("Brief_Nothing", Language) : t;
    }

    public string ToText()
    {
        var c = Loc.Culture(Language);
        var sb = new StringBuilder();
        sb.Append(Loc.Get("Brief_Title", Language)).Append(" - ").Append(Date.ToString("dddd dd MMM yyyy", c)).Append(" - ").Append(Owner).Append('\n');
        sb.Append(Loc.Get("Brief_Since", Language)).Append(' ').Append(Since.ToString("dd MMM HH:mm", c)).Append("\n\n");
        if (Summary.Length > 0) sb.Append(Summary.Trim()).Append("\n\n");
        foreach (var s in Sections)
        {
            sb.Append(s.Title(Language)).Append(" (").Append(s.Count.ToString(c));
            if (s.Delta is int d && d != 0) sb.Append(d > 0 ? ", +" : ", ").Append(d.ToString(c));
            sb.Append(")\n");
            if (s.Items.Count == 0) sb.Append("  ").Append(Loc.Get("Brief_Nothing", Language)).Append('\n');
            foreach (var i in s.Items.Take(8))
                sb.Append("  - ").Append(i.IsNew ? "[" + Loc.Get("Brief_New", Language).ToUpper(c) + "] " : "").Append(i.Title).Append(i.Detail.Length > 0 ? ": " + i.Detail : "").Append('\n');
            if (s.Items.Count > 8) sb.Append("  ... +").Append((s.Items.Count - 8).ToString(c)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public string ToHtml()
    {
        var rtl = Language == Loc.Arabic;
        var c = Loc.Culture(Language);
        string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append($"<html dir=\"{(rtl ? "rtl" : "ltr")}\"><body style=\"font-family:Segoe UI,Tahoma,Arial,sans-serif;color:#000;background:#fff\">");
        sb.Append($"<div style=\"background:#8B0000;color:#fff;padding:12px 16px\"><b style=\"font-size:18px\">{E(Loc.Get("Brief_Title", Language))}</b><br/>{E(Date.ToString("dddd dd MMM yyyy", c))} - {E(Owner)}</div>");
        if (Summary.Length > 0) sb.Append($"<p style=\"padding:8px 16px;background:#FFFF00\">{E(Summary).Replace("\n", "<br/>")}</p>");
        foreach (var s in Sections)
        {
            sb.Append($"<h3 style=\"margin:14px 16px 4px;border-bottom:2px solid #A6A6A6\">{E(s.Title(Language))} ({s.Count.ToString(c)}{(s.Delta is int d && d != 0 ? (d > 0 ? ", +" : ", ") + d.ToString(c) : "")})</h3><ul style=\"margin:0 16px\">");
            if (s.Items.Count == 0) sb.Append($"<li style=\"color:#666\">{E(Loc.Get("Brief_Nothing", Language))}</li>");
            foreach (var i in s.Items.Take(12))
                sb.Append($"<li><b>{(i.IsNew ? "[" + E(Loc.Get("Brief_New", Language).ToUpper(c)) + "] " : "")}{E(i.Title)}</b>{(i.Detail.Length > 0 ? " - " + E(i.Detail) : "")}</li>");
            sb.Append("</ul>");
        }
        sb.Append($"<p style=\"color:#666;font-size:11px;padding:8px 16px\">Raffaello - {E(Loc.Get("Brief_Generated", Language))} {BuiltAt.ToString("dd MMM yyyy HH:mm", c)}</p></body></html>");
        return sb.ToString();
    }
}

/// <summary>Everything the brief reads. Module data is optional.</summary>
public sealed class BriefInputs
{
    public required ProjectSnapshot Snapshot { get; init; }
    /// <summary>"Needs you today" (core + module sources: Aconex, materials, VOs).</summary>
    public IReadOnlyList<QueueItem> Queue { get; init; } = Array.Empty<QueueItem>();
    public IReadOnlyList<AssistantReminder> Reminders { get; init; } = Array.Empty<AssistantReminder>();
    public BriefSnapshot? Previous { get; init; }
    public string Owner { get; init; } = "";
    public string Language { get; init; } = Loc.English;
    public DateTime Now { get; init; } = DateTime.Now;
    public int WirDueDays { get; init; } = 14;
}

/// <summary>
/// Builds the morning brief from the project data: new claims, over-cap keys, checks pending, invoices awaiting you, Aconex steps
/// overdue, DNs without MIR, VO ageing, obligations / reminders due - each compared with the previous brief ("+3 since yesterday").
/// </summary>
public static class MorningBriefBuilder
{
    public static Brief Build(BriefInputs i)
    {
        var s = i.Snapshot;
        var lang = Loc.Normalize(i.Language);
        var c = Loc.Culture(lang);
        var prevMetrics = new Dictionary<string, double>();
        var prevKeys = new Dictionary<string, List<string>>();
        if (i.Previous != null) ReadMetrics(i.Previous.MetricsJson, prevMetrics, prevKeys);
        var since = i.Previous?.BuiltAt ?? i.Now.Date.AddDays(-1);
        var metrics = new Dictionary<string, double>();
        var keys = new Dictionary<string, List<string>>();
        var sections = new List<BriefSection>();

        int? Delta(string code, int now) => prevMetrics.TryGetValue(code, out var p) ? now - (int)p : null;
        bool IsNew(string code, string key) => i.Previous != null && !(prevKeys.GetValueOrDefault(code)?.Contains(key) ?? false);
        BriefSection Add(string code, string titleKey, List<BriefItem> items, IEnumerable<string>? itemKeys = null, int? count = null)
        {
            var n = count ?? items.Count;
            metrics[code] = n;
            if (itemKeys != null) keys[code] = itemKeys.Distinct().ToList();
            var sec = new BriefSection(code, titleKey, n, Delta(code, n), items);
            sections.Add(sec);
            return sec;
        }

        // 1. new claims since the previous brief, per subcontractor
        var newClaims = s.Claims.Where(cl => cl.EnteredAt > since && cl.Source != "REVERSAL").ToList();
        Add("NEW_CLAIMS", "Brief_NewClaims", newClaims.GroupBy(cl => cl.Subcontractor).OrderByDescending(g => g.Count()).Select(g => new BriefItem(
                g.Any(x => x.IsOver) ? "OVER" : "OPEN",
                $"{g.Key}: {g.Count().ToString(c)} lines",
                string.Join(", ", g.GroupBy(x => x.Stage).Select(x => $"{x.Key} {x.Sum(y => y.Qty).ToString("#,0.##", c)}")) + (g.Any(x => x.IsOver) ? $" - {g.Count(x => x.IsOver)} OVER" : ""),
                "Ledger", g.First().Room, true)).ToList(), count: newClaims.Count);

        // 2. over the cap (new keys flagged)
        var over = LedgerRules.Balances(s.RoomQtys, s.Claims).Values.Where(b => b.HasCap && b.IsOver).OrderByDescending(b => b.Claimed - b.ProjectQty).ToList();
        var overKeys = over.Select(b => LedgerKeys.Key(b.Room, b.Stage, b.Item)).ToList();
        Add("OVER_CAP", "Brief_OverCap", over.Select(b => new BriefItem("OVER", $"{b.Room} {b.Stage} {b.Item}",
            $"{b.Claimed.ToString("#,0.##", c)} / {b.ProjectQty.ToString("#,0.##", c)} ({string.Join(", ", b.BySubcontractor.Select(kv => kv.Key))})", "Ledger", b.Room,
            IsNew("OVER_CAP", LedgerKeys.Key(b.Room, b.Stage, b.Item)))).OrderByDescending(x => x.IsNew).ToList(), overKeys);

        // 3. checks pending (>4.5 m, 15 m)
        var pending = s.Claims.Where(cl => HeightCheck.IsPending(cl) || LengthCheck.IsPending(cl)).ToList();
        Add("CHECKS", "Brief_Checks", pending.GroupBy(cl => (cl.Subcontractor, Kind: HeightCheck.IsPending(cl) ? ">4.5 m" : "15 m")).Select(g => new BriefItem("DUE",
            $"{g.Key.Subcontractor}: {g.Count().ToString(c)} x {g.Key.Kind}",
            $"oldest {g.Min(x => x.EnteredAt).ToString("dd MMM", c)}", "Checks", "", g.Any(x => x.EnteredAt > since))).ToList(), count: pending.Count);

        // 4. invoices awaiting you (latest revision per invoice)
        var latest = s.SubInvoices.GroupBy(x => (x.ContractNo, x.Subcontractor, x.InvoiceNo)).Select(g => g.OrderBy(x => x.Revision).Last()).ToList();
        var awaiting = latest.Where(x => x.Status is SubInvoiceStatus.Draft or SubInvoiceStatus.Rejected).ToList();
        var invItems = awaiting.Select(x => new BriefItem(x.Status == SubInvoiceStatus.Rejected ? "CHECK" : "DUE",
            x.Title, x.Status == SubInvoiceStatus.Rejected ? $"REJECTED - {x.RejectionReason}" : "DRAFT - not submitted", "Invoices", $"{x.ContractNo}|{x.Subcontractor}|{x.InvoiceNo}",
            IsNew("INVOICES", $"{x.Title}|{x.Status}"))).ToList();
        invItems.AddRange(i.Queue.Where(q => q.Category == "PACKAGE").Select(q => new BriefItem(q.Tag, q.Title, q.Detail, q.Target.Module, q.Target.Key ?? "")));
        Add("INVOICES", "Brief_Invoices", invItems, awaiting.Select(x => $"{x.Title}|{x.Status}"));

        // 5. Aconex overdue steps / outcomes waiting to be recorded (from the module queue)
        var aconex = i.Queue.Where(q => q.Category == "ACONEX").ToList();
        Add("ACONEX", "Brief_Aconex", aconex.Select(q => new BriefItem(q.Tag, q.Title, q.Detail, q.Target.Module, q.Target.Key ?? "", IsNew("ACONEX", q.Title))).ToList(), aconex.Select(q => q.Title));

        // 6. materials: DNs without MIR, over PO, mismatches
        var mats = i.Queue.Where(q => q.Category == "MATERIAL").ToList();
        Add("MATERIALS", "Brief_Materials", mats.Select(q => new BriefItem(q.Tag, q.Title, q.Detail, q.Target.Module, q.Target.Key ?? "", IsNew("MATERIALS", q.Title))).ToList(), mats.Select(q => q.Title));

        // 7. VO ageing
        var vos = i.Queue.Where(q => q.Category == "VO").ToList();
        Add("VO", "Brief_Vo", vos.Select(q => new BriefItem(q.Tag, q.Title, q.Detail, q.Target.Module, q.Target.Key ?? "", IsNew("VO", q.Title))).ToList(), vos.Select(q => q.Title));

        // 8. obligations: reminders due by tonight, cumulative blocks waiting, open WIRs over the limit
        var due = i.Reminders.Where(r => !r.Done && r.Due <= i.Now.Date.AddDays(1)).OrderBy(r => r.Due).ToList();
        var obligations = due.Select(r => new BriefItem(r.Due < i.Now ? "DUE" : "OPEN", r.Text, r.Due.ToString("dd MMM HH:mm", c), r.TargetModule, r.TargetKey, r.CreatedAt > since)).ToList();
        obligations.AddRange(i.Queue.Where(q => q.Category is "CUMULATIVE" or "WIR").Select(q => new BriefItem(q.Tag, q.Title, q.Detail, q.Target.Module, q.Target.Key ?? "")));
        Add("OBLIGATIONS", "Brief_Obligations", obligations);

        return new Brief
        {
            Owner = i.Owner, Date = i.Now.Date, Since = since, BuiltAt = i.Now, Language = lang, Sections = sections, Metrics = metrics, Keys = keys,
        };
    }

    public static string MetricsJson(Brief b) => JsonSerializer.Serialize(new { counts = b.Metrics, keys = b.Keys });

    private static void ReadMetrics(string json, Dictionary<string, double> m, Dictionary<string, List<string>> k)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.TryGetProperty("counts", out var counts))
                foreach (var p in counts.EnumerateObject()) if (p.Value.TryGetDouble(out var d)) m[p.Name] = d;
            if (doc.RootElement.TryGetProperty("keys", out var keys))
                foreach (var p in keys.EnumerateObject()) k[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        }
        catch (JsonException) { }
    }

    /// <summary>Stores today's brief figures (one per user per day; the next brief compares against it).</summary>
    public static BriefSnapshot Save(IAssistantStore store, Brief b)
    {
        var existing = store.All<BriefSnapshot>().FirstOrDefault(x => string.Equals(x.Owner, b.Owner, StringComparison.OrdinalIgnoreCase) && x.Date.Date == b.Date.Date);
        if (existing != null)
        {
            existing.BuiltAt = b.BuiltAt;
            existing.MetricsJson = MetricsJson(b);
            if (b.Summary.Length > 0) existing.Summary = b.Summary;
            return store.Update(existing, $"Morning brief {b.Date:yyyy-MM-dd} updated for {b.Owner}");
        }
        return store.Insert(new BriefSnapshot { Owner = b.Owner, Date = b.Date.Date, BuiltAt = b.BuiltAt, MetricsJson = MetricsJson(b), Summary = b.Summary },
            $"Morning brief {b.Date:yyyy-MM-dd} for {b.Owner}");
    }

    /// <summary>The prompt for the optional Claude summary (figures only, no documents).</summary>
    public static string SummaryPrompt(Brief b) =>
        (b.Language == Loc.Arabic
            ? "اكتب ملخصاً صباحياً قصيراً (3 إلى 5 أسطر، بالعربية الفصحى البسيطة، بدون رموز تعبيرية) لمهندس الكميات من أرقام الموجز التالية. ابدأ بأهم ما يحتاج تصرفاً اليوم.\n\n"
            : "Write a short morning summary (3 to 5 lines, plain site-office English, no emojis) for the QS engineer from the brief below. Lead with what needs action today.\n\n")
        + b.ToText();
}
