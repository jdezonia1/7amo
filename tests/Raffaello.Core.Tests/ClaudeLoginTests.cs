using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Assistant;
using Raffaello.Core.Assistant.ClaudeCode;
using Raffaello.Core.Assistant.Mcp;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

/// <summary>Recorded Claude Code stream-json outputs (trimmed): the real "not logged in" run of 2.1.287 and a tool run.</summary>
internal static class ClaudeCodeFixtures
{
    public const string NotLoggedIn = """
        {"type":"system","subtype":"init","cwd":"C:\\tmp","session_id":"80d8","tools":[],"mcp_servers":[],"model":"claude-opus-5-5","permissionMode":"dontAsk"}
        {"type":"system","subtype":"status","status":"requesting","session_id":"80d8"}
        {"type":"assistant","message":{"id":"c5c6","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"Not logged in · Please run /login"}]},"session_id":"80d8","error":"authentication_failed","is_api_error_message":true}
        {"type":"result","subtype":"success","is_error":true,"num_turns":1,"result":"Not logged in · Please run /login","total_cost_usd":0,"terminal_reason":"api_error","stop_reason":"stop_sequence"}
        """;

    /// <summary>Partial messages on: deltas, then each complete message; one MCP tool call; Arabic text.</summary>
    public const string ToolRun = """
        {"type":"system","subtype":"init","model":"claude-sonnet-5","mcp_servers":[{"name":"raffaello","status":"connected"}],"tools":["mcp__raffaello__rooms_remaining"]}
        {"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1"}}}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Let me "}}}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"check."}}}
        {"type":"assistant","message":{"id":"msg_1","usage":{"input_tokens":900,"output_tokens":40},"content":[{"type":"text","text":"Let me check."}]}}
        {"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_1","name":"mcp__raffaello__rooms_remaining","input":{"building":"HOTEL","area_type":"GUESTROOM","stage":"2ND FIX","item":"DATA"}}]}}
        {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"{\"summary\":{\"rooms_with_remaining\":152}}"}]}]}}
        {"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_2"}}}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"152 غرفة [[room:H1-L2-231]] still have 2ND FIX DATA remaining."}}}
        {"type":"assistant","message":{"id":"msg_2","usage":{"input_tokens":1500,"output_tokens":60},"content":[{"type":"text","text":"152 غرفة [[room:H1-L2-231]] still have 2ND FIX DATA remaining."}]}}
        {"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"152 غرفة [[room:H1-L2-231]] still have 2ND FIX DATA remaining.","total_cost_usd":0.0123}
        """;

    /// <summary>No partial messages (older CLI): the text comes only with the complete messages.</summary>
    public const string NoPartials = """
        {"type":"system","subtype":"init","mcp_servers":[{"name":"raffaello","status":"connected"}]}
        {"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Checking the ledger."},{"type":"tool_use","id":"t1","name":"mcp__raffaello__query_ledger","input":{}}]}}
        {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"query_ledger failed"}]}}
        {"type":"assistant","message":{"id":"m2","content":[{"type":"text","text":"The ledger could not be read."}]}}
        not json at all
        {"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"The ledger could not be read."}
        """;

    public const string MaxTurns = """
        {"type":"system","subtype":"init","mcp_servers":[{"name":"raffaello","status":"connected"}]}
        {"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"mcp__raffaello__get_room","input":{"room":"P2-106"}}]}}
        {"type":"result","subtype":"error_max_turns","is_error":false,"num_turns":3}
        """;

    public static List<ClaudeCodeEvent> Feed(ClaudeCodeAccumulator acc, string fixture)
    {
        var events = new List<ClaudeCodeEvent>();
        foreach (var l in fixture.Split('\n')) events.AddRange(acc.Feed(l.TrimEnd('\r')));
        return events;
    }
}

/// <summary>Scripted Claude Code runs for the session tests (no process).</summary>
internal sealed class FakeClaudeRunner : IClaudeCodeRunner
{
    public List<ClaudeCodeRequest> Requests { get; } = new();
    public Func<ClaudeCodeRequest, Action<ClaudeCodeEvent>?, CancellationToken, Task<ClaudeCodeResult>> Script { get; set; } =
        (_, _, _) => Task.FromResult(new ClaudeCodeResult());

