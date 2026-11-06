namespace Workbench.Domain.Assistant;

/// <summary>
/// The organization's AI assistant settings (one row per tenant database). Off until an administrator turns it on and
/// accepts the data-handling notice. Credentials never live here: <see cref="Connection"/> names a connection the host
/// configured (<c>Assistant:Connections:{name}</c>), whose key or AWS role is in configuration or secrets.
/// </summary>
public sealed class AssistantSettings : Entity
{
    public bool IsEnabled { get; set; }

    /// <summary>The configured connection to use (for example <c>anthropic</c> or <c>bedrock-govcloud</c>).</summary>
    public string? Connection { get; set; }

    /// <summary>The model id; null uses the connection's default.</summary>
    public string? Model { get; set; }

    /// <summary>Tokens (input + output) the whole organization may use per calendar month (UTC); null is unlimited.</summary>
    public long? MonthlyTokenLimit { get; set; }

    /// <summary>Tokens one person may use per calendar month (UTC); null is unlimited.</summary>
    public long? MonthlyTokenLimitPerUser { get; set; }

    /// <summary>Questions one person may ask per day (UTC); null is unlimited.</summary>
    public int? DailyQuestionLimitPerUser { get; set; }

    /// <summary>When true, supervisors and Finance can ask the assistant to explain timesheet flags in plain language.</summary>
    public bool ExplainTimesheetFlags { get; set; } = true;

    /// <summary>Who accepted the data-handling notice (required to turn the assistant on), and when.</summary>
    public string? NoticeAcceptedBy { get; set; }

    public DateTime? NoticeAcceptedAtUtc { get; set; }

    /// <summary>The notice version that was accepted; a new version must be accepted again.</summary>
    public string? NoticeVersion { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}

public enum AssistantInteractionKind
{
    Question,
    Narrative,
    FlagExplanation,
    ReceiptReading,
    LeadFit,
    ProposalDraft,
}

public enum AssistantOutcome
{
    Answered,
    Refused,
    LimitReached,
    Disabled,
    Failed,
}

/// <summary>
/// One request to the assistant: who asked what, when, which provider and model answered, the answer, and the tokens it
/// used. Append-only (the assistant audit log); the tool calls it made are <see cref="AssistantToolCall"/> rows.
/// </summary>
public sealed class AssistantInteraction : Entity
{
    /// <summary>Groups the questions of one conversation in the panel.</summary>
    public Guid ConversationId { get; set; }

    public AssistantInteractionKind Kind { get; set; }

    public required string UserEmail { get; set; }

    /// <summary>The app-relative page the question was asked on (no query string).</summary>
    public string? PagePath { get; set; }

    public required string Prompt { get; set; }

    public string? Answer { get; set; }

    public AssistantOutcome Outcome { get; set; }

    /// <summary>Short reason for a refusal or failure (never a secret or a stack trace).</summary>
    public string? OutcomeDetail { get; set; }

    public required string Provider { get; set; }

    public string? Model { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long CacheReadTokens { get; set; }

    public long CacheWriteTokens { get; set; }

    public int ToolCallCount { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

public enum AssistantToolOutcome
{
    Ok,
    Denied,
    NotFound,
    Invalid,
    Failed,
}

/// <summary>A read-only tool the assistant called while answering, as the signed-in user, and the records it read.</summary>
public sealed class AssistantToolCall : Entity
{
    public Guid InteractionId { get; set; }

    public int Sequence { get; set; }

    public required string ToolName { get; set; }

    /// <summary>The arguments as JSON (clipped).</summary>
    public string? Arguments { get; set; }

    public AssistantToolOutcome Outcome { get; set; }

    /// <summary>The records read, as <c>Type:id</c> pairs separated by spaces (clipped).</summary>
    public string? RecordsRead { get; set; }

    public string? Summary { get; set; }

    public DateTime CalledAtUtc { get; set; }
}
