using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raffaello.Core.Assistant;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Localization;
using Raffaello.Core.Queue;

namespace Raffaello.Core.Notify;

public static class NotifyEvents
{
    public const string All = "*";
    public const string NewClaims = "NEW_CLAIMS";
    public const string OverCap = "OVER_CAP";
    public const string ChecksPending = "CHECKS";
    public const string InvoiceAwaiting = "INVOICE_AWAITING";
    public const string InvoiceRejected = "INVOICE_REJECTED";
    public const string AconexOverdue = "ACONEX_OVERDUE";
    public const string DnWithoutMir = "DN_WITHOUT_MIR";
    public const string VoAgeing = "VO_AGEING";
    public const string ReminderDue = "REMINDER_DUE";
    public const string Brief = "BRIEF";

    public static readonly string[] Kinds = { All, NewClaims, OverCap, ChecksPending, InvoiceAwaiting, InvoiceRejected, AconexOverdue, DnWithoutMir, VoAgeing, ReminderDue, Brief };

    public static string LabelKey(string kind) => kind switch
    {
        NewClaims => "Evt_NewClaims", OverCap => "Evt_OverCap", ChecksPending => "Evt_ChecksPending", InvoiceAwaiting => "Evt_InvoiceAwaiting",
        InvoiceRejected => "Evt_InvoiceRejected", AconexOverdue => "Evt_AconexOverdue", DnWithoutMir => "Evt_DnWithoutMir", VoAgeing => "Evt_VoAgeing",
        ReminderDue => "Evt_ReminderDue", Brief => "Evt_Brief", _ => "Evt_All",
    };
}

public static class NotifyChannels
{
    public const string InApp = "INAPP";
    public const string Windows = "WINDOWS";
    public const string Email = "EMAIL";
    public const string Teams = "TEAMS";
    public const string WhatsApp = "WHATSAPP";
    public static readonly string[] All = { InApp, Windows, Email, Teams, WhatsApp };
    public static bool IsExternal(string c) => c is Email or Teams or WhatsApp;
}

public static class Severity
{
    public static readonly string[] Order = { "OK", "OPEN", "DUE", "CHECK", "OVER" };
    public static int Rank(string? s) => Math.Max(0, Array.IndexOf(Order, (s ?? "").ToUpperInvariant()));
}

/// <summary>Something worth telling the user. <see cref="Key"/> is stable while the situation is the same (no repeats).</summary>
public sealed record NotificationEvent(string Kind, string Severity, string Title, string Body, string Key, string Module = "", string NavKey = "")
{
    public byte[]? Attachment { get; init; }
    public string AttachmentName { get; init; } = "";
    public string Html { get; init; } = "";
}

/// <summary>A delivery channel (in-app, Windows, e-mail, Teams, WhatsApp webhook).</summary>
public interface INotificationChannel
{
    string Code { get; }
    bool IsConfigured { get; }
    Task SendAsync(NotificationEvent e, string address, CancellationToken ct);
}

/// <summary>Channel implemented by the host (in-app toasts, Windows notifications).</summary>
public sealed class DelegateChannel : INotificationChannel
{
    private readonly Func<NotificationEvent, Task> _send;
    public DelegateChannel(string code, Func<NotificationEvent, Task> send, bool configured = true) { Code = code; _send = send; IsConfigured = configured; }
    public string Code { get; }
    public bool IsConfigured { get; }
    public Task SendAsync(NotificationEvent e, string address, CancellationToken ct) => _send(e);
}

public sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public bool Ssl { get; init; } = true;
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
    public string From { get; init; } = "";
    public string DefaultTo { get; init; } = "";
}

/// <summary>E-mail through the office SMTP server (settings; password from the DPAPI vault). HTML body + optional PDF.</summary>
public sealed class EmailChannel : INotificationChannel
{
    private readonly SmtpOptions _o;
    public EmailChannel(SmtpOptions o) => _o = o;
    public string Code => NotifyChannels.Email;
    public bool IsConfigured => _o.Host.Length > 0 && (_o.From.Length > 0 || _o.User.Contains('@')) && _o.DefaultTo.Length > 0;

