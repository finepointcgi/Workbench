using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

public sealed class CurrentUserContext(AccessControl access, ICurrentUser currentUser) : ICurrentUserContext
{
    public async Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return new CurrentUserInfo(
            access.Email,
            currentUser.DisplayName,
            await access.GetOrgRolesAsync(cancellationToken),
            await access.GetEmployeeIdAsync(cancellationToken));
    }
}
