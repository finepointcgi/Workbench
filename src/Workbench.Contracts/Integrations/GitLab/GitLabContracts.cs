namespace Workbench.Contracts.Integrations.GitLab;

// GitLab sync: a project's plan (WBS, schedule tasks, assignments) two-way with GitLab issues, epics and milestones.
// Workbench owns the schedule (dates, dependencies, WBS codes, hours); GitLab owns work state (open/closed, progress, new
// issues under a linked phase). Title, description and assignees are shared: the later edit wins and the other value is
// kept on the sync panel. docs/integrations/gitlab.md.

/// <summary>The organization's GitLab connection (Admin › Integrations › GitLab). Never carries the token.</summary>
public sealed record GitLabConnectionDto(
    bool IsConfigured,
    string? BaseUrl,
    bool IsGitLabCom,
    string? TokenHint,
    bool HasCustomCa,
    string? CaSummary,
    bool IsActive,
    string? ConnectedAsUsername,
    string? ConnectedAsName,
    string? ServerVersion,
    DateTime? LastTestedAtUtc,
    bool? LastTestOk,
    string? LastTestMessage,
    int LinkedProjects,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    string? CaCertificatePem = null);

/// <param name="Token">A personal, group or project access token with the <c>api</c> scope. Null or blank keeps the saved one.</param>
/// <param name="CaCertificatePem">PEM of a private certificate authority, for a self-managed server with an internal certificate. Blank: none.</param>
public sealed record SaveGitLabConnectionRequest(string BaseUrl, string? Token, string? CaCertificatePem, bool IsActive = true);

/// <param name="Hint">What to do about a failure, in plain words.</param>
public sealed record GitLabTestResult(bool Ok, string Message, string? Hint, string? Username, string? Name, string? Version, bool? EpicsLikely);

/// <summary>A GitLab project to link a Workbench project to.</summary>
public sealed record GitLabProjectOptionDto(long Id, string PathWithNamespace, string Name, string WebUrl, long? GroupId, string? GroupPath);

public enum GitLabPhaseChoice
{
    /// <summary>Epics when the group has them (Premium and Ultimate), else milestones.</summary>
    Auto,
    Epics,
    Milestones,
}

/// <param name="Project">The GitLab project's id, path (group/project) or web address.</param>
/// <param name="PhaseLevels">Outline levels of summary tasks that become epics or milestones: 1 = top level only, 0 = all.</param>
public sealed record LinkGitLabProjectRequest(string Project, GitLabPhaseChoice Phases = GitLabPhaseChoice.Auto, int PhaseLevels = 1);

/// <summary>Everything the project's GitLab panel shows.</summary>
public sealed record GitLabProjectSyncDto(
    Guid ProjectId,
    bool ConnectionReady,
    string? ConnectionProblem,
    bool CanManage,
    GitLabLinkDto? Link,
    IReadOnlyList<GitLabTaskLinkDto> Tasks,
    IReadOnlyList<GitLabConflictDto> Conflicts,
    IReadOnlyList<GitLabPendingIssueDto> Pending,
    IReadOnlyList<GitLabPersonToMatchDto> PeopleToMatch,
    IReadOnlyList<GitLabActivityDto> Recent,
    int ChangesWaiting,
    int ChangesFailed);

/// <param name="State">Plain words: "Up to date", "Sending changes", "Needs attention".</param>
public sealed record GitLabLinkDto(
    Guid Id,
    long GitLabProjectId,
    string GitLabProjectPath,
    string GitLabProjectUrl,
    string? GroupPath,
    string PhaseMode,
    int PhaseLevels,
    string State,
    DateTime? LastSyncedAtUtc,
    string? LastError,
    string? LastErrorHint,
    bool WebhookRegistered,
    DateTime CreatedAtUtc,
    string CreatedBy);

/// <param name="Label">"issue #12", "epic &amp;3" or "milestone %4".</param>
public sealed record GitLabTaskLinkDto(
    Guid ItemId,
    string Title,
    string? WbsCode,
    string Kind,
    long Iid,
    string Label,
    string WebUrl,
    DateTime? LastSyncedAtUtc,
    string? LastError,
    bool IsRemoved);

public sealed record GitLabConflictDto(
    Guid Id,
    Guid? ItemId,
    string? TaskTitle,
    string? GitLabLabel,
    string Field,
    string FieldLabel,
    string Kind,
    string? WorkbenchValue,
    string? GitLabValue,
    string Outcome,
    string? Hint,
    DateTime CreatedAtUtc,
    bool CanUseWorkbench,
    bool CanUseGitLab);

