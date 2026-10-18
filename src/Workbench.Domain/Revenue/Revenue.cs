namespace Workbench.Domain.Revenue;

/// <summary>How a project (or one of its CLINs) earns revenue, as in Costpoint's Project Revenue module.</summary>
public enum RevenueMethod
{
    /// <summary>Revenue equals what was billed (the fallback for projects without a setup).</summary>
    Billings,

    /// <summary>ASC 606 over time: cost to date ÷ estimate-at-completion cost × the total transaction price (fee included).</summary>
    CostToCost,

    /// <summary>T&amp;M / labor hour: revenue equals what was billed.</summary>
    TimeAndMaterialsBilled,

    /// <summary>T&amp;M / labor hour: approved hours × contract bill rate, plus allowable ODCs at cost or with a markup.</summary>
    TimeAndMaterialsIncurred,

    /// <summary>Cost-plus: allowable burdened cost plus the fee earned on it.</summary>
    CostPlusFee,

    /// <summary>Firm fixed price: the value of milestones completed.</summary>
    FixedPriceMilestones,

    /// <summary>Firm fixed price: units delivered × unit price.</summary>
    FixedPriceUnits,
}

/// <summary>
/// The revenue method for a project, or for one CLIN of it (<see cref="ClinId"/>). A CLIN setup covers the project's WBS
/// elements mapped to that CLIN; the project setup (no CLIN) covers the rest. One setup per project and CLIN.
/// </summary>
public sealed class RevenueSetup : Entity
{
    public Guid ProjectId { get; set; }
    public Guid? ClinId { get; set; }
    public RevenueMethod Method { get; set; }

    /// <summary>Cost-plus: the fee rate (0.07 = 7%); defaults to each CLIN's fee rate.</summary>
    public decimal? FeeRate { get; set; }

    /// <summary>T&amp;M as incurred: the markup on ODCs (0 = at cost).</summary>
    public decimal? OdcMarkupRate { get; set; }

    /// <summary>The total transaction price to suggest for EACs and cap units delivered; defaults to the CLIN or contract value.</summary>
    public decimal? TransactionPrice { get; set; }

    /// <summary>FFP units: the price of one unit.</summary>
    public decimal? UnitPrice { get; set; }

    public string? Notes { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum EstimateStatus
{
    Submitted,
    Approved,
    Rejected,
}

/// <summary>
/// An estimate at completion for a project (or CLIN): total burdened cost and total revenue (the transaction price,
/// fee included). Entered by the project manager or Finance and approved by Finance; the approved estimate with the
/// latest <see cref="AsOf"/> on or before a period's end drives that period's percent complete and loss provision.
/// Never edited: a revision is a new estimate.
/// </summary>
public sealed class RevenueEstimate : Entity
{
    public Guid ProjectId { get; set; }
    public Guid? ClinId { get; set; }
    public DateOnly AsOf { get; set; }
    public decimal EstimatedCost { get; set; }
    public decimal EstimatedRevenue { get; set; }
    public string? Notes { get; set; }
    public EstimateStatus Status { get; set; } = EstimateStatus.Submitted;
    public required string SubmittedBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>FFP units delivered on a project (or CLIN), for the units-delivered method. Append-only; corrections are negative quantities.</summary>
public sealed class RevenueDelivery : Entity
{
    public Guid ProjectId { get; set; }
    public Guid? ClinId { get; set; }
    public DateOnly DeliveredOn { get; set; }
    public decimal Quantity { get; set; }
    public required string Description { get; set; }
    public required string RecordedBy { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}

public enum RevenueRunStatus
{
    Posted,
    Reversed,
}

/// <summary>
/// One project's revenue recognized for one fiscal period, with its journal entry (source Revenue, source id = the run).
/// <see cref="PostedKey"/> is unique while the run is posted, so a project and period can't be posted twice; reversing
/// clears it so the period can be run again.
/// </summary>
public sealed class RevenueRun : Entity
{
    public Guid FiscalPeriodId { get; set; }
    public Guid ProjectId { get; set; }
    public RevenueRunStatus Status { get; set; } = RevenueRunStatus.Posted;

    /// <summary>"{projectId}:{periodId}" while posted; null once reversed.</summary>
    public string? PostedKey { get; set; }

    /// <summary>Cumulative revenue recognized through the period end.</summary>
    public decimal RecognizedToDate { get; set; }

    /// <summary>Revenue recognized in the period (all sources): recognized to date less the revenue booked through the previous period.</summary>
    public decimal PeriodRevenue { get; set; }

    /// <summary>Revenue booked by billing (invoices, accruals, manual entries) through the period end.</summary>
    public decimal BilledToDate { get; set; }

    /// <summary>The revenue this run's entry posts (credit positive).</summary>
    public decimal RevenueAdjustment { get; set; }

    /// <summary>Contract asset after the run: earned but not billed (AR unbilled).</summary>
    public decimal UnbilledBalance { get; set; }

    /// <summary>Contract liability after the run: billed in excess of revenue.</summary>
    public decimal BillingsInExcessBalance { get; set; }

    /// <summary>Provision for the expected loss still to be incurred, after the run.</summary>
    public decimal LossProvisionBalance { get; set; }

    public Guid? JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public required string PostedBy { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedAtUtc { get; set; }
    public string? ReversalReason { get; set; }

    public List<RevenueRunLine> Lines { get; } = [];

    public static string KeyFor(Guid projectId, Guid periodId) => $"{projectId:N}:{periodId:N}";
}

/// <summary>How a run computed one revenue unit (the project, or a CLIN of it): the method's inputs and the result.</summary>
public sealed class RevenueRunLine : Entity
{
    public Guid RunId { get; set; }
    public Guid? ClinId { get; set; }
    public RevenueMethod Method { get; set; }
    public decimal CostToDate { get; set; }
    public decimal? EstimatedCost { get; set; }
    public decimal? EstimatedRevenue { get; set; }

    /// <summary>Percent complete as a fraction (0.4 = 40%), for cost-to-cost.</summary>
    public decimal? PercentComplete { get; set; }

    public decimal RecognizedToDate { get; set; }
    public decimal BilledToDate { get; set; }
    public decimal ExpectedLoss { get; set; }
    public decimal LossProvision { get; set; }
    public string? Note { get; set; }
}
