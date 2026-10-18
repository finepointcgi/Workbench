namespace Workbench.Contracts.Assets;

// Fixed assets (Finance and administrators): the asset register with government property tracking (FAR 52.245-1),
// capitalization from AP bill lines, monthly depreciation posted to the general ledger per fiscal period, disposals with
// a gain/loss entry, transfers, and the register, depreciation schedule, roll-forward and government property reports.

public enum AssetDepreciationMethod { StraightLine, DecliningBalance, SumOfYearsDigits }

public enum AssetState { InService, Disposed, Transferred }

/// <summary>Book: what's posted. Tax: MACRS (half-year convention), for tax reporting only.</summary>
public enum DepreciationBook { Book, Tax }

public sealed record AssetCategoryDto(
    Guid Id,
    string Code,
    string Name,
    AssetDepreciationMethod DefaultMethod,
    int DefaultLifeYears,
    int? MacrsRecoveryYears,
    Guid AssetAccountId,
    string AssetAccountNumber,
    Guid AccumulatedDepreciationAccountId,
    string AccumulatedDepreciationAccountNumber,
    Guid ExpenseAccountId,
    string ExpenseAccountNumber,
    bool IsActive);

public sealed record SaveAssetCategoryRequest(
    string Code,
    string Name,
    AssetDepreciationMethod DefaultMethod,
    int DefaultLifeYears,
    Guid AssetAccountId,
    Guid AccumulatedDepreciationAccountId,
    Guid ExpenseAccountId,
    int? MacrsRecoveryYears = null,
    bool IsActive = true);

public sealed record AssetSummaryDto(
    Guid Id,
    string TagNumber,
    string Description,
    string CategoryCode,
    DateOnly AcquisitionDate,
    decimal Cost,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    AssetDepreciationMethod Method,
    int UsefulLifeYears,
    AssetState Status,
    string? Location,
    string? CustodianName,
    string? OrgUnitCode,
    string? ProjectCode,
    bool IsGovernmentFurnished,
    bool IsContractorAcquired,
    string? GovernmentPropertyNumber);

public sealed record AssetDepreciationDto(Guid FiscalPeriodId, string PeriodName, DateOnly PeriodStart, decimal Amount);

public sealed record AssetTransferDto(
    DateOnly TransferredOn,
    string? FromLocation,
    string? ToLocation,
    string? FromCustodian,
    string? ToCustodian,
    string? FromOrgUnit,
    string? ToOrgUnit,
    string? FromProject,
    string? ToProject,
    string? Reason,
    string TransferredBy);

