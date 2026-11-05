namespace Workbench.Contracts.Integrations.Jira;

// Jira sync: a project's Jira issues and its plan stay in step. Jira owns the title and the assignee; dates follow a setting
// (Workbench's by default); status flows both ways (Done in Workbench moves the issue to Done). Time is never imported:
// timesheets are the cost record. docs/integrations/jira.md.

/// <summary>The organization's Jira connection (Admin › Integrations › Jira). Never carries the token.</summary>
public sealed record JiraConnectionDto(
    bool IsConfigured,
    string? SiteUrl,
    string? AccountEmail,
    string? TokenHint,
    bool IsActive,
    string? ConnectedAsName,
    DateTime? LastTestedAtUtc,
    bool? LastTestOk,
    string? LastTestMessage,
    int LinkedProjects,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

/// <param name="Token">A Jira API token (Atlassian account › Security › API tokens). Null or blank keeps the saved one.</param>
public sealed record SaveJiraConnectionRequest(string SiteUrl, string AccountEmail, string? Token, bool IsActive = true);

/// <param name="Hint">What to do about a failure, in plain words.</param>
public sealed record JiraTestResult(bool Ok, string Message, string? Hint, string? Name);

/// <summary>A Jira project to link a Workbench project to.</summary>
public sealed record JiraProjectOptionDto(string Key, string Name, string WebUrl);

public enum JiraDateWinnerChoice
{
    Workbench,
    Jira,
}

/// <param name="Project">The Jira project's key (such as RAD) or its web address.</param>
/// <param name="EpicKey">Only this epic's issues sync (such as RAD-12). Blank: the whole project.</param>
/// <param name="Jql">Only issues this JQL also matches sync. Blank: no extra filter.</param>
public sealed record LinkJiraProjectRequest(
    string Project,
    string? EpicKey = null,
    string? Jql = null,
    JiraDateWinnerChoice DateWinner = JiraDateWinnerChoice.Workbench,
    bool CreateIssuesForNewTasks = false,
    string? IssueType = null);

/// <summary>Changes a link's settings without unlinking.</summary>
public sealed record UpdateJiraLinkRequest(JiraDateWinnerChoice DateWinner, bool CreateIssuesForNewTasks, string? IssueType = null);

/// <summary>Everything the project's Jira panel shows.</summary>
public sealed record JiraProjectSyncDto(
    Guid ProjectId,
    bool ConnectionReady,
    string? ConnectionProblem,
    bool CanManage,
    JiraLinkDto? Link,
    IReadOnlyList<JiraTaskLinkDto> Tasks,
    IReadOnlyList<JiraNoteDto> Notes,
    IReadOnlyList<JiraActivityDto> Recent,
    int ChangesWaiting,
    int ChangesFailed);

/// <param name="State">Plain words: "Up to date", "Sending changes", "Needs attention".</param>
/// <param name="Rules">One plain sentence saying who wins what.</param>
public sealed record JiraLinkDto(
    Guid Id,
    string JiraProjectKey,
    string JiraProjectName,
    string JiraProjectUrl,
    string? EpicKey,
    string? Jql,
    JiraDateWinnerChoice DateWinner,
    bool CreateIssuesForNewTasks,
    string IssueType,
    string State,
    string Rules,
    DateTime? LastSyncedAtUtc,
    string? LastError,
    string? LastErrorHint,
    DateTime CreatedAtUtc,
    string CreatedBy);

public sealed record JiraTaskLinkDto(
    Guid ItemId,
    string Title,
    string? WbsCode,
    string IssueKey,
    string WebUrl,
    string? JiraStatus,
    decimal? EstimateHours,
    DateTime? LastSyncedAtUtc,
    string? LastError,
    bool IsRemoved);

/// <param name="IsError">The sync couldn't do something (shown in red); otherwise it settled something and says how.</param>
public sealed record JiraNoteDto(Guid Id, Guid? ItemId, string? TaskTitle, string? IssueKey, string Field, string Message, string? Hint, bool IsError, DateTime CreatedAtUtc);

public sealed record JiraActivityDto(Guid? ItemId, string Summary, string Actor, DateTime OccurredAtUtc);

public sealed record JiraSyncNowResult(int Synced, int Created, int Failed, string Message);

/// <summary>For setting the webhook up by hand in Jira (Cloud admin › System › Webhooks) or in an automation rule.</summary>
public sealed record JiraWebhookSetupDto(string Url, string Secret, IReadOnlyList<string> Events, string Note);

/// <summary>What a task's details show: "Linked to Jira RAD-12".</summary>
public sealed record JiraTaskStatusDto(string IssueKey, string WebUrl, string? JiraStatus, DateTime? LastSyncedAtUtc, string? LastError, bool IsRemoved);

/// <summary>The organization's Jira site and token. Administrators only (Manage integrations); never open to API keys.</summary>
public interface IJiraConnectionService
{
    Task<JiraConnectionDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves and tests the connection. A blank token keeps the saved one.</summary>
    Task<JiraConnectionDto> SaveAsync(SaveJiraConnectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tests <paramref name="unsaved"/> (what's typed, a blank token meaning the saved one) or, when null, the saved connection.</summary>
    Task<JiraTestResult> TestAsync(SaveJiraConnectionRequest? unsaved = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the connection. Refused while projects are linked.</summary>
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A project's link to Jira (the project's Setup tab). Reading needs access to the project's plan; changes need project
/// setup (Finance, administrators, the project's manager with the Project manager role).
/// </summary>
public interface IJiraProjectSyncService
{
    Task<JiraProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JiraProjectOptionDto>> SearchJiraProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default);

    /// <summary>Links the project and reads the Jira issues into the plan.</summary>
    Task<JiraProjectSyncDto> LinkAsync(Guid projectId, LinkJiraProjectRequest request, CancellationToken cancellationToken = default);

    Task<JiraProjectSyncDto> UpdateLinkAsync(Guid projectId, UpdateJiraLinkRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops syncing. Jira issues and plan tasks both stay.</summary>
    Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Re-reads every issue now and sends what changed here.</summary>
    Task<JiraSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<JiraWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Closes a note on the panel.</summary>
    Task<JiraProjectSyncDto> DismissNoteAsync(Guid projectId, Guid noteId, CancellationToken cancellationToken = default);

    /// <summary>The task's Jira link, or null. Needs access to the sheet.</summary>
    Task<JiraTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);
}
