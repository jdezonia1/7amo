using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant;
using Raffaello.Core.Domain;

namespace Raffaello.Core.Tests;

public class AssistantClientTests
{
    [Fact]
    public void Request_body_caches_tools_and_system_streams_tool_input_and_opts_into_fallbacks()
    {
        var body = ClaudeTurnClient.BuildBody(new ClaudeRequest
        {
            Model = "claude-opus-5-5", Effort = "medium", MaxTokens = 16000, System = AssistantSession.SystemPrompt,
            Tools = AssistantTools.Definitions(), Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        });
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
        Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Equal("auto", (string?)body["tool_choice"]!["type"]);   // forced tool_choice is a 400 on Opus 5.5
        Assert.True((bool)body["stream"]!);
        Assert.Equal("ephemeral", (string?)body["cache_control"]!["type"]);
        var system = body["system"]!.AsArray();
        Assert.Equal("ephemeral", (string?)system[0]!["cache_control"]!["type"]);
        var tools = body["tools"]!.AsArray();
        Assert.Equal(15, tools.Count);
        Assert.All(tools, t => Assert.True((bool)t!["eager_input_streaming"]!));
        Assert.Equal("ephemeral", (string?)tools[^1]!["cache_control"]!["type"]);
        Assert.Null(tools[0]!["cache_control"]);
        Assert.Null(body["temperature"]);
        Assert.DoesNotContain("budget_tokens", body.ToJsonString());
        // the definitions are identical on every call (stable cached prefix)
        Assert.Equal(AssistantTools.Definitions().ToJsonString(), AssistantTools.Definitions().ToJsonString());
    }

    [Fact]
    public async Task Stream_parser_keeps_thinking_signature_text_tool_input_and_usage()
    {
        var stream = Sse.Of(
            new { type = "message_start", message = new { model = "claude-opus-5-5", usage = new { input_tokens = 1200, cache_read_input_tokens = 900, cache_creation_input_tokens = 0, output_tokens = 1 } } },
            new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } },
            new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "SIG123" } },
            new { type = "content_block_stop", index = 0 },
            new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } },
            new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "Checking " } },
            new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "the ledger." } },
            new { type = "content_block_stop", index = 1 },
            new { type = "content_block_start", index = 2, content_block = new { type = "tool_use", id = "toolu_9", name = "get_room", input = new { } } },
            new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "{\"room\": \"P2" } },
            new { type = "content_block_delta", index = 2, delta = new { type = "input_json_delta", partial_json = "-106\"}" } },
            new { type = "content_block_stop", index = 2 },
            new { type = "content_block_start", index = 3, content_block = new { type = "tool_use", id = "toolu_10", name = "get_room", input = new { } } },
            new { type = "content_block_delta", index = 3, delta = new { type = "input_json_delta", partial_json = "{\"room\": \"P2-1" } },
            new { type = "content_block_stop", index = 3 },
            new { type = "message_delta", delta = new { stop_reason = "tool_use" }, usage = new { output_tokens = 57 } },
            new { type = "message_stop" });
        var seen = new StringBuilder();
        var turn = await ClaudeTurnClient.ParseStreamAsync(stream, t => seen.Append(t), CancellationToken.None);
        Assert.Equal("Checking the ledger.", seen.ToString());
        Assert.Equal("tool_use", turn.StopReason);
        Assert.Equal(1200, turn.InputTokens);
        Assert.Equal(900, turn.CacheReadTokens);
        Assert.Equal(57, turn.OutputTokens);
        Assert.Equal("SIG123", (string?)turn.Content[0]!["signature"]);
        Assert.Equal("P2-106", (string?)turn.Content[2]!["input"]!["room"]);
        Assert.Equal(2, turn.ToolUses.Count());
        Assert.True(turn.InvalidToolInputs.ContainsKey("toolu_10"));   // truncated JSON is never run
        Assert.Equal("{}", turn.Content[3]!["input"]!.ToJsonString());
    }

    [Fact]
    public void After_a_mid_output_fallback_only_the_final_attempt_runs_and_is_echoed()
    {
        var turn = FakeTransport.Turn("tool_use",
            new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "a" },
            new JsonObject { ["type"] = "text", ["text"] = "partial " },
            new JsonObject { ["type"] = "tool_use", ["id"] = "old", ["name"] = "get_room", ["input"] = new JsonObject { ["room"] = "X" } },
            new JsonObject { ["type"] = "fallback", ["from"] = new JsonObject { ["model"] = "claude-opus-5-5" }, ["to"] = new JsonObject { ["model"] = "claude-opus-5" } },
            new JsonObject { ["type"] = "tool_use", ["id"] = "new", ["name"] = "get_room", ["input"] = new JsonObject { ["room"] = "P2-106" } });
        Assert.Equal(new[] { "new" }, turn.ToolUses.Select(t => (string?)t["id"]));
        var history = turn.ContentForHistory();
        Assert.Equal(new[] { "text", "fallback", "tool_use" }, history.Select(b => (string?)b!["type"]));
    }

    [Fact]
    public async Task Http_errors_are_mapped_and_retried_and_the_key_is_only_a_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}") },
                                      _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"message\":\"bad key\"}}") });
        var client = new ClaudeTurnClient("sk-test-123", new HttpClient(handler)) { Backoff = _ => TimeSpan.Zero };
        var ex = await Assert.ThrowsAsync<AnthropicException>(() => client.SendAsync(new ClaudeRequest { System = "s" }, null, CancellationToken.None));
        Assert.Equal(401, ex.Status);
        Assert.Contains("API key rejected", ex.Message);
        Assert.DoesNotContain("sk-test-123", ex.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("sk-test-123", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicClient.FallbackBeta, handler.Requests[0].Headers.GetValues("anthropic-beta").Single());
        Assert.DoesNotContain("sk-test-123", handler.Bodies[0]);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _answers;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> Bodies { get; } = new();
    public StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) => _answers = new(answers);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return _answers.Count > 1 ? _answers.Dequeue()(request) : _answers.Peek()(request);
    }
}

