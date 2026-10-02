using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raffaello.Core.Assistant;

/// <summary>
/// A record or page an answer used. Tool results carry a token <c>[[kind:key]]</c> for each record; the model repeats the token after
/// the fact it supports and the chat shows it as a clickable chip that navigates in the app.
/// </summary>
public sealed record Citation(string Kind, string Key, string Label, string Module = "", string NavKey = "", string Path = "", int Page = 0)
{
    public string Token => $"[[{Kind}:{Key}]]";
    public bool CanNavigate => Module.Length > 0 || Path.Length > 0;

    public static Citation Room(string room) => new("room", room, "ROOM " + room, "Ledger", room);
    public static Citation Invoice(string contract, string sub, int no, int rev = -1) =>
        new("invoice", $"{contract}|{sub}|{no}", $"{sub} INV-{no:00}{(rev >= 0 ? $" Rev {rev}" : "")}", "Invoices", $"{contract}|{sub}|{no}");
    public static Citation Dn(string supplier, string dnNo) => new("dn", dnNo, $"DN {dnNo}{(supplier.Length > 0 ? " " + supplier : "")}", "Materials", "DN|" + dnNo);
    public static Citation Po(string poNo) => new("po", poNo, "PO " + poNo, "Materials", poNo);
    public static Citation Variation(string number) => new("variation", number, number, "Variations", number);
    public static Citation Workflow(string wf) => new("aconex", wf, "ACONEX " + wf, "Aconex", wf);
    public static Citation Wir(string no) => new("wir", no, no, "Wir", no);
    public static Citation Rule(string code, string title) => new("rule", code, "RULE " + title);
    public static Citation Contract(string no) => new("contract", no, "CONTRACT " + no, "Contracts", no);
    public static Citation Document(string id, string label, string path, int page = 0) =>
        new("doc", page > 0 ? $"{id}#p{page}" : id, page > 0 ? $"{label} p.{page}" : label, "", "", path, page);
    public static Citation Queue(string title, string module, string key) => new("queue", title, title, module, key);
    public static Citation Reminder(long id, string text) => new("reminder", id.ToString(System.Globalization.CultureInfo.InvariantCulture), "REMINDER " + text);
    public static Citation Report(string key, string name) => new("report", key, "REPORT " + name, "Reports", key);
}

public static class CitationText
{
    private static readonly Regex TokenRx = new(@"\[\[([a-z]+):([^\]\r\n]{1,160})\]\]", RegexOptions.Compiled);

    /// <summary>Tokens found in an answer, in order of first use (kind, key).</summary>
    public static List<(string Kind, string Key)> Tokens(string text) =>
        TokenRx.Matches(text ?? "").Select(m => (m.Groups[1].Value, m.Groups[2].Value.Trim())).Distinct().ToList();

    /// <summary>The answer without the tokens (what the bubble shows; the chips show the sources).</summary>
    public static string Strip(string text) =>
        Regex.Replace(TokenRx.Replace(text ?? "", ""), @"[ \t]+([.,;:)])", "$1").Replace("  ", " ");

    /// <summary>
    /// The citations an answer used: tokens the model wrote, resolved against the records the tools returned this turn; when the model
    /// wrote none, every record the tools returned is listed (so an answer is never without its sources).
    /// </summary>
    public static List<Citation> Resolve(string answer, IReadOnlyCollection<Citation> available)
    {
        var byToken = available.GroupBy(c => (c.Kind, c.Key)).ToDictionary(g => g.Key, g => g.First());
        var used = new List<Citation>();
        foreach (var t in Tokens(answer))
            if (byToken.TryGetValue(t, out var c)) used.Add(c);
            else used.Add(new Citation(t.Kind, t.Key, $"{t.Kind.ToUpperInvariant()} {t.Key}"));
        if (used.Count == 0) used.AddRange(available.Take(12));
        return used.Distinct().ToList();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string Serialize(IEnumerable<Citation> c) => JsonSerializer.Serialize(c.ToList(), Json);
    public static List<Citation> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<Citation>>(json, Json) ?? new(); } catch (JsonException) { return new(); }
    }
}
