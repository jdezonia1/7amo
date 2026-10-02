using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;
using Raffaello.Core.Variations;

namespace Raffaello.Core.Assistant;

public static class AssistantActionKinds
{
    public const string DraftLedgerClaim = "draft_ledger_claim";
    public const string DraftInvoiceRevision = "draft_invoice_revision";
    public const string DraftRejectionReplyEmail = "draft_rejection_reply_email";
    public const string DraftVariation = "draft_variation";
    public const string CreateReminder = "create_reminder";
    public static readonly string[] All = { DraftLedgerClaim, DraftInvoiceRevision, DraftRejectionReplyEmail, DraftVariation, CreateReminder };

    public static string Label(string kind) => kind switch
    {
        DraftLedgerClaim => "POST LEDGER CLAIM",
        DraftInvoiceRevision => "NEW INVOICE REVISION",
        DraftRejectionReplyEmail => "SAVE E-MAIL DRAFT",
        DraftVariation => "CREATE VARIATION",
        CreateReminder => "CREATE REMINDER",
        _ => kind.ToUpperInvariant(),
    };
}

/// <summary>
/// Write actions the assistant can only PROPOSE. A proposal is stored (status PROPOSED, audited) and shown as a card in the chat;
/// the write happens in <see cref="Execute"/> when the user presses CONFIRM - re-checked against the data as it is then.
/// </summary>
public sealed class AssistantActions
{
    private readonly AssistantData _d;
    public AssistantActions(AssistantData d) => _d = d;

    private static string Up(string? s) => (s ?? "").Trim().ToUpperInvariant();
    private static string S(JsonObject o, string k) => AssistantTools.S(o, k) ?? "";

    public ToolOutcome Propose(string kind, JsonObject input, string toolUseId, long conversationId)
    {
        var (preview, warnings, error) = kind switch
        {
            AssistantActionKinds.DraftLedgerClaim => PreviewClaim(input),
            AssistantActionKinds.DraftInvoiceRevision => PreviewRevision(input),
            AssistantActionKinds.DraftRejectionReplyEmail => PreviewEmail(input),
            AssistantActionKinds.DraftVariation => PreviewVariation(input),
            AssistantActionKinds.CreateReminder => PreviewReminder(input),
            _ => ("", "", $"Unknown action {kind}."),
        };
        if (error.Length > 0) return new ToolOutcome { Content = error, IsError = true };
        var action = new AssistantAction
        {
            ConversationId = conversationId, ToolUseId = toolUseId, Kind = kind, InputJson = input.ToJsonString(), Preview = preview, Warnings = warnings,
            Status = AssistantActionStatus.Proposed, ProposedBy = _d.User, ProposedAt = _d.Clock(),
        };
        _d.Store.Insert(action, $"Assistant proposed {AssistantActionKinds.Label(kind)} for {_d.User}: {FirstLine(preview)}");
        var result = new JsonObject
        {
            ["status"] = "awaiting_user_confirmation",
            ["action_id"] = action.Id,
            ["preview"] = preview,
            ["warnings"] = warnings.Length > 0 ? warnings : null,
            ["instruction"] = "Nothing has been written. Tell the user what the card will do and that it runs only when they press CONFIRM in the chat.",
        };
        return new ToolOutcome { Content = result.ToJsonString(), Proposed = action };
    }

    private static string FirstLine(string s) => s.Split('\n')[0];

    // ------------------------------------------------------------------ previews

    private ClaimLine ClaimFrom(JsonObject i)
    {
        var room = Up(S(i, "room"));
        var roomRow = _d.Project.Snapshot.Rooms.FirstOrDefault(r => Up(r.Code) == room);
        var building = AssistantTools.S(i, "building") is { } b ? Up(b) : roomRow?.Building ?? _d.Project.Snapshot.Claims.FirstOrDefault(c => Up(c.Room) == room)?.Building ?? Buildings.Branded;
        var lenClaimed = AssistantTools.D(i, "length_claimed_qty") ?? 0;
        var qty = AssistantTools.D(i, "qty") ?? 0;
        return new ClaimLine
        {
            Building = building, Subcontractor = S(i, "subcontractor"), InvoiceNo = AssistantTools.I(i, "invoice_no") ?? 0, Stage = Stages.Normalize(S(i, "stage")),
            Floor = AssistantTools.S(i, "floor") ?? roomRow?.Level ?? "", Room = room, Item = Up(S(i, "item")), Qty = qty,
            SitePct = Math.Clamp(AssistantTools.D(i, "site_pct") ?? 1, 0, 1), WirPct = Math.Clamp(AssistantTools.D(i, "wir_pct") ?? 1, 0, 1),
            WirNo = S(i, "wir_no"), Notes = string.IsNullOrEmpty(S(i, "notes")) ? "via assistant" : S(i, "notes") + " (via assistant)",
            QtyAbove45 = AssistantTools.D(i, "qty_above_4_5m") ?? 0,
            LengthApplies = lenClaimed > qty, LengthClaimedQty = lenClaimed,
            Source = "ASSISTANT",
        };
    }

