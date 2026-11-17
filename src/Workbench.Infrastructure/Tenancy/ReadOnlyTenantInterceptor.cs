using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Workbench.Contracts;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>
/// Read-only mode for a hosted tenant whose trial has lapsed or whose subscription is cancelled or suspended
/// (<see cref="TenantEntitlements"/>): every save that changes business data is refused with a message that says why, so no
/// feature has to know. What keeps working is what lets people sign in and an administrator export their data: the security
/// log, one-time account links and password checks, and the "last seen" stamps on a user. Reads are never affected, and nothing is deleted.
/// </summary>
public sealed class ReadOnlyTenantInterceptor(TenantEntitlements entitlements) : SaveChangesInterceptor
{
    private static readonly HashSet<Type> AlwaysAllowed = [typeof(SecurityEvent), typeof(AccountToken), typeof(PasswordCredential)];
    private static readonly HashSet<string> UserStamps = [nameof(AppUser.LastSeenAtUtc), nameof(AppUser.LastSignInAtUtc)];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Check(DbContext? context)
    {
        if (context is null || entitlements.CurrentReadOnlyMessage() is not { } message)
        {
            return;
        }

        if (context.ChangeTracker.Entries().Any(IsBlocked))
        {
            throw new WorkbenchEditionException(message);
        }
    }

    private static bool IsBlocked(EntityEntry entry)
    {
        if (entry.State is EntityState.Detached or EntityState.Unchanged)
        {
            return false;
        }

        var type = entry.Entity.GetType();
        if (AlwaysAllowed.Contains(type))
        {
            return false;
        }

        return !(entry.Entity is AppUser && entry.State == EntityState.Modified && entry.Properties.Where(p => p.IsModified).All(p => UserStamps.Contains(p.Metadata.Name)));
    }
}
