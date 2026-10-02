using System.Text.Json;
using Npgsql;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Server.Auth;
using Raffaello.Server.Data;
using Raffaello.Server.Documents;
using Raffaello.Server.Hubs;

namespace Raffaello.Server.Api;

/// <summary>Creates the per-request <see cref="PgStore"/> for the authenticated caller, with every guard and the hub sink.</summary>
public sealed class StoreFactory
{
    private readonly NpgsqlDataSource _ds;
    private readonly IChangeSink _sink;
    private readonly List<IWriteGuard> _guards;

    public StoreFactory(NpgsqlDataSource ds, IChangeSink sink, ServerOptions opt, IServiceProvider sp)
    {
        _ds = ds; _sink = sink;
        _guards = PgStore.DefaultGuards(opt.RequireInternalApproval).ToList();
        foreach (var m in ServerModules.All) _guards.AddRange(m.Guards(sp));
    }

    public PgStore For(HttpContext ctx) => new(_ds, ctx.Identity(), _guards, _sink);
    public PgStore For(StoreIdentity who) => new(_ds, who, _guards, _sink);
}

public static class Endpoints
{
    /// <summary>Changes on every server start: a restored database can never be mistaken for a cached state.</summary>
    public static readonly string Epoch = Guid.NewGuid().ToString("N")[..8];

    private static readonly JsonSerializerOptions Json = RemoteJson.Options;

    private static IResult JsonResult(object value) => Results.Text(JsonSerializer.Serialize(value, value.GetType(), Json), "application/json");

