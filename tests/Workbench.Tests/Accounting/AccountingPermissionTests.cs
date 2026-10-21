using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using Workbench.Tests.Collaboration;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Accounting;

/// <summary>The real registrations: Finance-only GL services, and posting from approvals for any approver.</summary>
public sealed class AccountingPermissionTests
{
    [Theory]
    [InlineData(SeededHost.Viewer)]
    [InlineData(SeededHost.Owner)]
    [InlineData(SeededHost.Timekeeper)]
    public async Task Ledger_services_are_for_finance_and_admins_only(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(email).Host;
        var period = Guid.NewGuid();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IChartOfAccountsService>().ListAccountsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IJournalService>().ListAsync(new JournalQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPostingService>().PostBacklogAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IPeriodCloseService>().GetChecklistAsync(period));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IAccountingReportService>().GetTrialBalanceAsync(period));
    }

    [Theory]
    [InlineData(SeededHost.Finance)]
    [InlineData(SeededHost.Admin)]
    public async Task Finance_and_admins_can_use_the_ledger(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(email).Host;
        Assert.True(await host.Get<IChartOfAccountsService>().InstallDefaultChartAsync() > 0);
        Assert.NotEmpty(await host.Get<IChartOfAccountsService>().ListAccountsAsync());
        Assert.Empty(await host.Get<IJournalService>().ListAsync(new JournalQuery()));
    }

    [Fact]
    public async Task A_supervisor_outside_finance_approving_time_still_posts_it()
    {
        // The full registrations (plus configuration, which the timekeeping options bind from).
        var user = new TestUser { Email = SeededHost.Admin };
        var services = new ServiceCollection();
        services.AddSingleton(TestDb.CreateFactory());
        services.AddSingleton<IBlobStore>(new MemoryBlobStore());
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWorkbenchApplication();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
        await using (var db = await Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = SeededHost.Admin, DisplayName = "Admin" }, OrgRoles.Admin);
            await db.SaveChangesAsync();
        }

        await Get<IChartOfAccountsService>().InstallDefaultChartAsync();

        Guid sheetId;
        await using (var db = await Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
            db.Set<FiscalYear>().Add(year);
            db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) });
            var category = new LaborCategory { Code = "SE2", Name = "Engineer" };
            var boss = new Employee { EmployeeNumber = "E1", Email = SeededHost.Owner, DisplayName = "Pat", DefaultLaborCategoryId = category.Id, HireDate = new DateOnly(2020, 1, 1) };
            var worker = new Employee { EmployeeNumber = "E2", Email = SeededHost.Editor, DisplayName = "Alex", SupervisorId = boss.Id, DefaultLaborCategoryId = category.Id, HireDate = new DateOnly(2020, 1, 1) };
            var project = new Project { Code = "1001", Name = "Program" };
            var wbs = new WbsElement { ProjectId = project.Id, Code = "1001.01", Name = "Work" };
            var sheet = new Timesheet
            {
                EmployeeId = worker.Id, PeriodStart = new DateOnly(2026, 10, 5), PeriodEnd = new DateOnly(2026, 10, 11),
                Status = TimesheetStatus.Submitted, CertifiedAtUtc = DateTime.UtcNow, SubmittedAtUtc = DateTime.UtcNow,
            };
            sheet.Entries.Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = new DateOnly(2026, 10, 6), Hours = 7.5m });
            db.Set<LaborCategory>().Add(category);
            db.Set<Employee>().AddRange(boss, worker);
            db.Set<Project>().Add(project);
            db.Set<WbsElement>().Add(wbs);
            db.Set<Timesheet>().Add(sheet);
            db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = worker.Id, EffectiveFrom = new DateOnly(2020, 1, 1), HourlyCost = 41.37m });
            await db.SaveChangesAsync();
            sheetId = sheet.Id;
        }

        user.Email = SeededHost.Owner; // supervisor, not Finance
        await Get<ITimesheetApprovalService>().ApproveAsync(sheetId);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IJournalService>().ListAsync(new JournalQuery()));

        await using var check = await Get<IWorkbenchDbContextFactory>().CreateAsync();
        var entry = await check.Set<JournalEntry>().Include(e => e.Lines).SingleAsync();
        Assert.Equal(JournalSource.Timesheet, entry.Source);
        Assert.Equal(310.28m, entry.TotalDebit); // 7.5 h × $41.37 = 310.275 → 310.28
        Assert.Equal(SeededHost.Owner, entry.PostedBy);
    }
}
