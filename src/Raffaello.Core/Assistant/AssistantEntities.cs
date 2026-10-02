using Raffaello.Core.Domain;

namespace Raffaello.Core.Assistant;

/// <summary>One chat with the assistant, owned by one user (history is per user, local file or server).</summary>
public sealed class AssistantConversation : Entity
{
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastAt { get; set; }
    /// <summary>en / ar - the language the user chose when the conversation started.</summary>
    public string Language { get; set; } = "en";
    /// <summary>Model that answered last (the history is replayed unchanged, thinking blocks included).</summary>
    public string Model { get; set; } = "";
    public bool Archived { get; set; }
}

/// <summary>
/// One Messages-API message of a conversation, stored exactly as sent / received (<see cref="ContentJson"/> = the JSON array of
/// content blocks, thinking blocks with their signatures included) so the history is replayed append-only. <see cref="DisplayText"/>
/// is what the chat shows; tool-result-only user messages have <see cref="Hidden"/> = true.
/// </summary>
public sealed class AssistantMessage : Entity
{
    public long ConversationId { get; set; }
    public int Seq { get; set; }
    /// <summary>user / assistant.</summary>
    public string Role { get; set; } = "user";
    public string ContentJson { get; set; } = "[]";
    public string DisplayText { get; set; } = "";
    /// <summary>JSON array of <see cref="Citation"/> used by this answer (chips under the bubble).</summary>
    public string CitationsJson { get; set; } = "";
    public bool Hidden { get; set; }
    public bool Offline { get; set; }
    public string Model { get; set; } = "";
    public string StopReason { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class AssistantActionStatus
{
    public const string Proposed = "PROPOSED";
    public const string Executed = "EXECUTED";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}

/// <summary>
/// A write the assistant proposed (draft ledger claim, invoice revision, rejection e-mail, variation, reminder). Nothing is written to
/// the project until the user presses CONFIRM in the chat; every decision is kept here and in the audit log.
/// </summary>
public sealed class AssistantAction : Entity
{
    public long ConversationId { get; set; }
    public string ToolUseId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string InputJson { get; set; } = "{}";
    /// <summary>What will happen, in plain words (shown on the confirmation card).</summary>
    public string Preview { get; set; } = "";
    public string Warnings { get; set; } = "";
    public string Status { get; set; } = AssistantActionStatus.Proposed;
    public string ProposedBy { get; set; } = "";
    public DateTime ProposedAt { get; set; }
    public string DecidedBy { get; set; } = "";
    public DateTime? DecidedAt { get; set; }
    public string Result { get; set; } = "";
    /// <summary>Where the result lives (navigation target "Module|Key").</summary>
    public string ResultRef { get; set; } = "";
    /// <summary>The outcome has been told to the model (appended to the next user turn).</summary>
    public bool Reported { get; set; }
}

/// <summary>A reminder (from the assistant's create_reminder or typed by the user). Shows in Needs-today and the morning brief when due.</summary>
public sealed class AssistantReminder : Entity
{
    public string Owner { get; set; } = "";
    public DateTime Due { get; set; }
    public string Text { get; set; } = "";
    public string TargetModule { get; set; } = "";
    public string TargetKey { get; set; } = "";
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    public string Source { get; set; } = "MANUAL";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Per-user notification rule: which event goes to which channel, with quiet hours and a minimum severity.</summary>
public sealed class NotificationRule : Entity
{
    public string Owner { get; set; } = "";
    /// <summary>One of <see cref="Notify.NotifyEvents"/> (or "*" for every event).</summary>
    public string EventKind { get; set; } = "*";
    /// <summary>One of <see cref="Notify.NotifyChannels"/>.</summary>
    public string Channel { get; set; } = "INAPP";
    public bool Enabled { get; set; } = true;
    /// <summary>OK / DUE / CHECK / OVER - events below are not sent.</summary>
    public string MinSeverity { get; set; } = "CHECK";
    /// <summary>"22:00" .. "07:00" (empty = none). Wraps around midnight.</summary>
    public string QuietFrom { get; set; } = "";
    public string QuietTo { get; set; } = "";
    /// <summary>Channel address override (e-mail address, phone for WhatsApp ...). Empty = the channel's default.</summary>
    public string Address { get; set; } = "";
}

/// <summary>What was sent where (no repeats of the same event on the same channel; the brief once a day per user).</summary>
public sealed class NotificationLog : Entity
{
    public string Owner { get; set; } = "";
    public string EventKey { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime SentAt { get; set; }
    public string Status { get; set; } = "SENT";
    public string Error { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>The figures of a morning brief, kept so tomorrow's brief can say what changed since yesterday.</summary>
public sealed class BriefSnapshot : Entity
{
    public string Owner { get; set; } = "";
    public DateTime Date { get; set; }
    public DateTime BuiltAt { get; set; }
    public string MetricsJson { get; set; } = "{}";
    public string Summary { get; set; } = "";
}

public static class AssistantEntityTypes
{
    public static readonly Type[] All =
    {
        typeof(AssistantConversation), typeof(AssistantMessage), typeof(AssistantAction), typeof(AssistantReminder),
        typeof(NotificationRule), typeof(NotificationLog), typeof(BriefSnapshot),
    };
}
