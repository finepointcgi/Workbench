namespace Workbench.Application.Integrations.GitLab;

/// <summary>Server-wide GitLab sync settings (config section <c>GitLab</c>; docs/operations/configuration.md).</summary>
public sealed class GitLabOptions
{
    public const string Section = "GitLab";

    /// <summary>Runs the reconcile job on this host (the backstop for missed webhooks and for changes that raise no event).</summary>
    public bool ReconcileEnabled { get; set; } = true;

    /// <summary>Minutes between reconciles of each linked project.</summary>
    public int ReconcileMinutes { get; set; } = 10;

    /// <summary>
    /// Host names of self-managed GitLab servers on your own network (they resolve to private addresses). Workbench refuses
    /// private addresses for every other integration; only the server operator can allow one here, not a tenant admin.
    /// </summary>
    public string[] AllowedPrivateHosts { get; set; } = [];

    /// <summary>Sends GitLab calls through the system proxy (HTTPS_PROXY), for servers reachable only that way.</summary>
    public bool UseSystemProxy { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Pages of 100 issues one reconcile reads at most (the rest wait for the next one).</summary>
    public int MaxPagesPerReconcile { get; set; } = 20;

    /// <summary>When GitLab says fewer requests than this are left in the window, Workbench waits for the window to reset.</summary>
    public int RateLimitReserve { get; set; } = 5;

    /// <summary>Days to keep webhook receipts (the replay ledger).</summary>
    public int ReceiptRetentionDays { get; set; } = 14;

    public bool IsPrivateHostAllowed(string host) =>
        AllowedPrivateHosts.Any(h => string.Equals(h.Trim().TrimEnd('.'), host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
}
