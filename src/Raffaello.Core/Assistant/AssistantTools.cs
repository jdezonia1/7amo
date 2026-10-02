using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raffaello.Core.AconexWeb;
using Raffaello.Core.Domain;
using Raffaello.Core.Invoicing;
using Raffaello.Core.Ledger;
using Raffaello.Core.Materials;
using Raffaello.Core.Reports;
using Raffaello.Core.Rules;

namespace Raffaello.Core.Assistant;

/// <summary>Result of one tool call: the text that goes back to the model, the records it cited, and a proposed action (if any).</summary>
public sealed class ToolOutcome
{
    public string Content { get; init; } = "";
    public bool IsError { get; init; }
    public List<Citation> Citations { get; init; } = new();
    public AssistantAction? Proposed { get; init; }
}

/// <summary>
/// The assistant's tools over the app's data. Read tools never write; action tools only PROPOSE (an <see cref="AssistantAction"/>
/// the user confirms in the chat). Inputs are validated against the schema before anything runs (eager input streaming hands the
/// client unvalidated input).
/// </summary>
public sealed class AssistantTools
{
    public const string SearchDocuments = "search_documents";
    public const string QueryLedger = "query_ledger";
    public const string GetRoom = "get_room";
    public const string GetInvoice = "get_invoice";
    public const string ListNeedsToday = "list_needs_today";
    public const string GetContractTerms = "get_contract_terms";
    public const string FindDn = "find_dn";
    public const string ListAnomalies = "list_anomalies";
    public const string GetReport = "get_report";
    public const string ExplainRule = "explain_rule";

    public static readonly string[] ReadTools = { SearchDocuments, QueryLedger, GetRoom, GetInvoice, ListNeedsToday, GetContractTerms, FindDn, ListAnomalies, GetReport, ExplainRule };
    public static readonly string[] ActionTools = AssistantActionKinds.All;

    private readonly AssistantData _d;
    private readonly AssistantActions _actions;

    public AssistantTools(AssistantData data)
    {
        _d = data;
        _actions = new AssistantActions(data);
    }

    public AssistantActions Actions => _actions;

    // ------------------------------------------------------------------ definitions (fixed order: cached prefix)

