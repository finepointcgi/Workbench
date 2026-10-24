using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Application.Imports.Smartsheet;

// ---- What the importer works on: a sheet from the API or an .xlsx export, in one shape ----

/// <summary>A sheet to import. <see cref="Id"/> is the Smartsheet sheet id (API) or the file name (.xlsx).</summary>
public sealed record SourceSheet(string Id, string Name, bool Scheduling, IReadOnlyList<SourceColumn> Columns, IReadOnlyList<SourceRow> Rows, bool FromApi);

/// <summary>
/// A column. <see cref="Type"/> is the Smartsheet type (TEXT_NUMBER, DATE, CHECKBOX, PICKLIST, CONTACT_LIST,
/// DURATION, PREDECESSOR...); for .xlsx it's inferred. <see cref="Tags"/> carries GANTT_START_DATE and friends.
/// </summary>
public sealed record SourceColumn(string Id, string Title, string Type, bool Primary, IReadOnlyList<string> Options, IReadOnlyList<string> Tags, string? SystemType, string? Symbol = null);

/// <summary>A row. <see cref="Number"/> is its 1-based position (what Smartsheet predecessors refer to).</summary>
public sealed record SourceRow(string Id, string? ParentId, int Number, IReadOnlyDictionary<string, SourceCell> Cells);

/// <summary>A cell, keyed by column title in <see cref="SourceRow.Cells"/>.</summary>
/// <see cref="Url"/>: the cell's hyperlink (API sheets).
public sealed record SourceCell(string? Value, string? Display, IReadOnlyList<SourcePredecessor>? Predecessors = null, decimal? DurationDays = null, string? Email = null,
    string? Url = null);

/// <summary>A predecessor link: by row id (API) or row number (.xlsx), with its type and lag in working days.</summary>
public sealed record SourcePredecessor(string? RowId, int? RowNumber, string Type, int LagDays);

public sealed record SourceComment(string Id, string RowId, string Text, string? AuthorName, string? AuthorEmail, DateTime? CreatedAtUtc);

public sealed record SourceAttachment(string Id, string RowId, string Name, string? MimeType);

// ---- Smartsheet REST API 2.0 payloads (only the fields the importer reads) ----

public sealed class SmartsheetPage<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
}

public sealed class SmartsheetSheetSummary
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("modifiedAt")] public DateTime? ModifiedAt { get; set; }
}

public sealed class SmartsheetSheet
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dependenciesEnabled")] public bool DependenciesEnabled { get; set; }
    [JsonPropertyName("ganttEnabled")] public bool GanttEnabled { get; set; }
    [JsonPropertyName("columns")] public List<SmartsheetColumn> Columns { get; set; } = [];
    [JsonPropertyName("rows")] public List<SmartsheetRow> Rows { get; set; } = [];
}

public sealed class SmartsheetColumn
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "TEXT_NUMBER";
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("systemColumnType")] public string? SystemColumnType { get; set; }

    /// <summary>A symbols column's set: RYG, FLAG, STAR_RATING, PRIORITY, HARVEY_BALLS…</summary>
    [JsonPropertyName("symbol")] public string? Symbol { get; set; }
}

public sealed class SmartsheetRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("rowNumber")] public int RowNumber { get; set; }
    [JsonPropertyName("parentId")] public long? ParentId { get; set; }
    [JsonPropertyName("cells")] public List<SmartsheetCell> Cells { get; set; } = [];
}

public sealed class SmartsheetCell
{
    [JsonPropertyName("columnId")] public long ColumnId { get; set; }
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    [JsonPropertyName("displayValue")] public string? DisplayValue { get; set; }
    [JsonPropertyName("objectValue")] public JsonElement? ObjectValue { get; set; }

    /// <summary>A link in the cell: <c>{"url": "https://…"}</c> (or a link to another sheet or report).</summary>
    [JsonPropertyName("hyperlink")] public JsonElement? Hyperlink { get; set; }
}

public sealed class SmartsheetDiscussion
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("parentId")] public long ParentId { get; set; }
    [JsonPropertyName("parentType")] public string? ParentType { get; set; }
    [JsonPropertyName("comments")] public List<SmartsheetComment>? Comments { get; set; }
}

public sealed class SmartsheetComment
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("createdBy")] public SmartsheetUser? CreatedBy { get; set; }
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
}

public sealed class SmartsheetUser
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
}

public sealed class SmartsheetAttachment
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("attachmentType")] public string? AttachmentType { get; set; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("parentType")] public string? ParentType { get; set; }
    [JsonPropertyName("parentId")] public long ParentId { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
}
