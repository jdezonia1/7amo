using System.Globalization;
using Raffaello.Core.Documents.Ocr;

namespace Raffaello.Core.Documents.Smart;

/// <summary>One reading of a field by one engine (or derived from other fields by a rule).</summary>
public sealed record FieldCandidate(string Value, double Confidence, string Engine, Box? Box = null, int Page = 0);

public static class FieldStatus
{
    /// <summary>Two or more engines read the same value.</summary>
    public const string Agreed = "AGREED";
    /// <summary>Only one reading, confident and the validators pass.</summary>
    public const string Single = "SINGLE";
    /// <summary>Engines disagreed; the validators picked one (e.g. qty x rate = total).</summary>
    public const string Validated = "VALIDATED";
    /// <summary>Computed from other fields (the cell was unreadable) - shown for review.</summary>
    public const string Derived = "DERIVED";
    /// <summary>Engines disagree and no validator decides - the user must choose. Never silently picked.</summary>
    public const string Conflict = "CONFLICT";
    /// <summary>Low confidence, nothing to compare against.</summary>
    public const string Low = "LOW";
    /// <summary>Not found.</summary>
    public const string Missing = "MISSING";
    /// <summary>Set or confirmed by a person.</summary>
    public const string Confirmed = "CONFIRMED";
    public static bool NeedsReview(string s) => s is Conflict or Low or Missing or Derived;
}

/// <summary>A field after voting: chosen value, confidence, status and every reading kept for the review screen.</summary>
public sealed class FieldResult
{
    public string Name { get; init; } = "";
    public string Value { get; set; } = "";
    public double Confidence { get; set; }
    public string Status { get; set; } = FieldStatus.Missing;
    public Box? Box { get; set; }
    public int Page { get; set; }
    public List<FieldCandidate> Candidates { get; } = new();
    public string Note { get; set; } = "";
    public bool NeedsReview => FieldStatus.NeedsReview(Status);
    public double? Number => ArabicText.ParseNumber(Value);
    public override string ToString() => $"{Name}={Value} [{Status} {Confidence:0.00}]";
}

/// <summary>
/// Votes between engine readings of one field. Values are compared in a normalised form (digits, separators, Arabic variants); equal
/// readings add up; a validator can rule readings out; a disagreement that nothing resolves is a CONFLICT - the highest-confidence
/// reading is shown first but the field is flagged and the alternatives are kept.
/// </summary>
public static class FieldVote
{
    public static double LowConfidence { get; set; } = 0.75;

    public static string Key(string v)
    {
        var n = ArabicText.Normalize(v).Replace(" ", "");
        var num = ArabicText.ParseNumber(v);
        return num is double d && n.Count(char.IsDigit) >= n.Length / 2 ? d.ToString("0.######", CultureInfo.InvariantCulture) : n;
    }

    public static FieldResult Decide(string name, IEnumerable<FieldCandidate> candidates, Func<string, bool>? validator = null)
    {
        var all = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToList();
        var res = new FieldResult { Name = name };
        res.Candidates.AddRange(all);
        if (all.Count == 0) { res.Status = FieldStatus.Missing; return res; }
        var valid = validator is null ? all : all.Where(c => validator(c.Value)).ToList();
        if (validator != null && valid.Count == 0)
        {
            var b0 = all.OrderByDescending(c => c.Confidence).First();
            res.Value = b0.Value.Trim(); res.Box = b0.Box; res.Page = b0.Page;
            res.Status = FieldStatus.Conflict;
            res.Confidence = Math.Min(b0.Confidence, 0.4);
            res.Note = "no reading passes the check: " + string.Join(" / ", all.Select(c => $"{c.Value} ({c.Engine})").Distinct());
            return res;
        }
        var groups = valid.GroupBy(c => Key(c.Value))
            .Select(g => (Items: g.ToList(), Engines: g.Select(c => c.Engine).Distinct().Count(), Score: g.Sum(c => c.Confidence)))
            .OrderByDescending(g => g.Engines).ThenByDescending(g => g.Score).ToList();
        var top = groups[0];
        var pick = top.Items.OrderByDescending(c => c.Confidence).First();
        res.Value = pick.Value.Trim(); res.Box = pick.Box; res.Page = pick.Page;
        var removed = all.Count - valid.Count;
        if (groups.Count == 1)
        {
            if (removed > 0)
            {
                res.Status = FieldStatus.Validated;
                res.Confidence = Math.Max(pick.Confidence, 0.8);
                res.Note = $"{removed} reading(s) failed the check";
            }
            else if (top.Engines >= 2)
            {
                res.Status = FieldStatus.Agreed;
                res.Confidence = Math.Min(0.99, top.Items.Max(c => c.Confidence) + 0.1 * (top.Engines - 1));
            }
            else
            {
                res.Confidence = pick.Confidence;
                res.Status = pick.Confidence >= LowConfidence ? FieldStatus.Single : FieldStatus.Low;
            }
            return res;
        }
        var second = groups[1];
        var dissent = string.Join(" / ", valid.Select(c => $"{c.Value} ({c.Engine} {c.Confidence:0.00})").Distinct());
        if (top.Engines > second.Engines)
        {
            res.Status = FieldStatus.Agreed;
            res.Confidence = Math.Min(0.9, pick.Confidence);
            res.Note = "majority: " + dissent;
        }
        else
        {
            res.Status = FieldStatus.Conflict;
            res.Confidence = Math.Min(0.5, pick.Confidence);
            res.Note = "engines disagree: " + dissent;
        }
        return res;
    }

    public static FieldResult Single(string name, string value, double confidence, string engine, Box? box = null, int page = 0) =>
        Decide(name, new[] { new FieldCandidate(value, confidence, engine, box, page) });
}
