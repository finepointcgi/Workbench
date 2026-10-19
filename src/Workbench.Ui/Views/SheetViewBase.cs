using Microsoft.AspNetCore.Components;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Views;

/// <summary>
/// Base for every view of a sheet (grid, board, calendar, Gantt). The sheet page owns loading;
/// a view edits through <see cref="IItemService"/> / <see cref="IColumnService"/> and then
/// invokes <see cref="Changed"/> so the page reloads and every view sees the new data.
/// </summary>
public abstract class SheetViewBase : ComponentBase
{
    [Parameter, EditorRequired]
    public SheetDetail Sheet { get; set; } = default!;

    [Parameter, EditorRequired]
    public IReadOnlyList<ItemDto> Items { get; set; } = [];

    [Parameter]
    public EventCallback Changed { get; set; }

    /// <summary>The user's role on the sheet, from the sheet page. Null (no cascade) means full access.</summary>
    [CascadingParameter]
    public SheetAccess? Access { get; set; }

    /// <summary>Task assignment on this sheet, from the sheet page. Null (no cascade) hides assignees and "My tasks".</summary>
    [CascadingParameter]
    public Contracts.Tasks.SheetTasksDto? Tasks { get; set; }

    protected bool CanEdit => Access?.CanEdit ?? true;

    /// <summary>Editors assign; without the cascade nobody does.</summary>
    protected bool CanAssign => Tasks?.CanAssign == true && CanEdit;

    protected Guid SheetId => Sheet.Summary.Id;

    protected ColumnDto? FindColumn(string name) =>
        Sheet.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    protected static string? ValueOf(ItemDto item, ColumnDto column) =>
        item.Values.TryGetValue(column.Id.ToString(), out var value) ? value : null;
}
