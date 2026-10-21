using System.Text.Json;
using Workbench.Application;
using Workbench.Application.Assistant;
using Workbench.Application.Identity;
using Workbench.Contracts.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Assistant;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.Approvals;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Tasks;
using Workbench.Tests.Assistant;
using static Workbench.Tests.Identity.ProjectAccountingFixture;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// <see cref="Application.Identity.ProjectVisibility"/>: who may see a project at all, on every path project data is read.
/// Over <see cref="ProjectAccountingFixture"/>: pm@ manages 1001 (plan sheet = the Program Office "Schedule" sheet);
/// analyst@ charges 1001; viewer@ charges 2002 and is a member of the Program Office workspace; outsider@ charges 2002 only;
/// reviewer@ has no employee record but is a Program Office member.
/// </summary>
public sealed class ProjectVisibilityTests
{
    private const string ResourceManager = "rm@example.com";
    private const string BusinessDevelopment = "bd@example.com";
    private const string Pricing = "pricing@example.com";
    private const string Hr = "hr@example.com";
    private const string Approver = "approver@example.com";
    private const string Delegate = "delegate@example.com";

    [Theory]
    [InlineData(Finance, "1001,2002")]
    [InlineData(Admin, "1001,2002")]
    [InlineData(ResourceManager, "1001,2002")] // staffs work across every project
    [InlineData(Owner, "1001")] // the project manager
    [InlineData(Editor, "1001")] // charges time to 1001
    [InlineData(Viewer, "1001,2002")] // 1001: a member of its plan sheet's workspace; 2002: charges to it
    [InlineData(Outsider, "2002")]
    [InlineData(Commenter, "1001")] // no employee record, but a member of 1001's plan workspace
    [InlineData(Timekeeper, "")] // reviews every timesheet, but that's no project of theirs
    [InlineData(BusinessDevelopment, "")] // keeps the pipeline, gets no projects from the role
    [InlineData(Pricing, "")]
    [InlineData(Hr, "")]
    public async Task Each_role_lists_the_projects_the_rule_gives_it(string email, string expected)
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);
        await AddRoleUsersAsync(seeded);

        var host = seeded.As(email).Host;
        var codes = (await host.Get<IProjectService>().ListProjectsAsync()).Select(p => p.Code).Order(StringComparer.Ordinal);
        var hub = (await host.Get<IProjectHubService>().ListProjectsAsync()).Projects.Select(p => p.Code).Order(StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(",", codes));
        Assert.Equal(expected, string.Join(",", hub));
    }

    [Fact]
    public async Task A_project_the_employee_is_not_on_is_not_found_on_every_path()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var host = seeded.As(Outsider).Host;

        // The project list and pages (and the API, which calls the same services).
        var projects = host.Get<IProjectService>();
        Assert.Null(await projects.GetProjectAsync(f.Project1));
        Assert.Null(await projects.GetProjectForSheetAsync(seeded.SheetId));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => projects.ListWbsAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => projects.ListAssignmentsAsync(f.Wbs1));
        Assert.NotNull(await projects.GetProjectAsync(f.Project2)); // their own project still opens

        // The Projects hub and its tabs.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectHubService>().GetProjectAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectHubService>().GetTeamAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IBudgetService>().ListVersionsAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IOdcService>().ListProjectOdcsAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IResourcePlanningService>().GetProjectStaffingAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IResourceAllocationService>().ListAsync(new AllocationQuery(Day, Day, f.Project1)));

        // Reports: a hidden project as a parameter, and the report writer's project list.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IStandardReportService>().RunAsync(StandardReportKeys.LaborDistribution,
            new ReportParameters(Day, Day, ProjectId: f.Project1)));
        var wbs = await host.Get<IReportWriterService>().RunAsync(new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new ReportColumnSpec("project")]));
        Assert.Equal(["2002"], wbs.Rows.Where(r => r.Kind == ReportRowKind.Detail).Select(r => r.Values[0]).Distinct());
    }

    [Fact]
    public async Task Finance_and_admin_open_every_project()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        foreach (var email in new[] { Finance, Admin })
        {
            var host = seeded.As(email).Host;
            Assert.NotNull(await host.Get<IProjectService>().GetProjectAsync(f.Project1));
            Assert.NotNull(await host.Get<IProjectService>().GetProjectAsync(f.Project2));
            Assert.Equal(f.Project2, (await host.Get<IProjectHubService>().GetProjectAsync(f.Project2)).Project.Id);
            Assert.NotNull(await host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project2));
        }
    }

    [Fact]
    public async Task A_project_manager_sees_their_projects_and_not_found_for_others()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var host = seeded.As(Owner).Host;

        Assert.NotNull(await host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project1));
        Assert.Null(await host.Get<IProjectService>().GetProjectAsync(f.Project2));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project2));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectHubService>().GetProjectAsync(f.Project2));
    }

    [Fact]
    public async Task Someone_on_a_project_without_the_financials_rule_is_still_forbidden_its_money()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        // analyst@ sees 1001 (charges to it) but isn't its manager: forbidden, not hidden.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Editor).Host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project1));
    }

    [Fact]
    public async Task An_approver_and_their_delegate_see_the_projects_on_the_documents_they_approve()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<AppUser>().AddRange(new AppUser { Email = Approver, DisplayName = "Ann" }, new AppUser { Email = Delegate, DisplayName = "Dee" });
            var sheet = db.Set<Timesheet>().Single(t => t.EmployeeId == f.OutsiderId);
            var workflow = new ApprovalWorkflow { DocumentType = ApprovalDocumentTypes.Timesheet, Name = "Timesheets" };
            var version = new ApprovalWorkflowVersion { WorkflowId = workflow.Id, Version = 1, DefinitionJson = "{}", CreatedBy = Admin };
            db.Set<ApprovalWorkflow>().Add(workflow);
            db.Set<ApprovalWorkflowVersion>().Add(version);
            var instance = new ApprovalInstance
            {
                DocumentType = ApprovalDocumentTypes.Timesheet, DocumentId = sheet.Id, WorkflowVersionId = version.Id, SubmitterEmail = Outsider, Title = "Timesheet", Link = $"time/{sheet.Id}",
            };
            db.Set<ApprovalInstance>().Add(instance);
            db.Set<ApprovalTask>().AddRange(
                new ApprovalTask { InstanceId = instance.Id, StepName = "Supervisor", Status = ApprovalTaskStatus.Pending, AssigneesCsv = $"someone@example.com,{Approver}" },
                new ApprovalTask { InstanceId = instance.Id, StepName = "Program", Sequence = 1, Status = ApprovalTaskStatus.Waiting, AssigneesCsv = "later@example.com" });
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            db.Set<ApprovalDelegation>().Add(new ApprovalDelegation { DelegatorEmail = Approver, DelegateEmail = Delegate, StartsOn = today.AddDays(-1), EndsOn = today.AddDays(1), CreatedBy = Approver });
            await db.SaveChangesAsync();
        }

        foreach (var email in new[] { Approver, Delegate })
        {
            var host = seeded.As(email).Host;
            Assert.Equal(["2002"], (await host.Get<IProjectService>().ListProjectsAsync()).Select(p => p.Code));
            Assert.Equal(f.Project2, (await host.Get<IProjectHubService>().GetProjectAsync(f.Project2)).Project.Id);
            await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IProjectHubService>().GetProjectAsync(f.Project1));
        }

        // A task that hasn't reached them yet (a later step) puts nobody on the project.
        Assert.Empty(await seeded.As("later@example.com").Host.Get<IProjectService>().ListProjectsAsync());
    }

    [Fact]
    public async Task Task_assignments_allocations_and_open_charge_codes_put_people_on_projects()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        Guid open;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<ItemAssignee>().Add(new ItemAssignee { SheetId = seeded.SheetId, ItemId = seeded.ItemId, Email = Outsider, AssignedBy = Owner, AssignedAtUtc = DateTime.UtcNow });
            var indirect = new Project { Code = "IND", Name = "Indirect" };
            var root = new WbsElement { ProjectId = indirect.Id, Code = "IND", Name = "Indirect", IsChargeable = false, RequiresAssignment = false };
            db.Set<Project>().Add(indirect);
            db.Set<WbsElement>().AddRange(root, new WbsElement { ProjectId = indirect.Id, ParentId = root.Id, Code = "IND.OH", Name = "Overhead", RequiresAssignment = false });
            var closed = new Project { Code = "OLD", Name = "Closed", Status = ProjectStatus.Closed };
            db.Set<Project>().Add(closed);
            db.Set<WbsElement>().Add(new WbsElement { ProjectId = closed.Id, Code = "OLD.01", Name = "Old", RequiresAssignment = false });
            db.Set<Domain.Resourcing.ResourceAllocation>().Add(new Domain.Resourcing.ResourceAllocation { EmployeeId = f.AnalystId, ProjectId = f.Project2, WeekStart = new DateOnly(2026, 10, 19), Hours = 8, CreatedBy = Finance });
            await db.SaveChangesAsync();
            open = indirect.Id;
        }

        // outsider@: assigned a task on 1001's plan; everyone may charge the open indirect code; nobody may charge a closed project.
        Assert.Equal(["1001", "2002", "IND"], (await seeded.As(Outsider).Host.Get<IProjectService>().ListProjectsAsync()).Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.Equal(["1001", "2002", "IND"], (await seeded.As(Editor).Host.Get<IProjectService>().ListProjectsAsync()).Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.NotNull(await seeded.As(Outsider).Host.Get<IProjectService>().GetProjectAsync(open));
    }

    [Fact]
    public async Task Charge_codes_still_follow_eligibility_and_someone_elses_list_shows_only_visible_projects()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            db.Set<ChargeAssignment>().Add(new ChargeAssignment { WbsElementId = f.Wbs1, EmployeeId = f.AnalystId });
            await db.SaveChangesAsync();
        }

        async Task<IReadOnlyList<string>> CodesAsync(string asUser, Guid employee) =>
            (await seeded.As(asUser).Host.Get<IProjectService>().ListChargeCodesAsync(employee, Day)).Select(c => c.Code).ToList();

        Assert.Equal(["1001.01"], await CodesAsync(Editor, f.AnalystId)); // their own: exactly what they may charge
        Assert.Equal(["1001.01"], await CodesAsync(Owner, f.AnalystId)); // their supervisor
        Assert.Equal(["1001.01"], await CodesAsync(Timekeeper, f.AnalystId));
        Assert.Empty(await CodesAsync(Outsider, f.AnalystId)); // can't use it to discover 1001
        Assert.Empty(await CodesAsync(Outsider, f.OutsiderId)); // 2002.01 needs an assignment they don't have
    }

    [Fact]
    public async Task Notifications_about_a_project_the_user_can_no_longer_see_are_left_out()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var now = DateTime.UtcNow;
            db.Set<Notification>().AddRange(
                new Notification { RecipientEmail = Outsider, Kind = "ceiling.threshold", Title = "1001 is at 90%", Link = $"projects/{f.Project1}/financials", CreatedAtUtc = now },
                new Notification { RecipientEmail = Outsider, Kind = "ceiling.threshold", Title = "2002 is at 90%", Link = $"projects/{f.Project2}/financials", CreatedAtUtc = now.AddSeconds(-1) },
                new Notification { RecipientEmail = Outsider, Kind = NotificationKinds.Mention, Title = "Mentioned", Link = $"sheets/{seeded.SheetId}", CreatedAtUtc = now.AddSeconds(-2) });
            await db.SaveChangesAsync();
        }

        var page = await seeded.As(Outsider).Host.Get<INotificationService>().ListMineAsync();

        Assert.Equal(["2002 is at 90%", "Mentioned"], page.Items.Select(n => n.Title));
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Company_access_applies_on_top_of_being_on_the_project()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            if (!db.Set<Company>().Any(c => c.Id == Company.DefaultId))
            {
                db.Set<Company>().Add(new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main company" });
            }

            var fed = new Company { Code = "FED", LegalName = "Federal Solutions LLC" };
            db.Set<Company>().Add(fed);
            db.Set<Project>().Single(p => p.Id == f.Project2).CompanyId = fed.Id;
            var outsider = new AppUser { Email = Outsider, DisplayName = "Olly" };
            var finance = db.Set<AppUser>().Single(u => u.Email == Finance);
            db.Set<AppUser>().Add(outsider);
            db.Set<UserCompanyAccess>().AddRange(
                new UserCompanyAccess { UserId = outsider.Id, CompanyId = Company.DefaultId },
                new UserCompanyAccess { UserId = finance.Id, CompanyId = Company.DefaultId });
            await db.SaveChangesAsync();
        }

        // outsider@ charges 2002, but 2002 is in a company they can't use; Finance sees every project in theirs.
        Assert.Empty(await seeded.As(Outsider).Host.Get<IProjectService>().ListProjectsAsync());
        Assert.Empty((await seeded.As(Outsider).Host.Get<IProjectHubService>().ListProjectsAsync()).Projects);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(Outsider).Host.Get<IProjectHubService>().GetProjectAsync(f.Project2));
        Assert.Equal(["1001"], (await seeded.As(Finance).Host.Get<IProjectHubService>().ListProjectsAsync()).Projects.Select(p => p.Code));
    }

    [Fact]
    public async Task The_assistant_lists_and_reads_only_visible_projects()
    {
        await using var kit = await AssistantKit.CreateAsync();
        kit.As(Outsider);
        var tools = kit.Get<AssistantToolbox>();

        var list = await tools.RunAsync("list_projects", "{}", default);
        var json = JsonSerializer.Serialize(list.Data);
        Assert.Equal(AssistantToolOutcome.Ok, list.Outcome);
        Assert.Contains("2002", json);
        Assert.DoesNotContain("1001", json);

        var financials = await tools.RunAsync("get_project_financials", """{"project":"1001"}""", default);
        Assert.NotEqual(AssistantToolOutcome.Ok, financials.Outcome);
        Assert.DoesNotContain("Modernization", JsonSerializer.Serialize(financials.Data));
    }

    private static async Task AddRoleUsersAsync(SeededHost seeded)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        await RoleStore.AddUserAsync(db, new AppUser { Email = ResourceManager, DisplayName = "Rae" }, OrgRoles.ResourceManager);
        await RoleStore.AddUserAsync(db, new AppUser { Email = BusinessDevelopment, DisplayName = "Bea" }, OrgRoles.BusinessDevelopment);
        await RoleStore.AddUserAsync(db, new AppUser { Email = Pricing, DisplayName = "Pri" }, OrgRoles.Pricing);
        await RoleStore.AddUserAsync(db, new AppUser { Email = Hr, DisplayName = "Har" }, OrgRoles.Hr);
        await db.SaveChangesAsync();
    }
}
