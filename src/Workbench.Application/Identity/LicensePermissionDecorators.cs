using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;

namespace Workbench.Application.Identity;

/// <summary>Anyone signed in may see the edition (menus show what needs an upgrade); changing the license is Admin.</summary>
internal sealed class PermissionCheckingLicenseService(ILicenseService inner, AccessControl access) : ILicenseService
{
    private const string What = "manage the license";

    public Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetStatusAsync(cancellationToken);
    }

    public async Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminOrganization, What, cancellationToken);
        return await inner.ActivateAsync(request, cancellationToken);
    }

    public async Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminOrganization, What, cancellationToken);
        return await inner.RemoveAsync(cancellationToken);
    }
}
