using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Application.Approvals;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Mobile;
using Workbench.Ui.Setup.Approvals;

namespace Workbench.Tests.Approvals;

/// <summary>The one-click "project managers approve time charged to their projects" option on Setup › Approval workflows.</summary>
public sealed class ApprovalWorkflowsPageTests
{
    private sealed class User(OrgRoles roles) : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentUserInfo("admin@example.com", "Admin", roles, null));
    }

    private static ApprovalWorkflowDto Timesheet(ApprovalWorkflowDefinition definition) =>
        new(ApprovalDocumentTypes.Timesheet, "Timesheet", "Timesheet approval", 1, definition, [], true);

    private static (BunitContext Ctx, Fake<IApprovalWorkflowService> Workflows) Context(OrgRoles roles)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICurrentUserContext>(new User(roles));
        var (workflows, fake) = Fake<IApprovalWorkflowService>.Create();
        var stored = Timesheet(ApprovalDefaults.For(ApprovalDocumentTypes.Timesheet));
        fake.On(nameof(IApprovalWorkflowService.ListAsync), _ => (IReadOnlyList<ApprovalWorkflowSummaryDto>)[]);
        fake.On(nameof(IApprovalWorkflowService.ListOrgUnitApproversAsync), _ => (IReadOnlyList<OrgUnitApproverDto>)[]);
        fake.On(nameof(IApprovalWorkflowService.GetAsync), _ => stored);
        fake.On(nameof(IApprovalWorkflowService.SaveAsync), args => stored = Timesheet(((SaveApprovalWorkflowRequest)args[1]!).Definition));
        ctx.Services.AddSingleton(workflows);
        var (people, pf) = Fake<IPeopleService>.Create();
        pf.Returns(nameof(IPeopleService.ListEmployeesAsync), (IReadOnlyList<EmployeeDto>)[]);
        ctx.Services.AddSingleton(people);
        return (ctx, fake);
    }

    [Fact]
    public async Task An_admin_turns_project_manager_time_approval_on_and_off()
    {
        var (ctx, workflows) = Context(OrgRoles.Admin);
        await using var _ctx = ctx;

        var page = ctx.Render<ApprovalWorkflowsPage>();
        page.WaitForAssertion(() => Assert.Contains("Project managers approve time charged to their projects", page.Find("[data-testid='pm-time-approval']").TextContent));
        var input = page.Find("[data-testid='pm-time-approval'] input");
        Assert.False(input.HasAttribute("checked"));

        input.Change(true);

        page.WaitForAssertion(() => Assert.Single(workflows.Calls, c => c.Method == nameof(IApprovalWorkflowService.SaveAsync)));
        var request = (SaveApprovalWorkflowRequest)workflows.Single(nameof(IApprovalWorkflowService.SaveAsync))[1]!;
        Assert.Equal("Timesheet approval", request.Name);
        Assert.True(ProjectManagerTimeApproval.IsOn(request.Definition));
        Assert.Contains(request.Definition.Steps, s => s.Approver.Kind == ApproverRuleKind.Supervisor); // The supervisor still reviews.

        page.Find("[data-testid='pm-time-approval'] input").Change(false);
        page.WaitForAssertion(() => Assert.Equal(2, workflows.Calls.Count(c => c.Method == nameof(IApprovalWorkflowService.SaveAsync))));
        Assert.False(ProjectManagerTimeApproval.IsOn(((SaveApprovalWorkflowRequest)workflows.Calls.Last(c => c.Method == nameof(IApprovalWorkflowService.SaveAsync)).Args[1]!).Definition));
    }

    [Fact]
    public async Task Finance_sees_the_option_but_cannot_change_it()
    {
        var (ctx, _) = Context(OrgRoles.Finance);
        await using var _ctx = ctx;

        var page = ctx.Render<ApprovalWorkflowsPage>();

        page.WaitForAssertion(() => Assert.True(page.Find("[data-testid='pm-time-approval'] input").HasAttribute("disabled")));
    }
}
