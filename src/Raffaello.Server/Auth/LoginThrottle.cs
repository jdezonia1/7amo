using System.Collections.Concurrent;

namespace Raffaello.Server.Auth;

/// <summary>
/// [phase6] Sign-in rate limiting: after <see cref="ServerOptions.LoginMaxFailures"/> wrong passwords for the same user name or
/// from the same address within the window, further attempts are refused (429 + Retry-After) until the lockout ends.
/// A successful sign-in clears the user's counter. In memory: a server restart clears it.
/// </summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _max;
    private readonly TimeSpan _window, _lockout;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private sealed class Entry { public readonly List<DateTime> Failures = new(); public DateTime? LockedUntil; }

    public LoginThrottle(ServerOptions o)
    {
        _max = Math.Max(1, o.LoginMaxFailures);
        _window = TimeSpan.FromMinutes(Math.Max(1, o.LoginWindowMinutes));
        _lockout = TimeSpan.FromMinutes(Math.Max(1, o.LoginLockoutMinutes));
    }

    private static IEnumerable<string> Keys(string user, string address) => new[] { "u:" + user.Trim(), "a:" + address };

    /// <summary>Seconds to wait when the user or the address is locked out, else null.</summary>
    public int? RetryAfter(string user, string address)
    {
        var now = Clock();
        int? wait = null;
        foreach (var k in Keys(user, address))
            if (_entries.TryGetValue(k, out var e))
                lock (e)
                    if (e.LockedUntil is { } until && until > now)
                        wait = Math.Max(wait ?? 0, (int)Math.Ceiling((until - now).TotalSeconds));
        return wait;
    }

    public void Failed(string user, string address)
    {
        var now = Clock();
        foreach (var k in Keys(user, address))
        {
            var e = _entries.GetOrAdd(k, _ => new Entry());
            lock (e)
            {
                e.Failures.RemoveAll(t => now - t > _window);
                e.Failures.Add(now);
                if (e.Failures.Count >= _max) { e.LockedUntil = now + _lockout; e.Failures.Clear(); }
            }
        }
    }

    public void Succeeded(string user) => _entries.TryRemove("u:" + user.Trim(), out _);
}
