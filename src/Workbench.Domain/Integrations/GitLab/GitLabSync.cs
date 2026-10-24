namespace Workbench.Domain.Integrations.GitLab;

/// <summary>
/// The organization's GitLab server (gitlab.com or self-managed) and the access token Workbench uses for it. One per
/// tenant. The token is encrypted with Data Protection (<c>Workbench.Integrations.GitLab.v1</c>) and never shown again.
/// </summary>
public sealed class GitLabConnection : Entity
{
    /// <summary>The server's address without <c>/api/v4</c>, e.g. <c>https://gitlab.com</c> or <c>https://git.example.mil/gitlab</c>.</summary>
    public required string BaseUrl { get; set; }

    public required string TokenProtected { get; set; }

    /// <summary>The token's last four characters, so admins can tell which one is saved.</summary>
    public required string TokenHint { get; set; }

    /// <summary>
    /// PEM certificates of the private certificate authority a self-managed server's HTTPS certificate is issued by
    /// (air-gapped and internal servers). Public, not a secret. Null: the system's trusted authorities.
    /// </summary>
    public string? CaCertificatePem { get; set; }

    public bool IsActive { get; set; } = true;

    public string? ConnectedAsUsername { get; set; }
    public string? ConnectedAsName { get; set; }
    public string? ServerVersion { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public bool? LastTestOk { get; set; }
    public string? LastTestMessage { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>What a project's phases (summary tasks) become in GitLab.</summary>
public enum GitLabPhaseMode
{
    /// <summary>Group epics (GitLab Premium and Ultimate), nested like the plan.</summary>
    Epics,

    /// <summary>Project milestones (every tier). Flat: each detail task's issue gets its nearest phase's milestone.</summary>
    Milestones,
}

public enum GitLabLinkStatus
{
    Active,

    /// <summary>The last sync failed in a way that needs a person (a revoked token, a deleted GitLab project).</summary>
    NeedsAttention,
}

/// <summary>A Workbench project's plan linked to one GitLab project (and its group, for epics).</summary>
public sealed class GitLabProjectLink : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>The project's plan sheet when it was linked.</summary>
    public Guid SheetId { get; set; }

    public long GitLabProjectId { get; set; }
    public required string GitLabProjectPath { get; set; }
    public required string GitLabProjectUrl { get; set; }

    /// <summary>The group epics live in (the project's namespace when it's a group).</summary>
    public long? GitLabGroupId { get; set; }

    public string? GitLabGroupPath { get; set; }

    public GitLabPhaseMode PhaseMode { get; set; }

    /// <summary>How many outline levels of summary tasks become phases: 1 = top-level summary tasks only, 0 = every summary task.</summary>
    public int PhaseLevels { get; set; } = 1;

    /// <summary>The tier allows several assignees per issue (Premium and Ultimate); otherwise only the first is sent.</summary>
    public bool MultipleAssignees { get; set; }

    /// <summary>The tier allows "blocks" issue links (Premium and Ultimate); otherwise dependencies are sent as "relates to".</summary>
    public bool BlockingLinks { get; set; }

    /// <summary>The secret GitLab sends in <c>X-Gitlab-Token</c> with this link's webhooks, encrypted.</summary>
    public required string WebhookSecretProtected { get; set; }

    /// <summary>The project webhook Workbench registered in GitLab, when the token was allowed to.</summary>
    public long? HookId { get; set; }

    /// <summary>The group webhook for epic events, when registered.</summary>
    public long? GroupHookId { get; set; }

    public GitLabLinkStatus Status { get; set; } = GitLabLinkStatus.Active;
    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? LastErrorHint { get; set; }

    /// <summary>The reconcile reads issues updated after this (GitLab's clock, less a minute of overlap).</summary>
    public DateTime? ReconcileCursorUtc { get; set; }

    public DateTime? LastReconcileAtUtc { get; set; }

    /// <summary>Changes whenever a reconcile is claimed, so two hosts never run the same one.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum GitLabObjectKind
{
    Issue,
    Epic,
    Milestone,
}

/// <summary>
/// One plan task and the GitLab issue, epic or milestone it is synced with, plus the values both sides agreed on at the
/// last sync (the "base"): a field that differs from the base changed on that side since, which is how a change is told
/// apart from an echo and how two edits are detected.
/// </summary>
public sealed class GitLabItemLink : Entity
{
    public Guid LinkId { get; set; }

    /// <summary>The plan task (no foreign key: the link outlives a deleted task so its issue can be closed).</summary>
    public Guid ItemId { get; set; }

    public GitLabObjectKind Kind { get; set; }

    /// <summary>GitLab's global id of the issue, epic or milestone.</summary>
    public long GitLabId { get; set; }

    /// <summary>The number people see: issue #12, epic &amp;3, milestone %4.</summary>
    public long Iid { get; set; }

    public required string WebUrl { get; set; }

    public string? BaseTitle { get; set; }
    public string? BaseDescription { get; set; }

    /// <summary>GitLab user ids, sorted, comma-separated.</summary>
    public string? BaseAssignees { get; set; }

    public DateOnly? BaseStart { get; set; }
    public DateOnly? BaseDue { get; set; }

    /// <summary><c>opened</c> or <c>closed</c> (milestones: <c>active</c> or <c>closed</c>).</summary>
    public string? BaseState { get; set; }

    public string? BaseWbsCode { get; set; }
    public decimal? BaseEstimateHours { get; set; }

    /// <summary>The epic or milestone id the issue (or child epic) was last put under.</summary>
    public long? BaseParentId { get; set; }

    /// <summary>Issue numbers this one was linked to as blocking (dependencies), sorted, comma-separated.</summary>
    public string? BaseBlocks { get; set; }

    /// <summary>The task's % complete as GitLab last set it (so a Workbench edit of it can be told apart).</summary>
    public decimal? BasePercent { get; set; }

    /// <summary>% complete before GitLab closed the issue, restored if it is reopened.</summary>
    public decimal? PercentBeforeClose { get; set; }

    /// <summary>GitLab's <c>updated_at</c> at the last sync.</summary>
    public DateTime? GitLabUpdatedAtUtc { get; set; }

    /// <summary>The task's <c>ModifiedAtUtc</c> at the last sync.</summary>
    public DateTime? WorkbenchModifiedAtUtc { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>The task was deleted in Workbench and the issue closed.</summary>
    public bool IsRemoved { get; set; }

    public Guid Version { get; set; } = Guid.NewGuid();
}

/// <summary>A Workbench person matched to a GitLab user (by email, by user name, or by hand on the mapping screen).</summary>
public sealed class GitLabUserMap : Entity
{
    /// <summary>Lower-case email of the Workbench person.</summary>
    public required string Email { get; set; }

    public long GitLabUserId { get; set; }
    public required string GitLabUsername { get; set; }
    public string? GitLabName { get; set; }

    /// <summary><c>email</c>, <c>username</c> or <c>manual</c>.</summary>
    public required string MatchedBy { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum GitLabConflictKind
{
    /// <summary>GitLab changed a field Workbench owns (dates, WBS, hours, dependencies); Workbench's value was put back.</summary>
    WorkbenchOwns,

    /// <summary>Workbench changed a field GitLab owns (open/closed, % complete); GitLab's value was put back.</summary>
    GitLabOwns,

    /// <summary>Both sides changed a shared field (title, description, assignees) since the last sync; the later edit was kept.</summary>
    BothChanged,

    /// <summary>A person on one side has no match on the other.</summary>
    PersonNotMatched,

    /// <summary>A change couldn't be sent and won't be retried until someone fixes the cause.</summary>
    SyncFailed,
}

public enum GitLabConflictStatus
{
    Open,
    Resolved,
    Dismissed,
}

/// <summary>A sync decision someone should know about: shown on the project's GitLab panel until it's resolved or dismissed.</summary>
public sealed class GitLabSyncConflict : Entity
{
    public Guid LinkId { get; set; }
    public Guid? ItemId { get; set; }
    public long? Iid { get; set; }
    public GitLabObjectKind? ObjectKind { get; set; }
    public GitLabConflictKind Kind { get; set; }

    /// <summary>The field's key: title, description, assignees, start, due, state, percent, wbs, estimate, parent, person.</summary>
    public required string Field { get; set; }

    public string? WorkbenchValue { get; set; }
    public string? GitLabValue { get; set; }

    /// <summary>What Workbench did, in plain words.</summary>
    public required string Outcome { get; set; }

    public string? Hint { get; set; }
    public GitLabConflictStatus Status { get; set; } = GitLabConflictStatus.Open;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? Resolution { get; set; }
}

public enum GitLabPendingStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>An issue someone added in GitLab under a linked epic or milestone, waiting for approval to become a plan task.</summary>
public sealed class GitLabPendingIssue : Entity
{
    public Guid LinkId { get; set; }
    public long IssueId { get; set; }
    public long Iid { get; set; }
    public required string Title { get; set; }
    public required string WebUrl { get; set; }

    /// <summary>The phase task it would go under.</summary>
    public Guid? PhaseItemId { get; set; }

    public string? Author { get; set; }
    public GitLabPendingStatus Status { get; set; } = GitLabPendingStatus.Pending;
    public DateTime SeenAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }

    /// <summary>The task it became, once approved.</summary>
    public Guid? ItemId { get; set; }
}

/// <summary>A webhook call already handled, so a replay (GitLab retries, or a copy of the request) is ignored.</summary>
public sealed class GitLabWebhookReceipt : Entity
{
    public Guid LinkId { get; set; }

    /// <summary>GitLab's <c>Idempotency-Key</c> or <c>X-Gitlab-Event-UUID</c>, else a hash of the body.</summary>
    public required string DedupKey { get; set; }

    public required string EventKind { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public string? Outcome { get; set; }
}
