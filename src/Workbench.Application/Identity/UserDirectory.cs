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
            return new UserDto(system.Email, system.DisplayName, OrgRoleChecks.Everything, DateTime.MinValue, null);
        }

        var user = await provisioner.EnsureUserAsync(access.Email, currentUser.DisplayName == currentUser.Email ? null : currentUser.DisplayName, cancellationToken: cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ToDtosAsync(db, [user], cancellationToken))[0];
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
        return await ToDtosAsync(db, found, cancellationToken);
    }

    public async Task<UserDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "change people's roles", cancellationToken);
        await access.RequireCanGrantNewRolesAsync(email, BuiltInRoles.IdsOf(roles), cancellationToken);
        var user = await lifecycle.SetRolesAsync(email, roles, access.Email, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ToDtosAsync(db, [user], cancellationToken))[0];
    }

    /// <summary>Users with the built-in roles they hold (one query for all of them).</summary>
    internal static async Task<List<UserDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<AppUser> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();
        var held = (await db.Set<UserRole>().AsNoTracking().Where(r => ids.Contains(r.UserId)).Select(r => new { r.UserId, r.RoleId }).ToListAsync(ct))
            .ToLookup(r => r.UserId, r => r.RoleId);
        var refs = (await RoleStore.RefsAsync(db, held.SelectMany(g => g), ct)).ToDictionary(r => r.Id);
        return users.Select(u => new UserDto(u.Email, u.DisplayName, BuiltInRoles.FlagsOf(held[u.Id]), u.CreatedAtUtc, u.LastSeenAtUtc,
            held[u.Id].Select(refs.GetValueOrDefault).OfType<RoleRef>().OrderBy(r => !r.IsBuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList())).ToList();
    }
}
