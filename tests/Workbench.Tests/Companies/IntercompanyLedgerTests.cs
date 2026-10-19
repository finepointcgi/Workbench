using Microsoft.EntityFrameworkCore;
using Workbench.Application.Companies;
using Workbench.Application.Identity;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Companies;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;

namespace Workbench.Tests.Companies;

/// <summary>
/// The ledger's company rules on real postings. Dana (home company MAIN) charges 1,360.00 of labor to project 1001, which
/// is moved to company FED: FED carries the cost and owes MAIN, MAIN carries the payroll and is owed.
/// </summary>
public sealed class IntercompanyLedgerTests
{
    internal sealed record Fixture(LaborPostingTests.Host Host, Guid Fed, Guid ProjectId)
    {
        public LedgerKit Gl => Host.Gl;

        public ConsolidationService Consolidation()
        {
            var factory = Host.Time.Factory;
            return new ConsolidationService(factory, new CompanyScope(factory, new AccessControl(factory, Host.Time.User), Host.Time.User));
        }
    }

    /// <summary>Adds company FED (with the default intercompany accounts unless <paramref name="withAccounts"/> is false) and moves 1001 there.</summary>
    internal static async Task<Fixture> CreateAsync(bool withAccounts = true)
    {
        var host = await LaborPostingTests.CreateAsync();
        var fed = new Company { Code = "FED", LegalName = "Federal Solutions LLC", CreatedAtUtc = DateTime.UtcNow };
        Guid projectId;
        await using (var db = await host.Time.Factory.CreateAsync())
        {
            await CompanyService.EnsureDefaultAsync(db, default);
            db.Set<Company>().Add(fed);
            if (withAccounts)
            {
                db.Set<IntercompanyAccount>().AddRange(
                    new IntercompanyAccount { CompanyId = Company.DefaultId, CounterpartyCompanyId = fed.Id, DueFromAccountId = host.Gl["1180"], DueToAccountId = host.Gl["2180"] },
                    new IntercompanyAccount { CompanyId = fed.Id, CounterpartyCompanyId = Company.DefaultId, DueFromAccountId = host.Gl["1180"], DueToAccountId = host.Gl["2180"] });
            }

            var project = await db.Set<Project>().FirstAsync(p => p.Id == host.Time.Direct.ProjectId);
            project.CompanyId = fed.Id;
            projectId = project.Id;
            await db.SaveChangesAsync();
        }

        return new Fixture(host, fed.Id, projectId);
    }

    private static decimal Net(IEnumerable<JournalLine> lines) => lines.Sum(l => l.Debit - l.Credit);

    [Fact]
    public async Task Labor_charged_to_another_companys_project_posts_due_to_and_due_from_so_both_ledgers_balance()
    {
        var f = await CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(f.Host);

        var entry = Assert.Single(await f.Gl.EntriesAsync(JournalSource.Timesheet));
        Assert.Equal(Company.DefaultId, entry.CompanyId); // Dana's home company accrues the labor.
        Assert.Equal(4, entry.Lines.Count);

        var fedLines = entry.Lines.Where(l => l.CompanyId == f.Fed).ToList();
        var mainLines = entry.Lines.Where(l => l.CompanyId == Company.DefaultId).ToList();
        Assert.Equal(0m, Net(fedLines));
        Assert.Equal(0m, Net(mainLines));

        // FED: Dr 5010 direct labor on its project, Cr 2180 due to MAIN.
        Assert.Contains(fedLines, l => l.AccountId == f.Gl["5010"] && l.Debit == 1360m && l.ProjectId == f.ProjectId);
        var dueTo = Assert.Single(fedLines, l => l.AccountId == f.Gl["2180"]);
        Assert.Equal(1360m, dueTo.Credit);
        Assert.Equal(Company.DefaultId, dueTo.IntercompanyCompanyId);

        // MAIN: Cr 2100 accrued payroll, Dr 1180 due from FED.
        Assert.Contains(mainLines, l => l.AccountId == f.Gl["2100"] && l.Credit == 1360m);
        var dueFrom = Assert.Single(mainLines, l => l.AccountId == f.Gl["1180"]);
        Assert.Equal(1360m, dueFrom.Debit);
        Assert.Equal(f.Fed, dueFrom.IntercompanyCompanyId);
    }

