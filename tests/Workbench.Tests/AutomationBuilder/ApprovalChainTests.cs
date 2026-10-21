using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Approvals;
using Workbench.Domain.Companies;
using static Workbench.Tests.AutomationBuilder.AutomationKit;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>The approval workflow additions: company conditions, escalation to a role or people, and record approval workflows.</summary>
public sealed class ApprovalChainTests
{
    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero); // A Monday.

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ApprovalSubject Requisition(Guid projectId, Guid? companyId = null) =>
        new(ApprovalDocumentTypes.Requisition, Guid.NewGuid(), "Requisition R-1", Dev, "purchasing/requisitions", 500m, SubmitterEmployeeId: null,
            Charges: [new ApprovalCharge(projectId, 500m)], CompanyId: companyId);

    [Fact]
    public async Task A_company_condition_applies_a_step_only_to_that_companys_documents()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var other = Guid.NewGuid();
        await kit.Host.Get<IApprovalWorkflowService>().SaveAsync(ApprovalDocumentTypes.Requisition, new SaveApprovalWorkflowRequest("By company", new ApprovalWorkflowDefinition([
            new("Project managers", 1, new(ApproverRuleKind.ProjectManager)),
            new("Subsidiary finance", 2, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance), Conditions: [new(ApprovalConditionKind.CompanyIn, Ids: [other])]),
        ])));
        var engine = kit.Host.Get<ApprovalEngine>();
        await using var db = await kit.Factory.CreateAsync();

        var main = await engine.StartAsync(db, Requisition(kit.ProjectId, Company.DefaultId), default, persist: false);
        Assert.Equal(["Project managers"], main.Tasks.Select(t => t.StepName).ToArray());
        var subsidiary = await engine.StartAsync(db, Requisition(kit.ProjectId, other), default, persist: false);
        Assert.Equal(["Project managers", "Subsidiary finance"], subsidiary.Tasks.Select(t => t.StepName).ToArray());

        // Without a company on the document, the charged project's company counts.
        var byProject = await engine.StartAsync(db, Requisition(kit.ProjectId) with { }, default, persist: false);
        Assert.Single(byProject.Tasks);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Host.Get<IApprovalWorkflowService>().SaveAsync(ApprovalDocumentTypes.Requisition,
            new SaveApprovalWorkflowRequest("Bad", new ApprovalWorkflowDefinition([new("X", 1, new(ApproverRuleKind.Supervisor), Conditions: [new(ApprovalConditionKind.CompanyIn)])]))));
    }

    [Fact]
    public async Task A_step_escalates_after_n_business_days_to_a_role_that_can_then_decide()
    {
        var clock = new MovableClock();
        await using var kit = await AutomationKit.CreateAsync(s => s.AddSingleton<TimeProvider>(clock));
        await kit.Host.Get<IApprovalWorkflowService>().SaveAsync(ApprovalDocumentTypes.Requisition, new SaveApprovalWorkflowRequest("Escalating", new ApprovalWorkflowDefinition([
            new("Project managers", 1, new(ApproverRuleKind.ProjectManager), RemindAfterBusinessDays: 2, EscalateTo: new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance)),
        ])));
        var engine = kit.Host.Get<ApprovalEngine>();
        var subject = Requisition(kit.ProjectId);
        await using (var db = await kit.Factory.CreateAsync())
        {
            await engine.StartAsync(db, subject, default);
            await db.SaveChangesAsync();
        }

        async Task<bool> CanDecideAsync(string who)
        {
            await using var db = await kit.Factory.CreateAsync();
            return (await engine.CheckAsync(db, subject.DocumentType, subject.DocumentId, who, null, default)).CanDecide;
        }

        Assert.True(await CanDecideAsync(Pm));
        Assert.False(await CanDecideAsync(Finance));
        var reminders = kit.Host.Get<ApprovalReminderCheck>();
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(0, await reminders.RunAsync());
        Assert.False(await CanDecideAsync(Finance));

        clock.Now = clock.Now.AddDays(2); // Thursday: two business days waiting.
        Assert.True(await reminders.RunAsync() >= 2); // The reminder and the escalation.
        Assert.True(await CanDecideAsync(Finance));
        Assert.True(await CanDecideAsync(Pm));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Contains(await db.Set<ApprovalAction>().Select(a => a.Action).ToListAsync(), a => a == "Escalated");
        }

        // Escalation needs the reminder days, and an org role or people to go to.
        var workflows = kit.Host.Get<IApprovalWorkflowService>();
        await Assert.ThrowsAsync<ArgumentException>(() => workflows.SaveAsync(ApprovalDocumentTypes.Requisition, new SaveApprovalWorkflowRequest("Bad",
            new ApprovalWorkflowDefinition([new("X", 1, new(ApproverRuleKind.Supervisor), EscalateTo: new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance))]))));
        await Assert.ThrowsAsync<ArgumentException>(() => workflows.SaveAsync(ApprovalDocumentTypes.Requisition, new SaveApprovalWorkflowRequest("Bad",
            new ApprovalWorkflowDefinition([new("X", 1, new(ApproverRuleKind.Supervisor), RemindAfterBusinessDays: 1, EscalateTo: new(ApproverRuleKind.ProjectManager))]))));
    }

    [Fact]
    public async Task Record_approval_workflows_are_listed_edited_and_versioned_next_to_the_built_in_ones()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var workflows = kit.Host.Get<IApprovalWorkflowService>();
        var before = await workflows.ListAsync();
        Assert.Equal(ApprovalDocumentTypes.All, before.Select(w => w.DocumentType).ToArray()); // Every built-in type, defaults untouched.

        var created = await workflows.CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Contract change review"));
        var second = await workflows.CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Contract change review"));
        Assert.Equal("custom-contract-change-review", created.DocumentType);
        Assert.Equal("custom-contract-change-review-2", second.DocumentType);
        Assert.Equal("Contract change review", created.DocumentLabel);
        Assert.Equal(ApproverRuleKind.OrgRole, Assert.Single(created.Definition.Steps).Approver.Kind);

        var list = await workflows.ListAsync();
        Assert.Equal(ApprovalDocumentTypes.All.Count + 2, list.Count);
        Assert.Contains(list, w => w.DocumentType == created.DocumentType && w.DocumentLabel == "Contract change review");

        var saved = await workflows.SaveAsync(created.DocumentType, new SaveApprovalWorkflowRequest("Contract change review", new ApprovalWorkflowDefinition([
            new("Contracts", 1, new(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance)),
            new("Executives", 1, new(ApproverRuleKind.AnyOf, People: [Pm, Admin])),
        ])));
        Assert.Equal(2, saved.Definition.Steps.Count);
        Assert.True(saved.Version > created.Version);

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => workflows.GetAsync("custom-nope"));
        kit.As(Finance);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => workflows.CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Mine")));
    }
}
