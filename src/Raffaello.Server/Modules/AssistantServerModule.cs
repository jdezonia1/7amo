using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raffaello.Core.Assistant;
using Raffaello.Core.Notify;
using Raffaello.Core.Remote;
using Raffaello.Server.Api;
using Raffaello.Server.Auth;
using Raffaello.Server.Data;

namespace Raffaello.Server.Modules;

/// <summary>Server notification settings (appsettings.json, section "Raffaello:Notify"). Secrets can come from environment variables.</summary>
public sealed class NotifyOptions
{
    /// <summary>Send scheduled briefs and notifications from the server (e-mail / Teams / WhatsApp). Off until IT configures a channel.</summary>
    public bool Enabled { get; set; }
    public string BriefAt { get; set; } = "07:15";
    public string Language { get; set; } = "en";
    public int CheckEveryMinutes { get; set; } = 15;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    /// <summary>Or environment variable Raffaello__Notify__SmtpPassword.</summary>
    public string SmtpPassword { get; set; } = "";
    public string SmtpFrom { get; set; } = "";
    public string TeamsWebhookUrl { get; set; } = "";
    public string WhatsAppWebhookUrl { get; set; } = "";
    public string WhatsAppTemplate { get; set; } = "";
    public string WhatsAppHeaderName { get; set; } = "";
    public string WhatsAppHeaderValue { get; set; } = "";

    public List<INotificationChannel> Channels(HttpClient? http = null) => new()
    {
        new EmailChannel(new SmtpOptions { Host = SmtpHost, Port = SmtpPort, Ssl = SmtpSsl, User = SmtpUser, Password = SmtpPassword, From = SmtpFrom, DefaultTo = "-" }),
        new TeamsChannel(TeamsWebhookUrl, http),
        new WhatsAppWebhookChannel(WhatsAppWebhookUrl, WhatsAppTemplate, "", WhatsAppHeaderName, WhatsAppHeaderValue, http),
    };
}

/// <summary>
/// [assistant] Assistant tables (conversations per user, proposed actions, reminders, notification rules / log, brief snapshots) and the
/// brief endpoints. Scheduled briefs and notifications are sent by <see cref="AssistantNotifyService"/>.
/// </summary>
public sealed class AssistantServerModule : IServerModule
{
    public string Name => "Assistant";
    public IEnumerable<Type> EntityTypes => AssistantEntityTypes.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new AssistantOwnerGuard() };

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // the brief of the signed-in user, as text (the desktop shows its own; this is for scripts / checks)
        api.MapGet("/api/v1/assistant/brief", (HttpContext ctx, StoreFactory f, string? lang) =>
        {
            var store = f.For(ctx);
            var (project, queue) = StoreBriefData.Load(store, DateTime.Today);
            var hub = new NotificationHub(new ProjectStoreAssistantStore(store), () => Array.Empty<INotificationChannel>())
            {
                Obligations = () => Raffaello.Core.Wiring.ContractObligations.Build(store, project.Snapshot),
            };
            var brief = hub.BuildBrief(ctx.Identity().User, project.Snapshot, queue, lang ?? "en");
            return Results.Text(brief.ToText(), "text/plain; charset=utf-8");
        });

        // ADMIN: run the scheduled briefs now (test after configuring a channel)
        api.MapPost("/api/v1/assistant/brief/run", async (HttpContext ctx, AssistantNotifyService svc) =>
        {
            var who = ctx.Identity();
            if (!who.Can(Permissions.ManageSettings)) return Results.StatusCode(403);
            var n = await svc.RunBriefsAsync(force: true, ctx.RequestAborted);
            return Results.Text($"{{\"sent\":{n.ToString(CultureInfo.InvariantCulture)}}}", "application/json");
        });
    }
}

