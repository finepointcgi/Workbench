namespace Workbench.Contracts.Customization;

// Admin-configurable data: custom fields on core records, value lists, status labels and sub-statuses, and document
// numbering. Values cross the wire as invariant strings (see CustomFieldValues for the formats).

public enum CustomFieldType
{
    Text,
    LongText,
    Number,
    Currency,
    Date,
    YesNo,
    Picklist,
    MultiPicklist,
    Person,
    RecordLink,
    Url,
}

/// <summary>Entity type keys of the records that take custom fields (kebab-case, shared with automations and forms).</summary>
public static class CustomizableEntityTypes
{
    public const string Project = "project";
    public const string Contract = "contract";
    public const string WbsElement = "wbs-element";
    public const string Employee = "employee";
    public const string Vendor = "vendor";
    public const string Customer = "customer";
    public const string PurchaseOrder = "purchase-order";
    public const string ApBill = "ap-bill";
    public const string Invoice = "invoice";
    public const string ExpenseReport = "expense-report";
    public const string Timesheet = "timesheet";
    public const string Part = "part";
    public const string Opportunity = "opportunity";
}

/// <summary>How values are written as text: what the API, CSV and the editors use.</summary>
public static class CustomFieldValues
{
    /// <summary>Custom fields appear in the entity field catalog as <c>cf.&lt;key&gt;</c>, apart from built-in fields.</summary>
    public const string CatalogPrefix = "cf.";

    public const string Masked = "••••••";

    /// <summary>The field-level permission a sensitive field needs, for the permission catalog.</summary>
    public static string PermissionKey(string entityType, string key) => $"field.{entityType}.{key}";

    /// <summary>Formats by type: numbers and currency invariant ("1234.5"), dates yyyy-MM-dd, yes/no true/false,
    /// multi-select a comma-separated list of codes, person an email, record link the record id.</summary>
    public const string FormatHelp = "Numbers 1234.5, dates yyyy-MM-dd, yes/no true or false, multi-select codes separated by commas.";
}

public sealed record CustomizableEntityDto(string EntityType, string Label, string LabelPlural, bool HasApproval, string? StatusSet);

public sealed record CustomFieldDefinitionDto(
    Guid Id,
    string EntityType,
    string Key,
    string Label,
    string? HelpText,
    CustomFieldType FieldType,
    bool IsRequired,
    string? DefaultValue,
    bool ReadOnlyAfterApproval,
    bool IsActive,
    bool IsSensitive,
    int Order,
    string? ValueListKey,
    string? LinkEntityType,
    string? PermissionKey,
    int ValueCount);

/// <param name="Key">Generated from the label when empty on a new field; never changes afterwards.</param>
public sealed record SaveCustomFieldRequest(
    string EntityType,
    string Label,
    CustomFieldType FieldType,
    string? Key = null,
    string? HelpText = null,
    bool IsRequired = false,
    string? DefaultValue = null,
    bool ReadOnlyAfterApproval = false,
    bool IsSensitive = false,
    string? ValueListKey = null,
    string? LinkEntityType = null,
    bool IsActive = true);

public sealed record CustomFieldOptionDto(string Code, string Label);

/// <summary>One field's value on a record, as the editor shows it.</summary>
/// <param name="Value">Storage text (null = blank, or masked).</param>
/// <param name="Display">What a reader sees (labels instead of codes, names instead of ids, the mask).</param>
public sealed record CustomFieldValueDto(
    Guid FieldId,
    string Key,
    string Label,
    string? HelpText,
    CustomFieldType FieldType,
    bool IsRequired,
    bool IsActive,
    bool IsMasked,
    bool IsReadOnly,
    string? Value,
    string? Display,
    IReadOnlyList<CustomFieldOptionDto>? Options,
    string? LinkEntityType);

/// <param name="RecordId">Null when describing a new record (values are the defaults).</param>
public sealed record RecordCustomFieldsDto(string EntityType, Guid? RecordId, bool IsLocked, IReadOnlyList<CustomFieldValueDto> Fields);

/// <summary>Values by field key; a key that's left out keeps its value, null or "" clears it.</summary>
public sealed record SaveCustomFieldValuesRequest(IReadOnlyDictionary<string, string?> Values);

public enum CustomFieldOperator
{
    Equals,
    NotEquals,
    Contains,
    IsBlank,
    IsNotBlank,
    AtLeast,
    AtMost,
}

public sealed record CustomFieldFilter(string Key, CustomFieldOperator Operator, string? Value = null);

public sealed record CustomFieldSearchHitDto(string EntityType, string EntityLabel, Guid RecordId, string Title, string? Link, string FieldLabel, string Display);

/// <summary>Display values of many records, by record id then field key (sensitive ones masked).</summary>
public sealed record CustomFieldTableDto(IReadOnlyList<CustomFieldDefinitionDto> Fields, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string?>> Values);

public sealed record CustomFieldExportDto(string FileName, string ContentType, byte[] Content);

/// <summary>Request bodies of the customization API.</summary>
public sealed record CustomFieldTableRequest(IReadOnlyList<Guid>? RecordIds);

public sealed record SetSubStatusRequest(string? Code);

