using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raffaello.Core.Ai;

public sealed record ChatMessage(string Role, string Text);

public sealed class AnthropicException : Exception
{
    public int? Status { get; }
    public AnthropicException(string message, int? status = null) : base(message) { Status = status; }
}

/// <summary>
/// Minimal Claude Messages API client over HttpClient (raw HTTP + server-sent events).
/// Streams text deltas, uses adaptive thinking, sets effort explicitly and opts into server-side
/// refusal fallbacks. Handles the refusal stop reason and maps HTTP errors to readable messages.
/// </summary>
public sealed class AnthropicClient
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
    public const string DefaultModel = "claude-opus-5-5";

    private readonly HttpClient _http;
    public string ApiKey { get; set; }
    public string Model { get; set; } = DefaultModel;
    /// <summary>low / medium / high / xhigh / max. Claude Opus 5.5 defaults to medium; we set it explicitly.</summary>
    public string Effort { get; set; } = "medium";
    public int MaxTokens { get; set; } = 16000;
    public bool UseServerFallbacks { get; set; } = true;

    public AnthropicClient(string apiKey, HttpClient? http = null)
    {
        ApiKey = apiKey;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static string? ResolveKey(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
        : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } env ? env.Trim() : null;

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    internal JsonObject BuildBody(string system, IReadOnlyList<ChatMessage> messages, bool stream)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
            msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Text });
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = msgs,
            ["thinking"] = new JsonObject { ["type"] = "adaptive" },
            ["output_config"] = new JsonObject { ["effort"] = Effort },
            ["stream"] = stream,
        };
        if (UseServerFallbacks) body["fallbacks"] = "default";
        return body;
    }

    private HttpRequestMessage BuildRequest(JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        if (UseServerFallbacks) req.Headers.Add("anthropic-beta", FallbackBeta);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(body["stream"]?.GetValue<bool>() == true ? "text/event-stream" : "application/json"));
        return req;
    }

    /// <summary>Streams the assistant's text as it is generated.</summary>
    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!HasKey) throw new AnthropicException("No API key. Add one in Settings or set ANTHROPIC_API_KEY.");
        using var req = BuildRequest(BuildBody(system, messages, stream: true));
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new AnthropicException(DescribeError((int)res.StatusCode, err), (int)res.StatusCode);
        }
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? stopReason = null;
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (json.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(json); } catch (JsonException) { continue; }
            var type = node?["type"]?.GetValue<string>();
            switch (type)
            {
                case "content_block_delta":
                    var delta = node!["delta"];
                    if (delta?["type"]?.GetValue<string>() == "text_delta")
                    {
                        var text = delta["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text)) yield return text;
                    }
                    break;
                case "message_delta":
                    stopReason = node!["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                    break;
                case "error":
                    throw new AnthropicException(node!["error"]?["message"]?.GetValue<string>() ?? "Stream error.");
            }
        }
        if (stopReason == "refusal") yield return "\n\n[The request was declined by the model's safety filter. Try rephrasing the question.]";
        else if (stopReason == "max_tokens") yield return "\n\n[Answer cut at the token limit.]";
    }

    /// <summary>Non-streaming call; returns the concatenated text blocks.</summary>
    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        if (!HasKey) throw new AnthropicException("No API key. Add one in Settings or set ANTHROPIC_API_KEY.");
        using var req = BuildRequest(BuildBody(system, messages, stream: false));
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new AnthropicException(DescribeError((int)res.StatusCode, text), (int)res.StatusCode);
        var node = JsonNode.Parse(text);
        if (node?["stop_reason"]?.GetValue<string>() == "refusal")
            return "[The request was declined by the model's safety filter. Try rephrasing the question.]";
        var sb = new StringBuilder();
        foreach (var block in node?["content"]?.AsArray() ?? new JsonArray())
            if (block?["type"]?.GetValue<string>() == "text") sb.Append(block["text"]?.GetValue<string>());
        return sb.ToString();
    }

    internal static string DescribeError(int status, string body)
    {
        string? msg = null;
        try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>(); } catch (JsonException) { }
        var hint = status switch
        {
            400 => "Bad request - check the model name in Settings.",
            401 => "API key rejected - check it in Settings.",
            403 => "This key has no access to the model.",
            404 => "Model not found - check the model name in Settings.",
            429 => "Rate limited - wait a moment and try again.",
            >= 500 => "Anthropic service error - try again shortly.",
            _ => "Request failed.",
        };
        return msg is null ? $"{hint} (HTTP {status})" : $"{hint} (HTTP {status}: {msg})";
    }
}
