using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Portal;
using Raffaello.Core.Remote;
using Raffaello.Core.Statements;
using Raffaello.Server.Api;
using Raffaello.Server.Auth;
using Raffaello.Server.Data;
using Raffaello.Server.Documents;

namespace Raffaello.Server.Portal;

/// <summary>
/// [trust] Subcontractor portal: separate accounts (role SUBCONTRACTOR), a JSON API under /api/v1/portal that answers only
/// with the caller's company data (filtered on the server from the portal token - never from anything the browser sends), a
/// bilingual static web page at /portal, and the administration endpoints for the QS / ADMIN. Submissions, messages and company
/// settings are ordinary entities, so the app sees them live (inbox) and every change is audited and hash-chained.
/// </summary>
public sealed class PortalServerModule : IServerModule
{
    public string Name => "Portal";
    public IEnumerable<Type> EntityTypes => PortalEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new PortalGuard() };

    private static readonly JsonSerializerOptions Json = RemoteJson.Options;
    private static IResult JsonOk(object v) => Results.Text(JsonSerializer.Serialize(v, v.GetType(), Json), "application/json");

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var sp = api.ServiceProvider;
        var opt = sp.GetRequiredService<IConfiguration>().GetSection("Raffaello:Portal").Get<PortalOptions>() ?? new PortalOptions();
        var serverOpt = sp.GetRequiredService<ServerOptions>();
        var accounts = new PortalAccounts(sp.GetRequiredService<NpgsqlDataSource>())
        {
            TokenLifetime = TimeSpan.FromHours(Math.Clamp(opt.TokenHours, 1, 24 * 7)), BcryptWorkFactor = Math.Clamp(serverOpt.BcryptWorkFactor, 4, 16),
        };
        accounts.EnsureSchema();
        var limiter = new PortalRateLimiter();
        Accounts = accounts;
        Limiter = limiter;

        MapAdmin(api, accounts);
        if (!opt.Enabled) return;
        var portal = api.MapGroup("").AllowAnonymous().AddEndpointFilter(async (ic, next) =>
        {
            var h = ic.HttpContext.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Cache-Control"] = "no-store";
            h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
            try { return await next(ic); }
            catch (PortalLimitException ex)
            {
                h.RetryAfter = ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new ErrorDto { Code = "too_many_requests", Message = ex.Message }, Json, statusCode: 429);
            }
        });
        new PortalApi(accounts, limiter, opt).Map(portal);
        MapStatic(portal);
    }

    /// <summary>For tests / tools in the same process.</summary>
    public static PortalAccounts? Accounts { get; private set; }
    public static PortalRateLimiter? Limiter { get; private set; }

    private static void MapAdmin(IEndpointRouteBuilder api, PortalAccounts accounts)
    {
        api.MapGet(PortalRoutes.AdminAccounts, (HttpContext ctx) =>
        {
            Require(ctx, Permissions.EditData, Permissions.ManageUsers);
            return JsonOk(accounts.All().Select(a => a.ToDto()).ToList());
        });
        api.MapPost(PortalRoutes.AdminAccounts, (HttpContext ctx, PortalAccountDto dto, StoreFactory f) =>
        {
            Require(ctx, Permissions.ManageUsers);
            var a = accounts.Create(dto, ctx.Identity().User);
            f.For(ctx).LogEvent("PORTAL", $"Portal account {a.UserName} created for {a.Company}");
            return JsonOk(a.ToDto());
        });
        api.MapPut(PortalRoutes.AdminAccounts + "/{id:long}", (HttpContext ctx, long id, PortalAccountDto dto, StoreFactory f) =>
        {
            Require(ctx, Permissions.ManageUsers);
            var a = accounts.Update(id, dto);
            f.For(ctx).LogEvent("PORTAL", $"Portal account {a.UserName} updated ({a.Company}{(a.Active ? "" : ", disabled")}{(string.IsNullOrEmpty(dto.Password) ? "" : ", new password")})");
            return JsonOk(a.ToDto());
        });
    }

    private static void Require(HttpContext ctx, params string[] any)
    {
        var who = ctx.Identity();
        if (!any.Any(who.Can)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{who.User} ({who.Role}) needs {string.Join(" or ", any)}.");
    }

    private static void MapStatic(IEndpointRouteBuilder portal)
    {
        IResult Asset(string name, string type)
        {
            var asm = typeof(PortalServerModule).Assembly;
            using var s = asm.GetManifestResourceStream("Raffaello.Server.Portal.web." + name);
            if (s is null) return Results.NotFound();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Results.Bytes(ms.ToArray(), type);
        }
        portal.MapGet(PortalRoutes.Page, () => Asset("index.html", "text/html; charset=utf-8"));
        portal.MapGet(PortalRoutes.Page + "/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        portal.MapGet(PortalRoutes.Page + "/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        portal.MapGet(PortalRoutes.Page + "/logo.svg", () => Asset("logo.svg", "image/svg+xml"));
    }
}

/// <summary>The subcontractor API. Every handler starts with <see cref="Auth"/>; the company always comes from the token.</summary>
internal sealed class PortalApi
{
    private readonly PortalAccounts _accounts;
    private readonly PortalRateLimiter _limits;
    private readonly PortalOptions _opt;
    private static readonly JsonSerializerOptions Json = RemoteJson.Options;

    public PortalApi(PortalAccounts accounts, PortalRateLimiter limits, PortalOptions opt) { _accounts = accounts; _limits = limits; _opt = opt; }

    private static IResult Ok(object v) => Results.Text(JsonSerializer.Serialize(v, v.GetType(), Json), "application/json");
    private static string Address(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "";

    private PortalPrincipal Auth(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : "";
        var a = token.Length > 0 ? _accounts.Validate(token) : null;
        if (a is null)
        {
            _limits.Hit("anon|" + Address(ctx), _opt.RequestsPerMinute, TimeSpan.FromMinutes(1), "requests");
            throw PortalErrors.Unauthorized();
        }
        _limits.Hit("acct|" + a.Id, _opt.RequestsPerMinute, TimeSpan.FromMinutes(1), "requests");
        return new PortalPrincipal(a.Id, a.UserName, a.DisplayName, a.Company, a.Language, Address(ctx));
    }

    private static PortalCompanySetting Company(PgStore store, PortalPrincipal p) =>
        store.All<PortalCompanySetting>().FirstOrDefault(c => c.Name.Equals(p.Company, StringComparison.OrdinalIgnoreCase))
        ?? new PortalCompanySetting { Name = p.Company, DisplayName = p.Company };

    private static bool Mine(string? company, PortalPrincipal p) => string.Equals((company ?? "").Trim(), p.Company, StringComparison.OrdinalIgnoreCase);

    public void Map(IEndpointRouteBuilder g)
    {
        g.MapPost(PortalRoutes.Login, (PortalLoginRequest req, LoginThrottle throttle, HttpContext ctx, StoreFactory f) =>
        {
            var user = "portal:" + (req.UserName ?? "").Trim();
            var address = Address(ctx);
            _limits.Hit("login|" + address, Math.Max(10, _opt.RequestsPerMinute / 4), TimeSpan.FromMinutes(1), "sign-in attempts");
            if (throttle.RetryAfter(user, address) is { } wait)
            {
                ctx.Response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new ErrorDto { Code = "too_many_attempts", Message = $"Too many wrong sign-ins. Try again in {Math.Max(1, wait / 60)} minute(s)." }, Json, statusCode: 429);
            }
            var r = _accounts.Login(req.UserName ?? "", req.Password ?? "", address);
            var store = f.For(new StoreIdentity(user, PortalRoles.Subcontractor, address, "", "portal", Trusted: true));
            if (r is null)
            {
                throttle.Failed(user, address);
                store.LogEvent("PORTAL-LOGIN-FAILED", $"Portal sign-in refused for {req.UserName} from {address}");
                return Results.Json(new ErrorDto { Code = "unauthorized", Message = "Wrong user name or password, or the account is disabled." }, Json, statusCode: 401);
            }
            throttle.Succeeded(user);
            store.LogEvent("PORTAL-LOGIN", $"{r.UserName} ({r.Company}) signed in to the portal from {address}");
            return Ok(r);
        });

        g.MapPost(PortalRoutes.Logout, (HttpContext ctx) =>
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) _accounts.Revoke(h[7..].Trim());
            return Results.NoContent();
        });

        g.MapGet(PortalRoutes.Me, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var store = f.For(p.Who);
            var c = Company(store, p);
            return Ok(new PortalMeDto
            {
                UserName = p.UserName, DisplayName = p.DisplayName, Company = p.Company, CompanyDisplayName = c.DisplayName.Length > 0 ? c.DisplayName : c.Name,
                Building = c.Building, ContractNo = c.ContractNo, Language = p.Language, MaxFileBytes = _opt.MaxFileBytes, MaxFiles = _opt.MaxFilesPerSubmission,
                AllowedExtensions = PortalFileTypes.Extensions.ToList(),
                UnreadMessages = store.All<PortalMessage>().Count(m => Mine(m.Company, p) && m.Direction == PortalMessageDirections.ToSubcontractor && m.ReadAt is null),
            });
        });

        g.MapGet(PortalRoutes.Template, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            _limits.Hit("tmpl|" + p.AccountId, 20, TimeSpan.FromHours(1), "template downloads");
            var store = f.For(p.Who);
            var c = Company(store, p);
            var s = ProjectSnapshot.Load(store);
            var rooms = PortalScope.Rooms(s, c);
            if (rooms.Count == 0) throw new WriteRejectedException(409, "no_scope", "No rooms are assigned to your company yet - ask the QS.");
            var balances = LedgerRules.Balances(s.RoomQtys, s.Claims);
            var no = $"ST-{c.Name}-{s.Statements.Count(x => x.Subcontractor.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && x.Direction == "OUT") + 1:00}";
            var tmp = Path.Combine(Path.GetTempPath(), "raffaello-portal-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                SiteStatementService.Generate(tmp, c.Name, no, rooms, balances: balances);
                var bytes = File.ReadAllBytes(tmp);
                store.Insert(new SiteStatement { Subcontractor = c.Name, StatementNo = no, Direction = "OUT", FileName = no + ".xlsx", Lines = rooms.Count, At = DateTime.Now },
                    $"Portal: statement template {no} downloaded by {p.UserName} ({rooms.Count} rooms)");
                return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", no + ".xlsx");
            }
            finally { try { File.Delete(tmp); } catch (IOException) { /* temp clean-up */ } }
        });

        g.MapPost(PortalRoutes.Submissions, async (HttpContext ctx, StoreFactory f, DocumentStore docs) => await SubmitAsync(ctx, f, docs));

        g.MapGet(PortalRoutes.Submissions, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var list = f.For(p.Who).All<PortalSubmission>().Where(s => Mine(s.Company, p)).OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id)
                .Select(s => new
                {
                    s.Id, s.StatementNo, s.Note, s.SubmittedAt, s.SubmittedBy, s.Status, s.Reason, s.ReviewedAt, s.Files, s.TotalBytes, s.StatementLines,
                    s.StatementQty, s.Findings, s.ImportedLines, s.InvoiceNo,
                }).ToList();
            return Ok(list);
        });

        g.MapGet(PortalRoutes.Submissions + "/{id:long}/files", (HttpContext ctx, long id, StoreFactory f, DocumentStore docs) =>
        {
            var p = Auth(ctx);
            var s = f.For(p.Who).Get<PortalSubmission>(id);
            if (s is null || !Mine(s.Company, p)) throw PortalErrors.NotFound($"Submission #{id}");
            return Ok(docs.List(PortalFileKinds.LinkedTable, id).Select(d => new PortalFileDto { Id = d.Id, FileName = d.FileName, Kind = d.Category, Size = d.Size, Sha256 = d.Sha256, UploadedAt = d.UploadedAt }).ToList());
        });

        g.MapGet(PortalRoutes.Files + "/{id:long}", (HttpContext ctx, long id, StoreFactory f, DocumentStore docs) =>
        {
            var p = Auth(ctx);
            var info = docs.Get(id);
            // only files of the caller's own submissions; anything else is "not found" (no hint that it exists)
            if (info is null || info.LinkedTable != PortalFileKinds.LinkedTable) throw PortalErrors.NotFound($"File #{id}");
            var s = f.For(p.Who).Get<PortalSubmission>(info.LinkedId);
            if (s is null || !Mine(s.Company, p)) throw PortalErrors.NotFound($"File #{id}");
            var path = docs.PathOf(id);
            if (!File.Exists(path)) throw PortalErrors.NotFound($"File #{id}");
            return Results.File(path, string.IsNullOrEmpty(info.ContentType) ? "application/octet-stream" : info.ContentType, info.FileName);
        });

        g.MapGet(PortalRoutes.Claims, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var list = f.For(p.Who).All<ClaimLine>().Where(c => Mine(c.Subcontractor, p) && !c.ReplacedBySplit)
                .OrderByDescending(c => c.InvoiceNo).ThenBy(c => c.Room).ThenBy(c => c.Stage).ThenBy(c => c.Item)
                .Select(c => new PortalClaimDto
                {
                    InvoiceNo = c.InvoiceNo, Room = c.Room, Stage = c.Stage, Item = c.Item, Qty = c.Qty, SitePct = c.SitePct, WirPct = c.WirPct, WirNo = c.WirNo,
                    StatementNo = c.StatementNo, IsOver = c.IsOver, HeightStatus = c.HeightStatus, LengthStatus = c.LengthStatus, EnteredAt = c.EnteredAt,
                }).Take(5000).ToList();
            return Ok(list);
        });

        g.MapGet(PortalRoutes.Invoices, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var store = f.For(p.Who);
            var invoices = store.All<SubInvoice>().Where(i => Mine(i.Subcontractor, p) && InvoiceKinds.IsSubcontractor(i)).ToList();
            var ids = invoices.Select(i => i.Id).ToHashSet();
            var lines = store.All<SubInvoiceLine>().Where(l => ids.Contains(l.SubInvoiceId)).GroupBy(l => l.SubInvoiceId).ToDictionary(g => g.Key, g => g.ToList());
            var list = invoices.OrderByDescending(i => i.InvoiceNo).ThenByDescending(i => i.Revision).Select(i => new PortalInvoiceDto
            {
                InvoiceNo = i.InvoiceNo, Revision = i.Revision, ContractNo = i.ContractNo, Status = i.Status.ToLowerInvariant(),
                Reason = i.Status == SubInvoiceStatus.Rejected ? i.RejectionReason : "", CreatedAt = i.CreatedAt, SubmittedAt = i.SubmittedAt, ApprovedAt = i.ApprovedAt,
                CurrentAmount = Math.Round(lines.GetValueOrDefault(i.Id)?.Where(l => l.Kind == "ITEM").Sum(l => l.CurrAmount) ?? 0, 2),
                CumulativeAmount = Math.Round(lines.GetValueOrDefault(i.Id)?.Where(l => l.Kind == "ITEM").Sum(l => l.CumAmount) ?? 0, 2),
                AconexWorkflowNo = i.AconexWorkflowNo,
            }).ToList();
            return Ok(list);
        });

        g.MapGet(PortalRoutes.Remaining, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var store = f.For(p.Who);
            var c = Company(store, p);
            var s = ProjectSnapshot.Load(store);
            var rooms = PortalScope.Rooms(s, c).ToDictionary(r => r.Code.Trim().ToUpperInvariant(), r => r);
            var balances = LedgerRules.Balances(s.RoomQtys, s.Claims);
            var list = balances.Values.Where(b => rooms.ContainsKey(b.Room) && b.HasCap)
                .OrderBy(b => b.Room).ThenBy(b => b.Stage).ThenBy(b => b.Item)
                .Select(b => new PortalRemainingDto
                {
                    Room = rooms[b.Room].Code, Level = rooms[b.Room].Level, RoomType = rooms[b.Room].RoomType, Stage = b.Stage, Item = b.Item, ProjectQty = b.ProjectQty,
                    ClaimedByYou = b.BySubcontractor.Where(kv => Mine(kv.Key, p)).Sum(kv => kv.Value), Remaining = Math.Round(b.Remaining, 4),
                }).ToList();
            return Ok(list);
        });

        g.MapGet(PortalRoutes.Messages, (HttpContext ctx, StoreFactory f) =>
        {
            var p = Auth(ctx);
            return Ok(f.For(p.Who).All<PortalMessage>().Where(m => Mine(m.Company, p)).OrderBy(m => m.SentAt).ThenBy(m => m.Id)
                .Select(m => new PortalMessageDto { Id = m.Id, Direction = m.Direction, From = m.From, Subject = m.Subject, Body = m.Body, SentAt = m.SentAt, ReadAt = m.ReadAt, SubmissionId = m.SubmissionId })
                .ToList());
        });

        g.MapPost(PortalRoutes.Messages, (HttpContext ctx, PortalSendMessageRequest req, StoreFactory f) =>
        {
            var p = Auth(ctx);
            _limits.Hit("msg|" + p.AccountId, _opt.MessagesPerHour, TimeSpan.FromHours(1), "messages");
            var body = (req.Body ?? "").Trim();
            if (body.Length == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "The message is empty.");
            if (body.Length > _opt.MaxMessageChars) throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"Messages are limited to {_opt.MaxMessageChars} characters.");
            var store = f.For(p.Who);
            if (req.SubmissionId > 0)
            {
                var about = store.Get<PortalSubmission>(req.SubmissionId);
                if (about is null || !Mine(about.Company, p)) throw PortalErrors.NotFound($"Submission #{req.SubmissionId}");
            }
            var m = store.Insert(new PortalMessage
            {
                Company = p.Company, Direction = PortalMessageDirections.FromSubcontractor, From = p.UserName, Subject = Trim(req.Subject, 200), Body = body,
                SentAt = DateTime.Now, SubmissionId = req.SubmissionId,
            }, $"Portal message from {p.UserName} ({p.Company}): {Trim(req.Subject, 80)}");
            return Ok(new PortalMessageDto { Id = m.Id, Direction = m.Direction, From = m.From, Subject = m.Subject, Body = m.Body, SentAt = m.SentAt, SubmissionId = m.SubmissionId });
        });

        g.MapPost(PortalRoutes.Messages + "/{id:long}/read", (HttpContext ctx, long id, StoreFactory f) =>
        {
            var p = Auth(ctx);
            var store = f.For(p.Who);
            var m = store.Get<PortalMessage>(id);
            if (m is null || !Mine(m.Company, p)) throw PortalErrors.NotFound($"Message #{id}");
            if (m.Direction == PortalMessageDirections.ToSubcontractor && m.ReadAt is null)
            {
                m.ReadAt = DateTime.Now;
                store.Update(m, $"Portal message #{id} read by {p.UserName}");
            }
            return Results.NoContent();
        });
    }

    private static string Trim(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }

    /// <summary>
    /// Multipart upload: note, statementNo, files. Every file is checked (extension + content signature, size) before anything
    /// is stored; a statement workbook must be a Raffaello statement of the caller's own company.
    /// </summary>
    private async Task<IResult> SubmitAsync(HttpContext ctx, StoreFactory f, DocumentStore docs)
    {
        var p = Auth(ctx);
        if (!ctx.Request.HasFormContentType) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Send the files as multipart/form-data.");
        if (ctx.Request.ContentLength is long len && len > _opt.MaxSubmissionBytes + 64 * 1024)
            throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"A submission may be at most {PortalFileTypes.Describe(_opt.MaxSubmissionBytes)}.");
        var store = f.For(p.Who);
        var company = Company(store, p);
        if (!company.Active) throw new WriteRejectedException(403, ErrorCodes.Forbidden, "Your company's portal access is paused - contact the QS.");
        // only accepted submissions count against the hourly limit (refused uploads are covered by the per-minute request limit)
        _limits.Check("sub|" + p.AccountId, _opt.MaxSubmissionsPerHour, TimeSpan.FromHours(1), "submissions");
        var perDay = company.MaxSubmissionsPerDay > 0 ? company.MaxSubmissionsPerDay : _opt.MaxSubmissionsPerDay;
        var today = store.All<PortalSubmission>().Count(s => Mine(s.Company, p) && s.SubmittedAt.Date == DateTime.Today);
        if (today >= perDay) throw new PortalLimitException((int)(DateTime.Today.AddDays(1) - DateTime.Now).TotalSeconds, $"Your company already sent {today} submissions today (limit {perDay}).");

        ctx.Features.Set<IFormFeature>(new FormFeature(ctx.Request, new FormOptions
        {
            MultipartBodyLengthLimit = _opt.MaxSubmissionBytes, ValueCountLimit = 64, ValueLengthLimit = 8 * 1024, MultipartHeadersLengthLimit = 16 * 1024,
        }));
        IFormCollection form;
        try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
        catch (InvalidDataException ex) { throw new WriteRejectedException(413, ErrorCodes.TooLarge, "The upload is too large or malformed: " + ex.Message); }
        if (form.Files.Count == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Attach at least one file (statement, drawing or photo).");
        if (form.Files.Count > _opt.MaxFilesPerSubmission) throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"At most {_opt.MaxFilesPerSubmission} files per submission.");

        var temp = Path.Combine(Path.GetTempPath(), "raffaello-portal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var staged = new List<(string Path, string Name, string Kind, string ContentType, long Size)>();
            long total = 0;
            var i = 0;
            foreach (var file in form.Files)
            {
                var name = DocumentStore.SafeName(file.FileName);
                if (file.Length <= 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"{name} is empty.");
                if (file.Length > _opt.MaxFileBytes) throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"{name} is larger than {PortalFileTypes.Describe(_opt.MaxFileBytes)}.");
                total += file.Length;
                if (total > _opt.MaxSubmissionBytes) throw new WriteRejectedException(413, ErrorCodes.TooLarge, $"The files together are larger than {PortalFileTypes.Describe(_opt.MaxSubmissionBytes)}.");
                var path = Path.Combine(temp, $"{i++:000}_{name}");
                await using (var fs = File.Create(path)) await file.CopyToAsync(fs, ctx.RequestAborted);
                var head = new byte[16];
                int read;
                await using (var fs = File.OpenRead(path)) read = await fs.ReadAsync(head, ctx.RequestAborted);
                var kind = PortalFileTypes.Classify(name, head.AsSpan(0, read)) ?? throw PortalErrors.UnsupportedFile(name);
                staged.Add((path, name, kind.Kind, kind.ContentType, file.Length));
            }

            var statements = staged.Where(s => s.Kind == PortalFileKinds.Statement).ToList();
            if (statements.Count > 1) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "Send one statement workbook per submission.");
            var findings = new List<string>();
            int lines = 0; double qty = 0;
            var statementNo = Trim(form["statementNo"].ToString(), 60);
            if (statements.Count == 1)
            {
                var snapshot = ProjectSnapshot.Load(store);
                StatementImportResult read;
                try { read = SiteStatementService.Read(statements[0].Path, snapshot, 0, company.Building); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or KeyNotFoundException or FormatException or ArgumentException)
                { throw new WriteRejectedException(422, "invalid_statement", $"{statements[0].Name} is not a Raffaello site statement ({ex.Message}). Download the template from this portal."); }
                if (read.Subcontractor.Length == 0) throw new WriteRejectedException(422, "invalid_statement", $"{statements[0].Name} is not a Raffaello site statement. Download the template from this portal.");
                if (!Mine(read.Subcontractor, p))
                    throw new WriteRejectedException(422, "invalid_statement", $"{statements[0].Name} is a statement of another company - use your own template.");
                if (statementNo.Length == 0) statementNo = read.StatementNo;
                lines = read.Claims.Count;
                qty = read.Claims.Sum(c => c.Qty);
                if (read.IsDuplicate) findings.Add("DUPLICATE: " + string.Join(" ", read.Issues.Where(x => x.Level == Core.Import.IssueLevel.Error).Select(x => x.Message)));
                var over = read.Checks.Count(c => !c.Check.CanPost);
                if (over > 0) findings.Add($"{over} line(s) above the remaining quantity");
                var unknown = read.Issues.Count(x => x.Message.Contains("not a known room", StringComparison.Ordinal));
                if (unknown > 0) findings.Add($"{unknown} unknown room(s)");
            }
            else findings.Add("No statement workbook (drawings / photos only).");

            var sub = store.Insert(new PortalSubmission
            {
                Company = p.Company, SubmittedBy = p.UserName, SubmittedAt = DateTime.Now, StatementNo = statementNo, Note = Trim(form["note"].ToString(), 2000),
                Status = PortalSubmissionStatus.Submitted, Files = staged.Count, TotalBytes = total, StatementLines = lines, StatementQty = Math.Round(qty, 4),
                Findings = string.Join("; ", findings),
            }, $"Portal submission from {p.UserName} ({p.Company}): {staged.Count} file(s){(statementNo.Length > 0 ? ", statement " + statementNo : "")}");

            long statementDoc = 0; string statementSha = "";
            foreach (var s in staged)
            {
                await using var fs = File.OpenRead(s.Path);
                var info = await docs.SaveAsync(fs, s.Name, s.ContentType, s.Kind, PortalFileKinds.LinkedTable, sub.Id, p.Who, ctx.RequestAborted);
                if (s.Kind == PortalFileKinds.Statement) { statementDoc = info.Id; statementSha = info.Sha256; }
            }
            if (statementDoc > 0)
            {
                sub = store.Get<PortalSubmission>(sub.Id)!;   // as stored (timestamps at database precision)
                sub.StatementDocumentId = statementDoc;
                sub.StatementSha256 = statementSha;
                sub = store.Update(sub, $"Portal submission #{sub.Id}: statement file #{statementDoc}");
            }
            _limits.Hit("sub|" + p.AccountId, int.MaxValue, TimeSpan.FromHours(1), "submissions");
            return Ok(new { sub.Id, sub.Status, sub.StatementNo, sub.Files, sub.StatementLines, sub.StatementQty, sub.Findings });
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { /* temp clean-up */ } }
    }
}