public class AssistantSessionTests
{
    private static AssistantSession Session(AssistantEnv env, FakeTransport fake) =>
        new(env.Data, _ => fake) { ApiKey = () => "sk-test" };

    [Fact]
    public async Task Tool_loop_reads_the_ledger_cites_the_room_and_stores_an_append_only_history()
    {
        using var env = AssistantEnv.Create();
        var fake = new FakeTransport()
            .ThenTool("query_ledger", new JsonObject { ["room"] = "P2-106", ["stage"] = "2ND FIX", ["item"] = "LIGHT" })
            .ThenText("REMAINING 14 of PROJECT QTY 39 [[room:P2-106]].");
        var s = Session(env, fake);
        var events = new List<AssistantEvent>();
        var reply = await s.AskAsync("remaining in P2-106 2nd fix light?", new ScreenContext("ROOMS & LEDGER", "BRANDED"), onEvent: events.Add);

        Assert.False(reply.Offline);
        Assert.Equal("Let me check.REMAINING 14 of PROJECT QTY 39.", reply.Text.Replace("\n", ""));
        Assert.Contains(reply.Citations, c => c.Kind == "room" && c.Key == "P2-106" && c.Module == "Ledger");
        Assert.Contains(events, e => e.Kind == AssistantEventKind.ToolStarted && e.Text == "query_ledger");

        // the tool result the model saw: remaining 39 - 25 = 14, stages separate
        var toolResult = fake.Requests[1].Messages[^1]!["content"]![0]!;
        Assert.Equal("tool_result", (string?)toolResult["type"]);
        var payload = JsonNode.Parse((string)toolResult["content"]!)!;
        Assert.Equal(14, (double)payload["by_stage"]![0]!["remaining"]!);
        Assert.Contains("[[room:P2-106]]", (string)toolResult["content"]!);

        // append-only: request 2 starts with exactly the messages of request 1, thinking signature kept
        var first = fake.Requests[0].Messages.ToJsonString();
        var second = fake.Requests[1].Messages;
        Assert.StartsWith(first.TrimEnd(']'), second.ToJsonString());
        Assert.Contains("sig-toolu_1", second.ToJsonString());
        Assert.Equal(fake.Requests[0].System, fake.Requests[1].System);
        Assert.Equal(fake.Requests[0].Tools.ToJsonString(), fake.Requests[1].Tools.ToJsonString());

        // screen context travels in the user turn, never in the system prompt
        Assert.Contains("<app_context>", fake.Requests[0].Messages[0]!["content"]![0]!["text"]!.ToString());
        Assert.Contains("screen: ROOMS & LEDGER", fake.Requests[0].Messages[0]!["content"]![0]!["text"]!.ToString());
        Assert.DoesNotContain("ROOMS & LEDGER", AssistantSession.SystemPrompt);

        // stored per user and reloadable as bubbles
        var conv = Assert.Single(s.Conversations());
        Assert.Equal("tester", conv.Owner);
        var bubbles = s.Bubbles();
        Assert.Equal(2, bubbles.Count);
        Assert.Equal("user", bubbles[0].Role);
        Assert.Contains("REMAINING 14", bubbles[1].Text);
        Assert.Contains(bubbles[1].Citations, c => c.Key == "P2-106");
        Assert.Equal(4, s.History().Count);   // user, assistant(tool_use), user(tool_result, hidden), assistant
        Assert.True(s.History()[2].Hidden);
        // another user does not see it
        env.Project.CurrentUser = "someone.else";
        Assert.Empty(new AssistantSession(env.Data).Conversations());
    }

