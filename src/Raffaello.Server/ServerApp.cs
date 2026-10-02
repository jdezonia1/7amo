using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using Raffaello.Server.Api;
using Raffaello.Server.Auth;
using Raffaello.Server.Backup;
using Raffaello.Server.Data;
using Raffaello.Server.Documents;
using Raffaello.Server.Hubs;

namespace Raffaello.Server;

/// <summary>Builds the web host (also used by the integration tests with command-line style overrides).</summary>
public static class ServerApp
{
    public const string ServiceName = "RaffaelloServer";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        // site settings written by SETUP_SERVER.bat (kept out of appsettings.json so updates never overwrite them); command line wins
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"), optional: true, reloadOnChange: false);
        builder.Configuration.AddCommandLine(args);

        var opt = builder.Configuration.GetSection("Raffaello").Get<ServerOptions>() ?? new ServerOptions();
        var cs = ConnectionString(builder.Configuration);
        builder.WebHost.UseUrls(opt.Urls);
        var contentRoot = builder.Environment.ContentRootPath;

        builder.Services.AddSingleton(opt);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(cs));
        builder.Services.AddSingleton(sp => new UserStore(sp.GetRequiredService<NpgsqlDataSource>()) { BcryptWorkFactor = Math.Clamp(opt.BcryptWorkFactor, 4, 16) });
        builder.Services.AddSingleton(sp => new DocumentStore(sp.GetRequiredService<NpgsqlDataSource>(), opt.ResolveDocumentsRoot(contentRoot), opt.MaxDocumentBytes));
        builder.Services.AddSingleton(_ => new BackupRunner(cs, opt, contentRoot));
        builder.Services.AddSingleton(_ => new Auth.LoginThrottle(opt));   // [phase6] sign-in rate limit
        builder.Services.AddHostedService<NightlyBackupService>();
        // [assistant] begin: scheduled morning briefs / notifications (section Raffaello:Notify, off by default)
        builder.Services.AddSingleton<Modules.AssistantNotifyService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Modules.AssistantNotifyService>());
        // [assistant] end
        builder.Services.AddSignalR().AddJsonProtocol(o =>
        {
            o.PayloadSerializerOptions.PropertyNamingPolicy = null;
            o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddSingleton<IChangeSink, HubChangeSink>();
        builder.Services.AddSingleton<StoreFactory>();
        builder.Services.AddRaffaelloAuth(opt);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = null;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals;
        });
        builder.Services.Configure<KestrelServerOptions>(k => k.Limits.MaxRequestBodySize = Math.Max(opt.MaxDocumentBytes, 512L * 1024 * 1024) + 1024 * 1024);

        var app = builder.Build();
        PgSchema.Migrate(app.Services.GetRequiredService<NpgsqlDataSource>());

        app.UseMiddleware<ErrorMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRaffaelloApi();
        return app;
    }

    public static string ConnectionString(IConfiguration config) =>
        config.GetConnectionString("Raffaello") is { Length: > 0 } cs
            ? cs
            : throw new InvalidOperationException("ConnectionStrings:Raffaello is not set (appsettings.json next to Raffaello.Server.exe).");

    /// <summary>Configuration for the command-line tools: appsettings.json next to the exe, environment, then --Key=Value arguments.</summary>
    public static IConfiguration LoadConfig(string[] args) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=')).ToArray())
            .Build();

    /// <summary>Creates the database named in the connection string when it does not exist yet (connects to "postgres").</summary>
    public static bool EnsureDatabase(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var name = b.Database ?? throw new InvalidOperationException("The connection string has no Database.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        using var c = new NpgsqlConnection(admin.ConnectionString);
        c.Open();
        using (var q = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", c))
        {
            q.Parameters.AddWithValue("n", name);
            if (q.ExecuteScalar() != null) return false;
        }
        using var create = new NpgsqlCommand($"CREATE DATABASE {PgMap.Q(name)} ENCODING 'UTF8'", c);
        create.ExecuteNonQuery();
        return true;
    }

    public static string[] ListenUrls(ServerOptions opt)
    {
        var port = Uri.TryCreate(opt.Urls.Split(';')[0].Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u) ? u.Port : 5180;
        var hosts = new List<string> { Environment.MachineName };
        try
        {
            hosts.AddRange(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString()));
        }
        catch { /* network info is a convenience */ }
        return hosts.Distinct().Select(h => $"http://{h}:{port}").ToArray();
    }
}
