using Workbench.Contracts.Crm;

namespace Workbench.Application.Crm;

/// <summary>Turns open pursuits into weekly staffing demand: value ÷ revenue per hour, spread evenly over the period of performance.</summary>
public static class PipelineDemandMath
{
    public const decimal DefaultRevenuePerHour = 150m;
    public const int DefaultMonths = 12;
    public const int DaysFromProposalToAward = 90;

    public sealed record Input(Guid Id, string Number, string Name, OpportunityStage Stage, decimal EstimatedValue, int Pwin, int Pgo,
        DateOnly? EstimatedAwardDate, DateOnly? ProposalDueOn, int? PeriodOfPerformanceMonths);

    public static PipelineDemandDto Compute(IEnumerable<Input> pursuits, DateOnly from, int weeks, decimal revenuePerHour)
    {
        var rate = revenuePerHour > 0 ? revenuePerHour : DefaultRevenuePerHour;
        weeks = Math.Clamp(weeks, 1, 104);
        var monday = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        var rows = new List<PipelineDemandOpportunityDto>();
        var notCounted = 0;
        foreach (var o in pursuits)
        {
            var estimated = o.EstimatedAwardDate is null;
            var start = o.EstimatedAwardDate ?? o.ProposalDueOn?.AddDays(DaysFromProposalToAward);
            if (o.EstimatedValue <= 0 || start is null)
            {
                notCounted++;
                continue;
            }

            var end = start.Value.AddMonths(o.PeriodOfPerformanceMonths is > 0 ? o.PeriodOfPerformanceMonths.Value : DefaultMonths);
            var spanWeeks = Math.Max(1, (int)Math.Ceiling((end.DayNumber - start.Value.DayNumber) / 7.0));
            var perWeek = Math.Round(o.EstimatedValue / rate / spanWeeks, 1);
            var chance = PipelineMath.Clamp(o.Pwin) / 100m * PipelineMath.Clamp(o.Pgo) / 100m;
            rows.Add(new PipelineDemandOpportunityDto(o.Id, o.Number, o.Name, o.Stage, PipelineMath.Clamp(o.Pwin), start.Value, end, perWeek, Math.Round(perWeek * chance, 1), estimated));
        }

        var weekRows = Enumerable.Range(0, weeks).Select(i =>
        {
            var weekStart = monday.AddDays(i * 7);
            var active = rows.Where(r => r.StartsOn <= weekStart.AddDays(6) && r.EndsOn > weekStart).ToList();
            return new PipelineDemandWeekDto(weekStart, active.Sum(r => r.WeightedHoursPerWeek), active.Sum(r => r.HoursPerWeek));
        }).ToList();
        return new PipelineDemandDto(weekRows, rows.OrderBy(r => r.StartsOn).ThenBy(r => r.Number).ToList(), rate, notCounted);
    }
}
