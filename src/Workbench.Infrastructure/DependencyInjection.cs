using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Workbench.Application;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Collaboration;
using Workbench.Infrastructure.Notifications;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence. <c>Database:Provider</c> selects <c>PostgreSQL</c> (the default) or <c>Sqlite</c> (local
    /// development and tests). <c>Tenancy:Mode</c> selects one database (<c>SingleTenant</c>, connection string
    /// <c>ConnectionStrings:Workbench</c>) or one database per tenant (<c>MultiTenant</c>, found through the catalog
    /// database at <c>ConnectionStrings:Catalog</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = DatabaseProviders.FromConfiguration(configuration);
        var tenancy = configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions();
        services.AddSingleton(new WorkbenchDatabase(provider));
        services.Replace(ServiceDescriptor.Singleton(tenancy));
        services.TryAddSingleton(TimeProvider.System);

        if (tenancy.IsMultiTenant)
        {
            var catalogConnection = configuration.GetConnectionString("Catalog");
            if (string.IsNullOrWhiteSpace(catalogConnection))
            {
                throw new InvalidOperationException("Tenancy:Mode is MultiTenant: set ConnectionStrings:Catalog to the catalog database.");
            }

            services.AddDbContextFactory<CatalogDbContext>(o => o.Use(provider, catalogConnection));
            services.AddSingleton<TenantCatalog>();
            services.AddSingleton<TenantProvisioner>();
            services.AddSingleton<IWorkbenchConnectionResolver, TenantConnectionResolver>();
            services.Replace(ServiceDescriptor.Singleton<ITenantJobRunner, MultiTenantJobRunner>());
        }
        else
        {
            var connectionString = configuration.GetConnectionString("Workbench");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = provider == DatabaseProvider.Sqlite
                    ? "Data Source=workbench.db"
                    : throw new InvalidOperationException("Set ConnectionStrings:Workbench to the PostgreSQL database.");
            }

            services.AddSingleton<IWorkbenchConnectionResolver>(new FixedConnectionResolver(connectionString));
        }

        services.AddSingleton<IDbContextFactory<WorkbenchDbContext>>(sp =>
            new TenantAwareDbContextFactory(provider, sp.GetRequiredService<IWorkbenchConnectionResolver>(), sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IWorkbenchDbContextFactory, WorkbenchDbContextFactory>();
        services.AddSingleton<DatabaseMigrator>();
        services.AddScoped<Contracts.Tenancy.IHostAdminService, HostAdminService>();
        services.AddCollaborationInfrastructure(configuration);
        services.AddNotificationInfrastructure(configuration);
        return services;
    }
}
