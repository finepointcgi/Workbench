using MudBlazor;
using Workbench.Contracts.Scheduling;

namespace Workbench.Ui.Gantt;

/// <summary>Opens <see cref="TaskConstraintDialog"/> for one task, with its current constraint and conflicts from the plan.</summary>
public static class TaskConstraintEditor
{
    /// <summary>True when the dialog saved (the caller reloads either way: a save that found conflicts keeps the dialog open).</summary>
    public static async Task<bool> EditAsync(IDialogService dialogs, Guid sheetId, Guid itemId, string title, PlanScheduleDto? plan, DateOnly? suggested = null)
    {
        var parameters = new DialogParameters<TaskConstraintDialog>
        {
            { d => d.SheetId, sheetId },
            { d => d.ItemId, itemId },
            { d => d.Title, title },
            { d => d.Current, plan?.For(itemId)?.Constraint },
            { d => d.Conflicts, plan?.AllConflicts.Where(c => c.ItemId == itemId).ToList() ?? [] },
            { d => d.Suggested, suggested },
        };
        var dialog = await dialogs.ShowAsync<TaskConstraintDialog>("Date constraint", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        return await dialog.Result is { Canceled: false };
    }
}
