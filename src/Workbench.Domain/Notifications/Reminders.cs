namespace Workbench.Domain.Notifications;

/// <summary>
/// How one person wants to be reminded. No row means the defaults: both reminders on, by email only, no quiet hours.
/// Quiet hours are local hours (0-23) in the organization's time zone; a start after the end spans midnight (18 to 9).
/// </summary>
public sealed class ReminderPreference : Entity
{
    /// <summary>Lower-case.</summary>
    public required string UserEmail { get; set; }

    /// <summary>"Your timesheet isn't in".</summary>
    public bool TimesheetEnabled { get; set; } = true;

    /// <summary>The daily "waiting on you" approvals digest.</summary>
    public bool ApprovalsEnabled { get; set; } = true;

    public bool ViaEmail { get; set; } = true;

    public bool ViaTeams { get; set; }

    public int? QuietStartHour { get; set; }

    public int? QuietEndHour { get; set; }
}

/// <summary>
/// One reminder that went out, so a restart (or a second server) never sends it twice. <see cref="Key"/> names the occasion
/// ("2026-10-12:due", "2026-10-28"); the pair of <see cref="UserEmail"/>, <see cref="Kind"/> and <see cref="Key"/> is unique.
/// The row is written before the message is sent, so a crash in between loses a reminder rather than repeating it.
/// </summary>
public sealed class ReminderLog : Entity
{
    public required string UserEmail { get; set; }

    /// <summary><c>timesheet</c> or <c>approvals</c>.</summary>
    public required string Kind { get; set; }

    public required string Key { get; set; }

    /// <summary>The local day it was sent (at most one reminder of a kind per person per day).</summary>
    public DateOnly SentOn { get; set; }

    public DateTime SentAtUtc { get; set; }
}
