namespace Workbench.Domain.Integrations.Jira;

/// <summary>Which kind of Jira the site is. Only Cloud is built; Data Center follows behind the same client interface.</summary>
public enum JiraFlavor
{
    Cloud,
}

/// <summary>
/// The organization's Jira site and the account Workbench signs in as. One per tenant. The API token is encrypted with Data
/// Protection (<c>Workbench.Integrations.Jira.Token.v1</c>) and never shown again.
/// </summary>
public sealed class JiraConnection : Entity
{
    public JiraFlavor Flavor { get; set; } = JiraFlavor.Cloud;

    /// <summary>The site's address, e.g. <c>https://example.atlassian.net</c>.</summary>
    public required string SiteUrl { get; set; }

    /// <summary>The Atlassian account the API token belongs to (lower-case email).</summary>
    public required string AccountEmail { get; set; }

    public required string TokenProtected { get; set; }

    /// <summary>The token's last four characters, so admins can tell which one is saved.</summary>
    public required string TokenHint { get; set; }

    public bool IsActive { get; set; } = true;

    public string? ConnectedAsName { get; set; }
    public string? ConnectedAsAccountId { get; set; }
    public DateTime? LastTestedAtUtc { get; set; }
    public bool? LastTestOk { get; set; }
    public string? LastTestMessage { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Who wins when both sides changed the same date since the last sync.</summary>
public enum JiraDateWinner
{
    Workbench,
    Jira,
}

public enum JiraLinkStatus
{
    Active,

    /// <summary>The last sync failed in a way that needs a person (a revoked token, a deleted Jira project).</summary>
    NeedsAttention,
}

/// <summary>A Workbench project's plan linked to one Jira project (optionally narrowed to an epic or a JQL filter).</summary>
public sealed class JiraProjectLink : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>The project's plan sheet when it was linked.</summary>
    public Guid SheetId { get; set; }

    public required string JiraProjectKey { get; set; }
    public required string JiraProjectName { get; set; }
    public required string JiraProjectUrl { get; set; }

    /// <summary>Only this epic's issues sync (an issue key such as <c>RAD-12</c>), when set.</summary>
    public string? EpicKey { get; set; }

    /// <summary>Only issues this JQL also matches sync, when set.</summary>
    public string? Jql { get; set; }

    /// <summary>When both sides changed a date since the last sync, this side's date is kept.</summary>
    public JiraDateWinner DateWinner { get; set; } = JiraDateWinner.Workbench;

    /// <summary>A task added in Workbench after linking also creates a Jira issue.</summary>
    public bool CreateIssuesForNewTasks { get; set; }

    /// <summary>The issue type new issues get (e.g. <c>Task</c>).</summary>
    public string IssueType { get; set; } = "Task";

    /// <summary>The secret that signs this link's webhook calls (<c>X-Hub-Signature</c>), encrypted.</summary>
    public required string WebhookSecretProtected { get; set; }

    public JiraLinkStatus Status { get; set; } = JiraLinkStatus.Active;
    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? LastErrorHint { get; set; }

    /// <summary>The last poll started here; the next reads issues changed since then (less a few minutes of overlap).</summary>
    public DateTime? LastReconcileAtUtc { get; set; }

    /// <summary>Changes whenever a reconcile is claimed, so two hosts never run the same one.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// One plan task and the Jira issue it is synced with, plus the values each side had at the last sync (the "base"): a
/// value that differs from its base changed on that side since, which tells a change from an echo and detects two edits.
/// </summary>
public sealed class JiraItemLink : Entity
{
    public Guid LinkId { get; set; }

    /// <summary>The plan task (no foreign key: the link outlives a deleted task).</summary>
    public Guid ItemId { get; set; }

    /// <summary>Jira's id of the issue (stable if the key changes when it moves between projects).</summary>
    public required string IssueId { get; set; }

    /// <summary>The key people see, e.g. <c>RAD-12</c>.</summary>
    public required string IssueKey { get; set; }

    public required string WebUrl { get; set; }

    public string? BaseTitle { get; set; }

    /// <summary>Jira's account id of the assignee last synced.</summary>
    public string? BaseAssigneeAccountId { get; set; }

    /// <summary>The Workbench person the Jira assignee was put on the task as (so a later change removes the right one).</summary>
    public string? BaseAssigneeEmail { get; set; }

    public DateOnly? BaseWorkbenchDue { get; set; }
    public DateOnly? BaseJiraDue { get; set; }
    public bool BaseWorkbenchDone { get; set; }
    public bool BaseJiraDone { get; set; }

    /// <summary>Jira's status name, for the panel ("In Review").</summary>
    public string? JiraStatus { get; set; }

    /// <summary>Jira's original estimate in hours, when it has one.</summary>
    public decimal? EstimateHours { get; set; }

    /// <summary>The task's <c>ModifiedAtUtc</c> at the last sync.</summary>
    public DateTime? WorkbenchModifiedAtUtc { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>The issue was deleted in Jira; the task stays.</summary>
    public bool IsRemoved { get; set; }

    public Guid Version { get; set; } = Guid.NewGuid();
}

/// <summary>Something the sync left alone or settled that a person may want to see (a date both sides changed, an assignee Workbench doesn't know).</summary>
public sealed class JiraSyncNote : Entity
{
    public Guid LinkId { get; set; }
    public Guid? ItemId { get; set; }
    public string? IssueKey { get; set; }

    /// <summary><c>title</c>, <c>assignee</c>, <c>due</c>, <c>status</c> or <c>sync</c>.</summary>
    public required string Field { get; set; }

    public required string Message { get; set; }
    public string? Hint { get; set; }
    public bool IsError { get; set; }
    public bool IsOpen { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A webhook call already received, so a retry or replay changes nothing.</summary>
public sealed class JiraWebhookReceipt : Entity
{
    public Guid LinkId { get; set; }
    public required string DedupKey { get; set; }
    public required string EventKind { get; set; }
    public required string Outcome { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