/// <summary>
/// [assistant] Server mode: sends each user's morning brief at <see cref="NotifyOptions.BriefAt"/> and checks for new events every
/// <see cref="NotifyOptions.CheckEveryMinutes"/>, through the external channels in the users' notification rules (rule Address = the
/// user's e-mail / phone). In-app and Windows notifications stay on the desktops.
/// </summary>
public sealed class AssistantNotifyService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly StoreFactory _stores;
    private readonly NotifyOptions _opt;
    private readonly Microsoft.Extensions.Logging.ILogger<AssistantNotifyService> _log;
    private DateTime _lastCheck = DateTime.Now;

    public AssistantNotifyService(StoreFactory stores, Microsoft.Extensions.Configuration.IConfiguration config, Microsoft.Extensions.Logging.ILogger<AssistantNotifyService> log)
    {
        _stores = stores;
        _opt = config.GetSection("Raffaello:Notify").Get<NotifyOptions>() ?? new NotifyOptions();
        _log = log;
    }

    private (NotificationHub Hub, PgStore Store) Hub()
    {
        var store = _stores.For(StoreIdentity.System("NOTIFY"));
        var channels = _opt.Channels();
        return (new NotificationHub(new ProjectStoreAssistantStore(store), () => channels), store);
    }

    /// <summary>Users with at least one enabled rule on an external channel.</summary>
    private static List<string> Owners(PgStore store) =>
        store.All<NotificationRule>().Where(r => r.Enabled && NotifyChannels.IsExternal(r.Channel)).Select(r => r.Owner)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<int> RunBriefsAsync(bool force, CancellationToken ct)
    {
        var (hub, store) = Hub();
        var briefs = store.All<BriefSnapshot>();
        var due = Owners(store).Where(owner => force || NotificationHub.BriefDue(_opt.BriefAt, DateTime.Now,
            briefs.Where(b => string.Equals(b.Owner, owner, StringComparison.OrdinalIgnoreCase)).Select(b => (DateTime?)b.BuiltAt).Max())).ToList();
        if (due.Count == 0) return 0;
        var (project, queue) = StoreBriefData.Load(store, DateTime.Today);
        var sent = 0;
        foreach (var owner in due)
        {
            var brief = hub.BuildBrief(owner, project.Snapshot, queue, _opt.Language);
            sent += (await hub.SendBriefAsync(brief, ct)).Count(r => r.Sent);
        }
        return sent;
    }

    private async Task CheckEventsAsync(CancellationToken ct)
    {
        var (hub, store) = Hub();
        var owners = Owners(store);
        var since = _lastCheck;
        _lastCheck = DateTime.Now;
        if (owners.Count == 0) return;
        var (project, queue) = StoreBriefData.Load(store, DateTime.Today);
        foreach (var owner in owners) await hub.CheckAsync(owner, project.Snapshot, queue, since, _opt.Language, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled) return;
        var nextCheck = DateTime.Now.AddMinutes(Math.Max(5, _opt.CheckEveryMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (TaskCanceledException) { return; }
            try
            {
                await RunBriefsAsync(force: false, stoppingToken);
                if (DateTime.Now >= nextCheck)
                {
                    await CheckEventsAsync(stoppingToken);
                    nextCheck = DateTime.Now.AddMinutes(Math.Max(5, _opt.CheckEveryMinutes));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Scheduled brief / notifications failed");
            }
        }
    }
}

/// <summary>[assistant] A user writes only his own conversations, messages, cards, reminders and notification rules (administrators: all).</summary>
public sealed class AssistantOwnerGuard : IWriteGuard
{
    public void Check(WriteCheck c)
    {
        if (c.Who.Trusted || c.Who.Can(Permissions.ManageSettings)) return;
        string? owner = c.Entity switch
        {
            AssistantConversation x => x.Owner,
            AssistantReminder x => x.Owner,
            NotificationRule x => x.Owner,
            NotificationLog x => x.Owner,
            BriefSnapshot x => x.Owner,
            AssistantMessage x => ConversationOwner(c, x.ConversationId),
            AssistantAction x => ConversationOwner(c, x.ConversationId),
            _ => null,
        };
        if (owner != null && !string.Equals(owner, c.Who.User, StringComparison.OrdinalIgnoreCase))
            throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} may not change {c.Table} of {owner}.");
    }

    private static string? ConversationOwner(WriteCheck c, long conversationId) =>
        c.Tx.Query<AssistantConversation>($"""SELECT * FROM {PgMap.Q("AssistantConversations")} WHERE "Id" = @id""", ("id", conversationId)).FirstOrDefault()?.Owner;
}
