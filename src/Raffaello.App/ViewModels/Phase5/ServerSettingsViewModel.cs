using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raffaello.App.Services;
using Raffaello.App.Services.Phase5;
using Raffaello.Core.Data;
using Raffaello.Core.Remote;

namespace Raffaello.App.ViewModels.Phase5;

/// <summary>Settings card "DATA SOURCE": Local (this PC / shared file) or Server (URL), sign-in, test, migration, conflicts, approvals.</summary>
public sealed partial class ServerSettingsViewModel : ObservableObject
{
    private readonly PageContext _ctx;

    public ServerSettingsViewModel(PageContext ctx)
    {
        _ctx = ctx;
        var s = RemoteSettings.Load();
        _isServer = s.IsServer || string.Equals(s.Mode, DataSources.Server, StringComparison.OrdinalIgnoreCase);
        _serverUrl = s.ServerUrl;
        _useWindowsAuth = s.UseWindowsAuth;
        _signInUser = s.TokenUser;
        _status = s.IsServer ? $"Using the server {s.ServerUrl}" : "Using the local data file";
    }

    public SyncStatus Sync => SyncStatus.Current;

    [ObservableProperty] private bool _isServer;
    [ObservableProperty] private string _serverUrl = "";
    [ObservableProperty] private bool _useWindowsAuth = true;
    [ObservableProperty] private string _signInUser = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _migrationReport = "";
    [ObservableProperty] private bool _busy;

    public bool IsLocal { get => !IsServer; set => IsServer = !value; }
    partial void OnIsServerChanged(bool value) => OnPropertyChanged(nameof(IsLocal));

    private RemoteSettings Current()
    {
        var s = RemoteSettings.Load();
        s.Mode = IsServer ? DataSources.Server : DataSources.Local;
        s.ServerUrl = ServerUrl.Trim();
        if (UseWindowsAuth && !s.UseWindowsAuth) { s.Token = ""; s.TokenUser = ""; }
        s.UseWindowsAuth = UseWindowsAuth;
        return s;
    }

    [RelayCommand]
    private async Task Test()
    {
        Busy = true;
        try
        {
            var s = Current();
            var (ok, msg) = await Task.Run(() => DataSourceFactory.Test(s));
            Status = msg;
            _ctx.Toasts.Show(ok ? "SERVER OK" : "SERVER NOT REACHED", msg, ok ? ToastKind.Good : ToastKind.Warn, 8);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SignIn(object? passwordBox)
    {
        var pwd = (passwordBox as PasswordBox)?.Password ?? "";
        if (SignInUser.Trim().Length == 0 || pwd.Length == 0) { Status = "Type your server user name and password."; return; }
        Busy = true;
        try
        {
            var s = Current();
            var r = await Task.Run(() => DataSourceFactory.SignIn(s, SignInUser.Trim(), pwd));
            s.Save();
            UseWindowsAuth = false;
            if (passwordBox is PasswordBox pb) pb.Clear();
            Status = $"Signed in as {r.DisplayName} ({r.Role}) until {r.ExpiresAt:dd-MMM-yyyy}. Press USE THIS DATA SOURCE to switch.";
        }
        catch (Exception ex) { Status = "Sign-in failed: " + ex.Message; }
        finally { Busy = false; }
    }

    /// <summary>Saves the choice and reopens the project on the chosen source.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (IsServer && !Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out _)) { Status = "Type the server address, e.g. http://RAFFAELLO-SRV:5180"; return; }
        Busy = true;
        try
        {
            Current().Save();
            await Task.Run(() => _ctx.Project.Initialize());
            _ctx.Data.RaiseChanged();
            SyncStatus.Current.Update(DataSourceFactory.Current);
            Status = IsServer ? $"Connected to {ServerUrl}" : "Using the local data file";
            _ctx.Toasts.Show("DATA SOURCE CHANGED", _ctx.Project.DataLocation, ToastKind.Good);
        }
        catch (Exception ex)
        {
            Status = "Could not open the data source: " + StoreErrors.Describe(ex);
            _ctx.Toasts.Show("DATA SOURCE NOT OPENED", Status, ToastKind.Error, 10);
        }
        finally { Busy = false; }
    }

    private RemoteProjectStore? Remote()
    {
        var r = DataSourceFactory.Current;
        if (r is null) Status = "Switch the data source to Server first (USE THIS DATA SOURCE).";
        return r;
    }

    [RelayCommand]
    private Task DryRun() => Migrate(dryRun: true);

    [RelayCommand]
    private async Task RunMigration()
    {
        if (!_ctx.Dialogs.Confirm("Copy local data to the server",
                "Copy every row of the local data file to the server?\n\nIds are kept, rows already on the server are skipped, so running it twice is safe. Only an ADMIN may do this.")) return;
        await Migrate(dryRun: false);
    }

    private async Task Migrate(bool dryRun)
    {
        var remote = Remote();
        if (remote is null) return;
        var localPath = _ctx.Project.Settings.DataFilePath;
        Busy = true;
        try
        {
            var report = await Task.Run(() =>
            {
                var local = new Db(localPath, _ctx.Project.Settings.EffectiveUserName);
                return dryRun ? LocalToServerMigrator.Plan(local, remote) : LocalToServerMigrator.Run(local, remote, progress: m => _ctx.Toasts.Show("MIGRATING", m, ToastKind.Info, 2));
            });
            MigrationReport = report.ToText();
            if (!dryRun) await _ctx.Data.ReloadAsync();
            _ctx.Toasts.Show(dryRun ? "DRY RUN READY" : "MIGRATION DONE", dryRun ? $"{report.ToCopy} rows would be copied" : $"{report.Copied} rows copied", report.Blocked ? ToastKind.Warn : ToastKind.Good);
        }
        catch (Exception ex) { MigrationReport = "Migration failed: " + ex.Message; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void Conflicts()
    {
        var remote = Remote();
        if (remote is null) return;
        new Views.Phase5.SyncConflictsWindow(remote, _ctx) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        SyncStatus.Current.Update(remote);
    }

    [RelayCommand]
    private void Approvals()
    {
        var remote = Remote();
        if (remote is null) return;
        new Views.Phase5.ApprovalsWindow(remote, _ctx) { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private async Task Retry()
    {
        var remote = Remote();
        if (remote is null) return;
        var ok = await Task.Run(remote.CheckNow);
        SyncStatus.Current.Update(remote);
        Status = ok ? "Online." : "Still offline: " + remote.LastError;
        if (ok) await _ctx.Data.ReloadAsync();
    }
}
