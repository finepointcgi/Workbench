using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Contracts.Reports;
using Workbench.Domain.Companies;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Companies;

/// <summary>
/// Company filters on the Projects list, the report writer and dashboards, over the real registrations. MAIN has project
/// 1001, contract M-1 and a 75.00 journal entry; FED has project 4001, contract F-1 and a 40.00 entry. Finance may use both
/// companies (the multicompany Finance user); timekeeper@ is limited to FED.
/// </summary>
public sealed class CompanyFilterTests
{
    [Fact]
    public async Task Project_rows_carry_their_company_for_the_filter()
    {
        await using var kit = await TwoCompaniesAsync();

        var rows = (await kit.As(SeededHost.Finance).Get<IProjectHubService>().ListProjectsAsync()).Projects;

        Assert.Equal([("1001", Company.DefaultId, "MAIN"), ("4001", kit.Fed, "FED")],
            rows.OrderBy(r => r.Code).Select(r => (r.Code, r.CompanyId!.Value, r.CompanyCode!)));
        var context = await kit.As(SeededHost.Finance).Get<ICompanyService>().GetContextAsync();
        Assert.True(context is { ShowSwitcher: true, SelectedCompanyId: null }); // two companies in view: the filter shows
    }

    [Fact]
    public async Task Someone_limited_to_one_company_gets_no_filter_and_only_its_projects()
    {
        await using var kit = await TwoCompaniesAsync();

        var context = await kit.As(SeededHost.Timekeeper).Get<ICompanyService>().GetContextAsync();

        Assert.Equal(kit.Fed, context.SelectedCompanyId); // works in their one company: no filter to show
        await using var single = await TwoCompaniesAsync(secondCompany: false);
        Assert.False((await single.As(SeededHost.Finance).Get<ICompanyService>().GetContextAsync()).ShowSwitcher);
    }

