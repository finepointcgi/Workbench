using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Approvals;
using Workbench.Tests.Billing;
using Workbench.Tests.Time;

namespace Workbench.Tests.Approvals;

/// <summary>
/// The engine over the timekeeping org: Pat supervises Dana, Sam has no supervisor and manages project 2002, Pat
/// manages 1001, fin@ is Finance, admin@ is Admin. Requisition-type subjects let the tests choose charges freely.
/// </summary>
public sealed class ApprovalEngineTests
{
    private const string Fin = "fin@example.com";
    private const string Pat = "pat@example.com";
    private const string Dana = "dana@example.com";
    private const string Sam = "sam@example.com";

    private sealed class Kit
    {
        public required TimeTestHost Host { get; init; }
        public required ApprovalEngine Engine { get; init; }
        public required Guid Project1001 { get; init; }
        public required Guid Project2002 { get; init; }

        public ApprovalWorkflowService Workflows => new(Host.Factory, Engine, new AuditRecorder(Host.User, Host.Clock), new AccessControl(Host.Factory, Host.User));

        public async Task PublishAsync(string type, params ApprovalStepDefinition[] steps)
        {
            var user = Host.User.Email;
            Host.User.Email = "admin@example.com";
            await Workflows.SaveAsync(type, new SaveApprovalWorkflowRequest("Test", new ApprovalWorkflowDefinition(steps)));
            Host.User.Email = user;
        }

        public ApprovalSubject Subject(string submitter = Dana, decimal amount = 100m, Guid? id = null, params ApprovalCharge[] charges) =>
            new(ApprovalDocumentTypes.Requisition, id ?? Guid.NewGuid(), "Test", submitter, "purchasing/requisitions", amount,
                Charges: charges.Length == 0 ? [new ApprovalCharge(Project1001, amount)] : charges, PolicyThreshold: 1000m);

        public async Task<ApprovalRoute> StartAsync(ApprovalSubject subject)
        {
            await using var db = await Host.Factory.CreateAsync();
            var route = await Engine.StartAsync(db, subject, default);
            await db.SaveChangesAsync();
            return route;
        }

        public async Task<ApprovalOutcome?> DecideAsync(ApprovalSubject subject, string actor, ApprovalDecisionKind kind = ApprovalDecisionKind.Approve)
        {
            await using var db = await Host.Factory.CreateAsync();
            var check = await Engine.CheckAsync(db, subject.DocumentType, subject.DocumentId, actor, null, default);
            if (!check.CanDecide)
            {
                return null;
            }

            var outcome = await Engine.DecideAsync(db, check, kind, actor, kind == ApprovalDecisionKind.Approve ? null : "No", default);
            await db.SaveChangesAsync();
            return outcome;
        }

        public async Task<List<ApprovalTask>> TasksAsync(ApprovalSubject subject)
        {
            await using var db = await Host.Factory.CreateAsync();
            var ids = await db.Set<ApprovalInstance>().Where(i => i.DocumentId == subject.DocumentId).Select(i => i.Id).ToListAsync();
            return await db.Set<ApprovalTask>().Where(t => ids.Contains(t.InstanceId)).OrderBy(t => t.Sequence).ThenBy(t => t.ScopeKey).ToListAsync();
        }
    }

