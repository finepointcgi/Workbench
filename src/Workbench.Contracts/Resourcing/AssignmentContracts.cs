namespace Workbench.Contracts.Resourcing;

// Assigning people: a PM gives a person a total number of hours on the project (or one of its tasks) between two dates,
// like a Unanet project assignment. Workbench spreads the hours over the working weeks as staffing (weekly allocations),
// which is what the heat map, budget-vs-staffed, charging and "Fill from my staffing" already read.

/// <summary>One week of an assignment's spread hours.</summary>
public sealed record AssignmentWeekDto(DateOnly WeekStart, decimal Hours);

/// <summary>
/// A person assigned to a project or task for <see cref="TotalHours"/> between <see cref="From"/> and <see cref="To"/>.
/// <see cref="Weeks"/> are the weekly hours spread from it; <see cref="ChargedHours"/> are hours the person has put on
/// timesheets (draft, submitted or approved) against it between the dates.
/// </summary>
public sealed record StaffingAssignmentDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    Guid ProjectId,
    string ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    string? WbsName,
    Guid? LaborCategoryId,
    string? LaborCategory,
    DateOnly From,
    DateOnly To,
    decimal TotalHours,
    decimal ChargedHours,
    IReadOnlyList<AssignmentWeekDto> Weeks,
    string? Notes)
{
    /// <summary>"1001.01.02 Build" or "Whole project".</summary>
    public string WbsLabel => WbsCode is null ? "Whole project" : $"{WbsCode} {WbsName}".Trim();

    public decimal LeftHours => TotalHours - ChargedHours;
}

/// <summary>
/// Someone staffed week by week (or from the plan) rather than by an assignment: their hours on one task and labor
/// category, first to last week. Assigning them by total takes those weeks over.
/// </summary>
public sealed record UnassignedStaffingDto(
    Guid EmployeeId,
    string EmployeeName,
    Guid? WbsElementId,
    string? WbsCode,
    string? WbsName,
    Guid? LaborCategoryId,
    string? LaborCategory,
    DateOnly From,
    DateOnly To,
    decimal Hours)
{
    public string WbsLabel => WbsCode is null ? "Whole project" : $"{WbsCode} {WbsName}".Trim();
}

/// <summary>A project's assignments, and any staffing that isn't part of one.</summary>
public sealed record ProjectAssignmentsDto(
    Guid ProjectId,
    string ProjectCode,
    DateOnly? ProjectStart,
    DateOnly? ProjectFinish,
    IReadOnlyList<StaffingAssignmentDto> Assignments,
    IReadOnlyList<UnassignedStaffingDto> WeekByWeek);

/// <summary>
/// Assign someone: <see cref="TotalHours"/> between <see cref="From"/> and <see cref="To"/>. Leave the dates out to use the
/// task's dates on the plan (or the project's). <see cref="LaborCategoryId"/> defaults to the person's own. Give
/// <see cref="Id"/> to change an existing assignment.
/// </summary>
public sealed record SaveAssignmentRequest(
    Guid EmployeeId,
    Guid ProjectId,
    decimal TotalHours,
    Guid? WbsElementId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? LaborCategoryId = null,
    string? Notes = null,
    Guid? Id = null);

/// <summary>A week where the person would be booked past what they can work, counting every project.</summary>
public sealed record AssignmentOverbookedWeekDto(DateOnly WeekStart, decimal ThisAssignment, decimal OtherHours, decimal Capacity);

/// <summary>What saving an assignment would do: the dates used, the weekly spread and the warnings. Warnings never block saving.</summary>
public sealed record AssignmentPreviewDto(
    DateOnly From,
    DateOnly To,
    Guid? LaborCategoryId,
    string? LaborCategory,
    IReadOnlyList<AssignmentWeekDto> Weeks,
    IReadOnlyList<AssignmentOverbookedWeekDto> Overbooked,
    IReadOnlyList<string> Warnings);

