using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Financials;
using Workbench.Contracts.MyTodo;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// Ceiling-first planning: Finance creates the project from the contract and hands it to the PM; the plan's rows are the
/// WBS and its lowest rows the charge codes; the plan's dollars are measured against the ceiling and funding; the PM
/// submits and Finance approves (the baseline budget) or returns it. Today is Wed Oct 14 2026; on 1001 Design runs
/// Oct 12–23 and Build Oct 19–30 under Phase 1, on contract C1 ($1,000,000 ceiling, $500,000 funded). Dana costs $60/h.
/// </summary>
public sealed class ProjectPlanTests
{
    private static readonly DateOnly Oct19 = new(2026, 10, 19);

    /// <summary>Sets the project's ceiling below any plan, so a submitted plan goes to Finance instead of approving itself.</summary>
    internal static Task OverBudgetAsync(ResourcingHost host, Guid projectId) =>
        host.As(Finance).Get<IProjectPlanService>().SaveTermsAsync(projectId, new SaveProjectTermsRequest(Ceiling: 100m, Funded: 50m));

    // ---- Finance to PM ----

    [Fact]
    public async Task Finance_creates_a_project_from_the_contract_with_its_ceiling_and_hands_it_to_the_pm()
    {
        await using var host = await CreateAsync();
        var contract = await ContractAsync(host);
        await AddClinsAsync(host, contract, ("0001", "Engineering", 600_000m, 300_000m, 0.08m), ("0002", "Travel", 400_000m, 200_000m, null));
        await using (var db = await host.Db.CreateAsync())
        {
            // Finance works in its own workspace; the PM isn't in it.
            var workspace = new Workspace { Name = "Finance" };
            db.Workspaces.Add(workspace);
            db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Finance, Role = WorkspaceRole.Owner });
            await db.SaveChangesAsync();
        }

        // The form starts from what the contract says.
        var prefill = await host.As(Finance).Get<IProjectPlanService>().TermsFromContractAsync(contract);
        Assert.Equal((1_000_000m, 500_000m, (DateOnly?)new DateOnly(2024, 1, 1)), (prefill.Ceiling!.Value, prefill.Funded!.Value, prefill.Start));
        Assert.Equal(0.048m, prefill.FeeRate); // 8% on the $600,000 CLIN, weighted by ceiling
        Assert.Equal(["1001"], prefill.SharedWith);

        var project = await host.Get<IProjectStartService>().StartAsync(new StartProjectRequest("1002", "Data platform", contract, ProjectStartFrom.Contract,
            ProjectManagerId: host.PmId, Terms: new SaveProjectTermsRequest(Ceiling: 250_000m, Funded: 100_000m, FeeRate: 0.08m)));

        Assert.Equal(host.PmId, project.ProjectManagerId);
        var wbs = await host.Get<IProjectService>().ListWbsAsync(project.Id);
        Assert.Equal(["1002.01 CLIN 0001 Engineering", "1002.02 CLIN 0002 Travel"], wbs.OrderBy(w => w.Code).Select(w => $"{w.Code} {w.Name}"));
        var plan = await host.Get<IProjectPlanService>().GetAsync(project.Id);
        Assert.Equal((250_000m, false, 100_000m, false, 0.08m), (plan.Terms.Ceiling!.Value, plan.Terms.CeilingFromContract, plan.Terms.Funded!.Value, plan.Terms.FundedFromContract, plan.Terms.FeeRate));
        Assert.Equal((PlanApprovalStatus.Draft, true), (plan.Status, plan.CanEditTerms));

        // The PM is told, and it's on their list.
        await using (var db = await host.Db.CreateAsync())
        {
            var note = await db.Set<Notification>().SingleAsync(n => n.RecipientEmail == Pm);
            Assert.Equal(($"Plan project 1002 Data platform", $"projects/{project.Id}/plan"), (note.Title, note.Link));
            Assert.Contains("contract C1 with a $250,000 ceiling", note.Body, StringComparison.Ordinal);
        }

        // The PM can edit the plan Finance's workspace holds, and builds the WBS there.
        var row = await host.As(Pm).Get<IItemService>().CreateAsync(project.SheetId!.Value, new CreateItemRequest("Requirements", (await host.Get<IItemService>().ListAsync(project.SheetId.Value)).First().Id));
        Assert.Equal("1002.01.01", row.CostpointWbsId);

        var todo = await host.As(Pm).Get<IMyTodoService>().GetAsync();
        var item = Assert.Single(todo.Items, i => i.Key == $"plan:{project.Id}:plan");
        Assert.Equal("Plan 1002 Data platform", item.Title);
        Assert.DoesNotContain(todo.Items, i => i.Title.StartsWith("Assign people to 1002", StringComparison.Ordinal)); // one to-do, not two
    }

    // ---- The plan's rows are the WBS ----

    [Fact]
    public async Task Rows_become_charge_codes_as_they_are_added_and_only_the_lowest_take_time()
    {
        await using var host = await CreateAsync();
        var items = host.As(Pm).Get<IItemService>();
        var phase = await ItemAsync(host, "Phase 1");
        var engineering = await items.CreateAsync(host.SheetId, new CreateItemRequest("Engineering", phase, 2));
        var requirements = await items.CreateAsync(host.SheetId, new CreateItemRequest("Requirements", engineering.Id));
        var prototype = await items.CreateAsync(host.SheetId, new CreateItemRequest("Prototype", engineering.Id));
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Review", engineering.Id) { });

        var wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.ItemId ?? Guid.Empty);
        var e = wbs[engineering.Id];
        Assert.Equal(("1001.01.03", host.Root1, false), (e.Code, e.ParentId!.Value, e.IsChargeable)); // a summary
        Assert.Equal(("1001.01.03.01", e.Id, true), (wbs[requirements.Id].Code, wbs[requirements.Id].ParentId!.Value, wbs[requirements.Id].IsChargeable));
        Assert.Equal("1001.01.03.02", wbs[prototype.Id].Code);

        // Renaming a row renames its charge code; a leaf that gets subtasks stops taking time.
        await items.UpdateAsync(host.SheetId, prototype.Id, new UpdateItemRequest("Prototype and test", null, null, null, prototype.Values));
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Bench test", prototype.Id));
        wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.ItemId ?? Guid.Empty);
        Assert.Equal(("Prototype and test", false), (wbs[prototype.Id].Name, wbs[prototype.Id].IsChargeable));

        // People can be assigned to a new row straight away, and charge it (assignments authorize charging).
        var saved = await host.Get<IStaffingAssignmentService>().SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 16m, wbs[requirements.Id].Id, Oct19, Oct19.AddDays(4)));
        Assert.Equal("1001.01.03.01", saved.WbsCode);
    }

    [Fact]
    public async Task Indenting_and_outdenting_rows_moves_their_charge_codes_with_them()
    {
        // The e2e walkthrough: rows typed at the top level, then indented under the CLIN with Tab, stayed top-level
        // charge codes (and the CLIN row stayed chargeable) because the indent didn't run the WBS sync.
        await using var host = await CreateAsync();
        var items = host.As(Pm).Get<IItemService>();
        var schedule = host.As(Pm).Get<Workbench.Contracts.Scheduling.IPlanScheduleService>();
        var phase = await ItemAsync(host, "Phase 1");
        await AddClinsAsync(host, await ContractAsync(host), ("0001", "Base", 6_000m, 3_000m, null));
        Guid clin;
        await using (var db = await host.Db.CreateAsync())
        {
            clin = (await db.Set<Clin>().SingleAsync()).Id;
            (await db.Set<WbsElement>().SingleAsync(w => w.Id == host.Root1)).ClinId = clin; // Phase 1 is CLIN 0001
            await db.SaveChangesAsync();
        }

        var analysis = await items.CreateAsync(host.SheetId, new CreateItemRequest("Analysis", null));
        var migration = await items.CreateAsync(host.SheetId, new CreateItemRequest("Migration", null));
        var wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.ItemId ?? Guid.Empty);
        Assert.Null(wbs[analysis.Id].ParentId);

        await schedule.IndentAsync(host.SheetId, analysis.Id);
        await schedule.IndentAsync(host.SheetId, migration.Id);

        wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.ItemId ?? Guid.Empty);
        var parent = wbs[phase];
        Assert.False(parent.IsChargeable);
        Assert.Equal((parent.Id, true), (wbs[analysis.Id].ParentId!.Value, wbs[analysis.Id].IsChargeable));
        Assert.Equal(parent.Id, wbs[migration.Id].ParentId!.Value);
        Assert.StartsWith(parent.Code + ".", wbs[analysis.Id].Code);
        Assert.StartsWith(parent.Code + ".", wbs[migration.Id].Code);

        // Under the CLIN's row they bill to the CLIN (an invoice leaves out cost on a code with no CLIN), as does a row
        // added there directly.
        var review = await items.CreateAsync(host.SheetId, new CreateItemRequest("Review", phase));
        await using (var db = await host.Db.CreateAsync())
        {
            var clins = await db.Set<WbsElement>().Where(w => w.ItemId != null).ToDictionaryAsync(w => w.ItemId!.Value, w => w.ClinId);
            Assert.Equal((clin, clin, clin), (clins[analysis.Id], clins[migration.Id], clins[review.Id]));
        }

        await schedule.OutdentAsync(host.SheetId, migration.Id);
        wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.ItemId ?? Guid.Empty);
        Assert.NotEqual(parent.Id, wbs[migration.Id].ParentId);
        Assert.False(wbs[migration.Id].Code.StartsWith(parent.Code + ".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_charge_code_with_time_on_it_keeps_its_code_and_its_row_cant_be_deleted()
    {
        await using var host = await CreateAsync();
        var items = host.As(Pm).Get<IItemService>();
        var (phase, design, build) = (await ItemAsync(host, "Phase 1"), await ItemAsync(host, "Design"), await ItemAsync(host, "Build"));
        await host.AddApprovedTimeAsync(host.DevId, (host.Design, host.Se2, new DateOnly(2026, 10, 13), 8m));

        // A row inserted first: Build's code moves along, Design's stays (time is on it) and numbering works around it.
        var kickoff = await items.CreateAsync(host.SheetId, new CreateItemRequest("Kickoff", phase, 0));
        var wbs = (await host.Get<IProjectService>().ListWbsAsync(host.Project1)).ToDictionary(w => w.Id);
        Assert.Equal("1001.01.01", wbs[host.Design].Code);
        Assert.Equal("1001.01.03", wbs[host.Build].Code);
        Assert.Equal("1001.01.02", wbs.Values.Single(w => w.ItemId == kickoff.Id).Code);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => items.DeleteAsync(host.SheetId, design));
        Assert.Contains("1001.01.01 Design has time charged to it", ex.Message, StringComparison.Ordinal);
        Assert.Contains(await items.ListAsync(host.SheetId), i => i.Id == design);

        // Deleting the parent would take Design with it: refused too.
        await Assert.ThrowsAsync<ArgumentException>(() => items.DeleteAsync(host.SheetId, phase));

        // A row nobody used goes with its code; one with people on it is closed and kept.
        var kickoffElement = wbs.Values.Single(w => w.ItemId == kickoff.Id).Id;
        await items.DeleteAsync(host.SheetId, kickoff.Id);
        await host.Get<IStaffingAssignmentService>().SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 8m, host.Build));
        await items.DeleteAsync(host.SheetId, build);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<WbsElement>().AnyAsync(w => w.Id == kickoffElement));
        var closed = await db.Set<WbsElement>().SingleAsync(w => w.Id == host.Build);
        Assert.Equal((false, false, (Guid?)null), (closed.IsOpen, closed.IsChargeable, closed.ItemId));
        Assert.Contains(await db.Set<AuditEvent>().Where(a => a.EntityId == host.Build).Select(a => a.Action).ToListAsync(), a => a == "ClosedFromPlan");
    }

    // ---- The people grid ----

    [Fact]
    public async Task The_people_grid_saves_typed_and_pasted_rows_as_the_staffing_tabs_assignments()
    {
        await using var host = await CreateAsync();
        var plans = host.As(Pm).Get<IResourcePlanService>();

        // A typed row: person, task (a charge code), hours; the dates default to the task's.
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, WbsElementId: host.Build, EmployeeId: host.DevId, Hours: 40m)]);

        // Pasted rows from Excel: checked first (a bad one says why), then the valid ones are added in one go.
        var pasted = await host.Get<IStaffingAssignmentService>().PreviewPasteAsync(host.Project1,
            "Alex Rivera\t1001.01.01\t16\t2026-10-19\t2026-10-23\nNobody Real\t1001.01.02\t8\t2026-10-19\t2026-10-23");
        Assert.Equal((1, true), (pasted.ValidCount, pasted.HasErrors));
        Assert.Contains("Nobody Real", Assert.Single(pasted.Rows, r => !r.IsValid).Person, StringComparison.Ordinal);
        var request = pasted.Rows.Single(r => r.IsValid).Request!;
        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, WbsElementId: request.WbsElementId, EmployeeId: request.EmployeeId,
            Hours: request.TotalHours, LaborCategoryId: request.LaborCategoryId, From: request.From, To: request.To)]);

        var saved = (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments;
        Assert.Equal([("Alex Rivera", "1001.01.01", 16m), ("Dana Lee", "1001.01.02", 40m)], saved.Select(a => (a.EmployeeName, a.WbsCode!, a.TotalHours)));

        // Moving a row to another task is taking it off one and putting it on the other, in one step.
        var dana = saved.Single(a => a.EmployeeId == host.DevId);
        await plans.ApplyAsync(host.Project1,
        [
            new PlanChange(PlanChangeKind.RemovePerson, AssignmentId: dana.Id),
            new PlanChange(PlanChangeKind.SetPerson, WbsElementId: host.Design, EmployeeId: host.DevId, Hours: 40m),
        ]);
        Assert.Equal("1001.01.01", (await host.Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments.Single(a => a.EmployeeId == host.DevId).WbsCode);
    }

    // ---- Money against the ceiling ----

    [Fact]
    public async Task Planned_dollars_are_measured_against_funding_and_the_ceiling_for_the_project_and_each_clin()
    {
        await using var host = await CreateAsync();
        var contract = await ContractAsync(host);
        var (build, design) = (await ItemAsync(host, "Build"), await ItemAsync(host, "Design"));
        await host.As(Finance).Get<IProjectPlanService>().SaveTermsAsync(host.Project1, new SaveProjectTermsRequest(Ceiling: 10_000m, Funded: 5_000m, FeeRate: 0.10m));
        var plans = host.As(Pm).Get<IResourcePlanService>();

        await plans.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        var money = (await plans.GetAsync(host.Project1)).Totals.Money!;
        Assert.Equal((5_280m, 10_000m, 5_000m, 4_720m, PlanMoneyStatus.OverFunding), (money.Project.Planned, money.Project.Ceiling!.Value, money.Project.Funded!.Value, money.Project.Left!.Value, money.Project.Status));
        Assert.Equal("labor cost plus 10% fee", money.Basis);

        // Trying more hours shows the plan going past the ceiling before anything is saved.
        var tried = await plans.TryAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: design, EmployeeId: host.AnalystId, Hours: 160m)]);
        Assert.Equal((PlanMoneyStatus.OverCeiling, 14_080m), (tried.After.Money!.Project.Status, tried.After.Money.Project.Planned)); // + 160 h × $50 × 1.1

        // A CLIN mapped to the top of the WBS has its own ceiling and funding.
        await AddClinsAsync(host, contract, ("0001", "Base", 6_000m, 3_000m, null));
        await using (var db = await host.Db.CreateAsync())
        {
            (await db.Set<WbsElement>().SingleAsync(w => w.Id == host.Root1)).ClinId = (await db.Set<Clin>().SingleAsync()).Id;
            await db.SaveChangesAsync();
        }

        var line = Assert.Single((await plans.GetAsync(host.Project1)).Totals.Money!.Lines);
        Assert.Equal(("0001", 5_280m, 6_000m, 3_000m, PlanMoneyStatus.OverFunding), (line.ClinNumber, line.Planned, line.Ceiling!.Value, line.Funded!.Value, line.Status));

        // T&M work is priced at the contract's bill rates.
        await using (var db = await host.Db.CreateAsync())
        {
            db.Set<ContractLaborRate>().Add(new ContractLaborRate { ContractId = contract, LaborCategoryId = host.Se2, EffectiveFrom = new DateOnly(2024, 1, 1), HourlyBillRate = 100m });
            await db.SaveChangesAsync();
        }

        await host.As(Finance).Get<IProjectPlanService>().SaveTermsAsync(host.Project1, new SaveProjectTermsRequest(Ceiling: 10_000m, Funded: 5_000m, Pricing: ContractKind.TimeAndMaterials, FeeRate: 0.10m));
        money = (await host.As(Pm).Get<IResourcePlanService>().GetAsync(host.Project1)).Totals.Money!;
        Assert.Equal((8_000m, PlanMoneyStatus.OverFunding, "hours at the contract's bill rates"), (money.Project.Planned, money.Project.Status, money.Basis));

        // Those who may not see cost don't see the dollars.
        Assert.Null((await host.As(ResourceManager).Get<IResourcePlanService>().GetAsync(host.Project1)).Totals.Money);
    }

    [Fact]
    public async Task Terms_equal_to_the_contracts_are_kept_as_the_contracts()
    {
        await using var host = await CreateAsync();
        var plans = host.As(Finance).Get<IProjectPlanService>();

        var same = await plans.SaveTermsAsync(host.Project1, new SaveProjectTermsRequest(Ceiling: 1_000_000m, Funded: 500_000m));
        Assert.Equal((true, true), (same.Terms.CeilingFromContract, same.Terms.FundedFromContract));
        await using (var db = await host.Db.CreateAsync())
        {
            var row = await db.Set<ProjectPlan>().SingleAsync();
            Assert.Equal(((decimal?)null, (decimal?)null), (row.CeilingValue, row.FundedValue));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => plans.SaveTermsAsync(host.Project1, new SaveProjectTermsRequest(Ceiling: 100m, Funded: 200m)));
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm).Get<IProjectPlanService>().SaveTermsAsync(host.Project1, new SaveProjectTermsRequest(Ceiling: 5m)));
    }

    // ---- Submit, approve, return ----

    [Fact]
    public async Task A_first_plan_within_the_ceiling_approves_itself_and_opens_the_project_without_finance()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);

        var submitted = await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1, "Ready");

        Assert.Equal((PlanApprovalStatus.Approved, 1, "Workbench"), (submitted.Status, submitted.Approvals, submitted.ReviewedBy));
        var versions = await host.Get<IBudgetService>().ListVersionsAsync(host.Project1);
        Assert.True(Assert.Single(versions, v => v.Kind == BudgetVersionKind.Baseline).IsLocked);
        Assert.Single(versions, v => v.Kind == BudgetVersionKind.Current);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<Notification>().AnyAsync(n => n.RecipientEmail == Finance && n.Title == "Review the plan for 1001 Modernization"));
        Assert.Empty((await host.As(Finance).Get<IApprovalInboxService>().ListMineAsync()).Where(i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan));
    }

    [Fact]
    public async Task With_automatic_approval_turned_off_every_plan_goes_to_finance()
    {
        await using var host = await CreateAsync(services => services.Configure<Workbench.Application.Resourcing.ResourcingOptions>(o => o.AutoApprovePlansWithinBudget = false));
        var build = await ItemAsync(host, "Build");
        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);

        var submitted = await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);

        Assert.Equal(PlanApprovalStatus.Submitted, submitted.Status);
    }

    [Fact]
    public async Task A_first_plan_over_the_ceiling_still_goes_to_finance()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        await OverBudgetAsync(host, host.Project1);

        var submitted = await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);

        Assert.Equal(PlanApprovalStatus.Submitted, submitted.Status);
    }

    [Fact]
    public async Task Changes_to_an_approved_plan_approve_themselves_while_they_stay_within_its_hours_and_cost_and_go_to_finance_when_they_do_not()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        var resources = host.Get<IResourcePlanService>();
        await resources.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);
        var assignment = (await host.As(Pm).Get<IResourcePlanService>().GetAsync(host.Project1)).Task(build)!.People.Single().AssignmentId;

        // Fewer hours: still inside the approved budget.
        await resources.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: assignment, Hours: 60m)]);
        var smaller = await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);
        Assert.Equal((PlanApprovalStatus.Approved, 2, "Workbench"), (smaller.Status, smaller.Approvals, smaller.ReviewedBy));
        Assert.Equal(60m, Assert.Single(await host.Get<IBudgetService>().ListVersionsAsync(host.Project1), v => v.Kind == BudgetVersionKind.Current).TotalHours);

        // More hours than the approved budget holds: Finance decides.
        await resources.ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: assignment, Hours: 90m)]);
        var bigger = await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);
        Assert.Equal(PlanApprovalStatus.Submitted, bigger.Status);
    }

    [Fact]
    public async Task The_pm_submits_finance_returns_or_approves_and_the_approved_plan_is_the_baseline_budget()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        var pm = host.As(Pm).Get<IProjectPlanService>();

        var empty = await Assert.ThrowsAsync<ArgumentException>(() => pm.SubmitAsync(host.Project1));
        Assert.Contains("Assign people", empty.Message, StringComparison.Ordinal);

        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        await OverBudgetAsync(host, host.Project1);
        host.As(Pm);
        var submitted = await pm.SubmitAsync(host.Project1, "Ready for review");
        // Who submitted and reviewed reads as their name, not their sign-in email.
        Assert.Equal((PlanApprovalStatus.Submitted, Pm, "Pat", false, false), (submitted.Status, submitted.SubmittedBy, submitted.SubmittedByName, submitted.CanSubmit, submitted.CanReview));
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm).Get<IProjectPlanService>().ApproveAsync(host.Project1)); // not Finance
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.True(await db.Set<Notification>().AnyAsync(n => n.RecipientEmail == Finance && n.Title == "Review the plan for 1001 Modernization"));
        }

        // Finance finds it in the Approvals inbox with everything else (named, with the note), and once: not also as its own to-do.
        var review = Assert.Single(await host.As(Finance).Get<IApprovalInboxService>().ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan);
        Assert.Equal((host.Project1, "Pat", "Plan for 1001 Modernization"), (review.DocumentId, review.Submitter, review.Title));
        Assert.Contains("Ready for review", review.Summary, StringComparison.Ordinal);
        Assert.Equal(1, await host.As(Finance).Get<IApprovalInboxService>().CountMineAsync()); // and in the Approvals badge
        Assert.DoesNotContain((await host.As(Finance).Get<IMyTodoService>().GetAsync()).Items, i => i.Key == $"plan:{host.Project1}:review");
        Assert.Empty((await host.As(Pm).Get<IApprovalInboxService>().ListMineAsync()).Where(i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan)); // not the person who submitted it, and not someone who can't review it

        // Returned with comments; the PM sees why and resubmits.
        var finance = host.As(Finance).Get<IProjectPlanService>();
        Assert.True((await finance.GetAsync(host.Project1)).CanReview);
        await Assert.ThrowsAsync<ArgumentException>(() => finance.ReturnAsync(host.Project1, " "));
        var returned = await finance.ReturnAsync(host.Project1, "Add a QA person");
        Assert.Equal((PlanApprovalStatus.Returned, "Add a QA person"), (returned.Status, returned.ReviewComment));
        var todo = Assert.Single((await host.As(Pm).Get<IMyTodoService>().GetAsync()).Items, i => i.Key.StartsWith($"plan:{host.Project1}:returned", StringComparison.Ordinal));
        Assert.Contains("Add a QA person", todo.Reason, StringComparison.Ordinal);
        await OverBudgetAsync(host, host.Project1);
        await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);

        // Approved: the locked Baseline and the Current working budget are the plan's hours.
        var approved = await host.As(Finance).Get<IProjectPlanService>().ApproveAsync(host.Project1, "Looks right");
        Assert.Equal((PlanApprovalStatus.Approved, 1, false, "Fin"), (approved.Status, approved.Approvals, approved.ChangedSinceApproval, approved.ReviewedByName));
        var versions = await host.Get<IBudgetService>().ListVersionsAsync(host.Project1);
        var baseline = Assert.Single(versions, v => v.Kind == BudgetVersionKind.Baseline);
        var current = Assert.Single(versions, v => v.Kind == BudgetVersionKind.Current);
        Assert.Equal((true, 80m, approved.BaselineVersionId), (baseline.IsLocked, baseline.TotalHours, (Guid?)baseline.Id));
        Assert.Equal((false, 80m), (current.IsLocked, current.TotalHours));
        var line = Assert.Single((await host.Get<IBudgetService>().GetVersionAsync(baseline.Id))!.Lines);
        Assert.Equal((host.Build, (Guid?)host.Se2, 80m), (line.WbsElementId, line.LaborCategoryId, line.Hours));

        // The plan keeps changing in the working version; Finance sees the change against the baseline.
        var dana = (await host.As(Pm).Get<IResourcePlanService>().GetAsync(host.Project1)).Task(build)!.People.Single();
        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, AssignmentId: dana.AssignmentId, Hours: 100m)]);
        var changed = await host.As(Finance).Get<IProjectPlanService>().GetAsync(host.Project1);
        Assert.True(changed.ChangedSinceApproval);
        var se2 = Assert.Single(changed.SinceBaseline);
        Assert.Equal((80m, 100m, 20m), (se2.BaselineHours, se2.PlanHours, se2.HoursChange));
        Assert.True((await host.As(Pm).Get<IProjectPlanService>().GetAsync(host.Project1)).CanSubmit);

        // The PM's to-do is to send the changes, not two staffing gaps against a budget that is the plan.
        var afterApproval = (await host.As(Pm).Get<IMyTodoService>().GetAsync()).Items;
        Assert.Contains(afterApproval, i => i.Key == $"plan:{host.Project1}:changes");
        Assert.DoesNotContain(afterApproval, i => i.Key.StartsWith($"staffing:{host.Project1}:over:", StringComparison.Ordinal) || i.Key.StartsWith($"staffing:{host.Project1}:unstaffed", StringComparison.Ordinal));

        await OverBudgetAsync(host, host.Project1);
        await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1, "Dana needs 20 more hours");
        var second = await host.As(Finance).Get<IProjectPlanService>().ApproveAsync(host.Project1);
        Assert.Equal(2, second.Approvals);
        versions = await host.Get<IBudgetService>().ListVersionsAsync(host.Project1);
        Assert.Equal((80m, 100m), (versions.Single(v => v.Kind == BudgetVersionKind.Baseline).TotalHours, versions.Single(v => v.Kind == BudgetVersionKind.Current).TotalHours));
    }

    [Fact]
    public async Task Finance_returns_and_approves_a_plan_from_the_approvals_inbox()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 80m)]);
        await OverBudgetAsync(host, host.Project1);
        await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1, "Ready");

        // Rejecting in the inbox sends it back to the PM with the reason (a plan is returned, not thrown out).
        var inbox = host.As(Finance).Get<IApprovalInboxService>();
        await Assert.ThrowsAsync<ArgumentException>(() => inbox.DecideAsync(ApprovalDocumentTypes.ProjectPlan, host.Project1, new ApprovalDecisionRequest(ApprovalDecisionKind.Reject, " ")));
        await inbox.DecideAsync(ApprovalDocumentTypes.ProjectPlan, host.Project1, new ApprovalDecisionRequest(ApprovalDecisionKind.Reject, "Add QA"));
        var returned = await host.As(Finance).Get<IProjectPlanService>().GetAsync(host.Project1);
        Assert.Equal((PlanApprovalStatus.Returned, "Add QA"), (returned.Status, returned.ReviewComment));
        Assert.Empty((await host.As(Finance).Get<IApprovalInboxService>().ListMineAsync()).Where(i => i.DocumentType == ApprovalDocumentTypes.ProjectPlan));

        await OverBudgetAsync(host, host.Project1);
        await host.As(Pm).Get<IProjectPlanService>().SubmitAsync(host.Project1);
        await host.As(Finance).Get<IApprovalInboxService>().DecideAsync(ApprovalDocumentTypes.ProjectPlan, host.Project1, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve, "Good"));
        Assert.Equal(PlanApprovalStatus.Approved, (await host.As(Finance).Get<IProjectPlanService>().GetAsync(host.Project1)).Status);
    }

    [Fact]
    public async Task Finance_doesnt_approve_a_plan_they_submitted_and_others_cant_see_the_plan()
    {
        await using var host = await CreateAsync();
        var build = await ItemAsync(host, "Build");
        await host.As(Pm).Get<IResourcePlanService>().ApplyAsync(host.Project1, [new PlanChange(PlanChangeKind.SetPerson, ItemId: build, EmployeeId: host.DevId, Hours: 8m)]);
        var finance = host.As(Finance).Get<IProjectPlanService>();
        await OverBudgetAsync(host, host.Project1);
        await finance.SubmitAsync(host.Project1);

        Assert.False((await finance.GetAsync(host.Project1)).CanReview);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => finance.ApproveAsync(host.Project1));
        Assert.Contains("someone else", ex.Message, StringComparison.Ordinal);

        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Analyst).Get<IProjectPlanService>().GetAsync(host.Project1));
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm2).Get<IProjectPlanService>().SubmitAsync(host.Project1));
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm2).Get<IProjectStartService>().StartAsync(new StartProjectRequest("1009", "x")));
    }

    // ---- Helpers ----

    private static async Task<Guid> ItemAsync(ResourcingHost host, string title)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Items.Where(i => i.SheetId == host.SheetId && i.Title == title).Select(i => i.Id).SingleAsync();
    }

    private static async Task<Guid> ContractAsync(ResourcingHost host)
    {
        await using var db = await host.Db.CreateAsync();
        return await db.Set<Contract>().Select(c => c.Id).SingleAsync();
    }

    private static async Task AddClinsAsync(ResourcingHost host, Guid contractId, params (string Number, string Description, decimal Ceiling, decimal Funded, decimal? Fee)[] clins)
    {
        await using var db = await host.Db.CreateAsync();
        var order = 0;
        foreach (var (number, description, ceiling, funded, fee) in clins)
        {
            db.Set<Clin>().Add(new Clin
            {
                ContractId = contractId, Number = number, Description = description, CeilingValue = ceiling, FundedValue = funded, FeeRate = fee,
                PeriodStart = new DateOnly(2024, 1, 1), PeriodEnd = new DateOnly(2029, 1, 1), Order = order++,
            });
        }

        await db.SaveChangesAsync();
    }
}