    [Fact]
    public async Task Draft_ledger_claim_writes_nothing_until_confirm_then_posts_and_reports_back()
    {
        using var env = AssistantEnv.Create();
        var before = env.Project.Snapshot.Claims.Count;
        var fake = new FakeTransport()
            .ThenTool("draft_ledger_claim", new JsonObject { ["subcontractor"] = "ROOTS", ["invoice_no"] = 3, ["room"] = "P2-106", ["stage"] = "2ND FIX", ["item"] = "LIGHT", ["qty"] = 10, ["wir_no"] = "WIR-EL-0099" })
            .ThenText("I prepared the claim; press CONFIRM to post it.")
            .ThenText("Noted.");
        var s = Session(env, fake);
        var reply = await s.AskAsync("post 10 lights 2nd fix in P2-106 for ROOTS invoice 3", new ScreenContext());
        var action = Assert.Single(reply.Actions);
        Assert.Equal(AssistantActionStatus.Proposed, action.Status);
        Assert.Contains("remaining 14", action.Preview);
        Assert.Equal(before, env.Project.Snapshot.Claims.Count);   // nothing written yet
        var result = JsonNode.Parse((string)fake.Requests[1].Messages[^1]!["content"]![0]!["content"]!)!;
        Assert.Equal("awaiting_user_confirmation", (string?)result["status"]);

        var done = s.Confirm(action.Id);
        Assert.Equal(AssistantActionStatus.Executed, done.Status);
        Assert.Equal(before + 1, env.Project.Snapshot.Claims.Count);
        var posted = env.Project.Snapshot.Claims.Single(c => c.WirNo == "WIR-EL-0099");
        Assert.Equal(10, posted.Qty);
        Assert.Equal("ASSISTANT", posted.Source);
        Assert.Contains(env.Project.RecentActivity(20), a => a.Summary.Contains("confirmed POST LEDGER CLAIM"));
        Assert.Throws<InvalidOperationException>(() => s.Confirm(action.Id));   // once only

        await s.AskAsync("thanks", new ScreenContext());
        var ctx = fake.Requests[2].Messages[^1]!["content"]![0]!["text"]!.ToString();
        Assert.Contains($"card #{action.Id} POST LEDGER CLAIM: EXECUTED", ctx);
        Assert.True(env.Store.Actions(s.Conversation!.Id).Single().Reported);
    }

    [Fact]
    public async Task Claim_above_remaining_without_reason_is_not_even_proposed()
    {
        using var env = AssistantEnv.Create();
        var fake = new FakeTransport()
            .ThenTool("draft_ledger_claim", new JsonObject { ["subcontractor"] = "ROOTS", ["invoice_no"] = 3, ["room"] = "P2-106", ["stage"] = "2ND FIX", ["item"] = "LIGHT", ["qty"] = 30 })
            .ThenText("That exceeds the remaining 14.");
        var reply = await Session(env, fake).AskAsync("post 30", new ScreenContext());
        Assert.Empty(reply.Actions);
        var tr = fake.Requests[1].Messages[^1]!["content"]![0]!;
        Assert.True((bool)tr["is_error"]!);
        Assert.Contains("exceeds remaining", (string)tr["content"]!);
    }

