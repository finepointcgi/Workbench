using Workbench.Contracts.Scheduling;

namespace Workbench.Application.Scheduling;

/// <summary>A task in the network. A milestone has no duration; any other task lasts at least one working day.</summary>
public sealed record ScheduleTask(Guid Id, DateOnly Start, DateOnly Finish, bool IsMilestone = false);

public sealed record ScheduleLink(Guid PredecessorId, Guid SuccessorId, DependencyKind Type, int LagDays);

/// <summary>
/// Classic CPM over the plan calendar's working-day indexes (<see cref="WorkCalendar.Index"/>: holidays and days off don't
/// count). Each task is the half-open range [ES, ES + duration). A task's planned start acts as a "start no earlier than"
/// constraint, so tasks without predecessors stay where they are planned. Date constraints apply as in MS Project: No
/// Earlier Than and Must kinds bound the early dates, No Later Than and Must kinds the late dates, so a missed deadline
/// shows as negative slack.
/// </summary>
public static class CriticalPathCalculator
{
    public static ScheduleAnalysis Analyze(
        IReadOnlyCollection<ScheduleTask> tasks, IReadOnlyCollection<ScheduleLink> links, WorkCalendar? calendar = null,
        IReadOnlyDictionary<Guid, PlanConstraintDto>? constraints = null)
    {
        calendar ??= WorkCalendar.Weekdays;
        constraints ??= new Dictionary<Guid, PlanConstraintDto>();
        if (tasks.Count == 0)
        {
            return new ScheduleAnalysis(null, null, [], []);
        }

        var byId = tasks.ToDictionary(t => t.Id);
        var edges = links.Where(l => l.PredecessorId != l.SuccessorId && byId.ContainsKey(l.PredecessorId) && byId.ContainsKey(l.SuccessorId)).ToList();
        var incoming = edges.ToLookup(l => l.SuccessorId);
        var outgoing = edges.ToLookup(l => l.PredecessorId);
        var order = TopologicalOrder(tasks, edges);

        var duration = tasks.ToDictionary(t => t.Id, t => calendar.Duration(t.Start, t.Finish, t.IsMilestone));
        int Day(PlanConstraintDto c) => calendar.Index(ScheduleEngine.EffectiveDate(c, calendar));

        // The index just after a task finishing on the constraint's day (a milestone takes no time, so it's the day itself).
        int End(Guid id, PlanConstraintDto c) => Day(c) + (duration[id] == 0 ? 0 : 1);
        var es = new Dictionary<Guid, int>();
        var ef = new Dictionary<Guid, int>();
        foreach (var id in order)
        {
            var start = calendar.Index(byId[id].Start);
            constraints.TryGetValue(id, out var constraint);
            foreach (var link in incoming[id].Where(l => ef.ContainsKey(l.PredecessorId)))
            {
                var p = link.PredecessorId;
                start = Math.Max(start, link.Type switch
                {
                    DependencyKind.FinishToStart => ef[p] + link.LagDays,
                    DependencyKind.StartToStart => es[p] + link.LagDays,
                    DependencyKind.FinishToFinish => ef[p] + link.LagDays - duration[id],
                    _ => es[p] + link.LagDays - duration[id],
                });
            }

            start = constraint?.Kind switch
            {
                PlanConstraintKind.StartNoEarlierThan => Math.Max(start, Day(constraint)),
                PlanConstraintKind.FinishNoEarlierThan => Math.Max(start, End(id, constraint) - duration[id]),
                PlanConstraintKind.MustStartOn => Day(constraint),
                PlanConstraintKind.MustFinishOn => End(id, constraint) - duration[id],
                _ => start,
            };

            es[id] = start;
            ef[id] = start + duration[id];
        }

        var projectFinish = ef.Values.Max();
        var ls = new Dictionary<Guid, int>();
        var lf = new Dictionary<Guid, int>();
        foreach (var id in Enumerable.Reverse(order))
        {
            var finish = projectFinish;
            constraints.TryGetValue(id, out var constraint);
            foreach (var link in outgoing[id].Where(l => lf.ContainsKey(l.SuccessorId)))
            {
                var s = link.SuccessorId;
                finish = Math.Min(finish, link.Type switch
                {
                    DependencyKind.FinishToStart => ls[s] - link.LagDays,
                    DependencyKind.StartToStart => ls[s] - link.LagDays + duration[id],
                    DependencyKind.FinishToFinish => lf[s] - link.LagDays,
                    _ => lf[s] - link.LagDays + duration[id],
                });
            }

            finish = constraint?.Kind switch
            {
                PlanConstraintKind.FinishNoLaterThan or PlanConstraintKind.MustFinishOn => Math.Min(finish, End(id, constraint)),
                PlanConstraintKind.StartNoLaterThan or PlanConstraintKind.MustStartOn => Math.Min(finish, Day(constraint) + duration[id]),
                _ => finish,
            };

            lf[id] = finish;
            ls[id] = finish - duration[id];
        }

        // Free float: how far a task can slip before the first successor moves (or, at the end of the network, the plan finish).
        int FreeFloat(Guid id)
        {
            var free = outgoing[id].Any(l => es.ContainsKey(l.SuccessorId))
                ? outgoing[id].Where(l => es.ContainsKey(l.SuccessorId)).Min(l => l.Type switch
                {
                    DependencyKind.FinishToStart => es[l.SuccessorId] - (ef[id] + l.LagDays),
                    DependencyKind.StartToStart => es[l.SuccessorId] - (es[id] + l.LagDays),
                    DependencyKind.FinishToFinish => ef[l.SuccessorId] - (ef[id] + l.LagDays),
                    _ => ef[l.SuccessorId] - (es[id] + l.LagDays),
                })
                : projectFinish - ef[id];
            return Math.Max(0, Math.Min(free, ls[id] - es[id]));
        }

        var items = order
            .Select(id => new ItemSlack(
                id,
                calendar.FromIndex(es[id]),
                calendar.FromIndex(duration[id] == 0 ? es[id] : ef[id] - 1),
                ls[id] - es[id],
                ls[id] - es[id] <= 0,
                FreeFloat(id)))
            .OrderBy(i => i.EarlyStart)
            .ThenBy(i => i.EarlyFinish)
            .ToList();
        return new ScheduleAnalysis(
            items.Min(i => i.EarlyStart),
            items.Max(i => i.EarlyFinish),
            items.Where(i => i.IsCritical).Select(i => i.ItemId).ToList(),
            items);
    }

    /// <summary>Kahn's algorithm. Tasks caught in a cycle (which the services prevent) are appended last.</summary>
    private static List<Guid> TopologicalOrder(IReadOnlyCollection<ScheduleTask> tasks, IReadOnlyList<ScheduleLink> edges)
    {
        var inDegree = tasks.ToDictionary(t => t.Id, _ => 0);
        foreach (var edge in edges)
        {
            inDegree[edge.SuccessorId]++;
        }

        var outgoing = edges.ToLookup(e => e.PredecessorId, e => e.SuccessorId);
        var ready = new Queue<Guid>(tasks.Where(t => inDegree[t.Id] == 0).OrderBy(t => t.Start).Select(t => t.Id));
        var order = new List<Guid>(tasks.Count);
        while (ready.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var next in outgoing[id])
            {
                if (--inDegree[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        if (order.Count < tasks.Count)
        {
            var seen = order.ToHashSet();
            order.AddRange(tasks.Where(t => !seen.Contains(t.Id)).Select(t => t.Id));
        }

        return order;
    }
}
