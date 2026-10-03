using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raffaello.Core.Assistant.ClaudeCode;

public enum ClaudeCodeEventKind { Init, TextDelta, ToolUse, ToolResult, Result }

/// <summary>One thing that happened in a headless Claude Code run (already de-duplicated by <see cref="ClaudeCodeAccumulator"/>).</summary>
public sealed record ClaudeCodeEvent(ClaudeCodeEventKind Kind, string Text = "", string ToolName = "", string ToolId = "", bool IsError = false);

/// <summary>
/// Reads Claude Code's <c>--output-format stream-json</c> lines (with <c>--verbose --include-partial-messages</c>): text deltas, the
/// complete assistant messages (text + tool_use blocks), tool results, and the final result. Text that was already streamed as
/// deltas is not repeated when the complete message arrives; text that was not streamed (older CLI, no partial messages) is taken
/// from the complete message.
/// </summary>
public sealed class ClaudeCodeAccumulator
{
    public const string McpPrefix = "mcp__raffaello__";

    private readonly System.Text.StringBuilder _text = new();
    private readonly HashSet<string> _streamedMessages = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private string _currentStream = "";

    public string Text => _text.ToString();
    public List<string> ToolsUsed { get; } = new();
    public bool HasResult { get; private set; }
    public bool ResultIsError { get; private set; }
    public string ResultText { get; private set; } = "";
    public string ResultSubtype { get; private set; } = "";
    /// <summary>"authentication_failed", "rate_limit", ... when a message or the result reported an API error.</summary>
    public string ErrorKind { get; private set; } = "";
    public double CostUsd { get; private set; }
    public int Turns { get; private set; }
    public string Model { get; private set; } = "";
    /// <summary>Status of the raffaello MCP server reported at start ("connected", "failed", "" when not listed).</summary>
    public string McpStatus { get; private set; } = "";
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int Lines { get; private set; }

    public static string ShortToolName(string name) => name.StartsWith(McpPrefix, StringComparison.Ordinal) ? name[McpPrefix.Length..] : name;

    /// <summary>Feeds one stdout line; returns the events it produced (none for lines that do not matter or are not JSON).</summary>
    public List<ClaudeCodeEvent> Feed(string line)
    {
        var events = new List<ClaudeCodeEvent>();
        if (string.IsNullOrWhiteSpace(line)) return events;
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return events; }
        if (o is null) return events;
        Lines++;
        switch ((string?)o["type"])
        {
            case "system" when (string?)o["subtype"] == "init":
                Model = (string?)o["model"] ?? "";
                if (o["mcp_servers"] is JsonArray servers)
                    foreach (var s in servers.OfType<JsonObject>())
                        if ((string?)s["name"] == "raffaello") McpStatus = (string?)s["status"] ?? "";
                events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.Init, McpStatus));
                break;

            case "stream_event":
                var ev = o["event"] as JsonObject ?? new JsonObject();
                var delta = ev["delta"] as JsonObject;
                var deltaType = delta is null ? "" : (string?)delta["type"] ?? "";
                switch ((string?)ev["type"])
                {
                    case "message_start":
                        _currentStream = ev["message"] is JsonObject m ? (string?)m["id"] ?? "" : "";
                        break;
                    case "content_block_delta" when deltaType == "text_delta":
                        var t = (string?)delta!["text"] ?? "";
                        if (t.Length == 0) break;
                        _streamedMessages.Add(_currentStream);
                        _text.Append(t);
                        events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, t));
                        break;
                }
                break;

            case "assistant":
                var msg = o["message"] as JsonObject;
                var id = (string?)msg?["id"] ?? "";
                if (o["error"] is JsonValue err) ErrorKind = (string?)err ?? ErrorKind;
                if (msg?["usage"] is JsonObject u)
                {
                    InputTokens += (int?)u["input_tokens"] ?? 0;
                    OutputTokens += (int?)u["output_tokens"] ?? 0;
                }
                foreach (var b in (msg?["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    switch ((string?)b["type"])
                    {
                        case "text":
                            var text = (string?)b["text"] ?? "";
                            if (text.Length == 0 || (id.Length > 0 && _streamedMessages.Contains(id))) break;
                            if (_text.Length > 0 && !_text.ToString().EndsWith('\n')) { _text.Append("\n\n"); events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, "\n\n")); }
                            _text.Append(text);
                            events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, text));
                            break;
                        case "tool_use":
                            var name = ShortToolName((string?)b["name"] ?? "");
                            var tid = (string?)b["id"] ?? "";
                            _toolNames[tid] = name;
                            ToolsUsed.Add(name);
                            events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.ToolUse, b["input"]?.ToJsonString() ?? "{}", name, tid));
                            break;
                    }
                }
                break;

            case "user":
                foreach (var b in (o["message"]?["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if ((string?)b["type"] != "tool_result") continue;
                    var tid = (string?)b["tool_use_id"] ?? "";
                    var content = b["content"] switch
                    {
                        JsonValue v => (string?)v ?? "",
                        JsonArray a => string.Concat(a.OfType<JsonObject>().Where(x => (string?)x["type"] == "text").Select(x => (string?)x["text"] ?? "")),
                        _ => "",
                    };
                    events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.ToolResult, content, _toolNames.GetValueOrDefault(tid, ""), tid, (bool?)b["is_error"] ?? false));
                }
                break;

            case "result":
                HasResult = true;
                ResultIsError = (bool?)o["is_error"] ?? false;
                ResultSubtype = (string?)o["subtype"] ?? "";
                ResultText = (string?)o["result"] ?? "";
                CostUsd = (double?)o["total_cost_usd"] ?? 0;
                Turns = (int?)o["num_turns"] ?? 0;
                if (o["terminal_reason"] is JsonValue tr && (string?)tr == "api_error" && ErrorKind.Length == 0) ErrorKind = "api_error";
                if (ResultSubtype.StartsWith("error", StringComparison.Ordinal)) ResultIsError = ResultIsError || ResultSubtype != "error_max_turns";
                // a run that streamed nothing (e.g. max turns hit right after a tool) still has the final text here
                if (_text.Length == 0 && ResultText.Length > 0 && !ResultIsError)
                {
                    _text.Append(ResultText);
                    events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.TextDelta, ResultText));
                }
                events.Add(new ClaudeCodeEvent(ClaudeCodeEventKind.Result, ResultText, IsError: ResultIsError));
                break;
        }
        return events;
    }

    /// <summary>A plain-language reason when the run failed, else "".</summary>
    public string FailureReason()
    {
        if (ErrorKind == "authentication_failed" || ResultText.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || ResultText.Contains("/login", StringComparison.Ordinal))
            return "Claude Code is not logged in. Open a terminal, run  claude  once and sign in with your Claude account.";
        if (!HasResult) return "";
        if (!ResultIsError) return "";
        return ResultText.Length > 0 ? "Claude Code: " + ResultText : "Claude Code stopped with an error (" + (ErrorKind.Length > 0 ? ErrorKind : ResultSubtype) + ").";
    }
}