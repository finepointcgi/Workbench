namespace Workbench.Contracts.Contracts;

// Contract detail: CLINs (and SLINs), modifications and funding. Enums mirror Workbench.Domain.ProjectAccounting by name and value.

public enum ClinPricingKind { FirmFixedPrice, TimeAndMaterials, LaborHour, CostPlusFixedFee, CostPlusAwardFee, CostPlusIncentiveFee, CostNoFee }

public enum ModificationState { Draft, Executed }

// ---- CLINs ----

/// <summary>
/// A CLIN or SLIN. A SLIN's number is its parent's four digits plus two letters (<c>0001AA</c> under <c>0001</c>).
/// A CLIN with SLINs is a roll-up: its ceiling and funded value are the sums of its SLINs, and funding and charges go to the SLINs.
/// </summary>
public sealed record ClinDto(
    Guid Id,
    Guid ContractId,
    string Number,
    string? ParentNumber,
    string Description,
    ClinPricingKind Pricing,
    bool IsOption,
    bool IsExercised,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal FundedValue,
    decimal? FeeRate,
    int Order,
    bool HasSlins)
{
    public bool IsSlin => ParentNumber is not null;

    /// <summary>Unexercised options can't be charged, funded or billed.</summary>
    public bool IsActiveOption => !IsOption || IsExercised;
}

/// <summary>
/// Creates or edits a CLIN. <see cref="CeilingValue"/> can be set while the CLIN has no funding; after that, value changes
/// go through a modification. Funded value only ever changes through funding actions.
/// </summary>
public sealed record SaveClinRequest(
    string Number,
    string Description,
    ClinPricingKind Pricing,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal? FeeRate = null,
    bool IsOption = false);

/// <summary>Which CLIN a WBS element's cost goes to (null when unmapped).</summary>
public sealed record WbsClinMappingDto(Guid WbsElementId, Guid? ClinId);

public interface IClinService
{
    /// <summary>The contract's CLINs and SLINs in number order (parents before their SLINs).</summary>
    Task<IReadOnlyList<ClinDto>> ListClinsAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ClinDto?> GetClinAsync(Guid clinId, CancellationToken cancellationToken = default);

    /// <summary>Adds a CLIN at award or setup time (option CLINs start unexercised). Later additions belong in a modification.</summary>
    Task<ClinDto> CreateClinAsync(Guid contractId, SaveClinRequest request, CancellationToken cancellationToken = default);

    /// <summary>Edits a CLIN's description, pricing, fee and period; its ceiling only while it has no funding.</summary>
    Task<ClinDto> UpdateClinAsync(Guid clinId, SaveClinRequest request, CancellationToken cancellationToken = default);

    /// <summary>The CLINs a project's WBS can be mapped to: the leaf CLINs and SLINs of the project's contract.</summary>
    Task<IReadOnlyList<ClinDto>> ListClinsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WbsClinMappingDto>> ListWbsMappingsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Maps a WBS element to a CLIN of its project's contract, or clears the mapping when <paramref name="clinId"/> is null.</summary>
    Task<WbsClinMappingDto> SetWbsClinAsync(Guid wbsElementId, Guid? clinId, CancellationToken cancellationToken = default);
}

// ---- Modifications ----

public enum ModificationChangeKind
{
    /// <summary>Adds a new CLIN or SLIN (fields: ClinNumber, ClinDescription, Pricing, IsOption, PeriodStart, PeriodEnd, Amount = ceiling, FeeRate).</summary>
    AddClin,

    /// <summary>Exercises an option CLIN (ClinNumber).</summary>
    ExerciseOption,

    /// <summary>Changes a CLIN's ceiling by Amount (negative to reduce).</summary>
    ChangeClinValue,

    /// <summary>Extends the period of performance to NewPeriodEnd: of one CLIN (ClinNumber) or, with no ClinNumber, the contract.</summary>
    ExtendPeriod,

    /// <summary>Obligates (positive Amount) or de-obligates (negative) funds on a CLIN.</summary>
    Fund,
}

/// <summary>
/// One change in a modification. CLINs are named by number so a modification can add a CLIN and fund it in one go.
/// On execution changes apply in kind order (add, exercise, value, period, funding), then in list order.
/// </summary>
public sealed record ModificationChangeDto(
    ModificationChangeKind Kind,
    string? ClinNumber = null,
    decimal? Amount = null,
    DateOnly? NewPeriodEnd = null,
    string? ClinDescription = null,
    ClinPricingKind? Pricing = null,
    bool IsOption = false,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null,
    decimal? FeeRate = null,
    string? Note = null);

