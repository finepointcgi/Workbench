using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Time;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;
using Workbench.Domain.Approvals;
using Workbench.Domain.Identity;
using Workbench.Tests.Accounting;
using Workbench.Tests.Odcs;
using Workbench.Tests.Time;

namespace Workbench.Tests.Approvals;

/// <summary>Modules routed through configured (non-default) workflows, and the unified inbox acting through them.</summary>
public sealed class ApprovalModuleTests
{
    private static async Task PublishAsync(IWorkbenchDbContextFactory factory, TimeProvider clock, string type, params ApprovalStepDefinition[] steps)
    {
        var admin = new TestUser("admin@example.com");
        var service = new ApprovalWorkflowService(factory, new ApprovalEngine(clock), new AuditRecorder(admin, clock), new AccessControl(factory, admin));
        await service.SaveAsync(type, new SaveApprovalWorkflowRequest("Custom", new ApprovalWorkflowDefinition(steps)));
    }

    private static ApprovalInboxService Inbox(IWorkbenchDbContextFactory factory, TestUser user, TimeProvider clock, IServiceProvider services, params IApprovalDocumentHandler[] handlers) =>
        new(factory, new ApprovalEngine(clock), new AccessControl(factory, user), handlers);

    [Fact]
    public async Task A_timesheet_stays_submitted_until_the_last_step_approves()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.GrantAsync("fin@example.com", OrgRoles.Finance);
        await PublishAsync(host.Factory, host.Clock, ApprovalDocumentTypes.Timesheet,
            new ApprovalStepDefinition("Supervisor", 1, new ApproverRule(ApproverRuleKind.Supervisor)),
            new ApprovalStepDefinition("Finance review", 2, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance)));
        var id = await host.SubmittedDanaWeekAsync();

        host.User.Email = "fin@example.com";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveAsync(id));
        Assert.Empty(await host.Approvals.ListPendingAsync());

        host.SignInAs(host.Pat);
        Assert.Equal(TimesheetState.Submitted, (await host.Approvals.ApproveAsync(id)).Status);
        Assert.Contains(host.Notifier.Sent, n => n.Event == TimesheetEvent.Submitted && n.Recipients.Contains("fin@example.com"));
        Assert.Empty(await host.Approvals.ListPendingAsync());

        host.User.Email = "fin@example.com";
        Assert.Single(await host.Approvals.ListPendingAsync());
        var approved = await host.Approvals.ApproveAsync(id);
        Assert.Equal(TimesheetState.Approved, approved.Status);
        Assert.Equal("fin@example.com", approved.ReviewedBy);
    }

    [Fact]
    public async Task The_inbox_lists_my_tasks_across_types_and_decides_through_the_module()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();
        var services = new ServiceCollection().AddSingleton<ITimesheetApprovalService>(host.Approvals).BuildServiceProvider();
        var inbox = Inbox(host.Factory, host.User, host.Clock, services, new TimesheetApprovalHandler(services));

        host.SignInAs(host.Dana);
        Assert.Empty(await inbox.ListMineAsync());

        host.SignInAs(host.Pat);
        var item = Assert.Single(await inbox.ListMineAsync());
        Assert.Equal(ApprovalDocumentTypes.Timesheet, item.DocumentType);
        Assert.Equal(id, item.DocumentId);
        Assert.Equal("Supervisor", item.StepName);
        Assert.Equal(24m, item.Amount);
        Assert.Equal(1, await inbox.CountMineAsync());

        await Assert.ThrowsAsync<ArgumentException>(() => inbox.DecideAsync(ApprovalDocumentTypes.Timesheet, id, new ApprovalDecisionRequest(ApprovalDecisionKind.Return)));
        await inbox.DecideAsync(ApprovalDocumentTypes.Timesheet, id, new ApprovalDecisionRequest(ApprovalDecisionKind.Return, "Split Tuesday across two codes"));
        Assert.Empty(await inbox.ListMineAsync());

        var history = Assert.Single(await inbox.GetHistoryAsync(ApprovalDocumentTypes.Timesheet, id));
        Assert.Equal(ApprovalInstanceState.Returned, history.Status);
        Assert.Contains(history.Trail, a => a.Action == "Returned" && a.Actor == "pat@example.com");
        await using (var db = await host.Factory.CreateAsync())
        {
            var sheet = await db.Set<Workbench.Domain.ProjectAccounting.Timesheet>().SingleAsync(t => t.Id == id);
            Assert.Equal(Workbench.Domain.ProjectAccounting.TimesheetStatus.Rejected, sheet.Status);
            Assert.StartsWith("Returned for changes", sheet.RejectionReason);
        }

        // Resubmitted, then approved from the inbox.
        host.SignInAs(host.Dana);
        await host.Timesheets.SubmitAsync(id, new SubmitTimesheetRequest(true));
        host.SignInAs(host.Pat);
        await inbox.DecideAsync(ApprovalDocumentTypes.Timesheet, id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        Assert.Equal(TimesheetState.Approved, (await host.Timesheets.GetAsync(id)).Timesheet.Status);
        Assert.Equal(2, (await inbox.GetHistoryAsync(ApprovalDocumentTypes.Timesheet, id)).Count);

        host.SignInAs(host.Sam);
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() => inbox.GetHistoryAsync(ApprovalDocumentTypes.Timesheet, id));
    }

    [Fact]
    public async Task A_delegate_approves_a_timesheet_while_the_supervisor_is_out()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync();
        host.SignInAs(host.Pat);
        var today = DateOnly.FromDateTime(host.Clock.UtcNow);
        var delegations = new ApprovalDelegationService(host.Factory, new ApprovalEngine(host.Clock), new AccessControl(host.Factory, host.User), new AuditRecorder(host.User, host.Clock));
        await delegations.CreateAsync(new SaveApprovalDelegationRequest("sam@example.com", today, today.AddDays(3)));

        host.SignInAs(host.Sam);
        Assert.Single(await host.Approvals.ListPendingAsync());
        await host.Approvals.ApproveAsync(id);
        await using var db = await host.Factory.CreateAsync();
        var task = await db.Set<ApprovalTask>().SingleAsync(t => t.Status == ApprovalTaskStatus.Approved);
        Assert.Equal("sam@example.com", task.DecidedBy);
        Assert.Equal("pat@example.com", task.OnBehalfOf);
    }

    [Fact]
    public async Task Expense_reports_follow_a_conditional_finance_step_for_unallowable_costs()
    {
        var host = await OdcTestHost.CreateAsync();
        await using (var db = await host.Factory.CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = "finance@example.com", DisplayName = "Finance" }, OrgRoles.Finance);
            await db.SaveChangesAsync();
        }

        await PublishAsync(host.Factory, TimeProvider.System, ApprovalDocumentTypes.ExpenseReport,
            new ApprovalStepDefinition("Supervisor", 1, new ApproverRule(ApproverRuleKind.Supervisor), WhenSubmitterIsApprover: SelfApprovalPolicy.Unassigned),
            new ApprovalStepDefinition("Finance", 2, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance),
                Conditions: [new ApprovalCondition(ApprovalConditionKind.HasUnallowableCost)]));

        var plain = await host.SubmittedDevReportAsync(40m);
        host.As(OdcTestHost.Pm);
        Assert.Equal(OdcApprovalState.Approved, (await host.Approvals.ApproveReportAsync(plain.Id)).Status);

        host.As(OdcTestHost.Dev);
        var report = await host.Expenses.CreateReportAsync(new SaveExpenseReportRequest("Dinner", OdcTestHost.Day, OdcTestHost.Day));
        await host.Expenses.AddLineAsync(report.Id, host.Line(40m, description: "Wine", unallowable: true, reason: "Alcohol, FAR 31.205-51"));
        await host.Expenses.SubmitAsync(report.Id);
        host.As(OdcTestHost.Pm);
        Assert.Equal(OdcApprovalState.Submitted, (await host.Approvals.ApproveReportAsync(report.Id)).Status);
        Assert.Empty((await host.Approvals.GetMyQueueAsync()).Reports);
        host.As("finance@example.com");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Approvals.ApproveReportAsync(plain.Id));
        Assert.Single((await host.Approvals.GetMyQueueAsync()).Reports);
        Assert.Equal(OdcApprovalState.Approved, (await host.Approvals.ApproveReportAsync(report.Id)).Status);
    }

    [Fact]
    public async Task Manual_journal_entries_post_when_the_workflow_completes()
    {
        var host = await OdcTestHost.CreateAsync();
        host.As("finance@example.com");
        await using (var db = await host.Factory.CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = "finance@example.com", DisplayName = "Finance" }, OrgRoles.Finance);
            await RoleStore.AddUserAsync(db, new AppUser { Email = "controller@example.com", DisplayName = "Controller" }, OrgRoles.Finance);
            await db.SaveChangesAsync();
        }

        var gl = await LedgerKit.CreateAsync(host.Factory, host.User, TimeProvider.System);
        var journal = new JournalService(host.Factory, gl.Ledger, new AuditRecorder(host.User, TimeProvider.System), new ApprovalEngine(TimeProvider.System));

        // Built-in default: Finance posts, the preparer included (as before).
        var first = await journal.CreateDraftAsync(gl.Entry(OdcTestHost.Day, ("1010", 100m, 0), ("3100", 0, 100m)));
        Assert.Equal(JournalEntryState.Posted, (await journal.PostAsync(first.Entry.Id)).Entry.Status);

        // A second reviewer for manual entries, never the preparer.
        await PublishAsync(host.Factory, TimeProvider.System, ApprovalDocumentTypes.JournalEntry,
            new ApprovalStepDefinition("Preparer", 1, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance), WhenSubmitterIsApprover: SelfApprovalPolicy.Allow),
            new ApprovalStepDefinition("Controller", 2, new ApproverRule(ApproverRuleKind.AnyOf, People: ["controller@example.com"])));
        var second = await journal.CreateDraftAsync(gl.Entry(OdcTestHost.Day, ("1010", 50m, 0), ("3100", 0, 50m)));
        Assert.Equal(JournalEntryState.Draft, (await journal.PostAsync(second.Entry.Id)).Entry.Status);
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() => journal.PostAsync(second.Entry.Id));
        host.As("controller@example.com");
        Assert.Equal(JournalEntryState.Posted, (await journal.PostAsync(second.Entry.Id)).Entry.Status);
    }
}
