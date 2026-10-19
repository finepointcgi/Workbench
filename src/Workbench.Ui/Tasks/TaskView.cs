using MudBlazor;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;

namespace Workbench.Ui.Tasks;

/// <summary>Helpers for showing and changing task assignment in the sheet views.</summary>
public static class TaskView
{
    /// <summary>True when the item is the signed-in user's: in its Assignees, or in one of its Person columns.</summary>
    public static bool IsMine(SheetTasksDto? tasks, SheetDetail sheet, ItemDto item)
    {
        if (tasks is null || string.IsNullOrWhiteSpace(tasks.Me))
        {
            return false;
        }

        return tasks.IsAssignedTo(item.Id, tasks.Me)
            || sheet.Columns.Where(c => c.Type == ColumnKind.Person).Any(c => People(item.Values.GetValueOrDefault(c.Id.ToString())).Contains(tasks.Me, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The items to show for "My tasks": mine, plus their parents so the hierarchy still reads.</summary>
    public static IReadOnlySet<Guid> MineWithAncestors(SheetTasksDto? tasks, SheetDetail sheet, IReadOnlyList<ItemDto> items)
    {
        var byId = items.ToDictionary(i => i.Id);
        var keep = new HashSet<Guid>();
        foreach (var item in items.Where(i => IsMine(tasks, sheet, i)))
        {
            for (var current = item; current is not null && keep.Add(current.Id); current = current.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
            {
            }
        }

        return keep;
    }

    public static IEnumerable<string> People(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Names(IReadOnlyList<TaskAssigneeDto> assignees) =>
        assignees.Count == 0 ? "Unassigned" : string.Join(", ", assignees.Select(a => a.DisplayName));

    /// <summary>"AR" for Alex Rivera; the email's first letters when there's no name.</summary>
    public static string Initials(TaskAssigneeDto assignee)
    {
        var parts = assignee.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => Board.BoardLanes.Initials(assignee.Email),
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    /// <summary>Opens the assign dialog; true when the assignees (or due date) were saved.</summary>
    public static async Task<bool> AssignAsync(IDialogService dialogs, Guid sheetId, ItemDto item, IReadOnlyList<TaskAssigneeDto> current)
    {
        var parameters = new DialogParameters<AssignDialog>
        {
            { d => d.SheetId, sheetId },
            { d => d.Item, item },
            { d => d.Current, current },
        };
        var dialog = await dialogs.ShowAsync<AssignDialog>($"Assign \"{item.Title}\"", parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        return await dialog.Result is { Canceled: false };
    }
}
