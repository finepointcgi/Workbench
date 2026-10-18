using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;

namespace Workbench.Contracts.IndirectRates;

// Indirect rates, allocations and burdened cost. Rates are fractions (0.32 = 32%). IIndirectRateService is for Finance
// and administrators; IBurdenedFinancialsService follows the project financials rule (Finance, Admin, the project's PM).

public enum AllocationBaseKind { DirectLabor, DirectLaborPlusFringe, TotalCostInput, ValueAdded }

// ---- Pool setup ----

/// <summary>
/// How a pool is built: expense accounts by cost class and/or explicit accounts (a summary account brings its
/// descendants), plus the indirect labor accounts the posting rules map to the pool. <see cref="PoolAccounts"/> lists
/// the accounts that resolve into the pool today.
/// </summary>
public sealed record PoolSetupDto(
    Guid Id,
    CostPool Pool,
    string Name,
    AllocationBaseKind Base,
    int CascadeOrder,
    bool IsActive,
    IReadOnlyList<CostClass> CostClasses,
    bool IncludeMappedIndirectLabor,
    IReadOnlyList<Guid> AccountIds,
    IReadOnlyList<string> PoolAccounts,
    Guid AppliedAccountId,
    string AppliedAccount,
    Guid AbsorbedAccountId,
    string AbsorbedAccount);

public sealed record SavePoolSetupRequest(
    string Name,
    AllocationBaseKind Base,
    int CascadeOrder,
    bool IsActive,
    IReadOnlyList<CostClass> CostClasses,
    bool IncludeMappedIndirectLabor,
    IReadOnlyList<Guid> AccountIds,
    Guid AppliedAccountId,
    Guid AbsorbedAccountId);

// ---- Rates ----

