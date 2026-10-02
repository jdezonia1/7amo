using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Raffaello.Core.Data;

namespace Raffaello.Core.Remote;

/// <summary>
/// Thin HTTP client for Raffaello.Server. Synchronous methods (the store interface is synchronous and is called off the UI
/// thread); maps answers to exceptions: network trouble -> <see cref="ServerUnavailableException"/>, 409 -> <see cref="ConcurrencyException"/>
/// (with the server's current row in Data["current"]), 422 remaining -> <see cref="RemainingExceededException"/>, 403, 401 ...
/// </summary>
public sealed class RemoteApi : IDisposable
{
    private readonly HttpClient _http;
    public Uri BaseUri { get; }
    public string Machine { get; }
    public string ClientId { get; }
    public string? Token { get; private set; }

    public RemoteApi(string baseUrl, string? token, bool useWindowsAuth, string machine, string clientId, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"'{baseUrl}' is not a server address (e.g. http://RAFFAELLO-SRV:5180).");
        BaseUri = u;
        Machine = machine; ClientId = clientId; Token = string.IsNullOrWhiteSpace(token) ? null : token;
        handler ??= new HttpClientHandler { UseDefaultCredentials = useWindowsAuth, PreAuthenticate = true, AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { BaseAddress = u, Timeout = timeout };
        _http.DefaultRequestHeaders.Add(ApiRoutes.MachineHeader, machine);
        _http.DefaultRequestHeaders.Add(ApiRoutes.ClientHeader, clientId);
        if (Token != null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public void SetToken(string? token)
    {
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        _http.DefaultRequestHeaders.Authorization = Token is null ? null : new AuthenticationHeaderValue("Bearer", Token);
    }

    public Uri Url(string route) => new(BaseUri, route.TrimStart('/'));

    // ------------------------------------------------------------------ core send

    public HttpResponseMessage Send(HttpRequestMessage req, bool allowNotModified = false)
    {
        HttpResponseMessage resp;
        try { resp = _http.Send(req, HttpCompletionOption.ResponseHeadersRead); }
        catch (HttpRequestException ex) { throw new ServerUnavailableException($"Server {BaseUri.Authority} not reachable: {ex.Message}", ex); }
        catch (TaskCanceledException ex) { throw new ServerUnavailableException($"Server {BaseUri.Authority} did not answer in time.", ex); }
        catch (IOException ex) { throw new ServerUnavailableException($"Connection to {BaseUri.Authority} lost: {ex.Message}", ex); }
        if (resp.IsSuccessStatusCode || (allowNotModified && resp.StatusCode == HttpStatusCode.NotModified)) return resp;
        using (resp) throw MapError(resp);
    }

    private static Exception MapError(HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        string body;
        try { body = ReadString(resp); } catch (Exception ex) { return new ServerUnavailableException("Connection lost while reading the answer.", ex); }
        if (status is 502 or 503 or 504) return new ServerUnavailableException($"Server unavailable ({status}).");
        ErrorDto err;
        try { err = RemoteJson.Deserialize<ErrorDto>(body) ?? new ErrorDto(); }
        catch (JsonException) { err = new ErrorDto { Message = body.Length > 300 ? body[..300] : body }; }
        if (err.Message.Length == 0) err.Message = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
        switch (status)
        {
            case 401: return new RemoteAuthException(err.Code.Length > 0 ? err : new ErrorDto { Code = "unauthorized", Message = "Not signed in to the Raffaello server (or the session expired). Sign in again in Settings." });
            case 403: return new PermissionDeniedException(err);
            case 409 when err.Code == ErrorCodes.Conflict:
            {
                var ex = new ConcurrencyException(err.Table ?? "", err.RowId ?? 0, err.ChangedBy);
                if (err.Current is JsonElement cur) ex.Data["current"] = cur.GetRawText();
                if (err.ChangedAt is DateTime at) ex.Data["changedAt"] = at;
                return ex;
            }
            case 422 when err.Code == ErrorCodes.RemainingExceeded: return new RemainingExceededException(err);
            case 404 when err.Code == ErrorCodes.NotFound: return new InvalidOperationException(err.Message);
            default: return new RemoteRejectedException(status, err);
        }
    }

    private static string ReadString(HttpResponseMessage resp)
    {
        using var s = resp.Content.ReadAsStream();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private string GetString(string route)
    {
        using var resp = Send(new HttpRequestMessage(HttpMethod.Get, route.TrimStart('/')));
        return ReadString(resp);
    }

    private T Get<T>(string route) => RemoteJson.Deserialize<T>(GetString(route)) ?? throw new InvalidDataException($"Empty answer from {route}.");

    private string PostString(string route, object? body, HttpMethod? method = null)
    {
        var req = new HttpRequestMessage(method ?? HttpMethod.Post, route.TrimStart('/'));
        if (body != null) req.Content = new StringContent(RemoteJson.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = Send(req);
        return ReadString(resp);
    }

    private T Post<T>(string route, object? body) => RemoteJson.Deserialize<T>(PostString(route, body)) ?? throw new InvalidDataException($"Empty answer from {route}.");

    // ------------------------------------------------------------------ endpoints

    public bool Health()
    {
        try { _ = GetString(ApiRoutes.Health); return true; }
        catch (ServerUnavailableException) { return false; }
        catch (RemoteRejectedException) { return false; }
    }

    public LoginResponse Login(string user, string password) =>
        Post<LoginResponse>(ApiRoutes.Login, new LoginRequest { UserName = user, Password = password, Machine = Machine });

    public void Logout() { try { PostString(ApiRoutes.Logout, null); } catch (Exception) { /* best effort */ } }

    public MeDto Me() => Get<MeDto>(ApiRoutes.Me);
    public ServerInfoDto Info() => Get<ServerInfoDto>(ApiRoutes.Info);

    /// <summary>Rows of a table as JSON; null when the cached ETag is still current (304).</summary>
    public (string? Json, string? ETag) GetTable(string table, string? etag)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiRoutes.Tables.TrimStart('/')}/{Uri.EscapeDataString(table)}");
        if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var resp = Send(req, allowNotModified: true);
        var tag = resp.Headers.ETag?.ToString();
        if (resp.StatusCode == HttpStatusCode.NotModified) return (null, tag ?? etag);
        return (ReadString(resp), tag);
    }

    public string? GetRow(string table, long id)
    {
        try { return GetString($"{ApiRoutes.Tables}/{Uri.EscapeDataString(table)}/{id}"); }
        catch (InvalidOperationException) { return null; }
    }

    public int Count(string table)
    {
        using var doc = JsonDocument.Parse(GetString($"{ApiRoutes.Tables}/{Uri.EscapeDataString(table)}/count"));
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    public WriteResponseDto Write(WriteRequestDto req) => Post<WriteResponseDto>(ApiRoutes.Write, req);

    public void ClearAll() => PostString(ApiRoutes.ClearAll, null);

    public List<Domain.AuditEntry> Audit(int take, DateTime? since) =>
        Get<List<Domain.AuditEntry>>($"{ApiRoutes.Audit}?take={take}" + (since is DateTime s ? "&since=" + Uri.EscapeDataString(s.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)) : ""));

    public void LogEvent(string action, string summary) => PostString(ApiRoutes.Events, new EventDto { Action = action, Summary = summary });
    public void Heartbeat(string screen) => PostString(ApiRoutes.Presence, new PresenceDto { Screen = screen });
    public List<Domain.PresenceRow> Presence(TimeSpan window) => Get<List<Domain.PresenceRow>>($"{ApiRoutes.Presence}?windowSeconds={(int)window.TotalSeconds}");
    public string? GetMeta(string key) => Get<MetaDto>($"{ApiRoutes.Meta}/{Uri.EscapeDataString(key)}").Value;
    public void SetMeta(string key, string value) => PostString($"{ApiRoutes.Meta}/{Uri.EscapeDataString(key)}", new MetaDto { Value = value }, HttpMethod.Put);

    public List<ApprovalStampDto> Stamps(string table, long id) => Get<List<ApprovalStampDto>>($"{ApiRoutes.Approvals}?table={Uri.EscapeDataString(table)}&id={id}");
    public ApprovalStampDto Stamp(StampRequest req) => Post<ApprovalStampDto>(ApiRoutes.Approvals, req);

    public List<UserDto> Users() => Get<List<UserDto>>(ApiRoutes.Users);
    public UserDto CreateUser(UserDto u) => Post<UserDto>(ApiRoutes.Users, u);
    public UserDto UpdateUser(UserDto u) => RemoteJson.Deserialize<UserDto>(PostString($"{ApiRoutes.Users}/{u.Id}", u, HttpMethod.Put))!;

    public MigrateImportResponse MigrateImport(MigrateImportRequest req) => Post<MigrateImportResponse>(ApiRoutes.MigrateImport, req);
    public int MigrateAudit(MigrateAuditRequest req)
    {
        using var doc = JsonDocument.Parse(PostString(ApiRoutes.MigrateAudit, req));
        return doc.RootElement.GetProperty("added").GetInt32();
    }

    public DocumentInfo Upload(Stream content, string fileName, string category = "", string linkedTable = "", long linkedId = 0, string contentType = "application/octet-stream")
    {
        var route = $"{ApiRoutes.Documents.TrimStart('/')}?fileName={Uri.EscapeDataString(fileName)}&category={Uri.EscapeDataString(category)}&linkedTable={Uri.EscapeDataString(linkedTable)}&linkedId={linkedId}";
        var req = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StreamContent(content) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var resp = Send(req);
        return RemoteJson.Deserialize<DocumentInfo>(ReadString(resp))!;
    }

    public List<DocumentInfo> Documents(string? linkedTable = null, long? linkedId = null) =>
        Get<List<DocumentInfo>>($"{ApiRoutes.Documents}?linkedTable={Uri.EscapeDataString(linkedTable ?? "")}&linkedId={linkedId ?? 0}");

    /// <summary>Downloads a document to <paramref name="path"/> and checks its SHA-256 against the server's.</summary>
    public DocumentInfo Download(long id, string path)
    {
        var info = Get<DocumentInfo>($"{ApiRoutes.Documents}/{id}/info");
        using var resp = Send(new HttpRequestMessage(HttpMethod.Get, $"{ApiRoutes.Documents.TrimStart('/')}/{id}"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var s = resp.Content.ReadAsStream())
        using (var f = File.Create(path)) s.CopyTo(f);
        using (var f = File.OpenRead(path))
        {
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
            if (sha != info.Sha256) throw new InvalidDataException($"{info.FileName}: checksum mismatch after download (file damaged in transit or on the share).");
        }
        return info;
    }

    public void Dispose() => _http.Dispose();
}
