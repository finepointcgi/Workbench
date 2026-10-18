using Workbench.Application.Payroll;
using Workbench.Domain.Payroll;

namespace Workbench.Tests.Payroll;

/// <summary>The hours split, the pay calendars and the reconciliation arithmetic.</summary>
public sealed class PayrollMathTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static ApprovedHours Work(int day, decimal hours) => new(Monday.AddDays(day), "1001.01", false, hours);

    private static ApprovedHours Leave(int day, decimal hours, string code = "LV.PTO") => new(Monday.AddDays(day), code, true, hours);

    [Fact]
    public void Non_exempt_hours_over_40_worked_in_a_week_are_overtime()
    {
        var split = PayrollHours.Split(overtimeEligible: true, Monday, Monday.AddDays(13), [Work(0, 9), Work(1, 9), Work(2, 9), Work(3, 9), Work(4, 9), Leave(7, 8)]);

        Assert.Equal(40m, split.Regular);
        Assert.Equal(5m, split.Overtime);
        Assert.Equal(8m, split.Leave["LV.PTO"]);
    }

    [Fact]
    public void Exempt_employees_never_get_overtime()
    {
        var split = PayrollHours.Split(overtimeEligible: false, Monday, Monday.AddDays(6), [Work(0, 12), Work(1, 12), Work(2, 12), Work(3, 12)]);

        Assert.Equal(48m, split.Regular);
        Assert.Equal(0m, split.Overtime);
    }

    [Fact]
    public void Leave_hours_dont_count_toward_the_40()
    {
        var split = PayrollHours.Split(true, Monday, Monday.AddDays(6), [Work(0, 10), Work(1, 10), Work(2, 10), Work(3, 10), Leave(4, 8), Leave(5, 8, "LV.HOL")]);

        Assert.Equal(40m, split.Regular);
        Assert.Equal(0m, split.Overtime);
        Assert.Equal(16m, split.LeaveTotal);
        Assert.Equal(2, split.Leave.Count);
    }

    [Fact]
    public void A_workweek_split_by_the_period_start_counts_its_earlier_days()
    {
        // Mon–Tue (20 h) fall before the period; Wed–Fri (27 h) inside it: 47 h in the week, 7 h overtime, all inside the period.
        var split = PayrollHours.Split(true, Monday.AddDays(2), Monday.AddDays(15), [Work(0, 10), Work(1, 10), Work(2, 9), Work(3, 9), Work(4, 9)]);

        Assert.Equal(20m, split.Regular);
        Assert.Equal(7m, split.Overtime);
    }

    [Fact]
    public void Biweekly_periods_step_from_the_anchor_and_end_in_the_year()
    {
        var periods = PayCalendar.Generate(PayFrequency.Biweekly, new DateOnly(2026, 10, 5), 5, 2026);

        Assert.All(periods, p => Assert.Equal(13, p.End.DayNumber - p.Start.DayNumber));
        Assert.All(periods, p => Assert.Equal(2026, p.End.Year));
        Assert.All(periods, p => Assert.Equal(p.End.AddDays(5), p.CheckDate));
        Assert.Contains(periods, p => p.Start == new DateOnly(2026, 10, 5) && p.End == new DateOnly(2026, 10, 18));
        Assert.All(periods.Zip(periods.Skip(1)), pair => Assert.Equal(pair.First.End.AddDays(1), pair.Second.Start));
        Assert.True(periods[0].End >= new DateOnly(2026, 1, 1) && periods[0].Start < new DateOnly(2026, 1, 14));
        Assert.InRange(periods.Count, 26, 27);
    }

    [Fact]
    public void Weekly_semi_monthly_and_monthly_calendars()
    {
        Assert.InRange(PayCalendar.Generate(PayFrequency.Weekly, new DateOnly(2026, 10, 5), 3, 2027).Count, 52, 53);
        var semi = PayCalendar.Generate(PayFrequency.SemiMonthly, default, 0, 2027);
        Assert.Equal(24, semi.Count);
        Assert.Equal((new DateOnly(2027, 2, 16), new DateOnly(2027, 2, 28)), (semi[3].Start, semi[3].End));
        var monthly = PayCalendar.Generate(PayFrequency.Monthly, default, 0, 2028);
        Assert.Equal(12, monthly.Count);
        Assert.Equal(new DateOnly(2028, 2, 29), monthly[1].End);
    }

    [Fact]
    public void Hourly_gross_is_explained_by_worked_pay_leave_and_overtime_premium()
    {
        var hours = new HoursSplit(40, 5, new Dictionary<string, decimal> { ["LV.PTO"] = 8 });
        var line = PayrollReconciliation.Explain(new PayBasis(PayType.Hourly, null, 30m, 26), hours, 1_665m);

        Assert.Equal(1_350m, line.WorkedAtPayRate);
        Assert.Equal(240m, line.LeavePay);
        Assert.Equal(75m, line.OvertimePremium);
        Assert.Equal(0m, line.SalariedVariance);
        Assert.Equal(1_665m, line.ExpectedGross);
        Assert.Equal(0m, line.Unexplained);
    }

    [Fact]
    public void Salaried_gross_is_explained_by_the_salaried_variance()
    {
        // 104,000 / 2,080 = 50/h. 70 worked + 8 leave = 3,900 at that rate; the period salary is 4,000.
        var hours = new HoursSplit(70, 0, new Dictionary<string, decimal> { ["LV.HOL"] = 8 });
        var line = PayrollReconciliation.Explain(new PayBasis(PayType.Salaried, 104_000m, null, 26), hours, 4_010m);

        Assert.Equal(50m, line.PayRate);
        Assert.Equal(3_500m, line.WorkedAtPayRate);
        Assert.Equal(400m, line.LeavePay);
        Assert.Equal(100m, line.SalariedVariance);
        Assert.Equal(4_000m, line.ExpectedGross);
        Assert.Equal(10m, line.Unexplained);
    }
}
