using Raffaello.Core.Data;
using Raffaello.Core.Domain;
using Raffaello.Core.Remote;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Trust;

/// <summary>Registered public keys and signatures. SQLite (data file) or the Raffaello server.</summary>
public interface ITrustStore
{
    List<UserSigningKey> Keys();
    List<RecordSignature> Signatures();
    /// <summary>Registers the key for the signer (idempotent); a new key withdraws the user's previous one.</summary>
    UserSigningKey RegisterKey(SigningKeyInfo key, SignerInfo signer);
    RecordSignature AddSignature(RecordSignature signature);
}

/// <summary>[trust] Entity types of the trust module (registered for the remote store, migration and the server).</summary>
public static class TrustEntities
{
    public static readonly Type[] All = { typeof(UserSigningKey), typeof(RecordSignature) };
    private static bool _registered;
    public static void RegisterAll()
    {
        if (_registered) return;
        foreach (var t in All) EntityMeta.Register(t);
        _registered = true;
    }
}

/// <summary>
/// Rules a new signature / key must pass - the same code runs in the desktop store (local data file) and in the server guard
/// (where it is authoritative: the server recomputes the payload from the stored record).
/// </summary>
public static class SignatureRules
{
    public static string? CheckKey(UserSigningKey k, string user)
    {
        if (!string.Equals(k.UserName, user, StringComparison.OrdinalIgnoreCase)) return $"{user} may only register their own signing key.";
        if (k.Algorithm != KeyFingerprint.Algorithm) return $"Only {KeyFingerprint.Algorithm} keys are accepted.";
        try
        {
            using var pk = KeyFingerprint.ImportPublic(k.PublicKey);
            if (pk.KeySize != 256) return "The key must be an ECDSA P-256 key.";
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return "The public key is not a valid ECDSA key."; }
        if (KeyFingerprint.Of(k.PublicKey) != k.Fingerprint) return "The key fingerprint does not match the public key.";
        return null;
    }

