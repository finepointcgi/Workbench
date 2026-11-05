namespace Workbench.Contracts.Notifications;

/// <summary>Well-known reminder kinds (they are also notification kinds, so the bell can show them).</summary>
public static class ReminderKinds
{
    /// <summary>"Your timesheet isn't in".</summary>
    public const string Timesheet = "reminder.timesheet";

    /// <summary>The daily "waiting on you" digest for approvers.</summary>
    public const string Approvals = "reminder.approvals";
}

/// <summary>
/// The signed-in person's reminder choices. Defaults: both reminders on, email only, no quiet hours.
/// Quiet hours are local hours (0-23); no reminder is sent from the start hour until the end hour (18 to 9 spans midnight).
/// </summary>
public sealed record ReminderSettingsDto(
    bool TimesheetEnabled,
    bool ApprovalsEnabled,
    bool ViaEmail,
    bool ViaTeams,
    int? QuietStartHour,
    int? QuietEndHour,
    bool TeamsAvailable,
    bool ViaSlack = false,
    bool SlackAvailable = false);

public sealed record SaveReminderSettingsRequest(
    bool TimesheetEnabled,
    bool ApprovalsEnabled,
    bool ViaEmail,
    bool ViaTeams,
    int? QuietStartHour,
    int? QuietEndHour,
    bool ViaSlack = false);

/// <summary>Each person chooses which reminders they get, where, and when not to be disturbed.</summary>
public interface IReminderSettingsService
{
    Task<ReminderSettingsDto> GetMineAsync(CancellationToken cancellationToken = default);

    Task<ReminderSettingsDto> SetMineAsync(SaveReminderSettingsRequest request, CancellationToken cancellationToken = default);
}