    public Task<ClaudeCodeResult> RunAsync(ClaudeCodeRequest request, Action<ClaudeCodeEvent>? onEvent, CancellationToken ct)
    {
        Requests.Add(request);
        return Script(request, onEvent, ct);
    }
}
public class McpServerTests
{
    private static JsonObject Rpc(McpServer s, int id, string method, JsonObject? p = null)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p != null) msg["params"] = p;
        return s.Handle(msg.ToJsonString())!;
    }

    [Fact]
    public void Initialize_lists_only_read_tools_with_schemas()
    {
        using var env = AssistantEnv.Create();
        var server = new McpServer(() => new McpToolCatalog(env.Data));
        var init = Rpc(server, 1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "t" } });
        Assert.Equal("2025-03-26", (string?)init["result"]!["protocolVersion"]);
        Assert.Equal("raffaello", (string?)init["result"]!["serverInfo"]!["name"]);
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);
        var unknownVersion = Rpc(server, 2, "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });
        Assert.Equal(McpServer.SupportedVersions[0], (string?)unknownVersion["result"]!["protocolVersion"]);

        Assert.Null(server.Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        var tools = Rpc(server, 3, "tools/list")["result"]!["tools"]!.AsArray().OfType<JsonObject>().ToList();
        var names = tools.Select(t => (string?)t["name"]).ToList();
        foreach (var n in AssistantTools.ReadTools.Concat(ExtraReadTools.Names)) Assert.Contains(n, names);
        foreach (var w in AssistantActionKinds.All) Assert.DoesNotContain(w, names);
        Assert.All(tools, t =>
        {
            Assert.Equal("object", (string?)t["inputSchema"]!["type"]);
            Assert.True((bool)t["annotations"]!["readOnlyHint"]!);
            Assert.False(string.IsNullOrWhiteSpace((string?)t["description"]));
        });
    }

    [Fact]
    public void Tools_call_reads_the_ledger_and_refuses_writes_and_bad_input()
    {
        using var env = AssistantEnv.Create();
        var cites = new List<Citation>();
        var server = new McpServer(() => new McpToolCatalog(env.Data), onCitations: (_, c) => cites.AddRange(c));
        var r = Rpc(server, 1, "tools/call", new JsonObject { ["name"] = "get_room", ["arguments"] = new JsonObject { ["room"] = "P2-106" } })["result"]!;
        Assert.False((bool)r["isError"]!);
        var text = (string?)r["content"]![0]!["text"];
        Assert.Contains("P2-106", text);
        Assert.Contains("[[room:P2-106]]", text);
        Assert.Contains(cites, c => c.Kind == "room" && c.Key == "P2-106");

        var write = Rpc(server, 2, "tools/call", new JsonObject { ["name"] = "draft_ledger_claim", ["arguments"] = new JsonObject { ["room"] = "P2-106" } })["result"]!;
        Assert.True((bool)write["isError"]!);
        Assert.Contains("read-only", (string?)write["content"]![0]!["text"]);
        Assert.Empty(env.Store.All<AssistantAction>());   // nothing proposed, nothing written

        var bad = Rpc(server, 3, "tools/call", new JsonObject { ["name"] = "rooms_remaining", ["arguments"] = new JsonObject { ["stage"] = "2ND FIX" } })["result"]!;
        Assert.True((bool)bad["isError"]!);
        Assert.Contains("item is required", (string?)bad["content"]![0]!["text"]);

        Assert.Equal(-32601, (int)Rpc(server, 4, "nope")["error"]!["code"]!);
        Assert.Equal(-32700, (int)server.Handle("{oops")!["error"]!["code"]!);
        Assert.NotNull(Rpc(server, 5, "ping")["result"]);
    }

    [Fact]
    public void Rooms_remaining_counts_rooms_by_area_type_with_exact_item_and_over()
    {
        using var env = AssistantEnv.Create();
        var cat = new McpToolCatalog(env.Data);
        var r = cat.Call("rooms_remaining", new JsonObject { ["building"] = "BRANDED", ["area_type"] = "apartment", ["stage"] = "2nd fix", ["item"] = "LIGHT", ["only_with_remaining"] = false });
        Assert.False(r.IsError, r.Content);
        var o = JsonNode.Parse(r.Content)!.AsObject();
        var sum = o["summary"]!;
        Assert.Equal(2, (int)sum["rooms_matched"]!);
        Assert.Equal(1, (int)sum["rooms_with_remaining"]!);   // P2-106: 39 - (20 + 5) = 14
        Assert.Equal(1, (int)sum["rooms_over"]!);             // P2-107: 45 claimed of 39
        Assert.Equal(14, (double)sum["total_remaining_positive"]!);
        var rooms = o["rooms"]!.AsArray();
        Assert.Equal("P2-106", (string?)rooms[0]!["room"]);
        Assert.Equal(14, (double)rooms[0]!["remaining"]!);
        Assert.Equal("OVER", (string?)rooms[1]!["flag"]);
        Assert.Single(o["filter"]!["items"]!.AsArray());   // LIGHT only, not every item containing LIGHT

        var list = JsonNode.Parse(cat.Call("list_rooms", new JsonObject { ["room_type"] = "2br" }).Content)!;
        Assert.Equal(2, (int)list["rooms_matched"]!);
    }

    [Fact]
    public async Task Server_loop_answers_each_request_on_its_own_line()
    {
        using var env = AssistantEnv.Create();
        var server = new McpServer(() => new McpToolCatalog(env.Data));
        var input = new StringReader(string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":"b","method":"tools/list"}"""));
        var output = new StringWriter();
        await server.RunAsync(input, output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("b", (string?)JsonNode.Parse(lines[1])!["id"]);
    }

    [Fact]
    public void Data_source_reads_a_snapshot_and_never_changes_the_data_file()
    {
        using var env = AssistantEnv.Create();
        var path = env.Project.Settings.DataFilePath;
        var snapDir = Path.Combine(env.Dir, "snap");
        string before;
        DateTime stampBefore;
        using (var src = new McpDataSource(path, env.Project.Settings, new AssistantSettings(), "tester", snapDir) { RefreshEvery = TimeSpan.Zero })
        {
            var r = src.Catalog().Call("query_ledger", new JsonObject { ["room"] = "P2-106" });
            Assert.False(r.IsError, r.Content);
            Assert.Contains("P2-106", src.Data!.Project.Snapshot.Rooms.Select(x => x.Code));
            Assert.NotEqual(Path.GetFullPath(path), Path.GetFullPath(src.Data.Project.Settings.DataFilePath));
            Assert.Single(Directory.GetFiles(snapDir, "snapshot-*.db"));

            // the app adds a claim -> the next call sees it (fresh snapshot), the old snapshot is removed
            env.Project.Store.Insert(new ClaimLine { Building = Buildings.Branded, Subcontractor = "ROOTS", InvoiceNo = 3, Room = "P2-106", Floor = "2", Stage = Stages.Second, Item = "LIGHT", Qty = 4, EnteredAt = DateTime.Now });
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
            var again = JsonNode.Parse(src.Catalog().Call("rooms_remaining", new JsonObject { ["room"] = "P2-106", ["stage"] = "2ND FIX", ["item"] = "LIGHT" }).Content)!;
            Assert.Equal(10, (double)again["summary"]!["total_remaining_positive"]!);
            Assert.Single(Directory.GetFiles(snapDir, "snapshot-*.db"));

            // from here only the server reads: the data file must stay byte-identical
            before = Hash(path);
            stampBefore = File.GetLastWriteTimeUtc(path);
            src.Catalog().Call("list_wirs", new JsonObject());
            src.Catalog().Call("get_room", new JsonObject { ["room"] = "P2-106" });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(before, Hash(path));
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(snapDir, "snapshot-*.db"));
    }

    private static string Hash(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f));
    }

    [Fact]
    public void Host_parses_arguments_and_round_trips_citations()
    {
        var o = McpHost.Parse(new[] { "--mcp", "--db", @"C:\data\raffaello.db", "--user", "MOHAMED", "--cites-file", "c.jsonl" });
        Assert.Equal(@"C:\data\raffaello.db", o.Db);
        Assert.Equal("MOHAMED", o.User);
        Assert.Equal("c.jsonl", o.CitesFile);
        var file = Path.Combine(TestData.TempDir(), "cites.jsonl");
        McpHost.AppendCitations(file, "get_room", new[] { Citation.Room("P2-106") });
        File.AppendAllText(file, "{half a line\n");
        McpHost.AppendCitations(file, "get_invoice", new[] { Citation.Invoice("SUB-1", "ROOTS", 3) });
        var back = McpHost.ReadCitations(file);
        Assert.Equal(2, back.Count);
        Assert.Equal("Ledger", back[0].Module);
        Assert.Equal("SUB-1|ROOTS|3", back[1].NavKey);
    }
}