    public MailMessage Build(NotificationEvent e, string address)
    {
        var to = address.Length > 0 ? address : _o.DefaultTo;
        var msg = new MailMessage { From = new MailAddress(_o.From.Length > 0 ? _o.From : _o.User, "Raffaello"), Subject = "Raffaello - " + e.Title, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        foreach (var a in to.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) msg.To.Add(a);
        msg.IsBodyHtml = e.Html.Length > 0;
        msg.Body = e.Html.Length > 0 ? e.Html : e.Body;
        if (e.Attachment is { Length: > 0 } bytes)
            msg.Attachments.Add(new System.Net.Mail.Attachment(new MemoryStream(bytes), e.AttachmentName.Length > 0 ? e.AttachmentName : "raffaello.pdf", "application/pdf"));
        return msg;
    }

    public async Task SendAsync(NotificationEvent e, string address, CancellationToken ct)
    {
        using var msg = Build(e, address);
        using var smtp = new SmtpClient(_o.Host, _o.Port) { EnableSsl = _o.Ssl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (_o.User.Length > 0) smtp.Credentials = new NetworkCredential(_o.User, _o.Password);
        await smtp.SendMailAsync(msg, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Microsoft Teams incoming webhook (Workflows "post to a channel when a webhook request is received", or a legacy connector URL):
/// an Adaptive Card with the title, the text and an accent bar.
/// </summary>
public sealed class TeamsChannel : INotificationChannel
{
    private readonly string _url;
    private readonly HttpClient _http;
    public TeamsChannel(string url, HttpClient? http = null) { _url = url ?? ""; _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) }; }
    public string Code => NotifyChannels.Teams;
    public bool IsConfigured => Uri.TryCreate(_url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    public static JsonObject Payload(NotificationEvent e) => new()
    {
        ["type"] = "message",
        ["summary"] = e.Title,
        ["attachments"] = new JsonArray(new JsonObject
        {
            ["contentType"] = "application/vnd.microsoft.card.adaptive",
            ["content"] = new JsonObject
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.4",
                ["msteams"] = new JsonObject { ["width"] = "Full" },
                ["body"] = new JsonArray(
                    new JsonObject { ["type"] = "TextBlock", ["text"] = e.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["color"] = e.Severity is "OVER" ? "Attention" : "Default", ["wrap"] = true },
                    new JsonObject { ["type"] = "TextBlock", ["text"] = e.Body, ["wrap"] = true, ["fontType"] = "Default" },
                    new JsonObject { ["type"] = "TextBlock", ["text"] = "Raffaello  |  " + e.Severity, ["isSubtle"] = true, ["size"] = "Small" }),
            },
        }),
    };

    public async Task SendAsync(NotificationEvent e, string address, CancellationToken ct)
    {
        using var content = new StringContent(Payload(e).ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync(address.Length > 0 ? address : _url, content, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Teams webhook answered HTTP {(int)res.StatusCode}.");
    }
}

/// <summary>
/// WhatsApp through any gateway that takes a webhook (Twilio function, Meta Cloud API relay, a company gateway ...): POSTs the body
/// template with {to}, {title}, {text} filled in (JSON-escaped) and an optional auth header. No vendor lock-in.
/// </summary>
public sealed class WhatsAppWebhookChannel : INotificationChannel
{
    private readonly string _url, _template, _to, _headerName, _headerValue;
    private readonly HttpClient _http;

    public WhatsAppWebhookChannel(string url, string template, string to, string headerName = "", string headerValue = "", HttpClient? http = null)
    {
        _url = url ?? ""; _template = string.IsNullOrWhiteSpace(template) ? "{\"to\":\"{to}\",\"text\":\"{title}\\n{text}\"}" : template;
        _to = to ?? ""; _headerName = headerName ?? ""; _headerValue = headerValue ?? "";
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string Code => NotifyChannels.WhatsApp;
    public bool IsConfigured => Uri.TryCreate(_url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    private static string J(string s) { var j = JsonSerializer.Serialize(s ?? ""); return j[1..^1]; }

    public string Body(NotificationEvent e, string address)
    {
        var text = e.Body.Length > 3500 ? e.Body[..3500] + "..." : e.Body;
        return _template.Replace("{to}", J(address.Length > 0 ? address : _to)).Replace("{title}", J(e.Title)).Replace("{text}", J(text));
    }

    public async Task SendAsync(NotificationEvent e, string address, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(Body(e, address), Encoding.UTF8, "application/json") };
        if (_headerName.Length > 0 && _headerValue.Length > 0) req.Headers.TryAddWithoutValidation(_headerName, _headerValue);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"WhatsApp webhook answered HTTP {(int)res.StatusCode}.");
    }
}

/// <summary>Turns the current state of the project into notification events (stable keys, so a router sends each once).</summary>
public static class NotifyEventDetector
{
    public static List<NotificationEvent> Detect(ProjectSnapshot s, IReadOnlyList<QueueItem> queue, IReadOnlyList<AssistantReminder> reminders, DateTime since, DateTime now, string lang = Loc.English)
    {
        var c = Loc.Culture(lang);
        var ev = new List<NotificationEvent>();
        var newClaims = s.Claims.Where(x => x.EnteredAt > since && x.Source != "REVERSAL").ToList();
        foreach (var g in newClaims.GroupBy(x => x.Subcontractor))
            ev.Add(new(NotifyEvents.NewClaims, g.Any(x => x.IsOver) ? "CHECK" : "OPEN", $"{g.Key}: {g.Count()} new claim lines",
                string.Join(", ", g.GroupBy(x => x.Stage).Select(x => $"{x.Key} {x.Sum(y => y.Qty).ToString("#,0.##", c)}")),
                $"claims|{g.Key}|{g.Max(x => x.Id)}", "Ledger", g.First().Room));
        foreach (var b in LedgerRules.Balances(s.RoomQtys, s.Claims).Values.Where(b => b.HasCap && b.IsOver))
            ev.Add(new(NotifyEvents.OverCap, "OVER", $"OVER: {b.Room} {b.Stage} {b.Item}", $"Claimed {b.Claimed.ToString("#,0.##", c)} of PROJECT QTY {b.ProjectQty.ToString("#,0.##", c)}",
                $"over|{LedgerKeys.Key(b.Room, b.Stage, b.Item)}|{b.Claimed:0.##}", "Ledger", b.Room));
        foreach (var q in queue)
        {
            var kind = q.Category switch
            {
                "ACONEX" when q.Title.Contains("overdue", StringComparison.OrdinalIgnoreCase) => NotifyEvents.AconexOverdue,
                "ACONEX" when q.Title.Contains("rejected", StringComparison.OrdinalIgnoreCase) => NotifyEvents.InvoiceRejected,
                "INVOICE" when q.Title.Contains("rejected", StringComparison.OrdinalIgnoreCase) => NotifyEvents.InvoiceRejected,
                "INVOICE" or "PACKAGE" => NotifyEvents.InvoiceAwaiting,
                "MATERIAL" when q.Title.Contains("without MIR", StringComparison.OrdinalIgnoreCase) => NotifyEvents.DnWithoutMir,
                "VO" => NotifyEvents.VoAgeing,
                "HEIGHT" or "LENGTH" => NotifyEvents.ChecksPending,
                _ => "",
            };
            if (kind.Length > 0) ev.Add(new(kind, q.Tag, q.Title, q.Detail, $"{kind}|{q.Title}", q.Target.Module, q.Target.Key ?? ""));
        }
        foreach (var r in reminders.Where(r => !r.Done && r.Due <= now))
            ev.Add(new(NotifyEvents.ReminderDue, "DUE", "Reminder: " + r.Text, r.Due.ToString("dd MMM yyyy HH:mm", c), $"reminder|{r.Id}", r.TargetModule, r.TargetKey));
        return ev;
    }
}

public sealed record NotifyResult(NotificationEvent Event, string Channel, bool Sent, string Error);

/// <summary>
/// Sends events to the channels chosen by the user's rules: event kind, channel, minimum severity, quiet hours (external channels
/// and Windows notifications wait; in-app always shows). Each (event key, channel) is sent once (NotificationLog).
/// </summary>
public sealed class NotificationRouter
{
    private readonly IAssistantStore _store;
    private readonly Dictionary<string, INotificationChannel> _channels;
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public NotificationRouter(IAssistantStore store, IEnumerable<INotificationChannel> channels)
    {
        _store = store;
        _channels = channels.GroupBy(c => c.Code).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>Rules used when the user has not set any: in-app for CHECK and above, Windows for the urgent kinds and the brief.</summary>
    public static List<NotificationRule> DefaultRules(string owner) => new()
    {
        new() { Owner = owner, EventKind = NotifyEvents.All, Channel = NotifyChannels.InApp, MinSeverity = "CHECK" },
        new() { Owner = owner, EventKind = NotifyEvents.OverCap, Channel = NotifyChannels.Windows, MinSeverity = "OVER" },
        new() { Owner = owner, EventKind = NotifyEvents.AconexOverdue, Channel = NotifyChannels.Windows, MinSeverity = "DUE" },
        new() { Owner = owner, EventKind = NotifyEvents.InvoiceRejected, Channel = NotifyChannels.Windows, MinSeverity = "CHECK" },
        new() { Owner = owner, EventKind = NotifyEvents.ReminderDue, Channel = NotifyChannels.Windows, MinSeverity = "DUE" },
        new() { Owner = owner, EventKind = NotifyEvents.Brief, Channel = NotifyChannels.InApp, MinSeverity = "OK" },
    };

    public static bool InQuietHours(NotificationRule r, DateTime at)
    {
        if (!TryTime(r.QuietFrom, out var from) || !TryTime(r.QuietTo, out var to) || from == to) return false;
        var t = at.TimeOfDay;
        return from < to ? t >= from && t < to : t >= from || t < to;   // wraps midnight
    }

    private static bool TryTime(string s, out TimeSpan t) =>
        TimeSpan.TryParseExact((s ?? "").Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out t);

    public List<NotificationRule> RulesFor(string owner)
    {
        var rules = _store.Rules(owner);
        return rules.Count > 0 ? rules : DefaultRules(owner);
    }

    public async Task<List<NotifyResult>> RouteAsync(string owner, IEnumerable<NotificationEvent> events, CancellationToken ct = default)
    {
        var results = new List<NotifyResult>();
        var rules = RulesFor(owner).Where(r => r.Enabled).ToList();
        var log = _store.All<NotificationLog>().Where(l => string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase) && l.Status == "SENT")
            .Select(l => (l.EventKey, l.Channel)).ToHashSet();
        var now = Clock();
        foreach (var e in events)
            foreach (var channel in rules.Where(r => (r.EventKind == NotifyEvents.All || r.EventKind == e.Kind) && Severity.Rank(e.Severity) >= Severity.Rank(r.MinSeverity))
                         .GroupBy(r => r.Channel))
            {
                if (log.Contains((e.Key, channel.Key))) continue;
                var rule = channel.First();
                if (channel.Key != NotifyChannels.InApp && channel.All(r => InQuietHours(r, now))) continue;   // try again after the quiet hours
                if (!_channels.TryGetValue(channel.Key, out var ch) || !ch.IsConfigured) continue;
                string error = "";
                try { await ch.SendAsync(e, rule.Address, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }
                _store.Insert(new NotificationLog
                {
                    Owner = owner, EventKey = e.Key, Channel = channel.Key, SentAt = now, Status = error.Length == 0 ? "SENT" : "FAILED", Error = error,
                    Title = e.Title.Length > 200 ? e.Title[..200] : e.Title,
                }, null);
                log.Add((e.Key, channel.Key));
                results.Add(new NotifyResult(e, channel.Key, error.Length == 0, error));
            }
        return results;
    }

    /// <summary>The brief as one event (title = headline, text body, HTML for e-mail, PDF attached for e-mail).</summary>
    public static NotificationEvent BriefEvent(Brief b, bool attachPdf) => new(NotifyEvents.Brief, b.Attention > 0 ? "CHECK" : "OK",
        Loc.Get("Brief_Title", b.Language) + " " + b.Date.ToString("dd MMM", Loc.Culture(b.Language)) + ": " + b.Headline(), b.ToText(), $"brief|{b.Owner}|{b.Date:yyyy-MM-dd}", "Brief", "")
    {
        Html = b.ToHtml(),
        Attachment = attachPdf ? BriefPdf.Render(b) : null,
        AttachmentName = $"Raffaello_brief_{b.Date:yyyyMMdd}.pdf",
    };
}
