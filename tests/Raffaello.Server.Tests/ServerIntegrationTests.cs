using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Raffaello.Core;
using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Remote;
using Raffaello.Core.Seed;
using Raffaello.Core.Settings;
using Raffaello.Server.Backup;
using Raffaello.Server.Data;

namespace Raffaello.Server.Tests;

public sealed class ServerIntegrationTests
{
    private static ClaimLine Claim(string sub, double qty, string room = "P2-106", string stage = "1ST FIX", string item = "POWER") =>
        new() { Building = Buildings.Branded, Subcontractor = sub, InvoiceNo = 1, Room = room, Stage = stage, Item = item, Qty = qty, Source = "MANUAL", EnteredAt = DateTime.Now };

    // ------------------------------------------------------------------ ledger: the last units

    [PgFact]
    public async Task Concurrent_claims_for_the_last_units_exactly_one_wins()
    {
        await using var srv = await TestServer.StartAsync();
        using (var admin = srv.Client("admin", Roles.Admin))
        {
            admin.Insert(new RoomQty { Building = Buildings.Branded, Room = "P2-106", Stage = "1ST FIX", Item = "POWER", Qty = 10 });
            admin.Insert(Claim("ROOTS", 7));
        }

        // 12 users race for the last 3 units, each through its own client and connection
        var clients = Enumerable.Range(1, 12).Select(i => srv.Client("qs" + i)).ToList();
        using var start = new ManualResetEventSlim(false);
        var tasks = clients.Select((c, i) => Task.Run(() =>
        {
            start.Wait();
            try { c.Insert(Claim("SUB" + i, 3)); return "OK"; }
            catch (RemainingExceededException) { return "BLOCKED"; }
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r == "OK"));
        Assert.Equal(11, results.Count(r => r == "BLOCKED"));
        using var check = srv.Client("auditor", Roles.Reviewer);
        var claims = check.All<ClaimLine>();
        Assert.Equal(10, claims.Sum(c => c.Qty));
        Assert.Equal(0, LedgerRules.Balance(check.All<RoomQty>(), claims, "P2-106", "1ST FIX", "POWER").Remaining, 6);
        foreach (var c in clients) c.Dispose();
    }

    [PgFact]
    public async Task Over_claim_with_a_reason_posts_and_ledger_is_append_only()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        qs.Insert(new RoomQty { Building = Buildings.Branded, Room = "P3-103", Stage = "2ND FIX", Item = "LIGHT", Qty = 5 });
        var ex = Assert.Throws<RemainingExceededException>(() => qs.Insert(Claim("ROOTS", 6, "P3-103", "2ND FIX", "LIGHT")));
        Assert.Contains("exceeds remaining", ex.Message);

        var over = Claim("ROOTS", 6, "P3-103", "2ND FIX", "LIGHT");
        over.IsOver = true; over.OverReason = "site instruction SI-12";
        qs.Insert(over);
        Assert.True(over.Id > 0);

        var del = Assert.Throws<RemoteRejectedException>(() => qs.Delete(over));
        Assert.Equal(ErrorCodes.AppendOnly, del.Error.Code);
        over.Qty = 1;
        var upd = Assert.Throws<RemoteRejectedException>(() => qs.Update(over));
        Assert.Equal(ErrorCodes.AppendOnly, upd.Error.Code);

        // check decisions on the line are allowed (they do not change the claimed quantity)
        var fresh = qs.Get<ClaimLine>(over.Id)!;
        fresh.HeightNote = "checked on site";
        qs.Update(fresh);
        Assert.Equal(2, fresh.RowVersion);
    }

    // ------------------------------------------------------------------ concurrency + audit

