using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Financials;

namespace Workbench.Contracts.Pricing;

// Proposal pricing. Everything here exposes cost rates, indirect rates and fee, so it is for Finance, administrators and
// the Pricing org role only; the won handoff (contract CLINs, bill rates, budgets) is Finance/Admin. Rates are fractions
// (0.32 = 32%). Enums mirror Workbench.Domain.Pricing by name and value.

public enum PricingContractKind { FirmFixedPrice, TimeAndMaterials, LaborHour, CostPlusFixedFee, CostPlusAwardFee, CostPlusIncentiveFee }

public enum ProposalState { Draft, Submitted, Won, Lost, Cancelled }

public enum LaborRateSource { CategoryAverage, CategoryMedian, SelectedPeople, NamedEmployee, Manual }

public enum FeeMethodKind { FlatPercent, WeightedGuidelines }

public enum PriceCostType { Odc, Travel, Materials, Subcontract }

// ---- Proposals ----

public sealed record ProposalSummaryDto(
    Guid Id,
    string Number,
    string Title,
    string? Customer,
    Guid? OpportunityId,
    string? OpportunityName,
    PricingContractKind ContractType,
    ProposalState Status,
    decimal? TargetPrice,
    DateOnly? DueDate,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal? Price,
    int LockedVersions,
    DateTime UpdatedAtUtc);

/// <summary>One price of a proposal: the working price (<see cref="IsBase"/>), a scenario, or a locked version.</summary>
public sealed record PriceModelRefDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsBase,
    bool IsLocked,
    int? Version,
    DateTime? LockedAtUtc,
    string? LockedBy,
    string? LockNote,
    Guid? SourceModelId,
    DateTime UpdatedAtUtc);

public sealed record ProposalDto(
    Guid Id,
    string Number,
    string Title,
    string? Customer,
    Guid? OpportunityId,
    string? OpportunityName,
    PricingContractKind ContractType,
    ProposalState Status,
    decimal? TargetPrice,
    DateOnly? DueDate,
    string? Notes,
    string CreatedBy,
    DateTime CreatedAtUtc,
    IReadOnlyList<PriceModelRefDto> Models,
    HandoffDto? Handoff);

public sealed record HandoffDto(Guid ModelId, Guid ContractId, Guid ProjectId, Guid? BudgetVersionId, DateTime HandedOffAtUtc, string HandedOffBy);

/// <summary>
/// Creates or edits a proposal. A new proposal gets a working price with one base period starting
/// <see cref="FirstPeriodStart"/> (default: the first of next month) and <see cref="OptionYears"/> option years.
/// </summary>
public sealed record SaveProposalRequest(
    string Number,
    string Title,
    PricingContractKind ContractType,
    string? Customer = null,
    Guid? OpportunityId = null,
    string? OpportunityName = null,
    decimal? TargetPrice = null,
    DateOnly? DueDate = null,
    string? Notes = null,
    ProposalState? Status = null,
    DateOnly? FirstPeriodStart = null,
    int OptionYears = 0);

// ---- A price model's inputs ----

public sealed record PricePeriodDto(Guid Id, string Name, int Order, DateOnly Start, DateOnly End, bool IsOption);

public sealed record PriceClinDto(Guid Id, string Number, string Description, int Order);

public sealed record PriceWbsDto(Guid Id, Guid? ParentId, string Code, string Name, string? BasisOfEstimate, int Order);

public sealed record PriceEscalationDto(Guid LaborCategoryId, decimal Rate);

public sealed record PriceLaborLineDto(
    Guid Id,
    Guid WbsId,
    Guid? ClinId,
    Guid LaborCategoryId,
    Guid? EmployeeId,
    string? StaffName,
    LaborRateSource RateMethod,
    IReadOnlyList<Guid> SelectedEmployeeIds,
    decimal? ManualRate,
    string? Note,
    int Order,
    IReadOnlyDictionary<Guid, decimal> HoursByPeriod);

