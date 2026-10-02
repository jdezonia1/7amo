using Npgsql;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Core.Trust;

namespace Raffaello.Server.Tests;

/// <summary>[trust] Authoritative audit chain on the server, signing keys and signatures with server-side rules.</summary>
public sealed class TrustServerTests
{
    private static SigningKeyStore Keys() =>
        new(Path.Combine(Path.GetTempPath(), "raffaello-test-keys", Guid.NewGuid().ToString("N")), new PassphraseKeyProtector("pw") { Iterations = 1000 });

    private static SignerInfo Me(RemoteProjectStore c) => new(c.Me!.UserName, c.Me.DisplayName, c.Me.Role);

    private static void Sql(TestServer srv, string sql)
    {
        using var c = new NpgsqlConnection(srv.ConnectionString);
        c.Open();
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.ExecuteNonQuery();
    }

    private static SubInvoice Invoice(RemoteProjectStore qs)
    {
        SubInvoice? h = null;
        qs.Batch(w =>
        {
            h = w.Insert(new SubInvoice { Subcontractor = "SUB-A", ContractNo = "SUB-ELE-001", InvoiceNo = 1, CreatedAt = new DateTime(2026, 9, 30) });
            w.Insert(new SubInvoiceLine { SubInvoiceId = h.Id, RowOrder = 1, ItemNo = "1", BoqCode = "B6-01-01-00-1", Description = "Lighting point", Unit = "No", ContractQty = 100, Rate = 55, StagePct = 0.9, CurrQty = 40, CumQty = 40 });
        }, "test invoice");
        return qs.All<SubInvoice>().Single(i => i.InvoiceNo == 1);
    }

    [PgFact]
    public async Task Server_chain_seals_every_write_and_detects_tampering()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        for (var i = 0; i < 5; i++) qs.Insert(new Room { Building = Buildings.Branded, Code = $"P2-{i}" });
        qs.LogEvent("TEST", "hello");
        var anchors = new AuditAnchorStore(Path.Combine(srv.Folder, "anchors.json"));
        var checker = new IntegrityChecker(anchors);

        var rep = checker.Verify(qs);
        Assert.True(rep.Ok, rep.ToText());
        Assert.True(rep.RowsChecked >= 6);
        Assert.Equal(0, rep.Unsealed);
        var independent = checker.Verify(qs, independent: true);
        Assert.True(independent.Ok, independent.ToText());
        Assert.Equal(rep.LastSeq, independent.LastSeq);
        Assert.Equal(rep.LastHash, independent.LastHash);

        // someone with database access edits one audit row
        Sql(srv, """UPDATE "AuditLog" SET "Summary" = 'nothing to see' WHERE "ChainSeq" = 2""");
        var edited = checker.Verify(qs);
        Assert.False(edited.Ok);
        Assert.Contains(edited.Problems, p => p.Kind == AuditProblemKinds.Edited && p.Seq == 2);
        Assert.False(checker.Verify(qs, independent: true).Ok);

