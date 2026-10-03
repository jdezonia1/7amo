using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Raffaello.Core.Ai;
using Raffaello.Core.Assistant.ClaudeCode;

namespace Raffaello.Core.Assistant;

public enum AssistantEventKind { TextDelta, ToolStarted, ToolFinished, ActionProposed, Notice }

public sealed record AssistantEvent(AssistantEventKind Kind, string Text = "", AssistantAction? Action = null, IReadOnlyList<Citation>? Citations = null);

public sealed class AssistantReply
{
    public string Text { get; set; } = "";
    public List<Citation> Citations { get; } = new();
    public List<AssistantAction> Actions { get; } = new();
    public bool Offline { get; set; }
    public string Notice { get; set; } = "";
    public List<string> ToolsUsed { get; } = new();
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
}

/// <summary>A chat bubble rebuilt from the stored history (consecutive assistant messages of one reply are one bubble).</summary>
public sealed record ChatBubble(string Role, string Text, List<Citation> Citations, bool Offline, DateTime At, List<long> MessageIds);

/// <summary>
/// One user's conversation with the assistant: a manual tool loop over the Messages API (streaming, adaptive thinking), history stored
/// append-only (content blocks as received, so thinking blocks stay valid and the cache prefix stays stable), screen context and
/// confirmed-action outcomes carried in the user turns (never by editing the frozen system prompt), offline answers without a key.
/// </summary>
public sealed class AssistantSession
{
    private readonly AssistantData _d;
    private readonly Func<string, IClaudeTransport> _transport;
    private readonly AssistantTools _tools;
    private readonly OfflineAssistant _offline;

    public AssistantSession(AssistantData data, Func<string, IClaudeTransport>? transport = null)
    {
        _d = data;
        _transport = transport ?? (key => new ClaudeTurnClient(key));
        _tools = new AssistantTools(data);
        _offline = new OfflineAssistant(data, _tools);
    }

    public AssistantTools Tools => _tools;
    public AssistantActions Actions => _tools.Actions;
    public OfflineAssistant Offline => _offline;
    public AssistantConversation? Conversation { get; private set; }

    /// <summary>The API key (resolved by the app: DPAPI vault, ANTHROPIC_API_KEY ...). Null = offline mode.</summary>
    public Func<string?> ApiKey { get; set; } = () => null;
    public string Model { get; set; } = AnthropicClient.DefaultModel;
    public string Effort { get; set; } = "medium";
    public bool UseServerFallbacks { get; set; } = true;

    /// <summary>[claude-login] Picks the engine for each question. Null = the API key when one is set, else offline.</summary>
    public Func<RouteDecision>? Router { get; set; }
    /// <summary>[claude-login] Runs Claude Code with the user's Claude login (null = not available).</summary>
    public Func<IClaudeCodeRunner?>? ClaudeCode { get; set; }

