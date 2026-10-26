using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Ui.Projects;

namespace Workbench.Tests.Projects;

/// <summary>"Where this project is" on a project's Overview: the six steps, numbered in order, with a button for the next one.</summary>
public sealed class ProjectSetupStepsTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static ProjectHubDto Hub(Guid? sheetId, bool setup = true) => new(
        new ProjectHubRowDto(ProjectId, "5101", "Harbor", null, null, Guid.NewGuid(), "C-1", ProjectState.Planning, null, null, sheetId, false, false, null, null),
        null, null, new ProjectHubTabsDto(true, true, true, true, true, true, true, true, setup), null, [], new DateOnly(2026, 10, 26));

    private static BunitContext Context(IProjectPlanService? plans = null, IProjectStartService? start = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        if (plans is not null)
        {
            ctx.Services.AddSingleton(plans);
        }

        if (start is not null)
        {
            ctx.Services.AddSingleton(start);
        }

        return ctx;
    }

    [Fact]
    public void The_steps_are_numbered_one_to_six_and_only_the_next_one_has_a_button()
    {
        using var ctx = Context(new FakePlans());
        var steps = ctx.Render<ProjectSetupSteps>(p => p.Add(s => s.Hub, Hub(Guid.NewGuid())));

        steps.WaitForAssertion(() => Assert.NotEmpty(steps.FindAll("li[data-step]")));
        Assert.Equal(["1", "3", "4", "5", "6"], steps.FindAll(".wb-step-number").Select(n => n.TextContent.Trim())); // step 2 is done: a tick, not a number
        Assert.Equal(["funding"], steps.FindAll("[data-testid^='setup-step-']").Select(b => b.GetAttribute("data-testid")!["setup-step-".Length..]));
    }

    [Fact]
    public void A_project_with_no_plan_offers_one_button_to_set_it_up_from_the_contract()
    {
        var start = new FakeStart();
        using var ctx = Context(new FakePlans(), start);
        var steps = ctx.Render<ProjectSetupSteps>(p => p.Add(s => s.Hub, Hub(null)));

        Assert.Empty(steps.FindAll("[data-testid='setup-steps']"));
        steps.Find("[data-testid='finish-setup']").Click();

        steps.WaitForAssertion(() => Assert.Equal(ProjectId, start.Finished));
    }

    private sealed class FakeStart : IProjectStartService
    {
        public Guid? Finished { get; private set; }

        public Task<ProjectDto> FinishSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            Finished = projectId;
            return Task.FromResult(new ProjectDto(projectId, "5101", "Harbor", null, null, null, ProjectState.Planning, null, null, Guid.NewGuid()));
        }

        public Task<IReadOnlyList<ProjectTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> SuggestCodeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectDto> StartAsync(StartProjectRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Workbench.Contracts.Scheduling.PlanImportPreview> CheckImportAsync(Workbench.Contracts.Scheduling.PlanImportRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePlans : IProjectPlanService
    {
        public Task<ProjectPlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
        {
            var terms = new ProjectTermsDto(null, null, null, false, null, false, null, false, 0m, false, null, null, []);
            ProjectStepDto Step(string key, string label, bool done, bool current) => new(key, label, done, current, "detail");
            return Task.FromResult(new ProjectPlanDto(projectId, "5101", "Harbor", null, terms, PlanApprovalStatus.Draft, null, null, null, null, null, null, 0, null, false, [],
                false, false, false, ProjectStatus: ProjectState.Planning, Steps:
                [
                    Step("funding", "Funding", false, true), Step("categories", "Labor categories", true, false), Step("manager", "Project manager", false, false),
                    Step("plan", "Plan", false, false), Step("signoff", "Sign-off", false, false), Step("executing", "Executing", false, false),
                ]));
        }

        public Task<ProjectTermsDto> TermsFromContractAsync(Guid contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectPlanDto> SaveTermsAsync(Guid projectId, SaveProjectTermsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectPlanDto> SaveCategoriesAsync(Guid projectId, IReadOnlyList<Guid> laborCategoryIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectPlanDto> SubmitAsync(Guid projectId, string? note = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectPlanDto> ApproveAsync(Guid projectId, string? comment = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProjectPlanDto> ReturnAsync(Guid projectId, string comment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
