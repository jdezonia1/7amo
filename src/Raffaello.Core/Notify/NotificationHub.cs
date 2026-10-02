using Raffaello.Core.Assistant;
using Raffaello.Core.Data;
using Raffaello.Core.Localization;
using Raffaello.Core.Queue;

namespace Raffaello.Core.Notify;

/// <summary>
/// One place that builds briefs and routes notifications for the desktop app (channels from the user's settings + DPAPI vault) and
/// the server (channels from appsettings.json). Detection reads the data; routing follows the per-user rules.
/// </summary>
public sealed class NotificationHub
{
    private readonly IAssistantStore _store;
    private readonly Func<IReadOnlyList<INotificationChannel>> _channels;
    public Func<DateTime> Clock { get; init; } = () => DateTime.Now;

    public NotificationHub(IAssistantStore store, Func<IReadOnlyList<INotificationChannel>> channels)
    {
        _store = store;
        _channels = channels;
    }

    public IAssistantStore Store => _store;

    /// <summary>E-mail / Teams / WhatsApp channels from the per-user settings; secrets come from the vault.</summary>
    public static List<INotificationChannel> ExternalChannels(AssistantSettings s, ISecretVault vault, HttpClient? http = null) => new()
    {
        new EmailChannel(new SmtpOptions
        {
            Host = s.SmtpHost.Trim(), Port = s.SmtpPort <= 0 ? 587 : s.SmtpPort, Ssl = s.SmtpSsl, User = s.SmtpUser.Trim(),
            Password = vault.Get(SecretNames.SmtpPassword) ?? "", From = s.SmtpFrom.Trim(), DefaultTo = s.EmailTo.Trim(),
        }),
        new TeamsChannel(vault.Get(SecretNames.TeamsWebhook) ?? "", http),
        new WhatsAppWebhookChannel(vault.Get(SecretNames.WhatsAppWebhook) ?? "", s.WhatsAppTemplate, s.WhatsAppTo, s.WhatsAppHeaderName, vault.Get(SecretNames.WhatsAppHeaderValue) ?? "", http),
    };

    public Brief BuildBrief(string owner, ProjectSnapshot snapshot, IReadOnlyList<QueueItem> queue, string lang, int wirDueDays = 14)
    {
        var now = Clock();
        return MorningBriefBuilder.Build(new BriefInputs
        {
            Snapshot = snapshot, Queue = queue, Reminders = _store.Reminders(owner), Previous = _store.LastBrief(owner, now), Owner = owner,
            Language = lang, Now = now, WirDueDays = wirDueDays,
        });
    }

    /// <summary>Notification events since <paramref name="since"/>, routed by the owner's rules (each event once per channel).</summary>
    public Task<List<NotifyResult>> CheckAsync(string owner, ProjectSnapshot snapshot, IReadOnlyList<QueueItem> queue, DateTime since, string lang = Loc.English, CancellationToken ct = default)
    {
        var events = NotifyEventDetector.Detect(snapshot, queue, _store.Reminders(owner), since, Clock(), lang);
        return new NotificationRouter(_store, _channels()) { Clock = Clock }.RouteAsync(owner, events, ct);
    }

    /// <summary>
    /// First run for a user: records every current event as already notified (nothing is sent), so the user is told about what
    /// changes from now on instead of receiving the whole backlog. Returns how many were recorded.
    /// </summary>
    public int Baseline(string owner, ProjectSnapshot snapshot, IReadOnlyList<QueueItem> queue, string lang = Loc.English)
    {
        if (_store.All<NotificationLog>().Any(l => string.Equals(l.Owner, owner, StringComparison.OrdinalIgnoreCase))) return 0;
        var now = Clock();
        var events = NotifyEventDetector.Detect(snapshot, queue, _store.Reminders(owner), now, now, lang);
        var channels = new NotificationRouter(_store, _channels()).RulesFor(owner).Where(r => r.Enabled).Select(r => r.Channel).Distinct().ToList();
        var rows = events.SelectMany(e => channels.Select(c => new NotificationLog { Owner = owner, EventKey = e.Key, Channel = c, SentAt = now, Status = "SENT", Error = "baseline", Title = e.Title.Length > 200 ? e.Title[..200] : e.Title })).ToList();
        if (rows.Count == 0)
            rows.Add(new NotificationLog { Owner = owner, EventKey = "baseline", Channel = NotifyChannels.InApp, SentAt = now, Status = "SENT", Error = "baseline" });
        _store.Batch(b => { foreach (var r in rows) b.Insert(r); }, $"Notifications baseline for {owner}: {rows.Count} current events");
        return rows.Count;
    }

    /// <summary>True when today's brief already went out on some channel for this user.</summary>
    public bool BriefSentToday(string owner) =>
        _store.All<NotificationLog>().Any(l => l.EventKey == $"brief|{owner}|{Clock():yyyy-MM-dd}" && l.Status == "SENT");

    /// <summary>Sends the brief once per day per channel (e-mail gets HTML + PDF) and stores its figures for tomorrow's comparison.</summary>
    public async Task<List<NotifyResult>> SendBriefAsync(Brief brief, CancellationToken ct = default)
    {
        MorningBriefBuilder.Save(_store, brief);
        var router = new NotificationRouter(_store, _channels()) { Clock = Clock };
        var wantsPdf = router.RulesFor(brief.Owner).Any(r => r.Enabled && r.Channel == NotifyChannels.Email && (r.EventKind is NotifyEvents.Brief or NotifyEvents.All));
        return await router.RouteAsync(brief.Owner, new[] { NotificationRouter.BriefEvent(brief, wantsPdf) }, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a test message through one channel (Settings > SEND TEST). Returns the error, or "" when it went out.</summary>
    public async Task<string> TestAsync(string channel, string address = "", CancellationToken ct = default)
    {
        var ch = _channels().FirstOrDefault(c => c.Code == channel);
        if (ch is null || !ch.IsConfigured) return $"{channel} is not configured.";
        try
        {
            await ch.SendAsync(new NotificationEvent(NotifyEvents.All, "OK", "Raffaello test", $"Test message from Raffaello ({Clock():dd MMM yyyy HH:mm}).", "test|" + Guid.NewGuid().ToString("N")), address, ct).ConfigureAwait(false);
            return "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
    }

    /// <summary>True when the brief time has passed today and this user's brief has not been built yet.</summary>
    public static bool BriefDue(string briefTime, DateTime now, DateTime? lastSent) =>
        TimeSpan.TryParseExact((briefTime ?? "").Trim(), new[] { @"hh\:mm", @"h\:mm" }, System.Globalization.CultureInfo.InvariantCulture, out var t)
        && now.TimeOfDay >= t && (lastSent is null || lastSent.Value.Date < now.Date);
}
