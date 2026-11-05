using Workbench.ApiClient;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>A day off on the real host and demo seed: Alex asks, Pat (his supervisor) approves, and the balance and the team calendar agree.</summary>
public sealed class LeaveTests
{
    [Fact]
    public async Task A_day_off_asked_for_and_approved_shows_in_the_balance_and_the_calendar()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var employee = web.CreateClientAs("analyst@example.com");
        var today = DateOnly.FromDateTime(DateTime.Now);

        // A weekday about three weeks ahead, which isn't already requested.
        var day = today.AddDays(21);
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }

        var balances = new HttpLeaveBalanceService(employee);
        var pto = (await balances.GetMyBalancesAsync()).First(b => b.Type == LeaveTypeKind.Pto);
        var requests = new HttpLeaveRequestService(employee);
        // Holidays are refused too (three weeks ahead can be Thanksgiving), so move on to the next working day.
        LeaveRequestDto? asked = null;
        for (var tries = 0; asked is null && tries < 14; tries++)
        {
            try
            {
                asked = await requests.CreateAsync(new CreateLeaveRequest(pto.PolicyId, day, day, null, "Long weekend"));
            }
            catch (ArgumentException)
            {
                day = day.AddDays(1);
                while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    day = day.AddDays(1);
                }
            }
        }

        Assert.NotNull(asked);
        Assert.Equal((LeaveRequestState.Pending, 8m), (asked.Status, asked.Hours));

        // Pat has it waiting and approves.
        var approver = new HttpLeaveRequestService(finance);
        Assert.Contains(await approver.ListPendingApprovalsAsync(), r => r.Id == asked.Id);
        var approved = await approver.ApproveAsync(asked.Id, new DecideLeaveRequest("Enjoy"));
        Assert.Equal(LeaveRequestState.Approved, approved.Status);
        Assert.Equal(pto.Planned + 8m, (await balances.GetMyBalancesAsync()).Single(b => b.PolicyId == pto.PolicyId).Planned); // planned once approved

        // The team calendar shows Alex out that day.
        var calendar = await approver.GetTeamCalendarAsync(day, day);
        Assert.Contains(calendar, e => e.Kind == TeamCalendarEntryKind.Leave && e.RequestId == asked.Id);
    }
}
