using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Raffaello.Core.Remote;
using Raffaello.Server.Auth;
using Raffaello.Server.Data;

namespace Raffaello.Server.Hubs;

/// <summary>
/// Live notifications. The server sends "changed" (ChangeNotice[]) after every committed write; clients refresh the affected
/// views and show "updated by X just now". Clients may call Heartbeat(screen) instead of the HTTP presence endpoint.
/// </summary>
[Authorize]
public sealed class ChangesHub : Hub
{
    public const string ChangedMessage = "changed";
    private readonly Npgsql.NpgsqlDataSource _ds;

    public ChangesHub(Npgsql.NpgsqlDataSource ds) => _ds = ds;

    public void Heartbeat(string screen)
    {
        var ctx = Context.GetHttpContext();
        if (ctx is null) return;
        new PgStore(_ds, ctx.Identity(), Array.Empty<IWriteGuard>()).Heartbeat(screen);
    }
}

/// <summary>Publishes committed changes to every connected client (fire and forget).</summary>
public sealed class HubChangeSink : IChangeSink
{
    private readonly IHubContext<ChangesHub> _hub;
    private readonly ILogger<HubChangeSink> _log;
    public HubChangeSink(IHubContext<ChangesHub> hub, ILogger<HubChangeSink> log) { _hub = hub; _log = log; }

    public void Publish(IReadOnlyList<ChangeNotice> notices)
    {
        var list = notices.ToArray();
        _ = _hub.Clients.All.SendAsync(ChangesHub.ChangedMessage, list).ContinueWith(
            t => _log.LogWarning(t.Exception, "Change notification failed"), TaskContinuationOptions.OnlyOnFaulted);
    }
}