public sealed record PriceCostLineDto(
    Guid Id,
    Guid WbsId,
    Guid? ClinId,
    Guid PeriodId,
    PriceCostType Kind,
    string Description,
    decimal Quantity,
    decimal UnitCost,
    bool IsUnallowable,
    string? Vendor,
    string? QuoteReference,
    string? Destination,
    string? State,
    int Trips,
    int Travelers,
    int Nights,
    decimal AirfarePerTrip,
    decimal OtherPerTrip,
    int Order);

public sealed record PriceSettingsDto(
    RateBasis RateBasis,
    decimal? FringeOverride,
    decimal? OverheadOverride,
    decimal? GaOverride,
    DateOnly RatesAsOf,
    LaborRateSource DefaultRateMethod,
    decimal DefaultEscalation,
    FeeMethodKind FeeMethod,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal FeeRate,
    decimal WgTechnicalRisk,
    decimal WgManagementRisk,
    decimal WgContractTypeRisk,
    decimal WgCostEfficiency);

public sealed record PriceModelDto(
    Guid Id,
    Guid ProposalId,
    string ProposalNumber,
    string ProposalTitle,
    PricingContractKind ContractType,
    PriceModelRefDto Ref,
    PriceSettingsDto Settings,
    IReadOnlyList<PricePeriodDto> Periods,
    IReadOnlyList<PriceClinDto> Clins,
    IReadOnlyList<PriceWbsDto> Wbs,
    IReadOnlyList<PriceEscalationDto> Escalations,
    IReadOnlyList<PriceLaborLineDto> LaborLines,
    IReadOnlyList<PriceCostLineDto> CostLines);

public sealed record SavePriceSettingsRequest(PriceSettingsDto Settings);

/// <summary>A row to save. A null <see cref="Id"/> adds one; rows left out are removed.</summary>
public sealed record SavePricePeriodRequest(Guid? Id, string Name, DateOnly Start, DateOnly End, bool IsOption);

public sealed record SavePriceClinRequest(Guid? Id, string Number, string Description);

/// <summary><see cref="ParentCode"/> nests the element under another element of the same save.</summary>
public sealed record SavePriceWbsRequest(Guid? Id, string Code, string Name, string? BasisOfEstimate = null, string? ParentCode = null);

public sealed record SavePriceLaborLineRequest(
    Guid? Id,
    Guid WbsId,
    Guid? ClinId,
    Guid LaborCategoryId,
    LaborRateSource RateMethod,
    IReadOnlyDictionary<Guid, decimal> HoursByPeriod,
    Guid? EmployeeId = null,
    string? StaffName = null,
    IReadOnlyList<Guid>? SelectedEmployeeIds = null,
    decimal? ManualRate = null,
    string? Note = null);

public sealed record SavePriceCostLineRequest(
    Guid? Id,
    Guid WbsId,
    Guid? ClinId,
    Guid PeriodId,
    PriceCostType Kind,
    string Description,
    decimal Quantity = 1,
    decimal UnitCost = 0,
    bool IsUnallowable = false,
    string? Vendor = null,
    string? QuoteReference = null,
    string? Destination = null,
    string? State = null,
    int Trips = 0,
    int Travelers = 0,
    int Nights = 0,
    decimal AirfarePerTrip = 0,
    decimal OtherPerTrip = 0);

public sealed record CopyPriceModelRequest(string Name, string? Description = null);

/// <summary>Locks a price as a version. <see cref="MarkSubmitted"/> also sets the proposal to Submitted.</summary>
public sealed record LockPriceModelRequest(string? Note = null, bool MarkSubmitted = true);

// ---- The computed price ----

/// <summary>Cost build-up and price for a slice (the whole price, a CLIN in a period, a WBS element, a period).</summary>
public sealed record PriceFiguresDto(
    decimal Hours,
    decimal DirectLabor,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative,
    decimal Odc,
    decimal Travel,
    decimal Materials,
    decimal Subcontracts,
    decimal TotalCost,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Fee,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Price)
{
    public static readonly PriceFiguresDto Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public decimal DirectOther => Odc + Travel + Materials + Subcontracts;

    public PriceFiguresDto Add(PriceFiguresDto o) => new(Hours + o.Hours, DirectLabor + o.DirectLabor, Fringe + o.Fringe, Overhead + o.Overhead,
        GeneralAndAdministrative + o.GeneralAndAdministrative, Odc + o.Odc, Travel + o.Travel, Materials + o.Materials, Subcontracts + o.Subcontracts,
        TotalCost + o.TotalCost, Fee + o.Fee, Price + o.Price);
}

