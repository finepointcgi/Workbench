using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Notifications;
using Workbench.Application.Odcs;
using Workbench.Application.Time;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.ProjectAccountingFixture;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// Timesheet approval from roles (no more <c>Timekeeping:Admins</c>), and the timesheet / ODC notifications going out
/// through <see cref="INotificationPublisher"/>, over the real registrations.
/// </summary>
public sealed class TimekeepingNotificationTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static async Task<Guid> SubmitTodayAsync(SeededHost seeded, string email)
    {
        var timesheets = seeded.As(email).Host.Get<ITimesheetService>();
        var sheet = await timesheets.GetOrCreateMyTimesheetAsync(Day.AddDays(-7)); // the week before the fixture's approved one
        await timesheets.SubmitAsync(sheet.Timesheet.Id, new SubmitTimesheetRequest(true));
        return sheet.Timesheet.Id;
    }

    private static async Task<List<Notification>> NotificationsAsync(SeededHost seeded, string email, string kind)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        return await db.Set<Notification>().Where(n => n.RecipientEmail == email && n.Kind == kind).ToListAsync();
    }

    [Fact]
    public async Task Timekeeper_and_finance_view_every_timesheet_but_only_supervisors_and_admins_approve()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var analystSheet = await SubmitTodayAsync(seeded, Editor);
        var outsiderSheet = await SubmitTodayAsync(seeded, Outsider);

        var timekeeper = seeded.As(Timekeeper).Host;
        Assert.Equal(f.AnalystId, (await timekeeper.Get<ITimesheetService>().GetAsync(analystSheet)).Timesheet.EmployeeId);
        var team = await timekeeper.Get<ITimesheetApprovalService>().GetTeamStatusAsync(Today);
        Assert.Equal(4, team.Members.Count);
        Assert.Empty(await timekeeper.Get<ITimesheetApprovalService>().ListPendingAsync());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => timekeeper.Get<ITimesheetApprovalService>().ApproveAsync(analystSheet));
        await seeded.As(Finance).Host.Get<ITimesheetService>().GetAsync(outsiderSheet);

        // A colleague who isn't the supervisor sees neither the timesheet nor anyone else's team.
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => seeded.As(Viewer).Host.Get<ITimesheetService>().GetAsync(analystSheet));
        Assert.Empty((await seeded.Host.Get<ITimesheetApprovalService>().GetTeamStatusAsync(Today)).Members);

        // Admin approves for employees without a supervisor, never over the supervisor.
        var admin = seeded.As(Admin).Host.Get<ITimesheetApprovalService>();
        Assert.Equal([outsiderSheet], (await admin.ListPendingAsync()).Select(t => t.Id));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => admin.ApproveAsync(analystSheet));
        Assert.Equal(TimesheetState.Approved, (await admin.ApproveAsync(outsiderSheet)).Status);

        var pm = seeded.As(Owner).Host.Get<ITimesheetApprovalService>();
        Assert.True(await pm.IsApproverAsync());
        Assert.Equal(TimesheetState.Approved, (await pm.ApproveAsync(analystSheet)).Status);
    }

    [Fact]
    public async Task Timesheet_events_notify_the_approver_and_the_employee_but_never_the_actor()
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        var id = await SubmitTodayAsync(seeded, Editor);
        var submitted = Assert.Single(await NotificationsAsync(seeded, Owner, NotificationKinds.TimesheetSubmitted));
        Assert.Equal(("Alex Rivera submitted a timesheet", "time/approvals"), (submitted.Title, submitted.Link));

        await SubmitTodayAsync(seeded, Outsider);
        Assert.Single(await NotificationsAsync(seeded, Admin, NotificationKinds.TimesheetSubmitted)); // no supervisor: the admins

        await seeded.As(Owner).Host.Get<ITimesheetApprovalService>().RejectAsync(id, "Missing Friday");
        var rejected = Assert.Single(await NotificationsAsync(seeded, Editor, NotificationKinds.TimesheetRejected));
        Assert.Contains("Missing Friday", rejected.Body);

        await seeded.As(Editor).Host.Get<ITimesheetService>().SubmitAsync(id, new SubmitTimesheetRequest(true));
        await seeded.As(Owner).Host.Get<ITimesheetApprovalService>().ApproveAsync(id);
        Assert.Single(await NotificationsAsync(seeded, Editor, NotificationKinds.TimesheetApproved));

        // The approver reopens: the employee hears about it, the approver doesn't hear about their own action.
        await seeded.As(Owner).Host.Get<ITimesheetService>().ReopenAsync(id, "Wrong charge code");
        Assert.Single(await NotificationsAsync(seeded, Editor, NotificationKinds.TimesheetReopened));
        Assert.Empty(await NotificationsAsync(seeded, Owner, NotificationKinds.TimesheetReopened));

        // The employee recalls a submission: the approver hears about it.
        await seeded.As(Editor).Host.Get<ITimesheetService>().SubmitAsync(id, new SubmitTimesheetRequest(true));
        await seeded.As(Editor).Host.Get<ITimesheetService>().ReopenAsync(id, "Forgot leave");
        Assert.Single(await NotificationsAsync(seeded, Owner, NotificationKinds.TimesheetReopened));
        Assert.Single(await NotificationsAsync(seeded, Editor, NotificationKinds.TimesheetReopened));
    }

    [Fact]
    public async Task Odc_submission_notifies_the_approver_and_the_decision_notifies_the_submitter()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var finance = seeded.As(Finance).Host.Get<IOdcService>();
        await finance.AttachReceiptAsync(f.VendorOdcId, "invoice.pdf", new MemoryStream([1, 2, 3]));
        await finance.SubmitAsync(f.VendorOdcId);
        var needs = Assert.Single(await NotificationsAsync(seeded, Owner, NotificationKinds.ExpenseApproval));
        Assert.Equal("odcs/approvals", needs.Link);

        await seeded.As(Owner).Host.Get<IOdcApprovalService>().ApproveOdcAsync(f.VendorOdcId);
        Assert.Single(await NotificationsAsync(seeded, Finance, NotificationKinds.ExpenseApproved));

        var second = await seeded.As(Finance).Host.Get<IOdcService>().CreateVendorOdcAsync(f.Project1,
            new SaveVendorOdcRequest(f.Wbs1, OdcCostType.Materials, Day, 60m, "Cables", "Acme", "INV-2"));
        await seeded.Host.Get<IOdcService>().SubmitAsync(second.Id);
        await seeded.As(Owner).Host.Get<IOdcApprovalService>().RejectOdcAsync(second.Id, "Wrong project");
        Assert.Single(await NotificationsAsync(seeded, Finance, NotificationKinds.ExpenseRejected));
    }

    [Fact]
    public async Task Timekeeping_and_odcs_are_bound_to_the_publisher()
    {
        await using var seeded = await CreateAsync();
        Assert.IsType<PublishingTimesheetNotifier>(seeded.Host.Get<ITimesheetNotifier>());
        Assert.IsType<PublishingOdcNotifier>(seeded.Host.Get<IOdcNotifier>());
        Assert.Equal(NotificationKinds.ExpenseApproval, PublishingOdcNotifier.KindFor(OdcNotificationKind.NeedsApproval));
    }

    [Fact]
    public async Task Missing_timesheet_reminder_respects_employment_and_runs_once_per_day()
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<Employee>().AddRange(
                new Employee { EmployeeNumber = "E8", Email = "gone@example.com", DisplayName = "Gone", HireDate = new DateOnly(2024, 1, 1), TerminationDate = Day.AddDays(-1) },
                new Employee { EmployeeNumber = "E9", Email = "new@example.com", DisplayName = "New", HireDate = Day.AddDays(1) });
            await db.SaveChangesAsync();
        }

        var check = seeded.Host.Get<MissingTimesheetCheck>();

        // analyst@, viewer@ and outsider@ have hours on Day; pm@ doesn't.
        Assert.Equal(1, await check.RunForAsync(Day));
        var reminder = Assert.Single(await NotificationsAsync(seeded, Owner, NotificationKinds.TimesheetMissing));
        Assert.Equal(MissingTimesheetCheck.LinkFor(Day), reminder.Link);
        Assert.Empty(await NotificationsAsync(seeded, "gone@example.com", NotificationKinds.TimesheetMissing));
        Assert.Empty(await NotificationsAsync(seeded, "new@example.com", NotificationKinds.TimesheetMissing));

        Assert.Equal(0, await check.RunForAsync(Day));
        Assert.Single(await NotificationsAsync(seeded, Owner, NotificationKinds.TimesheetMissing));

        // A different day is a different reminder.
        Assert.Equal(5, await check.RunForAsync(Day.AddDays(1))); // new@ was hired that day
    }

    [Theory]
    [InlineData("2026-10-19", "2026-10-16")] // Monday → Friday
    [InlineData("2026-10-21", "2026-10-20")]
    [InlineData("2026-10-18", "2026-10-16")] // Sunday → Friday
    public void Previous_business_day_skips_weekends(string today, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), MissingTimesheetCheck.PreviousBusinessDay(DateOnly.Parse(today)));
}
