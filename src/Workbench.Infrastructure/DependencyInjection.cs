using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Infrastructure.Collaboration;
using Workbench.Infrastructure.Notifications;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence. <c>Database:Provider</c> selects <c>Sqlite</c> (local dev default)
    /// or <c>PostgreSQL</c>; the connection string is <c>ConnectionStrings:Workbench</c>.
    /// </summary>
    public static IServiceCollection AddWorkbenchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var connectionString = configuration.GetConnectionString("Workbench") ?? "Data Source=workbench.db";

        services.AddDbContextFactory<WorkbenchDbContext>(options =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "sqlite":
                    options.UseSqlite(connectionString);
                    break;
                case "postgresql":
                case "postgres":
                    options.UseNpgsql(connectionString);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Database:Provider '{provider}'. Use Sqlite or PostgreSQL.");
            }
        });
        services.AddSingleton<IWorkbenchDbContextFactory, WorkbenchDbContextFactory>();
        services.AddCollaborationInfrastructure(configuration);
        services.AddNotificationInfrastructure(configuration);
        return services;
    }
}
