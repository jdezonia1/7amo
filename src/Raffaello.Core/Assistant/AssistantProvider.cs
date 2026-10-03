using System.Text.Json;
using System.Text.Json.Nodes;
using Raffaello.Core.Assistant.ClaudeCode;

namespace Raffaello.Core.Assistant;

/// <summary>Which engine answers in Ask Raffaello (Settings > Assistant > PROVIDER).</summary>
public static class AssistantProviders
{
    /// <summary>API key if one is saved, else the Claude login through Claude Code when it is installed and logged in, else offline.</summary>
    public const string Auto = "AUTO";
    /// <summary>Anthropic API key (direct, streaming, write cards).</summary>
    public const string ApiKey = "API KEY";
    /// <summary>The user's own Claude login through Claude Code (read-only project tools).</summary>
    public const string ClaudeLogin = "CLAUDE LOGIN";
    /// <summary>Answers from local data only; nothing leaves the PC.</summary>
    public const string Offline = "OFFLINE";

    public static readonly string[] All = { Auto, ApiKey, ClaudeLogin, Offline };

    public static string Normalize(string? s)
    {
        var u = (s ?? "").Trim().ToUpperInvariant().Replace('_', ' ').Replace('-', ' ');
        return u switch
        {
            "API KEY" or "API" or "KEY" => ApiKey,
            "CLAUDE LOGIN" or "CLAUDE" or "CLAUDE CODE" or "LOGIN" => ClaudeLogin,
            "OFFLINE" or "LOCAL" => Offline,
            _ => Auto,
        };
    }
}

public enum AssistantRoute { Api, ClaudeCode, Offline }

/// <summary>The engine chosen for a question and why (shown under the chat).</summary>
public sealed record RouteDecision(AssistantRoute Route, string Reason, bool Warn = false)
{
    /// <summary>
    /// AUTO: API key if present, else Claude login if Claude Code is found and logged in, else offline. API KEY / CLAUDE LOGIN force that
    /// engine and fall back to offline (with the reason) when it is not available. OFFLINE never sends anything.
    /// </summary>
    public static RouteDecision Decide(string provider, bool hasApiKey, ClaudeCodeStatus claude)
    {
        switch (AssistantProviders.Normalize(provider))
        {
            case AssistantProviders.Offline:
                return new(AssistantRoute.Offline, "OFFLINE chosen in Settings: nothing leaves this PC.");
            case AssistantProviders.ApiKey:
                return hasApiKey ? new(AssistantRoute.Api, "API key") : new(AssistantRoute.Offline, "API KEY chosen but no key is saved (Settings > Assistant).", true);
            case AssistantProviders.ClaudeLogin:
                return claude.Ready ? new(AssistantRoute.ClaudeCode, "Claude login (Claude Code)") : new(AssistantRoute.Offline, claude.Message, true);
            default:
                if (hasApiKey) return new(AssistantRoute.Api, "AUTO: API key");
                if (claude.Ready) return new(AssistantRoute.ClaudeCode, "AUTO: Claude login (Claude Code)");
                return new(AssistantRoute.Offline, "AUTO: no API key and " + (claude.Found ? "Claude Code is not logged in." : "Claude Code is not installed."));
        }
    }
}

/// <summary>The snippet for Claude Desktop (option B): the user pastes it into claude_desktop_config.json himself - never edited by the app.</summary>
public static class ClaudeDesktopConfig
{
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    public static string Snippet(string command, IReadOnlyList<string> args) => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            ["raffaello"] = new JsonObject
            {
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static string Instructions =>
        "1. Open Claude Desktop > Settings > Developer > Edit Config (file: " + ConfigPath + ").\n" +
        "2. If the file is empty or has no \"mcpServers\", paste the whole snippet. If it already has \"mcpServers\", add only the \"raffaello\": { ... } entry inside it.\n" +
        "3. Save, then quit Claude Desktop completely (tray icon > Quit) and start it again.\n" +
        "4. In a new chat, the tools icon lists \"raffaello\". Ask e.g. \"how many hotel guest rooms have 2nd fix DATA remaining?\".\n" +
        "The server is read-only: Claude Desktop can look at the project data but cannot change it. Keep Raffaello installed in the same folder (the path above).";
}