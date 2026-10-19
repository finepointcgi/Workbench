using Workbench.Application.MyTodo;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Tasks;

namespace Workbench.Application.Tasks;

/// <summary>
/// "Assigned to you: X, due Fri": every open task assigned to the user through the Assignees field, whatever its due date
/// (undated tasks are "Coming up"). Sheet items found only through Person columns stay with <see cref="SheetTodoProvider"/>.
/// </summary>
public sealed class TaskTodoProvider : IMyTodoProvider
{
    public string Name => "Tasks";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<ITaskAssignmentService>() is not { } tasks)
        {
            return [];
        }

        return (await tasks.ListMineAsync(includeDone: false, cancellationToken))
            .Select(t => new MyTodoItem(
                Key(t.SheetId, t.ItemId), TodoCategories.Sheet,
                $"Assigned to you: {t.Title}",
                $"{t.SheetName} · " + (t.Due < context.Today ? $"was due {t.Due:ddd MMM d}" : TaskAssignmentService.DueText(t.Due)) + (t.Status is { } s ? $" · {s}" : ""),
                context.UrgencyFor(t.Due), t.Due, $"sheets/{t.SheetId}?item={t.ItemId}", "Open task", Priority: 1))
            .ToList();
    }

    /// <summary>The same key as <see cref="SheetTodoProvider"/> uses, so an item is listed once.</summary>
    public static string Key(Guid sheetId, Guid itemId) => $"sheet:{sheetId}:{itemId}";
}
