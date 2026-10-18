namespace Workbench.Contracts.ProjectAccounting;

/// <summary>How a project's WBS and its plan sheet disagree.</summary>
public enum PlanDifferenceKind
{
    /// <summary>A WBS element has no task in the plan sheet.</summary>
    WbsWithoutTask,

    /// <summary>A task in the plan sheet isn't linked to any WBS element.</summary>
    TaskWithoutWbs,

    /// <summary>A linked task's title differs from its WBS element's name.</summary>
    Renamed,

    /// <summary>A linked task shows a different WBS code than its element's (the name matches).</summary>
    CodeOutOfDate,
}

/// <summary>Which side wins when a difference is applied.</summary>
public enum PlanSyncDirection
{
    /// <summary>The WBS is the source: create or rename tasks in the plan sheet.</summary>
    WbsToPlan,

    /// <summary>The plan sheet is the source: create or rename WBS elements.</summary>
    PlanToWbs,
}

/// <summary>
/// One difference between the WBS and the plan sheet. <see cref="Key"/> identifies it when applying.
/// <see cref="CanApplyToPlan"/> / <see cref="CanApplyToWbs"/> say which directions can resolve it.
/// </summary>
public sealed record PlanDifferenceDto(
    string Key,
    PlanDifferenceKind Kind,
    Guid? WbsElementId,
    string? WbsCode,
    string? WbsName,
    Guid? ItemId,
    string? ItemTitle,
    bool CanApplyToPlan,
    bool CanApplyToWbs);

public sealed record PlanSyncReport(Guid ProjectId, Guid? SheetId, IReadOnlyList<PlanDifferenceDto> Differences);

/// <summary>Applies differences in one direction. A null <paramref name="Keys"/> applies every difference that direction can resolve.</summary>
public sealed record ApplyPlanSyncRequest(PlanSyncDirection Direction, IReadOnlyList<string>? Keys = null);

/// <summary>
/// Keeps a project's WBS and its plan sheet (one tree, <c>Project.SheetId</c> / <c>WbsElement.ItemId</c>) in step.
/// Linked tasks carry their element's WBS code in <c>ItemDto.CostpointWbsId</c>.
/// </summary>
public interface IPlanSyncService
{
    /// <summary>Creates a scheduling-enabled sheet whose items mirror the WBS tree and links them. Fails when the project already has one.</summary>
    Task<ProjectDto> CreatePlanSheetAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<PlanSyncReport> DiffAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Applies differences and returns what's left.</summary>
    Task<PlanSyncReport> ApplyAsync(Guid projectId, ApplyPlanSyncRequest request, CancellationToken cancellationToken = default);
}
