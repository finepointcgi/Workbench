using Workbench.Domain.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>Pay period calendars per frequency.</summary>
public static class PayCalendar
{
    public static int PeriodsPerYear(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Weekly => 52,
        PayFrequency.Biweekly => 26,
        PayFrequency.SemiMonthly => 24,
        _ => 12,
    };

    /// <summary>
    /// The periods ending in <paramref name="year"/>: weekly and biweekly step from <paramref name="anchor"/> (the first day
    /// of any period); semi-monthly is the 1st–15th and 16th–end of each month; monthly is each calendar month.
    /// Check dates are the period end plus <paramref name="checkOffsetDays"/>.
    /// </summary>
    public static IReadOnlyList<(DateOnly Start, DateOnly End, DateOnly CheckDate)> Generate(PayFrequency frequency, DateOnly anchor, int checkOffsetDays, int year)
    {
        var periods = new List<(DateOnly, DateOnly)>();
        switch (frequency)
        {
            case PayFrequency.Weekly or PayFrequency.Biweekly:
                var step = frequency == PayFrequency.Weekly ? 7 : 14;
                var jan1 = new DateOnly(year, 1, 1);

                // The first period whose end is on or after January 1.
                var offset = ((jan1.DayNumber - anchor.DayNumber) % step + step) % step;
                var start = jan1.AddDays(-offset);
                if (start.AddDays(step - 1) < jan1)
                {
                    start = start.AddDays(step);
                }

                for (; start.AddDays(step - 1).Year == year; start = start.AddDays(step))
                {
                    periods.Add((start, start.AddDays(step - 1)));
                }

                break;
            case PayFrequency.SemiMonthly:
                for (var m = 1; m <= 12; m++)
                {
                    periods.Add((new DateOnly(year, m, 1), new DateOnly(year, m, 15)));
                    periods.Add((new DateOnly(year, m, 16), new DateOnly(year, m, DateTime.DaysInMonth(year, m))));
                }

                break;
            default:
                for (var m = 1; m <= 12; m++)
                {
                    periods.Add((new DateOnly(year, m, 1), new DateOnly(year, m, DateTime.DaysInMonth(year, m))));
                }

                break;
        }

        return periods.Select(p => (p.Item1, p.Item2, p.Item2.AddDays(checkOffsetDays))).ToList();
    }
}

/// <summary>One approved time entry, for splitting hours.</summary>
public sealed record ApprovedHours(DateOnly Date, string WbsCode, bool IsLeave, decimal Hours);

public sealed record HoursSplit(decimal Regular, decimal Overtime, IReadOnlyDictionary<string, decimal> Leave)
{
    public decimal Worked => Regular + Overtime;

    public decimal LeaveTotal => Leave.Values.Sum();
}

public static class PayrollHours
{
    public const decimal OvertimeThreshold = 40m;

    /// <summary>Monday of the FLSA workweek (Monday–Sunday, the same week as weekly timesheets).</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// Splits approved hours dated in [start, end] into regular, overtime and leave. Overtime applies to non-exempt
    /// employees only: worked hours (leave excluded, as under the FLSA) beyond 40 in a Monday–Sunday workweek, counted in
    /// date order. <paramref name="entries"/> should cover the whole workweeks around the period, so a week split by the
    /// period boundary still counts its earlier days towards the 40 hours.
    /// </summary>
    public static HoursSplit Split(bool overtimeEligible, DateOnly start, DateOnly end, IEnumerable<ApprovedHours> entries)
    {
        var list = entries.ToList();
        decimal regular = 0, overtime = 0;
        var leave = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var e in list.Where(e => e.IsLeave && e.Date >= start && e.Date <= end))
        {
            leave[e.WbsCode] = leave.GetValueOrDefault(e.WbsCode) + e.Hours;
        }

        foreach (var week in list.Where(e => !e.IsLeave).GroupBy(e => WeekStart(e.Date)))
        {
            var cumulative = 0m;
            foreach (var day in week.GroupBy(e => e.Date).OrderBy(g => g.Key))
            {
                var hours = day.Sum(e => e.Hours);
                var reg = overtimeEligible ? Math.Max(0, Math.Min(hours, OvertimeThreshold - cumulative)) : hours;
                cumulative += hours;
                if (day.Key >= start && day.Key <= end)
                {
                    regular += reg;
                    overtime += hours - reg;
                }
            }
        }

        return new HoursSplit(regular, overtime, leave.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value));
    }
}

/// <summary>
/// The pay side of one employee for reconciliation. <see cref="Share"/> is the part of the pay period this rate covers
/// (working days in its part over the period's working days) when the rate changed within the period.
/// </summary>
public sealed record PayBasis(PayType PayType, decimal? AnnualSalary, decimal? HourlyRate, int PeriodsPerYear, decimal Share = 1m);

public sealed record ReconciliationLine(
    decimal PayRate,
    decimal WorkedAtPayRate,
    decimal LeavePay,
    decimal OvertimePremium,
    decimal SalariedVariance,
    decimal ExpectedGross,
    decimal Unexplained);

public static class PayrollReconciliation
{
    public const decimal OvertimePremiumFactor = 0.5m;

    /// <summary>
    /// Explains the register's gross against labor at pay rates:
    /// worked hours (regular + overtime) × pay rate, plus leave hours × pay rate, plus the overtime premium
    /// (half the rate on overtime hours), plus — for salaried employees — the period salary less all those hours at the
    /// salary's hourly equivalent (salary / 2,080). What's left is unexplained.
    /// </summary>
    public static ReconciliationLine Explain(PayBasis pay, HoursSplit hours, decimal registerGross)
    {
        var rate = pay.PayType == PayType.Salaried
            ? pay.AnnualSalary is { } s ? Math.Round(s / 2080m, 4, MidpointRounding.AwayFromZero) : 0
            : pay.HourlyRate ?? 0;
        var worked = Round(hours.Worked * rate);
        var leave = Round(hours.LeaveTotal * rate);
        var premium = Round(hours.Overtime * rate * OvertimePremiumFactor);
        var salaried = pay.PayType == PayType.Salaried && pay.AnnualSalary is { } salary
            ? Round(salary / pay.PeriodsPerYear * pay.Share) - worked - leave
            : 0;
        var expected = worked + leave + premium + salaried;
        return new ReconciliationLine(rate, worked, leave, premium, salaried, expected, registerGross - expected);
    }

    /// <summary>
    /// As <see cref="Explain(PayBasis, HoursSplit, decimal)"/> for a period whose pay rate changed: each part is explained at
    /// the rate in effect for it (hours by work date, a salary by its share of the working days) and the parts are added.
    /// <see cref="ReconciliationLine.PayRate"/> is the last part's rate.
    /// </summary>
    public static ReconciliationLine Explain(IReadOnlyList<(PayBasis Pay, HoursSplit Hours)> parts, decimal registerGross)
    {
        var lines = parts.Select(p => Explain(p.Pay, p.Hours, 0)).ToList();
        var expected = lines.Sum(l => l.ExpectedGross);
        return new ReconciliationLine(lines[^1].PayRate, lines.Sum(l => l.WorkedAtPayRate), lines.Sum(l => l.LeavePay), lines.Sum(l => l.OvertimePremium),
            lines.Sum(l => l.SalariedVariance), expected, registerGross - expected);
    }

    /// <summary>Monday–Friday days in [from, to].</summary>
    public static int WorkingDays(DateOnly from, DateOnly to)
    {
        var days = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            days += d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 1;
        }

        return days;
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
