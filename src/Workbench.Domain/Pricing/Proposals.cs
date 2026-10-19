using Workbench.Domain.Accounting;

namespace Workbench.Domain.Pricing;

/// <summary>The contract type a proposal is priced for. Mirrors <c>ProjectAccounting.ContractType</c> by name and value.</summary>
public enum PricingContractType
{
    FirmFixedPrice,
    TimeAndMaterials,
    LaborHour,
    CostPlusFixedFee,
    CostPlusAwardFee,
    CostPlusIncentiveFee,
}

public enum ProposalStatus
{
    Draft,
    Submitted,
    Won,
    Lost,
    Cancelled,
}

/// <summary>Where a labor line's direct (unburdened) hourly rate comes from.</summary>
public enum LaborRateMethod
{
    /// <summary>Average current cost rate of the active employees in the labor category.</summary>
    CategoryAverage,

    /// <summary>Median current cost rate of the active employees in the labor category.</summary>
    CategoryMedian,

    /// <summary>Average current cost rate of the people picked on the line.</summary>
    SelectedPeople,

    /// <summary>The named employee's current cost rate.</summary>
    NamedEmployee,

    /// <summary>A rate typed on the line (a TBD hire at a market rate, a salary survey figure).</summary>
    Manual,
}

/// <summary>How fee (cost-type) or profit (fixed-price) is set.</summary>
public enum FeeMethod
{
    FlatPercent,

    /// <summary>DFARS 215.404-71 weighted guidelines: performance risk (technical 60%, management 40%) + contract type risk + cost efficiency.</summary>
    WeightedGuidelines,
}

public enum PriceCostKind
{
    Odc,
    Travel,
    Materials,
    Subcontract,
}

/// <summary>
/// A bid being priced. Its prices are <see cref="PriceModel"/>s: the working price, what-if scenarios copied from it, and
/// locked versions (what was submitted). <see cref="OpportunityId"/> points at a CRM opportunity when there is one; it has
/// no foreign key here (the CRM module owns opportunities) and <see cref="OpportunityName"/> keeps a readable name.
/// </summary>
public sealed class Proposal : Entity
{
    public required string Number { get; set; }
    public required string Title { get; set; }
    public string? Customer { get; set; }
    public Guid? OpportunityId { get; set; }
    public string? OpportunityName { get; set; }
    public PricingContractType ContractType { get; set; }
    public ProposalStatus Status { get; set; } = ProposalStatus.Draft;

    /// <summary>The price the capture team believes wins (price-to-win).</summary>
    public decimal? TargetPrice { get; set; }

    public DateOnly? DueDate { get; set; }
    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    // The won handoff: what was generated from which locked price.
    public Guid? WonModelId { get; set; }
    public Guid? WonContractId { get; set; }
    public Guid? WonProjectId { get; set; }
    public Guid? WonBudgetVersionId { get; set; }
    public DateTime? HandedOffAtUtc { get; set; }
    public string? HandedOffBy { get; set; }
}

/// <summary>
/// One price for a proposal: its structure (periods, CLINs, WBS), labor and other costs, and how rates and fee are taken
/// from the books. A locked model is a submitted version: read-only, with the computed price frozen in
/// <see cref="LockedSnapshot"/> so later rate changes don't move it.
/// </summary>
public sealed class PriceModel : Entity
{
    public Guid ProposalId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The proposal's working price (exactly one per proposal); scenarios and versions are copies.</summary>
    public bool IsBase { get; set; }

    public Guid? SourceModelId { get; set; }

    /// <summary>Which indirect rate set to price with (forward pricing, provisional, target or actual).</summary>
    public IndirectRateBasis RateBasis { get; set; } = IndirectRateBasis.ForwardPricing;

    /// <summary>What-if overrides of the book rates for every year (fractions); null = the books.</summary>
    public decimal? FringeOverride { get; set; }
    public decimal? OverheadOverride { get; set; }
    public decimal? GaOverride { get; set; }

    /// <summary>Employee cost rates are read as of this date; escalation runs from it.</summary>
    public DateOnly RatesAsOf { get; set; }

    public LaborRateMethod DefaultRateMethod { get; set; } = LaborRateMethod.CategoryAverage;

    /// <summary>Annual escalation (0.03 = 3%) for labor categories without their own <see cref="PriceEscalation"/>.</summary>
    public decimal DefaultEscalation { get; set; }

    public FeeMethod FeeMethod { get; set; }

    /// <summary>The flat fee or profit rate on total cost (0.08 = 8%).</summary>
    public decimal FeeRate { get; set; }

    // Weighted guidelines values (fractions).
    public decimal WgTechnicalRisk { get; set; } = 0.05m;
    public decimal WgManagementRisk { get; set; } = 0.05m;
    public decimal WgContractTypeRisk { get; set; }
    public decimal WgCostEfficiency { get; set; }

