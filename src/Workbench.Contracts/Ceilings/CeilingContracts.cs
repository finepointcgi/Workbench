namespace Workbench.Contracts.Ceilings;

/// <summary>What a ceiling is set on. A labor-category ceiling is per WBS element (and everything under it).</summary>
public enum CeilingScope
{
    Contract,
    Clin,
    Project,
    Wbs,
    LaborCategory,
}

/// <summary>Warn reports and alerts; Block also refuses whatever would go past the ceiling.</summary>
public enum CeilingMode
{
    Warn,
    Block,
}

public enum CeilingMetric
{
    Hours,
    Cost,
    Billing,
}

public enum CeilingAlertLevel
{
    Ok,
    Warning,
    Critical,
    Over,
}

public enum RateCapKind
{
    Fringe,
    Overhead,
    GeneralAndAdministrative,
    FeeRate,
    FeeAmount,
}

/// <summary>
/// Thrown when a blocking ceiling refuses a charge, bill, order or invoice. It's an <see cref="ArgumentException"/>, so the
/// API answers 400 and the HTTP client throws it back as one.
/// </summary>
public sealed class CeilingExceededException(string message) : ArgumentException(message);

/// <summary>Thrown when something is charged to a closed contract or project (400 over HTTP).</summary>
public sealed class ChargingClosedException(string message) : ArgumentException(message);

public sealed record CeilingDto(
    Guid Id,
    CeilingScope Scope,
    Guid? ContractId,
    string? ContractNumber,
    Guid? ClinId,
    string? ClinNumber,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    Guid? LaborCategoryId,
    string? LaborCategoryCode,
    decimal? HoursCeiling,
    decimal? CostCeiling,
    decimal? BillingCeiling,
    CeilingMode Mode,
    IReadOnlyList<int> AlertThresholds,
    bool ProjectManagerMayOverride,
    string? Notes,
    bool IsActive,
    string Label);

public sealed record CeilingOverrideDto(
    Guid Id,
    Guid CeilingId,
    decimal? ExtraHours,
    decimal? ExtraCost,
    decimal? ExtraBilling,
    string Reason,
    DateTime ExpiresAtUtc,
    string ApprovedBy,
    DateTime ApprovedAtUtc,
    string? RevokedBy,
    DateTime? RevokedAtUtc,
    string? RevokeReason,
    bool IsActive);

/// <summary>
/// One measure of a ceiling. <see cref="EffectiveLimit"/> is the ceiling plus active overrides, or null while an override
/// without amounts lifts the ceiling.
/// </summary>
public sealed record CeilingMeasureDto(
    CeilingMetric Metric,
    decimal Limit,
    decimal? EffectiveLimit,
    decimal Consumed,
    decimal Remaining,
    decimal PercentConsumed,
    CeilingAlertLevel Level);

public sealed record CeilingStatusDto(
    CeilingDto Ceiling,
    IReadOnlyList<CeilingMeasureDto> Measures,
    IReadOnlyList<CeilingOverrideDto> ActiveOverrides,
    CeilingAlertLevel Level,
    bool CanOverride);

public sealed record RateCeilingDto(
    Guid Id,
    Guid ContractId,
    Guid? ClinId,
    string? ClinNumber,
    RateCapKind Kind,
    int? FiscalYear,
    decimal? MaxRate,
    decimal? MaxAmount,
    string? Notes,
    bool IsActive,
    string Label);

public sealed record ContractCeilingStatusDto(
    Guid ContractId,
    string ContractNumber,
    IReadOnlyList<CeilingStatusDto> Ceilings,
    IReadOnlyList<RateCeilingDto> RateCeilings,
    bool CanManage);

/// <summary>Every ceiling that applies to a project: its own, its WBS and labor categories, and its contract's and CLINs'.</summary>
public sealed record ProjectCeilingStatusDto(Guid ProjectId, string ProjectCode, Guid? ContractId, IReadOnlyList<CeilingStatusDto> Ceilings, bool CanManage);

/// <summary>
/// Creates a ceiling. Set the id matching <see cref="Scope"/> (a labor-category ceiling needs the WBS element and the labor
/// category). At least one of the amounts is required; <see cref="BillingCeiling"/> only on contract and CLIN ceilings.
/// <see cref="AlertThresholds"/> are percents, comma separated (default 80,95,100). On update the scope and target are ignored.
/// </summary>
public sealed record SaveCeilingRequest(
    CeilingScope Scope,
    Guid? ContractId = null,
    Guid? ClinId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null,
    Guid? LaborCategoryId = null,
    decimal? HoursCeiling = null,
    decimal? CostCeiling = null,
    decimal? BillingCeiling = null,
    CeilingMode Mode = CeilingMode.Warn,
    string? AlertThresholds = null,
    bool ProjectManagerMayOverride = false,
    string? Notes = null);

/// <summary>
/// A temporary override, in force through the end of <see cref="ExpiresOn"/> (UTC). Without extra amounts the ceiling stops
/// blocking (it still warns) while the override is in force.
/// </summary>
public sealed record GrantCeilingOverrideRequest(string Reason, DateOnly ExpiresOn, decimal? ExtraHours = null, decimal? ExtraCost = null, decimal? ExtraBilling = null);

public sealed record CeilingReasonRequest(string Reason);

/// <summary>Indirect rate caps (Fringe, Overhead, G&amp;A) and fee rate caps take <see cref="MaxRate"/> as a fraction; a fee amount cap takes <see cref="MaxAmount"/>.</summary>
public sealed record SaveRateCeilingRequest(RateCapKind Kind, decimal? MaxRate = null, decimal? MaxAmount = null, Guid? ClinId = null, int? FiscalYear = null, string? Notes = null);

/// <summary>
/// Ceilings on hours and dollars, overrides and indirect rate / fee caps. Reads: Finance, Admin and the managers of the
/// contract's (or project's) projects. Changes: Finance and Admin; overrides also the PM where the ceiling allows it.
/// </summary>
public interface ICeilingService
{
    Task<ContractCeilingStatusDto> GetContractStatusAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ProjectCeilingStatusDto> GetProjectStatusAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<CeilingStatusDto> GetAsync(Guid ceilingId, CancellationToken cancellationToken = default);

    Task<CeilingDto> CreateAsync(SaveCeilingRequest request, CancellationToken cancellationToken = default);

    Task<CeilingDto> UpdateAsync(Guid ceilingId, SaveCeilingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ceilings are never deleted; a deactivated one no longer counts or blocks.</summary>
    Task<CeilingDto> DeactivateAsync(Guid ceilingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CeilingOverrideDto>> ListOverridesAsync(Guid ceilingId, CancellationToken cancellationToken = default);

    Task<CeilingOverrideDto> GrantOverrideAsync(Guid ceilingId, GrantCeilingOverrideRequest request, CancellationToken cancellationToken = default);

    Task<CeilingOverrideDto> RevokeOverrideAsync(Guid overrideId, CeilingReasonRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RateCeilingDto>> ListRateCeilingsAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<RateCeilingDto> CreateRateCeilingAsync(Guid contractId, SaveRateCeilingRequest request, CancellationToken cancellationToken = default);

    Task<RateCeilingDto> DeactivateRateCeilingAsync(Guid rateCeilingId, CancellationToken cancellationToken = default);

    /// <summary>Sends any threshold alerts not sent yet for the contract's ceilings; returns how many were sent.</summary>
    Task<int> CheckAlertsAsync(Guid contractId, CancellationToken cancellationToken = default);
}
