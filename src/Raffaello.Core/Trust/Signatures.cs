using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Raffaello.Core.Domain;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Trust;

/// <summary>A user's registered public signing key (the private key never leaves the user's PC).</summary>
public sealed class UserSigningKey : Entity
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Algorithm { get; set; } = KeyFingerprint.Algorithm;
    public string Machine { get; set; } = "";
    public DateTime RegisteredAt { get; set; }
    /// <summary>Set when the user replaced or withdrew the key. Signatures made before stay valid.</summary>
    public DateTime? RevokedAt { get; set; }
    public string RevokedBy { get; set; } = "";
}

/// <summary>
/// An approval / sign-off: a user signed the canonical content of a record (invoice revision, its package, a variation)
/// with their key. Append-only. The signed text (<see cref="Statement"/>) is stored as signed, so anyone can re-check it.
/// </summary>
public sealed class RecordSignature : Entity
{
    /// <summary>INVOICE / PACKAGE / VO.</summary>
    public string Kind { get; set; } = "";
    public string RecordTable { get; set; } = "";
    public long RecordId { get; set; }
    public string RecordTitle { get; set; } = "";
    /// <summary>PREPARED / CHECKED / APPROVED.</summary>
    public string Purpose { get; set; } = "";
    public string PayloadSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string SignerUser { get; set; } = "";
    public string SignerName { get; set; } = "";
    public string SignerRole { get; set; } = "";
    /// <summary>UTC, ISO 8601 with Z - the exact text inside the statement.</summary>
    public string SignedAtUtc { get; set; } = "";
    /// <summary>Local time, for lists.</summary>
    public DateTime SignedAt { get; set; }
    public string KeyFingerprint { get; set; } = "";
    public string Algorithm { get; set; } = Trust.KeyFingerprint.Algorithm;
    public string Statement { get; set; } = "";
    /// <summary>ECDSA P-256 / SHA-256 signature of the UTF-8 statement, IEEE P1363 (r||s), base64.</summary>
    public string Signature { get; set; } = "";
    public string Machine { get; set; } = "";
}

public static class SignatureKinds
{
    public const string Invoice = "INVOICE";
    public const string Package = "PACKAGE";
    public const string Variation = "VO";
    public static readonly string[] All = { Invoice, Package, Variation };
}

public static class SignaturePurposes
{
    public const string Prepared = "PREPARED";
    public const string Checked = "CHECKED";
    public const string Approved = "APPROVED";
    public static readonly string[] All = { Prepared, Checked, Approved };
}

/// <summary>
/// What exactly is signed for each kind of record: the business content in canonical JSON. Workflow fields that legitimately
/// change after approval (status, Aconex no., submission dates, package bookkeeping, row stamps) are left out, so submitting an
/// approved invoice does not "change" it; any change of a quantity, rate, BOQ code or description does.
/// </summary>
public static class SignedRecords
{
    public const string InvoiceTable = "SubInvoices";
    public const string VariationTable = "Variations";

