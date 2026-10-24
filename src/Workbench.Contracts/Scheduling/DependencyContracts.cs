namespace Workbench.Contracts.Scheduling;

public enum DependencyKind
{
    /// <summary>The successor starts after the predecessor finishes (FS).</summary>
    FinishToStart,

    /// <summary>The successor starts after the predecessor starts (SS).</summary>
    StartToStart,

    /// <summary>The successor finishes after the predecessor finishes (FF).</summary>
    FinishToFinish,

    /// <summary>The successor finishes after the predecessor starts (SF).</summary>
    StartToFinish,
}

/// <summary>A scheduling link. <paramref name="LagDays"/> is in working days on the plan's calendar; negative is lead.</summary>
public sealed record DependencyDto(Guid Id, Guid PredecessorId, Guid SuccessorId, DependencyKind Type, int LagDays);

public sealed record CreateDependencyRequest(
    Guid PredecessorId,
    Guid SuccessorId,
    DependencyKind Type = DependencyKind.FinishToStart,
    int LagDays = 0);

/// <summary>
/// Slack for one scheduled item, in working days. Critical items have zero (or negative) slack. <paramref name="SlackDays"/>
/// is total float; <paramref name="FreeFloatDays"/> how far it can slip before any successor moves (never negative).
/// </summary>
public sealed record ItemSlack(Guid ItemId, DateOnly EarlyStart, DateOnly EarlyFinish, int SlackDays, bool IsCritical, int? FreeFloatDays = null);

/// <summary>
/// Critical-path analysis of a sheet. Only leaf items with dates take part; summary rows span their children.
/// <paramref name="CriticalPath"/> lists the critical item ids in early-start order.
/// </summary>
public sealed record ScheduleAnalysis(
    DateOnly? ProjectStart,
    DateOnly? ProjectFinish,
    IReadOnlyList<Guid> CriticalPath,
    IReadOnlyList<ItemSlack> Items);

public interface IDependencyService
{
    Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links two items on the same sheet. Throws <see cref="ArgumentException"/> when the items are on
    /// different sheets, the link already exists, or it would create a cycle.
    /// </summary>
    Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default);

    /// <summary>Runs a forward and backward pass over the plan's working days (its calendar, holidays and constraints).</summary>
    Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default);
}
