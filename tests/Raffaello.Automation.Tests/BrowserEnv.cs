using Microsoft.Playwright;
using Raffaello.Core.AconexWeb;

namespace Raffaello.Automation.Tests;

/// <summary>
/// Finds a browser Playwright can drive: RAFFAELLO_TEST_BROWSER, the pre-installed /opt/pw-browsers/chromium,
/// Playwright's own download, then Edge on Windows. Tests are skipped (not failed) when none can be launched.
/// </summary>
public static class BrowserEnv
{
    private static readonly Lazy<(string? Exe, string? Channel, string? Error)> Probe = new(DoProbe);

    public static string? SkipReason => Probe.Value.Error;

    public static void Apply(AconexConfig cfg)
    {
        cfg.BrowserExecutablePath = Probe.Value.Exe ?? "";
        cfg.BrowserChannel = Probe.Value.Channel ?? "";
    }

    private static (string?, string?, string?) DoProbe()
    {
        var candidates = new List<(string? Exe, string? Channel)>();
        if (Environment.GetEnvironmentVariable("RAFFAELLO_TEST_BROWSER") is { Length: > 0 } env) candidates.Add((env, null));
        candidates.Add((null, null));
        if (File.Exists("/opt/pw-browsers/chromium")) candidates.Add(("/opt/pw-browsers/chromium", null));
        if (OperatingSystem.IsWindows()) candidates.Add((null, "msedge"));
        var errors = new List<string>();
        foreach (var (exe, channel) in candidates)
        {
            try
            {
                using var pw = Playwright.CreateAsync().GetAwaiter().GetResult();
                var b = pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, ExecutablePath = exe, Channel = channel }).GetAwaiter().GetResult();
                b.CloseAsync().GetAwaiter().GetResult();
                return (exe, channel, null);
            }
            catch (Exception ex) { errors.Add($"{exe ?? channel ?? "bundled"}: {ex.Message.Split('\n')[0]}"); }
        }
        return (null, null, "No browser for Playwright: " + string.Join(" | ", errors));
    }
}
