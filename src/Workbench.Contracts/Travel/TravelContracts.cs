using Workbench.Contracts.Odcs;
using Workbench.Contracts.PerDiem;

namespace Workbench.Contracts.Travel;

// Enums mirror Workbench.Domain.Travel by name and value.

public enum TravelAuthorizationState { Draft, Submitted, Approved, Rejected, Cancelled }

public enum TravelCategory { Airfare, Lodging, Mie, RentalCar, Other }

public static class TravelLabels
{
    public static string Category(TravelCategory category) => category switch
    {
        TravelCategory.Airfare => "Airfare",
        TravelCategory.Lodging => "Lodging",
        TravelCategory.Mie => "Meals and incidentals (M&IE)",
        TravelCategory.RentalCar => "Rental car",
        _ => "Other",
    };

    public static string State(TravelAuthorizationState state) => state switch
    {
        TravelAuthorizationState.Submitted => "Waiting for approval",
        TravelAuthorizationState.Cancelled => "Cancelled",
        _ => state.ToString(),
    };
}

/// <summary>One day of the per diem estimate: the GSA lodging maximum for the night and the M&amp;IE allowance.</summary>
public sealed record TravelEstimateDayDto(DateOnly Date, string Location, string RateDescription, bool HasLodging, decimal LodgingMax, decimal MieAllowance);

/// <summary>
/// A trip's estimated cost. Lodging and M&amp;IE come from the GSA per diem tables (lodging maximum for each night; M&amp;IE
/// with 75% on the first and last day); airfare, rental car and other costs are the traveler's estimates.
/// </summary>
public sealed record TravelEstimateDto(
    decimal Airfare,
    decimal Lodging,
    decimal Mie,
    decimal RentalCar,
    decimal Other,
    decimal Total,
    int Nights,
    IReadOnlyList<TravelEstimateDayDto> Days,
    IReadOnlyList<string> Notes);

public sealed record EstimateTravelRequest(
    IReadOnlyList<TravelDestinationDto> Destinations,
    decimal EstimatedAirfare = 0,
    decimal EstimatedRentalCar = 0,
    decimal EstimatedOther = 0);

/// <summary>Creates or changes a draft (or rejected) travel authorization. Lodging and M&amp;IE are computed, not typed.</summary>
public sealed record SaveTravelAuthorizationRequest(
    string Title,
    string Purpose,
    Guid WbsElementId,
    IReadOnlyList<TravelDestinationDto> Destinations,
    decimal EstimatedAirfare = 0,
    decimal EstimatedRentalCar = 0,
    decimal EstimatedOther = 0,
    string? OtherDescription = null,
    decimal AdvanceRequested = 0);

public sealed record TravelAuthorizationDto(
    Guid Id,
    string Number,
    Guid EmployeeId,
    string EmployeeName,
    string Title,
    string Purpose,
    Guid ProjectId,
    string ProjectCode,
    Guid WbsElementId,
    string WbsCode,
    string WbsName,
    DateOnly TripStart,
    DateOnly TripEnd,
    string Destinations,
    TravelAuthorizationState Status,
    decimal EstimatedAirfare,
    decimal EstimatedLodging,
    decimal EstimatedMie,
    decimal EstimatedRentalCar,
    decimal EstimatedOther,
    string? OtherDescription,
    decimal EstimatedTotal,
    decimal AdvanceRequested,
    decimal? AdvanceIssuedAmount,
    DateOnly? AdvanceIssuedOn,
    string? AdvanceReference,
    Guid? ExpenseReportId,
    string? ExpenseReportTitle,
    OdcApprovalState? ExpenseReportStatus,
    DateTime CreatedAtUtc,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    string? RejectionReason,
    string? CancellationReason)
{
    public bool IsEditable => Status is TravelAuthorizationState.Draft or TravelAuthorizationState.Rejected;
}

/// <summary>An authorization with its stops and per-day estimate. <see cref="CanDecide"/>: an approval step waits on the viewer.</summary>
public sealed record TravelAuthorizationDetailDto(
    TravelAuthorizationDto Authorization,
    IReadOnlyList<TravelDestinationDto> Destinations,
    TravelEstimateDto Estimate,
    bool IsMine,
    bool CanDecide);

public sealed record TravelDecisionRequest(string? Comment = null);

public sealed record LinkExpenseReportRequest(Guid? ExpenseReportId);

public sealed record SetTravelLineCategoryRequest(TravelCategory Category);

/// <summary>Finance records the cash advance paid to the traveler; the expense report liquidates it.</summary>
public sealed record RecordTravelAdvanceRequest(decimal Amount, DateOnly IssuedOn, string? Reference = null);

public sealed record TravelAuthorizationQuery(TravelAuthorizationState? Status = null, Guid? EmployeeId = null, Guid? ProjectId = null, int Take = 500);