public class ClaudeLoginProviderTests
{
    private static readonly ClaudeCodeStatus Ready = new(true, @"C:\u\.local\bin\claude.exe", "2.1.287 (Claude Code)", true, "claude.ai", "ok");
    private static readonly ClaudeCodeStatus LoggedOut = new(true, @"C:\u\.local\bin\claude.exe", "2.1.287 (Claude Code)", false, "none", "not logged in");
    private static readonly ClaudeCodeStatus Missing = new(false, "", "", null, "", "not installed");

    [Theory]
    [InlineData("AUTO", true, "ready", AssistantRoute.Api)]
    [InlineData("AUTO", false, "ready", AssistantRoute.ClaudeCode)]
    [InlineData("AUTO", false, "out", AssistantRoute.Offline)]
    [InlineData("AUTO", false, "missing", AssistantRoute.Offline)]
    [InlineData("API KEY", true, "ready", AssistantRoute.Api)]
    [InlineData("API KEY", false, "ready", AssistantRoute.Offline)]
    [InlineData("CLAUDE LOGIN", true, "ready", AssistantRoute.ClaudeCode)]
    [InlineData("CLAUDE LOGIN", true, "out", AssistantRoute.Offline)]
    [InlineData("OFFLINE", true, "ready", AssistantRoute.Offline)]
    [InlineData("claude_login", false, "ready", AssistantRoute.ClaudeCode)]
    [InlineData("", false, "missing", AssistantRoute.Offline)]
    public void Provider_selection(string provider, bool key, string claude, AssistantRoute expected)
    {
        var st = claude switch { "ready" => Ready, "out" => LoggedOut, _ => Missing };
        var d = RouteDecision.Decide(provider, key, st);
        Assert.Equal(expected, d.Route);
        // forcing an engine that is not there says why; AUTO falls back quietly
        var p = AssistantProviders.Normalize(provider);
        if (expected == AssistantRoute.Offline && (p == AssistantProviders.ApiKey || p == AssistantProviders.ClaudeLogin)) Assert.True(d.Warn);
        if (p == AssistantProviders.Auto) Assert.False(d.Warn);
    }