    private (string, string, string) PreviewClaim(JsonObject i)
    {
        var line = ClaimFrom(i);
        if (line.Qty == 0) return ("", "", "qty must not be 0.");
        var bal = _d.Project.Workflow.Balance(line.Room, line.Stage, line.Item);
        var over = AssistantTools.S(i, "over_reason");
        var check = LedgerRules.Check(bal, line.Qty, over);
        if (!check.CanPost)
            return ("", "", $"Not proposed: {check.Message} Ask the user for the reason (over_reason) or a smaller quantity.");
        var warnings = new List<string>();
        if (!_d.Project.Snapshot.Rooms.Any(r => Up(r.Code) == line.Room) && !_d.Project.Snapshot.RoomQtys.Any(q => Up(q.Room) == line.Room))
            warnings.Add($"Room {line.Room} is not in the room list.");
        if (check.IsOver) warnings.Add("OVER: " + check.Message);
        if (line.QtyAbove45 > 0) warnings.Add($"{line.QtyAbove45:0.##} above 4.5 m: a HEIGHT check opens (held out of the invoice until decided).");
        if (line.LengthApplies) warnings.Add($"{line.LengthClaimedQty:0.##} claimed with the 15 m rule: a LENGTH check opens.");
        var preview = $"{line.Subcontractor} INV {line.InvoiceNo}: {line.Building} {line.Room} {line.Stage} {line.Item} qty {line.Qty:0.##}" +
                      $" (SITE {line.SitePct:P0}, WIR {line.WirPct:P0}{(line.WirNo.Length > 0 ? ", " + line.WirNo : "")})\n" +
                      $"PROJECT QTY {bal.ProjectQty:0.##}, claimed so far {bal.Claimed:0.##}, remaining {bal.Remaining:0.##} -> after this claim {bal.Remaining - line.Qty:0.##}";
        return (preview, string.Join("\n", warnings), "");
    }

    private (string, string, string) PreviewRevision(JsonObject i)
    {
        var sub = S(i, "subcontractor");
        var no = AssistantTools.I(i, "invoice_no") ?? 0;
        var contract = AssistantTools.S(i, "contract_no");
        var revs = _d.Project.Snapshot.SubInvoices.Where(x => x.Subcontractor.Contains(sub, StringComparison.OrdinalIgnoreCase) && x.InvoiceNo == no
                                                               && (contract is null || string.Equals(x.ContractNo, contract, StringComparison.OrdinalIgnoreCase))).ToList();
        if (revs.Count == 0) return ("", "", $"No invoice {sub} INV-{no:00} found.");
        var groups = revs.GroupBy(x => (x.ContractNo, x.Subcontractor)).ToList();
        if (groups.Count > 1) return ("", "", $"Several invoices match ({string.Join(", ", groups.Select(g => g.Key.ContractNo + " " + g.Key.Subcontractor))}). Give contract_no.");
        var last = revs.OrderBy(x => x.Revision).Last();
        if (last.Locked) return ("", "", $"{last.Title} is approved and locked; a new revision is not allowed. Start the next invoice number instead.");
        var next = Invoicing.InvoiceWorkflow.NextRevision(_d.Project.Snapshot.SubInvoices, last.ContractNo, last.Subcontractor, no);
        var build = _d.Project.Workflow.BuildInvoice(last.ContractNo, last.Subcontractor, no, next);
        var warnings = new List<string>();
        if (last.Status != SubInvoiceStatus.Rejected) warnings.Add($"{last.Title} is {last.Status}, not REJECTED.");
        var toConfirm = build.Mapping.NeedsConfirmation.Count();
        if (toConfirm > 0) warnings.Add($"{toConfirm} mapping groups need confirmation on the INVOICES screen before submitting.");
        warnings.AddRange(build.Warnings.Take(5));
        var preview = $"Build {last.Subcontractor} INV-{no:00} Rev {next} from the ledger (contract {last.ContractNo}) and save it as DRAFT.\n" +
                      $"This period SAR {build.Totals.CurrGross:N2}, cumulative SAR {build.Totals.CumGross:N2}. Last revision: Rev {last.Revision} {last.Status}" +
                      $"{(last.RejectionReason.Length > 0 ? " - " + last.RejectionReason : "")}.";
        return (preview, string.Join("\n", warnings), "");
    }