/// <summary>The indirect rates applied to a period: the fiscal year and basis they came from, and the burden per labor dollar.</summary>
public sealed record PeriodRatesDto(
    Guid PeriodId,
    string PeriodName,
    int FiscalYear,
    int RatesFiscalYear,
    RateBasis Basis,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative,
    decimal LaborMultiplier,
    bool Overridden);

/// <summary>One step of the cascade, in order: the pool and what it's applied to.</summary>
public sealed record CascadeStepDto(string Pool, string Base);

public sealed record PriceCellDto(Guid? ClinId, string ClinNumber, string ClinDescription, Guid PeriodId, string PeriodName, PriceFiguresDto Figures);

public sealed record LaborBuildUpDto(
    Guid LineId,
    string WbsCode,
    string? ClinNumber,
    Guid LaborCategoryId,
    string LaborCategoryCode,
    string LaborCategoryName,
    string Staff,
    string RateSource,
    decimal BaseRate,
    decimal Escalation,
    Guid PeriodId,
    string PeriodName,
    decimal Hours,
    decimal DirectRate,
    decimal DirectLabor);

public sealed record CostBuildUpDto(
    Guid LineId,
    string WbsCode,
    string? ClinNumber,
    string PeriodName,
    PriceCostType Kind,
    string Description,
    string Detail,
    decimal Amount,
    bool IsUnallowable);

/// <summary>A fully burdened hourly bill rate for T&amp;M / labor-hour pricing: direct rate × the cascade × (1 + fee).</summary>
public sealed record BillRateLineDto(
    Guid LaborCategoryId,
    string LaborCategoryCode,
    string LaborCategoryName,
    Guid PeriodId,
    string PeriodName,
    DateOnly PeriodStart,
    decimal Hours,
    decimal DirectRate,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Fee,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal BillRate);

public sealed record WbsPriceDto(Guid WbsId, string Code, string Name, string? BasisOfEstimate, int Depth, PriceFiguresDto Figures);

public sealed record PriceToWinDto(decimal TargetPrice, [property: SensitiveField(Permission.FieldsPricingFee)] decimal Difference, [property: SensitiveField(Permission.FieldsPricingFee)] decimal DifferencePercent, [property: SensitiveField(Permission.FieldsPricingFee)] decimal? FeeRateAtTarget, [property: SensitiveField(Permission.FieldsPricingFee)] bool UnderTarget);

public sealed record PriceResultDto(
    Guid ModelId,
    string ModelName,
    bool IsLocked,
    int? Version,
    DateTime ComputedAtUtc,
    PricingContractKind ContractType,
    RateBasis RateBasis,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal FeeRate,
    [property: SensitiveField(Permission.FieldsPricingFee)] string FeeExplanation,
    PriceFiguresDto Totals,
    decimal UnallowableExcluded,

    /// <summary>Price of labor (burdened, with fee) per direct labor dollar.</summary>
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal WrapRate,

    /// <summary>Burdened cost per direct labor dollar (no fee).</summary>
    decimal CostMultiplier,

    /// <summary>Fee ÷ price.</summary>
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Margin,
    PriceToWinDto? PriceToWin,
    IReadOnlyList<CascadeStepDto> Cascade,
    IReadOnlyList<PeriodRatesDto> PeriodRates,
    IReadOnlyList<PriceCellDto> Cells,
    IReadOnlyList<PriceFiguresByPeriodDto> Periods,
    IReadOnlyList<WbsPriceDto> Wbs,
    IReadOnlyList<LaborBuildUpDto> Labor,
    IReadOnlyList<CostBuildUpDto> OtherCosts,
    IReadOnlyList<BillRateLineDto> BillRates,
    IReadOnlyList<string> Notes);

public sealed record PriceFiguresByPeriodDto(Guid PeriodId, string PeriodName, DateOnly Start, DateOnly End, bool IsOption, PriceFiguresDto Figures);