public enum GitLabConflictChoice
{
    /// <summary>Leave what the sync did and close the note.</summary>
    Dismiss,

    /// <summary>Make GitLab match Workbench's value.</summary>
    UseWorkbench,

    /// <summary>Make Workbench match GitLab's value.</summary>
    UseGitLab,
}

public sealed record ResolveGitLabConflictRequest(GitLabConflictChoice Choice);

public sealed record GitLabPendingIssueDto(Guid Id, long Iid, string Title, string WebUrl, Guid? PhaseItemId, string? PhaseTitle, string? Author, DateTime SeenAtUtc);

/// <summary>Someone assigned on one side with no match on the other.</summary>
/// <param name="Side"><c>workbench</c>: a person on the plan with no GitLab user; <c>gitlab</c>: a GitLab assignee with no Workbench person.</param>
public sealed record GitLabPersonToMatchDto(string Side, string? Email, string? DisplayName, long? GitLabUserId, string? GitLabUsername, int Tasks);

public sealed record GitLabUserMapDto(string Email, string? DisplayName, long GitLabUserId, string GitLabUsername, string? GitLabName, string MatchedBy);

public sealed record GitLabUserOptionDto(long Id, string Username, string Name);

public sealed record MatchGitLabPersonRequest(string Email, long GitLabUserId);

public sealed record GitLabActivityDto(Guid? ItemId, string Summary, string Actor, DateTime OccurredAtUtc);

public sealed record GitLabSyncNowResult(int Synced, int Created, int NewIssues, int Conflicts, string Message);

/// <summary>For setting the webhook up by hand when the token couldn't (it needs the Maintainer role).</summary>
public sealed record GitLabWebhookSetupDto(string Url, string SecretToken, bool Registered, IReadOnlyList<string> Events);

/// <summary>What a task's details show: "Linked to GitLab issue #123".</summary>
public sealed record GitLabTaskStatusDto(string Label, string WebUrl, DateTime? LastSyncedAtUtc, string? LastError, bool IsRemoved);

/// <summary>
/// The organization's GitLab server and token, and people matches. Administrators only (Manage integrations); never open to API keys.
/// </summary>
public interface IGitLabConnectionService
{
    Task<GitLabConnectionDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves and tests the connection. A blank token keeps the saved one.</summary>
    Task<GitLabConnectionDto> SaveAsync(SaveGitLabConnectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tests <paramref name="unsaved"/> (what's typed, a blank token meaning the saved one) or, when null, the saved connection.</summary>
    Task<GitLabTestResult> TestAsync(SaveGitLabConnectionRequest? unsaved = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the connection. Refused while projects are linked.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabUserMapDto>> ListPeopleAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);

    Task<GitLabUserMapDto> MatchPersonAsync(MatchGitLabPersonRequest request, CancellationToken cancellationToken = default);

    Task UnmatchPersonAsync(string email, CancellationToken cancellationToken = default);
}

/// <summary>
/// A project's link to GitLab (the project's Setup tab and Plan panel). Reading needs access to the project's plan;
/// changes need project setup (Finance, administrators, the project's manager with the Project manager role).
/// </summary>
public interface IGitLabProjectSyncService
{
    Task<GitLabProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabProjectOptionDto>> SearchGitLabProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default);

    /// <summary>Links the project and sends the plan to GitLab (phases first, then tasks). Adds a "% Complete" column when the plan has none.</summary>
    Task<GitLabProjectSyncDto> LinkAsync(Guid projectId, LinkGitLabProjectRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops syncing. GitLab issues and plan tasks both stay; the webhook Workbench added is removed.</summary>
    Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Compares every linked task with GitLab now and sends or takes what changed.</summary>
    Task<GitLabSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<GitLabWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<GitLabProjectSyncDto> ResolveConflictAsync(Guid projectId, Guid conflictId, ResolveGitLabConflictRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approves (adds the task under its phase) or rejects an issue someone added in GitLab.</summary>
    Task<GitLabProjectSyncDto> DecidePendingAsync(Guid projectId, Guid pendingId, bool approve, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(Guid projectId, string query, CancellationToken cancellationToken = default);

    /// <summary>Matches a person on this project's plan to a GitLab user (for every project).</summary>
    Task<GitLabProjectSyncDto> MatchPersonAsync(Guid projectId, MatchGitLabPersonRequest request, CancellationToken cancellationToken = default);

    /// <summary>The task's GitLab link, or null. Needs access to the sheet.</summary>
    Task<GitLabTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);
}
