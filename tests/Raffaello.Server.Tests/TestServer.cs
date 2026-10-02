using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Raffaello.Core.Remote;
using Raffaello.Server;
using Raffaello.Server.Auth;

namespace Raffaello.Server.Tests;

/// <summary>
/// PostgreSQL for the integration tests: RAFFAELLO_TEST_PG (a connection string without Database) or the local default
/// Host=127.0.0.1;Username=raffaello;Password=raffaello_test. When no server answers, every [PgFact] is skipped with the reason.
/// </summary>
public static class TestPg
{
    public static readonly string Base = Environment.GetEnvironmentVariable("RAFFAELLO_TEST_PG")
                                         ?? "Host=127.0.0.1;Port=5432;Username=raffaello;Password=raffaello_test;Timeout=5";

    private static readonly Lazy<string?> Problem = new(() =>
    {
        try
        {
            using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Base) { Database = "postgres", Pooling = false }.ConnectionString);
            c.Open();
            using var cmd = new NpgsqlCommand("SELECT rolcreatedb FROM pg_roles WHERE rolname = current_user", c);
            return cmd.ExecuteScalar() is true ? null : "the test role cannot CREATE DATABASE";
        }
        catch (Exception ex) { return ex.Message; }
    });

    public static string? SkipReason => Problem.Value is null ? null
        : $"PostgreSQL not available for integration tests ({Problem.Value}). Install PostgreSQL 16, create role 'raffaello' (password 'raffaello_test', CREATEDB) or set RAFFAELLO_TEST_PG.";

    public static string CreateDatabase()
    {
        var name = "raffaello_test_" + Guid.NewGuid().ToString("N")[..10];
        using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Base) { Database = "postgres", Pooling = false }.ConnectionString);
        c.Open();
        using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c);
        cmd.ExecuteNonQuery();
        return new NpgsqlConnectionStringBuilder(Base) { Database = name }.ConnectionString;
    }

    public static void DropDatabase(string cs)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            var name = new NpgsqlConnectionStringBuilder(cs).Database;
            using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Base) { Database = "postgres", Pooling = false }.ConnectionString);
            c.Open();
            using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", c);
            cmd.ExecuteNonQuery();
        }
        catch { /* best effort clean-up */ }
    }
}

/// <summary>A fact that needs PostgreSQL; skipped (with the reason) when it is not available.</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute() { if (TestPg.SkipReason is { } r) Skip = r; }
}

/// <summary>A real Raffaello.Server on a free localhost port with its own fresh database, documents and backup folders.</summary>
public sealed class TestServer : IAsyncDisposable
{
    public WebApplication App { get; }
    public string ConnectionString { get; }
    public string Url { get; }
    public string Folder { get; }
    private readonly Dictionary<string, string> _tokens = new();

    private TestServer(WebApplication app, string cs, string url, string folder) { App = app; ConnectionString = cs; Url = url; Folder = folder; }

    public static async Task<TestServer> StartAsync(bool requireInternalApproval = true, long maxDocumentBytes = 5 * 1024 * 1024)
    {
        var cs = TestPg.CreateDatabase();
        var folder = Path.Combine(Path.GetTempPath(), "raffaello-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var app = ServerApp.Build(new[]
        {
            $"--ConnectionStrings:Raffaello={cs}",
            "--Raffaello:Urls=http://127.0.0.1:0",
            "--Raffaello:WindowsAuth=false",
            "--Raffaello:NightlyBackup=false",
            "--Raffaello:BcryptWorkFactor=4",
            $"--Raffaello:RequireInternalApproval={requireInternalApproval}",
            $"--Raffaello:MaxDocumentBytes={maxDocumentBytes}",
            $"--Raffaello:DocumentsRoot={Path.Combine(folder, "docs")}",
            $"--Raffaello:BackupFolder={Path.Combine(folder, "backups")}",
            "--Logging:LogLevel:Default=Warning",
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServer(app, cs, url, folder);
    }

    public UserStore Users => App.Services.GetRequiredService<UserStore>();

    /// <summary>Creates (once) a user with the role and returns a session token.</summary>
    public string Token(string user, string role)
    {
        if (_tokens.TryGetValue(user, out var t)) return t;
        if (Users.Find(user) is null) Users.Create(user, role, "password-" + user);
        t = Users.Login(user, "password-" + user, "TEST-PC")!.Token;
        _tokens[user] = t;
        return t;
    }

    /// <summary>A client store signed in as <paramref name="user"/>, with its own offline cache folder.</summary>
    public RemoteProjectStore Client(string user, string role = Roles.Qs, HttpMessageHandler? handler = null, bool connect = true)
    {
        var settings = new RemoteSettings
        {
            Mode = DataSources.Server, ServerUrl = Url, UseWindowsAuth = false, Token = Token(user, role),
            CacheFolder = Path.Combine(Folder, "cache-" + user + "-" + Guid.NewGuid().ToString("N")[..6]), TimeoutSeconds = 20,
        };
        var s = new RemoteProjectStore(settings, user, "PC-" + user, handler) { ReconnectInterval = TimeSpan.FromHours(1) };
        if (connect) s.EnsureSchema();
        return s;
    }

    public HttpClient Http(string user, string role = Roles.Qs)
    {
        var h = new HttpClient { BaseAddress = new Uri(Url) };
        h.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(user, role));
        h.DefaultRequestHeaders.Add(ApiRoutes.MachineHeader, "PC-" + user);
        return h;
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(); } catch { /* shutting down */ }
        await App.DisposeAsync();
        TestPg.DropDatabase(ConnectionString);
        try { Directory.Delete(Folder, true); } catch { /* temp clean-up */ }
    }
}

/// <summary>An HTTP handler that can simulate the network going down (every request fails like an unplugged cable).</summary>
public sealed class FlakyHandler : DelegatingHandler
{
    public volatile bool Down;
    public FlakyHandler() : base(new HttpClientHandler()) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Down ? throw new HttpRequestException("simulated network failure", null, HttpStatusCode.ServiceUnavailable) : base.SendAsync(request, ct);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
        Down ? throw new HttpRequestException("simulated network failure", null, HttpStatusCode.ServiceUnavailable) : base.Send(request, ct);
}
