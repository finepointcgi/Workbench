namespace Workbench.Contracts.Assets;

// Tax depreciation books (Finance and administrators). Every asset keeps its own book (GAAP, posted to the ledger, on the
// asset) and can add a federal, state and AMT tax book with its own MACRS method, recovery period and convention, Section
// 179 expense and bonus depreciation. Tax books are reporting books: they're never posted. Tax years follow the asset's
// company fiscal year and are named by the calendar year they begin in.

/// <summary>Book is the asset's own GAAP book (posted); the rest are tax reporting books.</summary>
public enum AssetBookKind { Book, Federal, State, Amt }

public enum TaxMethod { MacrsGds, Macrs150Db, StraightLine, MacrsAds }

public enum TaxConventionKind { HalfYear, MidQuarter, MidMonth }

/// <summary>Sale for cash, scrapped (proceeds are scrap value, often zero), or traded in toward a replacement.</summary>
public enum AssetDisposalType { Sale, Scrap, TradeIn }

public sealed record AssetTaxBookDto(
    Guid Id,
    Guid AssetId,
    AssetBookKind Book,
    TaxMethod Method,
    decimal RecoveryYears,
    TaxConventionKind Convention,
    decimal Basis,
    bool BasisIsAssetCost,
    DateOnly PlacedInServiceDate,
    decimal Section179Amount,
    decimal BonusPercent,
    decimal BonusAmount,
    string? Notes,
    decimal DepreciationToDate,
    decimal AdjustedBasis,
    decimal? DisposalDepreciation,
    decimal? DisposalAdjustedBasis,
    decimal? DisposalGainLoss,
    string UpdatedBy,
    DateTime UpdatedAtUtc);

/// <summary>Adds or changes a tax book. A null basis or placed-in-service date follows the asset's cost and in-service date.</summary>
public sealed record SaveAssetTaxBookRequest(
    AssetBookKind Book,
    TaxMethod Method,
    decimal RecoveryYears,
    TaxConventionKind Convention,
    decimal Section179Amount = 0,
    decimal BonusPercent = 0,
    decimal? Basis = null,
    DateOnly? PlacedInServiceDate = null,
    string? Notes = null);

/// <summary>One row of an asset's book schedule by tax year (the Book row is the GAAP schedule summed over the same year).</summary>
public sealed record TaxScheduleRowDto(int TaxYear, DateOnly Start, DateOnly End, decimal BeginningBasis, decimal Section179, decimal Bonus, decimal Depreciation, decimal Total, decimal EndingBasis);

public sealed record AssetTaxScheduleDto(Guid AssetId, string TagNumber, string Description, AssetBookKind Book, string Method, decimal Basis, IReadOnlyList<TaxScheduleRowDto> Rows);

/// <summary>Every book of an asset side by side, by tax year.</summary>
public sealed record AssetBooksDto(Guid AssetId, string TagNumber, int TaxYearStartMonth, IReadOnlyList<AssetTaxBookDto> Books, IReadOnlyList<AssetTaxScheduleDto> Schedules);

public sealed record TaxReportQuery(int TaxYear, AssetBookKind Book = AssetBookKind.Federal, Guid? CompanyId = null);

public sealed record TaxDepreciationRowDto(
    Guid AssetId,
    string TagNumber,
    string Description,
    string CompanyCode,
    DateOnly PlacedInService,
    string Method,
    decimal RecoveryYears,
    TaxConventionKind Convention,
    decimal Basis,
    decimal Section179,
    decimal Bonus,
    decimal PriorDepreciation,
    decimal CurrentDepreciation,
    decimal AccumulatedDepreciation,
    decimal EndingBasis,
    bool Disposed);

/// <summary>The tax depreciation schedule for one book and tax year (assets placed in service in or before it).</summary>
public sealed record TaxDepreciationReportDto(int TaxYear, DateOnly Start, DateOnly End, AssetBookKind Book, IReadOnlyList<TaxDepreciationRowDto> Rows, TaxDepreciationRowDto Totals);

