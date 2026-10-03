using System.Text;
using System.Text.Json;
using Raffaello.Core.Settings;

namespace Raffaello.Core.Assistant.Mcp;

/// <summary>
/// Entry point of the Raffaello MCP server (used by "Raffaello.exe --mcp", "raffaello-cli mcp", Claude Code and Claude Desktop).
/// Arguments: --db FILE (default: the data file in Raffaello's settings), --user NAME, --cites-file FILE (the app's side channel:
/// one JSON line per tool call with the records it returned, so the chat can show clickable sources).
/// </summary>
public static class McpHost
{
    public const string Usage = "raffaello mcp server: --mcp [--db <data file>] [--user <name>] [--cites-file <file>]";

    public sealed record Options(string? Db, string? User, string? CitesFile);

    public static Options Parse(IReadOnlyList<string> args)
    {
        string? db = null, user = null, cites = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--db": db = Next(); break;
                case "--user": user = Next(); break;
                case "--cites-file": cites = Next(); break;
            }
        }
        return new Options(db, user, cites);
    }

    /// <summary>Runs the server on the process's stdin / stdout until stdin closes. Returns the exit code.</summary>
    public static int RunStdio(IReadOnlyList<string> args)
    {
        var utf8 = new UTF8Encoding(false);
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        try
        {
            return RunAsync(args, stdin, stdout, stderr).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stderr.WriteLine("[raffaello-mcp] fatal: " + ex);
            return 1;
        }
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input, TextWriter output, TextWriter log, CancellationToken ct = default)
    {
        var o = Parse(args);
        var app = AppSettings.Load();
        if (o.Db is null && Raffaello.Core.Remote.RemoteSettings.Load().IsServer)
            log.WriteLine("[raffaello-mcp] note: the app is in SERVER mode; reading the local data file " + app.DataFilePath + " (server data is not available to the MCP server yet). Use --db to choose a file.");
        var db = o.Db ?? app.DataFilePath;
        var assistant = AssistantSettings.Load();
        using var source = new McpDataSource(db, app, assistant, o.User) { Log = log };
        log.WriteLine($"[raffaello-mcp] read-only server for {source.SourcePath}");
        var server = new McpServer(source.Catalog, log, o.CitesFile is null ? null : (tool, cites) => AppendCitations(o.CitesFile, tool, cites));
        await server.RunAsync(input, output, ct).ConfigureAwait(false);
        return 0;
    }

    /// <summary>One line per call: {"tool": name, "citations": [...]} (read back by <see cref="ReadCitations"/>).</summary>
    public static void AppendCitations(string file, string tool, IReadOnlyList<Citation> cites)
    {
        var line = JsonSerializer.Serialize(new CitationLine(tool, cites.ToList())) + "\n";
        File.AppendAllText(file, line, new UTF8Encoding(false));
    }

    public static List<Citation> ReadCitations(string file)
    {
        var res = new List<Citation>();
        if (!File.Exists(file)) return res;
        foreach (var l in File.ReadAllLines(file))
        {
            if (l.Trim().Length == 0) continue;
            try { if (JsonSerializer.Deserialize<CitationLine>(l) is { } cl) res.AddRange(cl.Citations); }
            catch (JsonException) { /* a half-written line: skip */ }
        }
        return res;
    }

    public sealed record CitationLine(string Tool, List<Citation> Citations);
}