    [Fact]
    public void Status_from_probe_outputs()
    {
        var none = ClaudeCodeLocator.Interpret(null, null, null, null);
        Assert.False(none.Found);
        Assert.Contains("install.ps1", none.Message);

        // the real output of "claude auth status --json" on a PC where the CLI was never logged in
        var outJson = """
            {
              "loggedIn": false,
              "authMethod": "none",
              "apiProvider": "firstParty"
            }
            """;
        var logged0 = ClaudeCodeLocator.Interpret(@"C:\x\claude.exe", "2.1.287 (Claude Code)\n", outJson, null);
        Assert.True(logged0.Found);
        Assert.False(logged0.Ready);
        Assert.Contains("NOT logged in", logged0.Message);
        Assert.Equal("2.1.287 (Claude Code)", logged0.Version);

        var ok = ClaudeCodeLocator.Interpret(@"C:\x\claude.exe", "2.1.287 (Claude Code)", """{"loggedIn":true,"authMethod":"claude.ai"}""", null);
        Assert.True(ok.Ready);
        Assert.Contains("logged in (claude.ai)", ok.Message);

        var old = ClaudeCodeLocator.Interpret(@"C:\x\claude.exe", "1.0.3", "error: unknown command 'auth'", null);
        Assert.True(old.Ready);           // cannot tell: try, and explain on failure
        Assert.Null(old.LoggedIn);

        var broken = ClaudeCodeLocator.Interpret(@"C:\x\claude.exe", null, null, "timed out");
        Assert.False(broken.Ready);
    }

