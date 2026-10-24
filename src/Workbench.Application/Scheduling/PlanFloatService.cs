using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Scheduling;

/// <summary>
/// Total and free float for the grid's float columns and its "critical only" filter. Total float is the analysis's slack;
/// free float is worked out here from the same early dates and links (the analysis doesn't keep it). Reads through the
/// permission-checked dependency and item services, so it shows only what the caller may see.
/// </summary>
public sealed class PlanFloatService(IWorkbenchDbContextFactory dbFactory, IDependencyService dependencies, IItemService items) : IPlanFloatService
{
    public async Task<PlanFloatDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        var analysis = await dependencies.AnalyzeAsync(sheetId, cancellationToken);
        if (analysis.Items.Count == 0)
        {
            return PlanFloatDto.Empty;
        }

        var links = await dependencies.ListAsync(sheetId, cancellationToken);
        var milestones = (await items.ListAsync(sheetId, cancellationToken)).Where(i => i.IsMilestone).Select(i => i.Id).ToHashSet();
        WorkCalendar calendar;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            calendar = await PlanReflow.CalendarAsync(db, sheetId, cancellationToken);
        }

        return Compute(analysis, links, milestones, calendar);
    }

    /// <summary>
    /// Free float of each task: the least room before a successor's link binds (FS: its early start minus lag minus this
    /// early finish; SS, FF and SF alike), or before the plan's finish for a task with no successors.
    /// </summary>
    public static PlanFloatDto Compute(ScheduleAnalysis analysis, IReadOnlyList<DependencyDto> links, IReadOnlySet<Guid> milestones, WorkCalendar? calendar = null)
    {
        calendar ??= WorkCalendar.Weekdays;
        var es = analysis.Items.ToDictionary(t => t.ItemId, t => calendar.Index(t.EarlyStart));
        var ef = analysis.Items.ToDictionary(t => t.ItemId, t => milestones.Contains(t.ItemId) ? es[t.ItemId] : calendar.Index(t.EarlyFinish) + 1);
        var finish = ef.Values.Max();
        var outgoing = links.Where(l => es.ContainsKey(l.PredecessorId) && es.ContainsKey(l.SuccessorId)).ToLookup(l => l.PredecessorId);

        var tasks = analysis.Items.Select(t =>
        {
            var id = t.ItemId;
            var room = outgoing[id].Any()
                ? outgoing[id].Min(l => l.Type switch
                {
                    DependencyKind.FinishToStart => es[l.SuccessorId] - l.LagDays - ef[id],
                    DependencyKind.StartToStart => es[l.SuccessorId] - l.LagDays - es[id],
                    DependencyKind.FinishToFinish => ef[l.SuccessorId] - l.LagDays - ef[id],
                    _ => ef[l.SuccessorId] - l.LagDays - es[id],
                })
                : finish - ef[id];
            var free = Math.Max(0, Math.Min(room, t.SlackDays));
            return new TaskFloatDto(id, t.SlackDays, free, t.IsCritical);
        }).ToList();
        return new PlanFloatDto(tasks);
    }
}
