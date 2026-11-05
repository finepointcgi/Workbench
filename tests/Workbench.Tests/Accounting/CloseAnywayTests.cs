using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Collaboration;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Accounting;

/// <summary>
/// A fiscal period (for the tenant, or one company) closes only when its close checklist passes, or when a holder of the
/// close permission says "close anyway" with a reason; that override is audited and a security event.
/// </summary>
public sealed class CloseAnywayTests
{
    private const string Employee = "emp@example.com";

    [Fact]
    public async Task A_failing_checklist_refuses_the_close_unless_the_closer_says_close_anyway()
    {
        await using var kit = await Kit.CreateAsync(failingCheck: true);
        var close = kit.Get<IPeriodCloseService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => close.CloseAsync(kit.October, new ClosePeriodRequest("Month end")));
        Assert.Contains(UnfinishedCheck.Summary, ex.Message);
        Assert.Contains("close anyway", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => close.CloseAsync(kit.October, new ClosePeriodRequest(" ", CloseAnyway: true)));
        Assert.False((await close.GetChecklistAsync(kit.October)).IsClosed);

        var closed = await close.CloseAsync(kit.October, new ClosePeriodRequest("Auditor agreed the module item can follow", CloseAnyway: true));

        Assert.True(closed.IsClosed);
        await using var db = await kit.Db.CreateAsync();
        var audit = await db.Set<AuditEvent>().SingleAsync(a => a.EntityType == nameof(FiscalPeriod) && a.EntityId == kit.October);
        Assert.Equal(PeriodCloseService.ClosedAnywayAction, audit.Action);
        Assert.Equal("Auditor agreed the module item can follow", audit.Reason);
        Assert.Contains(UnfinishedCheck.Summary, audit.AfterJson);
        var security = await db.Set<SecurityEvent>().SingleAsync(e => e.EventType == SecurityEventTypes.PeriodClosedAnyway);
        Assert.Equal(SeededHost.Admin, security.Actor);
        Assert.Contains(UnfinishedCheck.Summary, security.Detail);
    }

    [Fact]
    public async Task Close_anyway_needs_the_close_permission()
    {
        await using var kit = await Kit.CreateAsync(failingCheck: true);
        kit.User.Email = Employee;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            kit.Get<IPeriodCloseService>().CloseAsync(kit.October, new ClosePeriodRequest("Just close it", CloseAnyway: true)));

        await using var db = await kit.Db.CreateAsync();
        Assert.Equal(PeriodStatus.Open, (await db.Set<FiscalPeriod>().SingleAsync(p => p.Id == kit.October)).Status);
    }

    [Fact]
    public async Task A_passing_checklist_closes_without_an_override_and_records_a_plain_close()
    {
        await using var kit = await Kit.CreateAsync(failingCheck: false);

        var closed = await kit.Get<IPeriodCloseService>().CloseAsync(kit.October, new ClosePeriodRequest("Month end"));

        Assert.True(closed.IsClosed);
        await using var db = await kit.Db.CreateAsync();
        Assert.Equal("Closed", (await db.Set<AuditEvent>().SingleAsync(a => a.EntityId == kit.October)).Action);
        Assert.False(await db.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.PeriodClosedAnyway));
    }

    [Fact]
    public async Task A_company_period_closes_only_when_its_own_checklist_passes_or_by_close_anyway()
    {
        await using var kit = await Kit.CreateAsync(failingCheck: false);
        Guid fed;
        await using (var db = await kit.Db.CreateAsync())
        {
            var company = new Company { Code = "FED", LegalName = "Federal Solutions LLC" };
            db.Set<Company>().Add(company);
            db.Set<JournalEntry>().Add(new JournalEntry
            {
                CompanyId = company.Id, Date = new DateOnly(2026, 10, 15), FiscalPeriodId = kit.October, Source = JournalSource.Manual,
                Status = JournalStatus.Draft, Description = "FED accrual draft", CreatedBy = SeededHost.Admin,
            });
            await db.SaveChangesAsync();
            fed = company.Id;
        }

        var close = kit.Get<IPeriodCloseService>();
        Assert.False((await close.GetCompanyChecklistAsync(kit.October, fed)).Checks.Single(c => c.Kind == CloseCheckKind.NothingUnposted).Passed);
        Assert.True((await close.GetCompanyChecklistAsync(kit.October, Company.DefaultId)).Checks.All(c => c.Passed)); // FED's draft isn't MAIN's

        var companies = kit.Get<ICompanyService>();
        var main = await companies.SetPeriodClosedAsync(Company.DefaultId, kit.October, new SetCompanyPeriodRequest(true, "MAIN closes early"));
        Assert.True(main.IsClosed);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => companies.SetPeriodClosedAsync(fed, kit.October, new SetCompanyPeriodRequest(true, "Month end")));
        Assert.Contains("draft journal entr", ex.Message);
        Assert.Contains("close anyway", ex.Message);
        Assert.False((await companies.ListPeriodsAsync(kit.October)).Single(p => p.CompanyId == fed).IsClosed);

        var anyway = await companies.SetPeriodClosedAsync(fed, kit.October, new SetCompanyPeriodRequest(true, "Draft is a memo entry; reversing next month", CloseAnyway: true));

        Assert.True(anyway.IsClosed);
        await using var check = await kit.Db.CreateAsync();
        var audit = await check.Set<AuditEvent>().SingleAsync(a => a.EntityId == fed && a.Action == "PeriodClosedAnyway");
        Assert.Contains("FED accrual draft", audit.AfterJson);
        Assert.Equal("Draft is a memo entry; reversing next month", audit.Reason);
        Assert.True(await check.Set<SecurityEvent>().AnyAsync(e => e.EventType == SecurityEventTypes.PeriodClosedAnyway && e.Target!.StartsWith("FED")));
    }

    [Fact]
    public async Task A_company_close_anyway_needs_the_close_permission()
    {
        await using var kit = await Kit.CreateAsync(failingCheck: true);
        kit.User.Email = Employee;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            kit.Get<ICompanyService>().SetPeriodClosedAsync(Company.DefaultId, kit.October, new SetCompanyPeriodRequest(true, "Close", CloseAnyway: true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Get<IPeriodCloseService>().GetCompanyChecklistAsync(kit.October, Company.DefaultId));
    }

    /// <summary>The full registrations over a fresh database with one open period, signed in as an administrator.</summary>
    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private Kit(ServiceProvider provider, TestUser user, Guid october)
        {
            (_provider, User, October) = (provider, user, october);
            _scope = provider.CreateAsyncScope();
        }

        public TestUser User { get; }

        public Guid October { get; }

        public IWorkbenchDbContextFactory Db => Get<IWorkbenchDbContextFactory>();

        public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

        public static async Task<Kit> CreateAsync(bool failingCheck)
        {
            var user = new TestUser { Email = SeededHost.Admin };
            var services = new ServiceCollection();
            services.AddSingleton(TestDb.CreateFactory());
            services.AddSingleton<IBlobStore>(new MemoryBlobStore());
            services.AddSingleton<ICurrentUser>(user);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddWorkbenchApplication();
            services.UseBusinessEdition();
            if (failingCheck)
            {
                services.AddScoped<IPeriodCloseCheck, UnfinishedCheck>();
            }

            var provider = services.BuildServiceProvider();
            await using var db = await provider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            await RoleStore.AddUserAsync(db, new AppUser { Email = SeededHost.Admin, DisplayName = "Admin" }, OrgRoles.Admin);
            await RoleStore.AddUserAsync(db, new AppUser { Email = Employee, DisplayName = "Emp" }, OrgRoles.Employee);
            if (!await db.Set<Company>().AnyAsync(c => c.Id == Company.DefaultId))
            {
                db.Set<Company>().Add(new Company { Id = Company.DefaultId, Code = "MAIN", LegalName = "Main company" });
            }

            var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
            var period = new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) };
            db.Set<FiscalYear>().Add(year);
            db.Set<FiscalPeriod>().Add(period);
            await db.SaveChangesAsync();
            return new Kit(provider, user, period.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class UnfinishedCheck : IPeriodCloseCheck
    {
        public const string Summary = "Test module not done"; // No apostrophe: audit JSON escapes it.

        public Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken cancellationToken) =>
            Task.FromResult(new CloseCheckDto(CloseCheckKind.AllocationPosted, "Test module", false, 1, Summary, []));
    }
}