    [Fact]
    public void Locator_prefers_the_setting_then_path_and_resolves_the_npm_shim()
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = @"C:\Tools;C:\Users\u\AppData\Roaming\npm", ["USERPROFILE"] = @"C:\Users\u", ["APPDATA"] = @"C:\Users\u\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local",
        };
        var c = ClaudeCodeLocator.Candidates(@"D:\claude\claude.exe", k => env.GetValueOrDefault(k)).ToList();
        Assert.Equal(@"D:\claude\claude.exe", c[0]);
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains(@"C:\Users\u\.local\bin\claude.exe", c);
        var shim = @"C:\Users\u\AppData\Roaming\npm\claude.cmd";
        var native = @"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shim, native };
        Assert.Equal(native, ClaudeCodeLocator.Find(null, k => env.GetValueOrDefault(k), files.Contains));
        Assert.Equal(shim, ClaudeCodeLocator.ResolveExecutable(shim, _ => false));
        Assert.Null(ClaudeCodeLocator.Find(null, k => env.GetValueOrDefault(k), _ => false));
    }

    [Fact]
    public void Run_arguments_allow_only_the_raffaello_tools()
    {
        var a = ClaudeCodeRunner.Arguments(@"C:\t\mcp.json", @"C:\t\system.txt", "sonnet", 12);
        Assert.Equal("-p", a[0]);
        Assert.Equal("", a[a.IndexOf("--tools") + 1]);                       // no built-in tools (no shell, files, web)
        Assert.Equal("mcp__raffaello", a[a.IndexOf("--allowedTools") + 1]);
        Assert.Contains("--strict-mcp-config", a);
        Assert.Contains("--no-session-persistence", a);
        Assert.Equal("stream-json", a[a.IndexOf("--output-format") + 1]);
        Assert.Equal("sonnet", a[a.IndexOf("--model") + 1]);
        Assert.DoesNotContain(a, x => x.Contains("Bash") || x.Contains("dangerously"));
        Assert.DoesNotContain("--model", ClaudeCodeRunner.Arguments("m", "s", null, 5));

        var cfg = JsonNode.Parse(ClaudeCodeRunner.McpConfig(@"C:\Raffaello\Raffaello.exe", new[] { "--mcp", "--db", @"D:\data\raffaello.db" }))!;
        Assert.Equal("stdio", (string?)cfg["mcpServers"]!["raffaello"]!["type"]);
        Assert.Equal(@"D:\data\raffaello.db", (string?)cfg["mcpServers"]!["raffaello"]!["args"]![2]);

        var desk = JsonNode.Parse(ClaudeDesktopConfig.Snippet(@"C:\Raffaello\Raffaello.exe", new[] { "--mcp" }))!;
        Assert.Equal(@"C:\Raffaello\Raffaello.exe", (string?)desk["mcpServers"]!["raffaello"]!["command"]);
    }
}
public class ClaudeCodeStreamTests
{
    [Fact]
    public void Not_logged_in_run_is_a_failure_with_a_plain_reason()
    {
        var acc = new ClaudeCodeAccumulator();
        ClaudeCodeFixtures.Feed(acc, ClaudeCodeFixtures.NotLoggedIn);
        Assert.True(acc.HasResult);
        Assert.True(acc.ResultIsError);
        Assert.Equal("authentication_failed", acc.ErrorKind);
        Assert.Contains("not logged in", acc.FailureReason());
        Assert.Equal("", acc.McpStatus);
    }