public sealed record ContractModificationDto(
    Guid Id,
    Guid ContractId,
    string Number,
    DateOnly EffectiveDate,
    string Description,
    ModificationState Status,
    DateTime? ExecutedAtUtc,
    string? ExecutedBy,
    IReadOnlyList<ModificationChangeDto> Changes,
    decimal FundingDelta);

public sealed record SaveModificationRequest(string Number, DateOnly EffectiveDate, string Description, IReadOnlyList<ModificationChangeDto> Changes);

public interface IContractModificationService
{
    /// <summary>The contract's modifications, newest effective date first.</summary>
    Task<IReadOnlyList<ContractModificationDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ContractModificationDto?> GetAsync(Guid modificationId, CancellationToken cancellationToken = default);

    Task<ContractModificationDto> CreateDraftAsync(Guid contractId, SaveModificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces a draft's header and changes. Executed modifications are immutable (ArgumentException).</summary>
    Task<ContractModificationDto> UpdateDraftAsync(Guid modificationId, SaveModificationRequest request, CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(Guid modificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every change atomically (all or nothing), writes the funding actions, re-syncs CLIN and contract totals,
    /// audits it and marks it executed. A correction is another modification.
    /// </summary>
    Task<ContractModificationDto> ExecuteAsync(Guid modificationId, CancellationToken cancellationToken = default);
}

// ---- Funding ----

public sealed record FundingActionDto(
    Guid Id,
    Guid ContractId,
    Guid ClinId,
    string ClinNumber,
    Guid? ModificationId,
    string? ModificationNumber,
    DateOnly EffectiveDate,
    decimal Amount,
    decimal RunningTotal,
    string? Description,
    DateTime CreatedAtUtc,
    string CreatedBy);

public enum FundingLevel { Ok, Warning, Critical, Exhausted, NotActive }

/// <summary>
/// Funding status of one leaf CLIN. <see cref="Incurred"/> is approved cost to date (fully burdened when indirect rates
/// are available, otherwise direct: labor at cost plus allowable ODCs); <see cref="Committed"/> is submitted time and ODCs
/// awaiting approval. Percentages are fractions (0.75 = 75%) of funded value.
/// </summary>
public sealed record ClinFundingStatusDto(
    Guid ClinId,
    string Number,
    string Description,
    bool IsOption,
    bool IsExercised,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal FundedValue,
    decimal Incurred,
    decimal Committed,
    decimal Remaining,
    decimal? PercentIncurred,
    decimal? PercentCommitted,
    decimal BurnPerDay,
    DateOnly? ProjectedExhaustion,
    FundingLevel Level);

public sealed record FundingNoticeDto(
    Guid Id,
    Guid ContractId,
    Guid ClinId,
    string ClinNumber,
    string Title,
    int ThresholdPercent,
    decimal FundedValue,
    decimal IncurredAndCommitted,
    DateTime SentAtUtc,
    IReadOnlyList<string> Recipients);

public sealed record ContractFundingStatusDto(
    Guid ContractId,
    string ContractNumber,
    DateOnly AsOf,
    bool IsBurdened,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    IReadOnlyList<ClinFundingStatusDto> Clins,
    IReadOnlyList<FundingNoticeDto> Notices);

public interface IFundingService
{
    /// <summary>Funding actions oldest first, with each CLIN's running total; one CLIN's when <paramref name="clinId"/> is set.</summary>
    Task<IReadOnlyList<FundingActionDto>> ListFundingHistoryAsync(Guid contractId, Guid? clinId = null, CancellationToken cancellationToken = default);

    Task<ContractFundingStatusDto> GetFundingStatusAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>The contract's status limited to the CLINs the project's WBS is mapped to, or null when the project has no contract.</summary>
    Task<ContractFundingStatusDto?> GetProjectFundingStatusAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends Limitation of Funds / Limitation of Cost notices for CLINs whose incurred plus committed cost has reached a
    /// threshold, once per CLIN, threshold and funded value (new funding re-arms them). Returns the notices sent now.
    /// </summary>
    Task<IReadOnlyList<FundingNoticeDto>> CheckNoticesAsync(Guid contractId, CancellationToken cancellationToken = default);
}
