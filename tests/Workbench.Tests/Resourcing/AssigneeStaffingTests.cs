using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain.Identity;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// Putting someone on a task in a project's plan plans their hours on it, so Resourcing sees them without a separate step.
/// Today is Wed Oct 14 2026; Design runs Oct 12–23 and Build Oct 19–30.
/// </summary>
public sealed class AssigneeStaffingTests
{
    private static async Task<ResourcingHost> HostAsync()
    {
        var host = await CreateAsync();
        await using var db = await host.Db.CreateAsync();
        await RoleStore.AddUserAsync(db, new AppUser { Email = Analyst, DisplayName = "Alex" }, OrgRoles.Employee);
        await db.SaveChangesAsync();
        return host;
    }

    private static async Task<Guid> ItemAsync(ResourcingHost host, string title)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == title).Select(i => i.Id).SingleAsync();
    }

    [Fact]
    public async Task A_new_assignee_gets_hours_over_the_tasks_remaining_dates_and_follows_the_task()
    {
        await using var host = await HostAsync();
        var design = await ItemAsync(host, "Design");

        await host.As(Pm).Get<ITaskAssignmentService>().SetAssigneesAsync(host.SheetId, design, new SetAssigneesRequest([Analyst]));

        var alex = (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments.Single(a => a.EmployeeId == host.AnalystId);
        Assert.Equal((new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 23)), (alex.From, alex.To)); // today to the task's finish
        Assert.True(alex.TotalHours > 0);
        Assert.Equal(host.Design, alex.WbsElementId);

        // The heat map sees the hours, and when the task slips the hours go with it.
        Assert.Contains((await host.Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(new DateOnly(2026, 10, 12), 2)))
            .Rows.Single(r => r.EmployeeId == host.AnalystId).Weeks, w => w.Allocated > 0);
        await host.As(Pm).Get<IItemService>().UpdateAsync(host.SheetId, design,
            new UpdateItemRequest("Design", new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 30), null, new Dictionary<string, string?>()));
        var moved = (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments.Single(a => a.EmployeeId == host.AnalystId);
        Assert.Equal(new DateOnly(2026, 10, 30), moved.To);
    }

    [Fact]
    public async Task Assigning_someone_who_already_has_hours_on_the_task_leaves_their_hours_alone()
    {
        await using var host = await HostAsync();
        var design = await ItemAsync(host, "Design");
        var plans = host.As(Pm).Get<IResourcePlanService>();
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.AnalystId, Hours: 12m)]);

        await host.As(Pm).Get<ITaskAssignmentService>().SetAssigneesAsync(host.SheetId, design, new SetAssigneesRequest([Analyst]));

        var mine = (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments.Where(a => a.EmployeeId == host.AnalystId).ToList();
        Assert.Equal(12m, Assert.Single(mine).TotalHours);
    }

    [Fact]
    public async Task A_task_with_no_dates_cant_be_staffed_so_the_staffing_tab_says_so()
    {
        await using var host = await HostAsync();
        var design = await ItemAsync(host, "Design");
        await host.As(Pm).Get<IItemService>().UpdateAsync(host.SheetId, design, new UpdateItemRequest("Design", null, null, null, new Dictionary<string, string?>()));

        await host.As(Pm).Get<ITaskAssignmentService>().SetAssigneesAsync(host.SheetId, design, new SetAssigneesRequest([Analyst]));

        Assert.DoesNotContain((await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments, a => a.EmployeeId == host.AnalystId);
        var plan = await host.As(Pm).Get<IProjectStaffingService>().GetPlanAsync(host.Project1);
        Assert.Contains(plan.Notes, n => n.Contains("Alex") && n.Contains("no hours") && n.Contains("no dates"));
    }
}