    [Fact]
    public void Streamed_text_is_not_repeated_and_tools_are_reported()
    {
        var acc = new ClaudeCodeAccumulator();
        var events = ClaudeCodeFixtures.Feed(acc, ClaudeCodeFixtures.ToolRun);
        Assert.Equal("connected", acc.McpStatus);
        Assert.Equal("Let me check.152 غرفة [[room:H1-L2-231]] still have 2ND FIX DATA remaining.", acc.Text);
        Assert.Equal(new[] { "rooms_remaining" }, acc.ToolsUsed);
        var use = events.Single(e => e.Kind == ClaudeCodeEventKind.ToolUse);
        Assert.Contains("GUESTROOM", use.Text);
        var result = events.Single(e => e.Kind == ClaudeCodeEventKind.ToolResult);
        Assert.Equal("rooms_remaining", result.ToolName);
        Assert.Contains("152", result.Text);
        Assert.Equal(0.0123, acc.CostUsd, 4);
        Assert.Equal(2, acc.Turns);
        Assert.Equal(2400, acc.InputTokens);
        Assert.Equal("", acc.FailureReason());
        Assert.Equal(ClaudeCodeEventKind.Result, events[^1].Kind);
    }

    [Fact]
    public void Without_partial_messages_the_text_comes_from_complete_messages()
    {
        var acc = new ClaudeCodeAccumulator();
        var events = ClaudeCodeFixtures.Feed(acc, ClaudeCodeFixtures.NoPartials);
        Assert.Equal("Checking the ledger.\n\nThe ledger could not be read.", acc.Text);
        Assert.True(events.Single(e => e.Kind == ClaudeCodeEventKind.ToolResult).IsError);
        Assert.Equal(new[] { "query_ledger" }, acc.ToolsUsed);
    }

    [Fact]
    public void Max_turns_is_not_an_error_but_is_flagged()
    {
        var acc = new ClaudeCodeAccumulator();
        ClaudeCodeFixtures.Feed(acc, ClaudeCodeFixtures.MaxTurns);
        Assert.Equal("error_max_turns", acc.ResultSubtype);
        Assert.False(acc.ResultIsError);
        Assert.Equal("", acc.FailureReason());
    }
}

public class ClaudeLoginSessionTests
{
    private static AssistantSession Session(AssistantEnv env, FakeClaudeRunner runner) => new(env.Data, _ => throw new InvalidOperationException("the API must not be called"))
    {
        Router = () => new RouteDecision(AssistantRoute.ClaudeCode, "test"),
        ClaudeCode = () => runner,
    };

    [Fact]
    public async Task Answer_streams_shows_tools_and_cites_records_from_the_side_channel()
    {
        using var env = AssistantEnv.Create();
        var runner = new FakeClaudeRunner();
        runner.Script = (req, on, _) =>
        {
            on?.Invoke(new ClaudeCodeEvent(ClaudeCodeEventKind.ToolUse, "{}", "get_room", "t1"));
            on?.Invoke(new ClaudeCodeEvent(ClaudeCodeEventKind.ToolResult, "{}", "get_room", "t1"));
            on?.Invoke(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, "P2-106 has 14 left [[room:P2-106]]."));
            var r = new ClaudeCodeResult { Text = "P2-106 has 14 left [[room:P2-106]].", Model = "claude-sonnet-5", Turns = 2 };
            r.ToolsUsed.Add("get_room");
            r.Citations.Add(Citation.Room("P2-106"));
            return Task.FromResult(r);
        };
        var s = Session(env, runner);
        var events = new List<AssistantEvent>();
        var reply = await s.AskAsync("What is left in P2-106 2nd fix light?", new ScreenContext("Ledger"), null, events.Add);

