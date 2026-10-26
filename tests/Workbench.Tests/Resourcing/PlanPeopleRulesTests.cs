using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain.Identity;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;
using static Workbench.Tests.Resourcing.ResourcingHost;
using OrgRoles = Workbench.Contracts.Identity.OrgRoles;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// The rules that keep a project's plan, its WBS and its people in step: a task's people are one list (hours and
/// assignees), people's dates follow their task, hours that can't fit and people on summary rows are flagged, and summary
/// rows take nobody new. Today is Wed Oct 14 2026; Phase 1 (a summary) holds Design (Oct 12–23) and Build (Oct 19–30).
/// </summary>
public sealed class PlanPeopleRulesTests
{
    private static async Task<ResourcingHost> HostAsync()
    {
        var host = await CreateAsync();
        await using var db = await host.Db.CreateAsync();
        await RoleStore.AddUserAsync(db, new AppUser { Email = Dev, DisplayName = "Dana" }, OrgRoles.Employee);
        await RoleStore.AddUserAsync(db, new AppUser { Email = Analyst, DisplayName = "Alex" }, OrgRoles.Employee);
        await db.SaveChangesAsync();
        return host;
    }

    private static async Task<Guid> ItemAsync(ResourcingHost host, string title)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == title).Select(i => i.Id).SingleAsync();
    }

    private static async Task<List<string>> AssigneesAsync(ResourcingHost host, Guid itemId)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Set<ItemAssignee>().Where(a => a.ItemId == itemId).Select(a => a.Email).OrderBy(e => e).ToListAsync();
    }

    [Fact]
    public async Task People_with_hours_are_the_tasks_assignees_and_taking_them_off_either_list_takes_them_off_both()
    {
        await using var host = await HostAsync();
        var (build, design) = (await ItemAsync(host, "Build"), await ItemAsync(host, "Design"));
        var plans = host.As(Pm).Get<IResourcePlanService>();
        var tasks = host.As(Pm).Get<ITaskAssignmentService>();

        // Giving Dana hours on Build makes Build hers in My work, on the Gantt and the board.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 40m)]);
        Assert.Equal([Dev], await AssigneesAsync(host, build));

        // Taking her off in the Assignees cell takes her hours off too.
        await tasks.SetAssigneesAsync(host.SheetId, build, new SetAssigneesRequest([]));
        Assert.Empty((await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments);
        Assert.Empty(await AssigneesAsync(host, build));

        // Assigning Alex in the Assignees cell puts him in the plan's people with no hours yet.
        await tasks.SetAssigneesAsync(host.SheetId, design, new SetAssigneesRequest([Analyst]));
        var alex = (await plans.GetAsync(host.Project1)).Task(design)!.People.Single();
        Assert.Equal((true, (Guid?)null, 0m), (alex.NoHoursYet, alex.AssignmentId, alex.Hours));

        // Giving him hours makes them an assignment; he stays the assignee.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, WbsElementId: host.Design, EmployeeId: host.AnalystId, Hours: 20m)]);
        alex = (await plans.GetAsync(host.Project1)).Task(design)!.People.Single();
        Assert.Equal((false, 20m), (alex.NoHoursYet, alex.Hours));
        Assert.Equal([Analyst], await AssigneesAsync(host, design));

        // Taking him off in the people grid takes him off the task, however he was assigned.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.RemovePerson, AssignmentId: alex.AssignmentId)]);
        Assert.Empty(await AssigneesAsync(host, design));
        Assert.Empty((await plans.GetAsync(host.Project1)).Task(design)!.People);

        // Someone with no hours yet can be taken off from the people grid as well.
        await tasks.SetAssigneesAsync(host.SheetId, design, new SetAssigneesRequest([Analyst]));
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.Unassign, WbsElementId: host.Design, EmployeeId: host.AnalystId)]);
        Assert.Empty(await AssigneesAsync(host, design));
    }

    [Fact]
    public async Task People_on_a_task_move_with_it_unless_their_dates_were_set_on_purpose()
    {
        await using var host = await HostAsync();
        var build = await ItemAsync(host, "Build");
        var plans = host.As(Pm).Get<IResourcePlanService>();

        // Dana on Build's own dates; Alex on dates picked by hand (Build's first week only).
        await plans.ApplyAsync(host.Project1,
        [
            new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 40m),
            new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.AnalystId, Hours: 20m, From: new DateOnly(2026, 10, 19), To: new DateOnly(2026, 10, 23)),
        ]);

        // Build slips a week by typing new dates in the plan (not through "Try changes").
        await host.As(Pm).Get<IItemService>().UpdateAsync(host.SheetId, build,
            new UpdateItemRequest("Build", new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 6), null, new Dictionary<string, string?>()));

        var staffing = (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments;
        var dana = staffing.Single(a => a.EmployeeId == host.DevId);
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 6), 40m), (dana.From, dana.To, dana.TotalHours));
        Assert.Equal([new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 2)], dana.Weeks.Select(w => w.WeekStart));
        Assert.Equal(40m, dana.Weeks.Sum(w => w.Hours));

        // Alex's hand-picked dates stay, and the plan says they're now outside the task.
        var alex = staffing.Single(a => a.EmployeeId == host.AnalystId);
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 23)), (alex.From, alex.To));
        var flagged = (await plans.GetAsync(host.Project1)).Task(build)!.People.Single(p => p.EmployeeId == host.AnalystId);
        Assert.Contains(flagged.Issues, i => i.Contains("run outside the task"));
    }

    [Fact]
    public async Task Staffing_a_task_that_has_already_ended_says_when_it_ended()
    {
        await using var host = await HostAsync();
        var design = await ItemAsync(host, "Design");
        // Design ended on Mon Oct 12, before today (Wed Oct 14).
        await host.As(Pm).Get<IItemService>().UpdateAsync(host.SheetId, design,
            new UpdateItemRequest("Design", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 12), null, new Dictionary<string, string?>()));

        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            host.As(Pm).Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.DevId, Hours: 8m)]));

        Assert.Contains("Design finished on Oct 12, 2026", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("end date is before", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hours_that_cant_fit_and_people_on_a_summary_are_flagged_and_a_summary_takes_nobody_new()
    {
        await using var host = await HostAsync();
        var (phase, design) = (await ItemAsync(host, "Phase 1"), await ItemAsync(host, "Design"));
        var plans = host.As(Pm).Get<IResourcePlanService>();

        // 120 h in Design's 8 working days left (Oct 14–23) is 15 h a day.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.DevId, Hours: 120m)]);
        var dana = (await plans.GetAsync(host.Project1)).Task(design)!.People.Single();
        Assert.Contains(dana.Issues, i => i.Contains("120 h in 8 working days is 15 h a day"));

        // Phase 1 is a summary: nobody new goes on it, and the grid doesn't offer it.
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, WbsElementId: host.Root1, EmployeeId: host.AnalystId, Hours: 10m)]));
        Assert.Contains("summary row", error.Message);
        Assert.False((await plans.GetAsync(host.Project1)).Task(phase)!.CanAssign);

        // Someone already on a summary (from before this rule) is flagged with where to move them.
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<StaffingAssignment>().Add(new StaffingAssignment
            {
                EmployeeId = host.AnalystId, ProjectId = host.Project1, WbsElementId = host.Root1, From = Today, To = new DateOnly(2026, 10, 30),
                TotalHours = 16m, CreatedBy = Pm, CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var onSummary = (await plans.GetAsync(host.Project1)).Task(phase)!.People.Single();
        Assert.Contains(onSummary.Issues, i => i.Contains("Phase 1 is a summary row"));
    }
}
