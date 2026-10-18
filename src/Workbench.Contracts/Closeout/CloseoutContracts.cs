namespace Workbench.Contracts.Closeout;

public enum CloseoutScope
{
    Contract,
    Project,
}

public enum CloseoutState
{
    Open,
    PhysicallyComplete,
    Closing,
    Closed,
}

public enum CloseoutCheck
{
    FinalRatesSettled,
    AllCostsPosted,
    FinalInvoiceIssued,
    SubcontractsClosed,
    PropertyDispositioned,
    FundsDeobligated,
    ReleaseOfClaims,
    AssignmentOfRefunds,
    RecordsRetention,
}

/// <summary>One checklist item: satisfied by the data (or a recorded fact), or waived by Finance with a reason. <see cref="Link"/> is app-relative.</summary>
public sealed record CloseoutItemDto(
    CloseoutCheck Check,
    string Label,
    bool IsSatisfied,
    bool IsWaived,
    string? WaiverReason,
    string Detail,
    string? Link)
{
    public bool IsComplete => IsSatisfied || IsWaived;
}

public sealed record CloseoutInvoiceDto(Guid Id, string Number, string Status, DateOnly PeriodTo, decimal Total);

public sealed record CloseoutEventDto(string Action, string Actor, DateTime OccurredAtUtc, string? Reason);

public sealed record CloseoutDto(
    CloseoutScope Scope,
    Guid TargetId,
    string Code,
    string Name,
    CloseoutState Status,
    DateOnly? PhysicallyCompleteOn,
    IReadOnlyList<CloseoutItemDto> Items,
    Guid? FinalInvoiceId,
    string? FinalInvoiceNumber,
    decimal Funded,
    decimal Billed,
    decimal DeobligationCalculated,
    decimal? DeobligationRecorded,
    string? DeobligationRecordedBy,
    DateOnly? ReleaseOfClaimsOn,
    string? ReleaseOfClaimsNote,
    DateOnly? AssignmentOfRefundsOn,
    string? AssignmentOfRefundsNote,
    DateOnly? RecordsRetentionUntil,
    DateTime? ClosedAtUtc,
    string? ClosedBy,
    int ReopenCount,
    IReadOnlyList<CloseoutInvoiceDto> BilledInvoices,
    IReadOnlyList<CloseoutEventDto> History,
    bool CanManage)
{
    public bool IsClosed => Status == CloseoutState.Closed;

    public bool ChecklistComplete => Items.All(i => i.IsComplete);
}

public sealed record PhysicallyCompleteRequest(DateOnly? On = null);

public sealed record CloseoutReasonRequest(string Reason);

public sealed record SaveCloseoutRecordsRequest(
    DateOnly? ReleaseOfClaimsOn = null,
    string? ReleaseOfClaimsNote = null,
    DateOnly? AssignmentOfRefundsOn = null,
    string? AssignmentOfRefundsNote = null,
    DateOnly? RecordsRetentionUntil = null);

/// <summary>Marks a billed invoice of the contract as the final invoice; null clears it.</summary>
public sealed record MarkFinalInvoiceRequest(Guid? InvoiceId);

public sealed record WaiveCloseoutItemRequest(CloseoutCheck Check, string Reason);

/// <summary>
/// Contract and project closeout: Open → Physically complete → Closing → Closed, one step at a time. Closing needs every
/// checklist item complete; closed contracts and projects refuse new time, ODCs, purchase orders and bills. Reopening needs
/// a reason. Reads: Finance, Admin and the managers of the contract's projects (the project's manager); changes: Finance and Admin.
/// </summary>
public interface ICloseoutService
{
    Task<CloseoutDto> GetAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default);

    Task<CloseoutDto> MarkPhysicallyCompleteAsync(CloseoutScope scope, Guid id, PhysicallyCompleteRequest request, CancellationToken cancellationToken = default);

    Task<CloseoutDto> StartClosingAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default);

    Task<CloseoutDto> CloseAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default);

    Task<CloseoutDto> ReopenAsync(CloseoutScope scope, Guid id, CloseoutReasonRequest request, CancellationToken cancellationToken = default);

    Task<CloseoutDto> SaveRecordsAsync(CloseoutScope scope, Guid id, SaveCloseoutRecordsRequest request, CancellationToken cancellationToken = default);

    Task<CloseoutDto> WaiveAsync(CloseoutScope scope, Guid id, WaiveCloseoutItemRequest request, CancellationToken cancellationToken = default);

    Task<CloseoutDto> RemoveWaiverAsync(CloseoutScope scope, Guid id, CloseoutCheck check, CancellationToken cancellationToken = default);

    Task<CloseoutDto> MarkFinalInvoiceAsync(Guid contractId, MarkFinalInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the funds de-obligation (funded less billed) as calculated now.</summary>
    Task<CloseoutDto> RecordDeobligationAsync(Guid contractId, CancellationToken cancellationToken = default);
}
