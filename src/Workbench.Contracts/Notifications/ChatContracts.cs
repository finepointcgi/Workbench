namespace Workbench.Contracts.Notifications;

/// <summary>Chat products notifications can also be posted to. Slack can be added next without changing callers.</summary>
public static class ChatChannelKinds
{
    public const string Teams = "teams";
}

/// <summary>The few groups people choose from, instead of one switch per kind of notification.</summary>
public static class NotificationGroups
{
    public const string Approvals = "approvals";
    public const string Decisions = "decisions";
    public const string Assignments = "assignments";
    public const string Timesheets = "timesheets";
    public const string Other = "other";

    public static readonly IReadOnlyList<NotificationGroupDto> All =
    [
        new(Approvals, "Waiting for my approval", "Timesheets, expenses, bills and requests that need your decision."),
        new(Decisions, "Decisions on my requests", "Approved, returned or rejected."),
        new(Assignments, "Assigned to me and @mentions", "Tasks and comments that name you."),
        new(Timesheets, "Timesheet reminders", "Missing or reopened timesheets."),
        new(Other, "Everything else", "Rule alerts and other updates."),
    ];

    /// <summary>Which group a notification kind belongs to (kinds are free-form strings, so this goes by their wording).</summary>
    public static string For(string kind)
    {
        var k = kind.ToLowerInvariant();
        if (k.StartsWith("timesheet.missing", StringComparison.Ordinal) || k.StartsWith("timesheet.reopened", StringComparison.Ordinal))
        {
            return Timesheets;
        }

        if (k.Contains("approved", StringComparison.Ordinal) || k.Contains("rejected", StringComparison.Ordinal) || k.Contains("returned", StringComparison.Ordinal)
            || k.Contains("decided", StringComparison.Ordinal) || k.Contains("cancelled", StringComparison.Ordinal))
        {
            return Decisions;
        }

        if (k.Contains("approval", StringComparison.Ordinal) || k.EndsWith(".requested", StringComparison.Ordinal) || k.EndsWith("_requested", StringComparison.Ordinal)
            || k.EndsWith(".submitted", StringComparison.Ordinal))
        {
            return Approvals;
        }

        if (k.StartsWith("mention", StringComparison.Ordinal) || k.Contains("assign", StringComparison.Ordinal) || k.EndsWith(".task", StringComparison.Ordinal))
        {
            return Assignments;
        }

        return Other;
    }
}

public sealed record NotificationGroupDto(string Key, string Label, string Hint);

/// <summary>What an administrator sees: whether a channel is connected and where (the host only; the full address is a secret and never shown).</summary>
public sealed record ChatChannelDto(string Kind, bool Connected, bool IsActive, string? Host, string? LastError, DateTime? LastPostedAtUtc, DateTime? UpdatedAtUtc);

/// <summary>A person's choice for one group.</summary>
public sealed record ChatGroupChoiceDto(string Group, string Label, string Hint, bool Enabled);

/// <summary>The signed-in person's Teams choices. <see cref="Available"/> is false until an administrator connects a channel.</summary>
public sealed record ChatChoicesDto(bool Available, IReadOnlyList<ChatGroupChoiceDto> Groups);

public sealed record ConnectChatChannelRequest(string WebhookUrl);

public sealed record SaveChatChoicesRequest(IReadOnlyList<string> Groups);

/// <summary>Connect a Teams channel (administrators) and choose what also goes there (everyone).</summary>
public interface ITeamsNotificationService
{
    Task<ChatChannelDto> GetChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the channel's incoming-webhook address after checking it points at a Microsoft host.</summary>
    Task<ChatChannelDto> ConnectAsync(ConnectChatChannelRequest request, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Posts "Workbench is connected" so the administrator sees it arrive. Returns the channel with the outcome in <see cref="ChatChannelDto.LastError"/>.</summary>
    Task<ChatChannelDto> SendTestAsync(CancellationToken cancellationToken = default);

    Task<ChatChoicesDto> GetMineAsync(CancellationToken cancellationToken = default);

    Task<ChatChoicesDto> SetMineAsync(SaveChatChoicesRequest request, CancellationToken cancellationToken = default);
}
