using Workbench.Contracts.Identity;

namespace Workbench.Contracts.Security;

// ---- Users ----

/// <summary>A user as administrators see them. <see cref="EmployeeId"/> is the effective link (explicit or by email).</summary>
public sealed record UserAdminDto(
    Guid Id,
    string Email,
    string DisplayName,
    OrgRoles Roles,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc,
    DateTime? LastSignInAtUtc,
    DateTime? DeactivatedAtUtc,
    string? DeactivatedBy,
    Guid? EmployeeId,
    string? EmployeeName,
    EmployeeLinkMode EmployeeLink,
    bool ScimManaged,
    IReadOnlyList<RoleRef>? AssignedRoles = null)
{
    public bool IsAdmin => Roles.HasFlag(OrgRoles.Admin);
}

public enum EmployeeLinkMode
{
    /// <summary>Linked to the employee with the same email, if there is one (the default).</summary>
    ByEmail,

    /// <summary>Linked by an administrator to a specific employee.</summary>
    Explicit,

    /// <summary>Unlinked by an administrator: no employee, even with a matching email.</summary>
    Unlinked,
}

/// <summary>An employee an administrator can link a user to.</summary>
public sealed record LinkableEmployeeDto(Guid Id, string EmployeeNumber, string DisplayName, string Email);

/// <summary>
/// User lifecycle for administrators. Every change writes a security event. Nobody can remove their own Admin role
/// or deactivate themselves, and the last active Admin can't lose the role or be deactivated (both
/// <see cref="ArgumentException"/>).
/// </summary>
public interface IUserAdminService
{
    /// <summary>Users whose email or name contains <paramref name="query"/>, active first, then by name.</summary>
    Task<IReadOnlyList<UserAdminDto>> ListUsersAsync(string? query = null, bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<UserAdminDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default);

    /// <summary>Deactivates the user and ends their sessions. Their history stays.</summary>
    Task<UserAdminDto> DeactivateAsync(string email, string? reason = null, CancellationToken cancellationToken = default);

    Task<UserAdminDto> ReactivateAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Ends every session and rejects every token issued before now; the user can sign in again.</summary>
    Task<UserAdminDto> SignOutEverywhereAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Links the user to an employee, or with <paramref name="mode"/> Unlinked/ByEmail, unlinks or restores the email match.</summary>
    Task<UserAdminDto> SetEmployeeLinkAsync(string email, EmployeeLinkMode mode, Guid? employeeId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinkableEmployeeDto>> ListLinkableEmployeesAsync(CancellationToken cancellationToken = default);
}

// ---- Invites ----

public enum InviteStatus
{
    Pending,
    Accepted,
    Expired,
    Revoked,
}

public sealed record InviteDto(
    Guid Id,
    string Email,
    string DisplayName,
    OrgRoles Roles,
    string InvitedBy,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? AcceptedAtUtc,
    DateTime? RevokedAtUtc,
    InviteStatus Status,
    IReadOnlyList<RoleRef>? CustomRoles = null);

/// <summary>An invite. <paramref name="CustomRoleIds"/> are custom roles granted on top of the built-in <paramref name="Roles"/>.</summary>
public sealed record CreateInviteRequest(string Email, string DisplayName, OrgRoles Roles, IReadOnlyList<Guid>? CustomRoleIds = null);

/// <summary>Invitations. An invite expires after 7 days and is accepted on the first sign-in with its email.</summary>
public interface IInviteService
{
    Task<IReadOnlyList<InviteDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a pending invite (replacing any pending one for the same email). Admins only.</summary>
    Task<InviteDto> CreateAsync(CreateInviteRequest request, CancellationToken cancellationToken = default);

    Task<InviteDto> RevokeAsync(Guid inviteId, CancellationToken cancellationToken = default);
}

// ---- Access reviews ----

public enum AccessReviewDecisionKind
{
    Pending,
    Keep,
    Revoke,
}

public sealed record AccessReviewSummaryDto(
    Guid Id,
    string Name,
    bool IsCompleted,
    string StartedBy,
    DateTime StartedAtUtc,
    string? CompletedBy,
    DateTime? CompletedAtUtc,
    int UserCount,
    int Decided,
    int Revoked);

public sealed record AccessReviewItemDto(
    Guid Id,
    string UserEmail,
    string DisplayName,
    string OrgRoles,
    string WorkspaceRoles,
    DateTime? LastSignInAtUtc,
    string? EmployeeName,
    bool Dormant,
    AccessReviewDecisionKind Decision,
    string? Comment,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    DateTime? AppliedAtUtc,
    string? Permissions = null,
    string? RoleChanges = null);

/// <summary>A review. <paramref name="RoleChanges"/> lists changes to role definitions in the review period, for the export.</summary>
public sealed record AccessReviewDto(AccessReviewSummaryDto Summary, string? Notes, IReadOnlyList<AccessReviewItemDto> Items, IReadOnlyList<string>? RoleChanges = null);

public sealed record AccessReviewDecisionRequest(AccessReviewDecisionKind Decision, string? Comment);

/// <summary>
/// Quarterly access reviews (ISO 27001 A.5.18). Starting a review snapshots every active user's access. A Revoke
/// decision deactivates the user at once. Completed reviews are kept unchanged as evidence.
/// </summary>
public interface IAccessReviewService
{
    Task<IReadOnlyList<AccessReviewSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<AccessReviewDto> GetAsync(Guid reviewId, CancellationToken cancellationToken = default);

