namespace Workbench.Domain.Security;

/// <summary>
/// An invitation to join Workbench. Accepted on the invitee's first sign-in with the same email, before
/// <see cref="ExpiresAtUtc"/>. Kept after acceptance or expiry as a record of who granted access.
/// </summary>
public sealed class UserInvite : Entity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>The built-in roles it grants (<c>OrgRoles</c> flags).</summary>
    public int Roles { get; set; }

    /// <summary>Custom roles it grants: comma-separated role ids.</summary>
    public string? CustomRoleIds { get; set; }

    public required string InvitedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>
/// Append-only security event (ISO 27001 A.8.15): sign-ins, user lifecycle, permission denials, sensitive exports,
/// period close and admin settings. Never updated; deleted only by the retention job after the retention period.
/// </summary>
public sealed class SecurityEvent : Entity
{
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>A <c>SecurityEventTypes</c> constant, e.g. <c>signin.success</c>.</summary>
    public required string EventType { get; set; }

    /// <summary>Who did it: an email, <c>scim</c>, <c>system</c>, or <c>anonymous</c>.</summary>
    public required string Actor { get; set; }

    /// <summary>What it was done to: a user's email, a path, a project code. Optional.</summary>
    public string? Target { get; set; }

    /// <summary><c>success</c>, <c>failure</c> or <c>denied</c>.</summary>
    public required string Outcome { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Short human-readable detail. Never secrets, tokens or cost rates.</summary>
    public string? Detail { get; set; }
}

public enum AccessReviewStatus
{
    InProgress,
    Completed,
}

public enum AccessReviewDecision
{
    Pending,
    Keep,
    Revoke,
}

/// <summary>A periodic review of who has access (A.5.18). Completed reviews are evidence and are never changed.</summary>
public sealed class AccessReview : Entity
{
    public required string Name { get; set; }
    public AccessReviewStatus Status { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Notes { get; set; }
    public List<AccessReviewItem> Items { get; } = [];
}

/// <summary>One user in an access review: a snapshot of their access when the review started, and the decision.</summary>
public sealed class AccessReviewItem : Entity
{
    public Guid AccessReviewId { get; set; }
    public required string UserEmail { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>Roles (built-in and custom) at the start of the review, e.g. "Admin, Finance, Approvers" or "Employee".</summary>
    public required string OrgRoles { get; set; }

    /// <summary>The effective permissions those roles grant at the start of the review (catalog keys, comma-separated).</summary>
    public string? Permissions { get; set; }

    /// <summary>Changes to the user's roles in the review period (since the previous review started, at most 90 days back).</summary>
    public string? RoleChanges { get; set; }

    /// <summary>Workspace memberships at the start, e.g. "Program Office: Owner; Finance: Viewer".</summary>
    public required string WorkspaceRoles { get; set; }

    public DateTime? LastSignInAtUtc { get; set; }
    public string? EmployeeName { get; set; }

    /// <summary>No sign-in in the 90 days before the review started.</summary>
    public bool Dormant { get; set; }

    public AccessReviewDecision Decision { get; set; }
    public string? Comment { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>When a Revoke decision was applied (the user deactivated).</summary>
    public DateTime? AppliedAtUtc { get; set; }
}

/// <summary>An admin's override of a record type's retention period (defaults come from configuration).</summary>
public sealed class RetentionPolicy : Entity
{
    /// <summary>A <c>RetentionRecordTypes</c> constant.</summary>
    public required string RecordType { get; set; }

    public int RetentionDays { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A legal hold on a contract or project: nothing related to it may be deleted or purged until it's released.
/// </summary>
public sealed class LegalHold : Entity
{
    /// <summary><c>Contract</c> or <c>Project</c>.</summary>
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>Contract number or project code, for display.</summary>
    public required string Label { get; set; }

    public required string Reason { get; set; }
    public required string PlacedBy { get; set; }
    public DateTime PlacedAtUtc { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }

    public bool IsActive => ReleasedAtUtc is null;
}

/// <summary>An ASP.NET Core Data Protection key, stored as the XML the key manager writes.</summary>
public sealed class DataProtectionKeyRecord : Entity
{
    public string? FriendlyName { get; set; }
    public required string Xml { get; set; }
}
