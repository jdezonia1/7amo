using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Core.Trust;
using Raffaello.Core.Variations;
using Raffaello.Server.Auth;
using Raffaello.Server.Data;
using static Raffaello.Server.Data.PgMap;

namespace Raffaello.Server.Trust;

/// <summary>
/// [trust] Signing keys + record signatures (tables, guard) and the authoritative audit hash chain (sealer, verification
/// endpoints, anchor file).
/// </summary>
public sealed class TrustServerModule : IServerModule
{
    public string Name => "Trust";
    public IEnumerable<Type> EntityTypes => TrustEntities.All;
    public IEnumerable<IWriteGuard> Guards(IServiceProvider services) => new IWriteGuard[] { new TrustGuard() };

    /// <summary>The chain of the running server (set when the endpoints are mapped).</summary>
    public static ServerAuditChain? Chain { get; private set; }

    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var sp = api.ServiceProvider;
        var ds = sp.GetRequiredService<NpgsqlDataSource>();
        var opt = sp.GetRequiredService<ServerOptions>();
        var env = sp.GetRequiredService<IWebHostEnvironment>();
        var chain = new ServerAuditChain(ds, Path.Combine(opt.ResolveBackupFolder(env.ContentRootPath), "audit-anchors.log"));
        chain.EnsureSchema();
        Chain = chain;
        StartSealer(chain, sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Raffaello.AuditChain"));

        var json = RemoteJson.Options;
        IResult Json(object v) => Results.Text(JsonSerializer.Serialize(v, v.GetType(), json), "application/json");

        api.MapGet(TrustRoutes.AuditVerify, () => Json(chain.Verify()));
        api.MapPost(TrustRoutes.AuditVerify, (AuditVerifyRequest? req) => Json(chain.Verify(req?.Anchors)));
        api.MapGet(TrustRoutes.AuditHead, () => { chain.Seal(); return Json(chain.Head() ?? new AuditChainHead()); });
        api.MapGet(TrustRoutes.AuditChain, (long? afterSeq, int? take) =>
        {
            chain.Seal();
            return Json(chain.Stream(Math.Max(0, afterSeq ?? 0), Math.Clamp(take ?? 2000, 1, 10000)).ToList());
        });
        api.MapPost(TrustRoutes.AuditSeal, () => Json(new { sealedRows = chain.Seal() }));
        api.MapPost(TrustRoutes.AuditAnchor, (HttpContext ctx) =>
        {
            var who = ctx.Identity();
            if (!who.Can(Permissions.ManageSettings)) throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{who.User} ({who.Role}) may not write audit anchors.");
            chain.Seal();
            return Json(chain.WriteAnchor(force: true) ?? new AuditAnchor());
        });

        // server-side check of every signature of a record (the client can also verify by itself)
        api.MapGet(TrustRoutes.SignaturesVerify, (HttpContext ctx, string table, long id, Api.StoreFactory f) =>
        {
            var store = f.For(ctx);
            var payload = TrustService.CurrentPayloadSha(store, table, id);
            var keys = store.All<UserSigningKey>();
            string? pkg = table == SignedRecords.InvoiceTable ? store.Get<SubInvoice>(id)?.PackageSha256.ToLowerInvariant() : null;
            var checks = store.All<RecordSignature>().Where(s => s.RecordTable == table && s.RecordId == id).OrderBy(s => s.SignedAtUtc, StringComparer.Ordinal)
                .Select(s => SignatureService.Verify(s, payload, string.IsNullOrEmpty(pkg) ? null : pkg, keys)).ToList();
            return Json(checks);
        });
    }

