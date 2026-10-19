using Workbench.Contracts.Sheets;

namespace Workbench.Contracts.Tasks;

// Task assignment: a sheet item's dedicated Assignees field (people from the directory). The item's Finish is the due date.
// New records next to the sheet contracts rather than new fields on ItemDto (CONVENTIONS: don't change Contracts/Sheets).

public sealed record TaskAssigneeDto(string Email, string DisplayName, string AssignedBy, DateTime AssignedAtUtc);

/// <summary>
/// One sheet's assignments for its views. <see cref="CanAssign"/>: Editors and Owners (project managers own their projects'
/// workspaces). <see cref="StatusColumn"/> is the sheet's "Status" Dropdown and <see cref="PercentColumn"/> its
/// "% Complete" Number column, if it has them: what an assignee without edit rights may still change on their own tasks.
/// </summary>
public sealed record SheetTasksDto(
    Guid SheetId,
    string Me,
    bool CanAssign,
    IReadOnlyDictionary<Guid, IReadOnlyList<TaskAssigneeDto>> Assignees,
    ColumnDto? StatusColumn,
    ColumnDto? PercentColumn)
{
    public IReadOnlyList<TaskAssigneeDto> For(Guid itemId) => Assignees.TryGetValue(itemId, out var list) ? list : [];

    public bool IsAssignedTo(Guid itemId, string email) =>
        For(itemId).Any(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Replaces an item's assignees with <paramref name="Emails"/> (directory users; empty clears them). When
/// <paramref name="Due"/> is set it becomes the item's Finish; <paramref name="ClearDue"/> clears the Finish.
/// </summary>
public sealed record SetAssigneesRequest(IReadOnlyList<string> Emails, DateOnly? Due = null, bool ClearDue = false);

/// <summary>An assignee's update of their own task: the Status option and/or % complete (0–100). Null leaves it as it is.</summary>
public sealed record UpdateTaskStatusRequest(string? Status, decimal? PercentComplete = null);

/// <summary>A task assigned to the current user through the Assignees field.</summary>
public sealed record MyTaskDto(
    Guid SheetId,
    string SheetName,
    Guid ItemId,
    string Title,
    DateOnly? Due,
    string? Status,
    bool IsDone,
    string AssignedBy,
    DateTime AssignedAtUtc);

public sealed record WorkloadTaskDto(Guid ItemId, string Title, DateOnly? Due, string? Status);

/// <summary>Open tasks per person on a project's plan sheet (Assignees and Person columns).</summary>
public sealed record WorkloadRowDto(
    string Email,
    string DisplayName,
    int Open,
    int Overdue,
    int DueThisWeek,
    DateOnly? NextDue,
    IReadOnlyList<WorkloadTaskDto> Tasks);

public sealed record ProjectWorkloadDto(Guid ProjectId, Guid? SheetId, DateOnly Today, IReadOnlyList<WorkloadRowDto> People);

public static class TaskActivityKinds
{
    /// <summary>Activity entry: assignees added or removed.</summary>
    public const string ItemAssigned = "item.assigned";
}

public interface ITaskAssignmentService
{
    /// <summary>Assignees of every item on the sheet the user can see (Viewer and up).</summary>
    Task<SheetTasksDto> GetSheetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an item's assignees (Editors and Owners). Newly assigned people are notified (in the app and by email, per
    /// their preference) and the change is recorded in activity. Throws <see cref="ArgumentException"/> for someone who
    /// isn't an active user.
    /// </summary>
    Task<IReadOnlyList<TaskAssigneeDto>> SetAssigneesAsync(Guid sheetId, Guid itemId, SetAssigneesRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a task's Status and/or % complete. Editors may on any item; an assignee (Assignees field or a Person column)
    /// may on their own item even as a Viewer or Commenter.
    /// </summary>
    Task<ItemDto> UpdateStatusAsync(Guid sheetId, Guid itemId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tasks assigned to the current user on sheets they can still see, ordered by due date.</summary>
    Task<IReadOnlyList<MyTaskDto>> ListMineAsync(bool includeDone = false, CancellationToken cancellationToken = default);

    /// <summary>Open tasks per person on the project's plan sheet (Finance, administrators and the project's manager).</summary>
    Task<ProjectWorkloadDto> GetProjectWorkloadAsync(Guid projectId, CancellationToken cancellationToken = default);
}