    private static async Task<Kit> CreateAsync()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.GrantAsync(Fin, OrgRoles.Finance);
        var projects = await host.Projects.ListProjectsAsync();
        var p2002 = await host.Projects.CreateProjectAsync(new SaveProjectRequest("2002", "Second", null, host.Sam.Id, ProjectState.Active, null, null));
        return new Kit { Host = host, Engine = new ApprovalEngine(host.Clock), Project1001 = projects.Single(p => p.Code == "1001").Id, Project2002 = p2002.Id };
    }

    private static ApprovalStepDefinition Step(string name, int sequence, ApproverRule rule, params ApprovalCondition[] conditions) =>
        new(name, sequence, rule, Conditions: conditions);

    [Fact]
    public async Task Defaults_reproduce_the_old_requisition_routing_pm_then_finance_over_the_threshold()
    {
        var kit = await CreateAsync();

        var small = kit.Subject(amount: 500m);
        var route = await kit.StartAsync(small);
        Assert.Equal([Pat], route.NotifyEmails);
        Assert.Equal(Pat, route.ProjectApprover(kit.Project1001));
        Assert.DoesNotContain(route.Tasks, t => t.StepName == "Finance");
        Assert.True((await kit.DecideAsync(small, Pat))!.IsComplete);

        var large = kit.Subject(amount: 5000m);
        await kit.StartAsync(large);
        Assert.Null(await kit.DecideAsync(large, Fin)); // Finance waits for the PM.
        var afterPm = await kit.DecideAsync(large, Pat);
        Assert.False(afterPm!.IsComplete);
        Assert.Contains(Fin, afterPm.NotifyEmails);
        Assert.True((await kit.DecideAsync(large, Fin))!.IsComplete);
    }

    [Fact]
    public async Task Project_managers_approve_in_parallel_and_all_must_approve()
    {
        var kit = await CreateAsync();
        var subject = kit.Subject(amount: 300m, charges: [new ApprovalCharge(kit.Project1001, 100m), new ApprovalCharge(kit.Project2002, 200m)]);
        var route = await kit.StartAsync(subject);
        Assert.Equal(2, route.Active.Count());
        Assert.Equal(Sam, route.ProjectApprover(kit.Project2002));

        Assert.False((await kit.DecideAsync(subject, Sam))!.IsComplete);
        Assert.Null(await kit.DecideAsync(subject, Sam)); // Nothing more for Sam.
        Assert.True((await kit.DecideAsync(subject, Pat))!.IsComplete);
    }

    [Fact]
    public async Task Sequential_steps_wait_their_turn_and_conditions_pick_the_steps()
    {
        var kit = await CreateAsync();
        await kit.PublishAsync(ApprovalDocumentTypes.Requisition,
            Step("Supervisor", 1, new ApproverRule(ApproverRuleKind.Supervisor)),
            Step("Big ones", 2, new ApproverRule(ApproverRuleKind.AnyOf, People: [Sam, "Admin@Example.com"]), new ApprovalCondition(ApprovalConditionKind.AmountOver, 1000m)),
            Step("Unallowable", 3, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance), new ApprovalCondition(ApprovalConditionKind.HasUnallowableCost)));

        var small = kit.Subject(amount: 10m);
        var route = await kit.StartAsync(small);
        Assert.Single(route.Tasks);
        Assert.True((await kit.DecideAsync(small, Pat))!.IsComplete);

        var big = kit.Subject(amount: 5000m, charges: [new ApprovalCharge(kit.Project1001, 5000m, IsUnallowable: true)]);
        route = await kit.StartAsync(big);
        Assert.Equal([1, 2, 3], route.Tasks.Select(t => t.Sequence));
        Assert.Equal([ApprovalTaskStatus.Pending, ApprovalTaskStatus.Waiting, ApprovalTaskStatus.Waiting], route.Tasks.Select(t => t.Status));
        Assert.Null(await kit.DecideAsync(big, "admin@example.com"));
        await kit.DecideAsync(big, Pat);
        Assert.False((await kit.DecideAsync(big, "admin@example.com"))!.IsComplete); // Any one of the list.
        Assert.True((await kit.DecideAsync(big, Fin))!.IsComplete);
    }

    [Fact]
    public async Task Specific_people_each_approve_and_org_unit_managers_route_by_unit()
    {
        var kit = await CreateAsync();
        var unit = await kit.Host.People.CreateOrgUnitAsync(new CreateOrgUnitRequest("ENG", "Engineering"));
        kit.Host.User.Email = "admin@example.com";
        await kit.Workflows.SetOrgUnitApproverAsync(unit.Id, kit.Host.Sam.Id);
        await kit.PublishAsync(ApprovalDocumentTypes.Requisition,
            Step("Unit", 1, new ApproverRule(ApproverRuleKind.OrgUnitManager)),
            Step("Both", 2, new ApproverRule(ApproverRuleKind.SpecificPeople, People: [Pat, Fin])));

        var subject = kit.Subject(charges: [new ApprovalCharge(kit.Project1001, 100m, unit.Id)]);
        var route = await kit.StartAsync(subject);
        Assert.Equal(Sam, route.Active.Single().AssigneesCsv);
        Assert.Contains("ENG", route.Active.Single().ScopeLabel);
        await kit.DecideAsync(subject, Sam);
        Assert.False((await kit.DecideAsync(subject, Pat))!.IsComplete);
        Assert.True((await kit.DecideAsync(subject, Fin))!.IsComplete);
    }

    [Fact]
    public async Task Nobody_approves_their_own_document_the_step_is_skipped_or_rerouted()
    {
        var kit = await CreateAsync();

        // Default requisition: Pat requesting on Pat's own project skips the PM step; Finance approves instead.
        var own = kit.Subject(submitter: Pat, amount: 10m);
        var route = await kit.StartAsync(own);
        var pmTask = route.Tasks.Single(t => t.StepName == "Project managers");
        Assert.Equal(ApprovalTaskStatus.Skipped, pmTask.Status);
        Assert.Equal("The approver is the submitter", pmTask.SkipReason);
        Assert.Null(route.ProjectApprover(kit.Project1001));
        Assert.Null(await kit.DecideAsync(own, Pat));
        Assert.True((await kit.DecideAsync(own, Fin))!.IsComplete);

        // "Send it to the submitter's supervisor": Dana's supervisor is Pat; when Pat submits as PM, Pat's supervisor (none) leaves it unassigned.
        await kit.PublishAsync(ApprovalDocumentTypes.Requisition, new ApprovalStepDefinition("PM", 1, new ApproverRule(ApproverRuleKind.ProjectManager),
            WhenSubmitterIsApprover: SelfApprovalPolicy.SubmitterSupervisor));
        route = await kit.StartAsync(kit.Subject(submitter: Pat));
        Assert.True(route.HasUnassigned);

        // Finance submitting to Finance: excluded, others still can.
        await kit.Host.GrantAsync("fin2@example.com", OrgRoles.Finance);
        await kit.PublishAsync(ApprovalDocumentTypes.Requisition, Step("Finance", 1, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance)));
        var finance = kit.Subject(submitter: Fin);
        route = await kit.StartAsync(finance);
        Assert.DoesNotContain(Fin, route.NotifyEmails);
        Assert.Null(await kit.DecideAsync(finance, Fin));
        Assert.True((await kit.DecideAsync(finance, "fin2@example.com"))!.IsComplete);
    }

    [Fact]
    public async Task A_delegate_decides_on_behalf_of_the_approver_only_between_the_dates()
    {
        var kit = await CreateAsync();
        kit.Host.User.Email = Pat;
        var delegations = new ApprovalDelegationService(kit.Host.Factory, kit.Engine, new AccessControl(kit.Host.Factory, kit.Host.User), new AuditRecorder(kit.Host.User, kit.Host.Clock));
        var today = DateOnly.FromDateTime(kit.Host.Clock.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => delegations.CreateAsync(new SaveApprovalDelegationRequest(Pat, today, today)));
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() => delegations.CreateAsync(new SaveApprovalDelegationRequest(Sam, today, today, DelegatorEmail: Fin)));
        var later = await delegations.CreateAsync(new SaveApprovalDelegationRequest(Sam, today.AddDays(5), today.AddDays(9), Reason: "Vacation"));

        var subject = kit.Subject();
        await kit.StartAsync(subject);
        Assert.Null(await kit.DecideAsync(subject, Sam)); // Not yet in force.

        var now = await delegations.CreateAsync(new SaveApprovalDelegationRequest(Sam, today, today.AddDays(2), ApprovalDocumentTypes.Requisition));
        Assert.True(now.IsActive);
        kit.Host.User.Email = Sam;
        Assert.Equal(2, (await delegations.ListAsync()).Count);
        var outcome = await kit.DecideAsync(subject, Sam);
        Assert.Equal(Pat, outcome!.Decided.Single().OnBehalfOf);
        Assert.Equal(Sam, outcome.Decided.Single().DecidedBy);

        kit.Host.User.Email = Pat;
        Assert.NotNull((await delegations.RevokeAsync(later.Id)).RevokedAtUtc);
    }

    [Fact]
    public async Task Reminders_go_after_n_business_days_and_escalate_to_the_approvers_supervisor()
    {
        var kit = await CreateAsync();
        await kit.PublishAsync(ApprovalDocumentTypes.Requisition, new ApprovalStepDefinition("Pat's team", 1, new ApproverRule(ApproverRuleKind.AnyOf, People: [Dana]),
            RemindAfterBusinessDays: 2, EscalateToApproverSupervisor: true));
        var subject = kit.Subject(submitter: Sam);
        await kit.StartAsync(subject); // Wednesday.
        var publisher = new RecordingPublisher();
        var check = new ApprovalReminderCheck(kit.Host.Factory, kit.Engine, publisher);

        kit.Host.Clock.UtcNow = kit.Host.Clock.UtcNow.AddDays(1); // Thursday: one business day.
        Assert.Equal(0, await check.RunAsync());

        kit.Host.Clock.UtcNow = kit.Host.Clock.UtcNow.AddDays(1); // Friday: two.
        Assert.Equal(2, await check.RunAsync());
        Assert.Contains(publisher.Sent, s => s.Email == Dana && s.Kind == ApprovalReminderCheck.ReminderKind);
        Assert.Contains(publisher.Sent, s => s.Email == Pat && s.Kind == ApprovalReminderCheck.EscalationKind);
        Assert.Equal(0, await check.RunAsync()); // Idempotent.

        kit.Host.Clock.UtcNow = kit.Host.Clock.UtcNow.AddDays(3); // Monday: one business day since the last reminder.
        Assert.Equal(0, await check.RunAsync());
        kit.Host.Clock.UtcNow = kit.Host.Clock.UtcNow.AddDays(1);
        Assert.Equal(2, await check.RunAsync());
        var task = (await kit.TasksAsync(subject)).Single();
        Assert.Equal(2, task.RemindersSent);
    }

    [Fact]
    public async Task In_flight_documents_keep_their_workflow_version()
    {
        var kit = await CreateAsync();
        var before = kit.Subject(amount: 10m);
        await kit.StartAsync(before); // v1 (built-in): Pat.

        await kit.PublishAsync(ApprovalDocumentTypes.Requisition, Step("Finance only", 1, new ApproverRule(ApproverRuleKind.OrgRole, ApprovalOrgRole.Finance)));
        var after = kit.Subject(amount: 10m);
        await kit.StartAsync(after);

        Assert.Null(await kit.DecideAsync(before, Fin));
        Assert.True((await kit.DecideAsync(before, Pat))!.IsComplete);
        Assert.Null(await kit.DecideAsync(after, Pat));
        Assert.True((await kit.DecideAsync(after, Fin))!.IsComplete);

        kit.Host.User.Email = "admin@example.com";
        var workflow = await kit.Workflows.GetAsync(ApprovalDocumentTypes.Requisition);
        Assert.Equal(2, workflow.Version);
        Assert.Equal([2, 1], workflow.Versions.Select(v => v.Version));
        Assert.Equal("Project managers", (await kit.Workflows.GetVersionAsync(ApprovalDocumentTypes.Requisition, 1)).Steps[0].Name);
        Assert.Equal(3, (await kit.Workflows.ResetToDefaultAsync(ApprovalDocumentTypes.Requisition)).Version);
    }

    [Fact]
    public async Task Rejecting_ends_the_trip_and_resubmitting_starts_a_new_one()
    {
        var kit = await CreateAsync();
        var subject = kit.Subject(amount: 5000m);
        await kit.StartAsync(subject);
        var rejected = await kit.DecideAsync(subject, Pat, ApprovalDecisionKind.Reject);
        Assert.Equal(ApprovalInstanceStatus.Rejected, rejected!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.DecideAsync(subject, Fin)); // Nothing is waiting any more.

        await kit.StartAsync(subject);
        var tasks = await kit.TasksAsync(subject);
        Assert.Contains(tasks, t => t.Status == ApprovalTaskStatus.Rejected);
        Assert.Contains(tasks, t => t.Status == ApprovalTaskStatus.Cancelled); // The first trip's Finance step.
        Assert.Contains(tasks, t => t.Status == ApprovalTaskStatus.Pending);
    }

    [Fact]
    public async Task Definitions_are_validated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.FromResult(ApprovalWorkflowService.Validate(new ApprovalWorkflowDefinition([]))));
        Assert.Throws<ArgumentException>(() => ApprovalWorkflowService.Validate(new([Step("X", 1, new ApproverRule(ApproverRuleKind.AnyOf, People: ["not an email"]))])));
        Assert.Throws<ArgumentException>(() => ApprovalWorkflowService.Validate(new([Step("X", 1, new ApproverRule(ApproverRuleKind.OrgRole))])));
        Assert.Throws<ArgumentException>(() => ApprovalWorkflowService.Validate(new([Step("X", 1, new ApproverRule(ApproverRuleKind.Supervisor), new ApprovalCondition(ApprovalConditionKind.AmountOver))])));
        Assert.Throws<ArgumentException>(() => ApprovalWorkflowService.Validate(new([new ApprovalStepDefinition("X", 1, new ApproverRule(ApproverRuleKind.Supervisor), Fallback: new ApproverRule(ApproverRuleKind.ProjectManager))])));
        var ok = ApprovalWorkflowService.Validate(new([Step(" Two ", 2, new ApproverRule(ApproverRuleKind.AnyOf, People: [" A@Example.com "])), Step("One", 1, new ApproverRule(ApproverRuleKind.Supervisor))]));
        Assert.Equal(["One", "Two"], ok.Steps.Select(s => s.Name));
        Assert.Equal(["a@example.com"], ok.Steps[1].Approver.People);
    }

    [Fact]
    public async Task Only_admins_change_workflows_and_finance_can_read_them()
    {
        var kit = await CreateAsync();
        var access = new AccessControl(kit.Host.Factory, kit.Host.User);
        var service = new PermissionCheckingApprovalWorkflowService(kit.Workflows, access);
        kit.Host.User.Email = Dana;
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => service.ListAsync());
        kit.Host.User.Email = Fin;
        Assert.Equal(ApprovalDocumentTypes.All.Count, (await service.ListAsync()).Count);
        Assert.False((await service.GetAsync(ApprovalDocumentTypes.Timesheet)).CanEdit);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => service.ResetToDefaultAsync(ApprovalDocumentTypes.Timesheet));
        kit.Host.User.Email = "admin@example.com";
        Assert.Equal(2, (await service.ResetToDefaultAsync(ApprovalDocumentTypes.Timesheet)).Version);
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchNotFoundException>(() => service.GetAsync("nope"));
    }
}
