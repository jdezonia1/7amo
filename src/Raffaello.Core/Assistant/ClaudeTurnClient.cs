using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raffaello.Core.Ai;

namespace Raffaello.Core.Assistant;

/// <summary>Everything one Messages API call needs (the assistant builds it; the client only sends it).</summary>
public sealed class ClaudeRequest
{
    public string Model { get; init; } = AnthropicClient.DefaultModel;
    public string Effort { get; init; } = "medium";
    public int MaxTokens { get; init; } = 16000;
    public bool UseServerFallbacks { get; init; } = true;
    /// <summary>Frozen system prompt (never edited between turns - cached, and keeps thinking blocks valid).</summary>
    public string System { get; init; } = "";
    /// <summary>Tool definitions (fixed set and order - cached).</summary>
    public JsonArray Tools { get; init; } = new();
    public JsonArray Messages { get; init; } = new();
}

/// <summary>One assistant message as received: the content blocks exactly as the API sent them (replayed unchanged next turn).</summary>
public sealed class ClaudeTurn
{
    public JsonArray Content { get; init; } = new();
    public string StopReason { get; set; } = "";
    public string StopCategory { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    /// <summary>Tool inputs that were not valid JSON (tool_use id -> raw text): never executed, answered with an error result.</summary>
    public Dictionary<string, string> InvalidToolInputs { get; } = new();

    public string Text => string.Concat(Content.OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? ""));

    /// <summary>
    /// tool_use blocks that may run: after a mid-output fallback, blocks before the last "fallback" marker belong to the declined
    /// attempt and are neither executed nor echoed.
    /// </summary>
    public IEnumerable<JsonObject> ToolUses => AfterLastFallback(Content).Where(b => (string?)b["type"] == "tool_use");

    private static IEnumerable<JsonObject> AfterLastFallback(JsonArray content)
    {
        var blocks = content.OfType<JsonObject>().ToList();
        var last = blocks.FindLastIndex(b => (string?)b["type"] == "fallback");
        return last < 0 ? blocks : blocks.Skip(last + 1);
    }

    /// <summary>
    /// The content to append to the history: everything as received, except - after a mid-output fallback - thinking, redacted
    /// thinking and tool_use blocks that come before the final fallback marker (they belong to the declined attempt).
    /// </summary>
    public JsonArray ContentForHistory()
    {
        var blocks = Content.OfType<JsonObject>().ToList();
        var last = blocks.FindLastIndex(b => (string?)b["type"] == "fallback");
        var res = new JsonArray();
        for (var i = 0; i < blocks.Count; i++)
        {
            var t = (string?)blocks[i]["type"];
            if (i < last && t is "thinking" or "redacted_thinking" or "tool_use") continue;
            res.Add(blocks[i].DeepClone());
        }
        return res;
    }
}

/// <summary>Sends one request and returns the assistant message (streaming, text deltas reported as they arrive).</summary>
public interface IClaudeTransport
{
    Task<ClaudeTurn> SendAsync(ClaudeRequest request, Action<string>? onText, CancellationToken ct);
}

