using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Ai;

namespace Raffaello.Core.Documents;

/// <summary>
/// Reads one page (photo, scan or single-page PDF) with the Claude Messages API and returns JSON that matches the request's schema
/// (structured output). Same raw-HTTP conventions as <see cref="AnthropicClient"/>: adaptive thinking, explicit effort, server-side
/// refusal fallbacks. Every answer still goes through the same validation as text-layer extraction.
/// </summary>
public sealed class ClaudeVisionReader : IVisionReader
{
    private readonly HttpClient _http;
    private readonly bool _enabled;
    public string ApiKey { get; }
    public string Model { get; }
    public string Effort { get; set; } = "medium";
    public bool UseServerFallbacks { get; set; } = true;
    public int MaxTokens { get; set; } = 16000;

    public ClaudeVisionReader(bool cloudReadingEnabled, string? apiKey, string? model, HttpClient? http = null)
    {
        _enabled = cloudReadingEnabled;
        ApiKey = AnthropicClient.ResolveKey(apiKey) ?? "";
        Model = string.IsNullOrWhiteSpace(model) ? AnthropicClient.DefaultModel : model.Trim();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public bool IsAvailable => _enabled && ApiKey.Length > 0;
    public string Status => !_enabled ? "cloud reading is off (Materials > SETTINGS)" : ApiKey.Length == 0 ? "no API key (Settings)" : $"ready ({Model})";

    internal JsonObject BuildBody(VisionRequest r)
    {
        var data = Convert.ToBase64String(r.Content);
        JsonObject block = r.MediaType == "application/pdf"
            ? new JsonObject { ["type"] = "document", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = data } }
            : new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = r.MediaType, ["data"] = data } };
        var prompt = $"This is page {r.Page} of a {r.DocumentKind} from a construction project in Saudi Arabia (electrical materials).\n{r.Instructions}\n" +
                     "Copy numbers exactly as printed (keep decimals, drop thousands separators). Use an empty string or 0 when a field is not on the page; never guess.";
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["system"] = "You transcribe supplier documents into JSON for a quantity surveyor. Accuracy matters more than completeness.",
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = new JsonArray { block, new JsonObject { ["type"] = "text", ["text"] = prompt } } } },
            ["thinking"] = new JsonObject { ["type"] = "adaptive" },
            ["output_config"] = new JsonObject
            {
                ["effort"] = Effort,
                ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = r.Schema.DeepClone() },
            },
        };
        if (UseServerFallbacks) body["fallbacks"] = "default";
        return body;
    }

    public async Task<JsonNode?> ExtractAsync(VisionRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Cloud reading is not available: " + Status);
        using var req = new HttpRequestMessage(HttpMethod.Post, AnthropicClient.Endpoint)
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", AnthropicClient.ApiVersion);
        if (UseServerFallbacks) req.Headers.Add("anthropic-beta", AnthropicClient.FallbackBeta);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new AnthropicException(AnthropicClient.DescribeError((int)res.StatusCode, text), (int)res.StatusCode);
        return ParseAnswer(text);
    }

    /// <summary>Concatenated text blocks parsed as JSON (tolerates a stray prose line around the object).</summary>
    internal static JsonNode? ParseAnswer(string responseJson)
    {
        var node = JsonNode.Parse(responseJson);
        var stop = node?["stop_reason"]?.GetValue<string>();
        if (stop == "refusal") throw new AnthropicException("The page was declined by the model's safety filter.");
        var sb = new StringBuilder();
        foreach (var b in node?["content"]?.AsArray() ?? new JsonArray())
            if (b?["type"]?.GetValue<string>() == "text") sb.Append(b["text"]?.GetValue<string>());
        var s = sb.ToString().Trim();
        if (stop == "max_tokens") throw new AnthropicException("The page answer was cut at the token limit.");
        var i = s.IndexOf('{'); var j = s.LastIndexOf('}');
        if (i < 0 || j <= i) return null;
        return JsonNode.Parse(s[i..(j + 1)]);
    }
}
