using Microsoft.EntityFrameworkCore;
using Workbench.Application.Licensing;
using Workbench.Application.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>In MultiTenant mode the hosting service sets each tenant's plan in the catalog (Host admin); the current tenant's plan decides its edition.</summary>
public sealed class TenantPlanSource(IDbContextFactory<CatalogDbContext> catalog) : IHostedPlanSource
{
    public async Task<string?> GetPlanAsync(CancellationToken cancellationToken)
    {
        if (TenantScope.Current is not { } tenant)
        {
            return null;
        }

        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        return await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.Id).Select(t => t.Plan).FirstOrDefaultAsync(cancellationToken);
    }
}
