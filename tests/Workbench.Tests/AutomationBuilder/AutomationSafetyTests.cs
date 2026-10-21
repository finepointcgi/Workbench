using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Contracts;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Automations;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.AutomationBuilder.AutomationKit;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>Conditions and grouping, and the safety rails: containment, loop protection, rate limits, dry runs, versions, switches.</summary>
public sealed class AutomationSafetyTests
{
    private static AutomationCondition Field(string key, ConditionOperator op, string? value = null) => new(ConditionSubject.Field, op, key, value);

    [Theory]
    [InlineData(ConditionOperator.GreaterThan, "100.5", "75", true)]
    [InlineData(ConditionOperator.GreaterThan, "9", "75", false)]
    [InlineData(ConditionOperator.LessOrEqual, "2026-10-14", "2026-10-14", true)]
    [InlineData(ConditionOperator.LessThan, "2026-10-14", "2026-09-30", false)]
    [InlineData(ConditionOperator.Equals, "Submitted", "submitted", true)]
    [InlineData(ConditionOperator.NotEquals, "Draft", "Submitted", true)]
    [InlineData(ConditionOperator.Contains, "Server rack", "RACK", true)]
    [InlineData(ConditionOperator.StartsWith, "Server rack", "rack", false)]
    [InlineData(ConditionOperator.IsEmpty, "", null, true)]
    [InlineData(ConditionOperator.IsNotEmpty, null, null, false)]
    [InlineData(ConditionOperator.In, "Travel", "Materials, Travel", true)]
    [InlineData(ConditionOperator.NotIn, "Travel", "Materials", true)]
    public void Comparisons_treat_numbers_dates_and_text_sensibly(ConditionOperator op, string? actual, string? expected, bool result) =>
        Assert.Equal(result, AutomationConditions.Compare(op, actual, expected));

    [Fact]
    public async Task Condition_groups_combine_with_and_or_and_nest()
    {
        await using var kit = await AutomationKit.CreateAsync();
        async Task<AutomationRunDto> TestAsync(AutomationConditionGroup group) => await kit.Automations.DryRunAsync(new AutomationDryRunRequest(null,
            new AutomationDefinition(new AutomationTrigger(AutomationTriggerKind.Manual, "contract"), group, [new(AutomationActionKind.Notify, [Person(Finance)], "x")]),
            kit.ContractId));

        var match = await TestAsync(new(GroupLogic.All, [Field("ceilingValue", ConditionOperator.GreaterThan, "5000")],
            [new(GroupLogic.Any, [Field("contractNumber", ConditionOperator.Equals, "X9"), Field("name", ConditionOperator.Contains, "supp")])]));
        Assert.Contains(match.Steps, s => s.Outcome == AutomationStepOutcome.WouldDo);
        Assert.Equal(3, match.Conditions.Count);

        var noMatch = await TestAsync(new(GroupLogic.All, [Field("ceilingValue", ConditionOperator.GreaterThan, "5000")],
            [new(GroupLogic.Any, [Field("contractNumber", ConditionOperator.Equals, "X9"), Field("name", ConditionOperator.IsEmpty)])]));
        Assert.Empty(noMatch.Steps);
        Assert.Contains(noMatch.Conditions, c => c is { Description: "name is empty", Matched: false, Actual: "Support" });

        var company = await TestAsync(new(GroupLogic.Any, [new(ConditionSubject.Company, ConditionOperator.In, Values: [Company.DefaultId.ToString()])]));
        Assert.NotEmpty(company.Steps);
        var project = await TestAsync(new(GroupLogic.All, [new(ConditionSubject.Project, ConditionOperator.IsEmpty)]));
        Assert.NotEmpty(project.Steps); // A contract has no single project.
    }

