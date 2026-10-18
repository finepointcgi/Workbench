namespace Workbench.Domain.PerDiem;

/// <summary>
/// The per diem side of a travel expense report: its destinations, the traveler's daily claims, and the lines generated
/// from them. One per <c>ExpenseReport</c>; the report itself is unchanged.
/// </summary>
public sealed class TravelPerDiem : Entity
{
    public Guid ExpenseReportId { get; set; }

    /// <summary>Charge code the generated lodging and M&amp;IE lines go to.</summary>
    public Guid WbsElementId { get; set; }

    /// <summary>
    /// True (the FTR default): lodging taxes are claimed separately and reimbursed in full, outside the lodging maximum.
    /// False: the claimed lodging amount includes taxes and is compared with the maximum as a whole.
    /// </summary>
    public bool LodgingTaxesSeparate { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public required string UpdatedBy { get; set; }

    /// <summary>When expense lines were last generated; cleared when the trip changes, so null means the lines are out of date.</summary>
    public DateTime? AppliedAtUtc { get; set; }

    public List<TravelDestination> Destinations { get; } = [];
    public List<TravelDay> Days { get; } = [];
}

/// <summary>
/// One stop on a trip. The traveler arrives on <see cref="ArriveOn"/> and leaves on <see cref="DepartOn"/>; the next stop
/// starts the day this one ends. Lodging and M&amp;IE follow where the traveler sleeps each night.
/// </summary>
public sealed class TravelDestination : Entity
{
    public Guid TravelPerDiemId { get; set; }
    public int Order { get; set; }
    public PerDiemRateKind Kind { get; set; }
    public string Country { get; set; } = "US";
    public string State { get; set; } = "";
    public required string Destination { get; set; }
    public DateOnly ArriveOn { get; set; }
    public DateOnly DepartOn { get; set; }
}

/// <summary>What the traveler claims for one day of the trip, and which meals were provided.</summary>
public sealed class TravelDay : Entity
{
    public Guid TravelPerDiemId { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Lodging for the night starting on this date (taxes included when they aren't separate).</summary>
    public decimal LodgingClaimed { get; set; }

    public decimal LodgingTaxClaimed { get; set; }

    /// <summary>M&amp;IE claimed; null means "the allowable amount".</summary>
    public decimal? MieClaimed { get; set; }

    public bool BreakfastProvided { get; set; }
    public bool LunchProvided { get; set; }
    public bool DinnerProvided { get; set; }

    /// <summary>Why lodging above the maximum is necessary (actual expense, FTR 301-11.300 / FAR 31.205-46).</summary>
    public string? Justification { get; set; }
}

public enum PerDiemLineKind
{
    Lodging,
    LodgingTax,
    Mie,

    /// <summary>Lodging above the maximum that was justified (allowable actual expense).</summary>
    LodgingJustified,

    /// <summary>Claimed amounts above what's allowable (recorded on an unallowable line).</summary>
    Unallowable,
}

/// <summary>Links an expense line (<c>OdcEntry</c>) to the per diem computation that generated it.</summary>
public sealed class PerDiemLine : Entity
{
    public Guid TravelPerDiemId { get; set; }
    public Guid ExpenseReportId { get; set; }
    public Guid OdcEntryId { get; set; }
    public PerDiemLineKind Kind { get; set; }
    public DateOnly FirstDate { get; set; }
    public DateOnly LastDate { get; set; }
    public decimal Amount { get; set; }
}
