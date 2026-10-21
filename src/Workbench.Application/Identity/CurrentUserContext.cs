using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

public sealed class CurrentUserContext(AccessControl access, ICurrentUser currentUser, IWorkbenchDbContextFactory dbFactory) : ICurrentUserContext
{
    public async Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var resolved = await access.GetAccessAsync(cancellationToken);
        var roles = resolved.Roles;
        if (roles.Any(r => r.Scope is not null))
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            roles = await RoleStore.WithScopeTextAsync(db, roles, cancellationToken);
        }

        return new CurrentUserInfo(
            access.Email,
            currentUser.DisplayName,
            resolved.BuiltIn,
            await access.GetEmployeeIdAsync(cancellationToken),
            resolved.Permissions.ToList(),
            roles);
    }
}