/// <summary>Travel policy: how far above the authorized amount a category may go before it's flagged (<c>Travel:TolerancePercent</c>).</summary>
public sealed record TravelPolicyDto(decimal TolerancePercent);

// ---- Estimate vs actual ----

public sealed record TravelCategoryComparisonDto(
    TravelCategory Category,
    decimal Authorized,
    decimal Actual,
    bool IsOverAuthorized,
    bool IsOverTolerance)
{
    public decimal Difference => Actual - Authorized;
}

/// <summary>
/// An expense line as the trip comparison sees it. <see cref="IsFlagged"/>: its category is over the authorized amount
/// plus the tolerance (or wasn't authorized at all).
/// </summary>
public sealed record TravelComparisonLineDto(
    Guid OdcEntryId,
    DateOnly IncurredOn,
    string Description,
    decimal Amount,
    TravelCategory Category,
    bool CategoryChosen,
    bool IsUnallowable,
    string? CardLast4,
    bool IsCompanyPaid,
    bool IsFlagged,
    string? Flag);

/// <summary>
/// The approved authorization against the trip's expense report. Company-paid card lines are owed to the card issuer, not
/// the traveler; the advance (if issued) is deducted from what's reimbursed.
/// </summary>
public sealed record TravelComparisonDto(
    Guid AuthorizationId,
    string Number,
    Guid ExpenseReportId,
    TravelAuthorizationState AuthorizationStatus,
    decimal TolerancePercent,
    IReadOnlyList<TravelCategoryComparisonDto> Categories,
    decimal AuthorizedTotal,
    decimal ActualTotal,
    bool TotalOverTolerance,
    IReadOnlyList<TravelComparisonLineDto> Lines,
    decimal PaidByCompanyCard,
    decimal Reimbursable,
    decimal AdvanceIssued,
    IReadOnlyList<string> Flags)
{
    /// <summary>What the company owes the traveler after the advance; negative when the traveler owes the difference back.</summary>
    public decimal DueToEmployee => Reimbursable - AdvanceIssued;

    public int FlagCount => Flags.Count;
}

/// <summary>
/// Travel authorizations (Costpoint T&amp;E "travel authorization"): the traveler requests the trip, the approval workflow
/// (by default the supervisor and the charged project's manager) approves it, and the approved authorization becomes the
/// budget of the trip's expense report. Travelers see their own; approvers what routes to them; Finance everything.
/// </summary>
public interface ITravelAuthorizationService
{
    Task<IReadOnlyList<TravelAuthorizationDto>> ListMineAsync(CancellationToken cancellationToken = default);

    /// <summary>Waiting for the signed-in user's approval.</summary>
    Task<IReadOnlyList<TravelAuthorizationDto>> ListAwaitingMeAsync(CancellationToken cancellationToken = default);

    /// <summary>Everyone's (Finance and administrators).</summary>
    Task<IReadOnlyList<TravelAuthorizationDto>> ListAllAsync(TravelAuthorizationQuery? query = null, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> GetAsync(Guid authorizationId, CancellationToken cancellationToken = default);

    /// <summary>Prices a trip without saving it (lodging and M&amp;IE from the per diem tables).</summary>
    Task<TravelEstimateDto> EstimateAsync(EstimateTravelRequest request, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> CreateAsync(SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> UpdateAsync(Guid authorizationId, SaveTravelAuthorizationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft or rejected authorization.</summary>
    Task DeleteAsync(Guid authorizationId, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> SubmitAsync(Guid authorizationId, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> ApproveAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default);

    Task<TravelAuthorizationDetailDto> RejectAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The traveler cancels a trip that won't happen (before its expense report is submitted). A reason is required.</summary>
    Task<TravelAuthorizationDetailDto> CancelAsync(Guid authorizationId, TravelDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts the trip's expense report from an approved authorization (title, purpose and dates copied) and links it.</summary>
    Task<ExpenseReportDto> StartExpenseReportAsync(Guid authorizationId, CancellationToken cancellationToken = default);

    /// <summary>Links (or, with null, unlinks) one of the traveler's existing draft expense reports.</summary>
    Task<TravelAuthorizationDetailDto> LinkExpenseReportAsync(Guid authorizationId, LinkExpenseReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Estimate vs actual for an expense report, or null when no authorization is linked to it.</summary>
    Task<TravelComparisonDto?> GetComparisonForReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default);

    /// <summary>The traveler corrects the category of a line on their draft trip report.</summary>
    Task<TravelComparisonDto> SetLineCategoryAsync(Guid expenseReportId, Guid lineId, SetTravelLineCategoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finance records a cash advance issued against an approved authorization.</summary>
    Task<TravelAuthorizationDetailDto> RecordAdvanceAsync(Guid authorizationId, RecordTravelAdvanceRequest request, CancellationToken cancellationToken = default);

    Task<TravelPolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default);
}
