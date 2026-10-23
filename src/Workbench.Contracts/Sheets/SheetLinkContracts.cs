namespace Workbench.Contracts.Sheets;

/// <summary>
/// What a sheet needs to show and edit its link and lookup columns: for each link column, the linked sheet and the rows
/// of it the user can see (none, and no name, when they can't see that sheet).
/// </summary>
public sealed record SheetLinksDto(IReadOnlyList<LinkColumnDto> Links, IReadOnlyList<LookupColumnDto> Lookups)
{
    public static SheetLinksDto Empty { get; } = new([], []);

    public LinkColumnDto? For(Guid columnId) => Links.FirstOrDefault(l => l.ColumnId == columnId);
}

/// <summary>A link column: <paramref name="Rows"/> are the linked sheet's rows the user may pick from or see.</summary>
public sealed record LinkColumnDto(Guid ColumnId, Guid TargetSheetId, string? TargetSheetName, bool CanSee, IReadOnlyList<LinkableRowDto> Rows)
{
    public LinkableRowDto? Row(Guid id) => Rows.FirstOrDefault(r => r.Id == id);
}

public sealed record LinkableRowDto(Guid Id, string Title, Guid? ParentId);

/// <summary>A lookup column: the link column it follows and the field of the linked rows it shows.</summary>
public sealed record LookupColumnDto(Guid ColumnId, Guid LinkColumnId, string Field, string? FieldName);

/// <summary>A sheet the user can link to or reference, with the fields they can see on it.</summary>
public sealed record LinkableSheetDto(Guid Id, string Name, string Workspace, IReadOnlyList<SheetFieldDto> Fields);

/// <summary>A field of a sheet: a column (key = its id) or one of <c>title</c>, <c>start</c>, <c>finish</c>.</summary>
public sealed record SheetFieldDto(string Key, string Name, ColumnKind Type);

/// <summary>
/// Linked sheets, like monday.com connect/mirror columns and Smartsheet cross-sheet links. Create the columns with
/// <see cref="IColumnService"/>: <see cref="ColumnKind.LinkedRow"/> with Options [other sheet id], and
/// <see cref="ColumnKind.Lookup"/> with Options [link column id, field key].
/// </summary>
public interface ISheetLinkService
{
    /// <summary>The sheet's link and lookup columns with what the user may see of the linked sheets. Needs Viewer.</summary>
    Task<SheetLinksDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>Every sheet the user can see, with its visible fields, to link to or reference.</summary>
    Task<IReadOnlyList<LinkableSheetDto>> ListSheetsAsync(CancellationToken cancellationToken = default);
}