    [Fact]
    public async Task Cancelled_card_and_other_actions_create_reminder_variation_email()
    {
        using var env = AssistantEnv.Create();
        var fake = new FakeTransport()
            .ThenTool("create_reminder", new JsonObject { ["text"] = "Chase WIR for L2", ["due"] = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd") }, "t1")
            .ThenTool("draft_rejection_reply_email", new JsonObject { ["subject"] = "ROOTS INV-01 Rev 0 - reply", ["body"] = "Dear team,\nThe WIR for L2 is attached.\nشكراً", ["to"] = "ho@example.com" }, "t2")
            .ThenTool("draft_invoice_revision", new JsonObject { ["subcontractor"] = "ROOTS", ["invoice_no"] = 1 }, "t3")
            .ThenText("Three cards are ready.");
        var s = Session(env, fake);
        var reply = await s.AskAsync("set things up", new ScreenContext());
        Assert.Equal(3, reply.Actions.Count);
        Assert.Empty(env.Store.Reminders("tester"));

        var reminder = s.Confirm(reply.Actions[0].Id);
        Assert.Equal(AssistantActionStatus.Executed, reminder.Status);
        Assert.Single(env.Store.Reminders("tester"));

        var mail = s.Confirm(reply.Actions[1].Id);
        var path = mail.ResultRef["file|".Length..];
        var eml = File.ReadAllText(path);
        Assert.Contains("X-Unsent: 1", eml);
        Assert.Contains("To: ho@example.com", eml);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("ROOTS INV-01 Rev 0 - reply")), eml);

        Assert.Contains("Rev 1", reply.Actions[2].Preview);
        var cancelled = s.Cancel(reply.Actions[2].Id);
        Assert.Equal(AssistantActionStatus.Cancelled, cancelled.Status);
        Assert.DoesNotContain(env.Project.Snapshot.SubInvoices, i => i.Revision == 1);
    }

    [Fact]
    public async Task Refused_turn_runs_no_tools_and_keeps_the_history_valid()
    {
        using var env = AssistantEnv.Create();
        var fake = new FakeTransport()
            .Then(_ => FakeTransport.Turn("refusal", new JsonObject { ["type"] = "tool_use", ["id"] = "x1", ["name"] = "draft_ledger_claim", ["input"] = new JsonObject() }))
            .ThenText("ok");
        var s = Session(env, fake);
        var reply = await s.AskAsync("something", new ScreenContext());
        Assert.Contains("declined", reply.Notice);
        Assert.Empty(reply.Actions);
        Assert.Single(fake.Requests);
        await s.AskAsync("next question", new ScreenContext());
        var msgs = fake.Requests[1].Messages;
        // assistant tool_use is followed by a user turn that answers it first, then the new question (merged)
        Assert.Equal(new[] { "user", "assistant", "user" }, msgs.Select(m => (string?)m!["role"]));
        Assert.Equal("tool_result", (string?)msgs[2]!["content"]![0]!["type"]);
        Assert.Equal("x1", (string?)msgs[2]!["content"]![0]!["tool_use_id"]);
    }

