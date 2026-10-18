using Workbench.Contracts.Financials;
using Workbench.Contracts.Sheets;

namespace Workbench.Contracts.Imports;

// Enums mirror Workbench.Domain.Imports by name and value.

public enum ImportSource { Costpoint, SmartsheetApi, SmartsheetExcel }

public enum ImportKind { OrgUnits, LaborCategories, Employees, CostRates, Customers, Contracts, Clins, Projects, BillRates, Budgets, Sheets }

public enum ImportJobStatus { Uploaded, Mapped, Validated, DryRunDone, Imported, Failed, RolledBack }

public enum ImportRecordAction { Created, Updated, Deleted }

/// <summary>What a dry run (or the import) does with one source row.</summary>
public enum ImportRowAction { Create, Update, Skip }

/// <summary>A target field of an import kind, with the source headers that map to it automatically.</summary>
public sealed record ImportFieldDto(string Name, string Label, bool Required, string Description, IReadOnlyList<string> Aliases);

/// <summary>An import kind and the fields its file needs.</summary>
public sealed record ImportKindDto(
    ImportSource Source,
    ImportKind Kind,
    string Title,
    string Description,
    IReadOnlyList<ImportFieldDto> Fields,
    string? SampleFile);

/// <summary>
/// How a Smartsheet (or .xlsx) column becomes a Workbench column. <see cref="Source"/> is the source column's id
/// (API) or header (.xlsx); excluded columns are not imported.
/// </summary>
public sealed record ImportColumnDto(string Source, string Name, ColumnKind Type, bool Include, IReadOnlyList<string> Options, string? SourceType = null);

/// <summary>
/// The column mapping: target field name → source header (null = not mapped), plus, for Smartsheet, the columns to
/// create.
/// </summary>
public sealed record ImportMappingDto(IReadOnlyDictionary<string, string?> Fields, IReadOnlyList<ImportColumnDto> Columns);

/// <summary>
/// Job options. <see cref="ProjectId"/>: the project budgets go to, or whose WBS Smartsheet rows link to.
/// <see cref="LinkPlanSheet"/>: make the imported sheet that project's plan sheet.
/// </summary>
public sealed record ImportOptionsDto(
    Guid? ProjectId = null,
    string? BudgetVersionName = null,
    BudgetVersionKind BudgetVersionKind = BudgetVersionKind.Current,
    bool LinkPlanSheet = true,
    bool IncludeComments = false,
    bool IncludeAttachments = false,
    IReadOnlyList<string>? SheetIds = null);

public sealed record ImportFileDto(string FileName, long Length);

/// <summary>A row-level problem. <see cref="Row"/> is the 1-based row in the source file (0 = the whole file).</summary>
public sealed record ImportErrorDto(string File, int Row, string? Field, string Message, bool IsWarning = false);

/// <summary>One planned (or performed) change: what happens to a source row.</summary>
public sealed record ImportPlanRowDto(string File, int Row, ImportRowAction Action, string EntityType, string Key, string Summary);

/// <summary>A sheet a Smartsheet token can see.</summary>
public sealed record SmartsheetSheetInfoDto(string Id, string Name, DateTime? ModifiedAtUtc);

public sealed record ImportCountsDto(int TotalRows, int Creates, int Updates, int Skips, int Errors);

public sealed record ImportJobDto(
    Guid Id,
    ImportSource Source,
    ImportKind Kind,
    ImportJobStatus Status,
    IReadOnlyList<ImportFileDto> Files,
    ImportCountsDto Counts,
    string? Message,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? ImportedAtUtc,
    DateTime? RolledBackAtUtc,
    string? RolledBackBy,
    bool HasToken);

/// <summary>A job with everything the wizard shows: headers and sample rows of the source, mapping, errors and plan.</summary>
public sealed record ImportJobDetailDto(
    ImportJobDto Job,
    ImportKindDto Definition,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    ImportMappingDto Mapping,
    ImportOptionsDto Options,
    IReadOnlyList<ImportErrorDto> Errors,
    IReadOnlyList<ImportPlanRowDto> Plan,
    IReadOnlyList<SmartsheetSheetInfoDto> AvailableSheets);

public sealed record ImportRecordDto(int Sequence, string EntityType, Guid EntityId, ImportRecordAction Action, string Label);

/// <summary>Whether a job can be rolled back, and if not, why (each blocker names the record and what references it).</summary>
public sealed record RollbackCheckDto(bool CanRollBack, IReadOnlyList<string> Blockers, int RecordCount);

public sealed record UploadFileRequest(string FileName, byte[] Content);

public sealed record UploadImportRequest(ImportSource Source, ImportKind Kind, IReadOnlyList<UploadFileRequest> Files, ImportOptionsDto? Options = null);

public sealed record ConnectSmartsheetRequest(string ApiToken, ImportOptionsDto? Options = null);

public sealed record SelectSmartsheetSheetsRequest(IReadOnlyList<string> SheetIds);

public sealed record SaveImportMappingRequest(ImportMappingDto Mapping, ImportOptionsDto Options);

/// <summary>
/// Moves data from Costpoint exports and Smartsheet into Workbench: upload → map → validate → dry run → import, and
/// roll back. Imports go through the regular services (so validation, permissions and auditing apply) and match on
/// natural keys, so importing the same file again updates instead of duplicating. Finance and administrators only.
/// </summary>
public interface IImportService
{
    Task<IReadOnlyList<ImportKindDto>> ListKindsAsync(CancellationToken cancellationToken = default);

    /// <summary>Jobs, newest first.</summary>
    Task<IReadOnlyList<ImportJobDto>> ListJobsAsync(CancellationToken cancellationToken = default);

    Task<ImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Creates a job from uploaded CSV/.xlsx files and suggests a mapping from their headers.</summary>
    Task<ImportJobDetailDto> UploadAsync(UploadImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks a Smartsheet API token, lists the sheets it can see and keeps it (encrypted) for this job.</summary>
    Task<ImportJobDetailDto> ConnectSmartsheetAsync(ConnectSmartsheetRequest request, CancellationToken cancellationToken = default);

    /// <summary>Fetches the chosen sheets from Smartsheet into the job and suggests a mapping.</summary>
    Task<ImportJobDetailDto> SelectSmartsheetSheetsAsync(Guid jobId, SelectSmartsheetSheetsRequest request, CancellationToken cancellationToken = default);

    Task<ImportJobDetailDto> SaveMappingAsync(Guid jobId, SaveImportMappingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Checks every row against the mapping and reports row-level errors.</summary>
    Task<ImportJobDetailDto> ValidateAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Works out what the import would create, update and skip, without changing anything.</summary>
    Task<ImportJobDetailDto> DryRunAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Imports the valid rows. Rows with errors are skipped and reported.</summary>
    Task<ImportJobDetailDto> ImportAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImportRecordDto>> ListRecordsAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<RollbackCheckDto> CheckRollbackAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes an import: deletes what it created and restores what it changed. Refused (ArgumentException with the
    /// reasons) when anything was changed or referenced since.
    /// </summary>
    Task<ImportJobDetailDto> RollBackAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Discards a job that hasn't been imported (and its token).</summary>
    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}
