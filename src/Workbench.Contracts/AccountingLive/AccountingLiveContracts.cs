using Workbench.Contracts.AccountingExport;

namespace Workbench.Contracts.AccountingLive;

// Live push to QuickBooks Online and Xero: the connection (OAuth 2.0), a preview of what a push would do, the push itself with a
// result for every record, and a retry of the ones that failed. Needs the "Export to accounting software" permission.

public static class AccountingConnectionStatus
{
    public const string Connected = "Connected";

    /// <summary>Xero only: the user connected more than one organisation and has to pick which one Workbench posts to.</summary>
    public const string ChooseOrganisation = "ChooseOrganisation";

    /// <summary>The provider refused our refresh token (expired, revoked or replaced). The user has to connect again.</summary>
    public const string NeedsReconnect = "NeedsReconnect";
}

public sealed record AccountingOrganisationChoice(string Id, string Name);

/// <summary>One provider's connection. Never carries a token.</summary>
public sealed record AccountingConnectionDto(
    AccountingTarget Target,
    string ProviderName,
    bool Configured,
    bool Connected,
    string? Status,
    bool IsSandbox,
    string? CompanyId,
    string? CompanyName,
    string? ConnectedBy,
    DateTime? ConnectedAtUtc,
    DateTime? RefreshTokenExpiresUtc,
    string? LastError,
    DateTime? LastSyncUtc,
    string? LastSyncStatus,
    string? LastSyncMessage,
    IReadOnlyList<AccountingOrganisationChoice> Choices);

public sealed record AccountingAuthorizeStart(string Url);

public sealed record AccountingOAuthCallback(AccountingTarget Target, string? Code, string? State, string? RealmId, string? Error);

public sealed record AccountingPushRequest(
    AccountingTarget Target,
    AccountingExportKind Kind,
    DateOnly From,
    DateOnly To,
    Guid? CompanyId = null,
    bool CreateMissingContacts = false,
    bool Again = false,
    string? Note = null,
    bool IncludeSubledgerJournals = false);

public sealed record AccountingMissingDto(AccountingMapKind Kind, string Name, bool WillBeCreated);

public sealed record AccountingPushPreviewDto(
    AccountingTarget Target,
    AccountingExportKind Kind,
    int NewRecords,
    int AlreadyExported,
    int LinesOrRows,
    decimal ControlTotal,
    IReadOnlyList<AccountingUnmappedDto> Unmapped,
    IReadOnlyList<AccountingMissingDto> MissingAtProvider,
    int CustomersToCreate,
    int VendorsToCreate,
    IReadOnlyList<string> Warnings,
    bool CanPush,
    string? BlockedReason);

public static class AccountingPushItemStatus
{
    public const string Created = "Created";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";

    /// <summary>The provider has no way to take this record (a credit memo, a vendor credit). It is not exported, not linked and not retried.</summary>
    public const string NotSent = "NotSent";
}

public static class AccountingPushRunStatus
{
    public const string Completed = "Completed";
    public const string PartlyFailed = "PartlyFailed";
    public const string Failed = "Failed";
}

public sealed record AccountingPushItemDto(Guid RecordId, string RecordNumber, string Status, string? ProviderId, string? ProviderNumber, string? Message, int Attempts);

public sealed record AccountingPushRunDto(
    Guid RunId,
    AccountingTarget Target,
    AccountingExportKind Kind,
    DateOnly From,
    DateOnly To,
    string Status,
    int Total,
    int Created,
    int Skipped,
    int Failed,
    int NotSent,
    int ContactsCreated,
    Guid? RetryOfRunId,
    string StartedBy,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? Message,
    IReadOnlyList<AccountingPushItemDto> Items);

public interface IAccountingConnectionService
{
    Task<IReadOnlyList<AccountingConnectionDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The provider's consent page address. A one-time <c>state</c> is saved so only this sign-in can finish the connection.</summary>
    Task<AccountingAuthorizeStart> StartAsync(AccountingTarget target, CancellationToken cancellationToken = default);

    /// <summary>Finishes the connection from the provider's redirect: checks the state, exchanges the code, stores the tokens encrypted.</summary>
    Task<AccountingConnectionDto> CompleteAsync(AccountingOAuthCallback callback, CancellationToken cancellationToken = default);

    /// <summary>Xero: picks the organisation when several were connected.</summary>
    Task<AccountingConnectionDto> ChooseOrganisationAsync(AccountingTarget target, string organisationId, CancellationToken cancellationToken = default);

    /// <summary>Revokes the tokens at the provider and forgets them. History and the ids already linked to records stay.</summary>
    Task DisconnectAsync(AccountingTarget target, CancellationToken cancellationToken = default);
}

public interface IAccountingPushService
{
    /// <summary>What a push would do. Reads the provider's accounts, customers, vendors and items to say what is missing there.</summary>
    Task<AccountingPushPreviewDto> PreviewAsync(AccountingPushRequest request, CancellationToken cancellationToken = default);

    Task<AccountingPushRunDto> PushAsync(AccountingPushRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pushes the records of an earlier run that failed, and only those.</summary>
    Task<AccountingPushRunDto> RetryFailedAsync(Guid runId, bool createMissingContacts = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountingPushRunDto>> ListRunsAsync(AccountingTarget? target = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<AccountingPushRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
}
