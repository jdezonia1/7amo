using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Raffaello.Core.Trust;

/// <summary>Protects the private key bytes at rest.</summary>
public interface IKeyProtector
{
    /// <summary>Stored in the key file so the right protector is used to open it.</summary>
    string Name { get; }
    byte[] Protect(byte[] secret);
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>
/// Windows DPAPI, current user scope: only this Windows account on this PC can open the key (no password to remember;
/// a roaming profile or a domain backup key restores it). The default on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Raffaello.SigningKey.v1");
    public string Name => "DPAPI-CURRENTUSER";
    public byte[] Protect(byte[] secret) => ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// AES-256-GCM with a key derived from a passphrase (PBKDF2-SHA256, 210 000 iterations). For the command line on a PC
/// without DPAPI and for tests; the passphrase is never stored.
/// </summary>
public sealed class PassphraseKeyProtector : IKeyProtector
{
    private readonly string _passphrase;
    public int Iterations { get; init; } = 210_000;
    public PassphraseKeyProtector(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("A passphrase is required.", nameof(passphrase));
        _passphrase = passphrase;
    }

    public string Name => "PBKDF2-AESGCM";

    public byte[] Protect(byte[] secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(_passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[secret.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, secret, cipher, tag);
        var iters = BitConverter.GetBytes(Iterations);
        return iters.Concat(salt).Concat(nonce).Concat(tag).Concat(cipher).ToArray();
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 4 + 16 + 12 + 16) throw new CryptographicException("Protected key is damaged.");
        var iters = BitConverter.ToInt32(data, 0);
        var salt = data.AsSpan(4, 16).ToArray();
        var nonce = data.AsSpan(20, 12).ToArray();
        var tag = data.AsSpan(32, 16).ToArray();
        var cipher = data.AsSpan(48).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(_passphrase, salt, iters, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

/// <summary>Public part of a user's signing key.</summary>
public sealed class SigningKeyInfo
{
    public string UserName { get; set; } = "";
    /// <summary>SHA-256 of the public key (SubjectPublicKeyInfo DER), hex lower-case.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>SubjectPublicKeyInfo DER, base64.</summary>
    public string PublicKey { get; set; } = "";
    public string Algorithm { get; set; } = KeyFingerprint.Algorithm;
    public DateTime CreatedAt { get; set; }
    public string Machine { get; set; } = "";
    public string Protector { get; set; } = "";
    public string ShortFingerprint => KeyFingerprint.Short(Fingerprint);
}

public static class KeyFingerprint
{
    public const string Algorithm = "ECDSA-P256-SHA256";

    public static string Of(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    public static string Of(string spkiBase64) => Of(Convert.FromBase64String(spkiBase64));

    /// <summary>Four groups of four hex digits, e.g. 3F2A-9C01-77B4-E0D2: what is printed on a signature block.</summary>
    public static string Short(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length < 16) return fingerprint ?? "";
        var f = fingerprint.ToUpperInvariant();
        return $"{f[..4]}-{f[4..8]}-{f[8..12]}-{f[12..16]}";
    }

    public static ECDsa ImportPublic(string spkiBase64)
    {
        var k = ECDsa.Create();
        k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
        return k;
    }
}

/// <summary>
/// Per-user signing keys: ECDSA P-256 generated on first use, private key stored protected (DPAPI on Windows) in
/// %APPDATA%\Raffaello\keys\&lt;user&gt;.raffaello-key.json. Only the public key leaves the PC (registered on the server /
/// in the data file so anyone can verify).
/// </summary>
public sealed class SigningKeyStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Folder { get; }
    private readonly IKeyProtector _protector;

    public SigningKeyStore(string folder, IKeyProtector protector) { Folder = folder; _protector = protector; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raffaello", "keys");

    /// <summary>The Windows default (DPAPI). On other systems a passphrase protector must be given explicitly.</summary>
    public static SigningKeyStore Default(string? passphrase = null)
    {
        if (!string.IsNullOrEmpty(passphrase)) return new SigningKeyStore(DefaultFolder, new PassphraseKeyProtector(passphrase));
        if (OperatingSystem.IsWindows()) return new SigningKeyStore(DefaultFolder, new DpapiKeyProtector());
        throw new PlatformNotSupportedException("Signing keys are protected with Windows DPAPI; on this system pass a passphrase (--passphrase).");
    }

    private sealed class KeyFile
    {
        public int Version { get; set; } = 1;
        public SigningKeyInfo Key { get; set; } = new();
        public string ProtectedPrivateKey { get; set; } = "";
    }

    private string FileOf(string user)
    {
        var safe = new string((user ?? "").Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray());
        if (safe.Length == 0) throw new ArgumentException("A user name is needed for a signing key.");
        return Path.Combine(Folder, safe + ".raffaello-key.json");
    }

    public bool Has(string user) => File.Exists(FileOf(user));

    public SigningKeyInfo? Info(string user) => Has(user) ? Read(user).Key : null;

    private KeyFile Read(string user) =>
        JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(FileOf(user))) ?? throw new InvalidDataException("The signing key file is damaged.");

    /// <summary>Returns the user's key, generating (and protecting) a new one the first time.</summary>
    public SigningKeyInfo GetOrCreate(string user)
    {
        if (Has(user)) return Read(user).Key;
        return Create(user);
    }

    /// <summary>Makes a new key pair (replaces any existing one - the old public key stays valid for old signatures once registered).</summary>
    public SigningKeyInfo Create(string user)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = k.ExportSubjectPublicKeyInfo();
        var pkcs8 = k.ExportPkcs8PrivateKey();
        try
        {
            var info = new SigningKeyInfo
            {
                UserName = user.Trim(), PublicKey = Convert.ToBase64String(spki), Fingerprint = KeyFingerprint.Of(spki),
                CreatedAt = DateTime.Now, Machine = Environment.MachineName, Protector = _protector.Name,
            };
            var file = new KeyFile { Key = info, ProtectedPrivateKey = Convert.ToBase64String(_protector.Protect(pkcs8)) };
            Directory.CreateDirectory(Folder);
            var path = FileOf(user);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
            File.Move(tmp, path, overwrite: true);
            return info;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    /// <summary>Opens the private key (DPAPI / passphrase) for one signing operation; dispose it right after.</summary>
    public ECDsa OpenPrivateKey(string user)
    {
        var file = Read(user);
        if (!string.Equals(file.Key.Protector, _protector.Name, StringComparison.Ordinal))
            throw new CryptographicException($"The key of {user} is protected with {file.Key.Protector}; this PC uses {_protector.Name}.");
        var pkcs8 = _protector.Unprotect(Convert.FromBase64String(file.ProtectedPrivateKey));
        try
        {
            var k = ECDsa.Create();
            k.ImportPkcs8PrivateKey(pkcs8, out _);
            if (KeyFingerprint.Of(k.ExportSubjectPublicKeyInfo()) != file.Key.Fingerprint)
                throw new CryptographicException("The signing key file does not match its fingerprint.");
            return k;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    public byte[] Sign(string user, byte[] data)
    {
        using var k = OpenPrivateKey(user);
        return k.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
