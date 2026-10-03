using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raffaello.Core.Assistant.ClaudeCode;

/// <summary>What the app knows about Claude Code on this PC.</summary>
public sealed record ClaudeCodeStatus(bool Found, string Path, string Version, bool? LoggedIn, string AuthMethod, string Message)
{
    /// <summary>Usable: installed and not known to be logged out (an older CLI without "auth status" counts as usable).</summary>
    public bool Ready => Found && LoggedIn != false;
    public DateTime CheckedAt { get; init; } = DateTime.Now;
    public static ClaudeCodeStatus Unknown { get; } = new(false, "", "", null, "", "Claude Code not checked yet.");
}

/// <summary>Finds the Claude Code CLI (claude.exe) and checks the version and the login - never reads or stores any credential.</summary>
public static class ClaudeCodeLocator
{
    public const string InstallHelp =
        "Install Claude Code once: open PowerShell and run   irm https://claude.ai/install.ps1 | iex   (or, with Node.js:   npm install -g @anthropic-ai/claude-code ). " +
        "Then open a new terminal, run   claude   and sign in with your Claude account.";
    public const string LoginHelp =
        "Open a terminal (PowerShell), run   claude   once and sign in with your Claude account (or run   claude auth login ). Then press CHECK again.";

    /// <summary>Places to look, in order: the path set in Settings, PATH, the native installer, npm.</summary>
    public static IEnumerable<string> Candidates(string? overridePath, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath.Trim().Trim('"');
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : new[] { "claude" };
        foreach (var dir in (env("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var n in names) yield return System.IO.Path.Combine(dir.Trim('"'), n);
        var home = env("USERPROFILE") ?? env("HOME") ?? "";
        var appData = env("APPDATA") ?? "";
        var local = env("LOCALAPPDATA") ?? "";
        if (home.Length > 0)
        {
            yield return System.IO.Path.Combine(home, ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
            yield return System.IO.Path.Combine(home, ".claude", "local", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        }
        if (appData.Length > 0)
        {
            yield return System.IO.Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            yield return System.IO.Path.Combine(appData, "npm", "claude.cmd");
        }
        if (local.Length > 0) yield return System.IO.Path.Combine(local, "Programs", "claude-code", "claude.exe");
    }

    public static string? Find(string? overridePath = null, Func<string, string?>? env = null, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var c in Candidates(overridePath, env))
        {
            try { if (exists(c)) return ResolveExecutable(c, exists); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }

    /// <summary>The npm shim claude.cmd only starts node_modules\@anthropic-ai\claude-code\bin\claude.exe: use that directly when present.</summary>
    public static string ResolveExecutable(string path, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return path;
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var native = System.IO.Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        return exists(native) ? native : path;
    }

    /// <summary>Finds the CLI and asks it for its version and login state (two short runs, nothing is sent to Anthropic).</summary>
    public static ClaudeCodeStatus Probe(string? overridePath = null, TimeSpan? timeout = null)
    {
        var path = Find(overridePath);
        if (path is null) return Interpret(null, null, null, null);
        var t = timeout ?? TimeSpan.FromSeconds(20);
        var version = Run(path, new[] { "--version" }, t, out var vErr);
        if (version is null) return Interpret(path, null, null, vErr);
        var auth = Run(path, new[] { "auth", "status", "--json" }, t, out _);
        return Interpret(path, version, auth, null);
    }

    /// <summary>Turns the probe outputs into a status and a plain message (pure: tested with fixtures).</summary>
    public static ClaudeCodeStatus Interpret(string? path, string? versionOutput, string? authJson, string? error)
    {
        if (path is null) return new ClaudeCodeStatus(false, "", "", null, "", "Claude Code is not installed on this PC. " + InstallHelp);
        if (versionOutput is null)
            return new ClaudeCodeStatus(false, path, "", null, "", $"Claude Code was found at {path} but did not start ({error ?? "no answer"}). Reinstall it: " + InstallHelp);
        var version = versionOutput.Trim().Split('\n')[0].Trim();
        bool? logged = null;
        var method = "";
        if (!string.IsNullOrWhiteSpace(authJson))
        {
            var start = authJson.IndexOf('{');
            if (start >= 0)
                try
                {
                    if (JsonNode.Parse(authJson[start..]) is JsonObject o)
                    {
                        logged = (bool?)o["loggedIn"];
                        method = (string?)o["authMethod"] ?? "";
                    }
                }
                catch (JsonException) { }
        }
        var msg = logged switch
        {
            true => $"Claude Code found ({version}), logged in{(method.Length > 0 && method != "none" ? " (" + method + ")" : "")}. Ask Raffaello can use your Claude login.",
            false => $"Claude Code found ({version}) but NOT logged in. " + LoginHelp,
            null => $"Claude Code found ({version}). Login could not be checked; if answers fail, " + LoginHelp,
        };
        return new ClaudeCodeStatus(true, path, version, logged, method, msg);
    }

    /// <summary>Runs the CLI briefly; null when it could not start or timed out.</summary>
    internal static string? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, out string? error)
    {
        error = null;
        try
        {
            var psi = ClaudeCodeRunner.StartInfo(exe, args, null);
            using var p = Process.Start(psi);
            if (p is null) { error = "could not start"; return null; }
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch (Exception) { }
                error = "timed out";
                return null;
            }
            var output = stdout.GetAwaiter().GetResult();
            var err = stderr.GetAwaiter().GetResult();
            if (output.Trim().Length == 0 && p.ExitCode != 0) { error = err.Trim(); return null; }
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return null;
        }
    }
}