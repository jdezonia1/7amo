using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Portal;
using Raffaello.Core.Remote;
using Raffaello.Core.Trust;

namespace Raffaello.Server.Tests;

/// <summary>[trust] Subcontractor portal: strict company isolation, submission -> QS inbox -> ledger, file and rate limits.</summary>
public sealed class TrustPortalTests
{
    private static readonly JsonSerializerOptions Json = RemoteJson.Options;
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% marked-up drawing\n");
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3 };

    private sealed class Project : IAsyncDisposable
    {
        public required TestServer Srv { get; init; }
        public required RemoteProjectStore Qs { get; init; }
        public async ValueTask DisposeAsync() { Qs.Dispose(); await Srv.DisposeAsync(); }
    }

    /// <summary>Two subcontractors (ALPHA on P2-101 / P2-102, BETA on P3-201) with claims, invoices, messages and portal accounts.</summary>
    private static async Task<Project> Setup()
    {
        var srv = await TestServer.StartAsync();
        var qs = srv.Client("qs");
        qs.InsertMany(new[]
        {
            new Room { Building = Buildings.Branded, Code = "P2-101", Level = "L2", RoomType = "2BR" },
            new Room { Building = Buildings.Branded, Code = "P2-102", Level = "L2", RoomType = "2BR" },
            new Room { Building = Buildings.Branded, Code = "P3-201", Level = "L3", RoomType = "3BR" },
        }, "rooms");
        qs.InsertMany(new[]
        {
            new RoomQty { Building = Buildings.Branded, Room = "P2-101", Stage = "1ST FIX", Item = "POWER", Qty = 36 },
            new RoomQty { Building = Buildings.Branded, Room = "P2-102", Stage = "1ST FIX", Item = "POWER", Qty = 36 },
            new RoomQty { Building = Buildings.Branded, Room = "P3-201", Stage = "1ST FIX", Item = "POWER", Qty = 40 },
        }, "project qty");
        qs.Insert(new ClaimLine { Building = Buildings.Branded, Subcontractor = "ALPHA", InvoiceNo = 1, Room = "P2-101", Stage = "1ST FIX", Item = "POWER", Qty = 10, Source = "MANUAL", EnteredAt = DateTime.Now });
        qs.Insert(new ClaimLine { Building = Buildings.Branded, Subcontractor = "BETA", InvoiceNo = 1, Room = "P3-201", Stage = "1ST FIX", Item = "POWER", Qty = 15, Source = "MANUAL", EnteredAt = DateTime.Now });
        qs.Insert(new ClaimLine { Building = Buildings.Branded, Subcontractor = "BETA", InvoiceNo = 1, Room = "P2-101", Stage = "1ST FIX", Item = "POWER", Qty = 4, Source = "MANUAL", EnteredAt = DateTime.Now });
        foreach (var (sub, status, reason) in new[] { ("ALPHA", SubInvoiceStatus.Rejected, "INV-01: P2-101 claimed twice"), ("BETA", SubInvoiceStatus.Approved, "") })
            qs.Batch(w =>
            {
                var h = w.Insert(new SubInvoice { Subcontractor = sub, ContractNo = "SUB-" + sub, InvoiceNo = 1, Status = status, RejectionReason = reason, CreatedAt = DateTime.Now });
                w.Insert(new SubInvoiceLine { SubInvoiceId = h.Id, RowOrder = 1, ItemNo = "1", Rate = 50, StagePct = 0.9, CurrQty = 10, CumQty = 10 });
            }, "invoice " + sub);
        var inbox = new PortalInbox(qs);
        inbox.SaveCompany(new PortalCompanySetting { Name = "alpha", DisplayName = "Alpha Electrical", Building = Buildings.Branded, RoomScope = "P2-*" });
        inbox.SaveCompany(new PortalCompanySetting { Name = "BETA", DisplayName = "Beta Contracting", Building = Buildings.Branded });
        inbox.Send("ALPHA", "Welcome", "Please use the portal for statements.");
        inbox.Send("BETA", "Private to beta", "Beta's confidential message.");
        using var admin = srv.Http("admin", Roles.Admin);
        foreach (var (user, company) in new[] { ("alpha.site", "ALPHA"), ("beta.site", "BETA") })
        {
            var r = await admin.PostAsJsonAsync(PortalRoutes.AdminAccounts.TrimStart('/'), new PortalAccountDto { UserName = user, DisplayName = user, Company = company, Password = "Portal-pass-123" }, Json);
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        }
        return new Project { Srv = srv, Qs = qs };
    }

    private static async Task<HttpClient> Portal(TestServer srv, string user)
    {
        var h = new HttpClient { BaseAddress = new Uri(srv.Url) };
        var r = await h.PostAsJsonAsync(PortalRoutes.Login.TrimStart('/'), new PortalLoginRequest { UserName = user, Password = "Portal-pass-123" }, Json);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var login = (await r.Content.ReadFromJsonAsync<PortalLoginResponse>(Json))!;
        h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return h;
    }

    private static async Task<T> Get<T>(HttpClient h, string route)
    {
        var r = await h.GetAsync(route.TrimStart('/'));
        Assert.True(r.IsSuccessStatusCode, $"{route}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static MultipartFormDataContent Form(string note, params (string Name, byte[] Data)[] files)
    {
        var f = new MultipartFormDataContent { { new StringContent(note), "note" } };
        foreach (var (name, data) in files) f.Add(new ByteArrayContent(data), "files", name);
        return f;
    }

    private static async Task<long> Submit(HttpClient h, params (string Name, byte[] Data)[] files)
    {
        var r = await h.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("test", files));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("Id").GetInt64();
    }

    /// <summary>Fills the generated template: POWER qty on the first room row.</summary>
    private static byte[] Filled(byte[] template, double qty)
    {
        using var ms = new MemoryStream(template);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet("STATEMENT");
        var powerCol = ws.Row(5).CellsUsed().First(c => c.GetString() == "POWER").Address.ColumnNumber;
        var row = ws.RowsUsed().First(r => r.Cell(1).GetString() == "P2-101|1ST FIX").RowNumber();
        ws.Cell(row, powerCol).Value = qty;
        using var outMs = new MemoryStream();
        wb.SaveAs(outMs);
        return outMs.ToArray();
    }

    [PgFact]
    public async Task Each_subcontractor_sees_only_their_own_company()
    {
        await using var p = await Setup();
        using var alpha = await Portal(p.Srv, "alpha.site");
        using var beta = await Portal(p.Srv, "beta.site");

        var me = await Get<PortalMeDto>(alpha, PortalRoutes.Me);
        Assert.Equal("ALPHA", me.Company);
        Assert.Equal("Alpha Electrical", me.CompanyDisplayName);
        Assert.Equal(1, me.UnreadMessages);

        var claims = await Get<List<PortalClaimDto>>(alpha, PortalRoutes.Claims);
        var claim = Assert.Single(claims);
        Assert.Equal("P2-101", claim.Room);

        var invoices = await Get<List<PortalInvoiceDto>>(alpha, PortalRoutes.Invoices);
        var inv = Assert.Single(invoices);
        Assert.Equal("rejected", inv.Status);
        Assert.Equal("INV-01: P2-101 claimed twice", inv.Reason);
        Assert.Equal(450, inv.CurrentAmount);
        Assert.Equal("approved", Assert.Single(await Get<List<PortalInvoiceDto>>(beta, PortalRoutes.Invoices)).Status);

        // remaining: ALPHA's scope (P2-*) only; other subcontractors' claims reduce the remaining but are never named
        var rem = await Get<List<PortalRemainingDto>>(alpha, PortalRoutes.Remaining);
        Assert.Equal(new[] { "P2-101", "P2-102" }, rem.Select(r => r.Room).ToArray());
        var r101 = rem[0];
        Assert.Equal(10, r101.ClaimedByYou);
        Assert.Equal(36 - 10 - 4, r101.Remaining);
        var raw = await alpha.GetStringAsync(PortalRoutes.Remaining.TrimStart('/'));
        Assert.DoesNotContain("BETA", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("P3-201", raw);

        var msgs = await Get<List<PortalMessageDto>>(alpha, PortalRoutes.Messages);
        Assert.Equal("Welcome", Assert.Single(msgs).Subject);
        Assert.DoesNotContain("confidential", await alpha.GetStringAsync(PortalRoutes.Messages.TrimStart('/')));

        // BETA's submission and files are invisible to ALPHA (404, not 403: no hint they exist)
        var betaSub = await Submit(beta, ("drawing.pdf", Pdf));
        var betaFiles = await Get<List<PortalFileDto>>(beta, $"{PortalRoutes.Submissions}/{betaSub}/files");
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"{PortalRoutes.Submissions.TrimStart('/')}/{betaSub}/files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"{PortalRoutes.Files.TrimStart('/')}/{betaFiles[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await beta.GetAsync($"{PortalRoutes.Files.TrimStart('/')}/{betaFiles[0].Id}")).StatusCode);
        Assert.Empty(await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions));
        var spoof = await alpha.PostAsJsonAsync(PortalRoutes.Messages.TrimStart('/'), new PortalSendMessageRequest { Body = "hi", SubmissionId = betaSub }, Json);
        Assert.Equal(HttpStatusCode.NotFound, spoof.StatusCode);

        // a portal token opens nothing of the internal API ...
        foreach (var route in new[] { "api/v1/tables/ClaimLines", "api/v1/tables/SubInvoices", "api/v1/me", $"api/v1/documents/{betaFiles[0].Id}", "api/v1/audit", "hubs/changes/negotiate?negotiateVersion=1" })
        {
            var r = route.StartsWith("hubs", StringComparison.Ordinal) ? await alpha.PostAsync(route, null) : await alpha.GetAsync(route);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await alpha.PostAsJsonAsync("api/v1/write", new WriteRequestDto(), Json)).StatusCode);
        // ... and an app token opens nothing of the portal
        using var qsHttp = p.Srv.Http("qs");
        Assert.Equal(HttpStatusCode.Unauthorized, (await qsHttp.GetAsync(PortalRoutes.Claims.TrimStart('/'))).StatusCode);
        // an internal user still cannot use the portal-admin endpoints without the permission
        using var site = p.Srv.Http("site-user", Roles.Site);
        Assert.Equal(HttpStatusCode.Forbidden, (await site.GetAsync(PortalRoutes.AdminAccounts.TrimStart('/'))).StatusCode);
    }

    [PgFact]
    public async Task Statement_goes_from_the_portal_to_the_qs_inbox_and_into_the_ledger()
    {
        await using var p = await Setup();
        using var alpha = await Portal(p.Srv, "alpha.site");
        using var beta = await Portal(p.Srv, "beta.site");

        var tr = await alpha.GetAsync(PortalRoutes.Template.TrimStart('/'));
        Assert.True(tr.IsSuccessStatusCode, await tr.Content.ReadAsStringAsync());
        Assert.Equal("ST-ALPHA-01.xlsx", tr.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var template = await tr.Content.ReadAsByteArrayAsync();
        var filled = Filled(template, 8);

        // BETA cannot send ALPHA's statement as its own
        var wrong = await beta.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("ST-ALPHA-01.xlsx", filled)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Contains("another company", await wrong.Content.ReadAsStringAsync());

        var id = await Submit(alpha, ("ST-ALPHA-01.xlsx", filled), ("P2-101 markup.pdf", Pdf), ("site photo.jpg", Jpg));
        var mine = await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions);
        Assert.Equal("SUBMITTED", mine[0].GetProperty("Status").GetString());
        Assert.Equal(1, mine[0].GetProperty("StatementLines").GetInt32());
        Assert.Equal(3, mine[0].GetProperty("Files").GetInt32());

        // the QS inbox (live in the app)
        var inbox = new PortalInbox(p.Qs);
        var sub = Assert.Single(inbox.Submissions(openOnly: true));
        Assert.Equal("ALPHA", sub.Company);
        Assert.Equal(3, inbox.Files(sub).Count);
        sub = inbox.MarkUnderReview(sub);
        Assert.Equal("UNDER_REVIEW", (await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions))[0].GetProperty("Status").GetString());
        var folder = Path.Combine(p.Srv.Folder, "inbox");
        var (preview, path) = inbox.PreviewStatement(sub, ProjectSnapshot.Load(p.Qs), invoiceNo: 2, Buildings.Branded, folder);
        Assert.False(preview.IsDuplicate);
        var line = Assert.Single(preview.Claims);
        Assert.Equal(8, line.Qty);
        Assert.Equal("STATEMENT", line.Source);
        var posted = inbox.Import(sub, preview, path, invoiceNo: 2);
        Assert.Equal(1, posted);
        Assert.Contains(p.Qs.All<ClaimLine>(), c => c.Subcontractor == "ALPHA" && c.InvoiceNo == 2 && c.Qty == 8 && c.StatementNo == "ST-ALPHA-01");

        var after = (await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions))[0];
        Assert.Equal("IMPORTED", after.GetProperty("Status").GetString());
        Assert.Equal(1, after.GetProperty("ImportedLines").GetInt32());
        Assert.Contains(await Get<List<PortalMessageDto>>(alpha, PortalRoutes.Messages), m => m.Subject.Contains("ST-ALPHA-01"));
        Assert.Equal(2, (await Get<List<PortalClaimDto>>(alpha, PortalRoutes.Claims)).Count);

        // sending the same statement again: flagged as a duplicate at upload; rejected by the QS with a reason the sub sees
        var again = await Submit(alpha, ("ST-ALPHA-01.xlsx", filled));
        var dup = inbox.Submissions(openOnly: true).Single(s => s.Id == again);
        Assert.Contains("DUPLICATE", dup.Findings);
        inbox.Reject(dup, "Already imported as INV 2 - send only new work.");
        var rejected = (await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions)).Single(s => s.GetProperty("Id").GetInt64() == again);
        Assert.Equal("REJECTED", rejected.GetProperty("Status").GetString());
        Assert.Equal("Already imported as INV 2 - send only new work.", rejected.GetProperty("Reason").GetString());

        // message from the subcontractor reaches the QS
        var sent = await alpha.PostAsJsonAsync(PortalRoutes.Messages.TrimStart('/'), new PortalSendMessageRequest { Subject = "Question", Body = "When is INV 2 certified?" }, Json);
        Assert.True(sent.IsSuccessStatusCode);
        Assert.Contains(inbox.Messages("ALPHA"), m => m.Direction == PortalMessageDirections.FromSubcontractor && m.Body.Contains("INV 2"));

        // everything is in the hash-chained audit log
        var audit = p.Qs.RecentAudit(500);
        Assert.Contains(audit, a => a.Action == "PORTAL-LOGIN" && a.User == "portal:alpha.site");
        Assert.Contains(audit, a => a.TableName == "PortalSubmissions" && a.User == "portal:alpha.site");
        Assert.Contains(audit, a => a.TableName == "Documents" && a.User == "portal:alpha.site");
        Assert.True(IntegrityChecker.VerifyOnServer(p.Qs.Api, Array.Empty<AuditAnchor>()).Ok);
    }

    [PgFact]
    public async Task Server_rules_protect_submissions_and_messages()
    {
        await using var p = await Setup();
        using var alpha = await Portal(p.Srv, "alpha.site");
        var id = await Submit(alpha, ("drawing.pdf", Pdf));
        var inbox = new PortalInbox(p.Qs);
        var sub = p.Qs.Get<PortalSubmission>(id)!;

        Assert.Equal(ErrorCodes.Forbidden, Assert.Throws<PermissionDeniedException>(() => p.Qs.Insert(new PortalSubmission { Company = "ALPHA", Status = "SUBMITTED" })).Error.Code);
        var edit = p.Qs.Get<PortalSubmission>(id)!;
        edit.Note = "changed by QS";
        Assert.Equal(ErrorCodes.AppendOnly, Assert.Throws<RemoteRejectedException>(() => p.Qs.Update(edit)).Error.Code);
        var noReason = p.Qs.Get<PortalSubmission>(id)!;
        noReason.Status = PortalSubmissionStatus.Rejected;
        Assert.Throws<RemoteRejectedException>(() => p.Qs.Update(noReason));
        Assert.Equal(ErrorCodes.AppendOnly, Assert.Throws<RemoteRejectedException>(() => p.Qs.Delete(p.Qs.Get<PortalSubmission>(id)!)).Error.Code);
        Assert.Throws<ArgumentException>(() => inbox.Reject(sub, " "));
        var done = inbox.Reject(sub, "Drawing is not marked.");
        Assert.Equal("qs", done.ReviewedBy);
        var reopen = p.Qs.Get<PortalSubmission>(id)!;
        reopen.Status = PortalSubmissionStatus.Imported;
        Assert.Equal(ErrorCodes.InvalidTransition, Assert.Throws<RemoteRejectedException>(() => p.Qs.Update(reopen)).Error.Code);

        // a message written in the app is always "to the subcontractor" from the signed-in user
        var forged = p.Qs.Insert(new PortalMessage { Company = "alpha", Direction = PortalMessageDirections.FromSubcontractor, From = "alpha.site", Body = "fake" });
        Assert.Equal(PortalMessageDirections.ToSubcontractor, p.Qs.Get<PortalMessage>(forged.Id)!.Direction);
        Assert.Equal("qs", p.Qs.Get<PortalMessage>(forged.Id)!.From);
        Assert.Equal("ALPHA", p.Qs.Get<PortalMessage>(forged.Id)!.Company);
    }

    [PgFact]
    public async Task Files_types_sizes_and_rates_are_limited()
    {
        await using var p = await Setup();
        using var alpha = await Portal(p.Srv, "alpha.site");
        var exe = await alpha.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("tool.exe", new byte[] { 0x4D, 0x5A, 1, 2 })));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, exe.StatusCode);
        var fakeJpg = await alpha.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("photo.jpg", Encoding.ASCII.GetBytes("<script>alert(1)</script>"))));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, fakeJpg.StatusCode);
        var notStatement = await alpha.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("statement.xlsx", new byte[] { 0x50, 0x4B, 3, 4, 0, 0, 0 })));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notStatement.StatusCode);
        var big = new byte[25 * 1024 * 1024 + 10];
        Pdf.CopyTo(big, 0);
        var tooBig = await alpha.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("huge.pdf", big)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Empty(await Get<List<JsonElement>>(alpha, PortalRoutes.Submissions));   // nothing stored for refused uploads

        // at most 10 submissions per hour per account
        for (var i = 0; i < 10; i++) await Submit(alpha, ($"photo{i}.jpg", Jpg));
        var eleventh = await alpha.PostAsync(PortalRoutes.Submissions.TrimStart('/'), Form("x", ("photo.jpg", Jpg)));
        Assert.Equal((HttpStatusCode)429, eleventh.StatusCode);
        Assert.NotNull(eleventh.Headers.RetryAfter);

        // sign-in lockout after 5 wrong passwords (same throttle as the app accounts)
        using var anon = new HttpClient { BaseAddress = new Uri(p.Srv.Url) };
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(PortalRoutes.Login.TrimStart('/'), new PortalLoginRequest { UserName = "beta.site", Password = "wrong-password-1" }, Json)).StatusCode);
        var locked = await anon.PostAsJsonAsync(PortalRoutes.Login.TrimStart('/'), new PortalLoginRequest { UserName = "beta.site", Password = "Portal-pass-123" }, Json);
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);

        // disabling the account ends the session at once
        using var admin = p.Srv.Http("admin", Roles.Admin);
        var acc = (await Get<List<PortalAccountDto>>(admin, PortalRoutes.AdminAccounts)).Single(a => a.UserName == "alpha.site");
        acc.Active = false;
        Assert.True((await admin.PutAsJsonAsync($"{PortalRoutes.AdminAccounts.TrimStart('/')}/{acc.Id}", acc, Json)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await alpha.GetAsync(PortalRoutes.Me.TrimStart('/'))).StatusCode);
    }

    [PgFact]
    public async Task The_portal_page_is_served_with_a_strict_policy()
    {
        await using var p = await Setup();
        using var anon = new HttpClient { BaseAddress = new Uri(p.Srv.Url) };
        var page = await anon.GetAsync("portal");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("SUBCONTRACTOR PORTAL", html);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        var js = await anon.GetStringAsync("portal/app.js");
        Assert.Contains("بوابة مقاولي الباطن", js);   // Arabic texts ship with the page
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("portal/app.css")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(PortalRoutes.Me.TrimStart('/'))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(PortalRoutes.Template.TrimStart('/'))).StatusCode);
    }
}
