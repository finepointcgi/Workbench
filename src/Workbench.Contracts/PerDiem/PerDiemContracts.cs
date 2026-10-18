namespace Workbench.Contracts.PerDiem;

// Enums mirror Workbench.Domain.PerDiem by name and value.

/// <summary>GSA CONUS rates, or DoD/State OCONUS (non-foreign and foreign) rates.</summary>
public enum PerDiemTable { Conus, Oconus }

/// <summary>
/// What happens to lodging claimed above the maximum (config <c>PerDiem:LodgingOveragePolicy</c>). FAR 31.205-46 makes
/// the excess unallowable unless it's justified (actual expense, up to 300% of the maximum).
/// </summary>
public enum LodgingOveragePolicy { RequiresJustification, Unallowable }

public sealed record PerDiemRateDto(
    Guid Id,
    int FiscalYear,
    PerDiemTable Kind,
    string Country,
    string State,
    string Destination,
    string? County,
    DateOnly SeasonStart,
    DateOnly SeasonEnd,
    decimal LodgingMax,
    decimal MieRate,
    bool IsStandard);

/// <summary>The GSA M&amp;IE breakdown for one tier: meal values for provided-meal deductions and the 75% first/last-day amount.</summary>
public sealed record MieTierDto(int FiscalYear, decimal Total, decimal Breakfast, decimal Lunch, decimal Dinner, decimal Incidentals, decimal FirstLastDay);

public sealed record PerDiemBatchDto(
    Guid Id,
    int FiscalYear,
    PerDiemTable Kind,
    string Source,
    string? FileName,
    int RateCount,
    int TierCount,
    string ImportedBy,
    DateTime ImportedAtUtc);

public sealed record PerDiemRateQuery(int? FiscalYear = null, PerDiemTable? Kind = null, string? Search = null, int Take = 500);

/// <summary>The rates in force (the latest import per fiscal year and table), the M&amp;IE tiers, and the import history.</summary>
public sealed record PerDiemRateTableDto(
    IReadOnlyList<PerDiemRateDto> Rates,
    IReadOnlyList<MieTierDto> Tiers,
    IReadOnlyList<PerDiemBatchDto> Batches,
    bool GsaApiConfigured,
    LodgingOveragePolicy Policy);

/// <summary>A GSA per diem CSV or Excel file (CONUS), or a DoD/State CSV in the same shape (OCONUS).</summary>
public sealed record ImportPerDiemFileRequest(PerDiemTable Kind, int FiscalYear, string FileName, byte[] Content);

public sealed record PerDiemImportResultDto(PerDiemBatchDto Batch, IReadOnlyList<string> Warnings);

/// <summary>A place travelers can pick as a destination.</summary>
public sealed record PerDiemLocationDto(PerDiemTable Kind, string Country, string State, string Destination)
{
    public string Label => Kind == PerDiemTable.Conus
        ? (State.Length == 0 ? Destination : $"{Destination}, {State}")
        : (State.Length == 0 ? $"{Destination}, {Country}" : $"{Destination}, {State}, {Country}");
}

/// <summary>The rate that applies at a destination on a date, and the M&amp;IE breakdown for its tier.</summary>
public sealed record ResolvedPerDiemRateDto(
    decimal LodgingMax,
    decimal MieRate,
    MieTierDto Tier,
    bool IsStandardFallback,
    string Description,
    IReadOnlyList<string> Notes);

