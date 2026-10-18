namespace Workbench.Domain.Imports;

/// <summary>Where an import's data comes from.</summary>
public enum ImportSource
{
    /// <summary>CSV or Excel exports of Costpoint screens and reports.</summary>
    Costpoint,

    /// <summary>The Smartsheet REST API, with the customer's API token.</summary>
    SmartsheetApi,

    /// <summary>A Smartsheet sheet exported to .xlsx.</summary>
    SmartsheetExcel,
}

/// <summary>What an import creates or updates.</summary>
public enum ImportKind
{
    OrgUnits,
    LaborCategories,
    Employees,
    CostRates,
    Customers,
    Contracts,
    Clins,
    Projects,
    BillRates,
    Budgets,
    Sheets,
    FixedAssets,
}

public enum ImportJobStatus
{
    Uploaded,
    Mapped,
    Validated,
    DryRunDone,
    Imported,
    Failed,
    RolledBack,
}

/// <summary>
/// One run of the import wizard: the uploaded files, the column mapping, the validation and dry-run results and,
/// once imported, the records it touched (<see cref="ImportJobRecord"/>) so it can be rolled back.
/// </summary>
public sealed class ImportJob : Entity
{
    public ImportSource Source { get; set; }
    public ImportKind Kind { get; set; }
    public ImportJobStatus Status { get; set; }

    /// <summary>Source column → target field mapping and column definitions (JSON).</summary>
    public string? MappingJson { get; set; }

    /// <summary>Options such as the target project, budget version and comment/attachment switches (JSON).</summary>
    public string? OptionsJson { get; set; }

    /// <summary>Smartsheet API token, encrypted with Data Protection. Kept only while the job needs it, then cleared.</summary>
    public string? EncryptedToken { get; set; }

    /// <summary>Sheets the token can see (Smartsheet API jobs, JSON).</summary>
    public string? AvailableJson { get; set; }

    public int TotalRows { get; set; }
    public int CreateCount { get; set; }
    public int UpdateCount { get; set; }
    public int SkipCount { get; set; }
    public int ErrorCount { get; set; }

    /// <summary>Row-level errors and warnings from the last validation, dry run or import (JSON).</summary>
    public string? ErrorsJson { get; set; }

    /// <summary>The planned creates, updates and skips from the last dry run or import (JSON).</summary>
    public string? PlanJson { get; set; }

    /// <summary>Why the import failed or couldn't be rolled back, when it did.</summary>
    public string? Message { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ImportedAtUtc { get; set; }
    public string? ImportedBy { get; set; }
    public DateTime? RolledBackAtUtc { get; set; }
    public string? RolledBackBy { get; set; }

    public List<ImportJobFile> Files { get; } = [];
}

/// <summary>An uploaded file (or a sheet fetched from the Smartsheet API, as JSON) belonging to a job.</summary>
public sealed class ImportJobFile : Entity
{
    public Guid ImportJobId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Length { get; set; }
    public byte[] Content { get; set; } = [];
    public int Order { get; set; }
}

public enum ImportRecordAction
{
    /// <summary>The import created the entity; rolling back deletes it.</summary>
    Created,

    /// <summary>The import changed the entity; rolling back restores <see cref="ImportJobRecord.BeforeJson"/>.</summary>
    Updated,

    /// <summary>The import removed the entity (budget lines it replaced); rolling back re-inserts it.</summary>
    Deleted,
}

/// <summary>
/// An entity an import touched, in the order it happened, with property snapshots before and after. Rolling back
/// walks these newest first and refuses when an entity changed or gained references after the import.
/// </summary>
public sealed class ImportJobRecord : Entity
{
    public Guid ImportJobId { get; set; }
    public int Sequence { get; set; }

    /// <summary>The entity's CLR type name (e.g. <c>Workbench.Domain.ProjectAccounting.Employee</c>).</summary>
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }
    public ImportRecordAction Action { get; set; }

    /// <summary>Human-readable label, e.g. <c>Employee E2001</c>.</summary>
    public required string Label { get; set; }

    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}

/// <summary>
/// Links an external id that Workbench has no field for (a Smartsheet row or sheet id) to the entity imported from it,
/// so a repeat import updates instead of duplicating.
/// </summary>
public sealed class ImportKeyMap : Entity
{
    /// <summary>For example <c>smartsheet.row</c> or <c>smartsheet.sheet</c>.</summary>
    public required string Scope { get; set; }

    public required string ExternalId { get; set; }
    public Guid EntityId { get; set; }
    public Guid ImportJobId { get; set; }
}
