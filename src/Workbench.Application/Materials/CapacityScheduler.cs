using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>
/// Finite (or infinite) capacity scheduling of work order operations, without the database. Pinned operations are placed
/// first on their dates at the work center's full daily rate (a planner override may overload). Forward: work centers load
/// their queues in order (queue position, then due date), each operation as early as its work order's start, its previous
/// operation and the remaining capacity allow. Backward: each work order (in queue order) is placed back from its due date
/// as late as capacity allows; one that can't fit before today is scheduled forward from today instead (and is late).
/// </summary>
internal sealed class CapacityScheduler
{
    public sealed class Op
    {
        public required Guid Id { get; init; }
        public required Guid WorkOrderId { get; init; }
        public required string WorkOrderNumber { get; init; }
        public required int Sequence { get; init; }
        public required Guid WorkCenterId { get; init; }
        public required decimal Hours { get; init; }
        public required DateOnly EarliestStart { get; init; }
        public required DateOnly DueDate { get; init; }
        public int QueuePosition { get; init; }
        public DateOnly? PinnedStart { get; init; }

        public DateOnly? Start { get; set; }
        public DateOnly? End { get; set; }
        public string? Problem { get; set; }
        public Dictionary<DateOnly, decimal> Load { get; } = [];

        public bool IsPlaced { get; set; }

        public bool IsLate => End is { } e && e > DueDate;
    }

    private readonly Func<Guid, DateOnly, decimal> _capacity;
    private readonly Dictionary<(Guid, DateOnly), decimal> _used = [];
    private readonly bool _finite;
    private readonly DateOnly _asOf;
    private readonly DateOnly _horizon;

    public CapacityScheduler(Func<Guid, DateOnly, decimal> capacity, bool finite, DateOnly asOf, int horizonDays = 365)
    {
        _capacity = capacity;
        _finite = finite;
        _asOf = asOf;
        _horizon = asOf.AddDays(horizonDays);
    }

    public List<string> Messages { get; } = [];

    public void Schedule(IReadOnlyList<Op> ops, ScheduleDirection direction)
    {
        var byWo = ops.GroupBy(o => o.WorkOrderId).ToDictionary(g => g.Key, g => g.OrderBy(o => o.Sequence).ToList());
        foreach (var op in ops.Where(o => o.PinnedStart is not null).OrderBy(o => o.PinnedStart).ThenBy(o => o.WorkOrderNumber, StringComparer.Ordinal).ThenBy(o => o.Sequence))
        {
            PlacePinned(op);
        }

        if (direction == ScheduleDirection.Backward)
        {
            var forward = new List<Op>();
            foreach (var chain in byWo.Values.OrderBy(c => c.Min(o => o.QueuePosition)).ThenBy(c => c[0].DueDate).ThenBy(c => c[0].WorkOrderNumber, StringComparer.Ordinal))
            {
                if (chain.Any(o => o.PinnedStart is not null) || !TryBackward(chain))
                {
                    forward.AddRange(chain.Where(o => !o.IsPlaced));
                }
            }

            Forward(forward, byWo);
        }
        else
        {
            Forward(ops.Where(o => !o.IsPlaced).ToList(), byWo);
        }
    }

    private decimal Remaining(Guid center, DateOnly day)
    {
        var cap = _capacity(center, day);
        return _finite ? Math.Max(0, cap - _used.GetValueOrDefault((center, day))) : cap;
    }

    private void Use(Op op, DateOnly day, decimal hours)
    {
        op.Load[day] = op.Load.GetValueOrDefault(day) + hours;
        _used[(op.WorkCenterId, day)] = _used.GetValueOrDefault((op.WorkCenterId, day)) + hours;
    }

    private void Release(Op op)
    {
        foreach (var (day, hours) in op.Load)
        {
            _used[(op.WorkCenterId, day)] -= hours;
        }

        op.Load.Clear();
        op.Start = op.End = null;
        op.IsPlaced = false;
    }

    private void Finish(Op op)
    {
        op.IsPlaced = true;
        if (op.Load.Count > 0)
        {
            op.Start = op.Load.Keys.Min();
            op.End = op.Load.Keys.Max();
        }
    }

