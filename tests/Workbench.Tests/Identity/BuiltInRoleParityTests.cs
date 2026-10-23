using Workbench.Contracts.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Identity;

/// <summary>
/// The built-in roles grant exactly the access the fixed org roles had: every permission is held by exactly the role
/// combinations the role check it replaced let through, for all 512 combinations of the nine roles.
/// </summary>
public sealed class BuiltInRoleParityTests
{
    // Each permission and the role check (OrgRoleChecks, as it was) it replaced at its call sites.
    private static readonly Dictionary<string, Func<OrgRoles, bool>> Legacy = new()
    {
        [Permission.AdminUsers] = r => r.IsAdmin(),
        [Permission.AdminRoles] = r => r.IsAdmin(),
        [Permission.AdminSecurity] = r => r.IsAdmin(),
        [Permission.AdminRetention] = r => r.IsAdmin(),
        [Permission.AdminCompliance] = r => r.IsAdmin(),
        [Permission.AdminIntegrations] = r => r.IsAdmin(),
        [Permission.AdminAssistant] = r => r.IsAdmin(),
        [Permission.AdminSupport] = r => r.IsAdmin(),
        [Permission.AdminOrganization] = r => r.IsAdmin(),
        [Permission.AdminAllWorkspaces] = r => r.IsAdmin(),
        [Permission.AdminCustomization] = r => r.IsAdmin(),
        [Permission.AdminAutomations] = r => r.IsAdmin(),
        [Permission.ApprovalWorkflowsView] = r => r.IsFinance(),
        [Permission.ApprovalWorkflowsManage] = r => r.IsAdmin(),
        [Permission.ApprovalDelegationsManage] = r => r.IsAdmin(),
        [Permission.ApprovalsActAsAdmin] = r => r.IsAdmin(),
        [Permission.SetupOrganizationView] = r => r.IsFinance(),
        [Permission.SetupPeople] = r => r.IsFinance(),
        [Permission.SetupFiscalCalendar] = r => r.IsFinance(),
        [Permission.SetupContracts] = r => r.IsFinance(),
        [Permission.SetupBillRates] = r => r.IsFinance(),
        [Permission.SetupProjects] = r => r.IsFinance(),
        [Permission.SetupImports] = r => r.IsFinance(),
        [Permission.SetupAuditLog] = r => r.IsFinance(),
        [Permission.SetupPerDiem] = r => r.IsFinance(),
        [Permission.SetupCurrencies] = r => r.IsFinance(),
        [Permission.SetupCompanies] = r => r.IsFinance(),
        [Permission.TimeViewAll] = r => r.CanViewAllTimesheets(),
        [Permission.TimeTeam] = r => r.IsAdmin() || r.HasFlag(OrgRoles.Timekeeper), // NavAccess.ShowTeamTime
        [Permission.LaborCostView] = r => r.IsFinance(),
        [Permission.LaborCostRatesManage] = r => r.IsFinance(),
        [Permission.TimeTotalTime] = r => r.IsFinance(),
        [Permission.LeaveBalancesManage] = r => r.IsFinance(),
        [Permission.LeavePoliciesManage] = r => r.IsAdmin(),
        [Permission.ProjectsViewAll] = r => r.IsFinance() || r.IsResourceManager(), // Project visibility's "every project" (Finance, Admin, Resource manager).
        [Permission.ProjectsCreate] = r => r.CanManageProjects(),
        [Permission.ProjectsManageOwn] = r => r.IsProjectManager(),
        [Permission.ProjectsFinancialsView] = r => r.IsFinance(),
        [Permission.ResourcingManage] = r => r.IsResourceManager(),
        [Permission.ResourcingView] = r => r.IsFinance(),
        [Permission.EvmManage] = r => r.IsFinance(),
        [Permission.CeilingsManage] = r => r.IsFinance(),
        [Permission.GlAccountsManage] = r => r.IsFinance(),
        [Permission.GlJournalsPost] = r => r.IsFinance(),
        [Permission.GlPostingManage] = r => r.IsFinance(),
        [Permission.GlClose] = r => r.IsFinance(),
        [Permission.GlReportsView] = r => r.IsFinance(),
        [Permission.GlRecurringManage] = r => r.IsFinance(),
        [Permission.GlRevaluation] = r => r.IsFinance(),
        [Permission.BudgetsManage] = r => r.IsFinance(),
        [Permission.RevenueManage] = r => r.IsFinance(),
        [Permission.IndirectRatesManage] = r => r.IsFinance(),
        [Permission.IncurredCostManage] = r => r.IsFinance(),
        [Permission.CasCpsrManage] = r => r.IsFinance(),
        [Permission.SalesTaxManage] = r => r.IsFinance(),
        [Permission.AssetsManage] = r => r.IsFinance(),
        [Permission.AssetsDepreciationRun] = r => r.IsFinance(),
        [Permission.BillingInvoicesManage] = r => r.IsFinance(),
        [Permission.BillingPaymentsRecord] = r => r.IsFinance(),
        [Permission.BillingReportsView] = r => r.IsFinance(),
        [Permission.VendorsManage] = r => r.IsFinance(),
        [Permission.VendorTaxIds] = r => r.IsFinance(),
        [Permission.VendorBankDetails] = r => r.IsFinance(),
        [Permission.ApBillsManage] = r => r.IsFinance(),
        [Permission.ApPaymentsManage] = r => r.IsFinance(),
        [Permission.ApReportsView] = r => r.IsFinance(),
        [Permission.PurchaseOrdersManage] = r => r.IsFinance(),
        [Permission.SourcingManage] = r => r.IsFinance(),
        [Permission.CashManage] = r => r.IsFinance(),
        [Permission.CashForecastView] = r => r.IsFinance(),
        [Permission.PayrollRun] = r => r.IsFinance(),
        [Permission.PayrollSetup] = r => r.IsFinance(),
        [Permission.PayrollProfilesManage] = r => r.IsFinance(),
        [Permission.PayRatesView] = r => r.IsFinance(),
        [Permission.TravelManage] = r => r.IsFinance(),
        [Permission.CardsManage] = r => r.IsFinance(),
        [Permission.HrRecordsManage] = r => r.IsHr(),
        [Permission.HrPositionsManage] = r => r.IsHr(),
        [Permission.HrCompensationManage] = r => r.IsHr(),
        [Permission.HrWorkforceView] = r => r.IsHr() || r.IsFinance(),
        [Permission.HrBenefitsManage] = r => r.IsHr(),
        [Permission.HrOnboardingManage] = r => r.IsHr(),
        [Permission.HrRestrictedReports] = r => r.IsHr(),
        [Permission.PipelineView] = r => r.CanViewPipeline(),
        [Permission.CaptureManage] = r => r.IsBusinessDevelopment(),
        [Permission.OpportunitiesEditAll] = r => r.IsAdmin(),
        [Permission.BpChargeCode] = r => r.IsFinance(),
        [Permission.PricingManage] = r => r.CanPrice(),
        [Permission.MaterialsView] = r => r.CanViewMaterials(),
        [Permission.MaterialsManage] = r => r.IsMaterials(),
        [Permission.MaterialsCosting] = r => r.IsFinance(),
        [Permission.QualityDispositions] = r => r.IsFinance() || r.IsMaterials(),
        [Permission.SalesOrdersView] = r => r.CanViewMaterials() || r.IsBusinessDevelopment(),
        [Permission.SalesOrdersManage] = r => r.CanViewMaterials(),
        [Permission.SalesCreditHold] = r => r.IsFinance(),
        [Permission.RestrictedColumnsView] = r => r.IsFinance(),
        [Permission.SheetFormsPublic] = r => r.IsFinance() || r.IsProjectManager(), // New in wave E: public form links (also needs the workspace's Owner role).

        // Sensitive fields: who saw each field before it became a permission.
        [Permission.FieldsSalaries] = r => r.IsHr() || r.IsFinance(), // Salary ranges and compensation amounts (HR, Finance, Admin).
        [Permission.FieldsBurdenedCost] = r => r.IsFinance(), // Burdened cost beyond your own projects (Finance, Admin).
        [Permission.FieldsEeo] = r => r.IsHr(), // Others' EEO data and the EEO-1/VETS-4212 reports (HR, Admin).
        [Permission.FieldsPricingFee] = r => r.CanPrice(), // Fee, margin and price in proposal pricing.
    };

