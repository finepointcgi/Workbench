using Workbench.Application.Financials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.TotalTime;

/// <summary>One time entry on a timesheet, in the order hours were worked (work date, then entry).</summary>
public sealed record TimeCostInput(Guid EntryId, DateOnly WorkDate, decimal Hours);

/// <summary>An entry's labor cost. <paramref name="BaseRate"/> is the hourly cost rate on the work date (null: no rate, cost 0).</summary>
public sealed record CostedTimeEntry(Guid EntryId, DateOnly WorkDate, decimal Hours, decimal Cost, decimal UncompensatedHours, decimal? BaseRate)
{
    public bool MissingRate => BaseRate is null;
}

/// <summary>One timesheet costed by one method.</summary>
public sealed record TimesheetCost(
    LaborCostMethod Method,
    bool IsExempt,
    decimal StandardHours,
    decimal WorkedHours,
    decimal UncompensatedHours,
    decimal? PeriodSalary,
    decimal LaborCost,
    bool EffectiveRateApplied,
    IReadOnlyList<CostedTimeEntry> Entries)
{
    /// <summary>Labor cost ÷ hours worked (to 4 places), or null without hours.</summary>
    public decimal? EffectiveRate => WorkedHours > 0 ? Money.RoundRate(LaborCost / WorkedHours) : null;

    public decimal MissingRateHours => Entries.Where(e => e.MissingRate).Sum(e => e.Hours);
}

/// <summary>
/// Total time accounting (DCAA): an exempt salaried employee records every hour, and hours beyond the standard for the
/// period are uncompensated. The method decides what those hours cost:
/// <list type="bullet">
/// <item><see cref="LaborCostMethod.Standard"/>: each entry is hours × the cost rate on its work date, rounded per entry
/// (the arithmetic Workbench has always used). For an exempt employee, hours past the standard (in the order worked)
/// are uncompensated and cost nothing.</item>
/// <item><see cref="LaborCostMethod.TotalTimeEffectiveRate"/>: for an exempt employee, the period's salary (standard
/// hours at the hourly rate) is spread over every hour worked, so each entry costs hours × rate × standard ÷ worked.</item>
/// <item><see cref="LaborCostMethod.Hybrid"/>: Standard unless worked hours exceed the standard, then the effective rate.</item>
/// </list>
/// Non-exempt employees are always costed the standard way (their overtime is paid). When the effective rate applies,
/// entries are rounded cumulatively so the period's entries add up to the salary to the cent.
/// </summary>
public static class TotalTimeCosting
{
    public static TimesheetCost Cost(
        LaborCostMethod method, bool isExempt, decimal standardHours, IReadOnlyList<TimeCostInput> entries, Func<DateOnly, decimal?> rateOn)
    {
        var ordered = entries.Where(e => e.Hours != 0).OrderBy(e => e.WorkDate).ToList(); // Stable: keeps the caller's order within a day.
        var worked = ordered.Sum(e => e.Hours);
        var rates = ordered.Select(e => rateOn(e.WorkDate)).ToList();
        var over = isExempt && standardHours > 0 && worked > standardHours;
        var uncompensated = over ? worked - standardHours : 0m;
        var spread = isExempt && standardHours > 0 && worked > 0
            && (method == LaborCostMethod.TotalTimeEffectiveRate || (method == LaborCostMethod.Hybrid && over));

        decimal? salary = null;
        if (isExempt && standardHours > 0)
        {
            // Standard hours at the hourly rate. With a rate change inside the period, the hours-weighted rate.
            var exact = ordered.Select((e, i) => e.Hours * (rates[i] ?? 0m)).Sum();
            salary = worked > 0 ? Money.Round(exact * standardHours / worked) : null;
        }

        var costed = new List<CostedTimeEntry>(ordered.Count);
        if (spread)
        {
            var factor = standardHours / worked;
            var unpaidShare = uncompensated / worked;
            decimal exactCost = 0, roundedCost = 0, exactUnpaid = 0, roundedUnpaid = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                exactCost += e.Hours * (rates[i] ?? 0m) * factor;
                var cost = Money.Round(exactCost) - roundedCost;
                roundedCost += cost;
                exactUnpaid += e.Hours * unpaidShare;
                var unpaid = Money.Round(exactUnpaid) - roundedUnpaid;
                roundedUnpaid += unpaid;
                costed.Add(new CostedTimeEntry(e.EntryId, e.WorkDate, e.Hours, cost, unpaid, rates[i]));
            }
        }
        else
        {
            var remaining = over ? standardHours : decimal.MaxValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                var paid = e.Hours;
                if (over && e.Hours > 0)
                {
                    paid = Math.Clamp(remaining, 0m, e.Hours);
                    remaining -= paid;
                }

                costed.Add(new CostedTimeEntry(e.EntryId, e.WorkDate, e.Hours, Money.Round(paid * (rates[i] ?? 0m)), e.Hours - paid, rates[i]));
            }
        }

        return new TimesheetCost(method, isExempt, standardHours, worked, uncompensated, salary, costed.Sum(c => c.Cost), spread, costed);
    }
}
