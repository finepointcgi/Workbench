namespace Workbench.Domain.Reports;

public enum SavedReportVisibility
{
    /// <summary>Only the owner (and administrators, who can open anything) see it.</summary>
    Private,

    /// <summary>Everyone holding one of <see cref="SavedReport.SharedWith"/> sees it (read and run; only the owner edits).</summary>
    Shared,
}

/// <summary>
/// A named report: either a standard report with saved parameters (<see cref="StandardKey"/>) or a report-writer
/// definition over a curated data source. The definition is JSON (the contract records); it never holds SQL. Running it
/// always applies the runner's own permissions, so sharing a report never shares data the reader can't see.
/// </summary>
public sealed class SavedReport : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Lower-case email of the user who created it; only they (or an administrator) change or delete it.</summary>
    public required string OwnerEmail { get; set; }

    public SavedReportVisibility Visibility { get; set; } = SavedReportVisibility.Private;

    /// <summary>
    /// Comma-separated organization role names the report is shared with (<c>Employee</c> = everyone signed in,
    /// <c>Timekeeper</c>, <c>Finance</c>, <c>Admin</c>, <c>ResourceManager</c>). Ignored while private.
    /// </summary>
    public string? SharedWith { get; set; }

    /// <summary>Set for a saved standard report (e.g. <c>project-status</c>); null for a report-writer report.</summary>
    public string? StandardKey { get; set; }

    /// <summary>JSON: the standard report's parameters, or the report-writer definition.</summary>
    public required string DefinitionJson { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ReportScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

public enum ReportDelivery
{
    /// <summary>The owner gets the report as an attached file.</summary>
    Attachment,

    /// <summary>Everyone gets a link; the report runs with the reader's permissions when they open it.</summary>
    Link,
}

/// <summary>
/// Emails a saved report on a schedule. The run happens as <see cref="OwnerEmail"/> with that user's permissions at send
/// time (never as the Workbench System); a deactivated owner's schedule stops. The attachment only goes to the owner;
/// additional recipients (active users who can see the report) get a link.
/// </summary>
public sealed class ReportSchedule : Entity
{
    public Guid SavedReportId { get; set; }

    /// <summary>The user the report runs as; whoever created the schedule.</summary>
    public required string OwnerEmail { get; set; }

    public ReportScheduleFrequency Frequency { get; set; }

    /// <summary>Weekly: the day it goes out.</summary>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>Monthly: 1–28 (shorter months never skip a run).</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Hour of the day (UTC, 0–23) it goes out.</summary>
    public int HourUtc { get; set; } = 13;

    public ReportDelivery Delivery { get; set; } = ReportDelivery.Attachment;

    /// <summary><c>Xlsx</c> or <c>Csv</c>.</summary>
    public required string Format { get; set; }

    /// <summary>Comma-separated lower-case emails who get a link (never the attachment).</summary>
    public string? AdditionalRecipients { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>When it next goes out. Advanced in the same unit of work that records a run, so a run never repeats.</summary>
    public DateTime NextRunUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