    private static IEnumerable<OrgRoles> EveryCombination() =>
        Enumerable.Range(0, 1 << OrgRoleChecks.All.Count)
            .Select(bits => OrgRoleChecks.All.Where((_, i) => (bits & (1 << i)) != 0).Aggregate(OrgRoles.Employee, (all, r) => all | r));

    [Fact]
    public void Every_permission_names_the_role_check_it_replaced()
    {
        Assert.Equal(PermissionCatalog.All.Select(p => p.Key).Order(), Legacy.Keys.Order());
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(p => p.Key).Distinct().Count());
        Assert.All(PermissionCatalog.All, p => Assert.Contains(p.Area, PermissionCatalog.Areas));
    }

    [Fact]
    public void Built_in_roles_grant_exactly_what_the_old_role_checks_allowed()
    {
        foreach (var roles in EveryCombination())
        {
            var granted = PermissionCatalog.ForBuiltInRoles(roles);
            foreach (var (key, legacy) in Legacy)
            {
                Assert.True(legacy(roles) == granted.Has(key), $"{key} for {roles}: was {legacy(roles)}, now {granted.Has(key)}");
            }
        }
    }

    [Fact]
    public void Admin_holds_every_permission_and_Employee_none()
    {
        Assert.Equal(PermissionCatalog.All.Count, BuiltInRoles.Admin.Permissions.Count);
        Assert.Empty(BuiltInRoles.Employee.Permissions);
        Assert.Equal(10, BuiltInRoles.All.Count);
        Assert.Equal(BuiltInRoles.All.Count, BuiltInRoles.All.Select(r => r.Id).Distinct().Count());
        Assert.All(OrgRoleChecks.All, r => Assert.Equal(r, BuiltInRoles.FlagsOf([BuiltInRoles.IdOf(r)])));
    }

    [Fact]
    public void A_users_info_built_from_roles_alone_gets_the_built_in_permissions()
    {
        var finance = new CurrentUserInfo("f@example.com", "F", OrgRoles.Finance, null);
        Assert.True(finance.Can(Permission.GlJournalsPost));
        Assert.False(finance.Can(Permission.AdminUsers));

        var custom = new CurrentUserInfo("c@example.com", "C", OrgRoles.Employee, null, [Permission.GlJournalsPost]);
        Assert.True(custom.Can(Permission.GlJournalsPost));
        Assert.False(custom.Can(Permission.GlAccountsManage));
    }

    [Fact]
    public void Built_in_roles_see_the_same_menu_as_before()
    {
        // Spot checks of the menu rules that combine permissions (the plain ones follow from the parity above).
        static List<string> Menu(OrgRoles roles, bool managesProject = false) =>
            NavCatalog.MenuEntries(new NavAccess(new CurrentUserInfo("x@example.com", "X", roles, Guid.NewGuid()), IsProjectManager: managesProject)).Select(e => e.Href).ToList();

        Assert.Contains("time/team", Menu(OrgRoles.Timekeeper));
        Assert.DoesNotContain("time/team", Menu(OrgRoles.Finance));
        Assert.Contains("time/team", Menu(OrgRoles.Admin));
        Assert.Contains("setup/projects/new", Menu(OrgRoles.ProjectManager));
        Assert.DoesNotContain("setup/projects/new", Menu(OrgRoles.Finance | OrgRoles.ProjectManager));
        Assert.DoesNotContain("setup/projects/new", Menu(OrgRoles.Admin));
        Assert.Contains("reports/standard/project-status", Menu(OrgRoles.Employee, managesProject: true));
        Assert.DoesNotContain("reports/standard/project-status", Menu(OrgRoles.Finance, managesProject: true));
        Assert.Contains("odcs", Menu(OrgRoles.ProjectManager));
        Assert.DoesNotContain("odcs", Menu(OrgRoles.Employee));
        Assert.Contains("materials/sales-orders", Menu(OrgRoles.BusinessDevelopment));
        Assert.Contains("admin/roles", Menu(OrgRoles.Admin));
        Assert.DoesNotContain("admin/roles", Menu(OrgRoles.Finance));
        Assert.Equal("Setup", NavCatalog.TitleFor(NavCatalog.Sections.Single(s => s.Key == NavCatalog.Admin), new NavAccess(new CurrentUserInfo("x@example.com", "X", OrgRoles.Finance, null))));
        Assert.Equal("Admin", NavCatalog.TitleFor(NavCatalog.Sections.Single(s => s.Key == NavCatalog.Admin), new NavAccess(new CurrentUserInfo("x@example.com", "X", OrgRoles.Admin, null))));
    }

    [Fact]
    public void Every_permission_is_documented()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "security", "permissions.md"));
        Assert.All(PermissionCatalog.All, p => Assert.Contains($"`{p.Key}`", doc));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Workbench.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