    public static readonly HashSet<string> RowStamps = new(StringComparer.Ordinal) { nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy), nameof(Entity.RowVersion) };

    public static readonly HashSet<string> InvoiceWorkflowFields = new(StringComparer.Ordinal)
    {
        nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy), nameof(Entity.RowVersion),
        nameof(SubInvoice.Status), nameof(SubInvoice.AconexWorkflowNo), nameof(SubInvoice.RejectionReason), nameof(SubInvoice.SubmittedAt),
        nameof(SubInvoice.ApprovedAt), nameof(SubInvoice.Locked), nameof(SubInvoice.PackageFile), nameof(SubInvoice.PackageSha256),
        nameof(SubInvoice.PackageKind), nameof(SubInvoice.PackageBuiltAt), nameof(SubInvoice.PackageMissingWirs),
    };

    public static readonly HashSet<string> LineExcluded = new(StringComparer.Ordinal)
    {
        nameof(Entity.Id), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy), nameof(Entity.RowVersion), nameof(SubInvoiceLine.SubInvoiceId),
        nameof(VariationLine.VariationId),
    };

    public static readonly HashSet<string> VariationWorkflowFields = new(StringComparer.Ordinal)
    {
        nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy), nameof(Entity.RowVersion),
        nameof(Variation.Status), nameof(Variation.StatusChangedAt), nameof(Variation.SubmittedAt), nameof(Variation.DecidedAt),
        nameof(Variation.AconexWorkflowNo), nameof(Variation.ApprovedAmount),
    };

    public static string InvoicePayload(SubInvoice header, IEnumerable<SubInvoiceLine> lines)
    {
        var doc = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["format"] = "RAFFAELLO-INVOICE-1",
            ["header"] = Project(header, InvoiceWorkflowFields),
            ["lines"] = lines.OrderBy(l => l.RowOrder).ThenBy(l => l.BoqCode, StringComparer.Ordinal).ThenBy(l => l.ItemNo, StringComparer.Ordinal)
                .Select(l => Project(l, LineExcluded)).ToList(),
        };
        return CanonicalJson.Serialize(doc);
    }

    public static string VariationPayload(Variation v, IEnumerable<VariationLine> lines)
    {
        var doc = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["format"] = "RAFFAELLO-VO-1",
            ["header"] = Project(v, VariationWorkflowFields),
            ["lines"] = lines.OrderBy(l => l.Order).ThenBy(l => l.ItemCode, StringComparer.Ordinal).Select(l => Project(l, LineExcluded)).ToList(),
        };
        return CanonicalJson.Serialize(doc);
    }

    /// <summary>Stored properties of an entity as a sorted map (minus excluded names).</summary>
    private static SortedDictionary<string, object?> Project(object e, ISet<string> exclude)
    {
        var d = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in e.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0))
            if (!exclude.Contains(p.Name)) d[p.Name] = p.GetValue(e);
        return d;
    }

    public static string InvoiceTitle(SubInvoice h) => $"{h.ContractNo} {h.Title}".Trim();
}

