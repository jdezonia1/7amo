using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Raffaello.Core.AconexWeb;

namespace Raffaello.Automation;

/// <summary>
/// Optional stored Aconex login, encrypted with Windows DPAPI for the current Windows user (only he, on this PC, can
/// decrypt it). Never written in plain text; not supported outside Windows. Most users will not need it: the persistent
/// browser profile keeps the session after the first manual login.
/// </summary>
public sealed class DpapiCredentialVault : ICredentialVault
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Raffaello.Aconex.v1");
    private readonly string _path;

    public DpapiCredentialVault(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raffaello", "aconex.credential");
    }

    public string FilePath => _path;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool HasCredential => IsSupported && File.Exists(_path);

    public void Save(string userName, string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Storing the Aconex password needs Windows (DPAPI).");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new[] { userName, password });
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, blob);
        }
        finally { Array.Clear(plain); }
    }

    public (string User, string Password)? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var parts = JsonSerializer.Deserialize<string[]>(plain);
                return parts is { Length: 2 } ? (parts[0], parts[1]) : null;
            }
            finally { Array.Clear(plain); }
        }
        catch (CryptographicException) { return null; } // another user / PC: ignore the file
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