    private static void StartSealer(ServerAuditChain chain, IHostApplicationLifetime life, ILogger log)
    {
        Timer? timer = null;
        life.ApplicationStarted.Register(() =>
        {
            timer = new Timer(_ =>
            {
                try { chain.Seal(); chain.WriteAnchor(); }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException) { log.LogWarning(ex, "Audit chain sealing failed (will retry)"); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        });
        life.ApplicationStopping.Register(() =>
        {
            timer?.Dispose();
            try { chain.Seal(); chain.WriteAnchor(force: true); } catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or ObjectDisposedException) { /* shutting down */ }
        });
    }
}

public sealed class AuditVerifyRequest
{
    public List<AuditAnchor> Anchors { get; set; } = new();
}

/// <summary>[trust] Routes of the trust module (also used by the client).</summary>
public static class TrustRoutes
{
    public const string AuditVerify = TrustApi.AuditVerify;
    public const string AuditHead = TrustApi.AuditHead;
    public const string AuditChain = TrustApi.AuditChain;
    public const string AuditSeal = TrustApi.AuditSeal;
    public const string AuditAnchor = TrustApi.AuditAnchor;
    public const string SignaturesVerify = TrustApi.SignaturesVerify;
}

/// <summary>
/// Server rules for keys and signatures. Keys: only your own, valid P-256, fingerprint must match; withdrawing is the only
/// change allowed (owner or ADMIN); never deleted. Signatures: append-only; you sign as yourself with your registered active
/// key, in the role you have, with the permission the purpose needs; the server recomputes the record's canonical content
/// from the database and refuses a signature over anything else.
/// </summary>
public sealed class TrustGuard : IWriteGuard
{
    public const string SignatureInvalid = "signature_invalid";

    public void Check(WriteCheck c)
    {
        if (c.Type == typeof(UserSigningKey)) CheckKey(c);
        else if (c.Type == typeof(RecordSignature)) CheckSignature(c);
    }

    public void AfterWrite(WriteCheck c)
    {
        if (c.Type != typeof(UserSigningKey) || c.Kind != WriteKind.Insert) return;
        var k = (UserSigningKey)c.Entity;
        var n = c.Tx.Exec($"""
            UPDATE {Q("UserSigningKeys")} SET "RevokedAt" = @at, "RevokedBy" = @by, "RowVersion" = "RowVersion" + 1, "UpdatedAt" = @at, "UpdatedBy" = @by
            WHERE lower("UserName") = lower(@u) AND "Id" <> @id AND "RevokedAt" IS NULL
            """, ("at", c.Clock()), ("by", c.Who.User), ("u", k.UserName), ("id", k.Id));
        if (n > 0)
            c.Tx.Exec("""
                INSERT INTO "AuditLog" ("At", "User", "Machine", "TableName", "RowId", "Action", "Summary", "Changes")
                VALUES (@at, @u, @m, 'UserSigningKeys', @id, 'REVOKE', @s, '')
                """, ("at", c.Clock()), ("u", c.Who.User), ("m", c.Who.Machine), ("id", k.Id), ("s", $"{n} earlier signing key(s) of {k.UserName} withdrawn by the new key {KeyFingerprint.Short(k.Fingerprint)}"));
    }

