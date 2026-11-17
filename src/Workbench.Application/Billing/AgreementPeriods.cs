using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// The billing periods of a retainer or recurring plan: whole cadence periods counted from the plan's start date (a plan
/// that starts on the 15th bills the 15th to the 14th). The last period is cut at the end date and prorated by days.
/// </summary>
public static class AgreementPeriods
{
    private const int MaxPeriods = 1200;

    public static int Months(BillingCadence cadence) => cadence switch
    {
        BillingCadence.Monthly => 1,
        BillingCadence.Quarterly => 3,
        BillingCadence.SemiAnnual => 6,
        _ => 12,
    };

    public static string Label(BillingCadence cadence) => cadence switch
    {
        BillingCadence.Monthly => "month",
        BillingCadence.Quarterly => "quarter",
        BillingCadence.SemiAnnual => "half year",
        _ => "year",
    };

    /// <summary>First day of period <paramref name="index"/> (0 is the first). Counted from the start date so a 31st doesn't drift.</summary>
    public static DateOnly Start(AgreementBillingPlan plan, int index) => plan.StartDate.AddMonths(Months(plan.Cadence) * index);

    /// <summary>The period's days: its natural end, or the plan's end date when that comes first.</summary>
    public static (DateOnly From, DateOnly To) Period(AgreementBillingPlan plan, int index)
    {
        var natural = Start(plan, index + 1).AddDays(-1);
        return (Start(plan, index), plan.EndDate is { } end && end < natural ? end : natural);
    }

    /// <summary>The periods that have started by <paramref name="asOf"/> (and by the end date), oldest first.</summary>
    public static IEnumerable<(int Index, DateOnly From, DateOnly To)> Started(AgreementBillingPlan plan, DateOnly asOf)
    {
        for (var i = 0; i < MaxPeriods; i++)
        {
            var (from, to) = Period(plan, i);
            if (from > asOf || (plan.EndDate is { } end && from > end))
            {
                yield break;
            }

            yield return (i, from, to);
        }
    }

    /// <summary>The period that begins on <paramref name="from"/>, or null when no period does.</summary>
    public static (int Index, DateOnly From, DateOnly To)? Find(AgreementBillingPlan plan, DateOnly from)
    {
        for (var i = 0; i < MaxPeriods; i++)
        {
            var start = Start(plan, i);
            if (start > from || (plan.EndDate is { } end && start > end))
            {
                return null;
            }

            if (start == from)
            {
                var (f, t) = Period(plan, i);
                return (i, f, t);
            }
        }

        return null;
    }

    /// <summary>The plan's amount for the period: in full, or by days for a last period the end date cut short.</summary>
    public static decimal AmountFor(AgreementBillingPlan plan, int index)
    {
        var (from, to) = Period(plan, index);
        var natural = Start(plan, index + 1).AddDays(-1);
        if (to >= natural)
        {
            return plan.Amount;
        }

        var days = to.DayNumber - from.DayNumber + 1;
        var naturalDays = natural.DayNumber - from.DayNumber + 1;
        return Money.Round(plan.Amount * days / naturalDays);
    }

    public static string Describe(DateOnly from, DateOnly to) =>
        from.Year == to.Year && from.Month == to.Month && from.Day == 1 && to.Day == DateTime.DaysInMonth(to.Year, to.Month)
            ? from.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";

    /// <summary>
    /// Retainer hours used and available for the period at <paramref name="index"/>: approved time on the contract's projects
    /// against the included hours, plus (for rollover) the unused hours of earlier periods.
    /// </summary>
    internal static async Task<RetainerHours> HoursAsync(IWorkbenchDbContext db, BillingContext ctx, AgreementBillingPlan plan, int index, CancellationToken ct)
    {
        var included = plan.IncludedHours ?? 0m;
        var wbsIds = ctx.Wbs.Keys.ToList();
        var (from, to) = Period(plan, index);
        var first = plan.UnusedHours == UnusedHoursPolicy.Rollover ? plan.StartDate : from;
        var entries = wbsIds.Count == 0
            ? []
            : await (
                    from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                    where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId) && e.WorkDate >= first && e.WorkDate <= to && e.Hours != 0
                    select new { e.WorkDate, e.Hours })
                .ToListAsync(ct);

        var carried = 0m;
        if (plan.UnusedHours == UnusedHoursPolicy.Rollover)
        {
            for (var i = 0; i < index; i++)
            {
                var (f, t) = Period(plan, i);
                var used = entries.Where(e => e.WorkDate >= f && e.WorkDate <= t).Sum(e => e.Hours);
                carried = Math.Max(0m, carried + included - used);
            }
        }

        return new RetainerHours(included, carried, entries.Where(e => e.WorkDate >= from && e.WorkDate <= to).Sum(e => e.Hours));
    }
}

/// <summary>A retainer period's hours: included, carried in from earlier periods, and used.</summary>
internal readonly record struct RetainerHours(decimal Included, decimal Carried, decimal Used)
{
    public decimal Available => Included + Carried;

    public decimal Unused => Math.Max(0m, Available - Used);

    public decimal Over => Math.Max(0m, Used - Available);
}
