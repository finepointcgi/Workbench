namespace Workbench.Contracts.Revenue;

/// <summary>How a project (or CLIN) earns revenue. Mirrors the domain's <c>RevenueMethod</c>.</summary>
public enum RevenueMethodKind
{
    Billings,
    CostToCost,
    TimeAndMaterialsBilled,
    TimeAndMaterialsIncurred,
    CostPlusFee,
    FixedPriceMilestones,
    FixedPriceUnits,
}

public enum EstimateState { Submitted, Approved, Rejected }

public enum RevenueRunState { Posted, Reversed }

// ---- Setup ----

/// <summary>A project's (or CLIN's) revenue method and its parameters.</summary>
public sealed record RevenueSetupDto(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid? ContractId,
    string? ContractNumber,
    Guid? ClinId,
    string? ClinNumber,
    RevenueMethodKind Method,
    decimal? FeeRate,
    decimal? OdcMarkupRate,
    decimal? TransactionPrice,
    decimal? UnitPrice,
    string? Notes,
    string UpdatedBy,
    DateTime UpdatedAtUtc);

/// <summary>
/// Creates or replaces the setup for a project, or one of its CLINs (<see cref="ClinId"/>). Rates are fractions
/// (0.07 = 7%). Cost-plus may set <see cref="FeeRate"/> (default: each CLIN's fee); T&amp;M as incurred
/// <see cref="OdcMarkupRate"/> (default: at cost); FFP units needs <see cref="UnitPrice"/>.
/// </summary>
public sealed record SaveRevenueSetupRequest(
    Guid ProjectId,
    RevenueMethodKind Method,
    Guid? ClinId = null,
    decimal? FeeRate = null,
    decimal? OdcMarkupRate = null,
    decimal? TransactionPrice = null,
    decimal? UnitPrice = null,
    string? Notes = null);

/// <summary>A project with a contract and how its revenue is set up (Finance's setup list).</summary>
public sealed record RevenueProjectSummaryDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid ContractId,
    string ContractNumber,
    string ContractType,
    IReadOnlyList<RevenueSetupDto> Setups,
    EstimateDto? CurrentEstimate,
    int PendingEstimates);

// ---- Estimates at completion ----

public sealed record EstimateDto(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    Guid? ClinId,
    string? ClinNumber,
    DateOnly AsOf,
    decimal EstimatedCost,
    decimal EstimatedRevenue,
    string? Notes,
    EstimateState Status,
    string SubmittedBy,
    DateTime SubmittedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionNote)
{
    /// <summary>Expected margin at completion; negative is a loss contract.</summary>
    public decimal EstimatedMargin => EstimatedRevenue - EstimatedCost;

    public bool IsLoss => EstimatedCost > EstimatedRevenue;
}

/// <summary>
/// A new estimate at completion as of <see cref="AsOf"/>: total burdened cost and total revenue (transaction price,
/// fee included). The project manager or Finance submits; Finance approves.
/// </summary>
public sealed record SubmitEstimateRequest(Guid ProjectId, DateOnly AsOf, decimal EstimatedCost, decimal EstimatedRevenue, Guid? ClinId = null, string? Notes = null);

public sealed record DecideEstimateRequest(string? Note = null);

// ---- Deliveries (FFP units) ----

public sealed record RevenueDeliveryDto(Guid Id, Guid ProjectId, Guid? ClinId, string? ClinNumber, DateOnly DeliveredOn, decimal Quantity, string Description, string RecordedBy, DateTime RecordedAtUtc);

public sealed record RecordDeliveryRequest(Guid ProjectId, DateOnly DeliveredOn, decimal Quantity, string Description, Guid? ClinId = null);

// ---- Runs ----

/// <summary>One revenue unit (the project, or a CLIN of it) as a run computes it.</summary>
public sealed record RevenueLineDto(
    Guid? ClinId,
    string? ClinNumber,
    RevenueMethodKind Method,
    decimal CostToDate,
    decimal? EstimatedCost,
    decimal? EstimatedRevenue,
    decimal? PercentComplete,
    decimal RecognizedToDate,
    decimal BilledToDate,
    decimal ExpectedLoss,
    decimal LossProvision,
    string? Note);