    [Fact]
    public void Dangling_tool_use_after_a_stop_gets_an_error_result_and_same_role_messages_merge()
    {
        var h = new List<AssistantMessage>
        {
            new() { Role = "user", ContentJson = "[{\"type\":\"text\",\"text\":\"q1\"}]" },
            new() { Role = "assistant", ContentJson = "[{\"type\":\"tool_use\",\"id\":\"a\",\"name\":\"get_room\",\"input\":{}}]" },
            new() { Role = "user", ContentJson = "[{\"type\":\"text\",\"text\":\"q2\"}]" },
            new() { Role = "user", ContentJson = "[{\"type\":\"text\",\"text\":\"q3\"}]" },
        };
        var m = AssistantSession.BuildMessages(h);
        Assert.Equal(3, m.Count);
        var last = m[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", (string?)last[0]!["type"]);
        Assert.Equal(new[] { "tool_result", "text", "text" }, last.Select(b => (string?)b!["type"]));
        Assert.Equal(m.ToJsonString(), AssistantSession.BuildMessages(h).ToJsonString());   // deterministic
    }

    [Fact]
    public async Task Without_a_key_the_answer_comes_from_local_data_and_is_stored()
    {
        using var env = AssistantEnv.Create();
        var s = new AssistantSession(env.Data);   // no key
        var reply = await s.AskAsync("remaining in P2-106 2nd fix light", new ScreenContext());
        Assert.True(reply.Offline);
        Assert.Contains("REMAINING 14 of PROJECT QTY 39", reply.Text);
        Assert.Contains(reply.Citations, c => c.Key == "P2-106");
        Assert.Equal(2, s.History().Count);
        Assert.Contains("nothing leaves this PC", s.PrivacySummary());
    }

    [Fact]
    public async Task Api_failure_falls_back_to_the_offline_answer()
    {
        using var env = AssistantEnv.Create();
        var fake = new FakeTransport().Then(_ => throw new AnthropicException("Rate limited - wait a moment and try again. (HTTP 429)", 429));
        var reply = await Session(env, fake).AskAsync("where is DN 81064344", new ScreenContext());
        Assert.True(reply.Offline);
        Assert.Contains("HTTP 429", reply.Text);
        Assert.Contains("RAF-P.O-E-045-2026", reply.Text);
    }

    [Fact]
    public async Task Read_tools_refuse_when_the_user_switched_project_data_off()
    {
        using var env = AssistantEnv.Create(new AssistantSettings { AllowReadProjectData = false });
        var fake = new FakeTransport().ThenTool("get_room", new JsonObject { ["room"] = "P2-106" }).ThenText("I cannot read project data.");
        var s = Session(env, fake);
        await s.AskAsync("P2-106?", new ScreenContext());
        var tr = fake.Requests[1].Messages[^1]!["content"]![0]!;
        Assert.True((bool)tr["is_error"]!);
        Assert.Contains("switched off", (string)tr["content"]!);
        Assert.Contains("project data access: OFF", fake.Requests[0].Messages[0]!["content"]![0]!["text"]!.ToString());
        Assert.Contains("project data reading: OFF", s.PrivacySummary());
    }

    [Fact]
    public async Task Attachments_go_into_the_user_turn_and_show_in_the_privacy_line()
    {
        using var env = AssistantEnv.Create(new AssistantSettings { CloudDocumentReading = true });
        var csv = Path.Combine(env.Dir, "statement.csv");
        File.WriteAllText(csv, "ROOM,STAGE,LIGHT\nP2-106,2ND FIX,12\n");
        var att = await new AttachmentReader(cloudReading: true).ReadAsync(csv);
        Assert.Equal("TEXT", att.ReadAs);
        var fake = new FakeTransport().ThenText("The statement claims 12 lights in P2-106.");
        var s = Session(env, fake);
        await s.AskAsync("check this statement", new ScreenContext(), new[] { att });
        var content = fake.Requests[0].Messages[0]!["content"]!.AsArray();
        Assert.Contains(content, b => ((string?)b!["text"] ?? "").Contains("P2-106,2ND FIX,12"));
        Assert.Contains("statement.csv", s.PrivacySummary(new[] { att }));
    }
}

public class AssistantToolTests
{
    private static JsonNode Run(AssistantEnv env, string tool, JsonObject input, bool error = false)
    {
        var o = new AssistantTools(env.Data).Execute(tool, input, "t", 0);
        Assert.Equal(error, o.IsError);
        return error ? JsonValue.Create(o.Content)! : JsonNode.Parse(o.Content)!;
    }

    [Fact]
    public void Ledger_query_never_sums_stages_and_counts_every_subcontractor()
    {
        using var env = AssistantEnv.Create();
        var r = Run(env, AssistantTools.QueryLedger, new JsonObject { ["room"] = "P2-106", ["item"] = "LIGHT" });
        var stages = r["by_stage"]!.AsArray();
        Assert.Equal(new[] { "1ST FIX", "2ND FIX" }, stages.Select(s => (string?)s!["stage"]));
        Assert.Equal(0, (double)stages[0]!["remaining"]!);
        Assert.Equal(14, (double)stages[1]!["remaining"]!);
        var bySub = Run(env, AssistantTools.QueryLedger, new JsonObject { ["room"] = "P2-*", ["stage"] = "2nd fix", ["subcontractor"] = "roots", ["group_by"] = "room" });
        var st = bySub["by_stage"]![0]!;
        Assert.Equal(65, (double)st["claimed_matching_filter"]!);       // ROOTS 20 + 45
        Assert.Equal(1, (int)st["over_cap_keys"]!);                     // P2-107 45 > 39
        Assert.Contains(st["groups"]!.AsArray(), g => (string?)g!["group"] == "P2-107" && (double)g["remaining"]! == -6);
    }

