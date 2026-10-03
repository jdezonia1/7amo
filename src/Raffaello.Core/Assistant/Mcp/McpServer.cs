using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raffaello.Core.Assistant.Mcp;

/// <summary>
/// Minimal Model Context Protocol server over stdio (JSON-RPC 2.0, one message per line, UTF-8): initialize, ping, tools/list,
/// tools/call. Nothing but protocol messages is written to the output; diagnostics go to the log writer (stderr).
/// No SDK dependency on purpose: the protocol surface is four methods and the app ships as patches.
/// </summary>
public sealed class McpServer
{
    public static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    public const string ServerName = "raffaello";

    private readonly Func<McpToolCatalog> _catalog;
    private readonly TextWriter _log;
    private readonly Action<string, IReadOnlyList<Citation>>? _onCitations;

    /// <param name="catalog">The tools (asked for on each call so the data snapshot can be refreshed between calls).</param>
    /// <param name="log">Diagnostics (stderr). Never the protocol stream.</param>
    /// <param name="onCitations">Optional: receives the records each call returned (the app's side channel for clickable sources).</param>
    public McpServer(Func<McpToolCatalog> catalog, TextWriter? log = null, Action<string, IReadOnlyList<Citation>>? onCitations = null)
    {
        _catalog = catalog;
        _log = log ?? TextWriter.Null;
        _onCitations = onCitations;
    }

    public string Instructions { get; set; } =
        "Read-only access to the Raffaello project data (Raffles Hotel and Branded Residences electrical / ELV QS): room ledger, remaining per room, " +
        "invoices and their Aconex approval, WIRs, contracts, delivery notes, anomalies, reports, documents and the house rules. " +
        "Tool results carry cite_tokens such as [[room:P2-106]]; quote them after the facts they support. Stages (1ST FIX, 2ND FIX, FINAL FIX) are never added together.";

    /// <summary>Serves until the input ends. Returns the number of requests handled.</summary>
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var handled = 0;
        while (!ct.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Trim().Length == 0) continue;
            var response = Handle(line);
            handled++;
            if (response is null) continue;
            await output.WriteAsync(response.ToJsonString() + "\n").ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        return handled;
    }

    /// <summary>Handles one JSON-RPC message; null for notifications (no reply).</summary>
    public JsonObject? Handle(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException ex) { return Error(null, -32700, "Parse error: " + ex.Message); }
        if (node is JsonArray)
        {
            // batches are not used by current clients; answer each request in order as separate errors would break ids -> reject
            return Error(null, -32600, "Batch requests are not supported.");
        }
        if (node is not JsonObject msg) return Error(null, -32600, "Invalid request.");
        var id = msg["id"]?.DeepClone();
        var method = (string?)msg["method"];
        var isNotification = !msg.ContainsKey("id");
        if (method is null) return isNotification ? null : Error(id, -32600, "Invalid request: no method.");
        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(msg["params"] as JsonObject));
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = McpToolCatalog.ListTools() });
                case "tools/call":
                    return Result(id, CallTool(msg["params"] as JsonObject));
                case "resources/list":
                    return Result(id, new JsonObject { ["resources"] = new JsonArray() });
                case "prompts/list":
                    return Result(id, new JsonObject { ["prompts"] = new JsonArray() });
                default:
                    if (isNotification) return null;          // notifications/initialized, notifications/cancelled ...
                    return Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[raffaello-mcp] {method} failed: {ex}");
            return isNotification ? null : Error(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        var asked = (string?)p?["protocolVersion"];
        var version = asked != null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0];
        _log.WriteLine($"[raffaello-mcp] initialize from {(string?)p?["clientInfo"]?["name"] ?? "?"} (protocol {asked ?? "?"} -> {version})");
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Raffaello project data (read-only)", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "1.0" },
            ["instructions"] = Instructions,
        };
    }

    private JsonObject CallTool(JsonObject? p)
    {
        var name = (string?)p?["name"] ?? "";
        var args = p?["arguments"] as JsonObject;
        var started = DateTime.UtcNow;
        var outcome = _catalog().Call(name, args?.DeepClone() as JsonObject);
        _log.WriteLine($"[raffaello-mcp] {name} {(outcome.IsError ? "ERROR" : "ok")} {outcome.Content.Length} chars {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        if (outcome.Citations.Count > 0)
            try { _onCitations?.Invoke(name, outcome.Citations); } catch (Exception ex) { _log.WriteLine("[raffaello-mcp] citations: " + ex.Message); }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = outcome.Content }),
            ["isError"] = outcome.IsError,
        };
    }

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}