/// <summary>A journal line the run posts (or would post).</summary>
public sealed record RevenueEntryLineDto(string AccountNumber, string AccountName, decimal Debit, decimal Credit, string? Memo);

/// <summary>
/// One project's revenue for a period: recognized to date, billed to date, the period's revenue and the balances the
/// entry leaves (contract asset, contract liability, loss provision), with the journal lines it posts.
/// </summary>
public sealed record RevenueProjectRunDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? ContractNumber,
    decimal RecognizedToDate,
    decimal BilledToDate,
    decimal PeriodRevenue,
    decimal RevenueAdjustment,
    decimal UnbilledBalance,
    decimal BillingsInExcessBalance,
    decimal LossProvisionBalance,
    IReadOnlyList<RevenueLineDto> Lines,
    IReadOnlyList<RevenueEntryLineDto> Entry,
    IReadOnlyList<string> Notes)
{
    public bool HasEntry => Entry.Count > 0;
}

/// <summary>A preview row: what posting would do now, and the run already posted for the period (if any).</summary>
public sealed record RevenuePreviewRowDto(RevenueProjectRunDto Computed, RevenueRunDto? Posted)
{
    /// <summary>Posted, but the figures changed since (late approvals, a new EAC): reverse and run again to catch up.</summary>
    public bool IsStale => Posted is not null && Computed.HasEntry;
}

public sealed record RevenuePreviewDto(Guid FiscalPeriodId, string PeriodName, DateOnly PeriodEnd, bool PeriodIsClosed, IReadOnlyList<RevenuePreviewRowDto> Projects)
{
    public decimal PeriodRevenue => Projects.Sum(p => p.Posted?.PeriodRevenue ?? p.Computed.PeriodRevenue);

    public int ToPost => Projects.Count(p => p.Posted is null && p.Computed.HasEntry);
}

public sealed record RevenueRunDto(
    Guid Id,
    Guid FiscalPeriodId,
    string PeriodName,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    RevenueRunState Status,
    decimal RecognizedToDate,
    decimal PeriodRevenue,
    decimal BilledToDate,
    decimal RevenueAdjustment,
    decimal UnbilledBalance,
    decimal BillingsInExcessBalance,
    decimal LossProvisionBalance,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    Guid? ReversalEntryId,
    string? ReversalEntryNumber,
    string PostedBy,
    DateTime PostedAtUtc,
    string? ReversedBy,
    DateTime? ReversedAtUtc,
    string? ReversalReason,
    IReadOnlyList<RevenueLineDto> Lines);

/// <summary>Posts the period for every project, or only <see cref="ProjectIds"/>. Projects already posted for the period are skipped.</summary>
public sealed record PostRevenueRunRequest(IReadOnlyList<Guid>? ProjectIds = null);

public sealed record PostRevenueRunResultDto(IReadOnlyList<RevenueRunDto> Posted, IReadOnlyList<string> Skipped);

public sealed record ReverseRevenueRunRequest(string Reason);

public sealed record RevenueClinOptionDto(Guid Id, string Number, string Description, string Pricing);

/// <summary>Everything about one project's revenue: setup, estimates, deliveries, runs and the live position.</summary>
public sealed record ProjectRevenueDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid? ContractId,
    string? ContractNumber,
    string? ContractType,
    IReadOnlyList<RevenueClinOptionDto> Clins,
    decimal? SuggestedTransactionPrice,
    IReadOnlyList<RevenueSetupDto> Setups,
    IReadOnlyList<EstimateDto> Estimates,
    IReadOnlyList<RevenueDeliveryDto> Deliveries,
    IReadOnlyList<RevenueRunDto> Runs,
    bool CanManage);

// ---- Reports ----

public enum RevenueGrouping { Project, Contract }

/// <summary>Revenue booked (all sources) per project or contract per fiscal period, from <see cref="FromPeriodId"/> through <see cref="ToPeriodId"/>.</summary>
public sealed record RevenueByPeriodQuery(Guid FromPeriodId, Guid ToPeriodId, RevenueGrouping GroupBy = RevenueGrouping.Project);

public sealed record RevenueByPeriodRowDto(Guid Id, string Code, string Name, string? Method, IReadOnlyList<decimal> Amounts, decimal Total);

