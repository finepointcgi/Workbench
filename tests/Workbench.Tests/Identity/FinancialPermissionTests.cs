using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Time;
using static Workbench.Tests.Identity.ProjectAccountingFixture;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Identity;

/// <summary>
/// The permission decorators in front of timekeeping reports, project financials, budgets, exports, contract funding and
/// ODC listings, over the real registrations (pm@ manages 1001; analyst@ and viewer@ report to pm@).
/// </summary>
public sealed class FinancialPermissionTests
{
    private static readonly DateOnly From = Day.AddDays(-7);
    private static readonly DateOnly To = Day.AddDays(7);

    public static TheoryData<string> FinanceOrAdmin => new() { Finance, Admin };

    public static TheoryData<string> NotFinance => new() { Owner, Editor, Timekeeper, Viewer };

    [Theory]
    [MemberData(nameof(FinanceOrAdmin))]
    public async Task Finance_and_admin_export_the_labor_distribution_with_costs(string email)
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        var csv = await seeded.As(email).Host.Get<ITimeReportService>().ExportLaborDistributionCsvAsync(From, To);

        Assert.Contains("87.5000", csv);
    }

    [Theory]
    [MemberData(nameof(NotFinance))]
    public async Task Nobody_else_gets_the_labor_distribution(string email)
    {
        await using var seeded = await CreateAsync();
        await SeedAsync(seeded);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(email).Host.Get<ITimeReportService>().ExportLaborDistributionCsvAsync(From, To));
    }

    [Theory]
    [InlineData(Timekeeper, 3)]
    [InlineData(Finance, 3)]
    [InlineData(Admin, 3)]
    [InlineData(Owner, 2)] // supervisor: analyst@ and viewer@
    [InlineData(Editor, 1)] // only themselves
    [InlineData(Commenter, 0)] // no employee record: nobody
    public async Task Hours_and_daily_entry_reports_show_who_you_may_see(string email, int people)
    {
        await using var seeded = await CreateAsync();
        var fixture = await SeedAsync(seeded);
        var reports = seeded.As(email).Host.Get<ITimeReportService>();

        var hours = await reports.GetHoursByChargeCodeAsync(From, To);
        var daily = await reports.GetDailyEntryComplianceAsync(From, To);

        Assert.Equal(people, hours.Select(r => r.EmployeeId).Distinct().Count());
        Assert.Equal(people, daily.Select(r => r.EmployeeId).Distinct().Count());
        if (email == Editor)
        {
            Assert.All(hours, r => Assert.Equal(fixture.AnalystId, r.EmployeeId));
        }
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    [InlineData(Owner)] // the project's manager
    public async Task Finance_admin_and_the_pm_read_a_projects_financials_and_budgets(string email)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var host = seeded.As(email).Host;

        Assert.Equal("1001", (await host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project1)).ProjectCode);
        Assert.NotEmpty(await host.Get<IProjectFinancialsService>().GetWbsFinancialsAsync(f.Project1));
        await host.Get<IProjectFinancialsService>().GetTimePhasedBurnAsync(f.Project1);
        Assert.Contains("1001", (await host.Get<IFinancialExportService>().ExportWbsCsvAsync(f.Project1)).Content);
        await host.Get<IFinancialExportService>().ExportTimePhasedCsvAsync(f.Project1);
        Assert.Single(await host.Get<IBudgetService>().ListVersionsAsync(f.Project1));
        Assert.NotNull(await host.Get<IBudgetService>().GetVersionAsync(f.BudgetVersionId));
        Assert.True(await host.Get<IFinancialAccessService>().CanReadProjectFinancialsAsync(f.Project1));
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Timekeeper)]
    [InlineData(Viewer)]
    public async Task Others_get_403_on_project_financials_budgets_and_exports(string email)
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var host = seeded.As(email).Host;
        var financials = host.Get<IProjectFinancialsService>();
        var budgets = host.Get<IBudgetService>();
        var exports = host.Get<IFinancialExportService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => financials.GetProjectSummaryAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => financials.GetWbsFinancialsAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => financials.GetTimePhasedBurnAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => exports.ExportWbsCsvAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => exports.ExportTimePhasedCsvAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.ListVersionsAsync(f.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.GetVersionAsync(f.BudgetVersionId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.CreateVersionAsync(f.Project1, new CreateBudgetVersionRequest("F1", BudgetVersionKind.Forecast)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.SaveLinesAsync(f.BudgetVersionId, []));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.LockBaselineAsync(f.BudgetVersionId, "Approved"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.PromoteToCurrentAsync(f.BudgetVersionId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.RepriceAsync(f.BudgetVersionId));
        Assert.False(await host.Get<IFinancialAccessService>().CanReadProjectFinancialsAsync(f.Project1));
    }

    [Fact]
    public async Task A_pm_sees_only_their_own_projects_and_can_change_their_budgets()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var host = seeded.As(Owner).Host;
        var budgets = host.Get<IBudgetService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IProjectFinancialsService>().GetProjectSummaryAsync(f.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => budgets.CreateVersionAsync(f.Project2, new CreateBudgetVersionRequest("F", BudgetVersionKind.Forecast)));

        var forecast = await budgets.CreateVersionAsync(f.Project1, new CreateBudgetVersionRequest("Forecast 1", BudgetVersionKind.Forecast));
        await budgets.SaveLinesAsync(forecast.Version.Id, []);
        await budgets.PromoteToCurrentAsync(forecast.Version.Id);
        Assert.True((await budgets.LockBaselineAsync(f.BudgetVersionId, "Approved")).IsLocked);
    }

    [Fact]
    public async Task Contract_funding_is_for_finance_and_the_pms_of_its_projects()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);

        var all = await seeded.As(Finance).Host.Get<IProjectFinancialsService>().ListContractFundingAsync();
        Assert.Equal(2, all.Count);

        var pm = seeded.As(Owner).Host.Get<IProjectFinancialsService>();
        Assert.Equal(f.Contract1, (await pm.GetContractFundingAsync(f.Contract1)).ContractId);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.GetContractFundingAsync(f.Contract2));
        Assert.Equal([f.Contract1], (await pm.ListContractFundingAsync()).Select(c => c.ContractId));

        var analyst = seeded.As(Editor).Host.Get<IProjectFinancialsService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => analyst.GetContractFundingAsync(f.Contract1));
        Assert.Empty(await analyst.ListContractFundingAsync());
        Assert.False(await seeded.Host.Get<IFinancialAccessService>().CanReadContractFundingAsync(f.Contract1));
    }

    [Fact]
    public async Task Project_odc_lists_are_for_finance_and_the_pm_and_employees_see_their_own()
    {
        await using var seeded = await CreateAsync();
        var f = await SeedAsync(seeded);
        var query = new OdcListQuery(IncludeExpenses: true);

        Assert.Equal(2, (await seeded.As(Owner).Host.Get<IOdcService>().ListProjectOdcsAsync(f.Project1, query)).Entries.Count);
        Assert.Equal(2, (await seeded.As(Finance).Host.Get<IOdcService>().ListProjectOdcsAsync(f.Project1, query)).Entries.Count);

        var odcs = seeded.As(Editor).Host.Get<IOdcService>();
        var mine = await odcs.ListProjectOdcsAsync(f.Project1, query);
        Assert.Equal(f.ExpenseOdcId, Assert.Single(mine.Entries).Id);
        Assert.Equal(40m, mine.Total);
        Assert.NotNull(await odcs.GetAsync(f.ExpenseOdcId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => odcs.GetAsync(f.VendorOdcId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => odcs.OpenReceiptAsync(f.VendorOdcId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => odcs.DeleteVendorOdcAsync(f.VendorOdcId));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => odcs.SubmitAsync(f.VendorOdcId));

        Assert.Empty((await seeded.As(Outsider).Host.Get<IOdcService>().ListProjectOdcsAsync(f.Project1, query)).Entries);
        Assert.NotNull(await seeded.As(Owner).Host.Get<IOdcService>().GetAsync(f.VendorOdcId));
    }
}