/// <summary>A pasted row: its line number, what it reads as, and why it can't be saved (Errors) or might be wrong (Warnings).</summary>
public sealed record AssignmentPasteRowDto(
    int Line,
    string Person,
    string Task,
    decimal? Hours,
    DateOnly? From,
    DateOnly? To,
    SaveAssignmentRequest? Request,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Request is not null && Errors.Count == 0;
}

public sealed record AssignmentPastePreviewDto(IReadOnlyList<AssignmentPasteRowDto> Rows)
{
    public int ValidCount => Rows.Count(r => r.IsValid);

    public bool HasErrors => Rows.Any(r => !r.IsValid);
}

/// <summary>HTTP body for pasting rows copied from Excel (tab- or comma-separated: person, task, hours, from, to).</summary>
public sealed record AssignmentPasteRequest(string Text);

/// <summary>HTTP body for saving several assignments at once (pasted rows, a copied team).</summary>
public sealed record SaveAssignmentsRequest(IReadOnlyList<SaveAssignmentRequest> Assignments);

public sealed record SetAssignmentWeekRequest(DateOnly WeekStart, decimal Hours);

/// <summary>
/// The signed-in person's hours left on an assignment, for the timesheet. <see cref="WbsElementIds"/> are the charge codes
/// it covers (the task, the leaves under it, or every code on the project). <see cref="ChargedElsewhereHours"/> are hours
/// already on other timesheets between the dates; the timesheet adds its own.
/// </summary>
public sealed record AssignmentHoursLeftDto(
    Guid AssignmentId,
    string ProjectCode,
    string Label,
    DateOnly From,
    DateOnly To,
    decimal TotalHours,
    decimal ChargedElsewhereHours,
    IReadOnlyList<Guid> WbsElementIds);

/// <summary>
/// Assigning people to a project by total hours. Reading: Finance, administrators, resource managers and the project's
/// manager. Changing: resource managers, administrators and the project's manager.
/// </summary>
public interface IStaffingAssignmentService
{
    Task<ProjectAssignmentsDto> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The dates, weekly spread and "booked past their hours" warnings for a proposed assignment. Saves nothing.</summary>
    Task<AssignmentPreviewDto> PreviewAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates the assignment (or changes it, with Id) and spreads its hours into weekly staffing.</summary>
    Task<StaffingAssignmentDto> SaveAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Saves several assignments in one go; all or nothing.</summary>
    Task<IReadOnlyList<StaffingAssignmentDto>> SaveManyAsync(Guid projectId, IReadOnlyList<SaveAssignmentRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Removes the assignment and its weekly staffing.</summary>
    Task DeleteAsync(Guid assignmentId, CancellationToken cancellationToken = default);

    /// <summary>Sets one week's hours ("Adjust by week"); the assignment's total becomes the sum of its weeks.</summary>
    Task<StaffingAssignmentDto> SetWeekAsync(Guid assignmentId, DateOnly weekStart, decimal hours, CancellationToken cancellationToken = default);

    /// <summary>Reads rows pasted from Excel (person, task, hours, from, to) into assignments to check before saving.</summary>
    Task<AssignmentPastePreviewDto> PreviewPasteAsync(Guid projectId, string text, CancellationToken cancellationToken = default);

    /// <summary>The other project's team as assignments on this one (same people and hours, tasks matched by code or name, this project's dates). Saves nothing.</summary>
    Task<AssignmentPastePreviewDto> PreviewCopyTeamAsync(Guid projectId, Guid fromProjectId, CancellationToken cancellationToken = default);

    /// <summary>The signed-in person's assignments overlapping the dates, with hours left (for the timesheet).</summary>
    Task<IReadOnlyList<AssignmentHoursLeftDto>> ListMyHoursLeftAsync(DateOnly from, DateOnly to, Guid? excludeTimesheetId = null, CancellationToken cancellationToken = default);
}
