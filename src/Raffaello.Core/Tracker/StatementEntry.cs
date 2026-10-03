using System.Text.Json;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Tracker;

/// <summary>What to do with a statement line that claims more than the remaining quantity.</summary>
public static class PostOptions
{
    /// <summary>Post min(claimed, remaining); the excess stays ON HOLD (not posted) with the reason.</summary>
    public const string WithinRemaining = "WITHIN REMAINING";
    /// <summary>Post the full claimed quantity as OVER (needs a reason - the existing OVER-with-reason rule).</summary>
    public const string FullWithReason = "FULL WITH REASON";
    /// <summary>2ND FIX only: post the remaining as plan qty and the full claim as a 15 m route-length claim (LENGTH CHECK PENDING).</summary>
    public const string LengthPending = "15 M CHECK";
    /// <summary>Post nothing for this line.</summary>
    public const string Skip = "SKIP";
    public static readonly string[] All = { WithinRemaining, FullWithReason, LengthPending, Skip };
}

public sealed class StatementHeader
{
    public string Subcontractor { get; set; } = "";
    public int InvoiceNo { get; set; } = 1;
    public string StatementNo { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Today;
    public string Building { get; set; } = Buildings.Hotel;
    public double SitePct { get; set; } = 1;
    public double WirPct { get; set; } = 1;
}

public sealed class StatementDraftLine
{
    public string Room { get; set; } = "";
    public string Floor { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Item { get; set; } = "";
    /// <summary>The subcontractor's claimed quantity (whole numbers only).</summary>
    public int Claimed { get; set; }
    public string Option { get; set; } = PostOptions.WithinRemaining;
    public string Reason { get; set; } = "";
    public string Key => LedgerKeys.Key(Room, Stage, Item);
}

/// <summary>A statement being entered: header + lines. Saved as a JSON draft so the work can be resumed.</summary>
public sealed class StatementDraft
{
    public StatementHeader Header { get; set; } = new();
    public List<StatementDraftLine> Lines { get; set; } = new();
    public DateTime SavedAt { get; set; }
    public bool Posted { get; set; }

