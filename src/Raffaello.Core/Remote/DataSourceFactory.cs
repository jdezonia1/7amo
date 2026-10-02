using Raffaello.Core.Data;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Remote;

/// <summary>
/// Store factory for <see cref="ProjectService"/>: Local = SQLite data file (AppSettings.DataFilePath), Server = the
/// Raffaello server from %APPDATA%\Raffaello\server.json. Pass <see cref="Create"/> as the ProjectService store factory.
/// </summary>
public static class DataSourceFactory
{
    private static RemoteProjectStore? _current;

    /// <summary>The live remote store, when the app runs against the server.</summary>
    public static RemoteProjectStore? Current => _current;

    /// <summary>Raised when a new remote store replaces the previous one (Settings changed the data source).</summary>
    public static event Action<RemoteProjectStore?>? CurrentChanged;

    public static string? SettingsPath { get; set; }

    public static IProjectStore Create(AppSettings s)
    {
        var remote = RemoteSettings.Load(SettingsPath);
        var old = _current;
        if (!remote.IsServer)
        {
            _current = null;
            old?.Dispose();
            if (old != null) CurrentChanged?.Invoke(null);
            return new Db(s.DataFilePath, s.EffectiveUserName);
        }
        // the server is shared: never seed demo data into it
        s.SeedDemoData = false;
        ModuleEntities.RegisterAll();   // [phase6] module tables in the offline cache / sync
        var store = new RemoteProjectStore(remote, s.EffectiveUserName);
        _current = store;
        old?.Dispose();
        CurrentChanged?.Invoke(store);
        return store;
    }

    /// <summary>Settings "test connection": health, sign-in and who-am-I, without touching the current store.</summary>
    public static (bool Ok, string Message) Test(RemoteSettings settings)
    {
        try
        {
            using var api = new RemoteApi(settings.ServerUrl, settings.Token, settings.UseWindowsAuth && string.IsNullOrWhiteSpace(settings.Token),
                Environment.MachineName, "test", TimeSpan.FromSeconds(10));
            if (!api.Health()) return (false, $"No answer from {api.BaseUri}. Check the address, that the Raffaello service runs on the server PC and that the firewall port is open.");
            var me = api.Me();
            var info = api.Info();
            return (true, $"Connected to {info.Server} {info.Version} (database {info.Database}) as {me.DisplayName} - role {me.Role} ({me.AuthType}).");
        }
        catch (RemoteAuthException) { return (false, "The server answers but you are not signed in: use Windows sign-in on a domain PC, or sign in with your app account."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Signs in with an app account and stores the session token (never the password) in the settings.</summary>
    public static LoginResponse SignIn(RemoteSettings settings, string user, string password)
    {
        using var api = new RemoteApi(settings.ServerUrl, null, false, Environment.MachineName, "login", TimeSpan.FromSeconds(15));
        var r = api.Login(user, password);
        settings.Token = r.Token; settings.TokenUser = r.UserName; settings.TokenExpires = r.ExpiresAt; settings.UseWindowsAuth = false;
        return r;
    }
}