    private static void Require(HttpContext ctx, string permission)
    {
        var who = ctx.Identity();
        if (!who.Can(permission)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{who.User} ({(who.Role.Length == 0 ? "no role" : who.Role)}) needs {permission}.");
    }

    public static void MapRaffaelloApi(this WebApplication app)
    {
        app.MapGet(ApiRoutes.Health, (NpgsqlDataSource ds) =>
        {
            using var c = ds.OpenConnection();
            return JsonResult(new { ok = true, server = "Raffaello.Server", version = typeof(Endpoints).Assembly.GetName().Version?.ToString() ?? "", time = DateTime.Now });
        }).AllowAnonymous();

        app.MapPost(ApiRoutes.Login, (LoginRequest req, UserStore users, Auth.LoginThrottle throttle, HttpContext ctx) =>
        {
            var user = req.UserName ?? "";
            var address = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            if (throttle.RetryAfter(user, address) is { } wait)
            {
                ctx.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new ErrorDto { Code = "too_many_attempts", Message = $"Too many wrong sign-ins. Try again in {Math.Max(1, wait / 60)} minute(s) or ask the administrator." }, Json, statusCode: 429);
            }
            var r = users.Login(user, req.Password ?? "", req.Machine ?? "");
            if (r is null)
            {
                throttle.Failed(user, address);
                return Results.Json(new ErrorDto { Code = "unauthorized", Message = "Wrong user name or password, or the account is disabled." }, Json, statusCode: 401);
            }
            throttle.Succeeded(user);
            return JsonResult(r);
        }).AllowAnonymous();

        var api = app.MapGroup("").RequireAuthorization();

        api.MapPost(ApiRoutes.Logout, (HttpContext ctx, UserStore users) =>
        {
            var t = TokenAuthHandler.ReadToken(ctx.Request);
            if (t != null) users.Revoke(t);
            return Results.NoContent();
        });

        api.MapGet(ApiRoutes.Me, (HttpContext ctx) =>
        {
            var who = ctx.Identity();
            return JsonResult(new MeDto { UserName = who.User, DisplayName = ctx.DisplayName(), Role = who.Role, AuthType = who.AuthType, Permissions = PermissionMatrix.Of(who.Role).ToList() });
        });

        api.MapGet(ApiRoutes.Info, (NpgsqlDataSource ds, ServerOptions opt) => JsonResult(new ServerInfoDto
        {
            Version = typeof(Endpoints).Assembly.GetName().Version?.ToString() ?? "",
            Database = new NpgsqlConnectionStringBuilder(ds.ConnectionString).Database ?? "",
            Tables = EntityRegistry.All.Select(EntityRegistry.TableOf).ToList(),
            WindowsAuth = opt.WindowsAuth, RequireInternalApproval = opt.RequireInternalApproval, MaxDocumentBytes = opt.MaxDocumentBytes, ServerTime = DateTime.Now,
        }));

        // ---------------------------------------------------------------- tables (generic for every registered entity type)

        api.MapGet(ApiRoutes.Tables + "/{table}", (HttpContext ctx, string table, StoreFactory f) =>
        {
            var t = EntityRegistry.Require(table);
            var store = f.For(ctx);
            var tag = $"\"{Epoch}-{store.TableVersion(EntityRegistry.TableOf(t))}\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == tag) { ctx.Response.Headers.ETag = tag; return Results.StatusCode(304); }
            var (rows, version) = store.AllWithVersion(t);
            ctx.Response.Headers.ETag = $"\"{Epoch}-{version}\"";
            return Results.Text(JsonSerializer.Serialize(rows, Json), "application/json");
        });

        api.MapGet(ApiRoutes.Tables + "/{table}/count", (HttpContext ctx, string table, StoreFactory f) => JsonResult(new { count = f.For(ctx).CountOf(EntityRegistry.Require(table)) }));

        api.MapGet(ApiRoutes.Tables + "/{table}/{id:long}", (HttpContext ctx, string table, long id, StoreFactory f) =>
        {
            var e = f.For(ctx).GetOf(EntityRegistry.Require(table), id);
            return e is null ? Results.Json(new ErrorDto { Code = ErrorCodes.NotFound, Message = $"{table} #{id} not found." }, Json, statusCode: 404) : JsonResult(e);
        });

        api.MapPost(ApiRoutes.Write, (HttpContext ctx, WriteRequestDto req, StoreFactory f) => JsonResult(f.For(ctx).Execute(req)));

        api.MapPost(ApiRoutes.ClearAll, (HttpContext ctx, StoreFactory f) => { f.For(ctx).ClearAll(); return Results.NoContent(); });

        // ---------------------------------------------------------------- audit / events / presence / meta

        api.MapGet(ApiRoutes.Audit, (HttpContext ctx, int? take, DateTime? since, StoreFactory f) => JsonResult(f.For(ctx).RecentAudit(Math.Clamp(take ?? 50, 1, 5000), since)));

        api.MapPost(ApiRoutes.Events, (HttpContext ctx, EventDto e, StoreFactory f) => { f.For(ctx).LogEvent(e.Action, e.Summary); return Results.NoContent(); });

        api.MapPost(ApiRoutes.Presence, (HttpContext ctx, PresenceDto p, StoreFactory f) => { f.For(ctx).Heartbeat(p.Screen); return Results.NoContent(); });

        api.MapGet(ApiRoutes.Presence, (HttpContext ctx, int? windowSeconds, StoreFactory f) =>
            JsonResult(f.For(ctx).OthersOnline(TimeSpan.FromSeconds(Math.Clamp(windowSeconds ?? 180, 10, 86400)))));

        api.MapGet(ApiRoutes.Meta + "/{key}", (HttpContext ctx, string key, StoreFactory f) => JsonResult(new MetaDto { Value = f.For(ctx).GetMeta(key) }));

        api.MapPut(ApiRoutes.Meta + "/{key}", (HttpContext ctx, string key, MetaDto m, StoreFactory f) => { f.For(ctx).SetMeta(key, m.Value ?? ""); return Results.NoContent(); });

        // ---------------------------------------------------------------- approvals

        api.MapGet(ApiRoutes.Approvals, (HttpContext ctx, string table, long id, StoreFactory f) => JsonResult(f.For(ctx).Stamps(table, id)));

        api.MapPost(ApiRoutes.Approvals, (HttpContext ctx, StampRequest req, StoreFactory f) => JsonResult(f.For(ctx).Stamp(req)));

        // ---------------------------------------------------------------- documents

        api.MapPost(ApiRoutes.Documents, async (HttpContext ctx, string fileName, string? category, string? linkedTable, long? linkedId, DocumentStore docs) =>
        {
            Require(ctx, Permissions.UploadDocuments);
            if (ctx.Request.ContentLength is long len && len > docs.MaxBytes)
                throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"{fileName} is larger than the limit of {docs.MaxBytes / (1024 * 1024)} MB.");
            var info = await docs.SaveAsync(ctx.Request.Body, fileName, ctx.Request.ContentType ?? "application/octet-stream", category ?? "", linkedTable ?? "", linkedId ?? 0, ctx.Identity(), ctx.RequestAborted);
            return JsonResult(info);
        });