    [Fact]
    public async Task The_report_writer_shows_the_chosen_companys_data_and_all_of_the_users_companies_by_default()
    {
        await using var kit = await TwoCompaniesAsync();
        var writer = kit.As(SeededHost.Finance).Get<IReportWriterService>();
        var gl = new ReportDefinition(ReportSourceKeys.GlLines, [new ReportColumnSpec("description"), new ReportColumnSpec("debit")]);
        var projects = new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new ReportColumnSpec("project")]);

        Assert.Equal(["FED accrual", "MAIN accrual"], Details(await writer.RunAsync(gl), 0).Distinct().Order());
        Assert.Equal(["1001", "4001"], Details(await writer.RunAsync(projects), 0).Distinct().Order());

        var fedOnly = await writer.RunAsync(gl with { CompanyId = kit.Fed });
        Assert.Equal(["FED accrual"], Details(fedOnly, 0).Distinct());
        Assert.Contains("FED", fedOnly.Subtitle);
        Assert.Equal(["1001"], Details(await writer.RunAsync(projects with { CompanyId = Company.DefaultId }), 0).Distinct());
    }

    [Fact]
    public async Task The_report_writer_refuses_a_company_the_user_cant_use()
    {
        await using var kit = await TwoCompaniesAsync();

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(SeededHost.Timekeeper).Get<IReportWriterService>().RunAsync(
            new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new ReportColumnSpec("project")], CompanyId: Company.DefaultId)));
    }

    [Fact]
    public async Task A_saved_report_keeps_its_company()
    {
        await using var kit = await TwoCompaniesAsync();
        var saved = kit.As(SeededHost.Finance).Get<ISavedReportService>();
        var report = await saved.CreateAsync(new SaveReportRequest("FED projects", null, ReportVisibility.Private, null, null, null,
            new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new ReportColumnSpec("project")], CompanyId: kit.Fed)));

        Assert.Equal(["4001"], Details(await saved.RunAsync(report.Id), 0).Distinct());
    }

    [Fact]
    public async Task Dashboard_widgets_follow_the_dashboards_company_choice()
    {
        await using var kit = await TwoCompaniesAsync();
        var host = kit.As(SeededHost.Finance);
        var dashboards = host.Get<IDashboardService>();
        var dashboard = await dashboards.CreateAsync(new CreateDashboardRequest("Finance"));
        var funding = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("M-1 funding", WidgetType.ContractFunding, 6, WidgetHeight.Medium,
            new WidgetConfig(ContractId: kit.MainContract)));
        var projects = await host.Get<ISavedReportService>().CreateAsync(new SaveReportRequest("Projects", null, ReportVisibility.Private, null, null, null,
            new ReportDefinition(ReportSourceKeys.ProjectsWbs, [new ReportColumnSpec("project"), new ReportColumnSpec("contract_ceiling")])));
        var chart = await dashboards.AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("Projects", WidgetType.ReportChart, 6, WidgetHeight.Medium,
            new WidgetConfig(SavedReportId: projects.Id)));
        var data = host.Get<IWidgetDataService>();

        // Every company the user is working in (the default), then each company.
        Assert.Equal("M-1", (await data.GetAsync(dashboard.Id, funding.Id)).ContractFunding!.ContractNumber);
        Assert.Equal("M-1", (await data.GetAsync(dashboard.Id, funding.Id, Company.DefaultId)).ContractFunding!.ContractNumber);
        var elsewhere = await data.GetAsync(dashboard.Id, funding.Id, kit.Fed);
        Assert.Null(elsewhere.ContractFunding);
        Assert.Contains("isn't in FED", elsewhere.Error);

        Assert.Equal(["1001", "4001"], (await data.GetAsync(dashboard.Id, chart.Id)).Chart!.Labels.Order());
        Assert.Equal(["4001"], (await data.GetAsync(dashboard.Id, chart.Id, kit.Fed)).Chart!.Labels);

        // A company outside the user's companies is not found.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.As(SeededHost.Timekeeper).Get<IWidgetDataService>().GetAsync(dashboard.Id, funding.Id, Company.DefaultId));
    }

    [Fact]
    public async Task The_company_choice_lasts_only_for_the_call_that_made_it()
    {
        await using var kit = await TwoCompaniesAsync();
        var host = kit.As(SeededHost.Finance);
        var dashboard = await host.Get<IDashboardService>().CreateAsync(new CreateDashboardRequest("Finance"));
        var funding = await host.Get<IDashboardService>().AddWidgetAsync(dashboard.Id, new SaveWidgetRequest("F-1 funding", WidgetType.ContractFunding, 6, WidgetHeight.Medium,
            new WidgetConfig(ContractId: kit.FedContract)));

        await host.Get<IWidgetDataService>().GetAsync(dashboard.Id, funding.Id, kit.Fed);

        Assert.Equal(2, (await host.Get<IProjectService>().ListProjectsAsync()).Count);
        Assert.Equal(["F-1", "M-1"], (await host.Get<IContractService>().ListContractsAsync()).Select(c => c.ContractNumber).Order());
    }

    private static IEnumerable<string?> Details(ReportResultDto result, int column) =>
        result.Rows.Where(r => r.Kind == ReportRowKind.Detail).Select(r => r.Values[column]);

    private static async Task<Kit> TwoCompaniesAsync(bool secondCompany = true)
    {
        var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(SeededHost.Admin).Host;
        await host.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        var accounts = (await host.Get<IChartOfAccountsService>().ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        var period = (await host.Get<IFiscalCalendarService>().CreateFiscalYearAsync(2027, new DateOnly(2026, 10, 1))).First();
        var companies = host.Get<ICompanyService>();
        var contracts = host.Get<IContractService>();
        var customer = await contracts.CreateCustomerAsync(new SaveCustomerRequest("ARMY", "Army"));
        SaveContractRequest Contract(string number) =>
            new(customer.Id, number, number, ContractKind.FirmFixedPrice, ContractState.Active, true, period.Start, period.Start.AddYears(1), 100_000m, 50_000m);

        var mainContract = await contracts.CreateContractAsync(Contract("M-1"));
        await host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("1001", "Modernization", mainContract.Id, null, ProjectState.Active, null, null));
        await host.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(period.Start, "MAIN accrual",
            [new JournalLineInput(accounts["6110"], 75m, 0), new JournalLineInput(accounts["2200"], 0, 75m)]));
        if (!secondCompany)
        {
            return new Kit(seeded, Guid.Empty, mainContract.Id, Guid.Empty);
        }

        var fed = await companies.CreateAsync(new SaveCompanyRequest("FED", "Federal Solutions LLC"));
        await companies.SelectAsync(new SelectCompanyRequest(fed.Id));
        var fedContract = await contracts.CreateContractAsync(Contract("F-1"));
        await host.Get<IProjectService>().CreateProjectAsync(new SaveProjectRequest("4001", "Cyber assessments", fedContract.Id, null, ProjectState.Active, null, null));
        await host.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(period.Start, "FED accrual",
            [new JournalLineInput(accounts["6110"], 40m, 0), new JournalLineInput(accounts["2200"], 0, 40m)]));
        await companies.SelectAsync(new SelectCompanyRequest(null));

        var access = await companies.ListUserAccessAsync();
        await companies.SetUserAccessAsync(access.Single(u => u.Email == SeededHost.Finance).UserId, new SetUserCompanyAccessRequest([Company.DefaultId, fed.Id]));
        await companies.SetUserAccessAsync(access.Single(u => u.Email == SeededHost.Timekeeper).UserId, new SetUserCompanyAccessRequest([fed.Id]));
        return new Kit(seeded, fed.Id, mainContract.Id, fedContract.Id);
    }

    private sealed record Kit(SeededHost Seeded, Guid Fed, Guid MainContract, Guid FedContract) : IAsyncDisposable
    {
        public CollaborationHostAccess As(string email) => new(Seeded.As(email).Host);

        public ValueTask DisposeAsync() => Seeded.DisposeAsync();
    }

    private sealed record CollaborationHostAccess(Workbench.Tests.Collaboration.CollaborationHost Host)
    {
        public T Get<T>() where T : notnull => Host.Get<T>();
    }
}
