using Workbench.Domain.PerDiem;

namespace Workbench.Domain.Travel;

public enum TravelAuthorizationStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>The cost categories a trip is estimated in and its expense lines are compared by.</summary>
public enum TravelCostCategory
{
    Airfare,
    Lodging,
    Mie,
    RentalCar,
    Other,
}

/// <summary>
/// A request to travel (Costpoint "travel authorization"): purpose, the charge code, destinations and the estimated
/// cost. It is approved through the approval engine (document type <c>travel-authorization</c>); once approved it is the
/// budget for the trip's expense report (<see cref="ExpenseReportId"/>), which is compared with it line by line.
/// </summary>
public sealed class TravelAuthorization : Entity
{
    /// <summary>"TA-000001", assigned when created.</summary>
    public required string Number { get; set; }

    public Guid EmployeeId { get; set; }
    public required string Title { get; set; }
    public required string Purpose { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WbsElementId { get; set; }

    /// <summary>The first arrival and the last departure of <see cref="Destinations"/>.</summary>
    public DateOnly TripStart { get; set; }

    public DateOnly TripEnd { get; set; }

    public decimal EstimatedAirfare { get; set; }

    /// <summary>The GSA lodging maximum for each night, from the per diem tables when the estimate was last computed.</summary>
    public decimal EstimatedLodging { get; set; }

    /// <summary>The GSA M&amp;IE allowance (75% on the first and last day), from the per diem tables.</summary>
    public decimal EstimatedMie { get; set; }

    public decimal EstimatedRentalCar { get; set; }
    public decimal EstimatedOther { get; set; }
    public string? OtherDescription { get; set; }

    public decimal EstimatedTotal => EstimatedAirfare + EstimatedLodging + EstimatedMie + EstimatedRentalCar + EstimatedOther;

    /// <summary>A cash advance the traveler asks for (zero for none). Finance records it as issued after approval.</summary>
    public decimal AdvanceRequested { get; set; }

    public decimal? AdvanceIssuedAmount { get; set; }
    public DateOnly? AdvanceIssuedOn { get; set; }
    public string? AdvanceIssuedBy { get; set; }
    public string? AdvanceReference { get; set; }

    /// <summary>The trip's expense report, once started from the authorization.</summary>
    public Guid? ExpenseReportId { get; set; }

    public TravelAuthorizationStatus Status { get; set; } = TravelAuthorizationStatus.Draft;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancellationReason { get; set; }

    public List<TravelAuthorizationDestination> Destinations { get; } = [];
}

/// <summary>One stop on an authorized trip; the next stop starts the day this one ends (as on a per diem trip).</summary>
public sealed class TravelAuthorizationDestination : Entity
{
    public Guid TravelAuthorizationId { get; set; }
    public int Order { get; set; }
    public PerDiemRateKind Kind { get; set; }
    public string Country { get; set; } = "US";
    public string State { get; set; } = "";
    public required string Destination { get; set; }
    public DateOnly ArriveOn { get; set; }
    public DateOnly DepartOn { get; set; }
}

/// <summary>
/// The category the traveler chose for an expense line of a trip report, when the default (per diem kind, card merchant
/// category or description) is wrong. One per expense line (<c>OdcEntry</c>).
/// </summary>
public sealed class TravelLineCategory : Entity
{
    public Guid OdcEntryId { get; set; }
    public TravelCostCategory Category { get; set; }
}