    [PgFact]
    public async Task RowVersion_conflict_returns_409_with_current_row_and_who_changed_it()
    {
        await using var srv = await TestServer.StartAsync();
        using var a = srv.Client("ahmed");
        using var b = srv.Client("bilal");
        var room = a.Insert(new Room { Building = Buildings.Hotel, Level = "L02", Code = "L02-201", RoomType = "KING" });

        var mine = b.Get<Room>(room.Id)!;
        room.AreaType = "APARTMENT";
        a.Update(room, "area type set");
        mine.AreaType = "BOH";
        var ex = Assert.Throws<ConcurrencyException>(() => b.Update(mine));
        Assert.Equal("ahmed", ex.ChangedBy);
        var current = RemoteJson.Deserialize<Room>((string)ex.Data["current"]!)!;
        Assert.Equal("APARTMENT", current.AreaType);
        Assert.Equal(2, current.RowVersion);

        // raw HTTP: 409 + ErrorDto
        using var http = srv.Http("bilal");
        var req = new WriteRequestDto { Ops = { new WriteOpDto { Op = WriteOps.Update, Table = "Rooms", Entity = JsonSerializer.SerializeToElement(mine, RemoteJson.Options) } } };
        var resp = await http.PostAsync(ApiRoutes.Write, new StringContent(RemoteJson.Serialize(req), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var err = RemoteJson.Deserialize<ErrorDto>(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal(ErrorCodes.Conflict, err.Code);
        Assert.Equal("ahmed", err.ChangedBy);
        Assert.NotNull(err.Current);
    }

    [PgFact]
    public async Task Every_change_writes_an_audit_row_with_who_when_old_and_new()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs.user");
        var sub = qs.Insert(new Subcontractor { Name = "ROOTS", Trade = "ELEC" }, "Added ROOTS");
        sub.Contact = "site office";
        qs.Update(sub, "Contact changed");
        qs.Delete(sub, "Removed ROOTS");

        await using var c = new NpgsqlConnection(srv.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Action", "User", "Machine", "Summary", "Changes", "OldJson", "NewJson", "At" FROM "AuditLog" WHERE "TableName" = 'Subcontractors' ORDER BY "Id" """, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string Action, string User, string Machine, string Summary, string Changes, string? Old, string? New, DateTime At)>();
        while (await r.ReadAsync())
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetDateTime(7)));

        Assert.Equal(new[] { "INSERT", "UPDATE", "DELETE" }, rows.Select(x => x.Action));
        Assert.All(rows, x => { Assert.Equal("qs.user", x.User); Assert.Equal("PC-qs.user", x.Machine); Assert.True(x.At > DateTime.Now.AddMinutes(-5)); });
        Assert.Contains("\"site office\"", rows[1].Changes);
        Assert.Contains("\"Contact\"", rows[1].Changes);
        Assert.Contains("\"Contact\":\"\"", rows[1].Old);
        Assert.Contains("\"Contact\":\"site office\"", rows[1].New);
        Assert.NotNull(rows[2].Old);
        Assert.Equal("Removed ROOTS", rows[2].Summary);
        Assert.Contains(qs.RecentAudit(10), a => a.Summary == "Contact changed");
    }

