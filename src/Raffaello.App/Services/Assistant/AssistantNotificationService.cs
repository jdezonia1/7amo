using System.Windows.Threading;
using Raffaello.Core.Assistant;
using Raffaello.Core.Localization;
using Raffaello.Core.Notify;

namespace Raffaello.App.Services.Assistant;

/// <summary>
/// Desktop notifications: every few minutes the current events (new claims, over-cap, rejected invoices, Aconex overdue, DNs without
/// MIR, VO ageing, reminders due) are routed by the user's rules to in-app toasts, Windows notifications and - with a local data file -
/// e-mail / Teams / WhatsApp. In server mode those external channels are left to the server (it sends the scheduled briefs).
/// The morning brief goes out once a day after the brief time.
/// </summary>
public sealed class AssistantNotificationService
{
    private readonly AssistantHost _host;
    private readonly DataService _data;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _timer = new();
    private readonly List<NotificationEvent> _inApp = new();
    private DateTime _since;
    private bool _busy;

    public AssistantNotificationService(AssistantHost host, DataService data, ToastService toasts)
    {
        _host = host; _data = data; _toasts = toasts;
        _since = data.Project.Settings.LastSeenAt ?? DateTime.Now.AddDays(-1);
        Hub = new NotificationHub(host.Store, Channels);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public NotificationHub Hub { get; }
    public event Action<NotificationEvent>? Notified;

    public IReadOnlyList<INotificationChannel> Channels()
    {
        var s = _host.Settings;
        var list = new List<INotificationChannel>
        {
            new DelegateChannel(NotifyChannels.InApp, e => { lock (_inApp) _inApp.Add(e); return Task.CompletedTask; }, s.InAppToasts),
            new DelegateChannel(NotifyChannels.Windows, e => Task.FromResult(WindowsToasts.Show(e.Title, e.Body)), s.WindowsToasts && OperatingSystem.IsWindows()),
        };
        if (!_host.ServerMode) list.AddRange(NotificationHub.ExternalChannels(s, _host.Vault));
        return list;
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(_host.Settings.NotifyEveryMinutes, 2, 240));
        _timer.Start();
        _ = TickAsync(first: true);
    }

    private string Owner => _host.Data.User;

    public async Task TickAsync(bool first = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var p = _data.Project;
            var lang = _host.Settings.Language;
            if (first) await Task.Run(() => Hub.Baseline(Owner, p.Snapshot, p.Queue, lang));
            var since = _since;
            _since = DateTime.Now;
            await Hub.CheckAsync(Owner, p.Snapshot, p.Queue, since, lang);
            if (NotificationHub.BriefDue(_host.Settings.BriefTime, DateTime.Now, null) && !Hub.BriefSentToday(Owner))
            {
                var brief = Hub.BuildBrief(Owner, p.Snapshot, p.Queue, lang, p.Settings.WirDueDays);
                await Hub.SendBriefAsync(brief);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _busy = false;
            FlushInApp();
        }
    }

    /// <summary>In-app: up to three toasts, then one "and N more".</summary>
    private void FlushInApp()
    {
        List<NotificationEvent> batch;
        lock (_inApp) { batch = _inApp.ToList(); _inApp.Clear(); }
        foreach (var e in batch.OrderByDescending(e => Severity.Rank(e.Severity)).Take(3))
        {
            _toasts.Show(e.Title, e.Body, e.Severity switch { "OVER" => ToastKind.Error, "CHECK" or "DUE" => ToastKind.Warn, _ => ToastKind.Info }, 8);
            Notified?.Invoke(e);
        }
        if (batch.Count > 3)
            _toasts.Show(Loc.T("Queue_Title"), $"+{batch.Count - 3}", ToastKind.Info, 8);
    }
}