    public RouteDecision CurrentRoute()
    {
        if (Router != null)
            try { return Router(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { return new RouteDecision(AssistantRoute.Offline, ex.Message, true); }
        return string.IsNullOrWhiteSpace(ApiKey()) ? new RouteDecision(AssistantRoute.Offline, "no API key") : new RouteDecision(AssistantRoute.Api, "API key");
    }

    public const string SystemPrompt = """
        You are Raffaello, the assistant built into MOBCO's desktop app for the electrical / ELV quantity surveying of the Raffles Hotel and Branded Residences project in Riyadh. You help the QS engineer and the site team with subcontractor claims, the room ledger, invoices and their Aconex approval, materials (PO, delivery notes, MIR), variations and contracts.

        How to work:
        - Use the tools to look at the project data before answering any question about quantities, rooms, invoices, deliveries, contracts or what needs attention. Never invent figures; if the data does not show it, say what is missing and where the user can find or enter it.
        - Every tool result lists cite_tokens such as [[room:P2-106]] or [[invoice:SUB-ELE-028-2026|ROOTS|3]]. Put the matching token right after each fact you take from a result, exactly as written, so the app can show it as a clickable source. Do not make up tokens.
        - House rules you must respect: stages (1ST FIX, 2ND FIX, FINAL FIX) are never added together; remaining = PROJECT QTY minus the claims of all subcontractors for the same room x stage x item; claims above remaining need a reason and are flagged OVER; claimed above approved WIR is CHECK (hold); EMT is rework; compare after SITE %, value invoices on WIR %; 15 m rule qty per point = max(1, L / 15); >4.5 m only for the accepted quantity; pending checks are held out of invoices. Use explain_rule for the details.
        - Writing tools (draft_ledger_claim, draft_invoice_revision, draft_rejection_reply_email, draft_variation, create_reminder) only prepare a card; nothing is written until the user presses CONFIRM in the chat. Use them when the user asks you to do one of these things, then say plainly what the card will do. Never claim something was posted, saved or sent unless a later message says the user confirmed it and it succeeded.
        - Each user message starts with an <app_context> block: the date and time in Riyadh, the screen the user is on, the filter, the selected line or record, and the outcome of cards confirmed since your last answer. Use it to resolve "this line", "this room", "here". Attached files arrive as <attachment> blocks or as documents / images.
        - Answer in the language of the user's message: Arabic (Modern Standard, simple site-office wording, keep codes like P2-106, WIR, OVER, PROJECT QTY in Latin letters) or English. Keep answers short and practical: lead with the answer, then the figures. Quote quantities with units and SAR with thousands separators. Use UPPERCASE for labels like OVER, CHECK, WIR, PROJECT QTY. No emojis. Plain text with simple dashes for lists; no markdown tables.
        """;

    // ------------------------------------------------------------------ conversations

    public List<AssistantConversation> Conversations() => _d.Store.Conversations(_d.User);

    public void NewConversation() => Conversation = null;

    public void Open(long conversationId) =>
        Conversation = _d.Store.All<AssistantConversation>().FirstOrDefault(c => c.Id == conversationId && string.Equals(c.Owner, _d.User, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException("Conversation not found.");

    public void Archive(AssistantConversation c)
    {
        c.Archived = true;
        _d.Store.Update(c, $"Assistant conversation #{c.Id} archived");
        if (Conversation?.Id == c.Id) Conversation = null;
    }

    public List<AssistantMessage> History() => Conversation is null ? new() : _d.Store.Messages(Conversation.Id);

    public List<AssistantAction> ConversationActions() => Conversation is null ? new() : _d.Store.Actions(Conversation.Id);

    /// <summary>Bubbles for the chat: user questions and assistant replies (tool traffic hidden).</summary>
    public List<ChatBubble> Bubbles()
    {
        var res = new List<ChatBubble>();
        foreach (var m in History().Where(m => !m.Hidden))
        {
            if (m.Role == "assistant" && res.Count > 0 && res[^1].Role == "assistant")
            {
                var prev = res[^1];
                var text = string.IsNullOrWhiteSpace(m.DisplayText) ? prev.Text : (prev.Text.Length > 0 ? prev.Text + "\n\n" : "") + m.DisplayText;
                var cites = CitationText.Deserialize(m.CitationsJson);
                res[^1] = prev with { Text = text, Citations = cites.Count > 0 ? cites : prev.Citations, Offline = prev.Offline || m.Offline, MessageIds = prev.MessageIds.Append(m.Id).ToList() };
            }
            else res.Add(new ChatBubble(m.Role, m.DisplayText, CitationText.Deserialize(m.CitationsJson), m.Offline, m.CreatedAt, new List<long> { m.Id }));
        }
        return res;
    }

    private AssistantConversation EnsureConversation(string firstQuestion)
    {
        if (Conversation != null) return Conversation;
        var title = firstQuestion.Replace('\n', ' ').Trim();
        Conversation = _d.Store.Insert(new AssistantConversation
        {
            Owner = _d.User, Title = title.Length > 70 ? title[..70] + "..." : title, CreatedAt = _d.Clock(), LastAt = _d.Clock(),
            Language = OfflineAssistant.IsArabic(firstQuestion) || _d.Settings.IsArabic ? "ar" : "en", Model = Model,
        }, $"Assistant conversation started by {_d.User}");
        return Conversation;
    }

    private int NextSeq() => Conversation is null ? 1 : _d.Store.Messages(Conversation.Id).Select(m => m.Seq).DefaultIfEmpty(0).Max() + 1;

    private AssistantMessage Save(string role, JsonArray content, string display, bool hidden = false, bool offline = false, ClaudeTurn? turn = null, IEnumerable<Citation>? cites = null) =>
        _d.Store.Insert(new AssistantMessage
        {
            ConversationId = Conversation!.Id, Seq = NextSeq(), Role = role, ContentJson = content.ToJsonString(), DisplayText = display, Hidden = hidden, Offline = offline,
            CitationsJson = cites is null ? "" : CitationText.Serialize(cites), Model = turn?.Model ?? "", StopReason = turn?.StopReason ?? "",
            InputTokens = turn?.InputTokens ?? 0, OutputTokens = turn?.OutputTokens ?? 0, CacheReadTokens = turn?.CacheReadTokens ?? 0, CreatedAt = _d.Clock(),
        }, null);

    // ------------------------------------------------------------------ the user turn

    /// <summary>The &lt;app_context&gt; block of a user turn (time, user, screen, privacy, confirmed actions).</summary>
    public string ContextBlock(ScreenContext screen, IEnumerable<AssistantAction> decided)
    {
        var now = _d.Clock();
        var sb = new StringBuilder("<app_context>\n");
        sb.Append("now (Riyadh): ").Append(now.ToString("yyyy-MM-dd HH:mm ddd", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("user: ").Append(_d.User).Append('\n');
        sb.Append(screen.Describe()).Append('\n');
        if (!_d.Settings.AllowReadProjectData) sb.Append("project data access: OFF (the user switched it off - tools will refuse; answer from the house rules)\n");
        foreach (var a in decided)
            sb.Append($"card #{a.Id} {AssistantActionKinds.Label(a.Kind)}: {a.Status} by {a.DecidedBy}{(a.Result.Length > 0 ? " - " + a.Result : "")}\n");
        sb.Append("</app_context>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the messages array from the stored history: consecutive messages of the same role are merged (a question after an
    /// interrupted reply), and a tool_use without its result (stopped mid-way) gets an error result - both deterministic, so the
    /// prefix the API sees never changes between requests.
    /// </summary>
    public static JsonArray BuildMessages(IReadOnlyList<AssistantMessage> history)
    {
        var list = new List<(string Role, JsonArray Content)>();
        foreach (var m in history)
        {
            var content = JsonNode.Parse(m.ContentJson) as JsonArray ?? new JsonArray();
            list.Add((m.Role, content));
        }
        // repair: each assistant tool_use needs a tool_result in the next user message
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Role != "assistant") continue;
            var ids = list[i].Content.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_use").Select(b => (string?)b["id"] ?? "").ToList();
            if (ids.Count == 0) continue;
            var next = i + 1 < list.Count && list[i + 1].Role == "user" ? list[i + 1].Content : null;
            var answered = next?.OfType<JsonObject>().Where(b => (string?)b["type"] == "tool_result").Select(b => (string?)b["tool_use_id"] ?? "").ToHashSet() ?? new HashSet<string>();
            var missing = ids.Where(id => !answered.Contains(id)).ToList();
            if (missing.Count == 0) continue;
            var results = new JsonArray(missing.Select(id => (JsonNode)new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = id, ["is_error"] = true, ["content"] = "Not executed: the user stopped the answer.",
            }).ToArray());
            if (next != null)
            {
                // tool results go first in the user message
                var merged = new JsonArray();
                foreach (var r in results) merged.Add(r!.DeepClone());
                foreach (var b in next) merged.Add(b!.DeepClone());
                list[i + 1] = ("user", merged);
            }
            else list.Insert(i + 1, ("user", results));
        }
        var res = new JsonArray();
        string? lastRole = null;
        JsonArray? lastContent = null;
        foreach (var (role, content) in list)
        {
            if (role == lastRole && lastContent != null)
            {
                foreach (var b in content) lastContent.Add(b!.DeepClone());
                continue;
            }
            lastContent = (JsonArray)content.DeepClone();
            res.Add(new JsonObject { ["role"] = role, ["content"] = lastContent });
            lastRole = role;
        }
        return res;
    }

    /// <summary>Asks a question. Streams text through <paramref name="onEvent"/>, runs read tools, proposes actions, stores everything.</summary>
    public async Task<AssistantReply> AskAsync(string question, ScreenContext screen, IReadOnlyList<ChatAttachment>? attachments = null,
        Action<AssistantEvent>? onEvent = null, CancellationToken ct = default)
    {
        question = (question ?? "").Trim();
        attachments ??= Array.Empty<ChatAttachment>();
        if (question.Length == 0 && attachments.Count == 0) throw new ArgumentException("Empty question.");
        var display = question.Length > 0 ? question : "(attached " + string.Join(", ", attachments.Select(a => a.FileName)) + ")";
        if (attachments.Count > 0 && question.Length > 0) display += "\n[" + string.Join(", ", attachments.Select(a => a.FileName)) + "]";
        EnsureConversation(display);
        var earlier = Bubbles();

        // outcomes of cards decided since the last answer travel with this question (append-only)
        var decided = _d.Store.Actions(Conversation!.Id).Where(a => a.Status != AssistantActionStatus.Proposed && !a.Reported).ToList();
        var context = ContextBlock(screen, decided);
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = context } };
        foreach (var a in attachments) foreach (var b in a.Blocks) content.Add(b.DeepClone());
        content.Add(new JsonObject { ["type"] = "text", ["text"] = question.Length > 0 ? question : "Read the attached file(s) and tell me what they contain and what needs my attention." });
        Save("user", content, display);
        foreach (var a in decided) { a.Reported = true; _d.Store.Update(a, null); }

        var reply = new AssistantReply();
        var route = CurrentRoute();
        if (route.Route == AssistantRoute.ClaudeCode)
            return await AskClaudeCodeAsync(question, context, attachments, earlier, reply, onEvent, ct).ConfigureAwait(false);
        var key = route.Route == AssistantRoute.Api ? ApiKey() : null;
        if (string.IsNullOrWhiteSpace(key))
            return OfflineReply(question, reply, route.Warn ? route.Reason : "", onEvent);

        try
        {
            await RunLoopAsync(key, reply, onEvent, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reply.Notice = "stopped";
            throw;
        }
        catch (Exception ex) when (ex is AnthropicException or HttpRequestException or TaskCanceledException or IOException)
        {
            var why = ex is AnthropicException ? ex.Message : "Could not reach api.anthropic.com: " + ex.Message;
            return OfflineReply(question, reply, why, onEvent);
        }
        finally
        {
            Touch(Model);
        }
        return reply;
    }

    private void Touch(string model)
    {
        if (Conversation is null) return;
        Conversation.LastAt = _d.Clock();
        Conversation.Model = model;
        try { Conversation = _d.Store.Update(Conversation, null); }
        catch (Exception) { Conversation = _d.Store.All<AssistantConversation>().FirstOrDefault(c => c.Id == Conversation.Id) ?? Conversation; }
    }

    // ------------------------------------------------------------------ [claude-login] Claude Code with the user's Claude login

    public const string ClaudeCodeAddendum = """

        This chat runs through Claude Code with the user's own Claude login. Your only tools are the READ-ONLY Raffaello tools (their names start with mcp__raffaello__): use them for every figure. The writing tools named above (draft_ledger_claim, draft_invoice_revision, draft_rejection_reply_email, draft_variation, create_reminder) are NOT available here: when the user asks to post, save, create, remind or send something, say exactly what to enter and where in the app, or suggest switching Settings > Assistant > PROVIDER to API KEY for CONFIRM cards. Never say that anything was saved or sent.
        rooms_remaining answers "how many rooms still have X remaining" (filters: building, stage, item, room type, area type such as GUESTROOM); list_rooms shows the room and area types that exist.
        Earlier messages of this conversation, if any, are in <conversation_so_far>. Answer the QUESTION at the end.
        """;

    /// <summary>The system prompt of the Claude Code route (same house rules, plus what differs there).</summary>
    public static string ClaudeCodeSystemPrompt => SystemPrompt + "\n" + ClaudeCodeAddendum;

    /// <summary>The prompt sent on stdin: earlier turns (short), the app context, attached text, the question.</summary>
    public static string ClaudeCodePrompt(string question, string context, IReadOnlyList<ChatAttachment> attachments, IReadOnlyList<ChatBubble> earlier, int maxEarlier = 8, int maxChars = 2000)
    {
        var sb = new StringBuilder();
        var past = earlier.Where(b => b.Text.Trim().Length > 0).TakeLast(maxEarlier).ToList();
        if (past.Count > 0)
        {
            sb.Append("<conversation_so_far>\n");
            foreach (var b in past)
            {
                var t = b.Text.Trim();
                if (t.Length > maxChars) t = t[..maxChars] + " [...]";
                sb.Append(b.Role == "user" ? "USER: " : "RAFFAELLO: ").Append(t).Append("\n\n");
            }
            sb.Append("</conversation_so_far>\n\n");
        }
        sb.Append(context).Append("\n\n");
        foreach (var a in attachments)
        {
            var texts = a.Blocks.Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? "").Where(t => t.Length > 0).ToList();
            foreach (var t in texts) sb.Append(t).Append("\n\n");
            if (a.Blocks.Any(b => (string?)b["type"] != "text"))
                sb.Append($"[The file {a.FileName} is a scan / image: it was not sent through Claude Code. Only its text above (if any) is available.]\n\n");
        }
        sb.Append("QUESTION:\n").Append(question.Length > 0 ? question : "Read the attached file(s) and tell me what they contain and what needs my attention.");
        return sb.ToString();
    }

    private async Task<AssistantReply> AskClaudeCodeAsync(string question, string context, IReadOnlyList<ChatAttachment> attachments, IReadOnlyList<ChatBubble> earlier,
        AssistantReply reply, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var runner = ClaudeCode?.Invoke();
        if (runner is null) { Touch("claude-code"); return OfflineReply(question, reply, "Claude Code is not available on this PC.", onEvent); }
        var s = _d.Settings;
        var request = new ClaudeCodeRequest(ClaudeCodePrompt(question, context, attachments, earlier), ClaudeCodeSystemPrompt,
            string.IsNullOrWhiteSpace(s.ClaudeCodeModel) ? null : s.ClaudeCodeModel.Trim(),
            Math.Clamp(s.ClaudeCodeMaxTurns, 2, 40), TimeSpan.FromSeconds(Math.Clamp(s.ClaudeCodeTimeoutSeconds, 30, 1800)));
        var streamed = new StringBuilder();
        ClaudeCodeResult res;
        try
        {
            res = await runner.RunAsync(request, e =>
            {
                switch (e.Kind)
                {
                    case ClaudeCodeEventKind.TextDelta:
                        streamed.Append(e.Text);
                        onEvent?.Invoke(new AssistantEvent(AssistantEventKind.TextDelta, e.Text));
                        break;
                    case ClaudeCodeEventKind.ToolUse:
                        onEvent?.Invoke(new AssistantEvent(AssistantEventKind.ToolStarted, e.ToolName));
                        break;
                    case ClaudeCodeEventKind.ToolResult:
                        onEvent?.Invoke(new AssistantEvent(AssistantEventKind.ToolFinished, e.ToolName));
                        break;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reply.Notice = "stopped";
            if (streamed.Length > 0)
                Save("assistant", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = streamed.ToString() + " [stopped]" }), CitationText.Strip(streamed.ToString()).Trim() + " [stopped]");
            Touch("claude-code");
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Touch("claude-code");
            return OfflineReply(question, reply, "Claude Code: " + ex.Message, onEvent);
        }
        Touch("claude-code" + (res.Model.Length > 0 ? ":" + res.Model : ""));
        if (res.Failed) return OfflineReply(question, reply, res.Error, onEvent);

        var full = res.Text.Length > 0 ? res.Text : streamed.ToString();
        var notice = res.HitTurnLimit
            ? (_d.Settings.IsArabic ? "[توقفت الإجابة عند حد جولات الأدوات. اسأل سؤالاً أضيق.]" : "[Stopped at the tool-round limit (Settings > Assistant). Ask a narrower question.]")
            : "";
        var available = res.Citations.Count > 0 ? res.Citations : CitationsFromTokens(full);
        reply.Citations.AddRange(CitationText.Resolve(full, available));
        reply.Text = CitationText.Strip(full).Trim() + (notice.Length > 0 ? "\n\n" + notice : "");
        reply.Notice = notice;
        reply.ToolsUsed.AddRange(res.ToolsUsed);
        reply.InputTokens = res.InputTokens;
        reply.OutputTokens = res.OutputTokens;
        if (notice.Length > 0) onEvent?.Invoke(new AssistantEvent(AssistantEventKind.Notice, notice));
        var turn = new ClaudeTurn { Model = "claude-code" + (res.Model.Length > 0 ? ":" + res.Model : ""), StopReason = res.HitTurnLimit ? "max_turns" : "end_turn", InputTokens = res.InputTokens, OutputTokens = res.OutputTokens };
        Save("assistant", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = full + (notice.Length > 0 ? "\n\n" + notice : "") }), reply.Text, turn: turn, cites: reply.Citations);
        return reply;
    }

    /// <summary>Navigable citations rebuilt from the tokens of an answer (when the MCP side channel gave none).</summary>
    public static List<Citation> CitationsFromTokens(string answer)
    {
        var res = new List<Citation>();
        foreach (var (kind, key) in CitationText.Tokens(answer))
        {
            Citation c = kind switch
            {
                "room" => Citation.Room(key),
                "invoice" when key.Split('|') is { Length: 3 } p && int.TryParse(p[2], out var no) => Citation.Invoice(p[0], p[1], no),
                "dn" => Citation.Dn("", key),
                "po" => Citation.Po(key),
                "variation" => Citation.Variation(key),
                "aconex" => Citation.Workflow(key),
                "wir" => Citation.Wir(key),
                "contract" => Citation.Contract(key),
                "report" => Citation.Report(key, key),
                "rule" => Citation.Rule(key, key),
                _ => new Citation(kind, key, $"{kind.ToUpperInvariant()} {key}"),
            };
            res.Add(c);
        }
        return res;
    }

    private AssistantReply OfflineReply(string question, AssistantReply reply, string why, Action<AssistantEvent>? onEvent)
    {
        var a = _offline.Answer(question.Length > 0 ? question : "help");
        var ar = OfflineAssistant.IsArabic(question) || _d.Settings.IsArabic;
        var head = why.Length > 0 ? (ar ? "تعذر الاتصال بـ Claude (" + why + "). إجابة من البيانات المحلية:\n\n" : why + "\nAnswer from local data:\n\n") : "";
        reply.Text = head + a.Text;
        reply.Offline = true;
        reply.Notice = why;
        reply.Citations.AddRange(a.Citations);
        onEvent?.Invoke(new AssistantEvent(AssistantEventKind.TextDelta, reply.Text));
        Save("assistant", new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply.Text }), reply.Text, offline: true, cites: reply.Citations);
        return reply;
    }