    [PgFact]
    public async Task Same_request_id_is_written_once()
    {
        await using var srv = await TestServer.StartAsync();
        using var http = srv.Http("qs");
        var req = new WriteRequestDto { Ops = { new WriteOpDto { Op = WriteOps.Insert, Table = "Subcontractors", Entity = JsonSerializer.SerializeToElement(new Subcontractor { Name = "ELAF" }, RemoteJson.Options) } } };
        var body = RemoteJson.Serialize(req);
        var r1 = RemoteJson.Deserialize<WriteResponseDto>(await (await http.PostAsync(ApiRoutes.Write, new StringContent(body, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync())!;
        var r2 = RemoteJson.Deserialize<WriteResponseDto>(await (await http.PostAsync(ApiRoutes.Write, new StringContent(body, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync())!;
        Assert.False(r1.Replayed);
        Assert.True(r2.Replayed);
        Assert.Equal(r1.Results[0].Id, r2.Results[0].Id);
        using var qs = srv.Client("qs");
        Assert.Single(qs.All<Subcontractor>());
    }

    [PgFact]
    public async Task Batch_with_temporary_ids_links_children_to_the_new_parent_atomically()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        var header = new SubInvoice { Subcontractor = "ROOTS", ContractNo = "SUB-TEST-01", InvoiceNo = 3, CreatedAt = DateTime.Now };
        var lines = Enumerable.Range(1, 3).Select(i => new SubInvoiceLine { RowOrder = i, ItemNo = i.ToString(), CurrQty = i * 10 }).ToList();
        qs.Batch(w =>
        {
            w.Insert(header);
            foreach (var l in lines) l.SubInvoiceId = header.Id;
            w.InsertMany(lines);
        }, "invoice saved");
        Assert.True(header.Id > 0);
        Assert.All(lines, l => { Assert.True(l.Id > 0); Assert.Equal(header.Id, l.SubInvoiceId); });
        Assert.Equal(3, qs.All<SubInvoiceLine>().Count(l => l.SubInvoiceId == header.Id));

        // a failing op rolls the whole batch back
        var stale = qs.Get<SubInvoice>(header.Id)!;
        stale.RowVersion = 99;
        Assert.Throws<ConcurrencyException>(() => qs.Batch(w => { w.Insert(new SubInvoiceLine { SubInvoiceId = header.Id, ItemNo = "X" }); w.Update(stale); }, "bad"));
        Assert.Equal(3, qs.All<SubInvoiceLine>().Count);
    }

    // ------------------------------------------------------------------ roles + approvals

    [PgFact]
    public async Task Roles_are_enforced_and_invoice_needs_checked_and_approved_stamps_before_submission()
    {
        await using var srv = await TestServer.StartAsync();
        using var site = srv.Client("site.eng", Roles.Site);
        using var qs = srv.Client("qs");
        using var reviewer = srv.Client("reviewer", Roles.Reviewer);

        Assert.Throws<PermissionDeniedException>(() => site.Insert(new Room { Code = "X1" }));
        site.Insert(new SiteStatement { Subcontractor = "ROOTS", StatementNo = "ST-1", Direction = "IN", At = DateTime.Now });
        Assert.Throws<PermissionDeniedException>(() => qs.Insert(new InvoiceTemplateRow { ContractNo = "C1", RowOrder = 1 }));
        Assert.Throws<PermissionDeniedException>(() => reviewer.Insert(new SubInvoice { Subcontractor = "ROOTS" }));
        Assert.Throws<PermissionDeniedException>(() => qs.ClearAll());

        var inv = qs.Insert(new SubInvoice { Subcontractor = "ROOTS", ContractNo = "SUB-TEST-01", InvoiceNo = 1, CreatedAt = DateTime.Now });
        inv.Status = SubInvoiceStatus.Submitted;
        var noStamp = Assert.Throws<RemoteRejectedException>(() => qs.Update(inv));
        Assert.Equal(ErrorCodes.ApprovalRequired, noStamp.Error.Code);
        inv.Status = SubInvoiceStatus.Draft;

        Assert.Throws<PermissionDeniedException>(() => qs.Stamp(inv, ApprovalStages.Approved));      // QS cannot approve
        Assert.Throws<RemoteRejectedException>(() => reviewer.Stamp(inv, ApprovalStages.Approved));  // not checked yet
        var chk = qs.Stamp(inv, ApprovalStages.Checked, "quantities checked");
        Assert.Equal("qs", chk.By);
        var apr = reviewer.Stamp(inv, ApprovalStages.Approved);
        Assert.Equal(Roles.Reviewer, apr.Role);

        inv.Status = SubInvoiceStatus.Submitted; inv.AconexWorkflowNo = "WF-000123";
        qs.Update(inv);
        var stamps = qs.Stamps(inv);
        Assert.Equal(new[] { ApprovalStages.Checked, ApprovalStages.Approved, ApprovalStages.Submitted }, stamps.Select(s => s.Stage));
        Assert.All(stamps, s => Assert.True(s.At > DateTime.Now.AddMinutes(-5)));

        // head office approves -> locked; the reviewer may record it, nobody may edit afterwards
        var fromReviewer = reviewer.Get<SubInvoice>(inv.Id)!;
        fromReviewer.Status = SubInvoiceStatus.Approved; fromReviewer.Locked = true; fromReviewer.ApprovedAt = DateTime.Now;
        reviewer.Update(fromReviewer);
        fromReviewer.Notes = "late edit";
        var locked = Assert.Throws<RemoteRejectedException>(() => qs.Update(fromReviewer));
        Assert.Equal(ErrorCodes.Locked, locked.Error.Code);
        Assert.Equal(4, qs.Stamps(inv).Count);
    }

    [PgFact]
    public async Task Editing_an_approved_draft_makes_the_approval_stale()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        using var reviewer = srv.Client("reviewer", Roles.Reviewer);
        var inv = qs.Insert(new SubInvoice { Subcontractor = "ROOTS", ContractNo = "C", InvoiceNo = 2, CreatedAt = DateTime.Now });
        qs.Stamp(inv, ApprovalStages.Checked);
        reviewer.Stamp(inv, ApprovalStages.Approved);
        inv.Discount = 500;
        qs.Update(inv);
        inv.Status = SubInvoiceStatus.Submitted;
        var ex = Assert.Throws<RemoteRejectedException>(() => qs.Update(inv));
        Assert.Contains("changed after it was approved", ex.Message);
    }

    // ------------------------------------------------------------------ live notifications

    [PgFact]
    public async Task SignalR_notifies_the_other_client_who_changed_what()
    {
        await using var srv = await TestServer.StartAsync();
        using var writer = srv.Client("writer");
        using var watcher = srv.Client("watcher");
        Assert.True(await watcher.WaitForHubAsync(TimeSpan.FromSeconds(15)));
        Assert.True(await writer.WaitForHubAsync(TimeSpan.FromSeconds(15)));
        var got = new TaskCompletionSource<ChangeNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var own = 0;
        watcher.RemoteChanged += n => { var x = n.FirstOrDefault(c => c.Table == "Rooms"); if (x != null) got.TrySetResult(x); };
        writer.RemoteChanged += _ => Interlocked.Increment(ref own);

        var room = writer.Insert(new Room { Code = "L05-501", Level = "L05" }, "Room L05-501 added");
        var notice = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("writer", notice.By);
        Assert.Equal(room.Id, notice.Id);
        Assert.Equal(1, notice.Version);
        Assert.Equal("INSERT", notice.Action);
        Assert.Equal("Room L05-501 added", notice.Summary);
        await Task.Delay(300);
        Assert.Equal(0, own);   // a client is not notified of its own writes
    }

    // ------------------------------------------------------------------ offline

    [PgFact]
    public async Task Offline_writes_are_queued_and_replayed_with_temp_ids_and_conflicts_surface()
    {
        await using var srv = await TestServer.StartAsync();
        using var other = srv.Client("other");
        var shared = other.Insert(new Room { Code = "L01-101", Level = "L01", RoomType = "KING" });
        var untouched = other.Insert(new Room { Code = "L01-102", Level = "L01" });

        var net = new FlakyHandler();
        using var me = srv.Client("me", handler: net);
        Assert.True(me.IsOnline);
        Assert.Equal(2, me.All<Room>().Count);          // fills the offline cache

        net.Down = true;
        var mineShared = me.All<Room>().First(r => r.Id == shared.Id);
        Assert.False(me.IsOnline);
        mineShared.Zone = "EAST"; mineShared.RoomType = "KING-ACC";
        me.Update(mineShared, "zone set offline");
        var mineUntouched = me.All<Room>().First(r => r.Id == untouched.Id);
        mineUntouched.Zone = "WEST";
        me.Update(mineUntouched);
        var newRoom = me.Insert(new Room { Code = "L01-103", Level = "L01" });
        Assert.True(newRoom.Id < 0);                    // temporary id until the server gives the real one
        newRoom.Zone = "NORTH";
        me.Update(newRoom);                              // edit of an offline-inserted row
        var header = new SubInvoice { Subcontractor = "ROOTS", ContractNo = "C", InvoiceNo = 7, CreatedAt = DateTime.Now };
        me.Batch(w => { w.Insert(header); w.Insert(new SubInvoiceLine { SubInvoiceId = header.Id, ItemNo = "12", CurrQty = 4 }); }, "draft offline");
        me.LogEvent("NOTE", "worked offline");
        Assert.Equal(6, me.PendingWrites);
        Assert.Contains(me.All<Room>(), r => r.Code == "L01-103");      // visible in the cache while offline
        Assert.Throws<InvalidOperationException>(() => me.ClearAll());

        // meanwhile someone else edits the same room on the server
        var theirs = other.Get<Room>(shared.Id)!;
        theirs.RoomType = "KING-THEIRS"; theirs.Level = "L01A";
        other.Update(theirs);

        net.Down = false;
        Assert.True(me.CheckNow());
        Assert.Equal(0, me.PendingWrites);
        var conflict = Assert.Single(me.Conflicts);
        Assert.Equal(ConflictKinds.RowVersion, conflict.Kind);
        Assert.Equal(shared.Id, conflict.RowId);
        Assert.Equal("other", conflict.ChangedBy);

        var rooms = other.All<Room>();
        Assert.Equal("WEST", rooms.Single(r => r.Id == untouched.Id).Zone);
        var inserted = rooms.Single(r => r.Code == "L01-103");
        Assert.Equal("NORTH", inserted.Zone);
        Assert.Equal(2, inserted.RowVersion);
        var inv = other.All<SubInvoice>().Single();
        Assert.Equal(inv.Id, other.All<SubInvoiceLine>().Single().SubInvoiceId);
        Assert.Contains(other.RecentAudit(50), a => a.Summary == "worked offline");

        // merge: my Zone (only I changed it), RoomType changed by both -> pick mine, Level only they changed -> theirs
        var plan = me.MergePlan(conflict);
        var zone = plan.Single(f => f.Name == nameof(Room.Zone));
        Assert.Equal(MergeSide.Mine, zone.Use);
        var type = plan.Single(f => f.Name == nameof(Room.RoomType));
        Assert.True(type.IsConflict);
        type.Use = MergeSide.Mine;
        Assert.Equal(MergeSide.Theirs, plan.Single(f => f.Name == nameof(Room.Level)).Use);
        me.Merge(conflict, plan);
        Assert.Empty(me.Conflicts);
        var merged = other.Get<Room>(shared.Id)!;
        Assert.Equal("EAST", merged.Zone);
        Assert.Equal("KING-ACC", merged.RoomType);
        Assert.Equal("L01A", merged.Level);
        Assert.Equal(3, merged.RowVersion);
    }

    [PgFact]
    public async Task Offline_claim_that_no_longer_fits_becomes_a_remaining_conflict()
    {
        await using var srv = await TestServer.StartAsync();
        using var other = srv.Client("other");
        other.Insert(new RoomQty { Building = Buildings.Branded, Room = "P4-04", Stage = "1ST FIX", Item = "DATA", Qty = 9 });
        var net = new FlakyHandler();
        using var me = srv.Client("me", handler: net);
        me.All<ClaimLine>();
        net.Down = true;
        me.All<ClaimLine>();
        var mine = Claim("ABRAG", 5, "P4-04", "1ST FIX", "DATA");
        me.Insert(mine);
        other.Insert(Claim("ROOTS", 6, "P4-04", "1ST FIX", "DATA"));
        net.Down = false;
        Assert.True(me.CheckNow());
        var c = Assert.Single(me.Conflicts);
        Assert.Equal(ConflictKinds.Remaining, c.Kind);
        Assert.Single(other.All<ClaimLine>());

        me.KeepMine(c, "confirmed on site");
        var posted = other.All<ClaimLine>().Single(x => x.Subcontractor == "ABRAG");
        Assert.True(posted.IsOver);
        Assert.Equal("confirmed on site", posted.OverReason);
        Assert.Empty(me.Conflicts);
    }

    [PgFact]
    public async Task Store_opens_offline_from_the_cache_and_keep_theirs_drops_the_change()
    {
        await using var srv = await TestServer.StartAsync();
        using var other = srv.Client("other");
        var room = other.Insert(new Room { Code = "B1-01" });
        var net = new FlakyHandler();
        var cacheDir = Path.Combine(srv.Folder, "shared-cache");
        var settings = new RemoteSettings { Mode = DataSources.Server, ServerUrl = srv.Url, UseWindowsAuth = false, Token = srv.Token("me", Roles.Qs), CacheFolder = cacheDir };
        using (var first = new RemoteProjectStore(settings, "me", "PC-me")) { first.EnsureSchema(); first.All<Room>(); }

        net.Down = true;
        using var me = new RemoteProjectStore(settings, "me", "PC-me", net) { ReconnectInterval = TimeSpan.FromHours(1) };
        me.EnsureSchema();                               // no exception: opens offline from the cache
        Assert.False(me.IsOnline);
        Assert.Equal("me", me.User);
        var r = me.All<Room>().Single();
        r.Zone = "MINE";
        me.Update(r);
        var theirs = other.Get<Room>(room.Id)!; theirs.Zone = "THEIRS"; other.Update(theirs);
        net.Down = false;
        Assert.True(me.CheckNow());
        var c = Assert.Single(me.Conflicts);
        me.KeepTheirs(c);
        Assert.Empty(me.Conflicts);
        Assert.Equal("THEIRS", me.All<Room>().Single().Zone);

        // a brand-new PC with no cache and no server cannot open
        var empty = new RemoteSettings { Mode = DataSources.Server, ServerUrl = "http://127.0.0.1:1", UseWindowsAuth = false, CacheFolder = Path.Combine(srv.Folder, "empty-cache"), TimeoutSeconds = 5 };
        using var none = new RemoteProjectStore(empty, "x");
        Assert.Throws<ServerUnavailableException>(() => none.EnsureSchema());
    }

    // ------------------------------------------------------------------ same semantics as SQLite: ProjectService on the server

    [PgFact]
    public async Task ProjectService_runs_unchanged_against_the_server_store()
    {
        await using var srv = await TestServer.StartAsync(requireInternalApproval: false);
        srv.Token("qs", Roles.Qs);
        var settingsPath = Path.Combine(srv.Folder, "server.json");
        new RemoteSettings { Mode = DataSources.Server, ServerUrl = srv.Url, UseWindowsAuth = false, Token = srv.Token("qs", Roles.Qs), CacheFolder = Path.Combine(srv.Folder, "ps-cache") }.Save(settingsPath);
        DataSourceFactory.SettingsPath = settingsPath;
        try
        {
            var app = new AppSettings { UserName = "qs", SeedDemoData = true };
            var project = new ProjectService(app, DataSourceFactory.Create);
            project.Initialize();
            Assert.False(app.SeedDemoData);              // never seed demo data into the shared server
            Assert.IsType<RemoteProjectStore>(project.Store);
            Assert.True(project.Snapshot.IsEmpty);

            // seed through a trusted server-side store, then use the workflow through the client
            var pg = new PgStore(NpgsqlDataSource.Create(srv.ConnectionString), StoreIdentity.System("seeder"));
            new DemoSeeder(DateTime.Today).Seed(pg);
            pg.Insert(new RoomQty { Building = Buildings.Branded, Room = "P2-106", Stage = "1ST FIX", Item = "POWER", Qty = 4 });
            project.Reload();
            Assert.False(project.Snapshot.IsEmpty);
            Assert.NotEmpty(project.Chain);

            var check = project.Workflow.AddClaim(Claim("ROOTS", 3));
            Assert.True(check.CanPost);
            var blocked = project.Workflow.AddClaim(Claim("ABRAG", 2));
            Assert.False(blocked.CanPost);               // client-side check on the fresh snapshot
            Assert.Equal(1, project.Workflow.Balance("P2-106", "1ST FIX", "POWER").Remaining, 6);

            var wir = project.Snapshot.Wirs.First();
            project.ApproveWir(wir, true);
            Assert.Equal("qs", project.Snapshot.Wirs.First(w => w.Id == wir.Id).UpdatedBy);
            project.Store.SetMeta("ProjectStart", "2026-01-01");
            Assert.Equal("2026-01-01", project.Store.GetMeta("ProjectStart"));
            project.Heartbeat("Ledger");
            Assert.Empty(project.OthersOnline(TimeSpan.FromMinutes(1)));
        }
        finally { DataSourceFactory.SettingsPath = null; DataSourceFactory.Create(new AppSettings { DataFilePath = Path.Combine(srv.Folder, "local.db") }); }
    }

    // ------------------------------------------------------------------ migration local -> server

    [PgFact]
    public async Task Migration_from_local_sqlite_has_a_dry_run_keeps_ids_and_is_idempotent()
    {
        await using var srv = await TestServer.StartAsync();
        var file = Path.Combine(srv.Folder, "local.db");
        var local = new Db(file, "mohamed", "LAPTOP");
        local.EnsureSchema();
        new DemoSeeder(DateTime.Today).Seed(local);
        var localRooms = local.All<Room>();
        var localWirLines = local.All<WirLine>();
        using var admin = srv.Client("admin", Roles.Admin);

        var dry = LocalToServerMigrator.Plan(local, admin);
        Assert.True(dry.DryRun);
        Assert.False(dry.Blocked);
        Assert.True(dry.ToCopy > 100);
        Assert.Empty(admin.All<Room>());              // nothing written
        Assert.Contains("DRY RUN", dry.ToText());

        var run = LocalToServerMigrator.Run(local, admin);
        Assert.Equal(dry.ToCopy, run.Copied);
        Assert.True(run.AuditRows > 0);
        var serverRooms = admin.All<Room>();
        Assert.Equal(localRooms.Select(r => (r.Id, r.Code)), serverRooms.Select(r => (r.Id, r.Code)));
        Assert.Equal(localWirLines.Select(l => (l.Id, l.WirId, l.LineId)), admin.All<WirLine>().Select(l => (l.Id, l.WirId, l.LineId)));

        var again = LocalToServerMigrator.Plan(local, admin);
        Assert.Equal(0, again.ToCopy);
        Assert.False(again.Blocked);
        var rerun = LocalToServerMigrator.Run(local, admin);
        Assert.Equal(0, rerun.Copied);
        Assert.Equal(0, rerun.AuditRows);

        // ids continue after the migrated ones
        var next = admin.Insert(new Room { Code = "NEW-1" });
        Assert.True(next.Id > localRooms.Max(r => r.Id));

        // another data file is refused (would mix two projects) unless forced
        var other = new Db(Path.Combine(srv.Folder, "other.db"), "x");
        other.EnsureSchema();
        other.Insert(new Room { Code = "OTHER" });
        Assert.True(LocalToServerMigrator.Plan(other, admin).Blocked);

        // only ADMIN may migrate
        using var qs = srv.Client("qs");
        Assert.Throws<PermissionDeniedException>(() => LocalToServerMigrator.Run(local, qs, force: true));
    }

    // ------------------------------------------------------------------ documents

    [PgFact]
    public async Task Documents_are_stored_on_the_share_with_sha256_and_a_size_limit()
    {
        await using var srv = await TestServer.StartAsync(maxDocumentBytes: 64 * 1024);
        using var site = srv.Client("site", Roles.Site);
        var src = Path.Combine(srv.Folder, "statement.pdf");
        await File.WriteAllBytesAsync(src, Enumerable.Range(0, 20000).Select(i => (byte)(i % 251)).ToArray());
        var info = site.UploadDocument(src, "SITE STATEMENT", "SiteStatements", 5);
        Assert.Equal(64, info.Sha256.Length);
        Assert.Equal(20000, info.Size);
        Assert.Equal("site", info.UploadedBy);
        Assert.True(File.Exists(Directory.GetFiles(Path.Combine(srv.Folder, "docs"), "*statement.pdf", SearchOption.AllDirectories).Single()));

        var again = site.UploadDocument(src, "SITE STATEMENT", "SiteStatements", 5);
        Assert.Equal(info.Id, again.Id);                  // same content + link: no duplicate

        var dest = Path.Combine(srv.Folder, "down", "copy.pdf");
        site.DownloadDocument(info.Id, dest);
        Assert.Equal(await File.ReadAllBytesAsync(src), await File.ReadAllBytesAsync(dest));
        Assert.Single(site.Api.Documents("SiteStatements", 5));

        var big = Path.Combine(srv.Folder, "big.bin");
        await File.WriteAllBytesAsync(big, new byte[100 * 1024]);
        var ex = Assert.Throws<RemoteRejectedException>(() => site.UploadDocument(big));
        Assert.Equal(413, ex.Status);

        using var http = srv.Http("site", Roles.Site);
        var verify = await http.GetFromJsonAsync<JsonElement>($"{ApiRoutes.Documents}/{info.Id}/verify");
        Assert.True(verify.GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------------ auth

    [PgFact]
    public async Task Unauthenticated_calls_are_refused_and_tokens_can_be_revoked()
    {
        await using var srv = await TestServer.StartAsync();
        using var anon = new HttpClient { BaseAddress = new Uri(srv.Url) };
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(ApiRoutes.Health)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(ApiRoutes.Tables + "/Rooms")).StatusCode);
        var bad = await anon.PostAsync(ApiRoutes.Login, new StringContent(RemoteJson.Serialize(new LoginRequest { UserName = "nobody", Password = "x" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        srv.Users.Create("Sara", Roles.Reviewer, "correct-horse");
        var settings = new RemoteSettings { ServerUrl = srv.Url };
        var login = DataSourceFactory.SignIn(settings, "sara", "correct-horse");
        Assert.Equal(Roles.Reviewer, login.Role);
        Assert.False(settings.UseWindowsAuth);
        var (ok, msg) = DataSourceFactory.Test(settings);
        Assert.True(ok, msg);
        Assert.Contains("REVIEWER", msg);

        using var http = new HttpClient { BaseAddress = new Uri(srv.Url) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(ApiRoutes.Me)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync(ApiRoutes.Logout, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(ApiRoutes.Me)).StatusCode);

        using var admin = srv.Client("admin", Roles.Admin);
        var u = admin.Api.CreateUser(new UserDto { UserName = "omar", Role = Roles.Qs, Password = "longenough1" });
        Assert.Equal(Roles.Qs, u.Role);
        using var qs = srv.Client("qs");
        Assert.Throws<PermissionDeniedException>(() => qs.Api.Users());
    }

    // ------------------------------------------------------------------ backups

    [PgFact]
    public async Task Backup_and_restore_round_trip_with_pg_dump()
    {
        await using var srv = await TestServer.StartAsync();
        using (var qs = srv.Client("qs"))
            for (var i = 0; i < 5; i++) qs.Insert(new Room { Code = "R" + i });
        var opt = new ServerOptions { BackupFolder = Path.Combine(srv.Folder, "backups") };
        var runner = new BackupRunner(srv.ConnectionString, opt, srv.Folder);
        if (!File.Exists(runner.Tool("pg_dump")) && !runner.Tool("pg_dump").Contains(Path.DirectorySeparatorChar)) return;   // pg_dump not installed: covered by the CLI on the server
        var file = runner.Backup();
        Assert.True(new FileInfo(file).Length > 0);

        var target = TestPg.CreateDatabase();
        try
        {
            new BackupRunner(target, opt, srv.Folder).Restore(file);
            await using var c = new NpgsqlConnection(target);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("""SELECT COUNT(*) FROM "Rooms" """, c);
            Assert.Equal(5L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally { TestPg.DropDatabase(target); }
    }
}
