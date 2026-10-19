using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Support;

namespace Workbench.Application.Support;

/// <summary>
/// Support requests from every active tenant, for host administrators on the host site (MultiTenant). Reads each
/// tenant's database in turn through <see cref="ITenantJobRunner"/>; a tenant that fails to answer is skipped and logged.
/// </summary>
public sealed class HostSupportService(
    TenancyOptions tenancy, ITenantJobRunner tenants, IWorkbenchDbContextFactory dbFactory, ICurrentUser currentUser) : IHostSupportService
{
    public async Task<IReadOnlyList<HostSupportRequestDto>> ListAsync(SupportStatus? status = null, CancellationToken cancellationToken = default)
    {
        if (!tenancy.IsMultiTenant || TenantScope.Current is not null || !tenancy.IsHostAdmin(currentUser.Email))
        {
            throw new WorkbenchForbiddenException("Only host administrators can see support requests across organizations, on the host site.");
        }

        var all = new List<HostSupportRequestDto>();
        await tenants.ForEachTenantAsync(async ct =>
        {
            if (TenantScope.Current is not { } tenant)
            {
                return;
            }

            await using var db = await dbFactory.CreateAsync(ct);
            var rows = await SupportService.ListAsync(db, status, ct);
            lock (all)
            {
                all.AddRange(rows.Select(r => new HostSupportRequestDto(tenant.Slug, tenant.DisplayName, r)));
            }
        }, cancellationToken);

        return all.OrderByDescending(r => r.Request.CreatedAtUtc).ToList();
    }
}