/// <summary>The text that is signed (and stored verbatim). Every line is "name=value"; values never contain new lines.</summary>
public sealed class SignatureStatement
{
    public const string Header = "RAFFAELLO SIGNATURE v1";
    public string Kind { get; init; } = "";
    public string RecordTable { get; init; } = "";
    public long RecordId { get; init; }
    public string Title { get; init; } = "";
    public string Purpose { get; init; } = "";
    public string PayloadSha256 { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public string Signer { get; init; } = "";
    public string SignerName { get; init; } = "";
    public string SignerRole { get; init; } = "";
    public string KeyFingerprint { get; init; } = "";
    public string SignedAtUtc { get; init; } = "";

    private static string Clean(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    public string Text() => string.Join("\n",
        Header,
        "kind=" + Clean(Kind),
        "record=" + Clean(RecordTable) + "#" + RecordId.ToString(CultureInfo.InvariantCulture),
        "title=" + Clean(Title),
        "purpose=" + Clean(Purpose),
        "payload-sha256=" + Clean(PayloadSha256),
        "package-sha256=" + (string.IsNullOrEmpty(PackageSha256) ? "-" : Clean(PackageSha256)),
        "signer=" + Clean(Signer),
        "signer-name=" + Clean(SignerName),
        "signer-role=" + Clean(SignerRole),
        "key=" + Clean(KeyFingerprint),
        "signed-at=" + Clean(SignedAtUtc));

    public static SignatureStatement Parse(string text)
    {
        var lines = (text ?? "").Split('\n');
        if (lines.Length < 12 || lines[0] != Header) throw new FormatException("Not a Raffaello signature statement.");
        var d = lines.Skip(1).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        string G(string k) => d.TryGetValue(k, out var v) ? v : throw new FormatException($"Signature statement has no '{k}'.");
        var rec = G("record");
        var hash = rec.LastIndexOf('#');
        if (hash < 0 || !long.TryParse(rec[(hash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) throw new FormatException("Bad record reference in signature statement.");
        var pkg = G("package-sha256");
        return new SignatureStatement
        {
            Kind = G("kind"), RecordTable = rec[..hash], RecordId = id, Title = G("title"), Purpose = G("purpose"), PayloadSha256 = G("payload-sha256"),
            PackageSha256 = pkg == "-" ? "" : pkg, Signer = G("signer"), SignerName = G("signer-name"), SignerRole = G("signer-role"),
            KeyFingerprint = G("key"), SignedAtUtc = G("signed-at"),
        };
    }

    /// <summary>True when the stored fields of the signature row say the same as its statement (so lists never show something else than what was signed).</summary>
    public bool Matches(RecordSignature s) =>
        Kind == s.Kind && RecordTable == s.RecordTable && RecordId == s.RecordId && Purpose == s.Purpose && PayloadSha256 == s.PayloadSha256
        && PackageSha256 == (s.PackageSha256 ?? "") && string.Equals(Signer, s.SignerUser, StringComparison.OrdinalIgnoreCase)
        && KeyFingerprint == s.KeyFingerprint && SignedAtUtc == s.SignedAtUtc;
}

/// <summary>Who signs: the app user (name / role come from the server sign-in, or Settings in local mode).</summary>
public sealed record SignerInfo(string User, string DisplayName, string Role);

public static class SignatureVerdicts
{
    public const string Valid = "VALID";
    /// <summary>Signature is genuine but the record (or package) changed after signing.</summary>
    public const string Changed = "CHANGED";
    /// <summary>The signature does not verify (forged or damaged).</summary>
    public const string Invalid = "INVALID";
    /// <summary>The key is not registered to the signer.</summary>
    public const string UnknownKey = "UNKNOWN KEY";
}

/// <summary>Outcome of checking one signature against the record as it is now.</summary>
public sealed class SignatureCheck
{
    public RecordSignature Signature { get; init; } = new();
    public bool SignatureValid { get; init; }
    public bool StatementMatchesRow { get; init; }
    public bool KeyRegistered { get; init; }
    public bool ContentUnchanged { get; init; }
    /// <summary>Null when no package was signed / given.</summary>
    public bool? PackageUnchanged { get; init; }
    public string Verdict { get; init; } = SignatureVerdicts.Invalid;
    public string Message { get; init; } = "";
    public bool Ok => Verdict == SignatureVerdicts.Valid;
}

/// <summary>Signs records and checks signatures. Pure: storage is the caller's (see <see cref="ITrustStore"/>).</summary>
public static class SignatureService
{
    public static string NowUtc(DateTime? utc = null) => (utc ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Builds and signs a statement with the user's key. The row is ready to be stored.</summary>
    public static RecordSignature Sign(SigningKeyStore keys, SignerInfo signer, string kind, string recordTable, long recordId, string title, string purpose,
        string payloadSha256, string packageSha256 = "", DateTime? utcNow = null)
    {
        if (!SignatureKinds.All.Contains(kind)) throw new ArgumentException($"Kind must be one of {string.Join(", ", SignatureKinds.All)}.");
        if (!SignaturePurposes.All.Contains(purpose)) throw new ArgumentException($"Purpose must be one of {string.Join(", ", SignaturePurposes.All)}.");
        if (kind == SignatureKinds.Package && string.IsNullOrEmpty(packageSha256)) throw new ArgumentException("A package signature needs the package SHA-256 (build the package first).");
        var key = keys.GetOrCreate(signer.User);
        var at = utcNow ?? DateTime.UtcNow;
        var st = new SignatureStatement
        {
            Kind = kind, RecordTable = recordTable, RecordId = recordId, Title = title, Purpose = purpose, PayloadSha256 = payloadSha256,
            PackageSha256 = (packageSha256 ?? "").ToLowerInvariant(), Signer = signer.User, SignerName = signer.DisplayName, SignerRole = signer.Role,
            KeyFingerprint = key.Fingerprint, SignedAtUtc = NowUtc(at),
        };
        var text = st.Text();
        var sig = keys.Sign(signer.User, Encoding.UTF8.GetBytes(text));
        return new RecordSignature
        {
            Kind = kind, RecordTable = recordTable, RecordId = recordId, RecordTitle = title, Purpose = purpose, PayloadSha256 = payloadSha256,
            PackageSha256 = st.PackageSha256, SignerUser = signer.User, SignerName = signer.DisplayName, SignerRole = signer.Role,
            SignedAtUtc = st.SignedAtUtc, SignedAt = at.ToLocalTime(), KeyFingerprint = key.Fingerprint, Statement = text,
            Signature = Convert.ToBase64String(sig), Machine = Environment.MachineName,
        };
    }

    /// <summary>The ECDSA check alone: this statement was signed by the holder of this public key.</summary>
    public static bool VerifyStatement(string statement, string signatureBase64, string publicKeySpkiBase64)
    {
        try
        {
            using var k = KeyFingerprint.ImportPublic(publicKeySpkiBase64);
            return k.VerifyData(Encoding.UTF8.GetBytes(statement), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    /// <summary>
    /// Full check: genuine signature by a key registered to the signer, statement consistent with the row, and the record
    /// (and package) unchanged since signing. <paramref name="currentPayloadSha"/> = SHA-256 of the record's canonical JSON now.
    /// </summary>
    public static SignatureCheck Verify(RecordSignature s, string? currentPayloadSha, string? currentPackageSha, IEnumerable<UserSigningKey> registeredKeys)
    {
        var key = registeredKeys.FirstOrDefault(k => k.Fingerprint == s.KeyFingerprint && string.Equals(k.UserName, s.SignerUser, StringComparison.OrdinalIgnoreCase));
        SignatureStatement? st = null;
        try { st = SignatureStatement.Parse(s.Statement); } catch (FormatException) { }
        var matches = st != null && st.Matches(s);
        var genuine = key != null && KeyFingerprint.Of(key.PublicKey) == key.Fingerprint && VerifyStatement(s.Statement, s.Signature, key.PublicKey);
        var keyWasActive = key != null && (key.RevokedAt is null || SignedBeforeRevocation(s, key));
        var content = currentPayloadSha != null && string.Equals(currentPayloadSha, s.PayloadSha256, StringComparison.OrdinalIgnoreCase);
        bool? package = string.IsNullOrEmpty(s.PackageSha256) ? null
            : currentPackageSha is null ? false : string.Equals(currentPackageSha, s.PackageSha256, StringComparison.OrdinalIgnoreCase);
        var who = $"{(s.SignerName.Length > 0 ? s.SignerName : s.SignerUser)} ({s.SignerRole})";
        var when = s.SignedAt.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);
        var what = s.Kind == SignatureKinds.Package ? "package" : s.Kind == SignatureKinds.Variation ? "variation" : "invoice";
        string verdict, msg;
        if (key is null)
        {
            verdict = SignatureVerdicts.UnknownKey;
            msg = $"The key {Trust.KeyFingerprint.Short(s.KeyFingerprint)} is not registered to {s.SignerUser} - this signature cannot be trusted.";
        }
        else if (!genuine || !matches)
        {
            verdict = SignatureVerdicts.Invalid;
            msg = !genuine ? $"The signature of {who} does NOT verify - it was forged or the signed text was altered." : "The signature row was edited (its fields differ from the signed statement).";
        }
        else if (!keyWasActive)
        {
            verdict = SignatureVerdicts.Invalid;
            msg = $"Signed with a key withdrawn on {key.RevokedAt:dd-MMM-yyyy} - not valid.";
        }
        else if (!content || package == false)
        {
            verdict = SignatureVerdicts.Changed;
            msg = $"{s.Purpose} by {who} at {when}, but the {(!content ? "record" : "package")} CHANGED after it was signed" +
                  (package == false && currentPackageSha is null ? " (package file not found)" : "") + ".";
        }
        else
        {
            verdict = SignatureVerdicts.Valid;
            msg = $"This {what} was {s.Purpose.ToLowerInvariant()} by {who} at {when} and has not changed since" +
                  (package == true ? $" (package SHA-256 {s.PackageSha256[..12]}...)." : ".");
        }
        return new SignatureCheck
        {
            Signature = s, SignatureValid = genuine, StatementMatchesRow = matches, KeyRegistered = key != null, ContentUnchanged = content,
            PackageUnchanged = package, Verdict = verdict, Message = msg,
        };
    }

    private static bool SignedBeforeRevocation(RecordSignature s, UserSigningKey key)
    {
        if (key.RevokedAt is not { } revoked) return true;
        return DateTime.TryParse(s.SignedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
               && at.ToLocalTime() <= revoked;
    }

    /// <summary>SHA-256 of a file (package ZIP, PDF), hex lower-case.</summary>
    public static string FileSha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
}
