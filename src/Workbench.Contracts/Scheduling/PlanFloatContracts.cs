namespace Workbench.Contracts.Scheduling;

/// <summary>
/// A task's float in the plan's working days. <paramref name="TotalFloatDays"/>: how far it can slip before the plan's finish
/// (or a deadline) moves; 0 or less is critical. <paramref name="FreeFloatDays"/>: how far it can slip before any successor
/// has to move (never more than the total float, never below 0).
/// </summary>
public sealed record TaskFloatDto(Guid ItemId, int TotalFloatDays, int FreeFloatDays, bool IsCritical);

/// <summary>Float for the dated leaf tasks of a plan (summary rows span their subtasks and have none of their own).</summary>
public sealed record PlanFloatDto(IReadOnlyList<TaskFloatDto> Tasks)
{
    public static PlanFloatDto Empty { get; } = new([]);

    public TaskFloatDto? For(Guid itemId) => Tasks.FirstOrDefault(t => t.ItemId == itemId);
}

/// <summary>Read-only float of a scheduled sheet, from the critical-path analysis (<see cref="IDependencyService.AnalyzeAsync"/>).</summary>
public interface IPlanFloatService
{
    Task<PlanFloatDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default);
}
