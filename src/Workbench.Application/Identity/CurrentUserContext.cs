using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

public sealed class CurrentUserContext(AccessControl access, ICurrentUser currentUser) : ICurrentUserContext
{
    public async Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var resolved = await access.GetAccessAsync(cancellationToken);
        return new CurrentUserInfo(
            access.Email,
            currentUser.DisplayName,
            resolved.BuiltIn,
            await access.GetEmployeeIdAsync(cancellationToken),
            resolved.Permissions.ToList(),
            resolved.Roles);
    }
}
