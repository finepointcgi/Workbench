using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Hr;
using Workbench.Contracts.Identity;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Pricing;
using Workbench.Contracts.Security;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Hr;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// Roles limited to companies, org units, projects or contracts; sensitive fields as permissions (masked or refused
/// without them); and permissions features register at run time.
/// </summary>
public sealed class ScopedRoleAndFieldTests
{
    private static async Task<RoleDto> CreateRoleAsync(SeededHost seeded, string name, params string[] permissions) =>
        await seeded.As(Admin).Host.Get<IRoleService>().CreateAsync(new SaveRoleRequest(name, $"{name} for tests", permissions));

    private static Task<UserRolesDto> AssignAsync(SeededHost seeded, string email, params RoleAssignment[] assignments) =>
        seeded.As(Admin).Host.Get<IRoleService>().SetUserRoleAssignmentsAsync(email, assignments);

    // ---- Scopes ----

    [Fact]
    public async Task Finance_for_company_B_applies_only_while_working_in_company_B()
    {
        await using var seeded = await CreateAsync();
        var (a, b) = await TwoCompaniesAsync(seeded);
        var finance = BuiltInRoles.IdOf(OrgRoles.Finance);
        var held = await AssignAsync(seeded, Viewer, new RoleAssignment(finance, new RoleScope(RoleScopeKind.Companies, [b])));
        Assert.Equal("Finance (company B)", Assert.Single(held.Roles).Label);

        // Working in company A (the switcher): no Finance permissions at all.
        await SelectCompanyAsync(seeded, Viewer, a);
        seeded.As(Viewer);
        Assert.False(await seeded.Host.Get<AccessControl>().HasAsync(Permission.GlJournalsPost));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IJournalService>().ListAsync(new JournalQuery()));
        var meInA = await seeded.Host.Get<ICurrentUserContext>().GetAsync();
        Assert.False(meInA.Can(Permission.GlJournalsPost));
        Assert.Equal(OrgRoles.Employee, meInA.Roles); // The compatibility flags only show roles held everywhere.
        Assert.Equal("Finance (company B)", Assert.Single(meInA.AssignedRoles!).Label);

        // Switching to company B: Finance applies.
        await SelectCompanyAsync(seeded, Viewer, b);
        seeded.As(Viewer);
        Assert.True(await seeded.Host.Get<AccessControl>().HasAsync(Permission.GlJournalsPost));
        Assert.NotNull(await seeded.Host.Get<IJournalService>().ListAsync(new JournalQuery()));

        // Record checks follow the record's company, whatever is selected.
        var scope = await seeded.Host.Get<AccessControl>().GetGrantScopeAsync(Permission.ProjectsFinancialsView);
        Assert.False(scope.All);
        Assert.Equal([b], scope.Companies);