public sealed record AssetDto(
    AssetSummaryDto Asset,
    Guid CategoryId,
    DateOnly InServiceDate,
    decimal SalvageValue,
    int? MacrsRecoveryYears,
    string? SerialNumber,
    Guid? CustodianEmployeeId,
    Guid AssetAccountId,
    string AssetAccountNumber,
    Guid AccumulatedDepreciationAccountId,
    string AccumulatedDepreciationAccountNumber,
    Guid ExpenseAccountId,
    string ExpenseAccountNumber,
    Guid? OrgUnitId,
    Guid? ProjectId,
    Guid? WbsElementId,
    string? WbsCode,
    Guid? ContractId,
    string? ContractNumber,
    Guid? VendorId,
    string? VendorName,
    Guid? SourceBillLineId,
    string? SourceBillNumber,
    Guid? CapitalizationEntryId,
    DateOnly? DisposedOn,
    decimal? DisposalProceeds,
    string? DisposalReason,
    Guid? DisposalEntryId,
    IReadOnlyList<AssetDepreciationDto> Depreciation,
    IReadOnlyList<AssetTransferDto> Transfers,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>
/// An asset. Accounts default from the category; method and life too when omitted. Government-furnished and
/// contractor-acquired property isn't depreciated (the government owns it) but needs a contract.
/// </summary>
public sealed record SaveAssetRequest(
    string Description,
    Guid CategoryId,
    DateOnly AcquisitionDate,
    decimal Cost,
    decimal SalvageValue = 0,
    int? UsefulLifeYears = null,
    AssetDepreciationMethod? Method = null,
    string? TagNumber = null,
    DateOnly? InServiceDate = null,
    string? SerialNumber = null,
    string? Location = null,
    Guid? CustodianEmployeeId = null,
    Guid? OrgUnitId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null,
    bool IsGovernmentFurnished = false,
    bool IsContractorAcquired = false,
    Guid? ContractId = null,
    string? GovernmentPropertyNumber = null,
    int? MacrsRecoveryYears = null,
    Guid? AssetAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null,
    Guid? ExpenseAccountId = null,
    Guid? VendorId = null);

/// <summary>Capitalizes an AP bill's GL-account line (at or above the capitalization threshold) as a new asset.</summary>
public sealed record CapitalizeBillLineRequest(
    Guid BillLineId,
    Guid CategoryId,
    string? Description = null,
    int? UsefulLifeYears = null,
    AssetDepreciationMethod? Method = null,
    decimal SalvageValue = 0,
    string? SerialNumber = null,
    string? Location = null,
    Guid? CustodianEmployeeId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null);

/// <summary>A posted bill's GL-account line that can be capitalized (not already an asset).</summary>
public sealed record CapitalizableBillLineDto(
    Guid BillLineId,
    Guid BillId,
    string BillNumber,
    string VendorName,
    DateOnly InvoiceDate,
    int LineNumber,
    string Description,
    decimal Amount,
    string AccountNumber,
    string AccountName,
    string? OrgUnitCode);

public sealed record TransferAssetRequest(
    DateOnly TransferredOn,
    string? Location,
    Guid? CustodianEmployeeId,
    Guid? OrgUnitId,
    Guid? ProjectId,
    string? Reason,

    /// <summary>Transfers it out (to the government or another contract): status Transferred, no further depreciation.</summary>
    bool TransferOut = false);

public sealed record DisposeAssetRequest(DateOnly DisposedOn, decimal Proceeds, string Reason);

public sealed record AssetQuery(AssetState? Status = null, Guid? CategoryId = null, string? Search = null, bool GovernmentPropertyOnly = false);

public sealed record AssetSettingsDto(decimal CapitalizationThreshold, string GainLossAccountNumber, string CashAccountNumber);

public interface IAssetService
{
    Task<IReadOnlyList<AssetSummaryDto>> ListAsync(AssetQuery query, CancellationToken cancellationToken = default);

    Task<AssetDto> GetAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<AssetSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records an asset by hand. Nothing is posted: the cost is assumed already on the books.</summary>
    Task<AssetDto> CreateAsync(SaveAssetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Descriptive fields at any time; cost, life, method and accounts only before any depreciation is posted.</summary>
    Task<AssetDto> UpdateAsync(Guid assetId, SaveAssetRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CapitalizableBillLineDto>> ListCapitalizableBillLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the asset from a posted bill line. When the line was expensed to another account, posts a reclass entry
    /// (debit the asset account, credit the line's account) dated the invoice date, or today when that period is closed.
    /// </summary>
    Task<AssetDto> CapitalizeAsync(CapitalizeBillLineRequest request, CancellationToken cancellationToken = default);

    Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes of it: removes cost and accumulated depreciation, debits cash for the proceeds and books the gain or loss.
    /// Depreciation must be posted through the month before the disposal date.
    /// </summary>
    Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default);

    Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default);
}

// ---- Depreciation ----

public sealed record DepreciationPreviewLineDto(Guid AssetId, string TagNumber, string Description, string ExpenseAccountNumber, string? OrgUnitCode, string? ProjectCode, decimal Amount);

public sealed record DepreciationRunDto(Guid Id, Guid FiscalPeriodId, string PeriodName, decimal Total, int AssetCount, Guid? JournalEntryId, string? JournalEntryNumber, string RunBy, DateTime RunAtUtc);

public sealed record DepreciationPreviewDto(
    Guid FiscalPeriodId,
    string PeriodName,
    bool PeriodIsClosed,

    /// <summary>Assets still to depreciate this period (already-posted ones are excluded).</summary>
    IReadOnlyList<DepreciationPreviewLineDto> Lines,
    decimal Total,
    IReadOnlyList<DepreciationRunDto> PostedRuns);

public interface IDepreciationService
{
    Task<DepreciationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts one balanced entry for the period (debit each asset's expense account with its org/project segments, credit
    /// accumulated depreciation). Idempotent: assets already depreciated for the period are skipped, and when nothing is
    /// left it returns null without posting. Refused for a closed period.
    /// </summary>
    Task<DepreciationRunDto?> RunAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DepreciationRunDto>> ListRunsAsync(CancellationToken cancellationToken = default);
}

// ---- Reports ----

public sealed record DepreciationScheduleRowDto(int Year, string Label, decimal BeginningBookValue, decimal Depreciation, decimal EndingBookValue, decimal Posted);

public sealed record DepreciationScheduleDto(
    Guid AssetId,
    string TagNumber,
    string Description,
    DepreciationBook Book,
    string Method,
    decimal Cost,
    decimal SalvageValue,
    IReadOnlyList<DepreciationScheduleRowDto> Rows);

public sealed record RollForwardRowDto(string CategoryCode, string CategoryName, decimal Beginning, decimal Additions, decimal Depreciation, decimal Disposals, decimal Ending);

/// <summary>Net book value roll-forward: beginning + additions − depreciation − disposals (net book value removed) = ending.</summary>
public sealed record RollForwardDto(DateOnly From, DateOnly To, IReadOnlyList<RollForwardRowDto> Rows, RollForwardRowDto Totals);

public sealed record GovernmentPropertyRowDto(
    Guid AssetId,
    string TagNumber,
    string? GovernmentPropertyNumber,
    string Description,
    string Kind,
    string? ContractNumber,
    DateOnly AcquisitionDate,
    decimal Cost,
    string? SerialNumber,
    string? Location,
    string? Custodian,
    AssetState Status);

public sealed record AssetExportFile(string FileName, string ContentType, string Content);

public interface IAssetReportService
{
    Task<IReadOnlyList<AssetSummaryDto>> GetRegisterAsync(AssetQuery query, CancellationToken cancellationToken = default);

    /// <summary>Annual schedule over the asset's life: posted book depreciation, or MACRS for <see cref="DepreciationBook.Tax"/>.</summary>
    Task<DepreciationScheduleDto> GetScheduleAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default);

    Task<RollForwardDto> GetRollForwardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernmentPropertyRowDto>> GetGovernmentPropertyAsync(CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportRegisterCsvAsync(CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportScheduleCsvAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportRollForwardCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<AssetExportFile> ExportGovernmentPropertyCsvAsync(CancellationToken cancellationToken = default);
}
