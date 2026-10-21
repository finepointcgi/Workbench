namespace Workbench.Domain.Customization;

/// <summary>What a custom field holds. The value's storage column follows from it (<see cref="CustomFieldValue"/>).</summary>
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

    /// <summary>A Workbench user, stored as their lower-case email.</summary>
    Person,

    /// <summary>Another customizable record, stored as its id.</summary>
    RecordLink,
    Url,
}

/// <summary>
/// An administrator-defined field on a customizable record type (projects, vendors, ...). Fields are retired
/// (<see cref="IsActive"/> false), never deleted, so the values recorded against them stay readable in history.
/// </summary>
public sealed class CustomFieldDefinition : Entity
{
    /// <summary>The customizable entity type key, for example "project" or "purchase-order".</summary>
    public required string EntityType { get; set; }

    /// <summary>Lower-case letters, digits and underscores; unique per entity type and never changed.</summary>
    public required string Key { get; set; }

    public required string Label { get; set; }
    public string? HelpText { get; set; }
    public CustomFieldType FieldType { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>Invariant text in the field's storage format (see <see cref="CustomFieldValue"/>), used for new records.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Once the record is approved (or posted, issued...), the value can no longer change.</summary>
    public bool ReadOnlyAfterApproval { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Only people holding the field's permission (Finance and administrators until custom roles) see the value.</summary>
    public bool IsSensitive { get; set; }

    public int Order { get; set; }

    /// <summary>The <see cref="ValueList.Key"/> a picklist field offers.</summary>
    public string? ValueListKey { get; set; }

    /// <summary>The entity type a record-link field points at.</summary>
    public string? LinkEntityType { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// One custom field's value on one record, in a shared table for every entity type: typed columns keep numbers and dates
/// sortable and filterable on every provider, and <see cref="SearchText"/> holds the lower-case display text for search.
/// Text, long text, URLs, picklist codes, people (emails) and record links (ids) use <see cref="TextValue"/>; multi-select
/// picklists store their codes there as a JSON array; numbers and currency <see cref="NumberValue"/>; dates
/// <see cref="DateValue"/>; yes/no <see cref="BoolValue"/>. A blank value is no row.
/// </summary>
public sealed class CustomFieldValue : Entity
{
    public Guid FieldId { get; set; }
    public required string EntityType { get; set; }
    public Guid RecordId { get; set; }
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public DateOnly? DateValue { get; set; }
    public bool? BoolValue { get; set; }
    public string SearchText { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// An administrator-managed list of values: offered by picklist custom fields and by the dropdowns that used to be
/// hard-coded. Built-in lists start from their code defaults (<c>ValueListCatalog</c>) until first edited.
/// </summary>
public sealed class ValueList : Entity
{
    /// <summary>Lower-case kebab-case, unique, never changed (for example "vendor-socioeconomic").</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Built in (used by Workbench itself): it can't be deleted, only edited.</summary>
    public bool IsSystem { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public List<ValueListItem> Items { get; } = [];
}

public sealed class ValueListItem : Entity
{
    public Guid ValueListId { get; set; }

    /// <summary>What records store; fixed once used. The label is what people see and can change freely.</summary>
    public required string Code { get; set; }

    public required string Label { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>An administrator's label for a built-in status or other fixed value (the state machine keeps the value).</summary>
public sealed class StatusLabel : Entity
{
    /// <summary>The status set, for example "project-status".</summary>
    public required string SetKey { get; set; }

    /// <summary>The built-in value's name, for example "Active".</summary>
    public required string Value { get; set; }

    public required string Label { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>An administrator-defined refinement of a built-in status (for example Active › "Awaiting funding"). Informational only.</summary>
public sealed class SubStatus : Entity
{
    public required string SetKey { get; set; }

    /// <summary>The built-in value it refines.</summary>
    public required string ParentValue { get; set; }

    public required string Code { get; set; }
    public required string Label { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A record's current sub-status in a set. It's cleared when the record's status no longer matches its parent.</summary>
public sealed class RecordSubStatus : Entity
{
    public required string SetKey { get; set; }
    public Guid RecordId { get; set; }
    public required string ParentValue { get; set; }
    public required string Code { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>An administrator's numbering pattern for a document type (absent: the built-in default pattern).</summary>
public sealed class NumberingScheme : Entity
{
    /// <summary>The document type key, for example "purchase-order".</summary>
    public required string DocumentType { get; set; }

    /// <summary>Literal text and tokens: {YYYY} {YY} {MM} {FY} {company} {contract} and exactly one {seq:N}.</summary>
    public required string Pattern { get; set; }

    /// <summary>A separate sequence per company (the pattern must then contain {company}).</summary>
    public bool PerCompany { get; set; }

    /// <summary>The sequence restarts every year (the pattern must then contain {YYYY}, {YY} or {FY}).</summary>
    public bool ResetEachYear { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// The next number of one document type in one scope (company, year, contract). Allocated with an optimistic
/// <see cref="Version"/> check and retried, so concurrent requests never share a number on any provider.
/// </summary>
public sealed class NumberSequence : Entity
{
    public required string DocumentType { get; set; }

    /// <summary>"" for one sequence, else for example "company=…|year=2027".</summary>
    public required string ScopeKey { get; set; }

    public long NextValue { get; set; } = 1;

    /// <summary>Concurrency token, bumped on every allocation.</summary>
    public int Version { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
