using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raffaello.Core.Ai;

namespace Raffaello.Core.Assemblies;

/// <summary>
/// [assemblies] Optional Claude assist (only when enabled with an API key): proposes a component list for an unusual item description.
/// Only the item description, unit, parsed spec and the names the formulas may use are sent - never prices, contracts or documents.
/// The proposal is shown to the user; once confirmed it is saved as a new template (Origin = AI, unconfirmed).
/// </summary>
public sealed class ClaudeAssemblyAssist
{
    private readonly AnthropicClient _client;
    public ClaudeAssemblyAssist(AnthropicClient client) => _client = client;

    public const string System =
        "You are an electrical quantity surveyor (MEP, Saudi Arabia, BS 7671 practice) building a rate analysis. For the BOQ item given, list the " +
        "materials and labour needed PER UNIT of the item. Reply with JSON only: {\"name\": \"...\", \"unit\": \"PT|M|NO|END\", \"params\": [{\"name\": \"snake_case\", \"value\": number, \"unit\": \"...\", \"label\": \"...\"}], " +
        "\"components\": [{\"key\": \"snake_case\", \"name\": \"...\", \"spec\": \"material specification\", \"unit\": \"M|PCS|L|SET|HR\", \"qty\": \"formula\", \"waste\": 0.05, " +
        "\"stage\": \"1ST FIX|2ND FIX|3RD FIX|\", \"kind\": \"MATERIAL|LABOUR|EQUIPMENT\"}]}. Formulas use + - * / ( ), ceil, floor, round, min, max, if(cond,a,b), " +
        "numbers, your params, earlier component keys and these names: ";

    public static string BuildPrompt(string description, string unit, ItemSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BOQ ITEM:").AppendLine(description.Length > 3000 ? description[..3000] + " ..." : description);
        sb.AppendLine(CultureInfo.InvariantCulture, $"UNIT: {unit}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"PARSED: {spec.Summary}");
        sb.AppendLine("Labour as HR lines (kind LABOUR). Keep it to the components a QS would price; no prices.");
        return sb.ToString();
    }

    public async Task<AssemblyTemplate?> ProposeAsync(string description, string unit, ItemSpec spec, CancellationToken ct = default)
    {
        var system = System + string.Join(", ", TemplateCheck.BuiltIn) + ".";
        var reply = await _client.CompleteAsync(system, new[] { new ChatMessage("user", BuildPrompt(description, unit, spec)) }, ct).ConfigureAwait(false);
        return Parse(reply, spec);
    }

    /// <summary>Reads the model's JSON (tolerates prose / code fences). Labour HR lines are priced with lab_hr; returns null when nothing usable.</summary>
    public static AssemblyTemplate? Parse(string reply, ItemSpec spec)
    {
        var m = Regex.Match(reply ?? "", @"\{[\s\S]*\}");
        if (!m.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var root = doc.RootElement;
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            double D(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
            var t = new AssemblyTemplate
            {
                Header = new AsmTemplate
                {
                    Code = "AI-" + Regex.Replace(spec.ItemType, @"[^A-Z0-9]+", "-").Trim('-'),
                    Name = S(root, "name") is { Length: > 0 } n ? n : "Proposed by Claude",
                    ItemType = spec.ItemType, Unit = S(root, "unit") is { Length: > 0 } u ? u.ToUpperInvariant() : (spec.Unit.Length > 0 ? spec.Unit : "PT"),
                    Origin = "AI", Confirmed = false, Active = true, Description = "Proposed by Claude - check every line before use", Notes = "AI proposal",
                },
            };
            if (root.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    var name = Ident(S(p, "name"));
                    if (name.Length == 0 || t.Params.Any(x => x.Name == name)) continue;
                    t.Params.Add(new AsmParam { Name = name, Value = D(p, "value"), Unit = S(p, "unit"), Label = S(p, "label"), Note = "AI proposal" });
                }
            var order = 0;
            if (root.TryGetProperty("components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    var kind = S(c, "kind").ToUpperInvariant();
                    if (!ComponentKinds.All.Contains(kind)) kind = ComponentKinds.Material;
                    var key = Ident(S(c, "key"));
                    if (key.Length == 0) key = "c" + (order + 1);
                    var stage = S(c, "stage").ToUpperInvariant();
                    if (!StageNames.All.Contains(stage)) stage = "";
                    var comp = new AsmComponent
                    {
                        Order = ++order, Key = key, Name = S(c, "name"), Spec = S(c, "spec"), Unit = S(c, "unit").ToUpperInvariant() is { Length: > 0 } cu ? cu : "PCS",
                        QtyFormula = S(c, "qty") is { Length: > 0 } q ? q : (c.TryGetProperty("qty", out var qn) && qn.ValueKind == JsonValueKind.Number ? qn.GetDouble().ToString(CultureInfo.InvariantCulture) : "1"),
                        WastePct = Math.Clamp(D(c, "waste"), 0, 0.5), Stage = stage, Kind = kind, Notes = "AI proposal",
                    };
                    if (kind == ComponentKinds.Labour) { comp.LabourBasis = LabourBases.Hours; comp.DefaultPrice = "lab_hr"; comp.Unit = "HR"; }
                    t.Components.Add(comp);
                }
            if (t.Components.Count == 0) return null;
            // a subcontract line so the template also works in SUBCONTRACT mode
            t.Components.Add(new AsmComponent { Order = ++order, Key = "sub_self", Name = "Subcontract labour", Spec = "Subcontract rate (contract item)", Unit = t.Header.Unit, QtyFormula = "1", Kind = ComponentKinds.Labour, LabourBasis = LabourBases.Subcontract, LabourCategory = "SELF", Notes = "added: contract rate lookup" });
            return t;
        }
        catch (JsonException) { return null; }
    }

    private static string Ident(string s)
    {
        var x = Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');
        if (x.Length > 0 && char.IsDigit(x[0])) x = "p_" + x;
        return x;
    }
}