    private async Task RunLoopAsync(string key, AssistantReply reply, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var transport = _transport(key);
        var tools = AssistantTools.Definitions();
        var available = new List<Citation>();
        var text = new StringBuilder();
        var rounds = Math.Clamp(_d.Settings.MaxToolRounds, 1, 20);
        for (var round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            var request = new ClaudeRequest
            {
                Model = Model, Effort = Effort, MaxTokens = Math.Clamp(_d.Settings.MaxTokens, 1024, 64000), UseServerFallbacks = UseServerFallbacks,
                System = SystemPrompt, Tools = tools, Messages = BuildMessages(History()),
            };
            var turn = await transport.SendAsync(request, t => { text.Append(t); onEvent?.Invoke(new AssistantEvent(AssistantEventKind.TextDelta, t)); }, ct).ConfigureAwait(false);
            reply.InputTokens += turn.InputTokens; reply.OutputTokens += turn.OutputTokens; reply.CacheReadTokens += turn.CacheReadTokens;
            var turnText = string.Concat(turn.ContentForHistory().OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? ""));
            var toolUses = turn.ToolUses.ToList();
            var isLastTurn = turn.StopReason != "tool_use" || toolUses.Count == 0;

            var notice = turn.StopReason switch
            {
                "refusal" => _d.Settings.IsArabic ? "[رفض النموذج هذا الطلب. أعد صياغة السؤال.]" : "[The request was declined by the model's safety filter. Try rephrasing the question.]",
                "max_tokens" => _d.Settings.IsArabic ? "[توقفت الإجابة عند حد الرموز.]" : "[Answer cut at the token limit.]",
                _ => "",
            };
            if (notice.Length > 0)
            {
                reply.Notice = notice;
                text.Append("\n\n").Append(notice);
                onEvent?.Invoke(new AssistantEvent(AssistantEventKind.Notice, notice));
            }

            List<Citation>? cites = null;
            if (isLastTurn || notice.Length > 0)
            {
                cites = CitationText.Resolve(text.ToString(), available);
                reply.Citations.Clear();
                reply.Citations.AddRange(cites);
            }
            Save("assistant", turn.ContentForHistory(), CitationText.Strip(turnText) + (notice.Length > 0 ? "\n\n" + notice : ""), turn: turn, cites: cites);

            if (toolUses.Count == 0) break;

            // tools never run on a refused or truncated turn: answer each call with an error so the history stays valid
            var results = new JsonArray();
            var stop = notice.Length > 0 || round >= rounds;
            foreach (var tu in toolUses)
            {
                var id = (string?)tu["id"] ?? "";
                var name = (string?)tu["name"] ?? "";
                if (stop)
                {
                    results.Add(ToolResult(id, notice.Length > 0 ? "Not executed: the turn was " + (turn.StopReason == "refusal" ? "declined" : "cut at the token limit") + "."
                        : "Not executed: the tool budget for this question is used up. Answer with what you have.", true));
                    continue;
                }
                if (turn.InvalidToolInputs.TryGetValue(id, out var raw))
                {
                    results.Add(ToolResult(id, new JsonObject { ["INVALID_JSON"] = raw }.ToJsonString(), true));
                    continue;
                }
                onEvent?.Invoke(new AssistantEvent(AssistantEventKind.ToolStarted, name));
                var input = tu["input"] as JsonObject ?? new JsonObject();
                var outcome = await Task.Run(() => _tools.Execute(name, input, id, Conversation!.Id), ct).ConfigureAwait(false);
                reply.ToolsUsed.Add(name);
                available.AddRange(outcome.Citations);
                var body = outcome.Content.Length > _d.Settings.MaxToolResultChars
                    ? outcome.Content[.._d.Settings.MaxToolResultChars] + "\n[... result cut; ask with a narrower filter]"
                    : outcome.Content;
                results.Add(ToolResult(id, body, outcome.IsError));
                onEvent?.Invoke(new AssistantEvent(AssistantEventKind.ToolFinished, name, Citations: outcome.Citations));
                if (outcome.Proposed is { } action)
                {
                    reply.Actions.Add(action);
                    onEvent?.Invoke(new AssistantEvent(AssistantEventKind.ActionProposed, action.Preview, action));
                }
            }
            Save("user", results, "", hidden: true);
            if (stop && notice.Length > 0) break;
            if (round > rounds) break;
        }
        reply.Text = CitationText.Strip(text.ToString()).Trim();
        if (reply.Citations.Count == 0) reply.Citations.AddRange(CitationText.Resolve(text.ToString(), available));
    }

    private static JsonObject ToolResult(string id, string content, bool isError)
    {
        var o = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content };
        if (isError) o["is_error"] = true;
        return o;
    }

    // ------------------------------------------------------------------ cards

    public AssistantAction Confirm(long actionId)
    {
        var a = Actions.Find(actionId) ?? throw new InvalidOperationException($"Action #{actionId} not found.");
        return Actions.Execute(a);
    }

    public AssistantAction Cancel(long actionId)
    {
        var a = Actions.Find(actionId) ?? throw new InvalidOperationException($"Action #{actionId} not found.");
        return Actions.Cancel(a);
    }

    // ------------------------------------------------------------------ privacy

    /// <summary>What leaves this PC when the user asks (shown under the input box).</summary>
    public string PrivacySummary(IReadOnlyList<ChatAttachment>? attachments = null)
    {
        var ar = _d.Settings.IsArabic;
        var route = CurrentRoute().Route;
        if (route == AssistantRoute.Offline)
            return ar ? "بدون اتصال: لا يُرسل شيء خارج الجهاز." : "OFFLINE: nothing leaves this PC.";
        var parts = new List<string>
        {
            route == AssistantRoute.ClaudeCode
                ? (ar ? "يُرسل إلى Anthropic عبر Claude Code (حساب Claude الخاص بك، يُحتسب من استخدام اشتراكك): سؤالك وسياق الشاشة وآخر رسائل المحادثة"
                      : "Sent to Anthropic through Claude Code (your Claude login, counts against your plan's usage): your question, the screen context and the last messages of this chat")
                : (ar ? "يُرسل إلى Anthropic: سؤالك وسياق الشاشة" : "Sent to Anthropic: your question and the screen context"),
        };
        parts.Add(_d.Settings.AllowReadProjectData
            ? (ar ? "نتائج أدوات القراءة (غرف، سجل، فواتير، إشعارات تسليم...)" : "results of the read tools (rooms, ledger, invoices, DNs ...)")
            : (ar ? "قراءة بيانات المشروع: متوقفة" : "project data reading: OFF"));
        if (attachments is { Count: > 0 })
        {
            var cloud = attachments.Where(a => a.ReadAs == "CLOUD").ToList();
            var text = attachments.Where(a => a.ReadAs != "CLOUD").ToList();
            if (text.Count > 0) parts.Add((ar ? "نص الملفات: " : "text of ") + string.Join(", ", text.Select(a => a.FileName)));
            if (cloud.Count > 0) parts.Add((ar ? "الملفات كاملة (قراءة سحابية): " : "full files (cloud reading): ") + string.Join(", ", cloud.Select(a => a.FileName)));
        }
        else parts.Add(_d.Settings.CloudDocumentReading ? (ar ? "القراءة السحابية للمستندات: مفعلة" : "cloud document reading: ON") : (ar ? "القراءة السحابية: متوقفة" : "cloud document reading: OFF"));
        return string.Join("; ", parts) + ".";
    }
}
