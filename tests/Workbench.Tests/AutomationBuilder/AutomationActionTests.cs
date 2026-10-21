using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Approvals;
using Workbench.Domain.Automations;
using Workbench.Domain.Integrations;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.AutomationBuilder.AutomationKit;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>Each action: notify, set a field, create a to-do, require an approval, post to chat, call a webhook, start another automation, block.</summary>
public sealed class AutomationActionTests
{
    [Fact]
    public async Task Notify_sends_templated_text_to_people_roles_fields_and_relationships()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Tell everyone", new(AutomationTriggerKind.Manual, "contract"),
        [
            new(AutomationActionKind.Notify,
                [Person("someone@example.com"), new(RecipientKind.Role, "Finance"), new(RecipientKind.Relationship, AutomationRelationships.ProjectManager)],
                "{contractNumber} has {fundingRemainingPercent}% left", "Funded {fundedValue}, incurred {incurredCost}. {link}"),
        ]);
        var run = await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(kit.ContractId));

        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        var sent = await kit.NotificationsAsync();
        Assert.Equal(["admin@example.com", "finance@example.com", "pm@example.com", "someone@example.com"], sent.Select(n => n.RecipientEmail).Order().ToArray());
        Assert.All(sent, n => Assert.Equal("C1 has 20% left", n.Title));
        Assert.Contains($"Funded 1,000, incurred 800. setup/contracts/{kit.ContractId}", sent[0].Body);
        Assert.Equal("20", run.Inputs["fundingRemainingPercent"]);
    }

    [Fact]
    public async Task Set_field_writes_through_the_catalog_and_audits_the_change()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var report = await kit.DevReportAsync(null);
        var automation = await kit.CreateAsync("Default purpose", new(AutomationTriggerKind.Manual, "expense-report"),
            [new(AutomationActionKind.SetField, FieldKey: "purpose", Value: "Set by {automation}")], runAs: AutomationRunAs.ServicePrincipal, principal: Finance);
        kit.As(Admin);
        var run = await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(report.Id));

        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        Assert.Equal(Finance, run.Principal);
        await kit.WithDbAsync(async db =>
        {
            Assert.Equal("Set by Default purpose", (await db.Set<ExpenseReport>().FirstAsync(r => r.Id == report.Id)).Purpose);
            Assert.Contains(await db.Set<AuditEvent>().Where(a => a.EntityId == report.Id).ToListAsync(), a => a.Action == "FieldSet" && a.AfterJson!.Contains("Set by"));
            return 0;
        });

        // Fields that modules own can't be set.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CreateAsync("Bad", new(AutomationTriggerKind.Manual, "expense-report"),
            [new(AutomationActionKind.SetField, FieldKey: "status", Value: "Approved")]));
    }

    [Fact]
    public async Task Create_task_gives_each_recipient_a_to_do_they_can_complete()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Review", new(AutomationTriggerKind.Manual, "project"),
            [new(AutomationActionKind.CreateTask, [new(RecipientKind.Field, "projectManagerId")], "Review {code}", "Look at the EAC.", DueInDays: 3)]);
        await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(kit.ProjectId));

        kit.As(Pm);
        var tasks = kit.Host.Get<IAutomationTaskService>();
        var task = Assert.Single(await tasks.ListMineAsync());
        Assert.Equal("Review 1001", task.Title);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), task.DueOn);
        Assert.Equal($"projects/{kit.ProjectId}", task.Link);
        var todo = await kit.Host.Get<IMyTodoService>().GetAsync();
        Assert.Contains(todo.Items, i => i.Title == "Review 1001");

        kit.As(Dev);
        await Assert.ThrowsAnyAsync<Exception>(() => tasks.CompleteAsync(task.Id));
        kit.As(Pm);
        Assert.NotNull((await tasks.CompleteAsync(task.Id)).CompletedAtUtc);
        Assert.Empty(await tasks.ListMineAsync());
    }

    [Fact]
    public async Task Require_approval_routes_the_record_through_a_record_workflow_and_the_decision_fires_approved()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var workflow = await kit.Host.Get<IApprovalWorkflowService>().CreateRecordWorkflowAsync(new CreateRecordApprovalWorkflowRequest("Large expense review"));
        Assert.Equal("custom-large-expense-review", workflow.DocumentType);
        var route = await kit.CreateAsync("Route it", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
            [new(AutomationActionKind.RequireApproval, WorkflowKey: workflow.DocumentType)]);
        var approved = await kit.CreateAsync("When approved", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Approved]),
            [new(AutomationActionKind.Notify, [Person(Finance)], "Reviewed: {record}")]);

        var report = await kit.DevReportAsync("Kickoff", 50m);
        await kit.Host.Get<IExpenseReportService>().SubmitAsync(report.Id);
        await kit.ProcessAsync();
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), Assert.Single(await kit.RunsAsync(route.Id)).Status);

        kit.As(Admin);
        var inbox = kit.Host.Get<IApprovalInboxService>();
        var item = Assert.Single(await inbox.ListMineAsync(), i => i.DocumentType == workflow.DocumentType);
        Assert.Equal(report.Id, item.DocumentId);
        await inbox.DecideAsync(workflow.DocumentType, report.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve));
        await kit.ProcessAsync();

        Assert.Single(await kit.RunsAsync(approved.Id));
        await kit.WithDbAsync(async db =>
        {
            Assert.Equal(nameof(ApprovalInstanceStatus.Approved), (await db.Set<AutomationApprovalRequest>().SingleAsync()).Outcome);
            Assert.Equal(ApprovalInstanceStatus.Approved, (await db.Set<ApprovalInstance>().SingleAsync(i => i.DocumentType == workflow.DocumentType)).Status);
            return 0;
        });
    }

    [Fact]
    public async Task Post_to_chat_and_call_webhook_queue_deliveries_through_the_integration_dispatcher()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var (connectorId, endpointId) = await kit.WithDbAsync(async db =>
        {
            var connector = new IntegrationConnector { Kind = "teams", Name = "Billing channel", SettingsJson = "{}", CreatedBy = Admin };
            var endpoint = new WebhookEndpoint { Name = "ERP bridge", Url = "https://hooks.example.com/wb", EventTypes = "", SecretProtected = "x", SecretHint = "abcd", CreatedBy = Admin };
            db.Set<IntegrationConnector>().Add(connector);
            db.Set<WebhookEndpoint>().Add(endpoint);
            await db.SaveChangesAsync();
            return (connector.Id, endpoint.Id);
        });
        var automation = await kit.CreateAsync("Out", new(AutomationTriggerKind.Manual, "contract"),
        [
            new(AutomationActionKind.PostToChat, Title: "Funding: {contractNumber}", Message: "{fundingRemainingPercent}% left", ConnectorId: connectorId),
            new(AutomationActionKind.CallWebhook, Title: "Contract {contractNumber}", WebhookEndpointId: endpointId),
        ]);
        var run = await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(kit.ContractId));

        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        await kit.WithDbAsync(async db =>
        {
            var deliveries = await db.Set<IntegrationDelivery>().ToListAsync();
            Assert.Contains(deliveries, d => d.ConnectorId == connectorId && d.EventType == AutomationExecutor.ChatEventType);
            Assert.Contains(deliveries, d => d.EndpointId == endpointId && d.EventType == AutomationExecutor.WebhookEventType);
            var webhook = await db.Set<IntegrationOutboxEvent>().SingleAsync(e => e.EventType == AutomationExecutor.WebhookEventType);
            Assert.Contains("\"contractNumber\":\"C1\"", webhook.PayloadJson);
            Assert.NotNull(webhook.DispatchedAtUtc); // Never fanned out to other subscribers.
            var chat = await db.Set<IntegrationOutboxEvent>().SingleAsync(e => e.EventType == AutomationExecutor.ChatEventType);
            Assert.Equal("Funding: C1", Workbench.Application.Integrations.Connectors.ChatMessages.Describe(chat, "https://wb.example.com").Title);
            return 0;
        });
    }

    [Fact]
    public async Task Start_automation_runs_the_other_one_a_level_deeper_as_the_same_principal()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var child = await kit.CreateAsync("Child", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.Notify, [Person(Finance)], "From the child")],
            runAs: AutomationRunAs.ServicePrincipal, principal: Finance);
        var parent = await kit.CreateAsync("Parent", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.StartAutomation, TargetAutomationId: child.Id)]);
        var run = await kit.Automations.RunNowAsync(parent.Id, new AutomationRunNowRequest(kit.ContractId));
        await kit.ProcessAsync();

        var childRun = Assert.Single(await kit.RunsAsync(child.Id));
        Assert.Equal(1, childRun.Depth);
        Assert.Equal(run.Id, childRun.ParentRunId);
        Assert.Equal(Admin, childRun.Principal); // The parent's principal, not the child's own service user.
        Assert.Equal(kit.ContractId, childRun.RecordId);

        // Only manual automations can be started.
        var evented = await kit.CreateAsync("Evented", new(AutomationTriggerKind.RecordEvent, "contract", [RecordEventKind.Updated]), [new(AutomationActionKind.Notify, [Person(Finance)], "x")]);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CreateAsync("Bad", new(AutomationTriggerKind.Manual), [new(AutomationActionKind.StartAutomation, TargetAutomationId: evented.Id)]));
    }

    [Fact]
    public async Task Block_refuses_the_transition_with_the_message_and_logs_it()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var rule = await kit.CreateAsync("Purpose required over $60", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
            [new(AutomationActionKind.Block, Message: "Reports over $60 need a purpose ({total} on {record}).")],
            new AutomationConditionGroup(GroupLogic.All, [
                new(ConditionSubject.Field, ConditionOperator.GreaterThan, "total", "60"),
                new(ConditionSubject.Field, ConditionOperator.IsEmpty, "purpose"),
            ]));
        var expenses = kit.Host.Get<IExpenseReportService>();

        var report = await kit.DevReportAsync(null, 50m, 20m);
        var refused = await Assert.ThrowsAsync<AutomationBlockedException>(() => expenses.SubmitAsync(report.Id));
        Assert.Equal("Reports over $60 need a purpose (70 on Site visit).", refused.Message);
        Assert.Equal(OdcApprovalState.Draft, (await expenses.GetReportAsync(report.Id)).Report.Status);
        var run = Assert.Single(await kit.RunsAsync(rule.Id));
        Assert.Equal(nameof(AutomationRunStatus.Blocked), run.Status);

        var small = await kit.DevReportAsync(null, 30m);
        Assert.Equal(OdcApprovalState.Submitted, (await expenses.SubmitAsync(small.Id)).Status); // Conditions don't match: allowed.

        await expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), "Customer kickoff"));
        Assert.Equal(OdcApprovalState.Submitted, (await expenses.SubmitAsync(report.Id)).Status);

        // Validation rules only make sense before the change and can't be mixed with other actions.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CreateAsync("Bad", new(AutomationTriggerKind.Schedule), [new(AutomationActionKind.Block, Message: "no")]));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CreateAsync("Bad", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
            [new(AutomationActionKind.Block, Message: "no"), new(AutomationActionKind.Notify, [Person(Finance)], "x")]));
    }
}