    /// <summary>Null when the signature can be stored; otherwise the reason.</summary>
    public static string? CheckSignature(RecordSignature s, string user, IEnumerable<UserSigningKey> keys, string? currentPayloadSha)
    {
        if (!string.Equals(s.SignerUser, user, StringComparison.OrdinalIgnoreCase)) return $"{user} may only sign as themself (the signature says {s.SignerUser}).";
        var key = keys.FirstOrDefault(k => k.Fingerprint == s.KeyFingerprint && string.Equals(k.UserName, s.SignerUser, StringComparison.OrdinalIgnoreCase));
        if (key is null) return $"The key {KeyFingerprint.Short(s.KeyFingerprint)} is not registered to {s.SignerUser} - register it first.";
        if (key.RevokedAt != null) return $"The key {KeyFingerprint.Short(s.KeyFingerprint)} was withdrawn on {key.RevokedAt:dd-MMM-yyyy}.";
        SignatureStatement st;
        try { st = SignatureStatement.Parse(s.Statement); } catch (FormatException ex) { return ex.Message; }
        if (!st.Matches(s)) return "The signature fields differ from the signed statement.";
        if (!SignatureService.VerifyStatement(s.Statement, s.Signature, key.PublicKey)) return "The signature does not verify with the registered key.";
        if (currentPayloadSha is null) return $"{s.RecordTable} #{s.RecordId} does not exist.";
        if (!string.Equals(currentPayloadSha, s.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            return $"{s.RecordTitle} changed since you opened it - reload and sign again.";
        return null;
    }
}

/// <summary>Trust tables in the shared data file (local mode).</summary>
public sealed class SqliteTrustStore : SideStore, ITrustStore
{
    private readonly IProjectStore _project;
    public SqliteTrustStore(Db db) : base(db.Path, db.User, db.Machine) { _project = db; EnsureSchema(); }

    protected override IEnumerable<Type> Tables => TrustEntities.All;
    protected override IEnumerable<string> Indexes => new[] { "CREATE INDEX IF NOT EXISTS IX_RecordSignatures_Rec ON RecordSignatures(RecordTable, RecordId);" };

    public List<UserSigningKey> Keys() => All<UserSigningKey>();
    public List<RecordSignature> Signatures() => All<RecordSignature>();

    public UserSigningKey RegisterKey(SigningKeyInfo key, SignerInfo signer)
    {
        var existing = Keys();
        var same = existing.FirstOrDefault(k => k.Fingerprint == key.Fingerprint && string.Equals(k.UserName, signer.User, StringComparison.OrdinalIgnoreCase));
        if (same != null) return same;
        var row = TrustService.KeyRow(key, signer);
        if (SignatureRules.CheckKey(row, User) is { } err) throw new InvalidOperationException(err);
        UserSigningKey? saved = null;
        Batch(b =>
        {
            foreach (var old in existing.Where(k => k.RevokedAt is null && string.Equals(k.UserName, signer.User, StringComparison.OrdinalIgnoreCase)))
            {
                old.RevokedAt = DateTime.Now; old.RevokedBy = User;
                b.Update(old);
            }
            saved = b.Insert(row);
        }, $"Signing key {KeyFingerprint.Short(key.Fingerprint)} registered for {signer.User}");
        return saved!;
    }

    public RecordSignature AddSignature(RecordSignature s)
    {
        var payload = TrustService.CurrentPayloadSha(_project, s.RecordTable, s.RecordId);
        if (SignatureRules.CheckSignature(s, User, Keys(), payload) is { } err) throw new InvalidOperationException(err);
        return Insert(s, $"{s.RecordTitle} {s.Purpose} - signed by {s.SignerUser} ({KeyFingerprint.Short(s.KeyFingerprint)})");
    }
}

/// <summary>Trust tables on the Raffaello server (the server guard re-checks every rule).</summary>
public sealed class RemoteTrustStore : ITrustStore
{
    private readonly RemoteProjectStore _r;
    static RemoteTrustStore() => TrustEntities.RegisterAll();
    public RemoteTrustStore(RemoteProjectStore r) => _r = r;

    public List<UserSigningKey> Keys() => _r.All<UserSigningKey>();
    public List<RecordSignature> Signatures() => _r.All<RecordSignature>();

    public UserSigningKey RegisterKey(SigningKeyInfo key, SignerInfo signer)
    {
        var same = Keys().FirstOrDefault(k => k.Fingerprint == key.Fingerprint && string.Equals(k.UserName, signer.User, StringComparison.OrdinalIgnoreCase));
        return same ?? _r.Insert(TrustService.KeyRow(key, signer), $"Signing key {KeyFingerprint.Short(key.Fingerprint)} registered for {signer.User}");
    }

    public RecordSignature AddSignature(RecordSignature s) =>
        _r.Insert(s, $"{s.RecordTitle} {s.Purpose} - signed by {s.SignerUser} ({KeyFingerprint.Short(s.KeyFingerprint)})");
}

/// <summary>Picks the trust store for the current data source (like the materials selector).</summary>
public sealed class TrustStoreSelector : ITrustStore
{
    private readonly Func<IProjectStore> _store;
    private IProjectStore? _for;
    private ITrustStore? _impl;
    public TrustStoreSelector(Func<IProjectStore> store) => _store = store;

    private ITrustStore Impl
    {
        get
        {
            var s = _store();
            if (!ReferenceEquals(s, _for) || _impl is null)
            {
                _impl = s switch
                {
                    RemoteProjectStore r => new RemoteTrustStore(r),
                    Db db => new SqliteTrustStore(db),
                    _ => throw new InvalidOperationException($"Signatures are not available for the data source {s.GetType().Name}."),
                };
                _for = s;
            }
            return _impl;
        }
    }

    public List<UserSigningKey> Keys() => Impl.Keys();
    public List<RecordSignature> Signatures() => Impl.Signatures();
    public UserSigningKey RegisterKey(SigningKeyInfo key, SignerInfo signer) => Impl.RegisterKey(key, signer);
    public RecordSignature AddSignature(RecordSignature signature) => Impl.AddSignature(signature);
}

/// <summary>
/// Sign-off workflow used by the app and the CLI: signs an invoice revision, its package or a variation with the user's key
/// (registering the public key on first use) and verifies every signature of a record against its current content.
/// </summary>
public sealed class TrustService
{
    private readonly IProjectStore _project;
    private readonly ITrustStore _trust;
    private readonly SigningKeyStore _keys;

    public TrustService(IProjectStore project, ITrustStore trust, SigningKeyStore keys) { _project = project; _trust = trust; _keys = keys; }

    public static UserSigningKey KeyRow(SigningKeyInfo key, SignerInfo signer) => new()
    {
        UserName = signer.User, DisplayName = signer.DisplayName, Role = signer.Role, Fingerprint = key.Fingerprint, PublicKey = key.PublicKey,
        Algorithm = key.Algorithm, Machine = key.Machine, RegisteredAt = DateTime.Now,
    };

    /// <summary>Generates the key on first use and makes sure it is registered.</summary>
    public UserSigningKey EnsureKey(SignerInfo signer) => _trust.RegisterKey(_keys.GetOrCreate(signer.User), signer);

    public RecordSignature SignInvoice(SubInvoice inv, SignerInfo signer, string purpose)
    {
        EnsureKey(signer);
        var payload = CurrentPayloadSha(_project, SignedRecords.InvoiceTable, inv.Id) ?? throw new InvalidOperationException($"Invoice #{inv.Id} not found.");
        var sig = SignatureService.Sign(_keys, signer, SignatureKinds.Invoice, SignedRecords.InvoiceTable, inv.Id, SignedRecords.InvoiceTitle(inv), purpose, payload);
        return _trust.AddSignature(sig);
    }

    /// <summary>Signs the invoice revision together with its last package (the package ZIP must still be the one recorded).</summary>
    public RecordSignature SignPackage(SubInvoice inv, SignerInfo signer, string purpose)
    {
        var current = _project.Get<SubInvoice>(inv.Id) ?? throw new InvalidOperationException($"Invoice #{inv.Id} not found.");
        if (string.IsNullOrEmpty(current.PackageSha256)) throw new InvalidOperationException($"{current.Title} has no package yet - build the package first.");
        if (current.PackageFile.Length > 0 && File.Exists(current.PackageFile) && SignatureService.FileSha256(current.PackageFile) != current.PackageSha256.ToLowerInvariant())
            throw new InvalidOperationException($"{Path.GetFileName(current.PackageFile)} no longer matches the SHA-256 recorded when it was built - rebuild it.");
        EnsureKey(signer);
        var payload = CurrentPayloadSha(_project, SignedRecords.InvoiceTable, inv.Id)!;
        var sig = SignatureService.Sign(_keys, signer, SignatureKinds.Package, SignedRecords.InvoiceTable, inv.Id, SignedRecords.InvoiceTitle(current), purpose, payload, current.PackageSha256);
        return _trust.AddSignature(sig);
    }

    public RecordSignature SignVariation(Variation v, SignerInfo signer, string purpose)
    {
        EnsureKey(signer);
        var payload = CurrentPayloadSha(_project, SignedRecords.VariationTable, v.Id) ?? throw new InvalidOperationException($"Variation #{v.Id} not found.");
        var sig = SignatureService.Sign(_keys, signer, SignatureKinds.Variation, SignedRecords.VariationTable, v.Id, $"{v.Type} {v.Number} {v.Title}".Trim(), purpose, payload);
        return _trust.AddSignature(sig);
    }

    /// <summary>Every signature of a record, checked against the record (and its package file when <paramref name="packagePath"/> is given or recorded).</summary>
    public List<SignatureCheck> Verify(string table, long id, string? packagePath = null)
    {
        var payload = CurrentPayloadSha(_project, table, id);
        var keys = _trust.Keys();
        string? pkgSha = null;
        if (packagePath != null) pkgSha = File.Exists(packagePath) ? SignatureService.FileSha256(packagePath) : null;
        else if (table == SignedRecords.InvoiceTable && _project.Get<SubInvoice>(id) is { } inv && inv.PackageSha256.Length > 0)
            pkgSha = inv.PackageFile.Length > 0 && File.Exists(inv.PackageFile) ? SignatureService.FileSha256(inv.PackageFile) : inv.PackageSha256.ToLowerInvariant();
        return _trust.Signatures().Where(s => s.RecordTable == table && s.RecordId == id).OrderBy(s => s.SignedAtUtc, StringComparer.Ordinal)
            .Select(s => SignatureService.Verify(s, payload, pkgSha, keys)).ToList();
    }

    /// <summary>
    /// Finds the signatures that cover a package file by its SHA-256 alone ("is this ZIP what was approved?"),
    /// checked against the current record.
    /// </summary>
    public List<SignatureCheck> VerifyPackageFile(string packagePath)
    {
        var sha = SignatureService.FileSha256(packagePath);
        var keys = _trust.Keys();
        return _trust.Signatures().Where(s => string.Equals(s.PackageSha256, sha, StringComparison.OrdinalIgnoreCase))
            .Select(s => SignatureService.Verify(s, CurrentPayloadSha(_project, s.RecordTable, s.RecordId), sha, keys)).ToList();
    }

    public List<RecordSignature> SignaturesOf(string table, long id) =>
        _trust.Signatures().Where(s => s.RecordTable == table && s.RecordId == id).OrderBy(s => s.SignedAtUtc, StringComparer.Ordinal).ToList();

    /// <summary>SHA-256 of the canonical content of a signable record as stored now (null when it does not exist).</summary>
    public static string? CurrentPayloadSha(IProjectStore store, string table, long id)
    {
        switch (table)
        {
            case SignedRecords.InvoiceTable:
            {
                var h = store.Get<SubInvoice>(id);
                if (h is null) return null;
                var lines = store.All<SubInvoiceLine>().Where(l => l.SubInvoiceId == id);
                return CanonicalJson.Sha256(SignedRecords.InvoicePayload(h, lines));
            }
            case SignedRecords.VariationTable:
            {
                List<Variation> vs;
                try { vs = store.All<Variation>(); }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.Reflection.TargetInvocationException) { return null; }
                var v = vs.FirstOrDefault(x => x.Id == id);
                if (v is null) return null;
                return CanonicalJson.Sha256(SignedRecords.VariationPayload(v, store.All<VariationLine>().Where(l => l.VariationId == id)));
            }
            default:
                throw new ArgumentException($"{table} records cannot be signed.");
        }
    }
}
