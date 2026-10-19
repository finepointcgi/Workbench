using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Companies;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Companies;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Companies;

/// <summary>The real registrations: who manages companies, and company access lists limiting every company-owned read and write.</summary>
public sealed class CompanyAccessTests
{
    [Theory]
    [InlineData(SeededHost.Viewer)]
    [InlineData(SeededHost.Owner)]
    [InlineData(SeededHost.Timekeeper)]
    public async Task Only_finance_and_admins_manage_companies_and_see_consolidations(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(email).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<ICompanyService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("FED", "Federal")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<ICompanyService>().AssignAsync(new AssignCompanyRequest(CompanyRecordKind.Project, Guid.NewGuid(), Company.DefaultId)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IIntercompanyService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IConsolidationService>().GetAsync(ConsolidatedReport.TrialBalance, new ConsolidationRequest(Guid.NewGuid())));

        // Everyone signed in reads their own company context; with one company there's no switcher.
        var context = await host.Get<ICompanyService>().GetContextAsync();
        Assert.False(context.ShowSwitcher);
    }

    [Fact]
    public async Task Finance_manages_companies_but_only_admins_decide_who_uses_them()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(SeededHost.Finance).Host;
        var fed = await host.Get<ICompanyService>().CreateAsync(new SaveCompanyRequest("fed", "Federal Solutions LLC", "987654321"));
        Assert.Equal("FED", fed.Code);
        Assert.Equal("98-7654321", fed.Ein);
        Assert.Equal("USD", fed.HomeCurrencyCode);
        Assert.Equal(2, (await host.Get<ICompanyService>().ListAsync()).Count);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<ICompanyService>().ListUserAccessAsync());
        var someone = (await seeded.As(SeededHost.Admin).Host.Get<ICompanyService>().ListUserAccessAsync()).First();
        seeded.As(SeededHost.Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<ICompanyService>().SetUserAccessAsync(someone.UserId, new SetUserCompanyAccessRequest([fed.Id])));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Get<ICompanyService>().UpdateAsync(Company.DefaultId, new SaveCompanyRequest("MAIN", "Main", IsActive: false)));
    }

    /// <summary>
    /// MAIN has project 1001 and a journal entry; FED has project 4001 (created while the admin worked in FED). Finance is
    /// limited to FED.
    /// </summary>
    private static async Task<(SeededHost Seeded, Guid Fed, ProjectDto Main, ProjectDto Federal, JournalEntryDto MainEntry, Guid Period)> TwoCompaniesAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(SeededHost.Admin).Host;
        await host.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        var accounts = (await host.Get<IChartOfAccountsService>().ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        var period = (await host.Get<IFiscalCalendarService>().CreateFiscalYearAsync(2027, new DateOnly(2026, 10, 1))).First();
        var companies = host.Get<ICompanyService>();
        var fed = await companies.CreateAsync(new SaveCompanyRequest("FED", "Federal Solutions LLC"));
        Assert.True((await companies.GetContextAsync()).ShowSwitcher);

        var projects = host.Get<IProjectService>();
        var main = await projects.CreateProjectAsync(new SaveProjectRequest("1001", "Modernization", null, null, ProjectState.Active, null, null));
        var entry = await host.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(period.Start, "MAIN accrual",
            [new JournalLineInput(accounts["6110"], 75m, 0), new JournalLineInput(accounts["2200"], 0, 75m)]));

        await companies.SelectAsync(new SelectCompanyRequest(fed.Id));
        var federal = await projects.CreateProjectAsync(new SaveProjectRequest("4001", "Cyber assessments", null, null, ProjectState.Active, null, null));
        await host.Get<IJournalService>().CreateAndPostAsync(new SaveJournalEntryRequest(period.Start, "FED accrual",
            [new JournalLineInput(accounts["6110"], 40m, 0), new JournalLineInput(accounts["2200"], 0, 40m)]));
        await companies.SelectAsync(new SelectCompanyRequest(null));

        var finance = (await companies.ListUserAccessAsync()).Single(u => u.Email == SeededHost.Finance);
        await companies.SetUserAccessAsync(finance.UserId, new SetUserCompanyAccessRequest([fed.Id]));
        return (seeded, fed.Id, main, federal, entry, period.Id);
    }

    [Fact]
    public async Task New_records_go_to_the_company_being_worked_in()
    {
        var (seeded, fed, main, federal, entry, _) = await TwoCompaniesAsync();
        await using var _ = seeded;
        var records = await seeded.As(SeededHost.Admin).Host.Get<ICompanyService>().ListRecordsAsync(CompanyRecordKind.Project);
        Assert.Equal(Company.DefaultId, records.Single(r => r.Id == main.Id).CompanyId);
        Assert.Equal(fed, records.Single(r => r.Id == federal.Id).CompanyId);
        Assert.Equal(2, (await seeded.Host.Get<IJournalService>().ListAsync(new JournalQuery())).Count);
    }

    [Fact]
    public async Task People_limited_to_a_company_see_only_its_records_and_ledger()
    {
        var (seeded, fed, main, federal, mainEntry, period) = await TwoCompaniesAsync();
        await using var _ = seeded;
        var host = seeded.As(SeededHost.Finance).Host;

        var context = await host.Get<ICompanyService>().GetContextAsync();
        Assert.Equal([fed], context.Companies.Select(c => c.Id));
        Assert.Equal(fed, context.SelectedCompanyId);
        Assert.False(context.ShowSwitcher);

        var projects = host.Get<IProjectService>();
        Assert.Equal(["4001"], (await projects.ListProjectsAsync()).Select(p => p.Code));
        Assert.Null(await projects.GetProjectAsync(main.Id));
        Assert.NotNull(await projects.GetProjectAsync(federal.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => projects.UpdateProjectAsync(main.Id,
            new SaveProjectRequest("1001", "Renamed", null, null, ProjectState.Active, null, null)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => projects.ListWbsAsync(main.Id));

        var journal = host.Get<IJournalService>();
        Assert.Equal(["FED accrual"], (await journal.ListAsync(new JournalQuery())).Select(e => e.Description));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => journal.GetAsync(mainEntry.Entry.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => journal.ReverseAsync(mainEntry.Entry.Id, new ReverseJournalEntryRequest("Not mine")));

        // The ledger reports only add up FED's lines.
        var tb = await host.Get<IAccountingReportService>().GetTrialBalanceAsync(period);
        Assert.Equal(40m, tb.TotalPeriodDebit);

        // Another company can't be picked, consolidated or posted to.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<ICompanyService>().SelectAsync(new SelectCompanyRequest(Company.DefaultId)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Get<IConsolidationService>().GetAsync(ConsolidatedReport.TrialBalance,
            new ConsolidationRequest(period, [Company.DefaultId, fed])));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => journal.CreateDraftAsync(new SaveJournalEntryRequest(new DateOnly(2026, 10, 1), "Into MAIN", [], CompanyId: Company.DefaultId)));
    }

    [Fact]
    public async Task Working_in_one_company_narrows_an_unrestricted_users_lists_and_reports()
    {
        var (seeded, fed, _, _, _, period) = await TwoCompaniesAsync();
        await using var _ = seeded;
        var host = seeded.As(SeededHost.Admin).Host;
        Assert.Equal(2, (await host.Get<IProjectService>().ListProjectsAsync()).Count);
        Assert.Equal(115m, (await host.Get<IAccountingReportService>().GetTrialBalanceAsync(period)).TotalPeriodDebit);

        await host.Get<ICompanyService>().SelectAsync(new SelectCompanyRequest(Company.DefaultId));
        Assert.Equal(["1001"], (await host.Get<IProjectService>().ListProjectsAsync()).Select(p => p.Code));
        Assert.Equal(75m, (await host.Get<IAccountingReportService>().GetTrialBalanceAsync(period)).TotalPeriodDebit);

        // Consolidation isn't limited by the switcher: it takes every company the user may use.
        var tb = await host.Get<IConsolidationService>().GetAsync(ConsolidatedReport.TrialBalance, new ConsolidationRequest(period));
        Assert.Equal(2, tb.Companies.Count);
        Assert.Contains(tb.Companies, c => c.Id == fed);
    }
}
