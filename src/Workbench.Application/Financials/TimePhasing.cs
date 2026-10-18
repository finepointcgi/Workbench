using Workbench.Contracts.Financials;

namespace Workbench.Application.Financials;

public sealed record PeriodInfo(Guid Id, string Name, DateOnly Start, DateOnly End);

public sealed record TimePhasedResult(IReadOnlyList<TimePhasedPointDto> Periods, decimal UnphasedBudgetAmount, IReadOnlyList<string> Notes);

/// <summary>Actual cost per fiscal period against the phased budget, with cumulative series.</summary>
public static class TimePhasing
{
    /// <summary>
    /// The series covers the fiscal periods from the earliest of the project start, the first phased budget and the
    /// first actual, through the latest of the as-of date, the last phased budget and the last actual (not the project
    /// finish, so a long period of performance doesn't stretch the chart).
    /// </summary>
    public static TimePhasedResult Build(
        IReadOnlyList<PeriodInfo> calendar,
        DateOnly? projectStart,
        IReadOnlyList<BudgetAmount> budget,
        IReadOnlyList<CostedLabor> labor,
        IReadOnlyList<OdcActual> odcs,
        DateOnly asOf)
    {
        var dates = new List<DateOnly> { asOf };
        dates.AddRange(budget.Where(b => b.PeriodStart is not null).Select(b => b.PeriodStart!.Value));
        dates.AddRange(labor.Select(l => l.WorkDate));
        dates.AddRange(odcs.Select(o => o.IncurredOn));
        if (projectStart is { } s && s <= asOf)
        {
            dates.Add(s);
        }

        var from = dates.Min();
        var to = dates.Max();
        var periods = calendar.Where(p => p.End >= from && p.Start <= to).OrderBy(p => p.Start).ToList();
        PeriodInfo? Find(DateOnly d) => periods.FirstOrDefault(p => p.Start <= d && d <= p.End);

        var budgetBy = new Dictionary<Guid, decimal>();
        var hoursBy = new Dictionary<Guid, decimal>();
        var laborBy = new Dictionary<Guid, decimal>();
        var odcBy = new Dictionary<Guid, decimal>();
        var outside = 0;
        static void Add(Dictionary<Guid, decimal> map, Guid key, decimal value) => map[key] = map.GetValueOrDefault(key) + value;

        var unphased = 0m;
        foreach (var line in budget)
        {
            if (line.PeriodStart is not { } start)
            {
                unphased += line.Amount;
            }
            else if (Find(start) is { } p)
            {
                Add(budgetBy, p.Id, line.Amount);
            }
            else
            {
                outside++;
            }
        }

        foreach (var entry in labor)
        {
            if (Find(entry.WorkDate) is { } p)
            {
                Add(laborBy, p.Id, entry.Cost);
                Add(hoursBy, p.Id, entry.Hours);
            }
            else
            {
                outside++;
            }
        }

        foreach (var odc in odcs)
        {
            if (Find(odc.IncurredOn) is { } p)
            {
                Add(odcBy, p.Id, odc.Amount);
            }
            else
            {
                outside++;
            }
        }

        var points = new List<TimePhasedPointDto>();
        decimal cumBudget = 0, cumActual = 0;
        foreach (var p in periods)
        {
            var laborCost = laborBy.GetValueOrDefault(p.Id);
            var odcCost = odcBy.GetValueOrDefault(p.Id);
            var future = p.Start > asOf;
            cumBudget += budgetBy.GetValueOrDefault(p.Id);
            cumActual += laborCost + odcCost;
            points.Add(new TimePhasedPointDto(p.Id, p.Name, p.Start, p.End, budgetBy.GetValueOrDefault(p.Id), hoursBy.GetValueOrDefault(p.Id),
                laborCost, odcCost, laborCost + odcCost, cumBudget, future ? null : cumActual, future));
        }

        var notes = new List<string>();
        if (outside > 0)
        {
            notes.Add($"{outside} budget line(s) or actual(s) fall outside the fiscal calendar and aren't in the series.");
        }

        return new TimePhasedResult(points, unphased, notes);
    }
}