/// <summary>Every price of a proposal side by side, with the difference to the target price.</summary>
public sealed record ScenarioComparisonDto(Guid ProposalId, decimal? TargetPrice, IReadOnlyList<ScenarioRowDto> Rows);

public sealed record ScenarioRowDto(
    PriceModelRefDto Model,
    decimal Hours,
    decimal DirectLabor,
    decimal TotalCost,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Fee,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Price,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal FeeRate,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal WrapRate,
    [property: SensitiveField(Permission.FieldsPricingFee)] decimal Margin,
    PriceToWinDto? PriceToWin);

// ---- Lookups for the editor ----

public sealed record PricingLaborCategoryDto(Guid Id, string Code, string Name, int People, decimal? AverageRate, decimal? MedianRate);

public sealed record PricingEmployeeDto(Guid Id, string Name, Guid? LaborCategoryId, decimal? CostRate);

public sealed record PricingContractRefDto(Guid Id, string ContractNumber, string Name, IReadOnlyList<PricingProjectRefDto> Projects);

public sealed record PricingProjectRefDto(Guid Id, string Code, string Name);

/// <summary>What the editor offers: labor categories with their rate statistics, people with current cost rates (as of the date), and contracts for the handoff.</summary>
public sealed record PricingOptionsDto(
    DateOnly AsOf,
    IReadOnlyList<PricingLaborCategoryDto> LaborCategories,
    IReadOnlyList<PricingEmployeeDto> Employees,
    IReadOnlyList<PricingContractRefDto> Contracts);

public sealed record PricingHistoryDto(DateTime OccurredAtUtc, string Actor, string EntityType, Guid EntityId, string Action, string? Reason);

public sealed record PricingExportFile(string FileName, string ContentType, byte[] Content);

// ---- Won handoff ----

/// <summary>
/// Generates execution data from a locked price: contract CLINs (one per CLIN and period; option periods get the period
/// digit, 0001 → 1001), T&amp;M/labor-hour bill rates per labor category and period, the project's WBS elements and a budget
/// version (labor hours by WBS, labor category and fiscal period at the bid's direct rates; ODCs by type).
/// </summary>
public sealed record HandoffRequest(
    Guid ContractId,
    Guid ProjectId,
    BudgetVersionKind BudgetKind = BudgetVersionKind.Current,
    bool CreateClins = true,
    bool CreateBillRates = true,
    bool CreateBudget = true,
    string? BudgetName = null);

public sealed record HandoffResultDto(
    Guid ProposalId,
    Guid ContractId,
    Guid ProjectId,
    Guid? BudgetVersionId,
    int ClinsCreated,
    int ClinsUpdated,
    int BillRatesAdded,
    int WbsCreated,
    int BudgetLines,
    IReadOnlyList<string> Notes);

public interface IProposalPricingService
{
    Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default);

    Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default);

    Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default);

    Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default);

    Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default);

    Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default);

    /// <summary>The price from the current books (a locked version returns the price frozen when it was locked).</summary>
    Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default);

    /// <summary>Copies a price (any, including a locked version) to a new, editable scenario.</summary>
    Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a scenario. The working price and locked versions can't be deleted.</summary>
    Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default);

    /// <summary>Locks a copy of the price as the next version (read-only, audited); the price itself stays editable.</summary>
    Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default);

    Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default);

    Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>The cost volume workbook: summary by CLIN and period, labor build-up, indirect rates, basis of estimate, T&amp;M rates.</summary>
    Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default);

    /// <summary>The won handoff from a locked version (Finance/Admin). Marks the proposal Won.</summary>
    Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default);
}

// ---- Opportunity link ----

public sealed record OpportunityRefDto(Guid Id, string Name, string? Customer = null);

/// <summary>
/// Finds CRM opportunities to link a proposal to. Until the CRM module is merged the default finds nothing and the link
/// is an id plus a typed name; the CRM adapter (over <c>Contracts.Crm.IOpportunityService</c>) replaces it with one line.
/// </summary>
public interface IPricingOpportunityLookup
{
    Task<IReadOnlyList<OpportunityRefDto>> SearchAsync(string? search, CancellationToken cancellationToken = default);

    Task<OpportunityRefDto?> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default);
}
