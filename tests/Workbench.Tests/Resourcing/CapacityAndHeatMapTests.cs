using Workbench.Application.Resourcing;
using Workbench.Contracts.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

public sealed class CapacityAndHeatMapTests
{
    [Fact]
    public void Capacity_subtracts_holidays_leave_and_the_non_billable_allowance()
    {
        var person = new CapacityPerson(Guid.NewGuid(), 40m, new DateOnly(2020, 1, 1), null);
        var week = new DateOnly(2026, 11, 23); // Thanksgiving week
        HolidayDay[] holidays = [new(new DateOnly(2026, 11, 26), "Thanksgiving"), new(new DateOnly(2026, 11, 27), "Day after", Hours: 4)];
        ApprovedLeaveDay[] leave = [new(person.EmployeeId, new DateOnly(2026, 11, 24), 8), new(person.EmployeeId, new DateOnly(2026, 11, 27), 8), new(Guid.NewGuid(), week, 8)];

        var result = Assert.Single(CapacityCalculator.Compute(person, [week], holidays, leave, 10m));

        // 40 standard − 12 holiday (8 + 4) − 12 leave (8, and only the 4 h left on the half holiday) = 16; 10% allowance = 1.6.
        Assert.Equal((40m, 12m, 12m, 1.6m, 14.4m), (result.StandardHours, result.HolidayHours, result.LeaveHours, result.NonBillableHours, result.Capacity));
    }

    [Fact]
    public void Capacity_follows_hire_and_termination_dates_and_part_time_hours()
    {
        var person = new CapacityPerson(Guid.NewGuid(), 20m, new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 22));
        var weeks = CapacityCalculator.Compute(person, [new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 26)], [], [], 0m);

        Assert.Equal([12m, 16m, 0m], weeks.Select(w => w.Capacity)); // Wed–Fri, Mon–Thu, gone
    }

    [Theory]
    [InlineData(36, 0, LoadStatus.Under)]
    [InlineData(36, 32, LoadStatus.Under)]
    [InlineData(36, 32.4, LoadStatus.Full)]
    [InlineData(36, 36, LoadStatus.Full)]
    [InlineData(36, 36.5, LoadStatus.Over)]
    [InlineData(0, 0, LoadStatus.None)]
    [InlineData(0, 4, LoadStatus.Over)]
    public void Heat_map_statuses(decimal capacity, decimal allocated, LoadStatus expected) =>
        Assert.Equal(expected, CapacityCalculator.StatusOf(capacity, allocated, 90m));

    [Fact]
    public async Task Heat_map_and_capacity_use_the_leave_calendar_and_all_projects()
    {
        await using var host = await CreateAsync();
        host.Leave.Holidays.Add(new HolidayDay(new DateOnly(2026, 10, 12), "Columbus Day"));
        host.Leave.Leave.Add(new ApprovedLeaveDay(host.AnalystId, new DateOnly(2026, 10, 20), 8));
        var allocations = host.As(Admin).Get<IResourceAllocationService>();
        await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 30, Weeks: 2));
        await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, ThisWeek.AddDays(7), 10));
        await allocations.CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project1, ThisWeek, Percent: 75, Weeks: 2));

        var map = await host.As(Pm).Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(Today, 2));

        Assert.Equal([ThisWeek, ThisWeek.AddDays(7)], map.Weeks);
        var dev = map.Rows.Single(r => r.EmployeeId == host.DevId);
        // Week 1: 40 − 8 holiday = 32, less 10% = 28.8 capacity; 30 h → over. Week 2: 36 capacity; 30 + 10 = 40 → over.
        Assert.Equal([(28.8m, 30m, LoadStatus.Over), (36m, 40m, LoadStatus.Over)], dev.Weeks.Select(w => (w.Capacity, w.Allocated, w.Status)));
        var analyst = map.Rows.Single(r => r.EmployeeId == host.AnalystId);
        // 75% of 40 = 30 h. Week 1: 30 > 28.8 over; week 2: leave day → 32 − 3.2 = 28.8 → over again.
        Assert.Equal([LoadStatus.Over, LoadStatus.Over], analyst.Weeks.Select(w => w.Status));
        Assert.Equal(8m, analyst.Weeks[1].LeaveHours);
        var pm = map.Rows.Single(r => r.EmployeeId == host.PmId);
        Assert.Equal(LoadStatus.Under, pm.Weeks[0].Status);

        var onProject2 = await host.As(ResourceManager).Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(Today, 2, ProjectId: host.Project2));
        Assert.Equal(host.DevId, Assert.Single(onProject2.Rows).EmployeeId);
    }

    [Fact]
    public async Task Find_people_by_labor_category_and_free_hours()
    {
        await using var host = await CreateAsync();
        await host.As(Admin).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 30, Weeks: 2));

        var people = await host.As(Pm).Get<IResourcePlanningService>().FindAvailablePeopleAsync(new FindPeopleQuery(ThisWeek, ThisWeek.AddDays(13), host.Se2, 10));

        // SE2: pm@ (72 free), pm2@ (72), dev@ (2 × 6 = 12).
        Assert.Equal([72m, 72m, 12m], people.Select(p => p.AvailableHours));
        Assert.Equal(host.DevId, people[^1].EmployeeId);
        Assert.Empty(await host.Get<IResourcePlanningService>().FindAvailablePeopleAsync(new FindPeopleQuery(ThisWeek, ThisWeek.AddDays(13), host.Se2, 100)));
    }
}
