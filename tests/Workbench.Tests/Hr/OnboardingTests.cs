using Microsoft.EntityFrameworkCore;
using Workbench.Application.Hr;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Hr;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.Assets;
using Workbench.Domain.Identity;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using D = Workbench.Domain.Hr;

namespace Workbench.Tests.Hr;

/// <summary>Onboarding and offboarding checklists: who owns each task, the home to-dos, and offboarding's own actions.</summary>
public sealed class OnboardingTests
{
    [Fact]
    public async Task Onboarding_assigns_tasks_to_hr_it_finance_the_manager_and_the_new_hire()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var template = await kit.Onboarding.SaveTemplateAsync(null, new SaveChecklistTemplateRequest("Standard", ChecklistKind.Onboarding, true,
        [
            new("Send paperwork", ChecklistOwner.Hr, -7),
            new("Laptop and sign-in", ChecklistOwner.It, -2),
            new("Plan the first week", ChecklistOwner.Manager, 0),
            new("Form I-9", ChecklistOwner.Employee, 0),
            new("Set up cost rate", ChecklistOwner.Finance, 1),
        ]));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Onboarding.StartOnboardingAsync(new StartOnboardingRequest(kit.Fx.AnalystId, template.Id)));
        var checklist = await kit.As(HrKit.Hr).Onboarding.StartOnboardingAsync(new StartOnboardingRequest(kit.Fx.AnalystId, template.Id));
        Assert.Equal(7, checklist.Tasks.Count); // five from the template + benefits enrollment + EEO self-identification
        Assert.Equal(Owner, checklist.Tasks.Single(t => t.Owner == ChecklistOwner.Manager).AssigneeEmail);
        Assert.All(checklist.Tasks.Where(t => t.Owner == ChecklistOwner.Employee), t => Assert.Equal(Editor, t.AssigneeEmail));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Onboarding.StartOnboardingAsync(new StartOnboardingRequest(kit.Fx.AnalystId, template.Id)));

        // Each person sees their own tasks (and the home page lists them).
        Assert.Equal(["Plan the first week"], (await kit.As(Owner).Onboarding.ListMyTasksAsync()).Select(t => t.Task.Title));
        Assert.Equal(3, (await kit.As(Editor).Onboarding.ListMyTasksAsync()).Count);
        Assert.Equal(["Laptop and sign-in"], (await kit.As(Admin).Onboarding.ListMyTasksAsync()).Where(t => t.Task.Owner == ChecklistOwner.It).Select(t => t.Task.Title));
        Assert.Equal(["Set up cost rate"], (await kit.As(Finance).Onboarding.ListMyTasksAsync()).Select(t => t.Task.Title));
        Assert.Equal(["Send paperwork"], (await kit.As(HrKit.Hr).Onboarding.ListMyTasksAsync()).Select(t => t.Task.Title));
        var todo = await kit.As(Owner).Get<IMyTodoService>().GetAsync();
        Assert.Contains(todo.Items, i => i.Key.StartsWith("hr-task:") && i.Title.Contains("Plan the first week"));

        // The owner completes their task; others can't; HR can step in for the manager.
        var i9 = checklist.Tasks.First(t => t.Title == "Form I-9");
        var send = checklist.Tasks.Single(t => t.Title == "Send paperwork");
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Editor).Onboarding.CompleteTaskAsync(send.Id, new CompleteChecklistTaskRequest(null)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(Viewer).Onboarding.CompleteTaskAsync(i9.Id, new CompleteChecklistTaskRequest(null)));
        var after = await kit.As(Editor).Onboarding.CompleteTaskAsync(i9.Id, new CompleteChecklistTaskRequest("Done"));
        Assert.Equal(ChecklistTaskStatus.Done, after.Tasks.Single(t => t.Id == i9.Id).Status);
        await kit.As(HrKit.Hr).Onboarding.CompleteTaskAsync(checklist.Tasks.Single(t => t.Owner == ChecklistOwner.Manager).Id, new CompleteChecklistTaskRequest(null));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Onboarding.CompleteTaskAsync(checklist.Tasks.Single(t => t.Owner == ChecklistOwner.It).Id, new CompleteChecklistTaskRequest(null)));

        // Supervisors see their reports' checklists; peers don't.
        Assert.Single(await kit.As(Owner).Onboarding.ListAsync());
        Assert.Empty(await kit.As(Viewer).Onboarding.ListAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Onboarding.GetAsync(checklist.Id));
    }

    [Fact]
    public async Task Offboarding_deprovisions_access_recovers_assets_and_flags_the_final_pay()
    {
        await using var kit = await HrKit.CreateAsync();
        var viewer = kit.Fx.ViewerId;
        var lastDay = HrKit.Today.AddDays(7);
        var weekStart = lastDay.AddDays(-(((int)lastDay.DayOfWeek + 6) % 7));

        // A laptop in Val's custody, a PTO balance and a medical election.
        kit.As(Finance);
        var category = (await kit.Get<IAssetService>().ListCategoriesAsync()).First();
        var laptop = await kit.Get<IAssetService>().CreateAsync(new SaveAssetRequest("Laptop", category.Id, HrKit.Today.AddMonths(-6), 2_400m, CustodianEmployeeId: viewer));
        var pto = await kit.Payroll.PtoPolicyAsync();
        var plans = await kit.PlansAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<LeaveLedgerEntry>().Add(new LeaveLedgerEntry { EmployeeId = viewer, PolicyId = pto.Id, Date = HrKit.Today.AddDays(-30), Kind = LeaveLedgerKind.Adjustment, Hours = 24m, CreatedBy = "test" });
            db.Set<D.BenefitElection>().Add(new D.BenefitElection
            {
                EmployeeId = viewer, PlanId = plans.Dental.Id, PlanType = D.BenefitPlanType.Dental, CoverageStart = new DateOnly(HrKit.Today.Year, 1, 1),
                Source = D.ElectionSource.Administrative, ElectedBy = "test",
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(Owner).Onboarding.StartOffboardingAsync(new StartOffboardingRequest(viewer, lastDay, TerminationReason.Voluntary, null, null)));
        var checklist = await kit.As(HrKit.Hr).Onboarding.StartOffboardingAsync(new StartOffboardingRequest(viewer, lastDay, TerminationReason.Voluntary, null, "Moving"));
        ChecklistTaskDto TaskFor(ChecklistAction action) => checklist.Tasks.Single(t => t.Action == action);
        Assert.Equal(laptop.Asset.Id, TaskFor(ChecklistAction.RecoverAsset).AssetId);
        Assert.Equal(Owner, TaskFor(ChecklistAction.FinalTimesheet).AssigneeEmail);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(lastDay, (await db.Set<Employee>().FirstAsync(e => e.Id == viewer)).TerminationDate);
        }

        // IT (administrators) deactivates the sign-in; HR can't.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.DeprovisionAccess).Id, new CompleteChecklistTaskRequest(null)));
        var deprovisioned = await kit.As(Admin).Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.DeprovisionAccess).Id, new CompleteChecklistTaskRequest(null));
        Assert.NotNull(deprovisioned.AccessDeprovisionedAtUtc);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.False((await db.Set<AppUser>().FirstAsync(u => u.Email == Viewer)).IsActive);
        }

        Assert.Equal(1, await kit.SecurityEventsAsync(HrSecurityEvents.AccessDeprovisioned));

        // Finance recovers the asset: the custodian is cleared.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.As(HrKit.Hr).Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.RecoverAsset).Id, new CompleteChecklistTaskRequest(null)));
        await kit.As(Finance).Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.RecoverAsset).Id, new CompleteChecklistTaskRequest(null));
        var returned = await kit.Get<IAssetService>().GetAsync(laptop.Asset.Id);
        Assert.Null(returned.CustodianEmployeeId);

        // The manager confirms the final timesheet only once it's approved.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Owner).Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.FinalTimesheet).Id, new CompleteChecklistTaskRequest(null)));
        await kit.Payroll.SetTimesheetStatusAsync(viewer, weekStart, TimesheetStatus.Approved);
        Assert.True((await kit.Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.FinalTimesheet).Id, new CompleteChecklistTaskRequest(null))).FinalTimesheetApproved);

        // HR records the leave payout and ends benefits at the end of the month.
        kit.As(HrKit.Hr);
        Assert.Equal(24m, (await kit.Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.LeavePayout).Id, new CompleteChecklistTaskRequest(null))).LeavePayoutHours);
        await kit.Onboarding.CompleteTaskAsync(TaskFor(ChecklistAction.EndBenefits).Id, new CompleteChecklistTaskRequest(null));
        await using (var db = await kit.Factory.CreateAsync())
        {
            var end = new DateOnly(lastDay.Year, lastDay.Month, 1).AddMonths(1).AddDays(-1);
            Assert.Equal(end, (await db.Set<D.BenefitElection>().SingleAsync(e => e.EmployeeId == viewer)).CoverageEnd);
        }

        foreach (var open in (await kit.Onboarding.GetAsync(checklist.Id)).Tasks.Where(t => t.Status == ChecklistTaskStatus.Open))
        {
            await kit.Onboarding.SkipTaskAsync(open.Id, new CompleteChecklistTaskRequest("Not needed"));
        }

        Assert.Equal(ChecklistStatus.Completed, (await kit.Onboarding.GetAsync(checklist.Id)).Status);
        Assert.Equal(TerminationReason.Voluntary, (await kit.Personnel.GetAsync(viewer)).Job!.TerminationReason);
    }
}