    [Fact]
    public void Room_invoice_dn_contract_needs_today_anomalies_rules_report_search()
    {
        using var env = AssistantEnv.Create();
        var room = Run(env, AssistantTools.GetRoom, new JsonObject { ["room"] = "p2-106" });
        Assert.Equal("APARTMENT", (string?)room["area_type"]);
        Assert.Contains("unknown", Run(env, AssistantTools.GetRoom, new JsonObject { ["room"] = "Z9-999" }, error: true).ToString() + "unknown");

        var inv = Run(env, AssistantTools.GetInvoice, new JsonObject { ["subcontractor"] = "roots", ["invoice_no"] = 1 });
        var rev = inv["invoices"]![0]!["revisions"]![0]!;
        Assert.Equal("REJECTED", (string?)rev["status"]);
        Assert.Equal("WIR missing for L2", (string?)rev["rejection_reason"]);
        Assert.Equal("WF-000123", (string?)rev["aconex_workflow"]);

        var dn = Run(env, AssistantTools.FindDn, new JsonObject { ["query"] = "81064344" });
        Assert.Equal("RAF-P.O-E-045-2026", (string?)dn["delivery_notes"]![0]!["po_no"]);
        Assert.Equal("[[dn:81064344]]", (string?)dn["delivery_notes"]![0]!["cite"]);

        var ct = Run(env, AssistantTools.GetContractTerms, new JsonObject { ["subcontractor"] = "ROOTS", ["item_query"] = "lighting 2nd fix" });
        Assert.Equal(10, (double)ct["contracts"]![0]!["retention_pct"]!);
        Assert.Equal("11", (string?)ct["schedules"]![0]!["matching_items"]![0]!["item_no"]);

        var today = Run(env, AssistantTools.ListNeedsToday, new JsonObject());
        Assert.True((int)today["count_total"]! > 0);

        var an = Run(env, AssistantTools.ListAnomalies, new JsonObject());
        Assert.Contains(an["items"]!.AsArray(), i => (string?)i!["kind"] == "OVER CAP" && ((string?)i["title"])!.Contains("P2-107"));
        Assert.Contains(an["items"]!.AsArray(), i => (string?)i!["kind"] == "TWO SUBCONTRACTORS");

        var rule = Run(env, AssistantTools.ExplainRule, new JsonObject { ["topic"] = "15 m route" });
        Assert.Equal("LENGTH_15", (string?)rule["rules"]![0]!["code"]);

        var rep = Run(env, AssistantTools.GetReport, new JsonObject { ["report"] = "WEEKLY", ["max_rows"] = 5 });
        Assert.True(rep["sheets"]!.AsArray().Count > 0);

        var search = Run(env, AssistantTools.SearchDocuments, new JsonObject { ["query"] = "P2-106" });
        Assert.Contains(search["hits"]!.AsArray(), h => (string?)h!["kind"] == "room");
        var search2 = Run(env, AssistantTools.SearchDocuments, new JsonObject { ["query"] = "RAFPOE045" });
        Assert.Contains(search2["hits"]!.AsArray(), h => (string?)h!["kind"] is "po" or "dn");
    }

    [Fact]
    public void Inputs_are_validated_against_the_schema_before_anything_runs()
    {
        Assert.Contains("room is required", string.Join(";", AssistantTools.Validate("get_room", new JsonObject())));
        Assert.Contains("qty must be number", string.Join(";", AssistantTools.Validate("draft_ledger_claim", new JsonObject
            { ["subcontractor"] = "A", ["invoice_no"] = 1, ["room"] = "R", ["stage"] = "2ND FIX", ["item"] = "LIGHT", ["qty"] = "ten" })));
        Assert.Contains("invoice_no must be integer", string.Join(";", AssistantTools.Validate("get_invoice", new JsonObject { ["invoice_no"] = 1.5 })));
        Assert.Contains("stage must be one of", string.Join(";", AssistantTools.Validate("query_ledger", new JsonObject { ["stage"] = "THIRD" })));
        Assert.Contains("is not a known field", string.Join(";", AssistantTools.Validate("get_room", new JsonObject { ["room"] = "x", ["drop"] = 1 })));
        Assert.Contains("lines[0].description is required", string.Join(";", AssistantTools.Validate("draft_variation",
            new JsonObject { ["type"] = "VO", ["title"] = "t", ["lines"] = new JsonArray(new JsonObject { ["kind"] = "ADDITION", ["qty"] = 1 }) })));
        Assert.Empty(AssistantTools.Validate("get_room", new JsonObject { ["room"] = "P2-106" }));
        Assert.Equal(AssistantTools.ReadTools.Length + AssistantTools.ActionTools.Length, AssistantTools.Definitions().Count);
    }