    private static JsonObject Str(string desc, params string[] values)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = desc };
        if (values.Length > 0) o["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return o;
    }
    private static JsonObject Num(string desc) => new() { ["type"] = "number", ["description"] = desc };
    private static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };

    private static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["additionalProperties"] = false,
        },
    };

    private static readonly string[] StageEnum = { "1ST FIX", "2ND FIX", "FINAL FIX" };

    /// <summary>The tool definitions sent with every request (same text and order every time).</summary>
    public static JsonArray Definitions()
    {
        var a = new JsonArray
        {
            Tool(SearchDocuments,
                "Search the project's stored documents and records (document archive when available, attachments, Aconex documents, variation documents, contract item texts, invoices, DNs, POs, rooms) by words or numbers. Call it when the user asks where something is written, looks for a document, or names a reference you cannot place.",
                new JsonObject { ["query"] = Str("Words or numbers to look for, e.g. 'gas meter', 'WIR-EL-0123', 'MIR-EL-000169'."), ["limit"] = Int("Maximum hits (default 15).") },
                "query"),
            Tool(QueryLedger,
                "Room ledger totals: PROJECT QTY, claimed by all subcontractors, remaining and OVER keys, grouped by stage (stages are never added together) and optionally by room, item, subcontractor or invoice. Call it for any question about quantities claimed or remaining, e.g. 'remaining in P2-106 2nd fix light' or 'what has ROOTS claimed on L3'.",
                new JsonObject
                {
                    ["building"] = Str("BRANDED or HOTEL (omit for the building chosen in the app).", Buildings.Branded, Buildings.Hotel),
                    ["room"] = Str("Room code, e.g. P2-106. A trailing * matches a prefix (P2-1*)."),
                    ["floor"] = Str("Floor / level as written in the ledger, e.g. 2, L02."),
                    ["stage"] = Str("Stage.", StageEnum),
                    ["item"] = Str("System / item, e.g. LIGHT, POWER, DATA, GRMS, AV, EMERGENCY LIGHT, GAS."),
                    ["subcontractor"] = Str("Subcontractor name or part of it."),
                    ["invoice_no"] = Int("Only claims of this invoice number."),
                    ["group_by"] = Str("Extra grouping inside each stage.", "room", "item", "subcontractor", "invoice", "none"),
                    ["limit"] = Int("Maximum groups returned (default 40)."),
                }),
            Tool(GetRoom,
                "Everything about one room: building, level, type, area type, high-area note, the balance per stage x item (PROJECT QTY, claimed, remaining, % used, per subcontractor), recent claim lines and pending >4.5 m / 15 m checks. Call it when the user names a room.",
                new JsonObject { ["room"] = Str("Room code, e.g. P2-106.") },
                "room"),
            Tool(GetInvoice,
                "A subcontractor / supplier invoice with all its revisions: status, Aconex workflow no. and latest workflow step, rejection reasons, totals (gross this period, cumulative, retention, net incl. VAT), package. Call it for questions about an invoice or its approval.",
                new JsonObject
                {
                    ["subcontractor"] = Str("Subcontractor or supplier (part of the name is enough)."),
                    ["invoice_no"] = Int("Invoice number, e.g. 3 for INV-03."),
                    ["contract_no"] = Str("Contract no. when the subcontractor has several."),
                }),
            Tool(ListNeedsToday,
                "The 'Needs you today' queue (over-cap claims, holds, pending checks, invoices rejected / in Aconex, Aconex overdue steps, DNs without MIR, VOs ageing) plus reminders that are due. Call it for 'what should I do today' style questions.",
                new JsonObject { ["category"] = Str("Only items of this category, e.g. OVER, CERTIFY, MATERIAL, ACONEX, INVOICE, VO, CHECK."), ["limit"] = Int("Maximum items (default 20).") }),
            Tool(GetContractTerms,
                "A subcontract: value, retention, advance, signed date, status, its schedule items (rates, units, stage %, conduit, wall / ceiling, height band) and the items matching a description. Call it for contract, rate or payment-term questions.",
                new JsonObject
                {
                    ["contract_no"] = Str("Contract no."),
                    ["subcontractor"] = Str("Subcontractor (when the contract no. is not known)."),
                    ["item_query"] = Str("Words to find schedule items, e.g. 'data outlet 2nd fix'."),
                    ["limit"] = Int("Maximum items (default 25)."),
                }),
            Tool(FindDn,
                "Delivery note lookup: which PO, which supplier invoice (no., revision, status, Aconex), which MIR, quantities and 3-way match status. Call it for 'where is DN ...' or questions about a delivery, PO or drum / batch number.",
                new JsonObject
                {
                    ["query"] = Str("DN no., PO no., order no. or batch / drum no."),
                    ["supplier"] = Str("Supplier (part of the name)."),
                }),
            Tool(ListAnomalies,
                "Unusual claims: statements copied from an earlier one, keys over PROJECT QTY, the same room / stage / item claimed by two subcontractors, sudden jumps against the previous invoice, checks pending for long. Call it when the user asks what looks wrong or suspicious.",
                new JsonObject { ["limit"] = Int("Maximum items (default 25).") }),
            Tool(GetReport,
                "Rows of one of the management reports: WEEKLY (progress), SCORECARDS (subcontractors), CASHFLOW (claimed vs certified), MATERIALS (PO delivered, DNs without MIR), VO (variation register), INVOICES (invoice / Aconex status board).",
                new JsonObject
                {
                    ["report"] = Str("Report.", "WEEKLY", "SCORECARDS", "CASHFLOW", "MATERIALS", "VO", "INVOICES"),
                    ["building"] = Str("BRANDED or HOTEL (omit for all).", Buildings.Branded, Buildings.Hotel),
                    ["max_rows"] = Int("Rows per sheet (default 40)."),
                },
                "report"),
            Tool(ExplainRule,
                "Explains a house rule of this project and why a line was flagged: stages never summed, OVER, CHECK (claimed above WIR), EMT rework, PROJECT QTY cap, SITE % vs WIR %, 15 m route length, >4.5 m height, cumulative invoices, DATA RACK, BOQ code choice, invoice revisions, DN-line lock, point counting.",
                new JsonObject { ["topic"] = Str("The rule or flag, e.g. '15 m', 'OVER', 'cumulative', 'twin socket'.") },
                "topic"),

            // ---- actions: drafts only, the user must press CONFIRM in the chat
            Tool(AssistantActionKinds.DraftLedgerClaim,
                "Prepare a claim line for the room ledger (it is NOT posted: the user sees a card and must press CONFIRM). Use it when the user asks to enter / post / add a claim. Check the remaining quantity first with query_ledger or get_room; a claim above the remaining needs over_reason.",
                new JsonObject
                {
                    ["subcontractor"] = Str("Subcontractor."), ["invoice_no"] = Int("Invoice number the claim belongs to."),
                    ["building"] = Str("BRANDED or HOTEL.", Buildings.Branded, Buildings.Hotel),
                    ["room"] = Str("Room code."), ["floor"] = Str("Floor / level."), ["stage"] = Str("Stage.", StageEnum), ["item"] = Str("System / item."),
                    ["qty"] = Num("Quantity (points / metres)."), ["site_pct"] = Num("SITE % as a fraction 0-1 (default 1)."), ["wir_pct"] = Num("WIR % as a fraction 0-1 (default 1)."),
                    ["wir_no"] = Str("WIR number."), ["qty_above_4_5m"] = Num("Quantity claimed above 4.5 m (opens a height check)."),
                    ["length_claimed_qty"] = Num("Quantity claimed with the 15 m rule (opens a length check)."),
                    ["over_reason"] = Str("Reason, when the claim exceeds the remaining quantity."), ["notes"] = Str("Notes."),
                },
                "subcontractor", "invoice_no", "room", "stage", "item", "qty"),
            Tool(AssistantActionKinds.DraftInvoiceRevision,
                "Prepare the next revision of a subcontractor invoice from the ledger (after a rejection). Nothing is saved until the user presses CONFIRM.",
                new JsonObject { ["contract_no"] = Str("Contract no."), ["subcontractor"] = Str("Subcontractor."), ["invoice_no"] = Int("Invoice number."), ["note"] = Str("Why (e.g. the rejection comments being fixed).") },
                "subcontractor", "invoice_no"),
            Tool(AssistantActionKinds.DraftRejectionReplyEmail,
                "Prepare a reply e-mail (for example to head office or a subcontractor about a rejected invoice). You write the subject and body; on CONFIRM the draft is saved as an .eml file the user can open in Outlook - nothing is sent.",
                new JsonObject
                {
                    ["to"] = Str("Recipient address (optional)."), ["subject"] = Str("Subject."), ["body"] = Str("Plain-text body, ready to send."),
                    ["subcontractor"] = Str("Related subcontractor (optional)."), ["invoice_no"] = Int("Related invoice (optional)."),
                },
                "subject", "body"),
            Tool(AssistantActionKinds.DraftVariation,
                "Prepare a new variation / EI / SI in the register (status DRAFT) with optional lines. Created only after the user presses CONFIRM.",
                new JsonObject
                {
                    ["type"] = Str("Register type.", "VO", "EI", "SI"), ["title"] = Str("Title."), ["description"] = Str("Description / scope."),
                    ["consultant_ref"] = Str("Consultant reference."), ["building"] = Str("BRANDED or HOTEL.", Buildings.Branded, Buildings.Hotel),
                    ["lines"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Lines (optional).",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["kind"] = Str("Line kind.", "OMISSION", "ADDITION", "NEW ITEM"), ["item_code"] = Str("Contract / BOQ item."),
                                ["description"] = Str("Description."), ["unit"] = Str("Unit."), ["qty"] = Num("Quantity."), ["rate"] = Num("Rate (SAR)."),
                            },
                            ["required"] = new JsonArray("kind", "description", "qty"),
                            ["additionalProperties"] = false,
                        },
                    },
                },
                "type", "title"),
            Tool(AssistantActionKinds.CreateReminder,
                "Create a reminder for the user (shows in Needs-today and the morning brief when due). Created only after the user presses CONFIRM.",
                new JsonObject
                {
                    ["text"] = Str("What to remember."), ["due"] = Str("Due date or date-time, ISO format (yyyy-MM-dd or yyyy-MM-ddTHH:mm)."),
                    ["target_module"] = Str("Screen to open (Ledger, Invoices, Aconex, Materials, Variations ...)."), ["target_key"] = Str("Record on that screen (room, contract|sub|inv ...)."),
                },
                "text", "due"),
        };
        return a;
    }

    // ------------------------------------------------------------------ validation

    /// <summary>Checks an input against a tool's schema (required fields, types, enums). Returns the problems.</summary>
    public static List<string> Validate(string toolName, JsonObject input)
    {
        var def = Definitions().OfType<JsonObject>().FirstOrDefault(t => (string?)t["name"] == toolName);
        if (def is null) return new() { $"unknown tool {toolName}" };
        return ValidateObject(def["input_schema"]!.AsObject(), input, "");
    }

    private static List<string> ValidateObject(JsonObject schema, JsonObject input, string path)
    {
        var errors = new List<string>();
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        foreach (var r in (schema["required"] as JsonArray ?? new JsonArray()).Select(n => (string?)n))
            if (r != null && (!input.TryGetPropertyValue(r, out var v) || v is null)) errors.Add($"{path}{r} is required");
        foreach (var (name, value) in input)
        {
            if (props[name] is not JsonObject p) { errors.Add($"{path}{name} is not a known field"); continue; }
            if (value is null) continue;
            var type = (string?)p["type"];
            var kind = value.GetValueKind();
            var ok = type switch
            {
                "string" => kind == JsonValueKind.String,
                "number" => kind == JsonValueKind.Number,
                "integer" => kind == JsonValueKind.Number && Number(value) is double d && Math.Abs(d - Math.Round(d)) < 1e-9,
                "array" => kind == JsonValueKind.Array,
                "object" => kind == JsonValueKind.Object,
                _ => true,
            };
            if (!ok) { errors.Add($"{path}{name} must be {type}"); continue; }
            if (p["enum"] is JsonArray en && kind == JsonValueKind.String && !en.Select(e => (string?)e).Contains((string?)value, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{path}{name} must be one of {string.Join(", ", en.Select(e => (string?)e))}");
            if (type == "array" && p["items"] is JsonObject items && (string?)items["type"] == "object")
            {
                var i = 0;
                foreach (var el in value.AsArray())
                {
                    if (el is JsonObject eo) errors.AddRange(ValidateObject(items, eo, $"{path}{name}[{i}]."));
                    else errors.Add($"{path}{name}[{i}] must be an object");
                    i++;
                }
            }
        }
        return errors;
    }

    // ------------------------------------------------------------------ execution

    /// <summary>Runs a read tool, or proposes an action. Never throws: failures come back as an error result for the model.</summary>
    public ToolOutcome Execute(string name, JsonObject input, string toolUseId, long conversationId)
    {
        var problems = Validate(name, input);
        if (problems.Count > 0) return Error($"Invalid input for {name}: {string.Join("; ", problems)}. Fix the input and call the tool again.");
        try
        {
            if (AssistantActionKinds.All.Contains(name)) return _actions.Propose(name, input, toolUseId, conversationId);
            if (!_d.Settings.AllowReadProjectData && name != ExplainRule)
                return Error("The user has switched off 'Allow assistant to read project data' in Settings. Answer from the house rules only, or ask the user to switch it on.");
            return name switch
            {
                SearchDocuments => Search(S(input, "query")!, I(input, "limit") ?? 15),
                QueryLedger => Ledger(input),
                GetRoom => Room(S(input, "room")!),
                GetInvoice => Invoice(S(input, "subcontractor"), I(input, "invoice_no"), S(input, "contract_no")),
                ListNeedsToday => NeedsToday(S(input, "category"), I(input, "limit") ?? 20),
                GetContractTerms => ContractTerms(S(input, "contract_no"), S(input, "subcontractor"), S(input, "item_query"), I(input, "limit") ?? 25),
                FindDn => Dn(S(input, "query"), S(input, "supplier")),
                ListAnomalies => Anomalies(I(input, "limit") ?? 25),
                GetReport => Report(S(input, "report")!, S(input, "building"), I(input, "max_rows") ?? 40),
                ExplainRule => Rule(S(input, "topic")!),
                _ => Error($"Unknown tool {name}."),
            };
        }
        catch (Exception ex)
        {
            return Error($"{name} failed: {ex.Message}");
        }
    }

    internal static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim().Length > 0 ? v.GetValue<string>().Trim() : null;
    internal static int? I(JsonObject o, string k) => Number(o[k]) is double d ? (int)Math.Round(d) : null;
    internal static double? D(JsonObject o, string k) => Number(o[k]);

    /// <summary>A JSON number whatever backs the node (parsed element or a CLR value).</summary>
    internal static double? Number(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static ToolOutcome Error(string msg) => new() { Content = msg, IsError = true };

    private static ToolOutcome Ok(JsonObject result, List<Citation> cites)
    {
        if (cites.Count > 0) result["cite_tokens"] = new JsonArray(cites.Select(c => (JsonNode)JsonValue.Create(c.Token)!).Distinct().Take(40).ToArray());
        return new ToolOutcome { Content = result.ToJsonString(), Citations = cites };
    }

    private static double R2(double v) => Math.Round(v, 2);
    private static bool Like(string? value, string? query) => query is null || (value ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
    private static string Up(string? s) => (s ?? "").Trim().ToUpperInvariant();

    private string? Building(string? b) => b is { Length: > 0 } ? Up(b) : null;

    private static bool RoomMatch(string room, string? pattern)
    {
        if (pattern is null) return true;
        var p = Up(pattern);
        return p.EndsWith('*') ? Up(room).StartsWith(p.TrimEnd('*'), StringComparison.Ordinal) : Up(room) == p;
    }

    private static bool ItemMatch(string item, string? q) => q is null || Up(item) == Up(q) || Up(item).Contains(Up(q), StringComparison.Ordinal);

    // ---- query_ledger

    private ToolOutcome Ledger(JsonObject input)
    {
        var s = _d.Project.Snapshot;
        var building = Building(S(input, "building"));
        var room = S(input, "room");
        var floor = S(input, "floor");
        var stage = S(input, "stage") is { } st ? Stages.Normalize(st) : null;
        var item = S(input, "item");
        var sub = S(input, "subcontractor");
        var inv = I(input, "invoice_no");
        var groupBy = S(input, "group_by") ?? "none";
        var limit = Math.Clamp(I(input, "limit") ?? 40, 1, 200);

        bool KeyOk(string b, string r, string stg, string it) =>
            (building is null || b.Length == 0 || Up(b) == building) && RoomMatch(r, room) && (stage is null || Up(stg) == stage) && ItemMatch(it, item);

        var roomFloor = s.Rooms.GroupBy(r => Up(r.Code)).ToDictionary(g => g.Key, g => g.First());
        bool FloorOk(string r, string claimFloor) => floor is null || Up(claimFloor) == Up(floor)
            || (roomFloor.TryGetValue(Up(r), out var rr) && (Up(rr.Level) == Up(floor) || rr.Floor.ToString(CultureInfo.InvariantCulture) == floor.Trim()));

        var caps = s.RoomQtys.Where(q => KeyOk(q.Building, q.Room, q.Stage, q.Item) && FloorOk(q.Room, "")).ToList();
        var all = LedgerRules.Effective(s.Claims).Where(c => !c.Rework && KeyOk(c.Building, c.Room, c.Stage, c.Item) && FloorOk(c.Room, c.Floor)).ToList();
        var mine = all.Where(c => Like(c.Subcontractor, sub) && (inv is null || c.InvoiceNo == inv)).ToList();
        var balances = LedgerRules.Balances(caps, all);

        var result = new JsonObject
        {
            ["filter"] = new JsonObject { ["building"] = building ?? "ALL", ["room"] = room, ["floor"] = floor, ["stage"] = stage, ["item"] = item, ["subcontractor"] = sub, ["invoice_no"] = inv },
            ["note"] = "Stages measure the same points and are never added together. Remaining = PROJECT QTY - claims of ALL subcontractors.",
        };
        var stages = new JsonArray();
        var cites = new List<Citation>();
        foreach (var stg in caps.Select(c => Up(c.Stage)).Concat(all.Select(c => Up(c.Stage))).Distinct().OrderBy(x => Array.IndexOf(Stages.All, x) is var i && i < 0 ? 9 : i))
        {
            var capS = caps.Where(c => Up(c.Stage) == stg).Sum(c => c.Qty);
            var allS = all.Where(c => Up(c.Stage) == stg).Sum(c => c.Qty);
            var mineS = mine.Where(c => Up(c.Stage) == stg).ToList();
            var keys = balances.Values.Where(b => Up(b.Stage) == stg).ToList();
            var o = new JsonObject
            {
                ["stage"] = stg,
                ["project_qty"] = R2(capS),
                ["claimed_all_subcontractors"] = R2(allS),
                ["remaining"] = R2(capS - allS),
                ["keys"] = keys.Count,
                ["over_cap_keys"] = keys.Count(b => b.HasCap && b.IsOver),
                ["keys_without_project_qty"] = keys.Count(b => !b.HasCap && b.Claimed > 0),
            };
            if (sub != null || inv != null) { o["claimed_matching_filter"] = R2(mineS.Sum(c => c.Qty)); o["lines_matching_filter"] = mineS.Count; }
            if (groupBy != "none")
            {
                var groups = new JsonArray();
                Func<ClaimLine, string> keyOf = groupBy switch
                {
                    "room" => c => Up(c.Room),
                    "item" => c => Up(c.Item),
                    "subcontractor" => c => Up(c.Subcontractor),
                    _ => c => "INV " + c.InvoiceNo.ToString("00", CultureInfo.InvariantCulture),
                };
                var claimGroups = mineS.GroupBy(keyOf).ToDictionary(x => x.Key, x => x.ToList());
                var capKeys = groupBy switch
                {
                    "room" => caps.Where(c => Up(c.Stage) == stg).Select(c => Up(c.Room)),
                    "item" => caps.Where(c => Up(c.Stage) == stg).Select(c => Up(c.Item)),
                    _ => Enumerable.Empty<string>(),
                };
                foreach (var key in claimGroups.Keys.Union(capKeys).OrderByDescending(k => claimGroups.TryGetValue(k, out var l) ? l.Sum(c => c.Qty) : 0).ThenBy(k => k).Take(limit))
                {
                    var grp = claimGroups.GetValueOrDefault(key) ?? new List<ClaimLine>();
                    var go = new JsonObject { ["group"] = key, ["claimed"] = R2(grp.Sum(c => c.Qty)), ["lines"] = grp.Count };
                    if (groupBy is "room" or "item")
                    {
                        var capG = caps.Where(c => Up(c.Stage) == stg && (groupBy == "room" ? Up(c.Room) == key : Up(c.Item) == key)).Sum(c => c.Qty);
                        var allG = all.Where(c => Up(c.Stage) == stg && (groupBy == "room" ? Up(c.Room) == key : Up(c.Item) == key)).Sum(c => c.Qty);
                        go["project_qty"] = R2(capG); go["remaining"] = R2(capG - allG);
                    }
                    if (groupBy == "room") { var c = Citation.Room(key); cites.Add(c); go["cite"] = c.Token; }
                    groups.Add(go);
                }
                o["groups"] = groups;
            }
            stages.Add(o);
        }
        result["by_stage"] = stages;
        if (room is { } rm && !rm.EndsWith('*')) cites.Insert(0, Citation.Room(Up(rm)));
        foreach (var r in mine.Select(c => Up(c.Room)).Distinct().Take(10)) cites.Add(Citation.Room(r));
        if (stages.Count == 0) result["message"] = "No PROJECT QTY and no claims match this filter. Check the room code (e.g. P2-106), the stage and the item.";
        return Ok(result, cites.Distinct().ToList());
    }

    // ---- get_room

    private ToolOutcome Room(string roomCode)
    {
        var s = _d.Project.Snapshot;
        var code = Up(roomCode);
        var room = s.Rooms.FirstOrDefault(r => Up(r.Code) == code);
        var caps = s.RoomQtys.Where(q => Up(q.Room) == code).ToList();
        var claims = s.Claims.Where(c => Up(c.Room) == code).ToList();
        if (room is null && caps.Count == 0 && claims.Count == 0)
        {
            var close = s.Rooms.Select(r => r.Code).Concat(s.RoomQtys.Select(q => q.Room)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => Up(c).Contains(code.Replace("-", ""), StringComparison.Ordinal) || Up(c).Replace("-", "").Contains(code.Replace("-", ""), StringComparison.Ordinal)).Take(10).ToList();
            return Error($"Room {code} is not in the room list or the ledger.{(close.Count > 0 ? " Similar: " + string.Join(", ", close) : "")}");
        }
        var bal = LedgerRules.Balances(caps, claims).Values.OrderBy(b => Array.IndexOf(Stages.All, Up(b.Stage))).ThenBy(b => b.Item);
        var res = new JsonObject
        {
            ["room"] = code,
            ["building"] = room?.Building ?? claims.FirstOrDefault()?.Building ?? caps.FirstOrDefault()?.Building,
            ["level"] = room?.Level, ["type"] = room?.RoomType, ["area_type"] = room?.AreaType, ["unit"] = room?.Unit,
            ["high_area_note"] = room?.HighAreaNote,
            ["balances"] = new JsonArray(bal.Select(b => (JsonNode)new JsonObject
            {
                ["stage"] = b.Stage, ["item"] = b.Item, ["project_qty"] = R2(b.ProjectQty), ["claimed"] = R2(b.Claimed), ["remaining"] = R2(b.Remaining),
                ["used_pct"] = R2(b.UsedPct * 100), ["over"] = b.IsOver && b.HasCap,
                ["by_subcontractor"] = new JsonObject(b.BySubcontractor.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)R2(kv.Value)))),
            }).ToArray()),
            ["recent_claims"] = new JsonArray(claims.OrderByDescending(c => c.EnteredAt).ThenByDescending(c => c.Id).Take(15).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["subcontractor"] = c.Subcontractor, ["invoice_no"] = c.InvoiceNo, ["stage"] = c.Stage, ["item"] = c.Item, ["qty"] = R2(c.Qty),
                ["site_pct"] = R2(c.SitePct), ["wir_pct"] = R2(c.WirPct), ["wir_no"] = c.WirNo, ["over"] = c.IsOver, ["over_reason"] = c.OverReason,
                ["source"] = c.Source, ["entered"] = c.EnteredAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }).ToArray()),
            ["pending_checks"] = new JsonArray(claims.Where(c => HeightCheck.IsPending(c) || LengthCheck.IsPending(c)).Take(20).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["check"] = HeightCheck.IsPending(c) ? ">4.5 m height" : "15 m length", ["subcontractor"] = c.Subcontractor, ["stage"] = c.Stage, ["item"] = c.Item,
                ["qty"] = R2(c.Qty), ["above_4_5m"] = R2(c.QtyAbove45), ["length_claimed"] = R2(c.LengthClaimedQty),
            }).ToArray()),
        };
        var cites = new List<Citation> { Citation.Room(code) };
        foreach (var w in claims.Select(c => c.WirNo).Where(w => w.Length > 0).Distinct().Take(5)) cites.Add(Citation.Wir(w));
        return Ok(res, cites);
    }

    // ---- get_invoice

    private ToolOutcome Invoice(string? sub, int? no, string? contract)
    {
        var s = _d.Project.Snapshot;
        var revs = s.SubInvoices.Where(i => Like(i.Subcontractor, sub) && (no is null || i.InvoiceNo == no) && Like(i.ContractNo, contract))
            .OrderBy(i => i.Subcontractor).ThenBy(i => i.InvoiceNo).ThenBy(i => i.Revision).ToList();
        if (revs.Count == 0)
        {
            var subs = s.SubInvoices.Select(i => i.Subcontractor).Distinct().Take(20);
            return Error($"No invoice found for {sub ?? "any subcontractor"}{(no is null ? "" : $" INV-{no:00}")}. Invoices exist for: {string.Join(", ", subs)}.");
        }
        var aconex = _d.TryAconex();
        var latest = aconex?.LatestChecks() ?? new Dictionary<string, AconexWorkflowCheck>();
        var links = aconex?.ActiveLinks() ?? new List<AconexWorkflowLink>();
        var cites = new List<Citation>();
        var arr = new JsonArray();
        foreach (var g in revs.GroupBy(i => (i.ContractNo, i.Subcontractor, i.InvoiceNo)).Take(12))
        {
            var cite = Citation.Invoice(g.Key.ContractNo, g.Key.Subcontractor, g.Key.InvoiceNo);
            cites.Add(cite);
            var revArr = new JsonArray();
            foreach (var r in g)
            {
                var t = InvoiceTotals.Of(r, s.SubInvoiceLines.Where(l => l.SubInvoiceId == r.Id));
                var wf = links.Where(l => l.SubInvoiceId == r.Id).OrderByDescending(l => l.LinkedAt).FirstOrDefault()?.WorkflowNo ?? r.AconexWorkflowNo;
                var check = wf.Length > 0 ? latest.GetValueOrDefault(wf.Trim().ToUpperInvariant()) ?? latest.FirstOrDefault(kv => string.Equals(kv.Key, wf, StringComparison.OrdinalIgnoreCase)).Value : null;
                if (wf.Length > 0) cites.Add(Citation.Workflow(wf));
                revArr.Add(new JsonObject
                {
                    ["revision"] = r.Revision, ["status"] = r.Status, ["kind"] = r.Kind, ["locked"] = r.Locked,
                    ["created"] = r.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["submitted"] = r.SubmittedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["approved"] = r.ApprovedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["rejection_reason"] = r.RejectionReason,
                    ["aconex_workflow"] = wf,
                    ["aconex_status"] = check is null ? (wf.Length > 0 ? "not checked yet" : "no workflow") : $"{check.State} - step '{check.CurrentStep}' with {check.WithWhom}{(check.IsOverdue ? $", OVERDUE {check.DaysOverdue} days" : "")}{(check.Outcome.Length > 0 ? ", outcome " + check.Outcome : "")} (checked {check.CheckedAt:yyyy-MM-dd})",
                    ["gross_this_period_sar"] = R2(t.CurrGross), ["gross_cumulative_sar"] = R2(t.CumGross), ["retention_this_period_sar"] = R2(t.CurrRetention),
                    ["net_incl_vat_this_period_sar"] = R2(t.NetInclVatCurr), ["subcontract_value_sar"] = R2(t.SubcontractValue),
                    ["package"] = r.PackageFile.Length > 0 ? $"{Path.GetFileName(r.PackageFile)} ({r.PackageKind}, missing WIRs {r.PackageMissingWirs})" : "not built",
                });
            }
            arr.Add(new JsonObject { ["contract_no"] = g.Key.ContractNo, ["subcontractor"] = g.Key.Subcontractor, ["invoice_no"] = g.Key.InvoiceNo, ["cite"] = cite.Token, ["revisions"] = revArr });
        }
        return Ok(new JsonObject { ["invoices"] = arr, ["note"] = "Approved revisions are locked and become 'previous' for the next invoice." }, cites.Distinct().ToList());
    }

    // ---- list_needs_today

    private ToolOutcome NeedsToday(string? category, int limit)
    {
        var items = _d.Project.Queue.Where(q => category is null || Up(q.Category) == Up(category) || Up(q.Category).Contains(Up(category), StringComparison.Ordinal)).Take(Math.Clamp(limit, 1, 100)).ToList();
        var cites = items.Select(q => Citation.Queue(q.Title, q.Target.Module, q.Target.Key ?? "")).ToList();
        var now = _d.Clock();
        var reminders = _d.Store.Reminders(_d.User).Where(r => r.Due <= now.Date.AddDays(1)).Take(20).ToList();
        cites.AddRange(reminders.Select(r => Citation.Reminder(r.Id, r.Text)));
        return Ok(new JsonObject
        {
            ["count_total"] = _d.Project.Queue.Count,
            ["items"] = new JsonArray(items.Select(q => (JsonNode)new JsonObject
            {
                ["severity"] = q.Tag, ["category"] = q.Category, ["title"] = q.Title, ["detail"] = q.Detail, ["cite"] = Citation.Queue(q.Title, q.Target.Module, q.Target.Key ?? "").Token,
            }).ToArray()),
            ["reminders_due"] = new JsonArray(reminders.Select(r => (JsonNode)new JsonObject
            {
                ["text"] = r.Text, ["due"] = r.Due.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), ["overdue"] = r.Due < now, ["cite"] = Citation.Reminder(r.Id, r.Text).Token,
            }).ToArray()),
        }, cites);
    }

    // ---- get_contract_terms

    private ToolOutcome ContractTerms(string? contractNo, string? sub, string? itemQuery, int limit)
    {
        var s = _d.Project.Snapshot;
        var contracts = s.Contracts.Where(c => Like(c.ContractNo, contractNo) && Like(c.Subcontractor, sub)).ToList();
        var itemContracts = s.ContractItems.Select(i => i.ContractNo).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(no => Like(no, contractNo) && (sub is null || s.SubInvoices.Any(i => i.ContractNo == no && Like(i.Subcontractor, sub)) || contracts.Any(c => c.ContractNo == no))).ToList();
        if (contracts.Count == 0 && itemContracts.Count == 0)
            return Error($"No contract found for {contractNo ?? sub ?? "the request"}. Contracts on file: {string.Join(", ", s.Contracts.Select(c => c.ContractNo).Concat(s.ContractItems.Select(i => i.ContractNo)).Distinct().Take(20))}.");
        var cites = new List<Citation>();
        var res = new JsonObject();
        res["contracts"] = new JsonArray(contracts.Take(10).Select(c =>
        {
            cites.Add(Citation.Contract(c.ContractNo));
            return (JsonNode)new JsonObject
            {
                ["contract_no"] = c.ContractNo, ["subcontractor"] = c.Subcontractor, ["building"] = c.Building, ["scope"] = c.Scope, ["value_sar"] = R2(c.Value),
                ["retention_pct"] = R2(c.RetentionPct * 100), ["advance_pct"] = R2(c.AdvancePct * 100), ["signed"] = c.SignedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["status"] = c.Status,
                ["cite"] = Citation.Contract(c.ContractNo).Token,
            };
        }).ToArray());
        var schedules = new JsonArray();
        foreach (var no in itemContracts.Take(5))
        {
            cites.Add(Citation.Contract(no));
            var items = s.ContractItems.Where(i => i.ContractNo == no).ToList();
            var o = new JsonObject
            {
                ["contract_no"] = no, ["items"] = items.Count, ["sections"] = new JsonArray(items.Select(i => i.Section).Where(x => x.Length > 0).Distinct().Take(30).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["stage_payment_pcts"] = new JsonArray(items.Where(i => i.StagePct > 0).Select(i => R2(i.StagePct * 100)).Distinct().OrderBy(x => x).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                ["schedule_value_sar"] = R2(items.Sum(i => i.Qty * i.Rate)),
            };
            if (itemQuery != null)
            {
                var words = itemQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hits = items.Select(i => (i, score: words.Count(w => (i.Description + " " + i.Section + " " + i.FixStage + " " + i.Systems + " " + i.ConduitType + " " + i.Mount).Contains(w, StringComparison.OrdinalIgnoreCase))))
                    .Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.i.Order).Take(Math.Clamp(limit, 1, 100)).Select(x => x.i);
                o["matching_items"] = new JsonArray(hits.Select(i => (JsonNode)new JsonObject
                {
                    ["item_no"] = i.ItemNo, ["description"] = i.Description, ["unit"] = i.Unit, ["qty"] = R2(i.Qty), ["rate_sar"] = R2(i.Rate), ["stage"] = i.FixStage,
                    ["conduit"] = i.ConduitType, ["mount"] = i.Mount, ["height_band"] = i.HeightBand, ["stage_pct"] = R2(i.StagePct * 100), ["15m_rule"] = i.Is2ndFixPulling,
                }).ToArray());
            }
            schedules.Add(o);
        }
        res["schedules"] = schedules;
        return Ok(res, cites.Distinct().ToList());
    }

    // ---- find_dn

    private ToolOutcome Dn(string? query, string? supplier)
    {
        var mats = _d.TryMaterials();
        var cites = new List<Citation>();
        var arr = new JsonArray();
        if (mats != null)
        {
            foreach (var r in DnLookup.Search(_d.Project.Snapshot, mats, supplier, query).Take(25))
            {
                var c = Citation.Dn(r.Supplier, r.DnNo); cites.Add(c);
                if (r.PoNo.Length > 0) cites.Add(Citation.Po(r.PoNo));
                arr.Add(new JsonObject
                {
                    ["supplier"] = r.Supplier, ["dn_no"] = r.DnNo, ["dn_date"] = r.DnDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["po_no"] = r.PoNo,
                    ["lines"] = r.Lines, ["qty"] = R2(r.Qty), ["unit"] = r.Unit, ["invoice"] = r.Invoice, ["invoice_status"] = r.InvoiceStatus, ["aconex"] = r.AconexNo,
                    ["mir_no"] = r.MirNo, ["mir_status"] = r.MirStatus, ["match"] = r.Match, ["matched_on"] = r.MatchedOn, ["batches"] = r.Batches, ["cite"] = c.Token,
                });
            }
        }
        // demo / overview delivery notes (older table) as well
        var pos = _d.Project.Snapshot.PurchaseOrders.ToDictionary(p => p.Id);
        foreach (var dn in _d.Project.Snapshot.DeliveryNotes.Where(d => (query is null || Up(d.DnNo).Contains(Up(query).Replace(" ", ""), StringComparison.Ordinal)
                                                                         || (pos.TryGetValue(d.PoId, out var p0) && Up(p0.PoNo).Contains(Up(query), StringComparison.Ordinal)))
                                                                        && (supplier is null || (pos.TryGetValue(d.PoId, out var p1) && Like(p1.Supplier, supplier)))).Take(10))
        {
            if (arr.OfType<JsonObject>().Any(o => string.Equals((string?)o["dn_no"], dn.DnNo, StringComparison.OrdinalIgnoreCase))) continue;
            var po = pos.GetValueOrDefault(dn.PoId);
            var c = Citation.Dn(po?.Supplier ?? "", dn.DnNo); cites.Add(c);
            if (po != null) cites.Add(Citation.Po(po.PoNo));
            arr.Add(new JsonObject { ["supplier"] = po?.Supplier, ["dn_no"] = dn.DnNo, ["dn_date"] = dn.DnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["po_no"] = po?.PoNo, ["mir_no"] = dn.MirNo, ["cite"] = c.Token });
        }
        if (arr.Count == 0)
            return Error(mats is null ? "The materials module could not be read, and no delivery note matches." : $"No delivery note matches '{query}'{(supplier is null ? "" : $" for {supplier}")}. Try the PO number or a batch / drum number.");
        return Ok(new JsonObject { ["delivery_notes"] = arr }, cites.Distinct().ToList());
    }

    // ---- list_anomalies

    private ToolOutcome Anomalies(int limit)
    {
        var items = (_d.Anomalies?.Invoke() ?? BuiltInAnomalies()).Take(Math.Clamp(limit, 1, 100)).ToList();
        var cites = items.Where(i => i.Source != null).Select(i => i.Source!).Distinct().ToList();
        return Ok(new JsonObject
        {
            ["source"] = _d.Anomalies is null ? "built-in checks" : "anomaly module",
            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["severity"] = i.Severity, ["kind"] = i.Kind, ["title"] = i.Title, ["detail"] = i.Detail, ["cite"] = i.Source?.Token }).ToArray()),
        }, cites);
    }

    /// <summary>Claim checks that need no extra module: copied statements, over-cap keys, shared keys, jumps, old pending checks.</summary>
    public IEnumerable<AnomalyItem> BuiltInAnomalies()
    {
        var s = _d.Project.Snapshot;
        foreach (var c in InvoiceCopyDetector.Detect(s.Invoices, s.InvoiceLines))
            yield return new("OVER", "COPY", $"{c.Invoice.Subcontractor} {c.Invoice.InvoiceNo} copies {c.CopyOf.InvoiceNo}", $"{c.MatchingLines}/{c.TotalLines} lines identical", null);
        var bal = LedgerRules.Balances(s.RoomQtys, s.Claims).Values.ToList();
        foreach (var b in bal.Where(b => b.HasCap && b.IsOver).OrderByDescending(b => b.Claimed - b.ProjectQty).Take(30))
            yield return new("OVER", "OVER CAP", $"{b.Room} {b.Stage} {b.Item}: claimed {b.Claimed:0.##} > PROJECT QTY {b.ProjectQty:0.##}",
                string.Join(", ", b.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}")), Citation.Room(b.Room));
        foreach (var b in bal.Where(b => b.BySubcontractor.Count(kv => Math.Abs(kv.Value) > LedgerRules.Eps) > 1).Take(30))
            yield return new("CHECK", "TWO SUBCONTRACTORS", $"{b.Room} {b.Stage} {b.Item} claimed by {b.BySubcontractor.Count} subcontractors",
                string.Join(", ", b.BySubcontractor.Select(kv => $"{kv.Key} {kv.Value:0.##}")), Citation.Room(b.Room));
        // jump: an invoice's total claimed qty more than 3x the average of the same subcontractor's earlier invoices
        foreach (var g in LedgerRules.Effective(s.Claims).Where(c => !c.Rework).GroupBy(c => c.Subcontractor))
        {
            var per = g.GroupBy(c => c.InvoiceNo).OrderBy(x => x.Key).Select(x => (No: x.Key, Qty: x.Sum(c => c.Qty))).ToList();
            for (var i = 2; i < per.Count; i++)
            {
                var avg = per.Take(i).Average(p => p.Qty);
                if (avg > 0 && per[i].Qty > 3 * avg)
                    yield return new("CHECK", "JUMP", $"{g.Key} INV {per[i].No}: {per[i].Qty:0.#} points claimed", $"{per[i].Qty / avg:0.0}x the average of the earlier invoices ({avg:0.#})", null);
            }
        }
        var today = _d.Clock().Date;
        foreach (var c in s.Claims.Where(c => (HeightCheck.IsPending(c) || LengthCheck.IsPending(c)) && (today - c.EnteredAt.Date).TotalDays > 14).Take(20))
            yield return new("DUE", "OLD CHECK", $"{c.Room} {c.Stage} {c.Item} {c.Subcontractor}: check pending {(today - c.EnteredAt.Date).TotalDays:0} days",
                HeightCheck.IsPending(c) ? ">4.5 m height check" : "15 m length check", Citation.Room(c.Room));
    }

    // ---- get_report

    private ToolOutcome Report(string key, string? building, int maxRows)
    {
        key = Up(key);
        var def = ProjectReports.All.FirstOrDefault(r => r.Key == key);
        if (def is null) return Error($"Unknown report {key}. Reports: {string.Join(", ", ProjectReports.All.Select(r => r.Key))}.");
        var aconex = _d.TryAconex();
        var vstore = _d.TryVariations();
        var inputs = new ReportInputs
        {
            Project = _d.Project.Snapshot,
            Materials = _d.TryMaterials(),
            Variations = vstore?.Variations() ?? new(),
            VariationLines = vstore?.AllLines() ?? new(),
            InvoiceBoard = aconex is null ? new() : StatusBoard.Build(_d.Project.Snapshot.SubInvoices, aconex.ActiveLinks(), aconex.LatestChecks(), _d.Clock().Date, false),
            Building = Building(building),
            AsOf = _d.Clock().Date,
        };
        var sheets = ProjectReports.Build(key, inputs);
        var rows = Math.Clamp(maxRows, 1, 200);
        var arr = new JsonArray();
        foreach (var sh in sheets.Take(6))
        {
            arr.Add(new JsonObject
            {
                ["sheet"] = sh.Name, ["title"] = sh.Title, ["subtitle"] = sh.Subtitle,
                ["columns"] = new JsonArray(sh.Columns.Select(c => (JsonNode)JsonValue.Create(c.Header)!).ToArray()),
                ["rows"] = new JsonArray(sh.Rows.Take(rows).Select(r => (JsonNode)new JsonArray(r.Select(Cell).ToArray())).ToArray()),
                ["rows_total"] = sh.Rows.Count,
                ["total_row"] = sh.TotalRow is null ? null : new JsonArray(sh.TotalRow.Select(Cell).ToArray()),
            });
        }
        return Ok(new JsonObject { ["report"] = def.Name, ["sheets"] = arr }, new List<Citation> { Citation.Report(def.Key, def.Name) });
    }

    private static JsonNode? Cell(object? v) => v switch
    {
        null => null,
        double d => R2(d),
        float f => R2(f),
        decimal m => R2((double)m),
        int i => i,
        long l => l,
        bool b => b,
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    // ---- explain_rule

    private ToolOutcome Rule(string topic)
    {
        var hits = HouseRuleCatalog.Find(topic, _d.Project.Engine.Rules.Select(r => (r.Code, r.Title, r.Description))).Take(4).ToList();
        if (hits.Count == 0) hits = HouseRuleCatalog.Rules.Take(6).ToList();
        var cites = hits.Select(h => Citation.Rule(h.Code, h.Title)).ToList();
        return Ok(new JsonObject
        {
            ["rules"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject { ["code"] = h.Code, ["title"] = h.Title, ["rule"] = h.Text, ["cite"] = Citation.Rule(h.Code, h.Title).Token }).ToArray()),
        }, cites);
    }

    // ---- search_documents

    private ToolOutcome Search(string query, int limit)
    {
        limit = Math.Clamp(limit, 1, 50);
        var hits = new List<DocumentHit>();
        string source = "records";
        if (_d.Documents != null)
        {
            try { hits.AddRange(_d.Documents.Search(query, limit)); source = _d.Documents.Name + " + records"; }
            catch (Exception) { source = "records (archive not reachable)"; }
        }
        hits.AddRange(RecordSearch.Search(_d, query, limit));
        var list = hits.GroupBy(h => (h.Kind, h.Id)).Select(g => g.First()).Take(limit).ToList();
        var cites = list.Select(h => h.Kind switch
        {
            "room" => Citation.Room(h.Id),
            "invoice" => new Citation("invoice", h.Id, h.Title, "Invoices", h.Id),
            "dn" => Citation.Dn("", h.Id),
            "po" => Citation.Po(h.Id),
            "variation" => Citation.Variation(h.Id),
            "contract" => Citation.Contract(h.Id),
            "wir" => Citation.Wir(h.Id),
            _ => Citation.Document(h.Id, h.Title, h.Path, h.Page),
        }).ToList();
        return Ok(new JsonObject
        {
            ["searched"] = source,
            ["hits"] = new JsonArray(list.Select((h, i) => (JsonNode)new JsonObject
            {
                ["kind"] = h.Kind, ["title"] = h.Title, ["page"] = h.Page > 0 ? h.Page : null, ["snippet"] = h.Snippet, ["cite"] = cites[i].Token,
            }).ToArray()),
        }, cites);
    }
}

