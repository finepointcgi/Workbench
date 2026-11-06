using MudBlazor;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;

namespace Workbench.Ui.Resourcing;

/// <summary>
/// Staffing someone from the heat map or Find people goes through the same checked dialog as the project's Staffing tab:
/// pick the project (unless one is known), then <see cref="AssignPersonDialog"/> previews the weeks, warns about
/// overbooking, the budget and the labor category, and saves a total-hours assignment.
/// </summary>
public static class StaffingFlow
{
    /// <summary>Returns the saved assignment, or null when cancelled or the save failed (the user was told why).</summary>
    public static async Task<StaffingAssignmentDto?> AssignAsync(
        IDialogService dialogs, ISnackbar snackbar, IProjectService projects, IStaffingAssignmentService assignments,
        ResourcingContext lookups, SaveAssignmentRequest start, Guid? projectId)
    {
        if (projectId is null || lookups.ManageableProjects.All(p => p.Id != projectId))
        {
            var pick = await dialogs.ShowAsync<PickProjectDialog>("Staff on which project?",
                new DialogParameters<PickProjectDialog> { { d => d.Projects, lookups.ManageableProjects }, { d => d.DefaultProjectId, projectId } },
                new DialogOptions { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true });
            if (await pick.Result is not { Canceled: false, Data: Guid picked })
            {
                return null;
            }

            projectId = picked;
        }

        IReadOnlyList<WbsElementDto> wbs;
        try
        {
            wbs = await projects.ListWbsAsync(projectId.Value);
        }
        catch (Exception ex) when (ResourcingFormat.IsExpected(ex))
        {
            snackbar.Add($"Couldn't load the project's tasks: {ex.Message}", Severity.Error);
            return null;
        }

        var parameters = new DialogParameters<AssignPersonDialog>
        {
            { d => d.ProjectId, projectId.Value },
            { d => d.Wbs, wbs },
            { d => d.Categories, lookups.LaborCategories },
            { d => d.Start, start with { ProjectId = projectId.Value } },
        };
        var dialog = await dialogs.ShowAsync<AssignPersonDialog>("Assign a person", parameters, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: SaveAssignmentRequest request })
        {
            return null;
        }

        try
        {
            var saved = await assignments.SaveAsync(request);
            snackbar.Add($"Assigned {saved.EmployeeName} {ResourcingFormat.Hours(saved.TotalHours)} h on {saved.WbsLabel}.", Severity.Success);
            return saved;
        }
        catch (Exception ex) when (ResourcingFormat.IsExpected(ex))
        {
            snackbar.Add(ex.Message, Severity.Error);
            return null;
        }
    }
}