public sealed record IndirectRateDto(
    Guid Id,
    CostPool Pool,
    int FiscalYear,
    RateBasis Basis,
    decimal Rate,
    DateOnly EffectiveFrom,
    string? Note,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>Sets a pool's rate for a fiscal year and basis. Replaces (supersedes) the rate in force, if any.</summary>
public sealed record SaveIndirectRateRequest(
    CostPool Pool,
    int FiscalYear,
    RateBasis Basis,
    decimal Rate,
    DateOnly? EffectiveFrom = null,
    string? Note = null,
    bool Approve = false,
    string? Reason = null);

public sealed record WithdrawIndirectRateRequest(string Reason);

// ---- Rate calculation (actual rates from the GL) ----

public sealed record PoolRateDto(
    CostPool Pool,
    string Name,
    AllocationBaseKind Base,
    decimal PoolCost,
    decimal BaseAmount,
    decimal? ActualRate,
    decimal? ProvisionalRate,

    /// <summary>Actual rate − provisional rate.</summary>
    decimal? Variance,

    /// <summary>Burden credited to the pool's applied (contra) account in the range by allocation runs.</summary>
    decimal Applied,

    /// <summary>Pool cost − applied: positive is under-applied (rates too low), negative over-applied.</summary>
    decimal UnderApplied);

/// <summary>One pool in one period: the month's own rate and the fiscal year to date through that month.</summary>
public sealed record RateTrendPointDto(
    Guid FiscalPeriodId,
    string PeriodName,
    int FiscalYear,
    CostPool Pool,
    decimal PoolCost,
    decimal BaseAmount,
    decimal? MonthRate,
    decimal YtdPoolCost,
    decimal YtdBase,
    decimal? YtdRate,
    decimal? ProvisionalRate);

public sealed record RateCalculationDto(
    Guid FromPeriodId,
    Guid ToPeriodId,
    string FromPeriodName,
    string ToPeriodName,
    int FiscalYear,
    IReadOnlyList<PoolRateDto> Pools,
    IReadOnlyList<RateTrendPointDto> Trend,
    IReadOnlyList<string> Notes);

// ---- Allocation runs ----

public sealed record AllocationLineDto(
    Guid ProjectId,
    string ProjectCode,
    Guid WbsElementId,
    string WbsCode,
    decimal DirectLabor,
    decimal DirectOdc,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative)
{
    public decimal Burden => Fringe + Overhead + GeneralAndAdministrative;
}

public sealed record PostedAllocationDto(Guid JournalEntryId, string EntryNumber, DateOnly Date, int Revision, decimal Total, string? PostedBy, DateTime? PostedAtUtc);

/// <summary>
/// What the allocation would post for a period (provisional rates on the approved direct labor and allowable ODCs dated
/// in it) and what is posted. <see cref="IsCurrent"/> is true when the posted entry matches the preview.
/// </summary>
public sealed record AllocationPreviewDto(
    Guid FiscalPeriodId,
    string PeriodName,
    int FiscalYear,
    bool PeriodClosed,
    AppliedRates Rates,
    IReadOnlyList<AllocationLineDto> Lines,
    decimal Total,
    PostedAllocationDto? Posted,
    bool IsCurrent,
    IReadOnlyList<string> Notes);

public sealed record AllocationRunSummaryDto(
    Guid FiscalPeriodId,
    string PeriodName,
    DateOnly Start,
    DateOnly End,
    bool PeriodClosed,
    decimal PreviewTotal,
    PostedAllocationDto? Posted,
    bool IsCurrent);

public sealed record ReverseAllocationRequest(string Reason);

// ---- Year-end true-up ----

public sealed record TrueUpLineDto(
    Guid ProjectId,
    string ProjectCode,
    Guid? ContractId,
    string? ContractNumber,
    Guid WbsElementId,
    string WbsCode,
    Guid? ClinId,
    decimal DirectLabor,
    decimal DirectOdc,
    decimal ProvisionalBurden,
    decimal ActualBurden,
    decimal FringeDifference,
    decimal OverheadDifference,
    decimal GeneralAndAdministrativeDifference)
{
    public decimal Difference => ActualBurden - ProvisionalBurden;
}

public sealed record TrueUpTotalDto(Guid Id, string Code, string Name, decimal DirectCost, decimal ProvisionalBurden, decimal ActualBurden)
{
    public decimal Difference => ActualBurden - ProvisionalBurden;
}

/// <summary>
/// Cost at provisional vs actual rates for a fiscal year, per WBS, project and contract. Positive differences are
/// additional cost (actual rates above provisional): the input to billing's true-up invoices.
/// </summary>
public sealed record TrueUpDto(
    int FiscalYear,
    bool HasActualRates,
    AppliedRates Provisional,
    AppliedRates Actual,
    IReadOnlyList<TrueUpLineDto> Lines,
    IReadOnlyList<TrueUpTotalDto> Projects,
    IReadOnlyList<TrueUpTotalDto> Contracts,
    PostedAllocationDto? Posted,
    IReadOnlyList<string> Notes)
{
    public decimal Difference => Lines.Sum(l => l.Difference);
}

/// <summary>Posts the true-up adjustment dated <see cref="Date"/> (default: the last day of the fiscal year, which must be open).</summary>
public sealed record PostTrueUpRequest(DateOnly? Date = null);

public interface IIndirectRateService
{
    Task<IReadOnlyList<PoolSetupDto>> ListPoolSetupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or changes a pool's setup. Audited.</summary>
    Task<PoolSetupDto> SavePoolSetupAsync(CostPool pool, SavePoolSetupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Installs fringe, overhead and G&amp;A setups (and the applied-burden accounts) where missing; returns how many were created.</summary>
    Task<int> InstallDefaultPoolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Rates in force, optionally for one fiscal year; with <paramref name="includeHistory"/>, superseded rows too.</summary>
    Task<IReadOnlyList<IndirectRateDto>> ListRatesAsync(int? fiscalYear = null, bool includeHistory = false, CancellationToken cancellationToken = default);

    /// <summary>Sets a rate; an existing rate for the pool, year and basis is superseded (a reason is then required). Audited.</summary>
    Task<IndirectRateDto> SaveRateAsync(SaveIndirectRateRequest request, CancellationToken cancellationToken = default);

    Task<IndirectRateDto> ApproveRateAsync(Guid rateId, CancellationToken cancellationToken = default);

    /// <summary>Withdraws a rate (it stays in the history). Audited.</summary>
    Task WithdrawRateAsync(Guid rateId, WithdrawIndirectRateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Actual pool cost, base and rate per pool from GL balances over a period range, with the monthly and year-to-date trend.</summary>
    Task<RateCalculationDto> GetRateCalculationAsync(Guid fromPeriodId, Guid toPeriodId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AllocationRunSummaryDto>> ListAllocationRunsAsync(int fiscalYear, CancellationToken cancellationToken = default);

    Task<AllocationPreviewDto> PreviewAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts the period's allocation (Allocation journal). Re-running reverses the posted allocation and posts it again.
    /// <see cref="InvalidOperationException"/> when the period is closed.
    /// </summary>
    Task<AllocationPreviewDto> PostAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<AllocationPreviewDto> ReverseAllocationAsync(Guid fiscalPeriodId, ReverseAllocationRequest request, CancellationToken cancellationToken = default);

    Task<TrueUpDto> GetTrueUpAsync(int fiscalYear, CancellationToken cancellationToken = default);

    /// <summary>Posts the adjusting allocation for the true-up (re-posting reverses the earlier one). Needs actual rates for every pool.</summary>
    Task<TrueUpDto> PostTrueUpAsync(int fiscalYear, PostTrueUpRequest request, CancellationToken cancellationToken = default);
}

// ---- Burdened project financials ----

public sealed record BurdenedFiguresDto(
    decimal BudgetAmount,
    decimal DirectActual,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative,
    decimal DirectEtc,
    decimal BurdenedEtc,
    decimal DirectEac)
{
    public decimal Burden => Fringe + Overhead + GeneralAndAdministrative;

    public decimal BurdenedActual => DirectActual + Burden;

    public decimal BurdenedEac => BurdenedActual + BurdenedEtc;
}

public sealed record BurdenedWbsRowDto(Guid WbsElementId, Guid? ParentId, string Code, string Name, int Depth, bool IsLeaf, BurdenedFiguresDto Figures);

/// <summary>
/// Project financials at burdened cost: actuals burdened with the actual rates where set (else provisional), and the ETC
/// burdened at the rate experienced so far on each element (or the current year's labor burden when it has no actuals).
/// </summary>
public sealed record BurdenedFinancialsDto(
    Guid ProjectId,
    bool HasRates,
    IReadOnlyList<AppliedRates> Rates,
    BurdenedFiguresDto Totals,
    IReadOnlyList<BurdenedWbsRowDto> Rows,
    IReadOnlyList<string> Notes);

public interface IBurdenedFinancialsService
{
    Task<BurdenedFinancialsDto> GetProjectAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default);
}
