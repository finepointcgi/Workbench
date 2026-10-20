namespace Workbench.Contracts.Assets;

// Fixed assets (Finance and administrators): the asset register with government property tracking (FAR 52.245-1),
// capitalization from AP bill lines, monthly depreciation posted to the general ledger per fiscal period, disposals with
// a gain/loss entry, transfers, and the register, depreciation schedule, roll-forward and government property reports.

public enum AssetDepreciationMethod { StraightLine, DecliningBalance, SumOfYearsDigits }

/// <summary>The in-service month: a whole month, half a month, or nothing (depreciation starts the month after).</summary>
public enum AssetDepreciationConvention { FullMonth, MidMonth, FollowingMonth }

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
    bool IsActive,
    AssetDepreciationConvention Convention = AssetDepreciationConvention.FullMonth,

    /// <summary>Null: the organization's threshold (<see cref="AssetSettingsDto.CapitalizationThreshold"/>).</summary>
    decimal? CapitalizationThreshold = null,

    /// <summary>Assets in the category (any status); a category with assets can be deactivated but not deleted.</summary>
    int AssetCount = 0);

public sealed record SaveAssetCategoryRequest(
    string Code,
    string Name,
    AssetDepreciationMethod DefaultMethod,
    int DefaultLifeYears,
    Guid AssetAccountId,
    Guid AccumulatedDepreciationAccountId,
    Guid ExpenseAccountId,
    int? MacrsRecoveryYears = null,
    bool IsActive = true,
    AssetDepreciationConvention Convention = AssetDepreciationConvention.FullMonth,
    decimal? CapitalizationThreshold = null);

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
    string TransferredBy,
    string? FromContract = null,
    string? ToContract = null,
    string? FromCompany = null,
    string? ToCompany = null,
    Guid? JournalEntryId = null);

/// <summary>A row of the transfer history across assets.</summary>
public sealed record AssetTransferRowDto(Guid AssetId, string TagNumber, string Description, AssetTransferDto Transfer);

/// <summary>An asset's gain or loss on disposal in one book: proceeds less that book's adjusted basis (net book value) at the disposal date.</summary>
public sealed record AssetBookDisposalDto(AssetBookKind Book, decimal Proceeds, decimal AdjustedBasis, decimal GainLoss, decimal DisposalYearDepreciation);

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
    DateTime CreatedAtUtc,
    AssetDepreciationConvention Convention = AssetDepreciationConvention.FullMonth,
    DateOnly? OpeningBalanceDate = null,
    decimal OpeningAccumulatedDepreciation = 0,
    AssetDisposalType? DisposalKind = null,

    /// <summary>The gain or loss per book (the GAAP book first) once disposed of.</summary>
    IReadOnlyList<AssetBookDisposalDto>? BookDisposals = null,
    Guid CompanyId = default,
    string? CompanyCode = null);

/// <summary>
/// An asset. Accounts default from the category; method and life too when omitted. Government-furnished and
/// contractor-acquired property isn't depreciated (the government owns it) but needs a contract.
/// <para>Assets brought over from another system (the Costpoint fixed asset import) give <see cref="OpeningBalanceDate"/>,
/// a month end, and the accumulated depreciation through it. Nothing is posted (the balances arrive with the GL import);
/// depreciation resumes the month after, spreading cost − salvage − accumulated over the rest of the life.</para>
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
    Guid? VendorId = null,
    AssetDepreciationConvention? Convention = null,
    DateOnly? OpeningBalanceDate = null,
    decimal OpeningAccumulatedDepreciation = 0);

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
    bool TransferOut = false,

    /// <summary>
    /// Moves it to another company (null keeps it). Contractor-owned assets need depreciation posted through the month
    /// before; cost and accumulated depreciation move in one entry and the ledger adds the intercompany due to/due from.
    /// </summary>
    Guid? CompanyId = null,

    /// <summary>Government property: the contract it's now accountable to (null keeps it).</summary>
    Guid? ContractId = null);

/// <summary>
/// A disposal. A sale debits cash with the proceeds; a scrap takes the scrap value (often zero) to cash; a trade-in
/// debits <see cref="ProceedsAccountId"/> (the replacement's asset account, or accounts payable when the allowance reduces
/// the vendor's bill), defaulting to the asset's own asset account.
/// </summary>
public sealed record DisposeAssetRequest(DateOnly DisposedOn, decimal Proceeds, string Reason, AssetDisposalType Kind = AssetDisposalType.Sale, Guid? ProceedsAccountId = null);

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

    /// <summary>
    /// Moves it. An organization or company change of a contractor-owned asset posts a reclass of its cost and accumulated
    /// depreciation (a cross-company move goes through the intercompany accounts); other changes are recorded in the history only.
    /// </summary>
    Task<AssetDto> TransferAsync(Guid assetId, TransferAssetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Every transfer, newest first, optionally within a date range.</summary>
    Task<IReadOnlyList<AssetTransferRowDto>> ListTransfersAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes of it: removes cost and accumulated depreciation, debits cash (or the trade-in account) for the proceeds and
    /// books the gain or loss. Depreciation must be posted through the month before the disposal date. Each tax book gets
    /// its disposal-year depreciation and its own gain or loss (proceeds less its adjusted basis); only the GAAP book posts.
    /// </summary>
    Task<AssetDto> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetCategoryDto>> ListCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds (null id) or changes a category. Deactivate one with <see cref="SaveAssetCategoryRequest.IsActive"/> = false: new assets can't use it.</summary>
    Task<AssetCategoryDto> SaveCategoryAsync(Guid? categoryId, SaveAssetCategoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a category no asset uses; one in use is refused (deactivate it instead).</summary>
    Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
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
