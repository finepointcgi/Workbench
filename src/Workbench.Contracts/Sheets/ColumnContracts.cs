namespace Workbench.Contracts.Sheets;

/// <summary>Column data types. Cell values are stored as invariant strings (see <see cref="ItemDto.Values"/>).</summary>
public enum ColumnKind
{
    Text,
    Number,
    Currency,
    Date,
    Person,
    Dropdown,
    Checkbox,
    CostpointLookup,

    /// <summary>
    /// Links to rows of another sheet. <c>Options[0]</c> is the other sheet's id; the cell value is the linked item ids,
    /// comma-separated. Viewers who can't see that sheet see no links. Manage with <see cref="ISheetLinkService"/>.
    /// </summary>
    LinkedRow = 8,

    /// <summary>
    /// Read-only: shows a field of the rows linked in a <see cref="LinkedRow"/> column of this sheet, updated on every read.
    /// <c>Options</c> are the link column's id, then the field key (a column id, or <c>title</c>, <c>start</c>, <c>finish</c>).
    /// </summary>
    Lookup = 9,
}

/// <summary>
/// A sheet column. <paramref name="IsRestricted"/>: marked "Finance and owners only". <paramref name="ReadsRestricted"/>: a
/// formula column that reads a restricted column, so it is restricted too. Only people who may see restricted columns
/// (Finance, administrators, the workspace's owners) ever receive such columns.
/// </summary>
public sealed record ColumnDto(
    Guid Id, string Name, ColumnKind Type, int Order, IReadOnlyList<string> Options, string? Formula, bool IsRestricted = false, bool ReadsRestricted = false)
{
    public bool IsEffectivelyRestricted => IsRestricted || ReadsRestricted;
}

public sealed record CreateColumnRequest(string Name, ColumnKind Type, IReadOnlyList<string>? Options = null, string? Formula = null, bool IsRestricted = false);

/// <summary>
/// Replaces the editable fields of a column. The type can't be changed after creation. <paramref name="IsRestricted"/>
/// null leaves the restriction as it is; changing it needs Finance, Admin or Owner.
/// </summary>
public sealed record UpdateColumnRequest(string Name, IReadOnlyList<string> Options, string? Formula, int Order, bool? IsRestricted = null);

public interface IColumnService
{
    Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default);

    Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the column and removes its value from every item.</summary>
    Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default);
}