/// <summary>Custom field definitions (Admin) and values (whoever can see or change the record).</summary>
public interface ICustomFieldService
{
    Task<IReadOnlyList<CustomizableEntityDto>> ListEntityTypesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(string entityType, bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Creates (null id) or changes a field. The key and type are fixed once the field has values.</summary>
    Task<CustomFieldDefinitionDto> SaveDefinitionAsync(Guid? id, SaveCustomFieldRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retires (hides) or restores a field. Retired fields keep their values and history.</summary>
    Task<CustomFieldDefinitionDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Puts the entity type's fields in this order (ids not listed keep their relative order after them).</summary>
    Task<IReadOnlyList<CustomFieldDefinitionDto>> ReorderAsync(string entityType, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>A record's active fields with values (or the defaults when <paramref name="recordId"/> is null).</summary>
    Task<RecordCustomFieldsDto> GetValuesAsync(string entityType, Guid? recordId, CancellationToken cancellationToken = default);

    /// <summary>Validates and saves values, audits the change. Required fields must end up with a value.</summary>
    Task<RecordCustomFieldsDto> SaveValuesAsync(string entityType, Guid recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default);

    /// <summary>What would block saving these values (empty = fine); <paramref name="recordId"/> null for a new record.</summary>
    Task<IReadOnlyList<string>> ValidateAsync(string entityType, Guid? recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ids of the records whose values pass every filter.</summary>
    Task<IReadOnlyList<Guid>> FilterAsync(string entityType, IReadOnlyList<CustomFieldFilter> filters, CancellationToken cancellationToken = default);

    /// <summary>Records whose custom field values contain <paramref name="text"/>.</summary>
    Task<IReadOnlyList<CustomFieldSearchHitDto>> SearchAsync(string text, string? entityType = null, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Display values for list pages, exports and reports.</summary>
    Task<CustomFieldTableDto> GetTableAsync(string entityType, IReadOnlyList<Guid>? recordIds = null, CancellationToken cancellationToken = default);

    /// <summary>Every record of the type with its custom field values, as CSV.</summary>
    Task<CustomFieldExportDto> ExportCsvAsync(string entityType, CancellationToken cancellationToken = default);

    /// <summary>Records of a type whose title contains <paramref name="text"/> (Code = the record id), for record-link fields.</summary>
    Task<IReadOnlyList<CustomFieldOptionDto>> LookupRecordsAsync(string entityType, string? text, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Copies a record's values onto a copy of it (cloning), skipping retired fields.</summary>
    Task CopyValuesAsync(string entityType, Guid fromRecordId, Guid toRecordId, CancellationToken cancellationToken = default);
}

// ---- Value lists, status labels and sub-statuses ----

public sealed record ValueListItemDto(string Code, string Label, int Order, bool IsActive);

public sealed record ValueListDto(string Key, string Name, string? Description, bool IsSystem, bool IsCustomized, IReadOnlyList<ValueListItemDto> Items, IReadOnlyList<string> UsedBy);

public sealed record SaveValueListItem(string Code, string Label, bool IsActive = true);

/// <summary>Saves a list's name and items in order. Codes already stored on records can't be removed, only deactivated.</summary>
public sealed record SaveValueListRequest(string Key, string Name, string? Description, IReadOnlyList<SaveValueListItem> Items);

public sealed record StatusValueDto(string Value, string DefaultLabel, string Label, IReadOnlyList<ValueListItemDto> SubStatuses);

/// <param name="AllowsSubStatuses">Whether records of this set take an informational sub-status.</param>
public sealed record StatusSetDto(string Key, string Name, string Description, bool AllowsSubStatuses, string? EntityType, IReadOnlyList<StatusValueDto> Values);

public sealed record SaveStatusValue(string Value, string? Label, IReadOnlyList<SaveValueListItem>? SubStatuses = null);

public sealed record SaveStatusSetRequest(IReadOnlyList<SaveStatusValue> Values);

public sealed record RecordSubStatusDto(string SetKey, Guid RecordId, string? ParentValue, string? Code, string? Label);

/// <summary>Value lists and status labels: anyone signed in reads them, administrators change them.</summary>
public interface ICustomListService
{
    Task<IReadOnlyList<ValueListDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>A list by key (built-in lists answer with their defaults until first edited).</summary>
    Task<ValueListDto> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<ValueListDto> SaveAsync(SaveValueListRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes an unused custom list; built-in lists go back to their defaults.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StatusSetDto>> ListStatusSetsAsync(CancellationToken cancellationToken = default);

    Task<StatusSetDto> SaveStatusSetAsync(string setKey, SaveStatusSetRequest request, CancellationToken cancellationToken = default);

    Task<RecordSubStatusDto> GetSubStatusAsync(string setKey, Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears, with null) a record's sub-status; it must refine the record's current status.</summary>
    Task<RecordSubStatusDto> SetSubStatusAsync(string setKey, Guid recordId, string? code, CancellationToken cancellationToken = default);
}

// ---- Numbering ----

public sealed record NumberingSchemeDto(
    string DocumentType,
    string Name,
    string DefaultPattern,
    string Pattern,
    bool PerCompany,
    bool ResetEachYear,
    bool IsCustomized,
    string Example,
    IReadOnlyList<string> Tokens);

public sealed record SaveNumberingSchemeRequest(string Pattern, bool PerCompany = false, bool ResetEachYear = false);

public sealed record NumberingPreviewDto(string Example, string? Error);

/// <summary>Document numbering patterns: anyone signed in reads, administrators change. Numbers are allocated server-side.</summary>
public interface INumberingService
{
    Task<IReadOnlyList<NumberingSchemeDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<NumberingSchemeDto> SaveAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Goes back to the built-in pattern.</summary>
    Task<NumberingSchemeDto> ResetAsync(string documentType, CancellationToken cancellationToken = default);

    /// <summary>What a pattern would produce today (nothing is allocated).</summary>
    Task<NumberingPreviewDto> PreviewAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default);
}
