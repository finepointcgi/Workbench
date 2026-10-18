using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

public sealed class UserDirectory(IWorkbenchDbContextFactory dbFactory, AccessControl access, UserProvisioner provisioner, ICurrentUser currentUser, UserLifecycle lifecycle)
    : IUserDirectory
{
    public async Task<UserDto> GetMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (currentUser is SystemCurrentUser system)
        {
            return new UserDto(system.Email, system.DisplayName, OrgRoles.Admin | OrgRoles.Finance | OrgRoles.Timekeeper | OrgRoles.ResourceManager, DateTime.MinValue, null);
        }

        var user = await provisioner.EnsureUserAsync(access.Email, currentUser.DisplayName == currentUser.Email ? null : currentUser.DisplayName, cancellationToken: cancellationToken);
        return ToDto(user);
    }

    public async Task<IReadOnlyList<UserDto>> SearchAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        take = Math.Clamp(take, 1, 500);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        // Deactivated people aren't offered in pickers; their names still show on their history.
        var users = db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLowerInvariant();
            users = users.Where(u => u.Email.Contains(q) || u.DisplayName.ToLower().Contains(q));
        }

        var found = await users.OrderBy(u => u.DisplayName).ThenBy(u => u.Email).Take(take).ToListAsync(cancellationToken);
        return found.Select(ToDto).ToList();
    }

    public async Task<UserDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return ToDto(await lifecycle.SetRolesAsync(email, roles, access.Email, cancellationToken));
    }

    internal static UserDto ToDto(AppUser u) =>
        new(u.Email, u.DisplayName, AccessControl.ToRoles(u.IsAdmin, u.IsFinance, u.IsTimekeeper, u.IsResourceManager), u.CreatedAtUtc, u.LastSeenAtUtc);
}