/// <summary>Which rooms a company works in: its RoomScope patterns, else the rooms it already claimed in.</summary>
public static class PortalScope
{
    public static List<Room> Rooms(ProjectSnapshot s, PortalCompanySetting c)
    {
        var building = string.IsNullOrWhiteSpace(c.Building) ? null : c.Building.Trim();
        var rooms = s.Rooms.Where(r => building is null || r.Building.Equals(building, StringComparison.OrdinalIgnoreCase)).ToList();
        var patterns = (c.RoomScope ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<Room> pick;
        if (patterns.Length > 0)
            pick = rooms.Where(r => patterns.Any(pt => Core.Integrations.CadExchange.BlockMapping.Wildcard(pt, r.Code.Trim())));
        else
        {
            var claimed = s.Claims.Where(x => x.Subcontractor.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Room.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            pick = rooms.Where(r => claimed.Contains(r.Code.Trim()));
        }
        return pick.OrderBy(r => r.Plot).ThenBy(r => r.Floor).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Rules for the app side of the portal tables: submissions are created only by the portal (subcontractor) and their content
/// never changes - the QS only moves the status (with a reason) and records the import; messages from the app are always
/// "to the subcontractor" from the signed-in user; nothing portal-related is ever deleted.
/// </summary>
public sealed class PortalGuard : IWriteGuard
{
    private static readonly string[] ReviewFields =
    {
        nameof(PortalSubmission.Status), nameof(PortalSubmission.Reason), nameof(PortalSubmission.ReviewedBy), nameof(PortalSubmission.ReviewedAt),
        nameof(PortalSubmission.ImportedLines), nameof(PortalSubmission.InvoiceNo), "UpdatedAt", "UpdatedBy", "RowVersion",
    };

    public void Check(WriteCheck c)
    {
        if (c.Type == typeof(PortalSubmission)) Submission(c);
        else if (c.Type == typeof(PortalMessage)) Message(c);
        else if (c.Type == typeof(PortalCompanySetting) && c.Kind != WriteKind.Delete)
        {
            var s = (PortalCompanySetting)c.Entity;
            s.Name = (s.Name ?? "").Trim().ToUpperInvariant();
            if (s.Name.Length == 0) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "The company name (as used in the ledger) is required.");
            var dup = c.Tx.Scalar("""SELECT 1 FROM "PortalCompanySettings" WHERE upper("Name") = @n AND "Id" <> @id LIMIT 1""", ("n", s.Name), ("id", s.Id));
            if (dup != null) throw new WriteRejectedException(409, ErrorCodes.Conflict, $"Portal company {s.Name} already exists.");
        }
    }

    private static bool IsPortal(WriteCheck c) => c.Who.AuthType == "portal" && c.Who.Role == PortalRoles.Subcontractor;

    private static void Submission(WriteCheck c)
    {
        var s = (PortalSubmission)c.Entity;
        switch (c.Kind)
        {
            case WriteKind.Delete:
                throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "Portal submissions are kept as received - reject it instead of deleting.");
            case WriteKind.Insert:
                if (!IsPortal(c) && !c.Who.Trusted) throw new WriteRejectedException(403, ErrorCodes.Forbidden, "Submissions are created by subcontractors on the portal.");
                return;
            case WriteKind.Update:
            {
                var b = (PortalSubmission)c.Before!;
                var changed = PermissionGuard.ChangedFields(b, s).ToList();
                if (IsPortal(c))
                {
                    // the portal only attaches the statement document right after the upload
                    if (changed.Except(new[] { nameof(PortalSubmission.StatementDocumentId), nameof(PortalSubmission.StatementSha256), "UpdatedAt", "UpdatedBy", "RowVersion" }).Any() || b.StatementDocumentId != 0)
                        throw new WriteRejectedException(403, ErrorCodes.Forbidden, "A submission cannot be changed after it was sent - send a new one.");
                    return;
                }
                var content = changed.Except(ReviewFields).ToList();
                if (content.Count > 0) throw new WriteRejectedException(409, ErrorCodes.AppendOnly, $"The submitted content cannot change ({string.Join(", ", content)}).");
                if (b.Status != s.Status)
                {
                    if (!PortalSubmissionStatus.CanMove(b.Status, s.Status))
                        throw new WriteRejectedException(409, ErrorCodes.InvalidTransition, $"Submission #{s.Id} cannot go from {b.Status} to {s.Status}.");
                    if (s.Status == PortalSubmissionStatus.Rejected && string.IsNullOrWhiteSpace(s.Reason))
                        throw new WriteRejectedException(422, ErrorCodes.BadRequest, "A rejection needs a reason (the subcontractor sees it).");
                    if (!(c.Who.Can(Permissions.UploadStatements) || c.Who.Can(Permissions.EditData)))
                        throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} ({c.Who.Role}) may not review portal submissions.");
                    s.ReviewedBy = c.Who.User;
                    s.ReviewedAt = c.Clock();
                }
                return;
            }
        }
    }

    private static void Message(WriteCheck c)
    {
        var m = (PortalMessage)c.Entity;
        switch (c.Kind)
        {
            case WriteKind.Delete:
                throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "Portal messages are never deleted.");
            case WriteKind.Insert:
                if (IsPortal(c))
                {
                    if (m.Direction != PortalMessageDirections.FromSubcontractor) throw new WriteRejectedException(403, ErrorCodes.Forbidden, "Bad message direction.");
                    return;
                }
                if (c.Who.Trusted) return;
                m.Direction = PortalMessageDirections.ToSubcontractor;
                m.From = c.Who.User;
                m.SentAt = c.Clock();
                m.ReadAt = null;
                m.Company = (m.Company ?? "").Trim().ToUpperInvariant();
                if (m.Company.Length == 0 || string.IsNullOrWhiteSpace(m.Body)) throw new WriteRejectedException(400, ErrorCodes.BadRequest, "A message needs a company and a text.");
                return;
            case WriteKind.Update:
            {
                var changed = PermissionGuard.ChangedFields(c.Before!, m).Except(new[] { nameof(PortalMessage.ReadAt), "UpdatedAt", "UpdatedBy", "RowVersion" }).ToList();
                if (changed.Count > 0) throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "A sent message cannot change.");
                return;
            }
        }
    }
}
