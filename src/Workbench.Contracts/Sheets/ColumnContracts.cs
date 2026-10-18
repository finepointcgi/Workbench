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