public sealed record RevenueByPeriodDto(IReadOnlyList<string> Periods, IReadOnlyList<RevenueByPeriodRowDto> Rows, IReadOnlyList<decimal> Totals, decimal GrandTotal);

/// <summary>
/// Revenue vs billings at a period end: recognized to date against invoiced to date. Positive
/// <see cref="ContractAsset"/> is under-billed (earned, not billed); positive <see cref="ContractLiability"/> is over-billed.
/// </summary>
public sealed record RevenueVsBillingsRowDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? ContractNumber,
    string Method,
    decimal RecognizedToDate,
    decimal BilledToDate,
    decimal ContractAsset,
    decimal ContractLiability,
    decimal LossProvision,
    DateOnly? LastRunPeriodEnd);

public sealed record RevenueVsBillingsDto(Guid FiscalPeriodId, string PeriodName, DateOnly AsOf, IReadOnlyList<RevenueVsBillingsRowDto> Rows)
{
    public decimal Recognized => Rows.Sum(r => r.RecognizedToDate);

    public decimal Billed => Rows.Sum(r => r.BilledToDate);

    public decimal ContractAssets => Rows.Sum(r => r.ContractAsset);

    public decimal ContractLiabilities => Rows.Sum(r => r.ContractLiability);
}

/// <summary>Backlog per contract: value and funding less revenue recognized to date.</summary>
public sealed record BacklogRowDto(
    Guid ContractId,
    string ContractNumber,
    string ContractName,
    string CustomerName,
    decimal ContractValue,
    decimal FundedValue,
    decimal RecognizedToDate,
    decimal FundedBacklog,
    decimal UnfundedBacklog)
{
    public decimal TotalBacklog => FundedBacklog + UnfundedBacklog;
}

public sealed record BacklogDto(DateOnly AsOf, IReadOnlyList<BacklogRowDto> Rows)
{
    public decimal Funded => Rows.Sum(r => r.FundedBacklog);

    public decimal Unfunded => Rows.Sum(r => r.UnfundedBacklog);
}

// ---- Services ----

/// <summary>
/// Revenue setup, estimates at completion and FFP deliveries. Finance and administrators manage everything; a project's
/// manager reads their project and submits its estimates and deliveries.
/// </summary>
public interface IRevenueSetupService
{
    /// <summary>Every project with a contract, with its setups and current estimate (Finance).</summary>
    Task<IReadOnlyList<RevenueProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>One project's revenue (Finance or the project's manager).</summary>
    Task<ProjectRevenueDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<RevenueSetupDto> SaveSetupAsync(SaveRevenueSetupRequest request, CancellationToken cancellationToken = default);

    Task DeleteSetupAsync(Guid setupId, CancellationToken cancellationToken = default);

    /// <summary>Estimates by status (Finance's review queue); all when <paramref name="status"/> is null.</summary>
    Task<IReadOnlyList<EstimateDto>> ListEstimatesAsync(EstimateState? status = null, CancellationToken cancellationToken = default);

    Task<EstimateDto> SubmitEstimateAsync(SubmitEstimateRequest request, CancellationToken cancellationToken = default);

    Task<EstimateDto> ApproveEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default);

    Task<EstimateDto> RejectEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default);

    Task<RevenueDeliveryDto> RecordDeliveryAsync(RecordDeliveryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The monthly revenue run: preview, post (idempotent per project and period), reverse. Finance and administrators.</summary>
public interface IRevenueRunService
{
    Task<RevenuePreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<PostRevenueRunResultDto> PostAsync(Guid fiscalPeriodId, PostRevenueRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses a posted run (the project's latest) in its open period, so the period can be run again.</summary>
    Task<RevenueRunDto> ReverseAsync(Guid runId, ReverseRevenueRunRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RevenueRunDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<RevenueRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Revenue reports. Finance and administrators see every project; project managers see their own projects and contracts.</summary>
public interface IRevenueReportService
{
    Task<RevenueByPeriodDto> GetRevenueByPeriodAsync(RevenueByPeriodQuery query, CancellationToken cancellationToken = default);

    Task<RevenueVsBillingsDto> GetRevenueVsBillingsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<BacklogDto> GetBacklogAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);
}
