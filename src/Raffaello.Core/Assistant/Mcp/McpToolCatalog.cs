using System.Text.Json.Nodes;

namespace Raffaello.Core.Assistant.Mcp;

/// <summary>
/// The tools the Raffaello MCP server offers: the assistant's READ tools (same names, descriptions and schemas as the API path) plus
/// <see cref="ExtraReadTools"/>. The writing tools (draft claim, invoice revision, e-mail, variation, reminder) are deliberately NOT
/// offered: in this version nothing outside the app can change the data.
/// </summary>
public sealed class McpToolCatalog
{
    private readonly AssistantData _data;
    private readonly AssistantTools _tools;
    private readonly ExtraReadTools _extra;

    public McpToolCatalog(AssistantData data)
    {
        _data = data;
        _tools = new AssistantTools(data);
        _extra = new ExtraReadTools(data);
    }

    /// <summary>Names of every tool offered (read-only).</summary>
    public static IReadOnlyList<string> Names => AssistantTools.ReadTools.Concat(ExtraReadTools.Names).ToList();

    /// <summary>Tools in MCP shape: name, title, description, inputSchema, annotations (read-only).</summary>
    public static JsonArray ListTools()
    {
        var res = new JsonArray();
        var defs = AssistantTools.Definitions().OfType<JsonObject>().Where(t => AssistantTools.ReadTools.Contains((string?)t["name"]))
            .Concat(ExtraReadTools.Definitions().OfType<JsonObject>());
        foreach (var d in defs)
        {
            var name = (string?)d["name"] ?? "";
            res.Add(new JsonObject
            {
                ["name"] = name,
                ["title"] = name.Replace('_', ' ').ToUpperInvariant(),
                ["description"] = (string?)d["description"] ?? "",
                ["inputSchema"] = d["input_schema"]!.DeepClone(),
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = false },
            });
        }
        return res;
    }

    /// <summary>Runs one tool. Never throws; unknown or writing tools come back as an error result.</summary>
    public ToolOutcome Call(string name, JsonObject? arguments)
    {
        var input = arguments ?? new JsonObject();
        ToolOutcome outcome;
        if (AssistantActionKinds.All.Contains(name))
            outcome = new ToolOutcome { IsError = true, Content = $"{name} is not available here: Raffaello's MCP server is read-only. Ask the user to do it in the app (Ask Raffaello > CONFIRM card)." };
        else if (AssistantTools.ReadTools.Contains(name))
            outcome = _tools.Execute(name, input, "mcp", 0);
        else if (ExtraReadTools.Names.Contains(name))
            outcome = _extra.Execute(name, input);
        else
            outcome = new ToolOutcome { IsError = true, Content = $"Unknown tool {name}. Available: {string.Join(", ", Names)}." };
        var max = Math.Clamp(_data.Settings.MaxToolResultChars, 2000, 200_000);
        if (outcome.Content.Length > max)
            outcome = new ToolOutcome { Content = outcome.Content[..max] + "\n[... result cut; ask with a narrower filter]", IsError = outcome.IsError, Citations = outcome.Citations };
        return outcome;
    }
}