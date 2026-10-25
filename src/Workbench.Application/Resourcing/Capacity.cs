using Workbench.Contracts.Resourcing;

namespace Workbench.Application.Resourcing;

/// <summary>Resource planning settings (config section <c>Resourcing</c>).</summary>
public sealed class ResourcingOptions
{
    public const string Section = "Resourcing";

    /// <summary>Share of available hours kept for non-billable work (meetings, training, admin). Default 10%.</summary>
    public decimal NonBillableAllowancePercent { get; set; } = 0m;

    /// <summary>A week is "full" from this share of capacity up to 100%. Default 90%.</summary>
    public decimal FullThresholdPercent { get; set; } = 90m;

    /// <summary>A staffing gap within this share of the remaining budget hours counts as balanced. Default 10%.</summary>
    public decimal StaffingTolerancePercent { get; set; } = 10m;
}

/// <summary>A company holiday. <see cref="EmployeeId"/> null applies to everyone; <see cref="Hours"/> null is a full day.</summary>
public sealed record HolidayDay(DateOnly Date, string Name, Guid? EmployeeId = null, decimal? Hours = null);

/// <summary>Approved leave on one day.</summary>
public sealed record ApprovedLeaveDay(Guid EmployeeId, DateOnly Date, decimal Hours);

/// <summary>
/// Where resource planning reads holidays and approved leave. The default (<see cref="NoLeaveCalendar"/>) has none; the
/// payroll/leave module registers its own implementation (<c>services.AddScoped&lt;IResourceLeaveCalendar, ...&gt;()</c>).
/// </summary>
public interface IResourceLeaveCalendar
{
    Task<IReadOnlyList<HolidayDay>> GetHolidaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApprovedLeaveDay>> GetApprovedLeaveAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default);
}

public sealed class NoLeaveCalendar : IResourceLeaveCalendar
{
    public Task<IReadOnlyList<HolidayDay>> GetHolidaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HolidayDay>>([]);

    public Task<IReadOnlyList<ApprovedLeaveDay>> GetApprovedLeaveAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApprovedLeaveDay>>([]);
}

/// <summary>The person facts capacity needs.</summary>
public sealed record CapacityPerson(Guid EmployeeId, decimal StandardHoursPerWeek, DateOnly HireDate, DateOnly? TerminationDate)
{
    public bool IsActiveOn(DateOnly date) => date >= HireDate && (TerminationDate is null || date <= TerminationDate);
}

/// <summary>Weekly capacity: standard hours (Mon–Fri, while employed) − holidays − approved leave − the non-billable allowance.</summary>
public static class CapacityCalculator
{
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static IReadOnlyList<DateOnly> Weeks(DateOnly from, DateOnly to)
    {
        var weeks = new List<DateOnly>();
        for (var w = WeekStart(from); w <= to; w = w.AddDays(7))
        {
            weeks.Add(w);
        }

        return weeks;
    }

    public static IReadOnlyList<CapacityWeekDto> Compute(
        CapacityPerson person,
        IReadOnlyList<DateOnly> weekStarts,
        IReadOnlyList<HolidayDay> holidays,
        IReadOnlyList<ApprovedLeaveDay> leave,
        decimal nonBillablePercent)
    {
        var daily = person.StandardHoursPerWeek / 5m;
        var holidayByDate = holidays.Where(h => h.EmployeeId is null || h.EmployeeId == person.EmployeeId)
            .GroupBy(h => h.Date).ToDictionary(g => g.Key, g => Math.Min(daily, g.Sum(h => h.Hours ?? daily)));
        var leaveByDate = leave.Where(l => l.EmployeeId == person.EmployeeId)
            .GroupBy(l => l.Date).ToDictionary(g => g.Key, g => g.Sum(l => l.Hours));
        var result = new List<CapacityWeekDto>();
        foreach (var week in weekStarts)
        {
            decimal standard = 0, holiday = 0, leaveHours = 0;
            for (var i = 0; i < 5; i++)
            {
                var day = week.AddDays(i);
                if (!person.IsActiveOn(day))
                {
                    continue;
                }

                standard += daily;
                var h = holidayByDate.GetValueOrDefault(day);
                holiday += h;
                leaveHours += Math.Min(Math.Max(0, daily - h), leaveByDate.GetValueOrDefault(day));
            }

            var available = standard - holiday - leaveHours;
            var nonBillable = Math.Round(available * nonBillablePercent / 100m, 2, MidpointRounding.AwayFromZero);
            result.Add(new CapacityWeekDto(person.EmployeeId, week, standard, holiday, leaveHours, nonBillable, available - nonBillable));
        }

        return result;
    }

    /// <summary>Available hours per working day (standard − holidays − leave, no allowance), for utilization.</summary>
    public static IReadOnlyDictionary<DateOnly, decimal> AvailableByDay(
        CapacityPerson person, DateOnly from, DateOnly to, IReadOnlyList<HolidayDay> holidays, IReadOnlyList<ApprovedLeaveDay> leave)
    {
        var daily = person.StandardHoursPerWeek / 5m;
        var holidayByDate = holidays.Where(h => h.EmployeeId is null || h.EmployeeId == person.EmployeeId)
            .GroupBy(h => h.Date).ToDictionary(g => g.Key, g => Math.Min(daily, g.Sum(h => h.Hours ?? daily)));
        var leaveByDate = leave.Where(l => l.EmployeeId == person.EmployeeId).GroupBy(l => l.Date).ToDictionary(g => g.Key, g => g.Sum(l => l.Hours));
        var result = new Dictionary<DateOnly, decimal>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || !person.IsActiveOn(day))
            {
                continue;
            }

            var h = holidayByDate.GetValueOrDefault(day);
            result[day] = daily - h - Math.Min(Math.Max(0, daily - h), leaveByDate.GetValueOrDefault(day));
        }

        return result;
    }

    /// <summary>
    /// Booked past what they can work in a week (the "booked past their hours" check everywhere: assigning, the staffing
    /// plan and the plan's people): more than capacity, by more than rounding.
    /// </summary>
    public static bool IsOverbooked(decimal booked, decimal capacity) => booked > capacity + 0.005m;

    /// <summary>Over above capacity, Full from <paramref name="fullThresholdPercent"/> of it, otherwise Under; None with neither.</summary>
    public static LoadStatus StatusOf(decimal capacity, decimal allocated, decimal fullThresholdPercent)
    {
        if (capacity <= 0)
        {
            return allocated > 0 ? LoadStatus.Over : LoadStatus.None;
        }

        if (IsOverbooked(allocated, capacity))
        {
            return LoadStatus.Over;
        }

        return allocated >= capacity * fullThresholdPercent / 100m ? LoadStatus.Full : LoadStatus.Under;
    }
}
