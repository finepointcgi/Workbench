using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.ContractDetail;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Tests.Identity;

/// <summary>
/// The "Workbench System" context trusted background jobs run in, through the real web host: it may read burdened cost
/// (so Limitation of Funds notices use burdened numbers), it can't write, and an HTTP request can't obtain it.
/// </summary>
public sealed class SystemExecutionTests
{
    [Fact]
    public async Task The_background_funding_check_uses_burdened_cost()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var scopes = Started(factory);
        var contractId = await ContractWithClinsAsync(scopes);

        // What the hourly job used to get: nobody signed in, so burdened cost is refused and it falls back to direct.
        await using (var anonymous = scopes.CreateAsyncScope())
        {
            var direct = await anonymous.ServiceProvider.GetRequiredService<ClinFundingCalculator>().ComputeAsync(contractId, CancellationToken.None);
            Assert.False(direct.IsBurdened);
        }

        await using var system = SystemExecution.CreateScope(scopes);
        Assert.Equal(SystemExecutionContext.Actor, system.ServiceProvider.GetRequiredService<ICurrentUser>().Email);
        var status = await system.ServiceProvider.GetRequiredService<ClinFundingCalculator>().ComputeAsync(contractId, CancellationToken.None);
        Assert.True(status.IsBurdened);
        var (_, isBurdened) = await system.ServiceProvider.GetRequiredService<FundingNoticeChecker>().CheckWithStatusAsync(contractId, CancellationToken.None);
        Assert.True(isBurdened);
    }

    [Fact]
    public async Task Writes_and_other_reads_are_refused_in_the_system_context_and_logged_as_workbench_system()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        var scopes = Started(factory);
        var contractId = await ContractWithClinsAsync(scopes);
        await using var system = SystemExecution.CreateScope(scopes);
        T Get<T>() where T : notnull => system.ServiceProvider.GetRequiredService<T>();
        var period = (await ScopedAsync(scopes, db => db.Set<FiscalPeriod>().AsNoTracking().FirstAsync())).Id;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IIndirectRateService>().ApproveRateAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IIndirectRateService>().PostAllocationAsync(period));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IFiscalCalendarService>().SetPeriodStatusAsync(period, FiscalPeriodState.Closed, "Month end"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IInvoiceService>().DeleteDraftAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IApBillService>().PostAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IJournalService>().ListAsync(new JournalQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IIndirectRateService>().ListRatesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Get<IVendorService>().ListAsync());
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Get<Workbench.Contracts.Contracts.IFundingService>().GetFundingStatusAsync(contractId));

        var denied = await ScopedAsync(scopes, db => db.Set<SecurityEvent>().AsNoTracking()
            .Where(e => e.EventType == SecurityEventTypes.PermissionDenied).Select(e => e.Actor).Distinct().ToListAsync());
        Assert.Equal([SystemExecutionContext.Actor], denied);

        // The context is per scope: a fresh scope is an ordinary (here anonymous) caller again.
        await using var other = scopes.CreateAsyncScope();
        Assert.False(other.ServiceProvider.GetRequiredService<SystemExecutionContext>().IsActive);
        Assert.False(other.ServiceProvider.GetRequiredService<AccessControl>().IsSystemReader);
    }

    [Fact]
    public async Task An_http_request_cant_obtain_the_system_context()
    {
        await using var root = new WorkbenchWebFactory("Development", testAuth: true);
        await using var factory = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, ProbeFilter>()));
        var scopes = Started(factory);
        var projectId = await ScopedAsync(scopes, db => db.Set<Project>().AsNoTracking().Where(p => p.ContractId != null).Select(p => p.Id).FirstAsync());
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, "viewer@example.com");

        // Code running inside a request can't switch the context on, and the request's own scope never has it.
        Assert.Equal("refused; request scope inactive", await client.GetStringAsync(ProbeFilter.Path));

        // So someone without project financials still can't read burdened cost over the API.
        using var response = await client.GetAsync($"/api/v1/projects/{projectId}/burdened-cost?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And the guard holds wherever an HttpContext is flowing, not just in this pipeline.
        var accessor = factory.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext();
        try
        {
            Assert.Throws<InvalidOperationException>(() => SystemExecution.CreateScope(scopes));
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static IServiceScopeFactory Started<TFactory>(TFactory factory) where TFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        _ = factory.Server; // Starts the host, which creates and seeds the development database.
        return factory.Services.GetRequiredService<IServiceScopeFactory>();
    }

    private static Task<Guid> ContractWithClinsAsync(IServiceScopeFactory scopes) =>
        ScopedAsync(scopes, db => db.Set<Clin>().AsNoTracking()
            .Join(db.Set<Contract>().AsNoTracking().Where(c => c.Status == ContractStatus.Active), c => c.ContractId, k => k.Id, (c, _) => c.ContractId)
            .FirstAsync());

    private static async Task<T> ScopedAsync<T>(IServiceScopeFactory scopes, Func<IWorkbenchDbContext, Task<T>> query)
    {
        await using var scope = scopes.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
        return await query(db);
    }

    /// <summary>A test-only endpoint that tries to enter the system context from inside a request.</summary>
    private sealed class ProbeFilter : IStartupFilter
    {
        public const string Path = "/test/system-scope";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, probe => probe.Run(async context =>
            {
                string attempt;
                try
                {
                    await using var scope = SystemExecution.CreateScope(context.RequestServices.GetRequiredService<IServiceScopeFactory>());
                    attempt = "granted";
                }
                catch (InvalidOperationException)
                {
                    attempt = "refused";
                }

                var own = context.RequestServices.GetRequiredService<SystemExecutionContext>().IsActive ? "active" : "inactive";
                await context.Response.WriteAsync($"{attempt}; request scope {own}");
            }));
            next(app);
        };
    }
}