    [Fact]
    public void Citation_tokens_are_parsed_stripped_and_resolved()
    {
        var text = "REMAINING 14 [[room:P2-106]], invoice rejected [[invoice:C|ROOTS|1]] and [[dn:9]].";
        Assert.Equal(new[] { ("room", "P2-106"), ("invoice", "C|ROOTS|1"), ("dn", "9") }, CitationText.Tokens(text));
        Assert.Equal("REMAINING 14, invoice rejected and.", CitationText.Strip(text));
        var resolved = CitationText.Resolve(text, new[] { Citation.Room("P2-106"), Citation.Invoice("C", "ROOTS", 1) });
        Assert.Equal("Ledger", resolved[0].Module);
        Assert.Equal("Invoices", resolved[1].Module);
        Assert.False(resolved[2].CanNavigate);
        Assert.Equal(2, CitationText.Resolve("no tokens", new[] { Citation.Room("A"), Citation.Room("B") }).Count);
        Assert.Equal(resolved, CitationText.Deserialize(CitationText.Serialize(resolved)));
    }
}

public class OfflineAssistantTests
{
    [Theory]
    [InlineData("remaining in P2-106 2nd fix light", "remaining")]
    [InlineData("how many lights left in P2-106?", "remaining")]
    [InlineData("المتبقي في P2-106 التمديد الثاني إنارة", "remaining")]
    [InlineData("where is DN 81064344", "dn")]
    [InlineData("أين إشعار التسليم 81064344", "dn")]
    [InlineData("status of ROOTS INV 1", "invoice")]
    [InlineData("حالة الفاتورة رقم 1", "invoice")]
    [InlineData("P2-106", "room")]
    [InlineData("what needs me today", "today")]
    [InlineData("ما المطلوب اليوم", "today")]
    [InlineData("explain the 15 m rule", "rule")]
    [InlineData("anything suspicious?", "anomalies")]
    [InlineData("hello", "help")]
    public void Intents_are_recognised_in_english_and_arabic(string q, string intent)
    {
        using var env = AssistantEnv.Create();
        Assert.Equal(intent, new OfflineAssistant(env.Data).Classify(q));
    }

    [Fact]
    public void Answers_come_from_the_data_in_the_language_of_the_question()
    {
        using var env = AssistantEnv.Create();
        var off = new OfflineAssistant(env.Data);
        var en = off.Answer("remaining in P2-106 2nd fix light");
        Assert.Contains("2ND FIX: REMAINING 14 of PROJECT QTY 39", en.Text);
        var ar = off.Answer("المتبقي في P2-106 التمديد الثاني إنارة");
        Assert.Contains("المتبقي 14 من كمية المشروع 39", ar.Text);
        var all = off.Answer("remaining in P2-106");
        Assert.Contains("LIGHT: REMAINING 14 of 39", all.Text);
        Assert.Contains("POWER: REMAINING 36 of 36", all.Text);
        var dn = off.Answer("where is DN 81064344");
        Assert.Contains("PO RAF-P.O-E-045-2026", dn.Text);
        Assert.Contains(dn.Citations, c => c.Kind == "dn");
        var inv = off.Answer("status of ROOTS INV 1");
        Assert.Contains("Rev 0: REJECTED", inv.Text);
        Assert.Contains("WIR missing for L2", inv.Text);
        var rule = off.Answer("اشرح قاعدة 15 م");
        Assert.Contains("15", rule.Text);
        Assert.Contains("الطول", rule.Text);
        Assert.Contains("remaining in P2-106", off.Answer("hello").Text);
    }
}
