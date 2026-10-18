using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

public sealed class UserDirectory(IWorkbenchDbContextFactory dbFactory, AccessControl access, UserProvisioner provisioner, ICurrentUser currentUser)
    : IUserDirectory
{
    public async Task<UserDto> GetMeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (currentUser is SystemCurrentUser system)
        {
            return new UserDto(system.Email, system.DisplayName, true, DateTime.MinValue, null);
        }

        var user = await provisioner.EnsureUserAsync(access.Email, currentUser.DisplayName == currentUser.Email ? null : currentUser.DisplayName, cancellationToken: cancellationToken);
        return ToDto(user);
    }

    public async Task<IReadOnlyList<UserDto>> SearchAsync(string? query, int take = 20, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        take = Math.Clamp(take, 1, 500);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var users = db.Set<AppUser>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLowerInvariant();
            users = users.Where(u => u.Email.Contains(q) || u.DisplayName.ToLower().Contains(q));
        }

        var found = await users.OrderBy(u => u.DisplayName).ThenBy(u => u.Email).Take(take).ToListAsync(cancellationToken);
        return found.Select(ToDto).ToList();
    }

    public async Task<UserDto> SetAdminAsync(string email, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        email = AccessControl.Normalize(email);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"User {email} was not found.");
        if (user.IsAdmin && !isAdmin && await db.Set<AppUser>().CountAsync(u => u.IsAdmin, cancellationToken) == 1)
        {
            throw new ArgumentException("There must be at least one administrator.", nameof(isAdmin));
        }

        user.IsAdmin = isAdmin;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    internal static UserDto ToDto(AppUser u) => new(u.Email, u.DisplayName, u.IsAdmin, u.CreatedAtUtc, u.LastSeenAtUtc);
}