    private static void CheckKey(WriteCheck c)
    {
        var k = (UserSigningKey)c.Entity;
        switch (c.Kind)
        {
            case WriteKind.Delete:
                throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "Signing keys are never deleted (old signatures need them) - withdraw the key instead.");
            case WriteKind.Update:
            {
                var changed = PermissionGuard.ChangedFields(c.Before!, k).Except(new[] { nameof(UserSigningKey.RevokedAt), nameof(UserSigningKey.RevokedBy), "UpdatedAt", "UpdatedBy", "RowVersion" }).ToList();
                if (changed.Count > 0) throw new WriteRejectedException(409, ErrorCodes.AppendOnly, $"A registered key cannot change ({string.Join(", ", changed)}); register a new key.");
                if (((UserSigningKey)c.Before!).RevokedAt != null && k.RevokedAt == null) throw new WriteRejectedException(409, ErrorCodes.InvalidTransition, "A withdrawn key cannot be re-activated.");
                if (!c.Who.Trusted && !string.Equals(k.UserName, c.Who.User, StringComparison.OrdinalIgnoreCase) && !c.Who.Can(Permissions.ManageUsers))
                    throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} may only withdraw their own key.");
                return;
            }
            case WriteKind.Insert:
            {
                if (c.Who.Trusted) return;
                if (SignatureRules.CheckKey(k, c.Who.User) is { } err) throw new WriteRejectedException(422, SignatureInvalid, err);
                // the role and time come from the server, not from the client
                k.Role = c.Who.Role;
                k.RegisteredAt = c.Clock();
                k.RevokedAt = null;
                k.RevokedBy = "";
                var dup = c.Tx.Scalar($"""SELECT 1 FROM {Q("UserSigningKeys")} WHERE "Fingerprint" = @f LIMIT 1""", ("f", k.Fingerprint));
                if (dup != null) throw new WriteRejectedException(409, ErrorCodes.Conflict, $"The key {KeyFingerprint.Short(k.Fingerprint)} is already registered.");
                return;
            }
        }
    }

    private static void CheckSignature(WriteCheck c)
    {
        if (c.Kind != WriteKind.Insert)
            throw new WriteRejectedException(409, ErrorCodes.AppendOnly, "Signatures are append-only: they cannot be changed or deleted.");
        if (c.Who.Trusted) return;
        var s = (RecordSignature)c.Entity;
        var need = s.Purpose switch
        {
            SignaturePurposes.Approved => new[] { Permissions.ApproveInvoices },
            SignaturePurposes.Checked => new[] { Permissions.CheckInvoices },
            SignaturePurposes.Prepared => new[] { Permissions.PrepareInvoices, Permissions.EditData },
            _ => throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"Unknown signature purpose '{s.Purpose}'."),
        };
        if (!need.Any(c.Who.Can))
            throw new WriteRejectedException(403, ErrorCodes.Forbidden, $"{c.Who.User} ({c.Who.Role}) may not sign as {s.Purpose} (needs {string.Join(" or ", need)}).");
        if (!string.Equals(s.SignerRole, c.Who.Role, StringComparison.OrdinalIgnoreCase))
            throw new WriteRejectedException(422, SignatureInvalid, $"The signature says role {s.SignerRole} but {c.Who.User} is {c.Who.Role}.");
        var keys = c.Tx.Query<UserSigningKey>($"""SELECT * FROM {Q("UserSigningKeys")} WHERE lower("UserName") = lower(@u)""", ("u", s.SignerUser));
        var payload = CurrentPayloadSha(c.Tx, s.RecordTable, s.RecordId);
        if (SignatureRules.CheckSignature(s, c.Who.User, keys, payload) is { } err) throw new WriteRejectedException(422, SignatureInvalid, err);
        if (s.Kind == SignatureKinds.Package)
        {
            var pkg = c.Tx.Scalar($"""SELECT "PackageSha256" FROM {Q("SubInvoices")} WHERE "Id" = @id""", ("id", s.RecordId)) as string ?? "";
            if (!string.Equals(pkg, s.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new WriteRejectedException(422, SignatureInvalid, "The package signed is not the package recorded on the invoice revision - rebuild / reload and sign again.");
        }
        s.Machine = c.Who.Machine;
    }

    /// <summary>The canonical content of the record as stored now, read inside the write transaction.</summary>
    private static string? CurrentPayloadSha(PgTx tx, string table, long id)
    {
        switch (table)
        {
            case SignedRecords.InvoiceTable:
            {
                var h = (SubInvoice?)tx.Query(typeof(SubInvoice), $"""SELECT * FROM {Q("SubInvoices")} WHERE "Id" = @id""", ("id", id)).FirstOrDefault();
                if (h is null) return null;
                var lines = tx.Query(typeof(SubInvoiceLine), $"""SELECT * FROM {Q("SubInvoiceLines")} WHERE "SubInvoiceId" = @id""", ("id", id)).Cast<SubInvoiceLine>();
                return CanonicalJson.Sha256(SignedRecords.InvoicePayload(h, lines));
            }
            case SignedRecords.VariationTable:
            {
                var v = (Variation?)tx.Query(typeof(Variation), $"""SELECT * FROM {Q("Variations")} WHERE "Id" = @id""", ("id", id)).FirstOrDefault();
                if (v is null) return null;
                var lines = tx.Query(typeof(VariationLine), $"""SELECT * FROM {Q("VariationLines")} WHERE "VariationId" = @id""", ("id", id)).Cast<VariationLine>();
                return CanonicalJson.Sha256(SignedRecords.VariationPayload(v, lines));
            }
            default:
                throw new WriteRejectedException(400, ErrorCodes.BadRequest, $"{table} records cannot be signed.");
        }
    }
}