    /// <summary>Starts a review named e.g. "2026 Q4". Only one review can be in progress.</summary>
    Task<AccessReviewDto> StartAsync(string? name = null, CancellationToken cancellationToken = default);

    Task<AccessReviewItemDto> DecideAsync(Guid reviewId, Guid itemId, AccessReviewDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Completes the review. Every user needs a decision; Revoke needs a comment.</summary>
    Task<AccessReviewDto> CompleteAsync(Guid reviewId, string? notes = null, CancellationToken cancellationToken = default);
}

// ---- Security event log ----

public sealed record SecurityEventDto(
    Guid Id,
    DateTime OccurredAtUtc,
    string EventType,
    string Actor,
    string? Target,
    string Outcome,
    string? IpAddress,
    string? UserAgent,
    string? Detail);

public sealed record SecurityEventQuery(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? EventType = null,
    string? Actor = null,
    string? Outcome = null,
    string? Text = null,
    int Limit = 500);

public sealed record SecurityEventExport(string FileName, string ContentType, string Content);

/// <summary>Reads the append-only security event log (ISO 27001 A.8.15). Admins only; exporting is itself logged.</summary>
public interface ISecurityEventService
{
    Task<IReadOnlyList<SecurityEventDto>> SearchAsync(SecurityEventQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListEventTypesAsync(CancellationToken cancellationToken = default);

    Task<SecurityEventExport> ExportCsvAsync(SecurityEventQuery query, CancellationToken cancellationToken = default);
}

// ---- Retention and legal holds ----

/// <summary>
/// A record type's retention. Protected types (time, ODCs, journal, audit, access reviews) are never deleted
/// automatically; the job only reports what has passed its retention period.
/// </summary>
public sealed record RetentionPolicyDto(
    string RecordType,
    string Description,
    int RetentionDays,
    bool Protected,
    bool AfterContractClose,
    bool IsOverride);

public sealed record RetentionReportLine(string RecordType, bool Protected, int Eligible, int Purged, int Held, string Note);

public sealed record RetentionReportDto(DateTime RanAtUtc, bool DryRun, IReadOnlyList<RetentionReportLine> Lines);

public sealed record LegalHoldDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Label,
    string Reason,
    string PlacedBy,
    DateTime PlacedAtUtc,
    string? ReleasedBy,
    DateTime? ReleasedAtUtc);

public sealed record PlaceLegalHoldRequest(string EntityType, Guid EntityId, string Reason);

/// <summary>A contract or project that can be placed on legal hold.</summary>
public sealed record HoldableRecordDto(string EntityType, Guid Id, string Label);

/// <summary>Records retention (ISO 27001 A.5.33, A.8.10) and legal holds. Admins only.</summary>
public interface IRetentionService
{
    Task<IReadOnlyList<RetentionPolicyDto>> ListPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Changes a record type's retention period. Protected types can only be lengthened.</summary>
    Task<RetentionPolicyDto> SetPolicyAsync(string recordType, int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>What would be purged or is eligible, without deleting anything.</summary>
    Task<RetentionReportDto> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the purge now (it also runs nightly).</summary>
    Task<RetentionReportDto> RunNowAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LegalHoldDto>> ListHoldsAsync(bool includeReleased = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HoldableRecordDto>> ListHoldableAsync(CancellationToken cancellationToken = default);

    Task<LegalHoldDto> PlaceHoldAsync(PlaceLegalHoldRequest request, CancellationToken cancellationToken = default);

    Task<LegalHoldDto> ReleaseHoldAsync(Guid holdId, string reason, CancellationToken cancellationToken = default);

    /// <summary>True when the contract or project (or a project's contract) is on legal hold. Deletion paths must check it.</summary>
    Task<bool> IsOnHoldAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default);
}
