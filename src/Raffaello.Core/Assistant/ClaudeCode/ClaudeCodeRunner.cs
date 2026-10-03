using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Assistant.Mcp;

namespace Raffaello.Core.Assistant.ClaudeCode;

/// <summary>One question for a headless Claude Code run.</summary>
public sealed record ClaudeCodeRequest(string Prompt, string SystemPrompt, string? Model, int MaxTurns, TimeSpan Timeout);

public sealed class ClaudeCodeResult
{
    public string Text { get; set; } = "";
    /// <summary>Plain-language failure ("not logged in", "timed out" ...); empty when the run answered.</summary>
    public string Error { get; set; } = "";
    public bool Failed => Error.Length > 0;
    public bool HitTurnLimit { get; set; }
    public double CostUsd { get; set; }
    public int Turns { get; set; }
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string McpStatus { get; set; } = "";
    public List<string> ToolsUsed { get; } = new();
    public List<Citation> Citations { get; } = new();
    public int ExitCode { get; set; }
    public string Stderr { get; set; } = "";
}

/// <summary>Runs Claude Code for one question (tests use a fake).</summary>
public interface IClaudeCodeRunner
{
    Task<ClaudeCodeResult> RunAsync(ClaudeCodeRequest request, Action<ClaudeCodeEvent>? onEvent, CancellationToken ct);
}

/// <summary>
/// Starts <c>claude -p</c> with the user's own Claude login: prompt on stdin (UTF-8, any length, Arabic safe), our system prompt from a
/// file, the Raffaello MCP server as the ONLY tools (built-in tools switched off: no shell, no file editing, no web), no other MCP
/// servers or settings, no saved session. Output is read as stream-json and turned into chat events. Cancel / timeout kill the
/// whole process tree. Nothing about the login is read or stored by the app.
/// </summary>
public sealed class ClaudeCodeRunner : IClaudeCodeRunner
{
    private readonly string _claude;
    private readonly string _mcpCommand;
    private readonly IReadOnlyList<string> _mcpArgs;

    /// <param name="claudePath">claude.exe (or the npm claude.cmd shim).</param>
    /// <param name="mcpCommand">The MCP server program (Raffaello.exe or raffaello-cli.exe).</param>
    /// <param name="mcpArgs">Its arguments (e.g. --mcp --db FILE).</param>
    public ClaudeCodeRunner(string claudePath, string mcpCommand, IReadOnlyList<string> mcpArgs)
    {
        _claude = claudePath;
        _mcpCommand = mcpCommand;
        _mcpArgs = mcpArgs;
    }

