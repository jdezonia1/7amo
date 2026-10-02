using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Raffaello.Core.Remote;

namespace Raffaello.App.Services.Phase5;

/// <summary>What the status line shows about the data source (bound from MainWindow through <see cref="Current"/>).</summary>
public sealed partial class SyncStatus : ObservableObject
{
    public static SyncStatus Current { get; } = new();

    [ObservableProperty] private string _text = "LOCAL DATA FILE";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isServer;
    [ObservableProperty] private bool _isOnline = true;
    [ObservableProperty] private int _pending;
    [ObservableProperty] private int _conflicts;

    public void Update(RemoteProjectStore? store)
    {
        if (store is null)
        {
            IsServer = false; IsOnline = true; Pending = 0; Conflicts = 0;
            Text = "LOCAL DATA FILE"; Detail = "";
            return;
        }
        IsServer = true;
        IsOnline = store.IsOnline;
        Pending = store.PendingWrites;
        Conflicts = store.Conflicts.Count;
        var who = store.Me is { } me ? $"{me.UserName} ({me.Role})" : store.User;
        Text = !IsOnline ? $"OFFLINE - {Pending} CHANGE(S) WAITING"
             : Conflicts > 0 ? $"SERVER - {Conflicts} SYNC CONFLICT(S)"
             : store.HubConnected ? "SERVER - LIVE" : "SERVER - ONLINE";
        Detail = $"{store.Location} as {who}" + (store.LastError is { Length: > 0 } e && !IsOnline ? $" - {e}" : "");
    }
}

/// <summary>
/// Glue between the remote store and the UI: other people's changes -> toast "updated by X just now - review" + debounced
/// reload; reconnect -> replay done, reload; sync conflicts -> warning toast. Follows the store when Settings switches source.
/// </summary>
public sealed class RemoteSyncService
{
    private readonly DataService _data;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _debounce;
    private RemoteProjectStore? _store;
    private int _lastConflicts;

    public RemoteSyncService(DataService data, ToastService toasts)
    {
        _data = data; _toasts = toasts;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await _data.ReloadAsync(); };
    }

    /// <summary>Starts following <paramref name="store"/> (null when the app opened the local file) and later data-source switches.</summary>
    public void Start(RemoteProjectStore? store)
    {
        DataSourceFactory.CurrentChanged += s => OnUi(() => Attach(s));
        Attach(store);
    }

    private void Attach(RemoteProjectStore? store)
    {
        if (_store != null)
        {
            _store.RemoteChanged -= OnRemoteChanged;
            _store.StatusChanged -= OnStatus;
            _store.Reconnected -= OnReconnected;
        }
        _store = store;
        if (store != null)
        {
            store.RemoteChanged += OnRemoteChanged;
            store.StatusChanged += OnStatus;
            store.Reconnected += OnReconnected;
            _lastConflicts = store.Conflicts.Count;
        }
        SyncStatus.Current.Update(store);
    }

    private void OnRemoteChanged(IReadOnlyList<ChangeNotice> notices) => OnUi(() =>
    {
        var first = notices[0];
        var by = string.Join(", ", notices.Select(n => n.By).Distinct());
        var what = notices.Count == 1 ? (first.Summary.Length > 0 ? first.Summary : $"{first.Action} {first.Table} #{first.Id}")
                                      : $"{notices.Sum(n => n.Count)} changes in {string.Join(", ", notices.Select(n => n.Table).Distinct())}";
        _toasts.Show($"Updated by {by} just now - review", what, ToastKind.Info, 7);
        _debounce.Stop();
        _debounce.Start();
    });

    private void OnStatus() => OnUi(() =>
    {
        SyncStatus.Current.Update(_store);
        var n = _store?.Conflicts.Count ?? 0;
        if (n > _lastConflicts) _toasts.Show("Sync conflicts", $"{n - _lastConflicts} offline change(s) need a decision: Settings > Data source > Sync conflicts.", ToastKind.Warn, 10);
        _lastConflicts = n;
    });

    private void OnReconnected() => OnUi(async () =>
    {
        SyncStatus.Current.Update(_store);
        _toasts.Show("Back online", "Changes made offline were sent to the server.", ToastKind.Good);
        await _data.ReloadAsync();
    });

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