    private (string, string, string) PreviewEmail(JsonObject i)
    {
        var body = S(i, "body");
        if (body.Length == 0) return ("", "", "body is empty.");
        var preview = $"E-mail draft{(S(i, "to").Length > 0 ? " to " + S(i, "to") : "")}: {S(i, "subject")}\n\n{(body.Length > 1200 ? body[..1200] + "..." : body)}";
        return (preview, "Saved as an .eml draft to open in Outlook - nothing is sent.", "");
    }

    private (string, string, string) PreviewVariation(JsonObject i)
    {
        var type = Up(S(i, "type"));
        var lines = (i["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        double total = 0;
        var sb = new StringBuilder($"{type} '{S(i, "title")}' (DRAFT{(S(i, "consultant_ref").Length > 0 ? ", ref " + S(i, "consultant_ref") : "")})");
        foreach (var l in lines.Take(20))
        {
            var qty = AssistantTools.D(l, "qty") ?? 0; var rate = AssistantTools.D(l, "rate") ?? 0;
            var sign = Up(S(l, "kind")) == VariationLineKinds.Omission ? -1 : 1;
            total += sign * qty * rate;
            sb.Append($"\n- {Up(S(l, "kind"))} {S(l, "item_code")} {S(l, "description")}: {qty:0.##} {S(l, "unit")} x SAR {rate:N2}");
        }
        if (lines.Count > 0) sb.Append($"\nNet value SAR {total:N2}");
        return (sb.ToString(), _d.TryVariations() is null ? "The variations register could not be opened - CONFIRM will fail." : "", "");
    }

    public static DateTime? ParseDue(string s, DateTime now)
    {
        string[] formats = { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK" };
        if (DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return s.Trim().Length <= 10 ? d.Date.AddHours(8) : d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    private (string, string, string) PreviewReminder(JsonObject i)
    {
        var due = ParseDue(S(i, "due"), _d.Clock());
        if (due is null) return ("", "", "due must be an ISO date (yyyy-MM-dd) or date-time (yyyy-MM-ddTHH:mm).");
        return ($"Reminder {due:dd MMM yyyy HH:mm}: {S(i, "text")}", due < _d.Clock() ? "The due time is in the past." : "", "");
    }

    // ------------------------------------------------------------------ confirm / cancel

    public AssistantAction? Find(long id) => _d.Store.All<AssistantAction>().FirstOrDefault(a => a.Id == id);

    public AssistantAction Cancel(AssistantAction a)
    {
        if (a.Status != AssistantActionStatus.Proposed) return a;
        a.Status = AssistantActionStatus.Cancelled;
        a.DecidedBy = _d.User; a.DecidedAt = _d.Clock();
        a.Result = "Cancelled by the user.";
        return _d.Store.Update(a, $"Assistant action #{a.Id} {AssistantActionKinds.Label(a.Kind)} cancelled by {_d.User}");
    }

    /// <summary>The user pressed CONFIRM: performs the write (re-checked now) and records the outcome.</summary>
    public AssistantAction Execute(AssistantAction a)
    {
        if (a.Status != AssistantActionStatus.Proposed) throw new InvalidOperationException($"Action #{a.Id} is already {a.Status}.");
        var input = JsonNode.Parse(a.InputJson) as JsonObject ?? new JsonObject();
        string result, reference = "";
        var ok = true;
        try
        {
            switch (a.Kind)
            {
                case AssistantActionKinds.DraftLedgerClaim:
                {
                    var line = ClaimFrom(input);
                    var check = _d.Project.Workflow.AddClaim(line, AssistantTools.S(input, "over_reason"));
                    ok = check.CanPost;
                    result = ok ? $"Claim line #{line.Id} posted: {line.Room} {line.Stage} {line.Item} {line.Qty:0.##}. {check.Message}" : "Not posted: " + check.Message;
                    reference = ok ? "Ledger|" + line.Room : "";
                    break;
                }
                case AssistantActionKinds.DraftInvoiceRevision:
                {
                    var sub = S(input, "subcontractor"); var no = AssistantTools.I(input, "invoice_no") ?? 0; var contract = AssistantTools.S(input, "contract_no");
                    var last = _d.Project.Snapshot.SubInvoices.Where(x => x.Subcontractor.Contains(sub, StringComparison.OrdinalIgnoreCase) && x.InvoiceNo == no
                                                                          && (contract is null || string.Equals(x.ContractNo, contract, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(x => x.Revision).LastOrDefault() ?? throw new InvalidOperationException($"Invoice {sub} INV-{no:00} not found.");
                    var next = Invoicing.InvoiceWorkflow.NextRevision(_d.Project.Snapshot.SubInvoices, last.ContractNo, last.Subcontractor, no);
                    var build = _d.Project.Workflow.BuildInvoice(last.ContractNo, last.Subcontractor, no, next);
                    if (S(input, "note").Length > 0) build.Header.Notes = S(input, "note");
                    var saved = _d.Project.Workflow.SaveInvoice(build);
                    result = $"{saved.Title} saved as DRAFT: SAR {build.Totals.CurrGross:N2} this period.";
                    reference = $"Invoices|{saved.ContractNo}|{saved.Subcontractor}|{saved.InvoiceNo}";
                    break;
                }
                case AssistantActionKinds.DraftRejectionReplyEmail:
                {
                    var folder = Path.Combine(_d.DraftsPath(), "Drafts");
                    Directory.CreateDirectory(folder);
                    var name = $"{_d.Clock():yyyyMMdd_HHmm}_{Safe(S(input, "subject"))}.eml";
                    var path = Path.Combine(folder, name);
                    File.WriteAllText(path, Eml(S(input, "to"), S(input, "subject"), S(input, "body")), new UTF8Encoding(false));
                    result = $"E-mail draft saved: {path}";
                    reference = "file|" + path;
                    break;
                }
                case AssistantActionKinds.DraftVariation:
                {
                    var store = _d.TryVariations() ?? throw new InvalidOperationException("The variations register is not available.");
                    var v = store.Create(new Variation
                    {
                        Type = Up(S(input, "type")), Title = S(input, "title"), Description = S(input, "description"), ConsultantRef = S(input, "consultant_ref"),
                        Building = AssistantTools.S(input, "building") is { } b ? Up(b) : "", Date = _d.Clock().Date, Status = VariationStatus.Draft,
                        StatusChangedAt = _d.Clock(), CreatedBy = _d.User, Notes = "Drafted with the assistant",
                    });
                    var lines = (input["lines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select((l, n) => new VariationLine
                    {
                        VariationId = v.Id, Order = n + 1, Kind = Up(S(l, "kind")), ItemCode = S(l, "item_code"), Description = S(l, "description"), Unit = S(l, "unit"),
                        Qty = AssistantTools.D(l, "qty") ?? 0, Rate = AssistantTools.D(l, "rate") ?? 0,
                    }).ToList();
                    if (lines.Count > 0) store.Save(v, lines);
                    result = $"{v.Number} '{v.Title}' created as DRAFT with {lines.Count} lines.";
                    reference = "Variations|" + v.Number;
                    break;
                }
                case AssistantActionKinds.CreateReminder:
                {
                    var due = ParseDue(S(input, "due"), _d.Clock()) ?? _d.Clock().Date.AddDays(1).AddHours(8);
                    var r = _d.Store.Insert(new AssistantReminder
                    {
                        Owner = _d.User, Due = due, Text = S(input, "text"), TargetModule = S(input, "target_module"), TargetKey = S(input, "target_key"),
                        Source = "ASSISTANT", CreatedAt = _d.Clock(),
                    }, $"Reminder for {_d.User} on {due:yyyy-MM-dd HH:mm}");
                    result = $"Reminder #{r.Id} set for {due:dd MMM yyyy HH:mm}.";
                    reference = r.TargetModule.Length > 0 ? $"{r.TargetModule}|{r.TargetKey}" : "";
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown action {a.Kind}.");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            result = "Failed: " + ex.Message;
        }
        a.Status = ok ? AssistantActionStatus.Executed : AssistantActionStatus.Failed;
        a.DecidedBy = _d.User; a.DecidedAt = _d.Clock();
        a.Result = result; a.ResultRef = reference;
        var updated = _d.Store.Update(a, $"Assistant action #{a.Id} {AssistantActionKinds.Label(a.Kind)} confirmed by {_d.User}: {result}");
        try { _d.Project.Store.LogEvent("ASSISTANT", $"{_d.User} confirmed {AssistantActionKinds.Label(a.Kind)} (#{a.Id}): {result}"); } catch (Exception) { /* audit row in the assistant table is kept either way */ }
        return updated;
    }

    private static string Safe(string s)
    {
        var t = new string((s ?? "draft").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()).Trim('_');
        return t.Length == 0 ? "draft" : t.Length > 60 ? t[..60] : t;
    }

    /// <summary>An unsent e-mail (Outlook opens it as a draft: X-Unsent: 1). UTF-8, Arabic safe.</summary>
    public static string Eml(string to, string subject, string body)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder();
        if (to.Length > 0) sb.Append("To: ").Append(to.Replace("\r", "").Replace("\n", "")).Append("\r\n");
        sb.Append("Subject: =?utf-8?B?").Append(B64(subject.Replace("\r", " ").Replace("\n", " "))).Append("?=\r\n");
        sb.Append("X-Unsent: 1\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        var b = B64(body.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        for (var i = 0; i < b.Length; i += 76) sb.Append(b, i, Math.Min(76, b.Length - i)).Append("\r\n");
        return sb.ToString();
    }
}