        Assert.False(reply.Offline);
        Assert.Equal("P2-106 has 14 left.", reply.Text);
        Assert.Contains(reply.Citations, c => c.Key == "P2-106" && c.Module == "Ledger");
        Assert.Contains(events, e => e.Kind == AssistantEventKind.ToolStarted && e.Text == "get_room");
        Assert.Contains(events, e => e.Kind == AssistantEventKind.TextDelta);
        var req = runner.Requests.Single();
        Assert.Contains("<app_context>", req.Prompt);
        Assert.EndsWith("What is left in P2-106 2nd fix light?", req.Prompt);
        Assert.Contains("READ-ONLY", req.SystemPrompt);
        var stored = s.Bubbles();
        Assert.Equal(2, stored.Count);
        Assert.Equal("P2-106 has 14 left.", stored[1].Text);
        Assert.StartsWith("claude-code", s.Conversation!.Model);
    }

    [Fact]
    public async Task Second_question_carries_the_conversation_and_arabic_survives()
    {
        using var env = AssistantEnv.Create();
        var runner = new FakeClaudeRunner();
        var answers = new Queue<string>(new[] { "الغرفة P2-106 متبقي 14 نقطة.", "ROOTS claimed 59." });
        runner.Script = (_, _, _) => Task.FromResult(new ClaudeCodeResult { Text = answers.Dequeue() });
        var s = Session(env, runner);
        await s.AskAsync("كم المتبقي في الغرفة P2-106؟", new ScreenContext());
        var second = await s.AskAsync("and ROOTS?", new ScreenContext());
        Assert.Equal("ROOTS claimed 59.", second.Text);
        var prompt = runner.Requests[1].Prompt;
        Assert.Contains("<conversation_so_far>", prompt);
        Assert.Contains("USER: كم المتبقي في الغرفة P2-106؟", prompt);
        Assert.Contains("RAFFAELLO: الغرفة P2-106 متبقي 14 نقطة.", prompt);
        Assert.DoesNotContain("<conversation_so_far>", runner.Requests[0].Prompt);
    }

    [Fact]
    public async Task Failure_falls_back_to_the_offline_answer_with_the_reason()
    {
        using var env = AssistantEnv.Create();
        var runner = new FakeClaudeRunner { Script = (_, _, _) => Task.FromResult(new ClaudeCodeResult { Error = "Claude Code is not logged in. Open a terminal, run  claude  once." }) };
        var reply = await Session(env, runner).AskAsync("remaining in P2-106", new ScreenContext());
        Assert.True(reply.Offline);
        Assert.Contains("not logged in", reply.Text);
        Assert.Contains("not logged in", reply.Notice);
    }

    [Fact]
    public async Task Stop_cancels_and_keeps_what_was_streamed()
    {
        using var env = AssistantEnv.Create();
        using var cts = new CancellationTokenSource();
        var runner = new FakeClaudeRunner
        {
            Script = async (_, on, ct) =>
            {
                on?.Invoke(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, "Partial answer"));
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return new ClaudeCodeResult();
            },
        };
        var s = Session(env, runner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.AskAsync("long question", new ScreenContext(), null, null, cts.Token));
        Assert.Contains(s.Bubbles(), b => b.Role == "assistant" && b.Text.Contains("Partial answer [stopped]"));
    }

    [Fact]
    public async Task Offline_route_never_calls_claude_and_explains_a_forced_engine()
    {
        using var env = AssistantEnv.Create();
        var runner = new FakeClaudeRunner();
        var s = new AssistantSession(env.Data) { Router = () => new RouteDecision(AssistantRoute.Offline, "CLAUDE LOGIN chosen but Claude Code is not installed.", true), ClaudeCode = () => runner };
        var reply = await s.AskAsync("remaining in P2-106", new ScreenContext());
        Assert.True(reply.Offline);
        Assert.Contains("not installed", reply.Text);
        Assert.Empty(runner.Requests);
        Assert.StartsWith("OFFLINE", s.PrivacySummary());
    }

    [Fact]
    public void Prompt_keeps_attachment_text_and_flags_scans()
    {
        var att = new ChatAttachment { FileName = "dn.pdf", Blocks = { new JsonObject { ["type"] = "text", ["text"] = "<attachment name=\"dn.pdf\">DN 81064344</attachment>" }, new JsonObject { ["type"] = "image" } } };
        var p = AssistantSession.ClaudeCodePrompt("", "<app_context>\nx\n</app_context>", new[] { att }, Array.Empty<ChatBubble>());
        Assert.Contains("DN 81064344", p);
        Assert.Contains("dn.pdf is a scan / image", p);
        Assert.EndsWith("what needs my attention.", p);
    }

    [Fact]
    public void Tokens_become_navigable_citations_without_the_side_channel()
    {
        var c = AssistantSession.CitationsFromTokens("x [[room:P2-106]] y [[invoice:SUB-1|ROOTS|3]] z [[wir:WIR-EL-0001]] [[odd:1]]");
        Assert.Equal("Ledger", c[0].Module);
        Assert.Equal("Invoices", c[1].Module);
        Assert.Equal("SUB-1|ROOTS|3", c[1].NavKey);
        Assert.Equal("Wir", c[2].Module);
        Assert.Equal("odd", c[3].Kind);
    }
}

