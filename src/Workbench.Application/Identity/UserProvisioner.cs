using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// Creates a user the first time they sign in and keeps their name and last-seen time current. Bearer-token API
/// calls come in constantly, so last-seen is written at most every few minutes per user.
/// </summary>
public sealed class UserProvisioner(IWorkbenchDbContextFactory dbFactory, TimeProvider clock)
{
    private static readonly TimeSpan SeenInterval = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTime> _lastWritten = new();

    /// <summary>
    /// Ensures the user exists. <paramref name="makeAdmin"/> promotes them (used for configured bootstrap admins);
    /// it never demotes.
    /// </summary>
    public async Task<AppUser> EnsureUserAsync(string email, string? displayName, bool makeAdmin = false, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        ArgumentException.ThrowIfNullOrEmpty(email);
        displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var now = clock.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            user = new AppUser
            {
                Email = email,
                DisplayName = Truncate(displayName ?? DefaultName(email)),
                IsAdmin = makeAdmin,
                CreatedAtUtc = now,
                LastSeenAtUtc = now,
            };
            db.Set<AppUser>().Add(user);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Two first requests raced; the other one created the user.
                await using var retry = await dbFactory.CreateAsync(cancellationToken);
                return await retry.Set<AppUser>().AsNoTracking().FirstAsync(u => u.Email == email, cancellationToken);
            }

            _lastWritten[Tenancy.TenantScope.Qualify(email)] = now;
            return user;
        }

        var changed = false;
        if (displayName is not null && user.DisplayName != Truncate(displayName))
        {
            user.DisplayName = Truncate(displayName);
            changed = true;
        }

        if (makeAdmin && !user.IsAdmin)
        {
            user.IsAdmin = true;
            changed = true;
        }

        if (changed || !_lastWritten.TryGetValue(Tenancy.TenantScope.Qualify(email), out var written) || now - written >= SeenInterval)
        {
            user.LastSeenAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            _lastWritten[Tenancy.TenantScope.Qualify(email)] = now;
        }

        return user;
    }

    private static string DefaultName(string email) => email.Split('@')[0];

    private static string Truncate(string name) => name.Length > 200 ? name[..200] : name;
}
