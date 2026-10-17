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

public sealed record ColumnDto(Guid Id, string Name, ColumnKind Type, int Order, IReadOnlyList<string> Options, string? Formula);

public sealed record CreateColumnRequest(string Name, ColumnKind Type, IReadOnlyList<string>? Options = null, string? Formula = null);

/// <summary>Replaces the editable fields of a column. The type can't be changed after creation.</summary>
public sealed record UpdateColumnRequest(string Name, IReadOnlyList<string> Options, string? Formula, int Order);

public interface IColumnService
{
    Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default);

    Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the column and removes its value from every item.</summary>
    Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default);
}