/// <summary>
/// Claude Messages API over HttpClient with server-sent events, with client tools. Same raw-HTTP approach as
/// <see cref="AnthropicClient"/> (the project has no SDK dependency): adaptive thinking, explicit effort, server-side refusal
/// fallbacks ("default"), prompt caching on the tool definitions and the frozen system prompt plus automatic caching of the
/// conversation, eager input streaming on every client tool (inputs are validated before anything runs). Retries 429 / 5xx / 529
/// before the stream starts. The API key is only ever put in the request header - never logged.
/// </summary>
public sealed class ClaudeTurnClient : IClaudeTransport
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    public int MaxRetries { get; init; } = 2;
    public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    public ClaudeTurnClient(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey ?? "";
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static JsonObject BuildBody(ClaudeRequest r)
    {
        var tools = (JsonArray)r.Tools.DeepClone();
        foreach (var t in tools.OfType<JsonObject>()) t["eager_input_streaming"] = true;
        if (tools.Count > 0 && tools[^1] is JsonObject lastTool) lastTool["cache_control"] = Ephemeral();
        var body = new JsonObject
        {
            ["model"] = r.Model,
            ["max_tokens"] = r.MaxTokens,
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.System, ["cache_control"] = Ephemeral() }),
            ["messages"] = r.Messages.DeepClone(),
            ["thinking"] = new JsonObject { ["type"] = "adaptive" },
            ["output_config"] = new JsonObject { ["effort"] = r.Effort },
            // automatic caching of the growing conversation (breakpoint moves with the last cacheable block)
            ["cache_control"] = Ephemeral(),
            ["stream"] = true,
        };
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = new JsonObject { ["type"] = "auto" };
        }
        if (r.UseServerFallbacks) body["fallbacks"] = "default";
        return body;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    public async Task<ClaudeTurn> SendAsync(ClaudeRequest request, Action<string>? onText, CancellationToken ct)
    {
        if (_apiKey.Length == 0) throw new AnthropicException("No API key. Add one in Settings or set ANTHROPIC_API_KEY.");
        var json = BuildBody(request).ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, AnthropicClient.Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", AnthropicClient.ApiVersion);
            if (request.UseServerFallbacks) req.Headers.Add("anthropic-beta", AnthropicClient.FallbackBeta);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var status = (int)res.StatusCode;
                var err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (attempt < MaxRetries && (status == 429 || status >= 500 || res.StatusCode == HttpStatusCode.RequestTimeout))
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                    await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct).ConfigureAwait(false);
                    continue;
                }
                throw new AnthropicException(AnthropicClient.DescribeError(status, err), status);
            }
            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await ParseStreamAsync(stream, onText, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the SSE stream into a <see cref="ClaudeTurn"/> (public for tests).</summary>
    public static async Task<ClaudeTurn> ParseStreamAsync(Stream stream, Action<string>? onText, CancellationToken ct)
    {
        var turn = new ClaudeTurn();
        var blocks = new SortedDictionary<int, JsonObject>();
        var partial = new Dictionary<int, StringBuilder>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            JsonObject? ev;
            try { ev = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { continue; }
            if (ev is null) continue;
            switch ((string?)ev["type"])
            {
                case "message_start":
                    var msg = ev["message"];
                    turn.Model = (string?)msg?["model"] ?? "";
                    ReadUsage(turn, msg?["usage"]);
                    break;
                case "content_block_start":
                {
                    var index = (int?)ev["index"] ?? blocks.Count;
                    var block = (ev["content_block"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
                    if ((string?)block["type"] == "tool_use") partial[index] = new StringBuilder();
                    if ((string?)block["type"] == "text" && (string?)block["text"] is { Length: > 0 } first) onText?.Invoke(first);
                    blocks[index] = block;
                    break;
                }
                case "content_block_delta":
                {
                    var index = (int?)ev["index"] ?? 0;
                    if (!blocks.TryGetValue(index, out var block)) break;
                    var delta = ev["delta"];
                    switch ((string?)delta?["type"])
                    {
                        case "text_delta":
                            var t = (string?)delta!["text"] ?? "";
                            block["text"] = ((string?)block["text"] ?? "") + t;
                            if (t.Length > 0) onText?.Invoke(t);
                            break;
                        case "thinking_delta":
                            block["thinking"] = ((string?)block["thinking"] ?? "") + ((string?)delta!["thinking"] ?? "");
                            break;
                        case "signature_delta":
                            block["signature"] = (string?)delta!["signature"] ?? "";
                            break;
                        case "input_json_delta":
                            if (partial.TryGetValue(index, out var sb)) sb.Append((string?)delta!["partial_json"] ?? "");
                            break;
                        case "citations_delta":
                            var arr = block["citations"] as JsonArray ?? new JsonArray();
                            if (delta!["citation"] is JsonNode c) arr.Add(c.DeepClone());
                            block["citations"] = arr;
                            break;
                    }
                    break;
                }
                case "content_block_stop":
                {
                    var index = (int?)ev["index"] ?? 0;
                    if (blocks.TryGetValue(index, out var block) && partial.TryGetValue(index, out var sb))
                    {
                        var raw = sb.ToString();
                        var id = (string?)block["id"] ?? "";
                        if (raw.Trim().Length == 0) block["input"] = new JsonObject();
                        else
                        {
                            try
                            {
                                block["input"] = JsonNode.Parse(raw) is JsonObject o ? o : throw new JsonException("not an object");
                            }
                            catch (JsonException)
                            {
                                block["input"] = new JsonObject();
                                turn.InvalidToolInputs[id] = raw;
                            }
                        }
                        partial.Remove(index);
                    }
                    break;
                }
                case "message_delta":
                    turn.StopReason = (string?)ev["delta"]?["stop_reason"] ?? turn.StopReason;
                    turn.StopCategory = (string?)ev["delta"]?["stop_details"]?["category"] ?? turn.StopCategory;
                    if (ev["usage"]?["output_tokens"] is JsonNode o2) turn.OutputTokens = (int)o2;
                    break;
                case "error":
                    throw new AnthropicException((string?)ev["error"]?["message"] ?? "Stream error.", (int?)null);
            }
        }
        // a stream cut inside a tool input: keep the block valid for the history, never run it
        foreach (var (index, sb) in partial)
            if (blocks.TryGetValue(index, out var b)) { b["input"] = new JsonObject(); turn.InvalidToolInputs[(string?)b["id"] ?? ""] = sb.ToString(); }
        foreach (var b in blocks.Values) turn.Content.Add(b);
        return turn;
    }

    private static void ReadUsage(ClaudeTurn turn, JsonNode? usage)
    {
        if (usage is null) return;
        turn.InputTokens = (int?)usage["input_tokens"] ?? turn.InputTokens;
        turn.OutputTokens = (int?)usage["output_tokens"] ?? turn.OutputTokens;
        turn.CacheReadTokens = (int?)usage["cache_read_input_tokens"] ?? turn.CacheReadTokens;
        turn.CacheWriteTokens = (int?)usage["cache_creation_input_tokens"] ?? turn.CacheWriteTokens;
    }
}
