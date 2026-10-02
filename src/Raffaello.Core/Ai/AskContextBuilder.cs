using System.Text;
using Raffaello.Core.Chain;
using Raffaello.Core.Domain;
using Raffaello.Core.Queue;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Ai;

/// <summary>Builds the system prompt for "Ask Raffaello": house rules, current filter scope, the selected line's chain.</summary>
public static class AskContextBuilder
{
    public const string HouseRules = """
        House rules of this project (apply them in every answer):
        - The chain for each line is QS / PROJECT QTY -> GIVEN to subcontractor -> DONE on site (approved WIR) -> CLAIMED (cumulative invoice / statement) -> DELIVERED (delivery note). Every problem is a mismatch in that chain.
        - Stages (1ST FIX, 2ND FIX, FINAL FIX) measure the same points; never add stages together.
        - Lines that exceed their cap are posted at full quantity and carry an OVER flag.
        - CLAIMED greater than DONE (WIR) is CHECK: hold before certifying.
        - EMT is rework and never counts as progress.
        - PROJECT QTY caps subcontractor claims (QS is used while PROJECT QTY is empty).
        - Compare progress after SITE %; value invoices on WIR %.
        - Pipes and conduits: PCS to M at 6 m per piece unless the PO line says otherwise.
        - Delivered above the PO quantity is an OVER alarm.
        - Counting: twin socket = 1 point, twin data = 2 points at 2ND FIX, TV + soundbar = 1, switches under LIGHT, thermostat CP-4 counted in GRMS only.
        """;

    public static string Build(string userName, FilterSpec filter, ChainRow? selected, IReadOnlyList<ChainRow> scope, IReadOnlyList<QueueItem> queue, string screen)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Raffaello, the assistant inside MOBCO's desktop app for the Raffles Hotel and Branded Residences MEP (electrical / ELV) project in Riyadh.");
        sb.AppendLine($"You are helping {userName}, a QS / site engineer. Answer in short, plain site-office English. Use UPPERCASE for labels like OVER, CHECK, WIR, PROJECT QTY. Quote quantities with units and SAR with thousands separators. No emojis.");
        sb.AppendLine("Only use the figures given below; if something is not in the data, say what is missing instead of guessing.");
        sb.AppendLine();
        sb.AppendLine(HouseRules);
        sb.AppendLine($"Current screen: {screen}. Filter scope: {filter.Describe()}.");
        if (scope.Count > 0)
        {
            sb.AppendLine("Scope totals by stage (never summed across stages):");
            foreach (var t in ChainMath.TotalsByStage(scope).Values)
                sb.AppendLine($"- {t.Stage}: {t.Lines} lines, QS {t.Qs:N0}, GIVEN {t.Given:N0} ({t.GivenPct:P0}), DONE {t.Done:N0} ({t.DonePct:P0}), CLAIMED {t.Claimed:N0}, CERTIFIED {t.Certified:N0}");
            sb.AppendLine($"Flags in scope: OVER {scope.Count(r => r.Verdict == Verdict.Over)}, CHECK {scope.Count(r => r.Verdict == Verdict.Check)}, DUE {scope.Count(r => r.Verdict == Verdict.Due)}, OPEN {scope.Count(r => r.Verdict == Verdict.Open)}.");
        }
        if (selected != null)
        {
            sb.AppendLine();
            sb.AppendLine("Selected line (the user is looking at this one):");
            sb.AppendLine(Describe(selected));
        }
        if (queue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Needs-you-today queue (top items):");
            foreach (var q in queue.Take(8)) sb.AppendLine($"- [{q.Tag}] {q.Title}: {q.Detail}");
        }
        return sb.ToString();
    }

    public static string Describe(ChainRow r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{r.Building} {r.Level} ROOM {r.Room} ({r.RoomType}) - {r.System} - {r.Stage} - item {r.ItemCode} @ SAR {r.Rate:N2}/{r.Unit}");
        sb.AppendLine($"QS {r.Qs:N0}; PROJECT QTY {(r.ProjectQty.HasValue ? r.ProjectQty.Value.ToString("N0") : "not filled (QS used as cap)")}");
        sb.AppendLine($"GIVEN {r.Given:N0} to {(string.IsNullOrEmpty(r.Subcontractors) ? "nobody" : r.Subcontractors)}; REMAINING {r.Remaining:N0}");
        sb.AppendLine($"DONE (approved WIR) {r.Done:N0} = {r.WirPct:P0}; SITE % {r.SitePct:P0}; rework/EMT {r.Rework:N0}; open WIRs {r.OpenWirs}{(r.OpenWirs > 0 ? $" (oldest {r.OldestOpenWirDays} days, {r.LastWirNo})" : "")}");
        sb.AppendLine($"CLAIMED {r.Claimed:N0} (last statement {(string.IsNullOrEmpty(r.LastInvoiceNo) ? "none" : r.LastInvoiceNo)}); CERTIFIED {r.Certified:N0}; certifiable now {r.CertifiableQty:N0}");
        sb.AppendLine($"DELIVERED {(r.Delivered.HasValue ? r.Delivered.Value.ToString("N0") : "not tracked at this stage")}");
        sb.AppendLine($"Verdict {r.Status}: {string.Join("; ", r.Findings.Select(f => $"[{VerdictText.Of(f.Severity)}] {f.Message}"))}");
        return sb.ToString();
    }

    /// <summary>Offline answer used when no API key is configured: explains the selected chain from the rules.</summary>
    public static string OfflineAnswer(ChainRow? selected, IReadOnlyList<QueueItem> queue)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ASK RAFFAELLO is offline (no API key in Settings and no ANTHROPIC_API_KEY). Here is what the rules engine says:");
        sb.AppendLine();
        if (selected != null)
        {
            sb.AppendLine($"{selected.Key}: {selected.Status}");
            foreach (var f in selected.Findings) sb.AppendLine($"- {VerdictText.Of(f.Severity)}: {f.Message}");
            if (selected.Findings.Count == 0) sb.AppendLine("- Chain consistent: QS, GIVEN, DONE and CLAIMED agree.");
            sb.AppendLine($"- Certifiable now: {selected.CertifiableQty:N0} {selected.Unit} (min of CLAIMED, DONE, PROJECT QTY).");
        }
        else if (queue.Count > 0)
        {
            sb.AppendLine("Top of today's queue:");
            foreach (var q in queue.Take(5)) sb.AppendLine($"- [{q.Tag}] {q.Title}");
        }
        return sb.ToString();
    }
}
