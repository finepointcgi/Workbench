namespace Workbench.Domain.PerDiem;

/// <summary>Which published table a rate comes from.</summary>
public enum PerDiemRateKind
{
    /// <summary>GSA CONUS rates (the 48 contiguous states and DC).</summary>
    Conus,

    /// <summary>DoD (non-foreign OCONUS) and State Department (foreign) rates.</summary>
    Oconus,
}

/// <summary>
/// One import of a per diem table (a GSA file, a GSA API pull, or a DoD/State CSV) for one fiscal year and kind.
/// Imports are append-only: the latest batch for a fiscal year and kind is the table in force; earlier ones stay as history.
/// </summary>
public sealed class PerDiemRateBatch : Entity
{
    public int FiscalYear { get; set; }
    public PerDiemRateKind Kind { get; set; }

    /// <summary>"GSA CSV", "GSA Excel", "GSA API", "OCONUS CSV" or "Sample".</summary>
    public required string Source { get; set; }

    public string? FileName { get; set; }
    public int RateCount { get; set; }
    public int TierCount { get; set; }
    public required string ImportedBy { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>
/// A per diem rate for a destination and season: the lodging maximum and the M&amp;IE rate (federal fiscal year,
/// October to September). <see cref="SeasonStart"/> and <see cref="SeasonEnd"/> are actual dates inside the fiscal year.
/// The standard CONUS rate is the row with <see cref="IsStandard"/> set.
/// </summary>
public sealed class PerDiemRate : Entity
{
    public Guid BatchId { get; set; }
    public int FiscalYear { get; set; }
    public PerDiemRateKind Kind { get; set; }

    /// <summary>"US" for CONUS; the country (or territory) for OCONUS rates.</summary>
    public required string Country { get; set; }

    /// <summary>Two-letter state for CONUS (empty for the standard rate); empty or a region for OCONUS.</summary>
    public required string State { get; set; }

    /// <summary>The primary destination (city), e.g. "District of Columbia" or "Huntsville".</summary>
    public required string Destination { get; set; }

    /// <summary>The counties or locations the destination covers.</summary>
    public string? County { get; set; }

    public DateOnly SeasonStart { get; set; }
    public DateOnly SeasonEnd { get; set; }
    public decimal LodgingMax { get; set; }
    public decimal MieRate { get; set; }
    public bool IsStandard { get; set; }
}

/// <summary>
/// The GSA M&amp;IE breakdown for one M&amp;IE tier (e.g. $68): what each meal is worth, so provided meals can be deducted,
/// and the 75% first-and-last-day amount.
/// </summary>
public sealed class PerDiemMieTier : Entity
{
    public Guid BatchId { get; set; }
    public int FiscalYear { get; set; }
    public decimal Total { get; set; }
    public decimal Breakfast { get; set; }
    public decimal Lunch { get; set; }
    public decimal Dinner { get; set; }
    public decimal Incidentals { get; set; }
    public decimal FirstLastDay { get; set; }
}