    public string FileName => Safe($"{Header.Building}_{Header.Subcontractor}_INV{Header.InvoiceNo}_{(Header.StatementNo.Length > 0 ? Header.StatementNo : Header.Date.ToString("yyyyMMdd"))}") + ".json";

    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Save(string folder)
    {
        Directory.CreateDirectory(folder);
        SavedAt = DateTime.Now;
        var path = Path.Combine(folder, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        return path;
    }

    public static StatementDraft? Load(string path)
    {
        try { return JsonSerializer.Deserialize<StatementDraft>(File.ReadAllText(path), Json); }
        catch (Exception) { return null; }
    }

    public static IEnumerable<string> List(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderByDescending(File.GetLastWriteTime) : Array.Empty<string>();
}

/// <summary>Status words of a planned statement line.</summary>
public static class PlanStatus
{
    public const string Ok = "OK";
    public const string Part = "PART";
    public const string OnHold = "ON HOLD";
    public const string Over = "OVER";
    public const string LengthPending = "15 M PENDING";
    public const string NotCompared = "NOT COMPARED";
    public const string Skipped = "SKIPPED";
}

/// <summary>One statement line after the remaining check: what will be posted (whole numbers) and why the rest is not.</summary>
public sealed record PlannedLine(StatementDraftLine Line, double ProjectQty, double AlreadyClaimed, double RemainingBefore, int Post, int Excess,
    string Status, string Reason, bool NotCompared, bool LengthPending, bool Over)
{
    public int Claimed => Line.Claimed;
    public double RemainingAfter => RemainingBefore - Post;
}

/// <summary>
/// Turns a statement (subcontractor CLAIMED quantities per location x stage x item) into ledger lines, using the room ledger
/// balance (PROJECT QTY - all subcontractors): within remaining = posted as claimed; above remaining = by the line's option.
/// Quantities are whole numbers; the remaining is rounded down. Lines of the same key in one statement share the remaining.
/// </summary>
public static class StatementPlanner
{
    public static bool IsNotComparedStage(string stage) =>
        Recon.ReconImporter.NotComparedStages.Contains((stage ?? "").Trim().ToUpperInvariant());

    public static bool IsSecondFix(string stage) => (stage ?? "").Trim().ToUpperInvariant() is "2ND FIX" or "2NDFIX";

    public static List<PlannedLine> Plan(StatementDraft draft, Func<string, string, string, RoomBalance> balance)
    {
        var res = new List<PlannedLine>();
        var used = new Dictionary<string, double>();
        foreach (var l in draft.Lines)
        {
            var bal = balance(l.Room, l.Stage, l.Item);
            var already = bal.Claimed + used.GetValueOrDefault(l.Key);
            var remaining = bal.ProjectQty - already;
            var claimed = Math.Max(0, l.Claimed);
            if (claimed == 0 || l.Option == PostOptions.Skip)
            {
                res.Add(new(l, bal.ProjectQty, already, remaining, 0, claimed, PlanStatus.Skipped, claimed == 0 ? "nothing claimed" : "skipped by you", false, false, false));
                continue;
            }
            if (IsNotComparedStage(l.Stage))
            {
                res.Add(new(l, bal.ProjectQty, already, remaining, claimed, 0, PlanStatus.NotCompared, "not compared with a total (cable pulling / cable tray)", true, false, false));
                continue;
            }
            var whole = bal.HasCap ? (int)Math.Floor(Math.Max(0, remaining) + 1e-6) : 0;
            PlannedLine p;
            if (claimed <= whole)
                p = new(l, bal.ProjectQty, already, remaining, claimed, 0, PlanStatus.Ok, "", false, false, false);
            else
            {
                var why = bal.HasCap ? $"over remaining {Math.Max(0, remaining):0.##}" : "no PROJECT QTY for this location / stage / item";
                var option = l.Option;
                if (option == PostOptions.FullWithReason && string.IsNullOrWhiteSpace(l.Reason)) option = PostOptions.WithinRemaining;
                if (option == PostOptions.LengthPending && (!IsSecondFix(l.Stage) || !bal.HasCap)) option = PostOptions.WithinRemaining;
                p = option switch
                {
                    PostOptions.FullWithReason => new(l, bal.ProjectQty, already, remaining, claimed, 0, PlanStatus.Over, $"{why} - posted in full: {l.Reason.Trim()}", false, false, true),
                    PostOptions.LengthPending => new(l, bal.ProjectQty, already, remaining, whole, claimed - whole, PlanStatus.LengthPending,
                        $"{why} - 15 m route-length proof needed for {claimed - whole}", false, true, false),
                    _ => new(l, bal.ProjectQty, already, remaining, whole, claimed - whole, whole > 0 ? PlanStatus.Part : PlanStatus.OnHold,
                        $"{why} - {claimed - whole} on hold" + (l.Option == PostOptions.FullWithReason ? " (posting in full needs a reason)" : l.Option == PostOptions.LengthPending ? " (15 m check is for 2ND FIX with a total only)" : ""), false, false, false),
                };
            }
            used[l.Key] = used.GetValueOrDefault(l.Key) + p.Post;
            res.Add(p);
        }
        return res;
    }

    /// <summary>Ledger lines for the plan (lines with nothing to post are left out). OverReason is set on OVER lines.</summary>
    public static List<(PlannedLine Plan, ClaimLine Claim, string? OverReason)> ToClaims(StatementHeader h, IEnumerable<PlannedLine> plan)
    {
        var res = new List<(PlannedLine, ClaimLine, string?)>();
        var i = 0;
        foreach (var p in plan)
        {
            i++;
            if (p.Post <= 0 && !p.LengthPending) continue;
            var l = p.Line;
            var claim = new ClaimLine
            {
                Building = h.Building, Subcontractor = h.Subcontractor.Trim().ToUpperInvariant(), InvoiceNo = h.InvoiceNo,
                Stage = l.Stage.Trim().ToUpperInvariant(), Floor = l.Floor, Room = l.Room.Trim(), Item = l.Item.Trim().ToUpperInvariant(),
                Unit = p.NotCompared ? "m" : "no", Qty = p.Post, SitePct = h.SitePct, WirPct = h.WirPct, StatementNo = h.StatementNo,
                Source = "STATEMENT", SourceKey = $"STMT|{h.Building}|{h.Subcontractor}|{h.StatementNo}|{h.InvoiceNo}|{i}".ToUpperInvariant(),
                WorkType = p.NotCompared ? LedgerRules.NotComparedWorkType : "",
                Notes = $"STATEMENT {h.StatementNo} {h.Date:dd MMM yy}: claimed {l.Claimed}" + (p.Excess > 0 ? $", {p.Excess} not posted ({p.Reason})" : p.Reason.Length > 0 ? $" ({p.Reason})" : ""),
            };
            if (p.LengthPending) { claim.LengthApplies = true; claim.LengthClaimedQty = l.Claimed; }
            res.Add((p, claim, p.Over ? l.Reason.Trim() : null));
        }
        return res;
    }
}

/// <summary>One row of the end comparison sent back to the subcontractor.</summary>
public sealed record ComparisonRow(string Location, string Stage, string Item, int Claimed, int Certified, string Status, string Reason, double? Rate, string RateStatus,
    double ClaimedAmount, double CertifiedAmount)
{
    public int Difference => Claimed - Certified;
    public double DifferenceAmount => ClaimedAmount - CertifiedAmount;
}

public static class StatementComparison
{
    /// <summary>
    /// SUBCONTRACTOR CLAIMED vs MY CERTIFIED per location x stage x item, priced at the subcontractor's rate x SITE % x WIR %.
    /// A 15 m pending line is certified at its plan qty until the route-length check is decided.
    /// </summary>
    public static List<ComparisonRow> Build(StatementHeader h, IEnumerable<PlannedLine> plan, RateBook rates) =>
        plan.Where(p => p.Claimed > 0).Select(p =>
        {
            var r = rates.Resolve(h.Subcontractor, h.Building, h.InvoiceNo, p.Line.Stage, p.Line.Item);
            var claimedAmt = ClaimAmount.Of(p.Claimed, h.SitePct, h.WirPct, r).AfterWir;
            var certAmt = ClaimAmount.Of(p.Post, h.SitePct, h.WirPct, r).AfterWir;
            return new ComparisonRow(p.Line.Room, p.Line.Stage, p.Line.Item, p.Claimed, p.Post, p.Status, p.Reason, r.Rate, r.Status == RateStatus.Missing ? $"MISSING: {r.Reason}" : r.Source, claimedAmt, certAmt);
        }).ToList();
}