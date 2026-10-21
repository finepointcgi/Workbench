using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Application.Integrations;
using Workbench.Application.Time;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Automations;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.AutomationBuilder.AutomationKit;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>Each trigger type, fired by real module changes (expense reports, contracts, projects) and the scheduler.</summary>
public sealed class AutomationTriggerTests
{
    private static AutomationAction TellPm => new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.Supervisor)], "{record}: {status}");

    [Fact]
    public async Task Record_events_queue_runs_with_the_change_and_run_after_it_is_saved()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var created = await kit.CreateAsync("On create", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Created]), [TellPm]);
        var submitted = await kit.CreateAsync("On submit", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Submitted]), [TellPm]);
        var status = await kit.CreateAsync("On status", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.StatusChanged]), [TellPm]);

        var report = await kit.DevReportAsync("Kickoff", 50m);
        Assert.Equal(1, await kit.ProcessAsync());
        var run = Assert.Single(await kit.RunsAsync(created.Id));
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), run.Status);
        Assert.Equal(Dev, run.Actor);
        Assert.Equal(Dev, run.Principal);
        Assert.Equal(report.Id, run.RecordId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Pm && n.Title.StartsWith("Site visit"));

        await kit.Host.Get<IExpenseReportService>().SubmitAsync(report.Id);
        await kit.ProcessAsync();
        Assert.Single(await kit.RunsAsync(submitted.Id));
        Assert.Single(await kit.RunsAsync(status.Id));
        Assert.Single(await kit.RunsAsync(created.Id)); // Not fired again.
    }

    [Fact]
    public async Task Approved_and_rejected_events_fire_on_the_modules_decision()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var approved = await kit.CreateAsync("On approve", new(AutomationTriggerKind.RecordEvent, "expense-report", [RecordEventKind.Approved]),
            [new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.Employee)], "Approved: {record}")]);
        var report = await kit.DevReportAsync("Kickoff", 50m);
        await kit.Host.Get<IExpenseReportService>().SubmitAsync(report.Id);
        kit.As(Pm);
        await kit.Host.Get<IOdcApprovalService>().ApproveReportAsync(report.Id, null);
        await kit.ProcessAsync();

        var run = Assert.Single(await kit.RunsAsync(approved.Id));
        Assert.Equal(Pm, run.Actor);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Dev && n.Title == "Approved: Site visit");
    }

    [Fact]
    public async Task A_field_change_fires_only_for_the_chosen_from_and_to_values()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Purpose became conference",
            new(AutomationTriggerKind.FieldChanged, "expense-report", FieldKey: "purpose", FromValue: "Kickoff", ToValue: "Conference"), [TellPm]);
        var report = await kit.DevReportAsync("Kickoff");
        var expenses = kit.Host.Get<IExpenseReportService>();

        await expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), "Training"));
        await kit.ProcessAsync();
        Assert.Equal(nameof(AutomationRunStatus.ConditionsNotMet), Assert.Single(await kit.RunsAsync(automation.Id)).Status);

        await expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), "Kickoff"));
        await expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), "Conference"));
        await kit.ProcessAsync();
        var runs = await kit.RunsAsync(automation.Id);
        Assert.Equal(3, runs.Count);
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), runs[^1].Status);

        // A change to another field doesn't fire it at all.
        await expenses.UpdateReportAsync(report.Id, new SaveExpenseReportRequest("Renamed", Day, Day.AddDays(1), "Conference"));
        Assert.Equal(0, await kit.ProcessAsync());
    }

    [Fact]
    public async Task Integration_events_from_the_outbox_fire_their_automations()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("New project", new(AutomationTriggerKind.IntegrationEvent, EventKey: "project.created"),
            [new(AutomationActionKind.Notify, [Person(Finance)], "Project {code} created", "Status {status}")]);
        await kit.ProcessAsync(); // Starts reading the outbox from now.

        await kit.WithDbAsync(async db =>
        {
            var project = await db.Set<Project>().FirstAsync(p => p.Id == kit.ProjectId);
            IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.ProjectCreated, IntegrationEventPayloads.Project(project), Admin);
            return await db.SaveChangesAsync();
        });
        Assert.Equal(1, await kit.ProcessAsync());
        Assert.Equal(0, await kit.ProcessAsync()); // Read once.

        var run = Assert.Single(await kit.RunsAsync(automation.Id));
        Assert.Equal("project", run.EntityType);
        Assert.Equal(kit.ProjectId, run.RecordId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Finance && n.Title == "Project 1001 created");
    }

    [Fact]
    public async Task A_daily_schedule_runs_once_a_day_and_for_each_record_when_it_has_a_record_type()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var digest = await kit.CreateAsync("Daily digest", new(AutomationTriggerKind.Schedule, Frequency: ScheduleFrequency.Daily, Hour: 0),
            [new(AutomationActionKind.Notify, [Person(Finance)], "Good morning")]);
        var perProject = await kit.CreateAsync("Each project", new(AutomationTriggerKind.Schedule, "project", Frequency: ScheduleFrequency.Daily, Hour: 0),
            [new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.ProjectManager)], "Check {code}")]);
        var scheduler = kit.Host.Get<AutomationScheduler>();

        Assert.Equal(2, await scheduler.RunAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.RunAsync(CancellationToken.None));
        await kit.ProcessAsync();
        Assert.Equal(nameof(AutomationRunStatus.Succeeded), Assert.Single(await kit.RunsAsync(digest.Id)).Status);
        Assert.Equal(Admin, Assert.Single(await kit.RunsAsync(digest.Id)).Principal); // Schedules act as the person who published them.
        Assert.Equal(kit.ProjectId, Assert.Single(await kit.RunsAsync(perProject.Id)).RecordId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Pm && n.Title == "Check 1001");
    }

    [Fact]
    public async Task A_date_field_fires_the_set_number_of_days_before_the_date()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var today = kit.Host.Get<TimekeepingClock>().Today;
        await kit.WithDbAsync(async db =>
        {
            (await db.Set<Contract>().FirstAsync(c => c.Id == kit.ContractId)).PeriodEnd = today.AddDays(30);
            return await db.SaveChangesAsync();
        });
        var automation = await kit.CreateAsync("Closeout", new(AutomationTriggerKind.DateField, "contract", FieldKey: "periodEnd", OffsetDays: -30, Hour: 0),
            [new(AutomationActionKind.CreateTask, [new(RecipientKind.Relationship, AutomationRelationships.ProjectManager)], "Plan closeout of {contractNumber}", DueInDays: 10)]);
        var other = await kit.CreateAsync("Too early", new(AutomationTriggerKind.DateField, "contract", FieldKey: "periodEnd", OffsetDays: -60, Hour: 0),
            [new(AutomationActionKind.CreateTask, [Person(Pm)], "Nope")]);

        Assert.Equal(1, await kit.Host.Get<AutomationScheduler>().RunAsync(CancellationToken.None));
        await kit.ProcessAsync();
        Assert.Equal(kit.ContractId, Assert.Single(await kit.RunsAsync(automation.Id)).RecordId);
        Assert.Empty(await kit.RunsAsync(other.Id));
        kit.As(Pm);
        Assert.Equal("Plan closeout of C1", Assert.Single(await kit.Host.Get<IAutomationTaskService>().ListMineAsync()).Title);
    }

    [Fact]
    public async Task Funding_below_a_threshold_fires_once_and_rearms_when_funding_recovers()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Funding low", new(AutomationTriggerKind.Threshold, Threshold: ThresholdKind.ContractFundingBelow, Percent: 25m),
            [new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.ProjectManager)], "Funding under 25% on {contractNumber}", "{fundingRemainingPercent}% left")]);
        Assert.Equal("contract", automation.Definition.Trigger.EntityType);
        var scheduler = kit.Host.Get<AutomationScheduler>();

        Assert.Equal(1, await scheduler.RunAsync(CancellationToken.None));
        Assert.Equal(0, await scheduler.RunAsync(CancellationToken.None)); // Still under: no repeat.
        await kit.ProcessAsync();
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Pm && n.Title == "Funding under 25% on C1" && n.Body == "20% left");

        async Task FundAsync(decimal value) => await kit.WithDbAsync(async db =>
        {
            (await db.Set<Contract>().FirstAsync(c => c.Id == kit.ContractId)).FundedValue = value;
            return await db.SaveChangesAsync();
        });
        await FundAsync(10_000m);
        Assert.Equal(0, await scheduler.RunAsync(CancellationToken.None));
        await FundAsync(900m);
        Assert.Equal(1, await scheduler.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Budget_over_a_threshold_fires_for_the_project()
    {
        await using var kit = await AutomationKit.CreateAsync();
        await kit.WithDbAsync(async db =>
        {
            var version = new BudgetVersion { ProjectId = kit.ProjectId, Name = "Current", Kind = BudgetKind.Current, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow };
            db.Set<BudgetVersion>().Add(version);
            db.Set<BudgetLine>().Add(new BudgetLine { BudgetVersionId = version.Id, WbsElementId = kit.LeafId, PeriodStart = new DateOnly(2026, 10, 1), Amount = 1_000m });
            return await db.SaveChangesAsync();
        });
        var automation = await kit.CreateAsync("Budget", new(AutomationTriggerKind.Threshold, Threshold: ThresholdKind.ProjectBudgetOver, Percent: 75m),
            [new(AutomationActionKind.Notify, [Person(Finance)], "{code} used {budgetUsedPercent}%")]);
        Assert.Equal(1, await kit.Host.Get<AutomationScheduler>().RunAsync(CancellationToken.None));
        await kit.ProcessAsync();
        Assert.Equal(kit.ProjectId, Assert.Single(await kit.RunsAsync(automation.Id)).RecordId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.Title == "1001 used 80%");
    }

    [Fact]
    public async Task A_missing_timesheet_fires_per_employee_and_period()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Missing time", new(AutomationTriggerKind.Threshold, Threshold: ThresholdKind.TimesheetMissing, Days: 1),
            [new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.Employee)], "Timesheet missing")]);
        var scheduler = kit.Host.Get<AutomationScheduler>();
        Assert.True(await scheduler.RunAsync(CancellationToken.None) >= 1);
        Assert.Equal(0, await scheduler.RunAsync(CancellationToken.None));
        await kit.ProcessAsync();
        Assert.Contains(await kit.RunsAsync(automation.Id), r => r.EntityType == "employee" && r.RecordId == kit.PmId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Pm && n.Title == "Timesheet missing");
    }

    [Fact]
    public async Task An_approval_waiting_too_long_fires_for_the_document()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("Escalate", new(AutomationTriggerKind.Threshold, "expense-report", Threshold: ThresholdKind.ApprovalWaiting, Days: 0),
            [new(AutomationActionKind.Notify, [new(RecipientKind.Relationship, AutomationRelationships.Approvers)], "Still waiting: {record}")]);
        var report = await kit.DevReportAsync("Kickoff", 50m);
        await kit.Host.Get<IExpenseReportService>().SubmitAsync(report.Id);
        await kit.ProcessAsync();

        Assert.Equal(1, await kit.Host.Get<AutomationScheduler>().RunAsync(CancellationToken.None));
        Assert.Equal(0, await kit.Host.Get<AutomationScheduler>().RunAsync(CancellationToken.None));
        await kit.ProcessAsync();
        Assert.Equal(report.Id, Assert.Single(await kit.RunsAsync(automation.Id)).RecordId);
        Assert.Contains(await kit.NotificationsAsync(), n => n.RecipientEmail == Pm && n.Title == "Still waiting: Site visit");
    }

    [Fact]
    public async Task A_manual_automation_runs_on_a_chosen_record_when_someone_runs_it()
    {
        await using var kit = await AutomationKit.CreateAsync();
        var automation = await kit.CreateAsync("By hand", new(AutomationTriggerKind.Manual, "contract"), [new(AutomationActionKind.Notify, [Person(Finance)], "Look at {contractNumber}")]);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest()));
        var run = await kit.Automations.RunNowAsync(automation.Id, new AutomationRunNowRequest(kit.ContractId));
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        Assert.Equal("C1 Support", run.RecordLabel);
        Assert.Contains(await kit.NotificationsAsync(), n => n.Title == "Look at C1");
    }
}
