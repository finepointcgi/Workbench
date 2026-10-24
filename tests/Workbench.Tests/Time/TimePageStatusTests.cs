using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;
using Workbench.Tests.Mobile;
using Workbench.Ui.Time;
using static Workbench.Tests.Mobile.MobileTestData;

namespace Workbench.Tests.Time;

/// <summary>
/// The e2e walkthrough (docs/ux/e2e-walkthrough.md) found the submitted banner reading "Submitted, waiting on _waitingOn":
/// the page passed the field's name as a literal string instead of its value.
/// </summary>
public sealed class TimePageStatusTests
{
    [Fact]
    public async Task A_submitted_timesheet_says_who_it_is_waiting_on()
    {
        await using var ctx = Context();
        var detail = Detail(Sheet(TimesheetState.Submitted, firstSubmitted: new DateTime(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc)), Entry(Direct, "1001.01", Monday, 8m));
        var (timesheets, tf) = Fake<ITimesheetService>.Create();
        tf.Returns(nameof(ITimesheetService.GetOrCreateMyTimesheetAsync), detail);
        var (leave, lf) = Fake<ILeaveTimesheetService>.Create();
        lf.On(nameof(ILeaveTimesheetService.GetForTimesheetAsync), a => NoLeave((Guid)a[0]!));
        var (inbox, inf) = Fake<IApprovalInboxService>.Create();
        var task = new ApprovalTaskDto(Guid.NewGuid(), 1, "Supervisor", null, ApprovalTaskState.Pending, ["pm@example.com"], null, null, null, null, null, null);
        inf.Returns(nameof(IApprovalInboxService.GetHistoryAsync), (IReadOnlyList<ApprovalInstanceDto>)
        [
            new ApprovalInstanceDto(Guid.NewGuid(), ApprovalDocumentTypes.Timesheet, detail.Timesheet.Id, "Timesheet", ApprovalInstanceState.Pending, 1,
                detail.Timesheet.EmployeeEmail, DateTime.UtcNow, null, [task], []),
        ]);
        ctx.Services.AddSingleton(timesheets);
        ctx.Services.AddSingleton(leave);
        ctx.Services.AddSingleton(inbox);
        Providers(ctx);

        var page = ctx.Render<TimePage>();

        page.WaitForAssertion(() => Assert.Contains("Submitted, waiting on pm@example.com", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("_waitingOn", page.Markup, StringComparison.Ordinal);
    }
}
