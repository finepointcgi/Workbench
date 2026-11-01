using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Collaboration;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Accounting;

/// <summary>Setup › Fiscal calendar's Close goes through the period-close checklist.</summary>
public sealed class FiscalCalendarCloseTests
{
    [Fact]
    public async Task A_period_with_an_unapproved_timesheet_cant_be_closed_from_setup()
    {
        var host = await LaborPostingTests.CreateAsync();
        var calendar = new ChecklistFiscalCalendarService(host.Time.Calendar, host.Gl.Close);
        var october = host.Time.Periods.Single(p => p.Name == "2026-10").Id;
        var id = await host.Time.SubmittedDanaWeekAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Closed, "Month end"));
        Assert.Contains("timesheet", ex.Message);
        Assert.Contains(await calendar.ListPeriodsAsync(), p => p.Id == october && p.Status == FiscalPeriodState.Open);

        // Approved and posted: the checklist passes and Setup can close it, then reopen it as before.
        host.Time.SignInAs(host.Time.Pat);
        await host.Approvals.ApproveAsync(id);
        var closed = await calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Closed, "Month end");
        Assert.Equal(FiscalPeriodState.Closed, closed.Status);

        var reopened = await calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Open, "Adjustment");
        Assert.Equal(FiscalPeriodState.Open, reopened.Status);
    }

    [Fact]
    public async Task With_the_real_registrations_setup_close_runs_the_checks_other_modules_register()
    {
        // The full registrations (indirect rates add "Allocation posted", billing the unbilled accrual) plus one more
        // module check that fails, standing in for a module whose item isn't done yet.
        var services = new ServiceCollection();
        services.AddSingleton(TestDb.CreateFactory());
        services.AddSingleton<IBlobStore>(new MemoryBlobStore());
        services.AddSingleton<ICurrentUser>(new TestUser { Email = SeededHost.Admin });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWorkbenchApplication();
        services.UseBusinessEdition();
        services.AddScoped<IPeriodCloseCheck, UnfinishedCheck>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        Guid october;
        await using (var db = await Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = SeededHost.Admin, DisplayName = "Admin" }, OrgRoles.Admin);
            var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
            var period = new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) };
            db.Set<FiscalYear>().Add(year);
            db.Set<FiscalPeriod>().Add(period);
            await db.SaveChangesAsync();
            october = period.Id;
        }

        var checklist = await Get<IPeriodCloseService>().GetChecklistAsync(october);
        Assert.Contains(checklist.Checks, c => c.Kind == CloseCheckKind.AllocationPosted && c.Passed);
        Assert.Contains(checklist.Checks, c => c.Kind == CloseCheckKind.UnbilledAccrual && c.Passed);

        var calendar = Get<IFiscalCalendarService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => calendar.SetPeriodStatusAsync(october, FiscalPeriodState.Closed, "Month end"));
        Assert.Contains(UnfinishedCheck.Summary, ex.Message);
        Assert.Contains(await calendar.ListPeriodsAsync(), p => p.Id == october && p.Status == FiscalPeriodState.Open);
    }

    private sealed class UnfinishedCheck : IPeriodCloseCheck
    {
        public const string Summary = "Test module isn't done";

        public Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken) =>
            Task.FromResult(new CloseCheckDto(CloseCheckKind.AllocationPosted, "Test module", false, 1, Summary, []));
    }
}