public sealed record BookTaxDifferenceRowDto(
    Guid AssetId,
    string TagNumber,
    string Description,
    decimal BookDepreciation,
    decimal TaxDepreciation,
    decimal Difference,
    decimal BookNetValue,
    decimal TaxBasis,
    decimal TemporaryDifference,
    decimal DeferredTax);

/// <summary>
/// Book-to-tax differences for the tax provision: this year's depreciation difference (a Schedule M-1/M-3 adjustment) and
/// the cumulative temporary difference (book net value less tax basis) times the tax rate: a deferred tax liability when positive.
/// </summary>
public sealed record BookTaxDifferenceDto(int TaxYear, DateOnly Start, DateOnly End, AssetBookKind Book, decimal TaxRate, IReadOnlyList<BookTaxDifferenceRowDto> Rows, BookTaxDifferenceRowDto Totals);

public sealed record Form4562ClassRowDto(string Line, string Label, decimal Basis, string Convention, string Method, decimal Deduction);

/// <summary>Form 4562 (Depreciation and Amortization) figures for one tax year of the federal book. Listed property and amortization aren't covered.</summary>
public sealed record Form4562SummaryDto(
    int TaxYear,
    DateOnly Start,
    DateOnly End,
    decimal Line1MaximumAmount,
    decimal Line2TotalCost,
    decimal Line3Threshold,
    decimal Line4Reduction,
    decimal Line5DollarLimitation,
    decimal Line8ElectedCost,
    decimal Line12Section179Deduction,
    decimal Line14SpecialAllowance,
    decimal Line17PriorYearMacrs,
    IReadOnlyList<Form4562ClassRowDto> Line19Gds,
    IReadOnlyList<Form4562ClassRowDto> Line20Ads,
    decimal Line22Total,
    bool MidQuarterRequired);

public sealed record MidQuarterBookDto(Guid AssetId, string TagNumber, string Description, DateOnly PlacedInService, int Quarter, decimal Basis, TaxConventionKind Convention, bool ConventionMatches);

/// <summary>
/// The mid-quarter test: when more than 40% of the year's personal property basis (after Section 179, leaving out real
/// property and property disposed of the same year) was placed in service in the last quarter, every such asset uses mid-quarter.
/// </summary>
public sealed record MidQuarterTestDto(int TaxYear, DateOnly Start, DateOnly End, AssetBookKind Book, IReadOnlyList<decimal> BasisByQuarter, decimal Total, decimal LastQuarterPercent, bool MidQuarterRequired, IReadOnlyList<MidQuarterBookDto> Assets);

public sealed record TaxSettingsDto(decimal Section179Limit, decimal Section179PhaseOutThreshold, decimal TaxRate);

public interface IAssetTaxBookService
{
    /// <summary>The asset's books (its GAAP book and each tax book) with their schedules by tax year.</summary>
    Task<AssetBooksDto> GetBooksAsync(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or replaces the asset's book of that kind. Refused for <see cref="AssetBookKind.Book"/> (that's the asset itself),
    /// government property (not ours to depreciate) and disposed assets.
    /// </summary>
    Task<AssetTaxBookDto> SaveBookAsync(Guid assetId, SaveAssetTaxBookRequest request, CancellationToken cancellationToken = default);

    Task DeleteBookAsync(Guid assetId, AssetBookKind book, CancellationToken cancellationToken = default);

    Task<TaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<TaxDepreciationReportDto> GetDepreciationReportAsync(TaxReportQuery query, CancellationToken cancellationToken = default);

    Task<BookTaxDifferenceDto> GetBookTaxDifferenceAsync(TaxReportQuery query, CancellationToken cancellationToken = default);

    Task<Form4562SummaryDto> GetForm4562Async(TaxReportQuery query, CancellationToken cancellationToken = default);

    Task<MidQuarterTestDto> GetMidQuarterTestAsync(TaxReportQuery query, CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportDepreciationReportCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportBookTaxDifferenceCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default);
}