/// <summary>The real runner against a stand-in "claude" (.cmd script): replay, timeout, cancel. Windows only.</summary>
public class ClaudeCodeRunnerProcessTests
{
    private static string FakeClaude(string body)
    {
        var dir = TestData.TempDir();
        var cmd = Path.Combine(dir, "claude.cmd");
        File.WriteAllText(cmd, "@echo off\r\n" + body + "\r\n", new UTF8Encoding(false));
        return cmd;
    }

    private static string Replay(string fixture)
    {
        var cmd = FakeClaude("type \"%~dp0out.jsonl\"");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cmd)!, "out.jsonl"), fixture.Replace("\r\n", "\n"), new UTF8Encoding(false));
        return cmd;
    }

    [Fact]
    public async Task Replays_stream_json_from_the_cli()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new ClaudeCodeRunner(Replay(ClaudeCodeFixtures.ToolRun), "raffaello.exe", new[] { "--mcp" }) { WorkRoot = TestData.TempDir() };
        var deltas = new StringBuilder();
        var res = await runner.RunAsync(new ClaudeCodeRequest("سؤال", "system", null, 4, TimeSpan.FromSeconds(60)),
            e => { if (e.Kind == ClaudeCodeEventKind.TextDelta) deltas.Append(e.Text); }, CancellationToken.None);
        Assert.False(res.Failed, res.Error + res.Stderr);
        Assert.Contains("2ND FIX DATA remaining", res.Text);
        Assert.Contains("غرفة", res.Text);
        Assert.Equal(res.Text, deltas.ToString().Trim());
        Assert.Equal(new[] { "rooms_remaining" }, res.ToolsUsed);
        Assert.Empty(Directory.GetDirectories(runner.WorkRoot));   // temp config removed
    }

    [Fact]
    public async Task Not_logged_in_output_is_reported()
    {
        if (!OperatingSystem.IsWindows()) return;
        var res = await new ClaudeCodeRunner(Replay(ClaudeCodeFixtures.NotLoggedIn), "x", Array.Empty<string>()) { WorkRoot = TestData.TempDir() }
            .RunAsync(new ClaudeCodeRequest("q", "s", null, 4, TimeSpan.FromSeconds(60)), null, CancellationToken.None);
        Assert.True(res.Failed);
        Assert.Contains("not logged in", res.Error);
    }

    [Fact]
    public async Task Timeout_kills_the_run_and_says_so()
    {
        if (!OperatingSystem.IsWindows()) return;
        var started = DateTime.UtcNow;
        var res = await new ClaudeCodeRunner(FakeClaude("ping -n 30 127.0.0.1 >nul"), "x", Array.Empty<string>()) { WorkRoot = TestData.TempDir() }
            .RunAsync(new ClaudeCodeRequest("q", "s", null, 4, TimeSpan.FromSeconds(2)), null, CancellationToken.None);
        Assert.True(res.Failed);
        Assert.Contains("did not finish within 2 s", res.Error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Stop_cancels_the_run()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeCodeRunner(FakeClaude("ping -n 30 127.0.0.1 >nul"), "x", Array.Empty<string>()) { WorkRoot = TestData.TempDir() }
            .RunAsync(new ClaudeCodeRequest("q", "s", null, 4, TimeSpan.FromSeconds(60)), null, cts.Token));
    }
}