namespace Workbench.Contracts.ProjectAccounting;

// Ceiling-first planning, the way government contractors do it: Finance (or Contracts) creates the project from the
// contract with its ceiling, funding, type and fee, and hands it to the PM; the PM builds the WBS with people, hours and
// cost against that ceiling and submits the plan; Finance approves it (it becomes the baseline budget) or returns it.

public enum PlanApprovalStatus
{
    /// <summary>The PM is building the plan.</summary>
    Draft,

    /// <summary>Waiting for Finance.</summary>
    Submitted,

    /// <summary>Approved: the baseline budget. Changes since then are the working version.</summary>
    Approved,

    /// <summary>Finance sent it back with comments.</summary>
    Returned,
}

/// <summary>
/// The money a project is planned against. Each value says whether it comes from the contract (and its CLINs) or was
/// set for this project. <see cref="FeeRate"/> is a fraction (0.08 = 8%) on cost; T&amp;M and labor-hour work is priced at
/// the contract's bill rates instead.
/// </summary>
public sealed record ProjectTermsDto(
    Guid? ContractId,
    string? ContractNumber,
    decimal? Ceiling,
    bool CeilingFromContract,
    decimal? Funded,
    bool FundedFromContract,
    ContractKind? Pricing,
    bool PricingFromContract,
    decimal FeeRate,
    bool FeeFromContract,
    DateOnly? Start,
    DateOnly? Finish,

    /// <summary>Other projects on the same contract (their codes), whose plans share its ceiling and funding.</summary>
    IReadOnlyList<string> SharedWith)
{
    /// <summary>"T&amp;M", "Cost plus fixed fee" and so on.</summary>
    public string PricingLabel => Pricing switch
    {
        ContractKind.FirmFixedPrice => "Firm fixed price",
        ContractKind.TimeAndMaterials => "Time and materials",
        ContractKind.LaborHour => "Labor hour",
        ContractKind.CostPlusFixedFee => "Cost plus fixed fee",
        ContractKind.CostPlusAwardFee => "Cost plus award fee",
        ContractKind.CostPlusIncentiveFee => "Cost plus incentive fee",
        _ => "Internal (no contract)",
    };

    public bool IsBilledByRate => Pricing is ContractKind.TimeAndMaterials or ContractKind.LaborHour;
}

/// <summary>
/// The terms to set. Null keeps the contract's value (or clears an earlier one); a value the same as the contract's is
/// stored as "from the contract".
/// </summary>
public sealed record SaveProjectTermsRequest(decimal? Ceiling = null, decimal? Funded = null, ContractKind? Pricing = null, decimal? FeeRate = null);

/// <summary>How a plan's dollars stand against its funding and ceiling.</summary>
public enum PlanMoneyStatus
{
    /// <summary>Within funding (or there's nothing to compare with).</summary>
    Ok,

    /// <summary>Past what's funded but within the ceiling (amber).</summary>
    OverFunding,

    /// <summary>Past the ceiling (red).</summary>
    OverCeiling,
}

/// <summary>
/// One line of the money bar: the project, or a top WBS element with its CLIN. <see cref="Planned"/> is the plan priced
/// as the contract bills it.
/// </summary>
public sealed record PlanMoneyLineDto(
    Guid? WbsElementId,
    string Label,
    string? ClinNumber,
    decimal Planned,
    decimal? Ceiling,
    decimal? Funded,
    PlanMoneyStatus Status)
{
    public decimal? Left => Ceiling is { } c ? c - Planned : null;
}

/// <summary>
/// "Planned $X of $Y ceiling · $Z funded · $W left", for the project and each top WBS element (CLIN).
/// <see cref="Basis"/> says in words how planned dollars are worked out.
/// </summary>
public sealed record PlanMoneyDto(PlanMoneyLineDto Project, IReadOnlyList<PlanMoneyLineDto> Lines, string Basis, IReadOnlyList<string> Notes);

/// <summary>Hours and dollars of one labor category: the plan now against the approved baseline.</summary>
public sealed record PlanChangeLineDto(Guid? LaborCategoryId, string LaborCategory, decimal BaselineHours, decimal PlanHours, decimal BaselineAmount, decimal PlanAmount)
{
    public decimal HoursChange => PlanHours - BaselineHours;

    public decimal AmountChange => PlanAmount - BaselineAmount;
}

/// <summary>
/// A project's plan with Finance: its terms, where approval stands, and (once approved) how the working plan differs
/// from the baseline. The Can* flags say what the signed-in person may do now.
/// </summary>
public sealed record ProjectPlanDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string? ManagerName,
    ProjectTermsDto Terms,
    PlanApprovalStatus Status,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc,
    string? SubmitNote,
    string? ReviewedBy,
    DateTime? ReviewedAtUtc,
    string? ReviewComment,
    int Approvals,

    /// <summary>The approved baseline budget version, once there is one.</summary>
    Guid? BaselineVersionId,

    /// <summary>Approved, and the people or hours changed since: the working version differs from what Finance approved.</summary>
    bool ChangedSinceApproval,

    /// <summary>The plan against the baseline by labor category (empty before the first approval).</summary>
    IReadOnlyList<PlanChangeLineDto> SinceBaseline,
    bool CanEditTerms,
    bool CanSubmit,
    bool CanReview,

    /// <summary>The names of who submitted and who reviewed it, for display (<see cref="SubmittedBy"/> and <see cref="ReviewedBy"/> are their emails).</summary>
    string? SubmittedByName = null,
    string? ReviewedByName = null)
{
    public decimal HoursChange => SinceBaseline.Sum(l => l.HoursChange);

    public decimal AmountChange => SinceBaseline.Sum(l => l.AmountChange);
}

/// <summary>HTTP body for submitting, approving or returning a plan.</summary>
public sealed record PlanReviewRequest(string? Comment = null);

/// <summary>
/// The project's plan terms and approval. Reading: anyone who may see the project's financials. Terms, approving and
/// returning: Finance (Setup › Projects). Submitting: the project's manager (or Finance).
/// </summary>
public interface IProjectPlanService
{
    Task<ProjectPlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>What a project started from the contract gets: its ceiling, funding, type, fee and dates. Saves nothing.</summary>
    Task<ProjectTermsDto> TermsFromContractAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ProjectPlanDto> SaveTermsAsync(Guid projectId, SaveProjectTermsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends the plan to Finance (with an optional note). Finance is notified.</summary>
    Task<ProjectPlanDto> SubmitAsync(Guid projectId, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the submitted plan: the first approval makes it the locked Baseline budget, every approval makes it the
    /// Current (working) budget. The PM is notified.
    /// </summary>
    Task<ProjectPlanDto> ApproveAsync(Guid projectId, string? comment = null, CancellationToken cancellationToken = default);

    /// <summary>Sends the plan back to the PM with comments (required).</summary>
    Task<ProjectPlanDto> ReturnAsync(Guid projectId, string comment, CancellationToken cancellationToken = default);
}
