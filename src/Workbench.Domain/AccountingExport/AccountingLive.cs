namespace Workbench.Domain.AccountingExport;

// Live push to QuickBooks Online and Xero. Targets and kinds are stored as the contract enums' names, like the rest of the export tables.

/// <summary>
/// One OAuth connection per target per tenant database. Tokens are encrypted with Data Protection; nothing here is ever sent to a client.
/// <see cref="TokenVersion"/> is a concurrency token: both providers rotate refresh tokens, so the row that holds the newest one must
/// be written once, atomically, and a second writer that lost the race has to re-read instead of overwriting it.
/// </summary>
public sealed class AccountingConnection : Entity
{
    public required string Target { get; set; }
    public bool IsSandbox { get; set; }

    /// <summary>Connected, ChooseOrganisation or NeedsReconnect.</summary>
    public required string Status { get; set; }

    /// <summary>QuickBooks realm (company) id, or the Xero tenant id once chosen.</summary>
    public string? CompanyId { get; set; }

    public string? CompanyName { get; set; }
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTime AccessExpiresUtc { get; set; }
    public DateTime RefreshExpiresUtc { get; set; }

    /// <summary>Xero: the organisations the user connected, until one is chosen (JSON array of id/name).</summary>
    public string? ChoicesJson { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastRefreshedUtc { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncMessage { get; set; }
    public required string ConnectedBy { get; set; }
    public DateTime ConnectedAtUtc { get; set; }
    public int TokenVersion { get; set; }
}

/// <summary>A started authorisation. The state we sent to the provider is kept as a hash; the callback must present it, once, soon.</summary>
public sealed class AccountingOAuthState : Entity
{
    public required string StateHash { get; set; }
    public required string Target { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// The provider's own id for a Workbench record once it is in the books: this, not a name or number, is what stops a retry from
/// creating a second copy. Unique per target × kind × record.
/// </summary>
public sealed class AccountingProviderLink : Entity
{
    public required string Target { get; set; }
    public required string Kind { get; set; }
    public Guid RecordId { get; set; }
    public required string ProviderId { get; set; }
    public string? ProviderNumber { get; set; }
    public Guid RunId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A push run. Shares its id with the <see cref="AccountingExportRun"/> that shows it in the history.</summary>
public sealed class AccountingPushRun : Entity
{
    public required string Target { get; set; }
    public required string Kind { get; set; }
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public Guid? CompanyId { get; set; }
    public required string Status { get; set; }
    public int Total { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int NotSent { get; set; }
    public int ContactsCreated { get; set; }
    public Guid? RetryOfRunId { get; set; }
    public bool CreateMissingContacts { get; set; }
    public string? Message { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>What happened to one record in a push run.</summary>
public sealed class AccountingPushItem : Entity
{
    public Guid RunId { get; set; }
    public Guid RecordId { get; set; }
    public required string RecordNumber { get; set; }

    /// <summary>Created, Skipped (already linked), Failed or NotSent.</summary>
    public required string Status { get; set; }

    public string? ProviderId { get; set; }
    public string? ProviderNumber { get; set; }
    public string? Message { get; set; }
    public int Attempts { get; set; }
}