    public bool IsLocked { get; set; }

    /// <summary>1, 2, ... among the proposal's locked versions.</summary>
    public int? Version { get; set; }

    public DateTime? LockedAtUtc { get; set; }
    public string? LockedBy { get; set; }
    public string? LockNote { get; set; }

    /// <summary>The computed price at lock time (JSON of the contract's result record).</summary>
    public string? LockedSnapshot { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<PricePeriod> Periods { get; } = [];
    public List<PriceClin> Clins { get; } = [];
    public List<PriceWbs> Wbs { get; } = [];
    public List<PriceEscalation> Escalations { get; } = [];
    public List<PriceLaborLine> LaborLines { get; } = [];
    public List<PriceCostLine> CostLines { get; } = [];
}

/// <summary>A period of performance: the base period or an option.</summary>
public sealed class PricePeriod : Entity
{
    public Guid ModelId { get; set; }
    public required string Name { get; set; }
    public int Order { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public bool IsOption { get; set; }
}

/// <summary>
/// A CLIN of the bid (for example <c>0001</c> Labor, <c>0002</c> Travel). Its value is priced for every period; at award
/// option periods become <c>1001</c>, <c>2001</c>, ... (the first digit is the period).
/// </summary>
public sealed class PriceClin : Entity
{
    public Guid ModelId { get; set; }
    public required string Number { get; set; }
    public required string Description { get; set; }
    public int Order { get; set; }
}

/// <summary>A basis-of-estimate WBS element, with the narrative explaining how its hours and costs were estimated.</summary>
public sealed class PriceWbs : Entity
{
    public Guid ModelId { get; set; }
    public Guid? ParentId { get; set; }

    /// <summary>For example <c>01</c> or <c>01.02</c>; at award it becomes <c>{project code}.{code}</c>.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public string? BasisOfEstimate { get; set; }
    public int Order { get; set; }
}

/// <summary>Annual escalation for one labor category (overrides the model's default).</summary>
public sealed class PriceEscalation : Entity
{
    public Guid ModelId { get; set; }
    public Guid LaborCategoryId { get; set; }
    public decimal Rate { get; set; }
}

/// <summary>A labor line: a labor category on a WBS element and CLIN, hours by period, and optionally who will do it.</summary>
public sealed class PriceLaborLine : Entity
{
    public Guid ModelId { get; set; }
    public Guid WbsId { get; set; }
    public Guid? ClinId { get; set; }
    public Guid LaborCategoryId { get; set; }

    /// <summary>A named person (key personnel); null for "TBD".</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>How the line names its staff when it isn't a named employee (for example "TBD hire").</summary>
    public string? StaffName { get; set; }

    public LaborRateMethod RateMethod { get; set; }

    /// <summary>For <see cref="LaborRateMethod.SelectedPeople"/>: employee ids, comma separated.</summary>
    public string? SelectedEmployeeIds { get; set; }

    /// <summary>For <see cref="LaborRateMethod.Manual"/>: the direct hourly rate.</summary>
    public decimal? ManualRate { get; set; }

    public string? Note { get; set; }
    public int Order { get; set; }
    public List<PriceLaborHours> Hours { get; } = [];
}

public sealed class PriceLaborHours : Entity
{
    public Guid LineId { get; set; }
    public Guid PeriodId { get; set; }
    public decimal Hours { get; set; }
}

/// <summary>
/// A non-labor cost in one period: an ODC, materials, a subcontractor's priced quote, or travel. Travel is priced from
/// the GSA per diem tables (lodging and M&amp;IE for the destination) plus airfare and other costs per trip. Unallowable
/// lines are shown but never priced.
/// </summary>
public sealed class PriceCostLine : Entity
{
    public Guid ModelId { get; set; }
    public Guid WbsId { get; set; }
    public Guid? ClinId { get; set; }
    public Guid PeriodId { get; set; }
    public PriceCostKind Kind { get; set; }
    public required string Description { get; set; }

    /// <summary>Quantity × unit cost for ODCs, materials and subcontracts.</summary>
    public decimal Quantity { get; set; } = 1m;

    public decimal UnitCost { get; set; }
    public bool IsUnallowable { get; set; }

    // Subcontract quotes.
    public string? Vendor { get; set; }
    public string? QuoteReference { get; set; }

    // Travel.
    public string? Destination { get; set; }
    public string? State { get; set; }
    public int Trips { get; set; }
    public int Travelers { get; set; }

    /// <summary>Nights per trip (a trip of N nights has N + 1 travel days, the first and last at 75% M&amp;IE).</summary>
    public int Nights { get; set; }

    public decimal AirfarePerTrip { get; set; }

    /// <summary>Rental car, mileage, parking and the like, per traveler per trip.</summary>
    public decimal OtherPerTrip { get; set; }

    public int Order { get; set; }
}