    [Fact]
    public async Task Actor_role_conditions_check_who_made_the_change()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Finance-made", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Created]),
            [new(AutomationActionKind.Notify, [Person(Finance)], "x")], new(GroupLogic.All, [new(ConditionSubject.ActorRole, ConditionOperator.In, Values: ["Finance"])]));
        await kit.DevReportAsync("Kickoff");
        await kit.ProcessAsync();
        var run = Assert.Single(await kit.RunsAsync(automation.Id));
        Assert.Equal(nameof(AutomationRunStatus.ConditionsNotMet), run.Status);
        Assert.Contains("\"actual\":\"Employee\"", run.ConditionsJson);
    }

    [Fact]
    public async Task An_automation_never_does_more_than_its_principal_could()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var report = await kit.DevReportAsync(null);
        kit.As(Admin);

        // A service user without Finance can't set expense report fields, nor share a contract it can't see.
        var asViewer = await kit.CreateAsync("As viewer", new(AutomationTriggerKind.Manual, "expense-report"),
            [new(AutomationActionKind.SetField, FieldKey: "purpose", Value: "Hijacked"), new(AutomationActionKind.Notify, [Person(Finance)], "{record}")],
            runAs: AutomationRunAs.ServicePrincipal, principal: Viewer);
        var run = await kit.Automations.RunNowAsync(asViewer.Id, new AutomationRunNowRequest(report.Id));
        Assert.Equal(AutomationRunStatus.Failed, run.Status);
        Assert.All(run.Steps, s => Assert.Equal(AutomationStepOutcome.Denied, s.Outcome));
        Assert.Null(await kit.WithDbAsync(async db => (await db.Set<ExpenseReport>().FirstAsync(r => r.Id == report.Id)).Purpose));
        Assert.Empty(await kit.NotificationsAsync());

        // The actor's own access applies: dev@ may share their own report but not change it as Finance would.
        var asActor = await kit.CreateAsync("As actor", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Updated]),
            [new(AutomationActionKind.SetField, FieldKey: "purpose", Value: "x"), new(AutomationActionKind.Notify, [Person(Finance)], "{record} changed")]);
        kit.As(Dev);
        await kit.Host.Get<IExpenseReportService>().UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Trip", Day, Day.AddDays(1)));
        kit.As(Admin);
        await kit.ProcessAsync();
        var actorRun = Assert.Single(await kit.RunsAsync(asActor.Id));
        Assert.Equal(nameof(AutomationRunStatus.PartiallyFailed), actorRun.Status);
        Assert.Contains("\"outcome\":\"Denied\"", actorRun.StepsJson);

        // Finance-only fields read as "no access" for someone without Finance.
        var dryAsDev = await kit.CreateAsync("Funding check", new(AutomationTriggerKind.Manual, "contract"),
            [new(AutomationActionKind.Notify, [Person(Finance)], "x")], new(GroupLogic.All, [Field("fundedValue", ConditionOperator.GreaterThan, "0")]),
            AutomationRunAs.ServicePrincipal, Pm);
        var pmRun = await kit.Automations.RunNowAsync(dryAsDev.Id, new AutomationRunNowRequest(kit.ContractId));
        Assert.Equal(AutomationRunStatus.ConditionsNotMet, pmRun.Status);
        Assert.Equal("(no access)", Assert.Single(pmRun.Conditions).Actual);
    }

    [Fact]
    public async Task Only_permitted_people_manage_automations_or_choose_who_they_act_as()
    {
        await using var kit = await AutomationKit.CreateAsync();
        kit.As(Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Automations.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Automations.CreateAsync(new SaveAutomationRequest("x",
            new AutomationDefinition(new AutomationTrigger(AutomationTriggerKind.Manual), null, [new(AutomationActionKind.Notify, [Person(Finance)], "x")]))));

        kit.As(Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.CreateAsync("Ghost", new(AutomationTriggerKind.Manual),
            [new(AutomationActionKind.Notify, [Person(Finance)], "x")], runAs: AutomationRunAs.ServicePrincipal, principal: "nobody@example.com"));
    }

    [Fact]
    public async Task Loop_protection_stops_a_chain_that_comes_back_to_the_same_record()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var report = await kit.DevReportAsync("Trip");
        var loop = await kit.CreateAsync("Append", new(AutomationTriggerKind.FieldChanged, "expense-report", FieldKey: "purpose"),
            [new(AutomationActionKind.SetField, FieldKey: "purpose", Value: "{purpose}!")], runAs: AutomationRunAs.ServicePrincipal, principal: Finance);
        await kit.Host.Get<IExpenseReportService>().UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), "Go"));
        await kit.ProcessAsync();

        var runs = await kit.RunsAsync(loop.Id);
        Assert.Equal(2, runs.Count);
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), runs[0].Status);
        Assert.Equal(nameof(AutomationRunStatus.Skipped), runs[1].Status);
        Assert.Equal(1, runs[1].Depth);
        Assert.Equal(runs[0].Id, runs[1].ParentRunId);
        Assert.StartsWith("Loop protection", runs[1].Error);
        Assert.Equal("Go!", await kit.WithDbAsync(async db => (await db.Set<ExpenseReport>().FirstAsync(r => r.Id == report.Id)).Purpose));
    }

    [Fact]
    public async Task Loop_protection_limits_chain_depth_and_runs_per_record()
    {
        await using var kit = await AutomationKit.CreateAsync();
        await kit.Automations.SaveSettingsAsync(new SaveAutomationSettingsRequest(false, MaxChainDepth: 1, MaxRunsPerRecordPerHour: 2));
        var c = await kit.CreateAsync("C", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.Notify, [Person(Finance)], "c")]);
        var b = await kit.CreateAsync("B", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.StartAutomation, TargetAutomationId: c.Id)]);
        var a = await kit.CreateAsync("A", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.StartAutomation, TargetAutomationId: b.Id)]);
        await kit.Automations.RunNowAsync(a.Id, new AutomationRunNowRequest(kit.ContractId));
        await kit.ProcessAsync();
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), Assert.Single(await kit.RunsAsync(b.Id)).Status);
        var stopped = Assert.Single(await kit.RunsAsync(c.Id));
        Assert.Equal(nameof(AutomationRunStatus.Skipped), stopped.Status);
        Assert.Contains("2 levels deep", stopped.Error);

        await kit.Automations.RunNowAsync(c.Id, new AutomationRunNowRequest(kit.ContractId));
        await kit.Automations.RunNowAsync(c.Id, new AutomationRunNowRequest(kit.ContractId));
        var limited = await kit.Automations.RunNowAsync(c.Id, new AutomationRunNowRequest(kit.ContractId));
        Assert.Equal(AutomationRunStatus.Skipped, limited.Status);
        Assert.StartsWith("Rate limit", limited.Error);
    }

    [Fact]
    public async Task A_dry_run_shows_what_would_happen_and_changes_nothing()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var report = await kit.DevReportAsync(null, 50m);
        var automation = await kit.CreateAsync("Everything", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]),
        [
            new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.Supervisor)], "{record} for {total}"),
            new(AutomationActionKind.SetField, FieldKey: "purpose", Value: "Reviewed"),
            new(AutomationActionKind.CreateTask, [Person(Finance)], "Check {record}"),
        ], runAs: AutomationRunAs.ServicePrincipal, principal: Finance);

        kit.As(Admin);
        var run = await kit.Automations.DryRunAsync(new AutomationDryRunRequest(automation.Id, null, report.Id));
        Assert.Equal(AutomationRunStatus.DryRun, run.Status);
        Assert.Equal(3, run.Steps.Count);
        Assert.All(run.Steps, s => Assert.Equal(AutomationStepOutcome.WouldDo, s.Outcome));
        Assert.Contains("pm@example.com", run.Steps[0].Detail);
        Assert.Contains("Site visit for 50", run.Steps[0].Detail);
        Assert.Empty(await kit.NotificationsAsync());
        await kit.WithDbAsync(async db =>
        {
            Assert.Null((await db.Set<ExpenseReport>().FirstAsync(r => r.Id == report.Id)).Purpose);
            Assert.Empty(await db.Set<AutomationTask>().ToListAsync());
            Assert.True((await db.Set<AutomationRun>().SingleAsync()).IsDryRun);
            return 0;
        });

        // An unsaved draft can be tried too; it isn't logged.
        var draft = await kit.Automations.DryRunAsync(new AutomationDryRunRequest(null,
            new AutomationDefinition(new AutomationTrigger(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]), null,
                [new(AutomationActionKind.Block, Message: "No")]), report.Id));
        Assert.Contains("Would refuse", Assert.Single(draft.Steps).Detail);
    }

    [Fact]
    public async Task Editing_publishes_versions_and_every_change_is_audited()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var created = await kit.CreateAsync("Funding", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.Notify, [Person(Finance)], "v1")]);
        var service = kit.Automations;
        var v1Run = await service.RunNowAsync(created.Id, new AutomationRunNowRequest(kit.ContractId));
        var updated = await service.UpdateAsync(created.Id, new SaveAutomationRequest("Funding check",
            new AutomationDefinition(new AutomationTrigger(AutomationTriggerKind.Manual, "contract"), null, [new(AutomationActionKind.Notify, [Person(Pm)], "v2")]), Note: "Tell the PM"));
        Assert.Equal(2, updated.Version);
        Assert.Equal([2, 1], updated.Versions.Select(v => v.Version).ToArray());
        Assert.Equal("Tell the PM", updated.Versions[0].Note);
        Assert.Equal("v1", (await service.GetVersionAsync(created.Id, 1)).Actions[0].Title);
        var v2Run = await service.RunNowAsync(created.Id, new AutomationRunNowRequest(kit.ContractId));
        Assert.Equal((1, 2), (v1Run.Version, v2Run.Version));

        var restored = await service.RestoreVersionAsync(created.Id, 1);
        Assert.Equal(3, restored.Version);
        Assert.Equal("v1", restored.Definition.Actions[0].Title);
        await service.SetEnabledAsync(created.Id, false);
        await service.DeleteAsync(created.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.GetAsync(created.Id));

        var actions = await kit.WithDbAsync(async db => await db.Set<AuditEvent>().Where(a => a.EntityId == created.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal(new[] { "Created", "Deleted", "Disabled", "Restored", "Updated" }, actions.Order().ToArray());
        Assert.Equal(3, await kit.WithDbAsync(async db => await db.Set<AutomationVersion>().CountAsync(v => v.AutomationId == created.Id))); // History kept.
    }

    [Fact]
    public async Task Templates_that_need_no_further_choices_save_as_they_are()
    {
        await using var kit = await AutomationKit.CreateAsync();
        foreach (var template in AutomationTemplates.All.Where(t => t.Key is not ("large-requisition" or "invoice-to-teams")))
        {
            var created = await kit.Automations.CreateAsync(template.Request);
            Assert.Equal(template.Key, created.TemplateKey);
        }

        // These two need a workflow or connector picked in the builder first.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Automations.CreateAsync(AutomationTemplates.Find("large-requisition")!.Request));
        Assert.Equal(AutomationTemplates.All.Count - 2, (await kit.Automations.ListAsync()).Count);
    }

    [Fact]
    public async Task The_disable_switches_stop_runs()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("On create", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Created]),
            [new(AutomationActionKind.Notify, [Person(Finance)], "x")]);
        await kit.Automations.SetEnabledAsync(automation.Id, false);
        await kit.DevReportAsync("One");
        Assert.Equal(0, await kit.ProcessAsync());

        kit.As(Admin);
        await kit.Automations.SetEnabledAsync(automation.Id, true);
        await kit.Automations.SaveSettingsAsync(new SaveAutomationSettingsRequest(true, 5, 20));
        Assert.True((await kit.Automations.GetSettingsAsync()).PausedAll);
        await kit.DevReportAsync("Two");
        Assert.Equal(0, await kit.ProcessAsync());
        var three = await kit.DevReportAsync("Three");
        kit.As(Admin);
        Assert.Equal(AutomationRunStatus.Skipped, (await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(three.Id))).Status);
    }
}
