using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>Where a <see cref="WorkbenchDbContext"/> connects: one fixed database, or the current tenant's.</summary>
public interface IWorkbenchConnectionResolver
{
    string Resolve();
}

/// <summary>SingleTenant: <c>ConnectionStrings:Workbench</c>.</summary>
internal sealed class FixedConnectionResolver(string connectionString) : IWorkbenchConnectionResolver
{
    public string Resolve() => connectionString;
}

/// <summary>MultiTenant: the ambient tenant's database. Without a tenant it throws, so nothing falls back to a shared database.</summary>
internal sealed class TenantConnectionResolver(TenantCatalog catalog) : IWorkbenchConnectionResolver
{
    public string Resolve() => TenantScope.Current is { } tenant ? catalog.GetConnectionString(tenant) : throw new TenantNotResolvedException();
}

/// <summary>
/// The only <see cref="IDbContextFactory{TContext}"/> for <see cref="WorkbenchDbContext"/>; <c>IWorkbenchDbContextFactory</c>
/// wraps it. A singleton that asks <see cref="IWorkbenchConnectionResolver"/> on every call, so feature code stays
/// unaware of tenants. Options are built once per connection string.
/// </summary>
internal sealed class TenantAwareDbContextFactory(DatabaseProvider provider, IWorkbenchConnectionResolver resolver, ILoggerFactory? loggerFactory = null)
    : IDbContextFactory<WorkbenchDbContext>
{
    private readonly ConcurrentDictionary<string, DbContextOptions<WorkbenchDbContext>> _options = new(StringComparer.Ordinal);

    public WorkbenchDbContext CreateDbContext() => new(OptionsFor(resolver.Resolve()));

    public Task<WorkbenchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    private DbContextOptions<WorkbenchDbContext> OptionsFor(string connectionString) =>
        _options.GetOrAdd(connectionString, cs =>
        {
            var builder = new DbContextOptionsBuilder<WorkbenchDbContext>();
            builder.Use(provider, cs);
            if (loggerFactory is not null)
            {
                builder.UseLoggerFactory(loggerFactory);
            }

            return builder.Options;
        });
}