        api.MapGet(ApiRoutes.Documents, (string? linkedTable, long? linkedId, DocumentStore docs) => JsonResult(docs.List(linkedTable, linkedId)));

        api.MapGet(ApiRoutes.Documents + "/{id:long}/info", (long id, DocumentStore docs) =>
            docs.Get(id) is { } d ? JsonResult(d) : Results.Json(new ErrorDto { Code = ErrorCodes.NotFound, Message = $"Document #{id} not found." }, Json, statusCode: 404));

        api.MapGet(ApiRoutes.Documents + "/{id:long}/verify", async (long id, DocumentStore docs, CancellationToken ct) => JsonResult(new { id, ok = await docs.VerifyAsync(id, ct) }));

        api.MapGet(ApiRoutes.Documents + "/{id:long}", (HttpContext ctx, long id, DocumentStore docs) =>
        {
            var info = docs.Get(id) ?? throw new WriteRejectedException(404, ErrorCodes.NotFound, $"Document #{id} not found.");
            var path = docs.PathOf(id);
            if (!File.Exists(path)) throw new WriteRejectedException(404, ErrorCodes.NotFound, $"The file of document #{id} is missing on the shared drive.");
            ctx.Response.Headers[ApiRoutes.ShaHeader] = info.Sha256;
            return Results.File(path, string.IsNullOrEmpty(info.ContentType) ? "application/octet-stream" : info.ContentType, info.FileName);
        });

        // ---------------------------------------------------------------- users (ADMIN)

        api.MapGet(ApiRoutes.Users, (HttpContext ctx, UserStore users) => { Require(ctx, Permissions.ManageUsers); return JsonResult(users.All().Select(u => u.ToDto()).ToList()); });

        api.MapPost(ApiRoutes.Users, (HttpContext ctx, UserDto dto, UserStore users, StoreFactory f) =>
        {
            Require(ctx, Permissions.ManageUsers);
            var u = users.Create(dto.UserName, dto.Role, string.IsNullOrEmpty(dto.Password) ? null : dto.Password, dto.DisplayName, dto.WindowsAccount ?? "");
            f.For(ctx).LogEvent("USER", $"User {u.UserName} created ({u.Role})");
            return JsonResult(u.ToDto());
        });

        api.MapPut(ApiRoutes.Users + "/{id:long}", (HttpContext ctx, long id, UserDto dto, UserStore users, StoreFactory f) =>
        {
            Require(ctx, Permissions.ManageUsers);
            var u = users.Update(id, dto);
            f.For(ctx).LogEvent("USER", $"User {u.UserName} updated ({u.Role}{(u.Active ? "" : ", disabled")}{(string.IsNullOrEmpty(dto.Password) ? "" : ", new password")})");
            return JsonResult(u.ToDto());
        });

        // ---------------------------------------------------------------- migration local SQLite -> server (ADMIN)

        api.MapPost(ApiRoutes.MigrateImport, (HttpContext ctx, MigrateImportRequest req, StoreFactory f) =>
        {
            var t = EntityRegistry.Require(req.Table);
            var rows = req.Rows.Select(r => (Entity)(r.Deserialize(t, Json) ?? throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Empty row."))).ToList();
            var (ins, skip) = f.For(ctx).ImportWithIds(t, rows, string.IsNullOrWhiteSpace(req.Source) ? "local data file" : req.Source);
            return JsonResult(new MigrateImportResponse { Table = EntityRegistry.TableOf(t), Inserted = ins, Skipped = skip });
        });

        api.MapPost(ApiRoutes.MigrateAudit, (HttpContext ctx, MigrateAuditRequest req, StoreFactory f) => JsonResult(new { added = f.For(ctx).ImportAudit(req.SourceKey, req.Rows) }));

        app.MapHub<ChangesHub>(ApiRoutes.ChangesHub).RequireAuthorization();

        foreach (var m in ServerModules.All) m.MapEndpoints(api);
    }
}
