namespace Workbench.Application.Integrations.Jira;

/// <summary>Server-wide Jira sync settings (config section <c>Jira</c>; docs/operations/configuration.md).</summary>
public sealed class JiraOptions
{
    public const string Section = "Jira";

    /// <summary>Runs the poll on this host (the backstop for missed webhooks, and the only path when no webhook is set up).</summary>
    public bool ReconcileEnabled { get; set; } = true;

    /// <summary>Minutes between polls of each linked project.</summary>
    public int ReconcileMinutes { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Pages of 100 issues one poll reads at most (the rest wait for the next one).</summary>
    public int MaxPagesPerReconcile { get; set; } = 20;

    /// <summary>Days to keep webhook receipts (the replay ledger).</summary>
    public int ReceiptRetentionDays { get; set; } = 14;
}