    [Fact]
    public async Task Reopening_the_timesheet_reverses_both_companies_lines()
    {
        var f = await CreateAsync();
        var id = await LaborPostingTests.ApproveDanaAsync(f.Host);
        f.Host.Time.SignInAs(f.Host.Time.Dana);
        await f.Host.Timesheets.ReopenAsync(id, "Wrong project");

        var lines = (await f.Gl.EntriesAsync()).Where(e => e.Status != JournalStatus.Draft).SelectMany(e => e.Lines).ToList();
        Assert.Equal(0m, Net(lines.Where(l => l.CompanyId == f.Fed && l.AccountId == f.Gl["2180"])));
        Assert.Equal(0m, Net(lines.Where(l => l.CompanyId == Company.DefaultId && l.AccountId == f.Gl["1180"])));
        Assert.All(lines.GroupBy(l => l.CompanyId), g => Assert.Equal(0m, Net(g)));
    }

    [Fact]
    public async Task Crossing_companies_without_intercompany_accounts_is_refused()
    {
        var f = await CreateAsync(withAccounts: false);
        var date = new DateOnly(2026, 10, 20);
        var request = new SaveJournalEntryRequest(date, "Reclass to FED",
            [new JournalLineInput(f.Gl["5010"], 100m, 0, ProjectId: f.ProjectId), new JournalLineInput(f.Gl["2100"], 0, 100m)], CompanyId: Company.DefaultId);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => f.Gl.Journal.CreateAndPostAsync(request));
        Assert.Contains("intercompany accounts", error.Message);
        Assert.Empty(await f.Gl.EntriesAsync());
    }

    [Fact]
    public async Task A_company_that_closed_the_period_refuses_postings_while_the_other_still_posts()
    {
        var f = await CreateAsync();
        var period = f.Host.Time.Periods.First(p => p.Name == "2026-10");
        await using (var db = await f.Host.Time.Factory.CreateAsync())
        {
            db.Set<CompanyPeriodClose>().Add(new CompanyPeriodClose { CompanyId = f.Fed, FiscalPeriodId = period.Id, IsClosed = true, ChangedBy = "test", Reason = "Closed early" });
            await db.SaveChangesAsync();
        }

        var date = new DateOnly(2026, 10, 20);
        var intoFed = new SaveJournalEntryRequest(date, "FED accrual", [new JournalLineInput(f.Gl["5010"], 50m, 0, ProjectId: f.ProjectId), new JournalLineInput(f.Gl["2200"], 0, 50m)], CompanyId: f.Fed);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => f.Gl.Journal.CreateAndPostAsync(intoFed));
        Assert.Contains("closed for FED", error.Message);

        var intoMain = new SaveJournalEntryRequest(date, "MAIN accrual", [new JournalLineInput(f.Gl["6110"], 50m, 0), new JournalLineInput(f.Gl["2200"], 0, 50m)], CompanyId: Company.DefaultId);
        var posted = await f.Gl.Journal.CreateAndPostAsync(intoMain);
        Assert.Equal(JournalEntryState.Posted, posted.Entry.Status);
    }

    [Fact]
    public async Task An_account_limited_to_one_company_is_refused_in_another()
    {
        var f = await CreateAsync();
        await using (var db = await f.Host.Time.Factory.CreateAsync())
        {
            (await db.Set<Account>().FirstAsync(a => a.Number == "2200")).CompanyId = f.Fed;
            await db.SaveChangesAsync();
        }

        var request = new SaveJournalEntryRequest(new DateOnly(2026, 10, 20), "MAIN accrual",
            [new JournalLineInput(f.Gl["6110"], 50m, 0), new JournalLineInput(f.Gl["2200"], 0, 50m)], CompanyId: Company.DefaultId);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => f.Gl.Journal.CreateAndPostAsync(request));
        Assert.Contains("belongs to another company", error.Message);
    }

    [Fact]
    public async Task Single_company_postings_are_unchanged()
    {
        var host = await LaborPostingTests.CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(host);

        var entry = Assert.Single(await host.Gl.EntriesAsync(JournalSource.Timesheet));
        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(Company.DefaultId, entry.CompanyId);
        Assert.All(entry.Lines, l => Assert.Equal(Company.DefaultId, l.CompanyId));
        Assert.All(entry.Lines, l => Assert.Null(l.IntercompanyCompanyId));
    }

    // ---- Consolidation ----

    [Fact]
    public async Task The_consolidated_trial_balance_shows_each_company_the_eliminations_and_the_total()
    {
        var f = await CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(f.Host);
        var period = f.Host.Time.Periods.First(p => p.Name == "2026-10");

        var tb = await f.Consolidation().GetAsync(ConsolidatedReport.TrialBalance, new ConsolidationRequest(period.Id));
        Assert.Equal(["FED", "MAIN"], tb.Companies.Select(c => c.Code));
        ConsolidationRowDto Row(string number) => Assert.Single(tb.Rows, r => r.AccountNumber == number);

        // Companies in code order: FED, MAIN.
        Assert.Equal([0m, 1360m], Row("1180").Companies);
        Assert.Equal(-1360m, Row("1180").Eliminations);
        Assert.Equal(0m, Row("1180").Total);
        Assert.Equal([-1360m, 0m], Row("2180").Companies);
        Assert.Equal(1360m, Row("2180").Eliminations);
        Assert.Equal(0m, Row("2180").Total);
        Assert.Equal(1360m, Row("5010").Total);
        Assert.Equal(-1360m, Row("2100").Total);

        var debits = Assert.Single(tb.Rows, r => r.Label == "Total debits");
        var credits = Assert.Single(tb.Rows, r => r.Label == "Total credits");
        Assert.Equal(debits.Total, credits.Total);
        Assert.Equal(debits.Companies, credits.Companies);

        Assert.Equal(2, tb.Eliminations.Count);
        Assert.Contains(tb.Eliminations, e => e is { CompanyCode: "MAIN", CounterpartyCode: "FED", AccountNumber: "1180", Amount: -1360m });
        Assert.Contains(tb.Eliminations, e => e is { CompanyCode: "FED", CounterpartyCode: "MAIN", AccountNumber: "2180", Amount: 1360m });
        Assert.Empty(tb.Notes);
    }

    [Fact]
    public async Task Consolidated_statements_drop_intercompany_balances_and_keep_the_labor_cost()
    {
        var f = await CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(f.Host);
        var period = f.Host.Time.Periods.First(p => p.Name == "2026-10");
        var service = f.Consolidation();

        var income = await service.GetAsync(ConsolidatedReport.IncomeStatement, new ConsolidationRequest(period.Id));
        var labor = Assert.Single(income.Rows, r => r.AccountNumber == "5010");
        Assert.Equal([1360m, 0m], labor.Companies);
        Assert.Equal(0m, labor.Eliminations);
        Assert.Equal(1360m, labor.Total);
        Assert.Equal(-1360m, Assert.Single(income.Rows, r => r.Label == "Net income").Total);

        var balance = await service.GetAsync(ConsolidatedReport.BalanceSheet, new ConsolidationRequest(period.Id));
        var assets = Assert.Single(balance.Rows, r => r.Label == "Total assets");
        Assert.Equal([0m, 1360m], assets.Companies);
        Assert.Equal(0m, assets.Total);
        var liabilitiesAndEquity = Assert.Single(balance.Rows, r => r.Label == "Total liabilities and equity");
        Assert.Equal(assets.Total, liabilitiesAndEquity.Total);
        Assert.Equal(assets.Companies, liabilitiesAndEquity.Companies);
    }

    [Fact]
    public async Task A_company_outside_the_consolidation_keeps_its_balances_and_one_sided_entries_are_flagged()
    {
        var f = await CreateAsync();
        await LaborPostingTests.ApproveDanaAsync(f.Host);
        var period = f.Host.Time.Periods.First(p => p.Name == "2026-10");

        // MAIN alone: nothing is eliminated, and the note says FED's balances aren't.
        var alone = await f.Consolidation().GetAsync(ConsolidatedReport.TrialBalance, new ConsolidationRequest(period.Id, [Company.DefaultId]));
        Assert.Equal(1360m, Assert.Single(alone.Rows, r => r.AccountNumber == "1180").Total);
        Assert.Empty(alone.Eliminations);
        Assert.Contains(alone.Notes, n => n.Contains("FED", StringComparison.Ordinal) && n.Contains("aren't eliminated", StringComparison.Ordinal));

        // A one-sided intercompany entry (only FED's books) makes the two companies disagree.
        await using (var db = await f.Host.Time.Factory.CreateAsync())
        {
            var entry = new JournalEntry { CompanyId = f.Fed, Date = new DateOnly(2026, 10, 22), FiscalPeriodId = period.Id, Description = "One-sided", CreatedBy = "test" };
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, CompanyId = f.Fed, LineNumber = 1, AccountId = f.Gl["6110"], Debit = 25m });
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, CompanyId = f.Fed, IntercompanyCompanyId = Company.DefaultId, LineNumber = 2, AccountId = f.Gl["2180"], Credit = 25m });
            db.Set<JournalEntry>().Add(entry);
            await f.Gl.Ledger.PostAsync(db, entry, default);
            await db.SaveChangesAsync();
        }

        var both = await f.Consolidation().GetAsync(ConsolidatedReport.BalanceSheet, new ConsolidationRequest(period.Id));
        Assert.Contains(both.Notes, n => n.Contains("disagree", StringComparison.Ordinal) && n.Contains("25.00", StringComparison.Ordinal));
    }
}
