using Microsoft.Extensions.Logging;
using Workbench.Application.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>Runs background work once per active tenant, each inside its own <see cref="TenantScope"/>. Suspended and read-only tenants are skipped.</summary>
internal sealed class MultiTenantJobRunner(TenantCatalog catalog, TenantEntitlements entitlements, ILogger<MultiTenantJobRunner> logger) : ITenantJobRunner
{
    public async Task ForEachTenantAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        foreach (var tenant in await catalog.ListActiveAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (TenantScope.Enter(tenant))
            {
                // A read-only tenant (lapsed trial, cancelled) can't save anything, so its jobs would only fail.
                if (entitlements.CurrentReadOnlyMessage() is not null)
                {
                    continue;
                }

                try
                {
                    await work(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Background work failed for tenant {Tenant}", tenant.Slug);
                }
            }
        }
    }
}
