using Workbench.Application.Accounting;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Tests.Accounting;

/// <summary>Setup › Fiscal calendar's Close goes through the period-close checklist.</summary>
public sealed class FiscalCalendarCloseTests
{
    [Fact]
    public async Task A_period_with_an_unapproved_timesheet_cant_be_closed_from_setup()
    {
        var host = await LaborPostingTests.CreateAsync();
        var calendar = new ChecklistFiscalCalendarService(host.Time.Calendar, host.Gl.Close);
        var october = host.Time.Periods.Single(p => p.Name == "2026-10").Id;
        var id = await host.Time.SubmittedDanaWeekAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Closed, "Month end"));
        Assert.Contains("timesheet", ex.Message);
        Assert.Contains(await calendar.ListPeriodsAsync(), p => p.Id == october && p.Status == FiscalPeriodState.Open);

        // Approved and posted: the checklist passes and Setup can close it, then reopen it as before.
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(id);
        var closed = await calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Closed, "Month end");
        Assert.Equal(FiscalPeriodState.Closed, closed.Status);

        var reopened = await calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Open, "Adjustment");
        Assert.Equal(FiscalPeriodState.Open, reopened.Status);
    }
}