/// <summary>Per diem rate tables: view, look up, and import (Finance and Admin import; anyone signed in may look up).</summary>
public interface IPerDiemRateService
{
    Task<PerDiemRateTableDto> GetTableAsync(PerDiemRateQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>Destinations in the tables in force for the fiscal year (all tables when <paramref name="kind"/> is null).</summary>
    Task<IReadOnlyList<PerDiemLocationDto>> ListLocationsAsync(int fiscalYear, PerDiemTable? kind = null, string? search = null, CancellationToken cancellationToken = default);

    Task<ResolvedPerDiemRateDto> LookupAsync(PerDiemLocationDto location, DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>Imports a CSV or Excel file as a new batch, which becomes the table in force for that fiscal year and kind.</summary>
    Task<PerDiemImportResultDto> ImportFileAsync(ImportPerDiemFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pulls CONUS lodging rates and the M&amp;IE breakdown from the GSA Per Diem API (config <c>PerDiem:Gsa:ApiKey</c>).</summary>
    Task<PerDiemImportResultDto> ImportFromGsaApiAsync(int fiscalYear, CancellationToken cancellationToken = default);

    /// <summary>Loads the shipped sample CONUS rates (a few cities plus the standard rate) for a fiscal year.</summary>
    Task<PerDiemImportResultDto> InstallSampleRatesAsync(int fiscalYear, CancellationToken cancellationToken = default);
}

// ---- Travel per diem on expense reports ----

public sealed record TravelDestinationDto(
    PerDiemTable Kind,
    string Country,
    string State,
    string Destination,
    DateOnly ArriveOn,
    DateOnly DepartOn);

/// <summary>What the traveler claims for a day. <see cref="MieClaimed"/> null claims the allowable M&amp;IE.</summary>
public sealed record TravelDayInput(
    DateOnly Date,
    decimal LodgingClaimed = 0,
    decimal LodgingTaxClaimed = 0,
    decimal? MieClaimed = null,
    bool BreakfastProvided = false,
    bool LunchProvided = false,
    bool DinnerProvided = false,
    string? Justification = null);

public sealed record SaveTravelPerDiemRequest(
    Guid WbsElementId,
    bool LodgingTaxesSeparate,
    IReadOnlyList<TravelDestinationDto> Destinations,
    IReadOnlyList<TravelDayInput> Days);

public enum PerDiemAmountStatus { Ok, RequiresJustification, Justified, Unallowable }

/// <summary>The computed per diem for one day against what was claimed.</summary>
public sealed record PerDiemDayDto(
    DateOnly Date,
    string Location,
    string RateDescription,
    bool IsStandardRate,
    bool IsFirstDay,
    bool IsLastDay,
    bool HasLodging,
    decimal LodgingMax,
    decimal MieRate,

    /// <summary>0.75 on the first and last travel day (FTR 301-11.101, JTR 020301), otherwise 1.</summary>
    decimal MieFactor,
    decimal MieBase,
    decimal MealDeduction,
    decimal MieAllowable,
    decimal MieClaimed,
    decimal MieUnallowable,
    decimal LodgingClaimed,
    decimal LodgingTaxClaimed,
    decimal LodgingAllowable,
    decimal LodgingTaxAllowable,

    /// <summary>Claimed lodging above the maximum.</summary>
    decimal LodgingOverage,

    /// <summary>The part of the overage that's justified (allowable actual expense).</summary>
    decimal LodgingJustified,

    /// <summary>The part of the overage that isn't allowable (or isn't yet, while it awaits a justification).</summary>
    decimal LodgingUnallowable,
    PerDiemAmountStatus LodgingStatus,
    bool BreakfastProvided,
    bool LunchProvided,
    bool DinnerProvided,
    string? Justification,
    IReadOnlyList<string> Notes)
{
    public decimal Claimed => LodgingClaimed + LodgingTaxClaimed + MieClaimed;

    public decimal Allowable => LodgingAllowable + LodgingTaxAllowable + LodgingJustified + MieAllowable;
}

public sealed record PerDiemTotalsDto(
    decimal LodgingClaimed,
    decimal LodgingAllowable,
    decimal LodgingTax,
    decimal LodgingJustified,
    decimal MieClaimed,
    decimal MieAllowable,
    decimal Unallowable,
    decimal Claimed,
    decimal Allowable);

public sealed record TravelPerDiemDto(
    Guid ExpenseReportId,
    bool Exists,
    Guid? WbsElementId,
    string? WbsCode,
    bool LodgingTaxesSeparate,
    LodgingOveragePolicy Policy,
    IReadOnlyList<TravelDestinationDto> Destinations,
    IReadOnlyList<PerDiemDayDto> Days,
    PerDiemTotalsDto Totals,
    DateTime? AppliedAtUtc,

    /// <summary>True when the trip changed after expense lines were last generated.</summary>
    bool LinesOutOfDate,

    /// <summary>Days with lodging above the maximum that still need a justification (the report can't be submitted).</summary>
    int PendingJustifications,
    IReadOnlyList<string> Notes);

/// <summary>
/// Per diem on travel expense reports: destinations and daily claims, the computed allowance per day, and expense lines
/// generated from it (Lodging, Lodging taxes, M&amp;IE, justified and unallowable overage). The employee edits their own
/// report; their supervisor, Finance and Admin may read it.
/// </summary>
public interface ITravelPerDiemService
{
    Task<TravelPerDiemDto> GetAsync(Guid expenseReportId, CancellationToken cancellationToken = default);

    Task<TravelPerDiemDto> SaveAsync(Guid expenseReportId, SaveTravelPerDiemRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the lines previously generated from the per diem with lines from the current computation.</summary>
    Task<TravelPerDiemDto> ApplyToReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default);

    /// <summary>Removes the per diem and the lines it generated.</summary>
    Task RemoveAsync(Guid expenseReportId, CancellationToken cancellationToken = default);
}

/// <summary>HTTP body for <see cref="IPerDiemRateService.LookupAsync"/>.</summary>
public sealed record PerDiemLookupRequest(PerDiemLocationDto Location, DateOnly Date);
