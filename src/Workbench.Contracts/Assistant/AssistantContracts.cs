namespace Workbench.Contracts.Assistant;

/// <summary>The data-handling notice an administrator accepts before turning the assistant on.</summary>
public static class AssistantNotice
{
    /// <summary>Bump when the text changes materially; the assistant then needs a fresh acceptance.</summary>
    public const string Version = "2026-10";

    public const string Text =
        "When the assistant is on, the questions people type and the records the assistant reads to answer them (project "
        + "financials, budgets, earned value, ledger lines, timesheets, approvals, ceilings, funding, resourcing and sheet rows) "
        + "are sent to the AI provider you choose below, which processes them to produce the answer. The assistant reads only "
        + "what the person asking could already open in Workbench, it can't change anything, and every question, every record "
        + "it read and every answer is kept in the assistant audit log. Anthropic's API and Amazon Bedrock don't train models "
        + "on this data. For controlled unclassified information (CUI), use an Amazon Bedrock connection in an AWS GovCloud "
        + "(US) region, and check your contracts' data-handling clauses (for example DFARS 252.204-7012) before turning it on.";
}

public enum AssistantProviderKind
{
    Fake,
    Anthropic,
    Bedrock,
}

/// <summary>A connection the host configured (no secrets): what an administrator can pick for the organization.</summary>
public sealed record AssistantConnectionDto(
    string Name,
    AssistantProviderKind Provider,
    string Label,
    string? Region,
    bool IsGovCloud,
    string DefaultModel,
    IReadOnlyList<string> Models,
    string DataHandling);

public sealed record AssistantSettingsDto(
    bool IsEnabled,
    string? Connection,
    string? Model,
    long? MonthlyTokenLimit,
    long? MonthlyTokenLimitPerUser,
    int? DailyQuestionLimitPerUser,
    bool ExplainTimesheetFlags,
    string? NoticeAcceptedBy,
    DateTime? NoticeAcceptedAtUtc,
    bool NoticeIsCurrent,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    IReadOnlyList<AssistantConnectionDto> Connections);

/// <summary>
/// Saves the organization's settings (Admin). Turning the assistant on needs <see cref="AcceptNotice"/> (unless the
/// current notice was already accepted) and a configured <see cref="Connection"/>. Limits must be positive or null.
/// </summary>
public sealed record SaveAssistantSettingsRequest(
    bool IsEnabled,
    string? Connection,
    string? Model,
    long? MonthlyTokenLimit = null,
    long? MonthlyTokenLimitPerUser = null,
    int? DailyQuestionLimitPerUser = null,
    bool ExplainTimesheetFlags = true,
    bool AcceptNotice = false);

/// <summary>What the signed-in user's UI needs: whether to show the assistant and why it can't answer right now.</summary>
public sealed record AssistantStatusDto(
    bool IsEnabled,
    bool CanAsk,
    string? Reason,
    string? ProviderLabel,
    bool ExplainsTimesheetFlags,
    string DataNotice);

/// <summary>A page that shows the numbers an answer used. <see cref="Href"/> is always app-relative and built by Workbench.</summary>
public sealed record AssistantCitation(string Label, string Href);

/// <summary>A question from the panel. Questions in the same <see cref="ConversationId"/> see the earlier questions and answers.</summary>
public sealed record AskAssistantRequest(string Question, Guid? ConversationId = null, string? PagePath = null);

public enum AssistantEventKind
{
    /// <summary>The interaction started (<see cref="AssistantEvent.ConversationId"/> and <see cref="AssistantEvent.InteractionId"/> set).</summary>
    Started,

    /// <summary>More answer text (<see cref="AssistantEvent.Text"/>).</summary>
    Text,

    /// <summary>The assistant is reading something (<see cref="AssistantEvent.Text"/> says what, in plain words).</summary>
    Tool,

    /// <summary>The answer is complete (<see cref="AssistantEvent.Answer"/>).</summary>
    Completed,

    /// <summary>It couldn't answer (<see cref="AssistantEvent.Text"/> says why).</summary>
    Failed,
}

public sealed record AssistantEvent(
    AssistantEventKind Kind,
    string? Text = null,
    Guid? ConversationId = null,
    Guid? InteractionId = null,
    AssistantAnswerDto? Answer = null);

public sealed record AssistantTokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static AssistantTokenUsage Zero { get; } = new(0, 0, 0, 0);

    public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    public AssistantTokenUsage Add(AssistantTokenUsage other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, CacheReadTokens + other.CacheReadTokens, CacheWriteTokens + other.CacheWriteTokens);
}

public sealed record AssistantAnswerDto(
    Guid InteractionId,
    Guid ConversationId,
    string Answer,
    IReadOnlyList<AssistantCitation> Citations,
    IReadOnlyList<string> ToolsUsed,
    string Outcome,
    AssistantTokenUsage Usage);

/// <summary>
/// Ask the organization's data in plain English. Answers come from a fixed set of read-only tools that call Workbench's
/// permission-checked services as the signed-in user; the assistant has no write tools.
/// </summary>
public interface IAssistantService
{
    Task<AssistantStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams the answer. Never throws for refusals or limits: they arrive as a <see cref="AssistantEventKind.Failed"/> event.</summary>
    IAsyncEnumerable<AssistantEvent> AskAsync(AskAssistantRequest request, CancellationToken cancellationToken = default);
}

// ---- Administration ----

public sealed record AssistantUsageRowDto(string UserEmail, int Questions, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public long Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;
}

public sealed record AssistantUsageDto(int Year, int Month, long TotalTokens, long? MonthlyTokenLimit, long? MonthlyTokenLimitPerUser, IReadOnlyList<AssistantUsageRowDto> Users);

public sealed record AssistantAuditQuery(DateOnly? From = null, DateOnly? To = null, string? UserEmail = null, int Take = 200);

public sealed record AssistantToolCallDto(
    int Sequence,
    string ToolName,
    string? Arguments,
    string Outcome,
    string? RecordsRead,
    string? Summary,
    DateTime CalledAtUtc);

public sealed record AssistantInteractionDto(
    Guid Id,
    Guid ConversationId,
    string Kind,
    string UserEmail,
    string? PagePath,
    string Prompt,
    string? Answer,
    string Outcome,
    string? OutcomeDetail,
    string Provider,
    string? Model,
    AssistantTokenUsage Usage,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    IReadOnlyList<AssistantToolCallDto> ToolCalls);

/// <summary>The organization's assistant settings, usage and audit log. Administrators only.</summary>
public interface IAssistantAdminService
{
    Task<AssistantSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<AssistantSettingsDto> SaveSettingsAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tokens used per person in a calendar month (UTC; default: this month).</summary>
    Task<AssistantUsageDto> GetUsageAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default);

    /// <summary>Newest first, with the tools each question called and the records they read.</summary>
    Task<IReadOnlyList<AssistantInteractionDto>> ListAuditAsync(AssistantAuditQuery query, CancellationToken cancellationToken = default);
}
