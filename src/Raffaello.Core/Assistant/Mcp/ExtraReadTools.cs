using System.Globalization;
using System.Text.Json.Nodes;
using Raffaello.Core.Domain;
using Raffaello.Core.Ledger;

namespace Raffaello.Core.Assistant.Mcp;

/// <summary>
/// Read-only tools offered to Claude through the Raffaello MCP server in addition to the assistant's read tools: room lists by type /
/// area, remaining per room for one stage x item (with room-type / area filters), WIRs and site statements. They never write.
/// </summary>
public sealed class ExtraReadTools
{
    public const string ListRooms = "list_rooms";
    public const string RoomsRemaining = "rooms_remaining";
    public const string ListWirs = "list_wirs";
    public const string ListSiteStatements = "list_site_statements";

    public static readonly string[] Names = { ListRooms, RoomsRemaining, ListWirs, ListSiteStatements };

    private readonly AssistantData _d;
    public ExtraReadTools(AssistantData data) => _d = data;

    private static JsonObject Str(string desc, params string[] values)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = desc };
        if (values.Length > 0) o["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return o;
    }
    private static JsonObject Int(string desc) => new() { ["type"] = "integer", ["description"] = desc };
    private static JsonObject Bool(string desc) => new() { ["type"] = "boolean", ["description"] = desc };

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

    /// <summary>Definitions in the same shape as <see cref="AssistantTools.Definitions"/> (name, description, input_schema).</summary>
    public static JsonArray Definitions() => new()
    {
        Tool(ListRooms,
            "Rooms of the project with their building, level, room type (e.g. TSK, K1, 2BR) and area type (GUESTROOM, APARTMENT, FOH, BOH, BALCONY, PARKING ...). Returns counts per area type and room type plus the room codes. Call it to learn which room / area types exist or how many rooms match.",
            new JsonObject
            {
                ["building"] = Str("BRANDED or HOTEL (omit for both).", Buildings.Branded, Buildings.Hotel),
                ["level"] = Str("Level as written on the room, e.g. L02, B1."),
                ["room_type"] = Str("Room type, e.g. TSK, K1, 2BR, 1BR-A."),
                ["area_type"] = Str("Area type, e.g. GUESTROOM, APARTMENT, FOH, BOH."),
                ["room"] = Str("Room code; a trailing * matches a prefix (e.g. 3*)."),
                ["limit"] = Int("Maximum room codes listed (default 100)."),
            }),
        Tool(RoomsRemaining,
            "Remaining quantity per room for ONE stage and ONE item: PROJECT QTY, claimed by all subcontractors, remaining, OVER. Filters by building, level, room type and area type (e.g. HOTEL + GUESTROOM for hotel guest rooms). Returns a summary (rooms matched, rooms with remaining > 0, fully claimed, OVER, totals) and the rooms sorted by remaining. Call it for questions like 'how many hotel guest rooms still have 2nd fix DATA remaining'.",
            new JsonObject
            {
                ["stage"] = Str("Stage: 1ST FIX, 2ND FIX, FINAL FIX (or CEILING, FLEXIBLE, DB PANELS when the ledger uses them)."),
                ["item"] = Str("System / item, e.g. LIGHT, POWER, DATA, GRMS, AV. Exact match first, then 'contains'."),
                ["building"] = Str("BRANDED or HOTEL (omit for both).", Buildings.Branded, Buildings.Hotel),
                ["level"] = Str("Level, e.g. L02."),
                ["room_type"] = Str("Room type, e.g. TSK, K1, 2BR."),
                ["area_type"] = Str("Area type, e.g. GUESTROOM, APARTMENT, FOH, BOH."),
                ["room"] = Str("Room code; a trailing * matches a prefix."),
                ["only_with_remaining"] = Bool("List only rooms with remaining > 0 (default true). The summary always counts all rooms."),
                ["limit"] = Int("Maximum rooms listed (default 50)."),
            },
            "stage", "item"),
        Tool(ListWirs,
            "Work inspection requests (WIR) with status (OPEN / APPROVED / REJECTED ...), Aconex no., subcontractor, level, system, stage and dates, plus counts per status. Call it for WIR / inspection questions.",
            new JsonObject
            {
                ["building"] = Str("BRANDED or HOTEL (omit for both).", Buildings.Branded, Buildings.Hotel),
                ["subcontractor"] = Str("Subcontractor (part of the name)."),
                ["level"] = Str("Level."),
                ["system"] = Str("System, e.g. LIGHT, DATA."),
                ["stage"] = Str("Stage."),
                ["status"] = Str("Status, e.g. OPEN, APPROVED, REJECTED."),
                ["wir_no"] = Str("WIR number or part of it."),
                ["limit"] = Int("Maximum WIRs listed (default 30)."),
            }),
        Tool(ListSiteStatements,
            "Site statements imported into the app (subcontractor, statement no., direction, file name, number of lines, date). Call it for questions about statements received or sent.",
            new JsonObject
            {
                ["subcontractor"] = Str("Subcontractor (part of the name)."),
                ["limit"] = Int("Maximum statements listed (default 30)."),
            }),
    };

    public ToolOutcome Execute(string name, JsonObject input)
    {
        var def = Definitions().OfType<JsonObject>().FirstOrDefault(t => (string?)t["name"] == name);
        if (def is null) return Error($"Unknown tool {name}.");
        var problems = Validate(def["input_schema"]!.AsObject(), input);
        if (problems.Count > 0) return Error($"Invalid input for {name}: {string.Join("; ", problems)}. Fix the input and call the tool again.");
        if (!_d.Settings.AllowReadProjectData)
            return Error("The user has switched off 'Allow assistant to read project data' in Settings. Answer from the house rules only, or ask the user to switch it on.");
        try
        {
            return name switch
            {
                ListRooms => Rooms(input),
                RoomsRemaining => Remaining(input),
                ListWirs => Wirs(input),
                ListSiteStatements => Statements(input),
                _ => Error($"Unknown tool {name}."),
            };
        }
        catch (Exception ex)
        {
            return Error($"{name} failed: {ex.Message}");
        }
    }

    private static List<string> Validate(JsonObject schema, JsonObject input)
    {
        var errors = new List<string>();
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        foreach (var r in (schema["required"] as JsonArray ?? new JsonArray()).Select(n => (string?)n))
            if (r != null && (!input.TryGetPropertyValue(r, out var v) || v is null)) errors.Add($"{r} is required");
        foreach (var (k, v) in input)
        {
            if (props[k] is not JsonObject p) { errors.Add($"{k} is not a known field"); continue; }
            if (v is null) continue;
            var kind = v.GetValueKind();
            var ok = (string?)p["type"] switch
            {
                "string" => kind == System.Text.Json.JsonValueKind.String,
                "integer" => kind == System.Text.Json.JsonValueKind.Number,
                "boolean" => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
                _ => true,
            };
            if (!ok) errors.Add($"{k} must be {(string?)p["type"]}");
            else if (p["enum"] is JsonArray en && !en.Select(e => (string?)e).Contains((string?)v, StringComparer.OrdinalIgnoreCase))
                errors.Add($"{k} must be one of {string.Join(", ", en.Select(e => (string?)e))}");
        }
        return errors;
    }

    // ------------------------------------------------------------------ helpers

    private static ToolOutcome Error(string msg) => new() { Content = msg, IsError = true };

    private static ToolOutcome Ok(JsonObject result, List<Citation> cites)
    {
        if (cites.Count > 0) result["cite_tokens"] = new JsonArray(cites.Select(c => (JsonNode)JsonValue.Create(c.Token)!).Distinct().Take(40).ToArray());
        return new ToolOutcome { Content = result.ToJsonString(), Citations = cites };
    }

    private static string Up(string? s) => (s ?? "").Trim().ToUpperInvariant();
    /// <summary>Compares labels ignoring case, spaces, dashes and underscores (GUEST ROOM = GUESTROOM, 1BR-A = 1BRA).</summary>
    internal static string Norm(string? s) => new string(Up(s).Where(char.IsLetterOrDigit).ToArray());
    private static bool Eq(string? value, string? filter) => filter is null || Norm(value) == Norm(filter);
    private static bool Like(string? value, string? q) => q is null || (value ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
    private static bool BuildingOk(string? value, string? filter) => filter is null || Up(value).Length == 0 || Up(value) == Up(filter);
    private static double R2(double v) => Math.Round(v, 2);

    private static bool RoomMatch(string room, string? pattern)
    {
        if (pattern is null) return true;
        var p = Up(pattern);
        return p.EndsWith('*') ? Up(room).StartsWith(p.TrimEnd('*'), StringComparison.Ordinal) : Up(room) == p;
    }

    private static string StageOf(string raw)
    {
        var n = Stages.Normalize(raw);
        return n.Length > 0 ? n : Up(raw);
    }

    private static bool LevelOk(Room r, string? level) =>
        level is null || Norm(r.Level) == Norm(level) || r.Floor.ToString(CultureInfo.InvariantCulture) == level.Trim();

    private IEnumerable<Room> FilterRooms(JsonObject input)
    {
        var building = AssistantTools.S(input, "building");
        var level = AssistantTools.S(input, "level");
        var type = AssistantTools.S(input, "room_type");
        var area = AssistantTools.S(input, "area_type");
        var room = AssistantTools.S(input, "room");
        return _d.Project.Snapshot.Rooms.Where(r => (building is null || Up(r.Building) == Up(building)) && LevelOk(r, level)
                                                    && Eq(r.RoomType, type) && Eq(r.AreaType, area) && RoomMatch(r.Code, room));
    }

    // ------------------------------------------------------------------ list_rooms

    private ToolOutcome Rooms(JsonObject input)
    {
        var limit = Math.Clamp(AssistantTools.I(input, "limit") ?? 100, 1, 500);
        var rooms = FilterRooms(input).OrderBy(r => r.Building).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new JsonObject
        {
            ["rooms_matched"] = rooms.Count,
            ["by_area_type"] = Counts(rooms.GroupBy(r => r.Building + " / " + (r.AreaType.Length > 0 ? r.AreaType : "(no area type)"))),
            ["by_room_type"] = Counts(rooms.GroupBy(r => r.Building + " / " + (r.RoomType.Length > 0 ? r.RoomType : "(no room type)"))),
        };
        var list = new JsonArray();
        var cites = new List<Citation>();
        foreach (var r in rooms.Take(limit))
        {
            list.Add(new JsonObject { ["room"] = r.Code, ["building"] = r.Building, ["level"] = r.Level, ["room_type"] = r.RoomType, ["area_type"] = r.AreaType });
            cites.Add(Citation.Room(r.Code));
        }
        result["rooms"] = list;
        if (rooms.Count > limit) result["note"] = $"{rooms.Count - limit} more rooms not listed; narrow the filter or raise limit.";
        return Ok(result, cites);
    }

    private static JsonObject Counts(IEnumerable<IGrouping<string, Room>> groups)
    {
        var o = new JsonObject();
        foreach (var g in groups.OrderByDescending(g => g.Count()).Take(60)) o[g.Key] = g.Count();
        return o;
    }

    // ------------------------------------------------------------------ rooms_remaining

    private ToolOutcome Remaining(JsonObject input)
    {
        var s = _d.Project.Snapshot;
        var stage = StageOf(AssistantTools.S(input, "stage")!);
        var itemQ = AssistantTools.S(input, "item")!;
        var building = AssistantTools.S(input, "building") is { } b ? Up(b) : null;
        var onlyRemaining = !(input["only_with_remaining"] is JsonValue ov && ov.GetValueKind() == System.Text.Json.JsonValueKind.False);
        var limit = Math.Clamp(AssistantTools.I(input, "limit") ?? 50, 1, 500);
        var roomFilters = new[] { "level", "room_type", "area_type" }.Any(k => AssistantTools.S(input, k) != null);
        var pattern = AssistantTools.S(input, "room");

        // exact item first ("LIGHT" must not pull in TERRACE LIGHT / EMERGENCY LIGHT), then 'contains'
        var itemsInData = s.RoomQtys.Where(q => Up(q.Stage) == stage).Select(q => Up(q.Item)).Distinct().ToList();
        var items = itemsInData.Contains(Up(itemQ)) ? new List<string> { Up(itemQ) } : itemsInData.Where(i => i.Contains(Up(itemQ), StringComparison.Ordinal)).ToList();
        if (items.Count == 0) items.Add(Up(itemQ));

        var rooms = FilterRooms(input).ToList();
        var roomSet = rooms.Select(r => Up(r.Code)).ToHashSet();
        var roomInfo = rooms.GroupBy(r => Up(r.Code)).ToDictionary(g => g.Key, g => g.First());
        bool KeyOk(string bld, string room, string stg, string item) =>
            BuildingOk(bld, building) && Up(stg) == stage && items.Contains(Up(item))
            && (roomFilters ? roomSet.Contains(Up(room)) : RoomMatch(room, pattern));

        var caps = s.RoomQtys.Where(q => KeyOk(q.Building, q.Room, q.Stage, q.Item)).ToList();
        var claims = LedgerRules.Effective(s.Claims).Where(c => !c.Rework && !LedgerRules.IsNotCompared(c) && KeyOk(c.Building, c.Room, c.Stage, c.Item)).ToList();

        var capBy = caps.GroupBy(q => Up(q.Room)).ToDictionary(g => g.Key, g => g.Sum(q => q.Qty));
        var claimBy = claims.GroupBy(c => Up(c.Room)).ToDictionary(g => g.Key, g => g.ToList());
        var keys = roomFilters ? roomSet.ToList() : capBy.Keys.Union(claimBy.Keys).ToList();

        var rows = keys.Select(k =>
        {
            var cap = capBy.GetValueOrDefault(k);
            var list = claimBy.GetValueOrDefault(k) ?? new List<ClaimLine>();
            var claimed = list.Sum(c => c.Qty);
            return (Room: k, Cap: cap, Claimed: claimed, Remaining: cap - claimed,
                Subs: list.GroupBy(c => c.Subcontractor).ToDictionary(g => g.Key, g => g.Sum(c => c.Qty)));
        }).ToList();

        var withCap = rows.Where(r => r.Cap > 0).ToList();
        var result = new JsonObject
        {
            ["filter"] = new JsonObject
            {
                ["building"] = building ?? "ALL", ["stage"] = stage, ["items"] = new JsonArray(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
                ["level"] = AssistantTools.S(input, "level"), ["room_type"] = AssistantTools.S(input, "room_type"), ["area_type"] = AssistantTools.S(input, "area_type"), ["room"] = pattern,
            },
            ["summary"] = new JsonObject
            {
                ["rooms_matched"] = rows.Count,
                ["rooms_with_project_qty"] = withCap.Count,
                ["rooms_without_project_qty"] = rows.Count - withCap.Count,
                ["rooms_with_remaining"] = withCap.Count(r => r.Remaining > LedgerRules.Eps),
                ["rooms_fully_claimed"] = withCap.Count(r => Math.Abs(r.Remaining) <= LedgerRules.Eps),
                ["rooms_over"] = rows.Count(r => r.Claimed > r.Cap + LedgerRules.Eps),
                ["rooms_not_started"] = withCap.Count(r => r.Claimed <= LedgerRules.Eps),
                ["total_project_qty"] = R2(rows.Sum(r => r.Cap)),
                ["total_claimed"] = R2(rows.Sum(r => r.Claimed)),
                ["total_remaining_positive"] = R2(rows.Where(r => r.Remaining > 0).Sum(r => r.Remaining)),
                ["total_over_qty"] = R2(rows.Where(r => r.Remaining < 0).Sum(r => -r.Remaining)),
            },
            ["note"] = "Remaining = PROJECT QTY - claims of ALL subcontractors for room x stage x item (rework and NOT COMPARED lines excluded). Stages are never added together.",
        };
        var shown = rows.Where(r => !onlyRemaining || r.Remaining > LedgerRules.Eps)
            .OrderByDescending(r => r.Remaining).ThenBy(r => r.Room, StringComparer.Ordinal).ToList();
        var listOut = new JsonArray();
        var cites = new List<Citation>();
        foreach (var r in shown.Take(limit))
        {
            var o = new JsonObject
            {
                ["room"] = r.Room, ["project_qty"] = R2(r.Cap), ["claimed"] = R2(r.Claimed), ["remaining"] = R2(r.Remaining),
            };
            if (roomInfo.TryGetValue(r.Room, out var info)) { o["level"] = info.Level; o["room_type"] = info.RoomType; o["area_type"] = info.AreaType; }
            if (r.Claimed > r.Cap + LedgerRules.Eps) o["flag"] = "OVER";
            if (r.Subs.Count > 0)
            {
                var subs = new JsonObject();
                foreach (var (k, v) in r.Subs.OrderByDescending(x => x.Value)) subs[k] = R2(v);
                o["by_subcontractor"] = subs;
            }
            listOut.Add(o);
            cites.Add(Citation.Room(r.Room));
        }
        result["rooms"] = listOut;
        if (shown.Count > limit) result["more"] = $"{shown.Count - limit} more rooms not listed (raise limit or narrow the filter).";
        return Ok(result, cites);
    }

    // ------------------------------------------------------------------ list_wirs

    private ToolOutcome Wirs(JsonObject input)
    {
        var s = _d.Project.Snapshot;
        var building = AssistantTools.S(input, "building");
        var sub = AssistantTools.S(input, "subcontractor");
        var level = AssistantTools.S(input, "level");
        var system = AssistantTools.S(input, "system");
        var stage = AssistantTools.S(input, "stage") is { } st ? StageOf(st) : null;
        var status = AssistantTools.S(input, "status");
        var no = AssistantTools.S(input, "wir_no");
        var limit = Math.Clamp(AssistantTools.I(input, "limit") ?? 30, 1, 300);
        var wirs = s.Wirs.Where(w => (building is null || Up(w.Building) == Up(building)) && Like(w.Subcontractor, sub) && Eq(w.Level, level)
                                     && Eq(w.System, system) && (stage is null || Up(w.Stage) == stage) && Eq(w.Status, status) && Like(w.WirNo, no))
            .OrderByDescending(w => w.SubmittedAt).ToList();
        var byStatus = new JsonObject();
        foreach (var g in wirs.GroupBy(w => Up(w.Status)).OrderByDescending(g => g.Count())) byStatus[g.Key.Length > 0 ? g.Key : "(blank)"] = g.Count();
        var list = new JsonArray();
        var cites = new List<Citation>();
        foreach (var w in wirs.Take(limit))
        {
            list.Add(new JsonObject
            {
                ["wir_no"] = w.WirNo, ["status"] = w.Status, ["subcontractor"] = w.Subcontractor, ["building"] = w.Building, ["level"] = w.Level,
                ["system"] = w.System, ["stage"] = w.Stage, ["aconex_no"] = w.AconexNo, ["description"] = w.Description,
                ["submitted"] = w.SubmittedAt == default ? null : w.SubmittedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["approved"] = w.ApprovedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
            if (w.WirNo.Length > 0) cites.Add(Citation.Wir(w.WirNo));
        }
        var result = new JsonObject { ["wirs_matched"] = wirs.Count, ["by_status"] = byStatus, ["wirs"] = list };
        if (wirs.Count > limit) result["more"] = $"{wirs.Count - limit} more WIRs not listed.";
        return Ok(result, cites);
    }

    // ------------------------------------------------------------------ list_site_statements

    private ToolOutcome Statements(JsonObject input)
    {
        var sub = AssistantTools.S(input, "subcontractor");
        var limit = Math.Clamp(AssistantTools.I(input, "limit") ?? 30, 1, 300);
        var rows = _d.Project.Snapshot.Statements.Where(x => Like(x.Subcontractor, sub)).OrderByDescending(x => x.At).ToList();
        var list = new JsonArray();
        foreach (var x in rows.Take(limit))
            list.Add(new JsonObject
            {
                ["subcontractor"] = x.Subcontractor, ["statement_no"] = x.StatementNo, ["direction"] = x.Direction, ["file"] = x.FileName, ["lines"] = x.Lines,
                ["date"] = x.At == default ? null : x.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        var result = new JsonObject { ["statements_matched"] = rows.Count, ["statements"] = list };
        if (rows.Count == 0) result["note"] = "No site statements are stored in this data file.";
        return Ok(result, new List<Citation>());
    }
}