    public string WorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "Raffaello", "claude-runs");

    /// <summary>The MCP config file content (mcpServers.raffaello = stdio server).</summary>
    public static string McpConfig(string command, IReadOnlyList<string> args) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["raffaello"] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            },
        },
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Command-line arguments of the run (prompt goes on stdin).</summary>
    public static List<string> Arguments(string mcpConfigPath, string systemPromptPath, string? model, int maxTurns)
    {
        var a = new List<string>
        {
            "-p",
            "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--mcp-config", mcpConfigPath, "--strict-mcp-config",
            "--tools", "",                                   // no built-in tools at all (no Bash / PowerShell / Edit / Write / Read / Web)
            "--allowedTools", "mcp__raffaello",              // the Raffaello read-only tools run without asking
            "--permission-mode", "dontAsk",                  // anything else is refused, never prompted
            "--system-prompt-file", systemPromptPath,
            "--max-turns", Math.Clamp(maxTurns, 1, 40).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--setting-sources", "local",                    // ignore the user's own Claude Code settings / hooks / plugins
            "--no-session-persistence",                      // no transcript saved by Claude Code
            "--disable-slash-commands",
        };
        if (!string.IsNullOrWhiteSpace(model)) { a.Add("--model"); a.Add(model.Trim()); }
        return a;
    }

    /// <summary>Start info for the CLI: a .cmd shim goes through cmd.exe, everything else is started directly.</summary>
    public static ProcessStartInfo StartInfo(string exe, IReadOnlyList<string> args, string? workDir)
    {
        var utf8 = new UTF8Encoding(false);
        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd /s /c ""shim" "arg" ..." keeps the quoting of every argument
            var line = "\"" + Quote(exe) + " " + string.Join(" ", args.Select(Quote)) + "\"";
            psi = new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c " + line };
        }
        else
        {
            psi = new ProcessStartInfo(exe);
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardInputEncoding = utf8;
        psi.StandardOutputEncoding = utf8;
        psi.StandardErrorEncoding = utf8;
        if (workDir != null) psi.WorkingDirectory = workDir;
        psi.Environment.Remove("CLAUDECODE");               // started from inside another Claude Code session (tests): still a fresh run
        return psi;
    }

    private static string Quote(string a) => a.Length > 0 && !a.Any(ch => char.IsWhiteSpace(ch) || ch == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    public async Task<ClaudeCodeResult> RunAsync(ClaudeCodeRequest request, Action<ClaudeCodeEvent>? onEvent, CancellationToken ct)
    {
        var work = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        var utf8 = new UTF8Encoding(false);
        var mcpPath = Path.Combine(work, "mcp.json");
        var sysPath = Path.Combine(work, "system.txt");
        var citesPath = Path.Combine(work, "cites.jsonl");
        File.WriteAllText(mcpPath, McpConfig(_mcpCommand, _mcpArgs.Concat(new[] { "--cites-file", citesPath }).ToList()), utf8);
        File.WriteAllText(sysPath, request.SystemPrompt, utf8);

        var result = new ClaudeCodeResult();
        var acc = new ClaudeCodeAccumulator();
        var stderr = new StringBuilder();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        Process? p = null;
        try
        {
            p = Process.Start(StartInfo(_claude, Arguments(mcpPath, sysPath, request.Model, request.MaxTurns), work))
                ?? throw new InvalidOperationException("Claude Code could not be started.");
            using var reg = linked.Token.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (Exception) { } });
            var errTask = Task.Run(async () =>
            {
                string? l;
                while ((l = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    lock (stderr) if (stderr.Length < 16_000) stderr.AppendLine(l);
            });
            try
            {
                await p.StandardInput.WriteAsync(request.Prompt).ConfigureAwait(false);
                await p.StandardInput.FlushAsync().ConfigureAwait(false);
                p.StandardInput.Close();
            }
            catch (IOException) { /* the CLI exited before reading the question: its output says why */ }

            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                foreach (var e in acc.Feed(line)) onEvent?.Invoke(e);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            result.ExitCode = p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            result.Error = "Claude Code could not be started: " + ex.Message + ". " + ClaudeCodeLocator.InstallHelp;
        }
        finally
        {
            p?.Dispose();
        }
        ct.ThrowIfCancellationRequested();   // the user pressed STOP: the caller shows it as stopped

        result.Text = acc.Text.Trim();
        result.CostUsd = acc.CostUsd;
        result.Turns = acc.Turns;
        result.Model = acc.Model;
        result.McpStatus = acc.McpStatus;
        result.InputTokens = acc.InputTokens;
        result.OutputTokens = acc.OutputTokens;
        result.ToolsUsed.AddRange(acc.ToolsUsed);
        result.HitTurnLimit = acc.ResultSubtype == "error_max_turns";
        result.Stderr = stderr.ToString();
        try { result.Citations.AddRange(McpHost.ReadCitations(citesPath)); } catch (IOException) { }

        if (result.Error.Length == 0)
        {
            if (timeout.IsCancellationRequested)
                result.Error = $"Claude Code did not finish within {request.Timeout.TotalSeconds:F0} s (Settings > Assistant > time limit).";
            else if (acc.FailureReason() is { Length: > 0 } why)
                result.Error = why;
            else if (!acc.HasResult)
                result.Error = "Claude Code stopped without an answer (exit " + result.ExitCode + ")" + (result.Stderr.Trim().Length > 0 ? ": " + Tail(result.Stderr, 400) : ".");
        }
        if (result.Error.Length == 0 && acc.McpStatus.Length > 0 && acc.McpStatus != "connected" && result.ToolsUsed.Count == 0)
            result.Error = "The Raffaello data server did not start inside Claude Code (" + acc.McpStatus + "). Answer not based on project data.";

        try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static string Tail(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : "..." + s[^max..];
    }
}