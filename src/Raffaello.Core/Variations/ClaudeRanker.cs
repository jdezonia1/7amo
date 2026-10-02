using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Raffaello.Core.Ai;

namespace Raffaello.Core.Variations;

/// <summary>
/// Optional semantic re-ranking of the offline suggestions by Claude. Used only when enabled in Settings and an API key
/// is present; only the variation text (trimmed) and the candidate descriptions are sent, never prices or documents.
/// </summary>
public sealed class ClaudeRanker
{
    private readonly AnthropicClient _client;
    public ClaudeRanker(AnthropicClient client) => _client = client;

    public const string System = "You are an electrical quantity surveyor on a hotel and residences project. " +
        "Given a variation / engineer's instruction and numbered candidate BOQ or contract items, rank the candidates the variation most likely " +
        "adds to or omits from. Reply with JSON only: [{\"i\": <number>, \"score\": <0..1>, \"why\": \"<max 12 words>\"}], best first, at most 10 entries, " +
        "omit unrelated candidates.";

    public static string BuildPrompt(string title, string body, IReadOnlyList<Suggestion> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("VARIATION:").AppendLine(title.Trim());
        var b = (body ?? "").Trim();
        if (b.Length > 4000) b = b[..4000] + " ...";
        if (b.Length > 0) sb.AppendLine(b);
        sb.AppendLine().AppendLine("CANDIDATES:");
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i].Candidate;
            var d = c.Description.Length > 300 ? c.Description[..300] : c.Description;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. [{c.Source} {c.Code}] ({c.Unit}) {d.Replace('\n', ' ')}");
        }
        return sb.ToString();
    }

    /// <summary>Reads the model's JSON (tolerates prose or code fences around it). Unknown indexes are ignored.</summary>
    public static List<(int Index, double Score, string Why)> ParseRanking(string reply, int count)
    {
        var list = new List<(int, double, string)>();
        var m = Regex.Match(reply ?? "", @"\[[\s\S]*\]");
        if (!m.Success) return list;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("i", out var iEl) || iEl.ValueKind != JsonValueKind.Number || !iEl.TryGetInt32(out var i)) continue;
                if (i < 1 || i > count) continue;
                var score = e.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var sv) ? Math.Clamp(sv, 0, 1) : 0.5;
                var why = e.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
                if (list.All(x => x.Item1 != i - 1)) list.Add((i - 1, score, why));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Merges: ranked candidates first (score = 0.7 x AI + 0.3 x offline), then the rest in offline order.</summary>
    public static List<Suggestion> Merge(IReadOnlyList<Suggestion> offline, IReadOnlyList<(int Index, double Score, string Why)> ranking)
    {
        var ranked = ranking.Select(r => offline[r.Index] with
        {
            Score = Math.Round(0.7 * r.Score + 0.3 * offline[r.Index].Score, 3),
            Why = ("AI: " + r.Why + " | " + offline[r.Index].Why).Trim(' ', '|'),
        }).OrderByDescending(s => s.Score).ToList();
        var used = ranking.Select(r => r.Index).ToHashSet();
        return ranked.Concat(offline.Where((s, i) => !used.Contains(i))).ToList();
    }

    public async Task<List<Suggestion>> RerankAsync(string title, string body, IReadOnlyList<Suggestion> offline, CancellationToken ct = default)
    {
        if (offline.Count == 0) return offline.ToList();
        var top = offline.Take(30).ToList();
        var reply = await _client.CompleteAsync(System, new[] { new ChatMessage("user", BuildPrompt(title, body, top)) }, ct).ConfigureAwait(false);
        var ranking = ParseRanking(reply, top.Count);
        return ranking.Count == 0 ? offline.ToList() : Merge(top, ranking).Concat(offline.Skip(top.Count)).ToList();
    }
}
