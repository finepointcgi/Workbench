namespace Workbench.Contracts.Sheets;

/// <summary>
/// A sheet row, which is also a task on boards and the Gantt.
/// <para>
/// <see cref="Values"/> is keyed by column id (<c>Guid.ToString()</c>). Values are invariant strings:
/// Number/Currency <c>1234.5</c>, Date <c>yyyy-MM-dd</c>, Checkbox <c>true</c>/<c>false</c>,
/// Dropdown the option text, Person the user's email, CostpointLookup the Costpoint id.
/// </para>
/// </summary>
public sealed record ItemDto(
    Guid Id,
    Guid? ParentId,
    int Order,
    string Title,
    DateOnly? Start,
    DateOnly? Finish,
    string? CostpointWbsId,
    IReadOnlyDictionary<string, string?> Values,
    bool IsMilestone = false);

/// <summary>Creates an item. A null <paramref name="Order"/> appends it after its siblings.</summary>
public sealed record CreateItemRequest(
    string Title,
    Guid? ParentId = null,
    int? Order = null,
    DateOnly? Start = null,
    DateOnly? Finish = null,
    IReadOnlyDictionary<string, string?>? Values = null,
    bool IsMilestone = false);

/// <summary>
/// Replaces the editable fields of an item. Send the full set of values. <paramref name="IsMilestone"/> null leaves it
/// as it is. A milestone has one date: Start and Finish are set to the same day (Finish, or Start if there's no Finish).
/// </summary>
public sealed record UpdateItemRequest(
    string Title,
    DateOnly? Start,
    DateOnly? Finish,
    string? CostpointWbsId,
    IReadOnlyDictionary<string, string?> Values,
    bool? IsMilestone = null);

/// <summary>Moves an item under <paramref name="ParentId"/> (null = top level) at position <paramref name="Order"/>.</summary>
public sealed record MoveItemRequest(Guid? ParentId, int Order);

public interface IItemService
{
    /// <summary>All items in the sheet, ordered by parent then order.</summary>
    Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default);

    Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default);

    Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the item and all of its descendants.</summary>
    Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);
}