        // Admin can't be limited.
        await Assert.ThrowsAsync<ArgumentException>(() => AssignAsync(seeded, Viewer, new RoleAssignment(BuiltInRoles.Admin.Id, new RoleScope(RoleScopeKind.Companies, [b]))));
    }

    [Fact]
    public async Task A_role_limited_to_an_org_unit_or_a_project_covers_only_those_projects_and_people()
    {
        await using var seeded = await CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        Guid eng;
        await using (var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var engUnit = new OrgUnit { Code = "ENG", Name = "Engineering" };
            var sub = new OrgUnit { Code = "ENG-SW", Name = "Software", ParentId = engUnit.Id };
            db.Set<OrgUnit>().AddRange(engUnit, sub);
            (await db.Set<Employee>().SingleAsync(e => e.Id == fx.PmId)).OrgUnitId = sub.Id; // Pat (manager of 1001) is in a sub-unit of ENG.
            (await db.Set<Employee>().SingleAsync(e => e.Id == fx.OutsiderId)).OrgUnitId = engUnit.Id;
            await db.SaveChangesAsync();
            eng = engUnit.Id;
        }

        var controller = await CreateRoleAsync(seeded, "Project controller", Permission.ProjectsFinancialsView, Permission.SetupProjects, Permission.TimeViewAll);
        var held = await AssignAsync(seeded, Viewer, new RoleAssignment(controller.Id, new RoleScope(RoleScopeKind.OrgUnits, [eng])));
        Assert.Equal("Project controller (org unit ENG)", Assert.Single(held.Roles).Label);

        seeded.As(Viewer);
        var projects = seeded.Host.Get<ProjectAccess>();
        Assert.True(await projects.CanReadProjectFinancialsAsync(fx.Project1));
        Assert.False(await projects.CanReadProjectFinancialsAsync(fx.Project2));
        Assert.True(await projects.CanSetUpProjectAsync(fx.Project1));
        Assert.False(await projects.CanSetUpProjectAsync(fx.Project2));
        Assert.Equal([fx.Project1], await projects.ManagedProjectIdsAsync());
        Assert.Equal([fx.Contract1], await projects.ManagedContractIdsAsync());

        // Time: their own, plus everyone in ENG and its sub-units (not Alex, who is in no unit).
        var visible = await projects.VisibleTimeEmployeeIdsAsync();
        Assert.NotNull(visible);
        Assert.Contains(fx.PmId, visible);
        Assert.Contains(fx.OutsiderId, visible);
        Assert.Contains(fx.ViewerId, visible);
        Assert.DoesNotContain(fx.AnalystId, visible);

        // Without context the scoped permission isn't held, so page-level checks fail closed.
        Assert.False(await seeded.Host.Get<AccessControl>().HasAsync(Permission.ProjectsFinancialsView));

        // The same role limited to project 2002 instead.
        await AssignAsync(seeded, Viewer, new RoleAssignment(controller.Id, new RoleScope(RoleScopeKind.Projects, [fx.Project2])));
        seeded.As(Viewer);
        Assert.False(await projects.CanReadProjectFinancialsAsync(fx.Project1));
        Assert.True(await projects.CanReadProjectFinancialsAsync(fx.Project2));
        Assert.True(await seeded.Host.Get<AccessControl>().HasForContractAsync(Permission.ProjectsFinancialsView, fx.Contract2));
        Assert.False(await seeded.Host.Get<AccessControl>().HasForContractAsync(Permission.ProjectsFinancialsView, fx.Contract1));

        // Limiting an assignment is audited like any other change.
        await using var check = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var events = await check.Set<SecurityEvent>().AsNoTracking().Where(e => e.EventType == SecurityEventTypes.RolesChanged && e.Target == Viewer).ToListAsync();
        Assert.Contains(events, e => e.Detail!.Contains("Project controller (project 2002)"));
    }

    // ---- Sensitive fields ----

    [Fact]
    public async Task Salaries_are_masked_without_the_field_permission()
    {
        await using var kit = await HrKit.CreateAsync();
        kit.As(HrKit.Hr);
        var position = await kit.Positions.CreateAsync(new SavePositionRequest("eng-se2", "Systems Engineer II", null, null, null, EeoJobCategory.Professionals, true,
            EmploymentTypeKind.FullTime, 90_000m, 120_000m, 2, PositionStatus.Approved));

        // Workforce data without salaries: the history shows, the amounts don't.
        var analyst = await CreateRoleAsync(kit.Seeded, "Workforce analyst", Permission.HrWorkforceView);
        await AssignAsync(kit.Seeded, Viewer, new RoleAssignment(analyst.Id));
        kit.As(Viewer);
        Assert.Null((await kit.Positions.GetAsync(position.Id)).SalaryMin);
        var history = await kit.Compensation.GetHistoryAsync(kit.Fx.AnalystId);
        Assert.NotEmpty(history.Rates);
        Assert.All(history.Rates, r => Assert.True(r.HourlyRate is null && r.AnnualSalary is null));

        // Finance (built in) still sees them.
        kit.As(Finance);
        Assert.Equal(90_000m, (await kit.Positions.GetAsync(position.Id)).SalaryMin);
        Assert.Contains((await kit.Compensation.GetHistoryAsync(kit.Fx.AnalystId)).Rates, r => r.HourlyRate == 50m);

        // Granting the field reveals it.
        await kit.Seeded.As(Admin).Host.Get<IRoleService>().UpdateAsync(analyst.Id, new SaveRoleRequest(analyst.Name, null, [Permission.HrWorkforceView, Permission.FieldsSalaries]));
        kit.As(Viewer);
        Assert.Equal(120_000m, (await kit.Positions.GetAsync(position.Id)).SalaryMax);
        Assert.Contains((await kit.Compensation.GetHistoryAsync(kit.Fx.AnalystId)).Rates, r => r.HourlyRate == 50m);
    }

    [Fact]
    public async Task Eeo_data_needs_the_field_permission_except_for_the_employee_themself()
    {
        await using var kit = await HrKit.CreateAsync();
        var records = await CreateRoleAsync(kit.Seeded, "Records clerk", Permission.HrRecordsManage, Permission.HrRestrictedReports);
        await AssignAsync(kit.Seeded, Viewer, new RoleAssignment(records.Id));

        kit.As(Viewer);
        var denied = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Personnel.GetEeoAsync(kit.Fx.AnalystId));
        Assert.Contains("See EEO data", denied.Message);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.GetEeo1Async(HrKit.Today));
        Assert.NotNull(await kit.Personnel.GetEeoAsync(kit.Fx.ViewerId)); // Their own.

        kit.As(HrKit.Hr);
        Assert.NotNull(await kit.Personnel.GetEeoAsync(kit.Fx.AnalystId));
        Assert.NotNull(await kit.Reports.GetEeo1Async(HrKit.Today));
    }

    [Fact]
    public async Task Burdened_cost_needs_the_field_permission_except_on_your_own_projects()
    {
        await using var seeded = await CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var reader = await CreateRoleAsync(seeded, "Financials reader", Permission.ProjectsFinancialsView);
        await AssignAsync(seeded, Viewer, new RoleAssignment(reader.Id));

        seeded.As(Viewer);
        Assert.True(await seeded.Host.Get<ProjectAccess>().CanReadProjectFinancialsAsync(fx.Project2));
        var denied = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            seeded.Host.Get<IBurdenedCostService>().GetProjectCostAsync(fx.Project2, ProjectAccountingFixture.Day, ProjectAccountingFixture.Day, RateBasis.Provisional));
        Assert.Contains("See burdened cost", denied.Message);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IBurdenedFinancialsService>().GetProjectAsync(fx.Project2));

        // Pat manages 1001; Finance holds the field.
        Assert.IsNotType<WorkbenchForbiddenException>(await Record.ExceptionAsync(() =>
            seeded.As(Owner).Host.Get<IBurdenedCostService>().GetProjectCostAsync(fx.Project1, ProjectAccountingFixture.Day, ProjectAccountingFixture.Day, RateBasis.Provisional)));
        Assert.IsNotType<WorkbenchForbiddenException>(await Record.ExceptionAsync(() =>
            seeded.As(Finance).Host.Get<IBurdenedCostService>().GetProjectCostAsync(fx.Project2, ProjectAccountingFixture.Day, ProjectAccountingFixture.Day, RateBasis.Provisional)));
    }

    [Fact]
    public async Task Pricing_without_the_fee_field_shows_cost_only_and_refuses_the_export()
    {
        await using var seeded = await CreateAsync();
        var estimator = await CreateRoleAsync(seeded, "Estimator", Permission.PricingManage);
        await AssignAsync(seeded, Viewer, new RoleAssignment(estimator.Id));
        seeded.As(Viewer);
        var denied = await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.Host.Get<IProposalPricingService>().ExportCostVolumeAsync(Guid.NewGuid()));
        Assert.Contains("See fee and margin", denied.Message);

        // What the decorators return: fee, fee rate, margin, wrap rate and price masked, cost kept, nested lists too.
        var figures = new PriceFiguresDto(10, 1000, 300, 400, 150, 50, 0, 0, 0, 1900, 190, 2090);
        var model = new PriceModelRefDto(Guid.NewGuid(), "Base", null, true, false, null, null, null, null, null, DateTime.UtcNow);
        var comparison = new ScenarioComparisonDto(Guid.NewGuid(), 2000m,
            [new ScenarioRowDto(model, 10, 1000, 1900, 190, 2090, 0.1m, 2.09m, 0.0909m, new PriceToWinDto(2000m, 90m, 0.045m, 0.05m, false))]);
        var masked = FieldMasker.Mask(comparison, PermissionSet.Of([Permission.PricingManage]));
        var row = Assert.Single(masked.Rows);
        Assert.Equal((1900m, 0m, 0m, 0m, 0m, 0m), (row.TotalCost, row.Fee, row.Price, row.FeeRate, row.Margin, row.WrapRate));
        Assert.Equal((2000m, 0m, (decimal?)null), (row.PriceToWin!.TargetPrice, row.PriceToWin.Difference, row.PriceToWin.FeeRateAtTarget));
        Assert.Equal(190m, comparison.Rows[0].Fee); // The original is untouched.
        Assert.Same(comparison, FieldMasker.Mask(comparison, PermissionSet.Of([Permission.FieldsPricingFee])));
        Assert.Equal(0m, FieldMasker.Mask(figures, PermissionSet.Empty).Price);
        Assert.Contains((nameof(PriceFiguresDto.Fee), Permission.FieldsPricingFee), FieldMasker.FieldsOf(typeof(PriceResultDto)));
        Assert.Equal([Permission.FieldsPricingFee], FieldMasker.WithheldPermissions(typeof(PriceResultDto), PermissionSet.Of([Permission.PricingManage])));
    }

    [Fact]
    public async Task Burden_columns_of_the_gl_reconciliation_are_masked_without_the_field()
    {
        await using var seeded = await CreateAsync();
        var gl = await CreateRoleAsync(seeded, "GL reports", Permission.GlReportsView);
        await AssignAsync(seeded, Viewer, new RoleAssignment(gl.Id));
        var withBurden = new ReconciliationDto([new Contracts.Accounting.ReconciliationRowDto(Guid.NewGuid(), "1001", Guid.NewGuid(), "1001.01", 100, 0, 100, 0, 55, 60)], []);
        var masked = FieldMasker.Mask(withBurden, await seeded.As(Viewer).Host.Get<AccessControl>().GetPermissionsAsync());
        Assert.Equal((100m, 0m, 0m), (masked.Rows[0].FinancialsLabor, masked.Rows[0].GlAppliedBurden, masked.Rows[0].ExpectedBurden));
        Assert.Equal(60m, FieldMasker.Mask(withBurden, await seeded.As(Finance).Host.Get<AccessControl>().GetPermissionsAsync()).Rows[0].ExpectedBurden);
        Assert.NotNull(await seeded.As(Viewer).Host.Get<IAccountingReportService>().GetReconciliationAsync());
    }

    [Fact]
    public void Built_in_roles_keep_every_sensitive_field_they_saw_before()
    {
        // Each field permission goes to exactly the built-in roles that already saw the field.
        Assert.Equal(PermissionCatalog.Find(Permission.HrWorkforceView)!.GrantedTo, PermissionCatalog.Find(Permission.FieldsSalaries)!.GrantedTo);
        Assert.Equal(PermissionCatalog.Find(Permission.ProjectsFinancialsView)!.GrantedTo, PermissionCatalog.Find(Permission.FieldsBurdenedCost)!.GrantedTo);
        Assert.Equal(PermissionCatalog.Find(Permission.HrRestrictedReports)!.GrantedTo, PermissionCatalog.Find(Permission.FieldsEeo)!.GrantedTo);
        Assert.Equal(PermissionCatalog.Find(Permission.PricingManage)!.GrantedTo, PermissionCatalog.Find(Permission.FieldsPricingFee)!.GrantedTo);
        Assert.All(PermissionCatalog.All.Where(p => p.Area == PermissionCatalog.AreaFields), p => Assert.True(p.Sensitive));
        Assert.Equal(8, PermissionCatalog.All.Count(p => p.Area == PermissionCatalog.AreaFields));
    }

    // ---- Run-time permissions ----

    [Fact]
    public async Task Features_register_permissions_that_roles_grant_like_built_in_ones()
    {
        await using var seeded = await CreateAsync();
        var registry = seeded.Host.Get<IPermissionRegistry>();
        const string key = "custom_field.clearance.view";
        var registered = await registry.RegisterAsync(new RegisterPermissionRequest(key, "Custom fields", "See the Clearance field", "The Clearance custom field on employees.",
            "custom-fields", OrgRoles.Finance, Sensitive: true));
        Assert.Equal("custom-fields", registered.Source);

        // Listed after the built-in catalog; Finance (the built-in grantee) and Admin hold it; nobody else does.
        var catalog = await seeded.Host.Get<IPermissionCatalog>().ListAsync();
        Assert.Equal(key, catalog[^1].Key);
        Assert.Equal(PermissionCatalog.All.Count + 1, catalog.Count);
        Assert.True(await seeded.As(Finance).Host.Get<AccessControl>().HasAsync(key));
        Assert.True(await seeded.As(Admin).Host.Get<AccessControl>().HasAsync(key));
        Assert.False(await seeded.As(Viewer).Host.Get<AccessControl>().HasAsync(key));
        var roles = await seeded.As(Admin).Host.Get<IRoleService>().ListAsync();
        Assert.Contains(key, roles.Single(r => r.BuiltIn == OrgRoles.Finance).Permissions);
        Assert.Contains(key, roles.Single(r => r.BuiltIn == OrgRoles.Admin).Permissions);
        Assert.DoesNotContain(key, roles.Single(r => r.BuiltIn == OrgRoles.Hr).Permissions);

        // A custom role can grant it.
        var reviewer = await CreateRoleAsync(seeded, "Clearance reviewer", key);
        await AssignAsync(seeded, Viewer, new RoleAssignment(reviewer.Id));
        Assert.True(await seeded.As(Viewer).Host.Get<AccessControl>().HasAsync(key));
        Assert.Contains(key, (await seeded.Host.Get<ICurrentUserContext>().GetAsync()).Permissions);

        // Re-registering updates it; another feature can't take it over; bad and built-in keys are refused.
        await registry.RegisterAsync(new RegisterPermissionRequest(key, "Custom fields", "See the Clearance field", "Updated.", "custom-fields", OrgRoles.Finance | OrgRoles.Hr));
        Assert.Contains(key, (await seeded.As(Admin).Host.Get<IRoleService>().ListAsync()).Single(r => r.BuiltIn == OrgRoles.Hr).Permissions);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(new RegisterPermissionRequest(key, "X", "X", "", "automations")));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(new RegisterPermissionRequest("Not A Key", "X", "X", "", "automations")));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(new RegisterPermissionRequest(Permission.GlJournalsPost, "X", "X", "", "automations")));
        Assert.Equal([key], (await registry.ListBySourceAsync("custom-fields")).Select(p => p.Key));

        // Unregistering takes it from every role.
        await registry.UnregisterAsync(key);
        Assert.False(await seeded.As(Viewer).Host.Get<AccessControl>().HasAsync(key));
        Assert.False(await seeded.As(Finance).Host.Get<AccessControl>().HasAsync(key));
        Assert.Empty((await seeded.As(Admin).Host.Get<IRoleService>().GetAsync(reviewer.Id)).Permissions);
        await Assert.ThrowsAsync<ArgumentException>(() => seeded.Host.Get<IRoleService>().CreateAsync(new SaveRoleRequest("Too late", null, [key])));

        // Each step is audited and recorded as a security event.
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var audit = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == PermissionRegistry.AuditEntity).Select(a => a.Action).ToListAsync();
        Assert.Equal(["Registered", "Updated", "Unregistered"], audit);
        var events = await db.Set<SecurityEvent>().AsNoTracking().Where(e => e.Target == key).Select(e => e.EventType).ToListAsync();
        Assert.Equal([SecurityEventTypes.PermissionRegistered, SecurityEventTypes.PermissionUnregistered], events);
    }

    private static async Task<(Guid A, Guid B)> TwoCompaniesAsync(SeededHost seeded)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var a = await db.Set<Company>().FirstOrDefaultAsync(c => c.Id == Company.DefaultId);
        if (a is null)
        {
            a = new Company { Id = Company.DefaultId, Code = "A", LegalName = "Company A" };
            db.Set<Company>().Add(a);
        }

        var b = new Company { Code = "B", LegalName = "Company B" };
        db.Set<Company>().Add(b);
        await db.SaveChangesAsync();
        return (a.Id, b.Id);
    }

    private static async Task SelectCompanyAsync(SeededHost seeded, string email, Guid companyId)
    {
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var userId = await db.Set<AppUser>().Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        db.Set<UserCompanySelection>().RemoveRange(await db.Set<UserCompanySelection>().Where(s => s.UserId == userId).ToListAsync());
        db.Set<UserCompanySelection>().Add(new UserCompanySelection { UserId = userId, CompanyId = companyId, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }
}
