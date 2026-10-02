using System.Text.Json;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Api;

/// <summary>Turns store / guard exceptions into stable HTTP answers with an <see cref="ErrorDto"/> body.</summary>
public sealed class ErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorMiddleware> _log;
    public ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log) { _next = next; _log = log; }

    public async Task Invoke(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            var (status, dto) = Map(ex);
            if (status >= 500) _log.LogError(ex, "Request {Path} failed", ctx.Request.Path);
            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(dto, RemoteJson.Options));
        }
    }

    public static (int Status, ErrorDto Dto) Map(Exception ex)
    {
        switch (ex)
        {
            case WriteRejectedException w:
                return (w.Status, new ErrorDto { Code = w.Code, Message = w.Message });
            case ConcurrencyException c:
            {
                var current = c.Data["current"] as Entity;
                return (409, new ErrorDto
                {
                    Code = ErrorCodes.Conflict, Message = c.Message, Table = c.Table, RowId = c.RowId, ChangedBy = current?.UpdatedBy ?? c.ChangedBy,
                    ChangedAt = current?.UpdatedAt, Current = current is null ? null : JsonSerializer.SerializeToElement(current, current.GetType(), RemoteJson.Options),
                });
            }
            case JsonException or BadHttpRequestException or FormatException:
                return (400, new ErrorDto { Code = ErrorCodes.BadRequest, Message = ex.Message });
            case InvalidOperationException io when io.Message.Contains("locked", StringComparison.OrdinalIgnoreCase):
                return (409, new ErrorDto { Code = ErrorCodes.Locked, Message = io.Message });
            default:
                return (500, new ErrorDto { Code = "server_error", Message = "The server could not complete the request: " + ex.Message });
        }
    }
}
