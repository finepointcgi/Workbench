using Workbench.ApiClient;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Financials;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>
/// The staged demo projects (LifecycleSeed): 5101 to 5104, one at each step of a project's life, so any stage can be shown
/// straight away. Each says what comes next and who does it, and the right person has the to-do.
/// </summary>
public sealed class LifecycleDemoTests
{
    private const string Finance = "pm@example.com";
    private const string Pm = "sam.pm@example.com";

    [Fact]
    public async Task Each_staged_project_is_at_its_step_and_names_what_comes_next_and_who_has_the_to_do()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs(Finance);
        using var pm = web.CreateClientAs(Pm);
        var projects = (await new HttpProjectService(finance).ListProjectsAsync()).ToDictionary(p => p.Code);
        var plans = new HttpProjectPlanService(finance);
        async Task<ProjectPlanDto> PlanAsync(string code) => await plans.GetAsync(projects[code].Id);
        string Next(ProjectPlanDto plan) => plan.Steps!.Single(s => s.Current).Key;

        // 1. Just set up: no funding, no labor categories, no manager. Finance has the next step.
        var setUp = await PlanAsync("5101");
        Assert.Equal((ProjectState.Planning, false, "funding"), (setUp.ProjectStatus, setUp.SetupDone, Next(setUp)));
        Assert.Null(projects["5101"].ProjectManagerId);

        // 2. Funded, categories chosen, Sam is the manager: waiting for his plan.
        var planning = await PlanAsync("5102");
        Assert.Equal((ProjectState.Planning, true, "plan", PlanApprovalStatus.Draft), (planning.ProjectStatus, planning.SetupDone, Next(planning), planning.Status));
        Assert.Equal(3, planning.Categories!.Count(c => c.Selected));
        var todo = (await new HttpMyTodoService(pm).GetAsync()).Items;
        Assert.Contains(todo, i => i.Key == $"plan:{projects["5102"].Id}:plan");

        // 3. Sam's plan is with Finance, who has the review to-do; Sam doesn't.
        var awaiting = await PlanAsync("5103");
        Assert.Equal((ProjectState.Planning, PlanApprovalStatus.Submitted, true), (awaiting.ProjectStatus, awaiting.Status, awaiting.CanReview));
        Assert.Contains(await new HttpApprovalInboxService(finance).ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan && i.DocumentId == projects["5103"].Id);
        Assert.DoesNotContain(await new HttpApprovalInboxService(pm).ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan);

        // 4. Approved: the project is Active, every step is done, and the baseline holds the plan's hours.
        var executing = await PlanAsync("5104");
        Assert.Equal((ProjectState.Active, PlanApprovalStatus.Approved, 1), (executing.ProjectStatus, executing.Status, executing.Approvals));
        Assert.All(executing.Steps!, s => Assert.True(s.Done));
        Assert.NotNull(executing.BaselineVersionId);
        var totals = (await new HttpProjectFinancialsService(finance).GetProjectSummaryAsync(projects["5104"].Id)).Totals;
        Assert.Equal(40m, totals.BudgetHours); // Sam 16 h, Val 8 h, Sam 16 h

        // 5103 is ready to approve: nobody is overbooked and every task has someone, so the plan has nothing to warn about.
        var resourcePlan = await new HttpResourcePlanService(finance).GetAsync(projects["5103"].Id);
        Assert.True(resourcePlan.Totals.OverbookedPeople.Count == 0, "overbooked: " + string.Join(", ", resourcePlan.Totals.OverbookedPeople));
        Assert.Equal((0, 0), (resourcePlan.Health!.Failed, resourcePlan.Tasks.Sum(t => t.People.Sum(p => p.Issues.Count))));

        // Finance can approve 5103, which opens it.
        var approved = await plans.ApproveAsync(projects["5103"].Id);
        Assert.Equal(ProjectState.Active, approved.ProjectStatus);
    }
}