/// <summary>Search over the records in the data file (always available, also offline).</summary>
public static class RecordSearch
{
    public static IEnumerable<DocumentHit> Search(AssistantData d, string query, int limit)
    {
        var words = query.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) yield break;
        bool Hit(params string?[] fields)
        {
            var text = string.Join(" ", fields.Where(f => !string.IsNullOrEmpty(f)));
            var compact = new string(text.Where(char.IsLetterOrDigit).ToArray());
            return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase) || compact.Contains(new string(w.Where(char.IsLetterOrDigit).ToArray()), StringComparison.OrdinalIgnoreCase) && w.Any(char.IsLetterOrDigit));
        }
        static string Snip(string text, string word)
        {
            var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return text.Length > 160 ? text[..160] + "..." : text;
            var a = Math.Max(0, i - 70);
            var len = Math.Min(text.Length - a, 200);
            return (a > 0 ? "..." : "") + text.Substring(a, len).Replace('\n', ' ') + (a + len < text.Length ? "..." : "");
        }
        var s = d.Project.Snapshot;
        var n = 0;
        foreach (var a in s.Attachments.Where(a => Hit(a.FileName, a.Kind, a.OwnerKind, a.OwnerKey)))
        {
            if (n++ >= limit) yield break;
            yield return new DocumentHit("att" + a.Id, a.FileName, a.FilePath, 0, $"{a.OwnerKind} {a.OwnerKey} ({a.Kind})", "doc");
        }
        foreach (var doc in s.AconexDocs.Where(x => Hit(x.DocNo, x.Title, x.DocType)))
        {
            if (n++ >= limit) yield break;
            yield return doc.DocType.StartsWith("WIR", StringComparison.OrdinalIgnoreCase) || doc.DocNo.Contains("WIR", StringComparison.OrdinalIgnoreCase)
                ? new DocumentHit(doc.DocNo, $"{doc.DocNo} {doc.Title}", doc.LocalPath, 0, $"{doc.DocType} rev {doc.Revision} {doc.Status}", "wir")
                : new DocumentHit("aconex" + doc.Id, $"{doc.DocNo} {doc.Title}", doc.LocalPath, 0, $"{doc.DocType} rev {doc.Revision} {doc.Status}", "doc");
        }
        var vs = d.TryVariations();
        if (vs != null)
        {
            var vars = vs.Variations().ToDictionary(v => v.Id);
            foreach (var v in vars.Values.Where(v => Hit(v.Number, v.Title, v.Description, v.ConsultantRef)))
            {
                if (n++ >= limit) yield break;
                yield return new DocumentHit(v.Number, $"{v.Number} {v.Title}", "", 0, $"{v.Status} - {v.Description}", "variation");
            }
            foreach (var v in vars.Values)
                foreach (var doc in vs.Docs(v.Id).Where(x => Hit(x.FileName, x.ExtractedText)))
                {
                    if (n++ >= limit) yield break;
                    yield return new DocumentHit("vdoc" + doc.Id, $"{doc.FileName} ({v.Number})", doc.Path, 0, Snip(doc.ExtractedText, words[0]), "doc");
                }
        }
        foreach (var r in s.Rooms.Where(r => Hit(r.Code, r.RoomType, r.Level, r.Unit)).Take(10))
        {
            if (n++ >= limit) yield break;
            yield return new DocumentHit(r.Code.ToUpperInvariant(), $"ROOM {r.Code}", "", 0, $"{r.Building} {r.Level} {r.RoomType} {r.AreaType}", "room");
        }
        foreach (var i in s.SubInvoices.Where(i => Hit(i.Title, i.ContractNo, i.AconexWorkflowNo, i.RejectionReason)).Take(10))
        {
            if (n++ >= limit) yield break;
            yield return new DocumentHit($"{i.ContractNo}|{i.Subcontractor}|{i.InvoiceNo}", i.Title, "", 0, $"{i.Status} {i.AconexWorkflowNo} {i.RejectionReason}", "invoice");
        }
        foreach (var ci in s.ContractItems.Where(ci => Hit(ci.ItemNo, ci.Description, ci.Section)).Take(10))
        {
            if (n++ >= limit) yield break;
            yield return new DocumentHit(ci.ContractNo, $"{ci.ContractNo} item {ci.ItemNo}", "", 0, $"{ci.Description} - SAR {ci.Rate:N2}/{ci.Unit}", "contract");
        }
        var mats = d.TryMaterials();
        if (mats != null)
        {
            foreach (var dn in mats.Dns.Where(x => Hit(x.DnNo, x.Supplier, x.PoNo, x.OrderNo)).Take(10))
            {
                if (n++ >= limit) yield break;
                yield return new DocumentHit(dn.DnNo, $"DN {dn.DnNo}", dn.SourceFile, 0, $"{dn.Supplier} PO {dn.PoNo}", "dn");
            }
            foreach (var po in mats.Pos.Where(x => Hit(x.PoNo, x.Supplier)).Take(10))
            {
                if (n++ >= limit) yield break;
                yield return new DocumentHit(po.PoNo, $"PO {po.PoNo}", po.SourceFile, 0, po.Supplier, "po");
            }
        }
    }
}