        // ... or deletes one
        Sql(srv, """DELETE FROM "AuditLog" WHERE "ChainSeq" = 4""");
        var deleted = IntegrityChecker.VerifyOnServer(qs.Api, Array.Empty<AuditAnchor>());
        Assert.Contains(deleted.Problems, p => p.Kind == AuditProblemKinds.Deleted && p.Seq == 4);
    }

    [PgFact]
    public async Task Rewriting_the_server_chain_is_caught_by_the_anchor_file()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        qs.Insert(new Room { Building = Buildings.Branded, Code = "P2-1" });
        qs.Insert(new Room { Building = Buildings.Branded, Code = "P2-2" });
        using (var http = srv.Http("admin", Roles.Admin))
        {
            var r = await http.PostAsync(TrustApi.AuditAnchor.TrimStart('/'), null);
            r.EnsureSuccessStatusCode();
        }
        Assert.True(File.Exists(Path.Combine(srv.Folder, "backups", "audit-anchors.log")));
        // drop the whole chain and let the sealer re-seal everything from scratch (a consistent but different chain)
        Sql(srv, """UPDATE "AuditLog" SET "Summary" = 'rewritten' WHERE "ChainSeq" = 1""");
        Sql(srv, """UPDATE "AuditLog" SET "ChainSeq" = NULL, "PrevHash" = NULL, "Hash" = NULL; DELETE FROM "AuditChainHead";""");
        var rep = IntegrityChecker.VerifyOnServer(qs.Api, Array.Empty<AuditAnchor>());
        Assert.False(rep.Ok);
        Assert.Contains(rep.Problems, p => p.Kind == AuditProblemKinds.AnchorMismatch);
    }

    [PgFact]
    public async Task Signatures_follow_roles_and_the_server_checks_the_signed_content()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        using var rev = srv.Client("rev", Roles.Reviewer);
        var inv = Invoice(qs);
        var qsKeys = Keys();
        var revKeys = Keys();

        // QS checks (allowed), but may not approve
        var qsSvc = new TrustService(qs, new RemoteTrustStore(qs), qsKeys);
        var chk = qsSvc.SignInvoice(inv, Me(qs), SignaturePurposes.Checked);
        Assert.True(chk.Id > 0);
        Assert.Throws<PermissionDeniedException>(() => qsSvc.SignInvoice(inv, Me(qs), SignaturePurposes.Approved));

        // the reviewer approves
        var revSvc = new TrustService(rev, new RemoteTrustStore(rev), revKeys);
        revSvc.SignInvoice(inv, Me(rev), SignaturePurposes.Approved);
        var checks = revSvc.Verify(SignedRecords.InvoiceTable, inv.Id);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(c.Ok, c.Message));
        Assert.Contains(checks, c => c.Message.Contains("approved by rev (REVIEWER)"));

        // keys carry the server's role, not what the client said
        Assert.All(qs.All<UserSigningKey>(), k => Assert.Equal(k.UserName == "qs" ? Roles.Qs : Roles.Reviewer, k.Role));

        // signing as someone else / claiming another role / with an unregistered key is refused by the server
        var payload = TrustService.CurrentPayloadSha(qs, SignedRecords.InvoiceTable, inv.Id)!;
        var asRev = SignatureService.Sign(qsKeys, new SignerInfo("rev", "Reviewer", Roles.Reviewer), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Checked, payload);
        var e1 = Assert.Throws<RemoteRejectedException>(() => qs.Insert(asRev));
        Assert.Equal(TrustGuardCodes.SignatureInvalid, e1.Error.Code);
        var roleLie = SignatureService.Sign(qsKeys, new SignerInfo("qs", "QS", Roles.Admin), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Checked, payload);
        Assert.Contains("role", Assert.Throws<RemoteRejectedException>(() => qs.Insert(roleLie)).Message);
        var unregistered = SignatureService.Sign(Keys(), Me(qs), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Checked, payload);
        Assert.Contains("not registered", Assert.Throws<RemoteRejectedException>(() => qs.Insert(unregistered)).Message);

        // a signature over content the server does not hold (client-side tampering of the payload hash)
        var stale = SignatureService.Sign(qsKeys, Me(qs), SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, "x", SignaturePurposes.Checked, new string('0', 64));
        Assert.Contains("changed since", Assert.Throws<RemoteRejectedException>(() => qs.Insert(stale)).Message);

        // append-only
        var stored = qs.All<RecordSignature>().First();
        stored.Purpose = SignaturePurposes.Approved;
        Assert.Equal(ErrorCodes.AppendOnly, Assert.Throws<RemoteRejectedException>(() => qs.Update(stored)).Error.Code);
        Assert.Equal(ErrorCodes.AppendOnly, Assert.Throws<RemoteRejectedException>(() => qs.Delete(qs.All<RecordSignature>().First())).Error.Code);

        // somebody else's key cannot be registered by me
        var other = Keys().GetOrCreate("rev");
        Assert.Throws<RemoteRejectedException>(() => qs.Insert(TrustService.KeyRow(other, new SignerInfo("rev", "x", Roles.Reviewer))));

        // a change of the invoice after approval: the server's own verification says CHANGED
        var line = qs.All<SubInvoiceLine>().Single();
        line.CurrQty = 41;
        qs.Update(line);
        using var http = srv.Http("rev", Roles.Reviewer);
        var json = await http.GetStringAsync($"{TrustApi.SignaturesVerify.TrimStart('/')}?table=SubInvoices&id={inv.Id}");
        var serverChecks = RemoteJson.Deserialize<List<SignatureCheck>>(json)!;
        Assert.All(serverChecks, c => Assert.Equal(SignatureVerdicts.Changed, c.Verdict));
    }

    [PgFact]
    public async Task New_key_withdraws_the_previous_one_on_the_server()
    {
        await using var srv = await TestServer.StartAsync();
        using var qs = srv.Client("qs");
        var inv = Invoice(qs);
        var keys = Keys();
        var svc = new TrustService(qs, new RemoteTrustStore(qs), keys);
        svc.SignInvoice(inv, Me(qs), SignaturePurposes.Prepared);
        keys.Create("qs");
        svc.SignInvoice(inv, Me(qs), SignaturePurposes.Checked);
        var all = qs.All<UserSigningKey>();
        Assert.Equal(2, all.Count);
        Assert.Single(all, k => k.RevokedAt is null);
        Assert.All(svc.Verify(SignedRecords.InvoiceTable, inv.Id), c => Assert.True(c.Ok, c.Message));
        Assert.True(IntegrityChecker.VerifyOnServer(qs.Api, Array.Empty<AuditAnchor>()).Ok);
    }
}

internal static class TrustGuardCodes
{
    public const string SignatureInvalid = Raffaello.Server.Trust.TrustGuard.SignatureInvalid;
}
