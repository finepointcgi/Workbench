namespace Workbench.Domain.Support;

/// <summary>
/// A problem report or piece of feedback sent from inside the app. It holds only what the person typed plus the page
/// path and app version: never form values, query strings or anything else from the page.
/// </summary>
public sealed class SupportRequest : Entity
{
    /// <summary>A short, human number (SR-000001), unique per organization.</summary>
    public int Number { get; set; }

    public SupportRequestKind Kind { get; set; } = SupportRequestKind.Problem;

    public SupportRequestStatus Status { get; set; } = SupportRequestStatus.New;

    public required string Summary { get; set; }

    public string? Details { get; set; }

    /// <summary>The app-relative page path the person was on, without query string or fragment.</summary>
    public string? PagePath { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>Set when the report started from an error screen.</summary>
    public bool FromError { get; set; }

    public required string ReporterEmail { get; set; }

    public string? ReporterName { get; set; }

    public string? ScreenshotKey { get; set; }

    public string? ScreenshotFileName { get; set; }

    public string? ScreenshotContentType { get; set; }

    public long? ScreenshotBytes { get; set; }

    /// <summary>What the administrator who handled it wrote back.</summary>
    public string? Resolution { get; set; }

    public string? HandledBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

public enum SupportRequestKind
{
    Problem,
    Feedback,
}

public enum SupportRequestStatus
{
    New,
    InProgress,
    Resolved,
    Closed,
}

/// <summary>Per-user help state: which welcome tours are done and the last "What's new" entry seen.</summary>
public sealed class UserHelpState : Entity
{
    /// <summary>Lower-case email; one row per user.</summary>
    public required string UserEmail { get; set; }

    /// <summary>Comma-separated tour keys the user finished or dismissed (e.g. "employee,finance").</summary>
    public string CompletedTours { get; set; } = "";

    public string? WhatsNewSeen { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