    /// <summary>A planner's date: from the pinned day at the center's full daily rate, whatever else is loaded.</summary>
    private void PlacePinned(Op op)
    {
        var left = op.Hours;
        var day = op.PinnedStart!.Value;
        while (left > 0 && day < _horizon.AddDays(365))
        {
            var take = Math.Min(left, _capacity(op.WorkCenterId, day));
            if (take > 0)
            {
                Use(op, day, take);
                left -= take;
            }

            day = day.AddDays(1);
        }

        if (left > 0)
        {
            op.Problem = "The work center has no capacity on or after the pinned date.";
        }

        op.Start ??= op.PinnedStart;
        Finish(op);
        op.Start = op.PinnedStart;
        op.End ??= op.PinnedStart;
    }

    private void Forward(List<Op> pending, IReadOnlyDictionary<Guid, List<Op>> byWo)
    {
        var queues = pending.GroupBy(o => o.WorkCenterId)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.QueuePosition).ThenBy(o => o.DueDate).ThenBy(o => o.WorkOrderNumber, StringComparer.Ordinal).ThenBy(o => o.Sequence).ToList());
        var left = pending.ToHashSet();
        while (left.Count > 0)
        {
            var heads = queues.Values.Select(q => q.FirstOrDefault(o => left.Contains(o))).OfType<Op>().Where(o => Ready(o, byWo)).ToList();
            if (heads.Count == 0)
            {
                // Queue orders that contradict routings: take any operation whose previous one is placed.
                heads = left.Where(o => Ready(o, byWo)).OrderBy(o => o.QueuePosition).ThenBy(o => o.DueDate).Take(1).ToList();
            }

            var next = heads.OrderBy(o => EarliestStart(o, byWo)).ThenBy(o => o.QueuePosition).ThenBy(o => o.DueDate).First();
            PlaceForward(next, EarliestStart(next, byWo));
            left.Remove(next);
        }
    }

    private static Op? Previous(Op op, IReadOnlyDictionary<Guid, List<Op>> byWo)
    {
        var chain = byWo[op.WorkOrderId];
        var i = chain.IndexOf(op);
        return i > 0 ? chain[i - 1] : null;
    }

    private static bool Ready(Op op, IReadOnlyDictionary<Guid, List<Op>> byWo) => Previous(op, byWo) is not { IsPlaced: false };

    private DateOnly EarliestStart(Op op, IReadOnlyDictionary<Guid, List<Op>> byWo)
    {
        var start = op.EarliestStart < _asOf ? _asOf : op.EarliestStart;
        return Previous(op, byWo) is { End: { } end } && end > start ? end : start;
    }

    private void PlaceForward(Op op, DateOnly from)
    {
        var left = op.Hours;
        var day = from;
        while (left > 0 && day < _horizon)
        {
            var take = Math.Min(left, Remaining(op.WorkCenterId, day));
            if (take > 0)
            {
                Use(op, day, take);
                left -= take;
            }

            day = day.AddDays(1);
        }

        if (left > 0)
        {
            op.Problem = $"Not enough capacity in the next {_horizon.DayNumber - _asOf.DayNumber} days ({left:0.##} h left over).";
            Messages.Add($"{op.WorkOrderNumber} operation {op.Sequence}: {op.Problem}");
        }

        Finish(op);
        op.Start ??= from;
        op.End ??= from;
    }

    /// <summary>Places a work order's operations back from its due date; false (and nothing placed) when it can't fit after today.</summary>
    private bool TryBackward(List<Op> chain)
    {
        var latest = chain[^1].DueDate;
        foreach (var op in Enumerable.Reverse(chain))
        {
            var left = op.Hours;
            var day = latest;
            while (left > 0 && day >= _asOf)
            {
                var take = Math.Min(left, Remaining(op.WorkCenterId, day));
                if (take > 0)
                {
                    Use(op, day, take);
                    left -= take;
                }

                if (left > 0)
                {
                    day = day.AddDays(-1);
                }
            }

            if (left > 0 || latest < _asOf)
            {
                foreach (var placed in chain)
                {
                    Release(placed);
                }

                return false;
            }

            Finish(op);
            op.Start ??= latest;
            op.End ??= latest;
            latest = op.Start!.Value;
        }

        return true;
    }
}
