namespace Workbench.Contracts.AccountingExport;

/// <summary>The package the file is for. Mappings and "already exported" are kept per target.</summary>
public enum AccountingTarget
{
    QuickBooks,
    Xero,
    Generic,
}

public enum AccountingExportKind
{
    ChartOfAccounts,
    JournalEntries,
    Invoices,
    Payments,
    Bills,
}

/// <summary>What a mapping row maps.</summary>
public enum AccountingMapKind
{
    Account,
    Customer,
    Vendor,

    /// <summary>The tax code a line carries in the destination. Workbench bills sales tax as its own line, so lines map the built-in <see cref="AccountingMapKeys.NoTax"/>.</summary>
    TaxCode,

    /// <summary>QuickBooks class / Xero tracking option: a Workbench project.</summary>
    Class,

    /// <summary>QuickBooks product or service for an invoice line, by Workbench line type (labor, ODC, fee, sales tax...).</summary>
    Item,
}

public static class AccountingMapKeys
{
    /// <summary>The built-in tax code every exported line carries.</summary>
    public const string NoTax = "NO_TAX";
}

public static class AccountingFormatIds
{
    public const string QuickBooksOnlineCsv = "qbo-csv";
    public const string QuickBooksDesktopIif = "qbd-iif";
    public const string XeroCsv = "xero-csv";
    public const string GenericCsv = "generic-csv";
    public const string GenericXlsx = "generic-xlsx";
}

/// <summary>An export format: what it is called, which package it is for, what it can export and where its layout comes from.</summary>
public sealed record AccountingFormatDto(
    string Id,
    string Name,
    AccountingTarget Target,
    string Extension,
    IReadOnlyList<AccountingExportKind> Kinds,
    string LayoutSource,
    string Notes);

// ---- Mapping ----

public sealed record AccountingMappingRowDto(
    AccountingMapKind Kind,
    string SourceKey,
    string SourceName,
    string? DestinationName,
    string? DestinationCode,
    bool InUse,
    bool IsMapped);

public sealed record AccountingMappingSetDto(
    AccountingTarget Target,
    IReadOnlyList<AccountingMappingRowDto> Rows,
    int Unmapped,
    int UnmappedInUse);

public sealed record AccountingMappingEntry(AccountingMapKind Kind, string SourceKey, string? DestinationName, string? DestinationCode);

public sealed record SaveAccountingMappingsRequest(IReadOnlyList<AccountingMappingEntry> Entries);

// ---- Export ----

/// <summary>
/// What to export. <paramref name="Again"/> re-includes records already exported to the target and needs a <paramref name="Note"/>;
/// without it, records that were exported before are left out and a period with nothing new is refused.
/// </summary>
public sealed record AccountingFileRequest(
    string FormatId,
    AccountingExportKind Kind,
    DateOnly From,
    DateOnly To,
    Guid? CompanyId = null,
    bool AllowUnmapped = false,
    bool Again = false,
    string? Note = null,
    bool IncludeSubledgerJournals = false);

public sealed record AccountingUnmappedDto(AccountingMapKind Kind, string SourceKey, string SourceName, int Uses);

public sealed record AccountingPriorRunDto(Guid RunId, DateTime CreatedAtUtc, string CreatedBy, DateOnly From, DateOnly To, int RecordCount, bool IsRepeat);

/// <summary>What an export would do, without doing it.</summary>
public sealed record AccountingExportPreviewDto(
    string FormatId,
    AccountingExportKind Kind,
    int NewRecords,
    int AlreadyExported,
    int Rows,
    decimal ControlTotal,
    IReadOnlyList<AccountingUnmappedDto> Unmapped,
    IReadOnlyList<AccountingPriorRunDto> PriorRuns,
    IReadOnlyList<string> Warnings,
    bool CanExport,
    string? BlockedReason);

public sealed record AccountingExportResultDto(
    Guid RunId,
    string FileName,
    string ContentType,
    byte[] Content,
    int RowCount,
    int RecordCount,
    decimal ControlTotal,
    string Checksum,
    IReadOnlyList<string> Warnings);

public sealed record AccountingExportRunDto(
    Guid Id,
    AccountingTarget Target,
    string FormatId,
    string FormatName,
    AccountingExportKind Kind,
    DateOnly From,
    DateOnly To,
    string FileName,
    int RowCount,
    int RecordCount,
    decimal ControlTotal,
    string Checksum,
    IReadOnlyList<string> Warnings,
    bool UnmappedAllowed,
    int UnmappedCount,
    bool IsRepeat,
    string? Note,
    string CreatedBy,
    DateTime CreatedAtUtc,
    bool HasFile);

/// <summary>
/// Accounting exports for QuickBooks, Xero and generic files. Needs the "Export to accounting software" permission (Finance and Admin).
/// Every file is a run in the history, and a record exported to a target is not exported to it again unless asked to (with a note).
/// </summary>
public interface IAccountingExportService
{
    Task<IReadOnlyList<AccountingFormatDto>> ListFormatsAsync(CancellationToken cancellationToken = default);

    /// <summary>What the export would contain and whether it may run: unmapped names, records exported before, warnings.</summary>
    Task<AccountingExportPreviewDto> PreviewAsync(AccountingFileRequest request, CancellationToken cancellationToken = default);

    /// <summary>Builds the file, records the run and marks its records exported, in one transaction.</summary>
    Task<AccountingExportResultDto> ExportAsync(AccountingFileRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountingExportRunDto>> ListRunsAsync(AccountingExportKind? kind = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>The file of an earlier run, byte for byte. Recorded in the security log.</summary>
    Task<AccountingExportResultDto> DownloadRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Maps Workbench accounts, customers, vendors, tax codes, classes and invoice items to the destination's names. Per target.</summary>
public interface IAccountingMappingService
{
    Task<AccountingMappingSetDto> GetAsync(AccountingTarget target, CancellationToken cancellationToken = default);

    /// <summary>Saves the given rows; a blank destination removes the mapping. Returns the whole set.</summary>
    Task<AccountingMappingSetDto> SaveAsync(AccountingTarget target, SaveAccountingMappingsRequest request, CancellationToken cancellationToken = default);
}
