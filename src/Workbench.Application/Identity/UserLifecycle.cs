using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Application.Identity;

/// <summary>Sign-in and invite rules. The web host sets them from configuration.</summary>
public sealed class SignInPolicyOptions
{
    /// <summary>Only invited (or already known) people may sign in. Config <c>Authentication:RequireInvite</c>; default true outside Development.</summary>
    public bool RequireInvite { get; set; } = true;

    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Emails made administrators when they sign in (config <c>Authentication:Admins</c>).</summary>
    public HashSet<string> BootstrapAdmins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Whether a user may use their session: they exist, are active, and their sessions weren't revoked.</summary>
public sealed record UserSecurityState(bool Exists, bool IsActive, DateTime? SessionsRevokedAtUtc)
{
    public static readonly UserSecurityState Missing = new(false, false, null);

    /// <summary>True when a session or token issued at <paramref name="issuedAtUtc"/> is still good.</summary>
    public bool Accepts(DateTime? issuedAtUtc) =>
        Exists && IsActive && (SessionsRevokedAtUtc is null || (issuedAtUtc is { } issued && issued > SessionsRevokedAtUtc.Value));
}

/// <summary>
/// Caches each user's <see cref="UserSecurityState"/> for a few seconds, so checking it on every request and circuit
/// is cheap. Changes made through <see cref="UserLifecycle"/> invalidate the entry at once; other server instances
/// pick them up within <see cref="Ttl"/>.
/// </summary>
public sealed class UserStatusCache(IWorkbenchDbContextFactory dbFactory, TimeProvider clock, Tenancy.TenancyOptions? tenancy = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (UserSecurityState State, DateTime FetchedAtUtc)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<UserSecurityState> GetAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);

        // The MultiTenant host admin site has no user table: its users are the configured host admins.
        if (tenancy?.IsMultiTenant == true && Tenancy.TenantScope.Current is null)
        {
            return tenancy.IsHostAdmin(email) ? new UserSecurityState(true, true, null) : UserSecurityState.Missing;
        }

        var key = Tenancy.TenantScope.Qualify(email);
        var now = clock.GetUtcNow().UtcDateTime;
        if (_entries.TryGetValue(key, out var cached) && now - cached.FetchedAtUtc < Ttl)
        {
            return cached.State;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.IsActive, u.SessionsRevokedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var state = row is null ? UserSecurityState.Missing : new UserSecurityState(true, row.IsActive, row.SessionsRevokedAtUtc);
        _entries[key] = (state, now);
        return state;
    }

    public void Invalidate(string email) => _entries.TryRemove(Tenancy.TenantScope.Qualify(AccessControl.Normalize(email)), out _);
}

/// <summary>
/// User lifecycle without permission checks: the callers decide who may do it (<see cref="UserAdminService"/> for
/// admins, SCIM for the identity provider, access reviews). Enforces the rules that protect the organization from
/// locking itself out, and records a security event for every change.
/// </summary>
public sealed class UserLifecycle(
    IWorkbenchDbContextFactory dbFactory,
    UserStatusCache statusCache,
    SecurityEventLog securityLog,
    TimeProvider clock)
{
    public async Task<AppUser> GetAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"User {email} was not found.");
    }

    /// <summary>Creates a user (SCIM or an import). Throws <see cref="ArgumentException"/> when the email is taken.</summary>
    public async Task<AppUser> CreateAsync(string email, string? displayName, OrgRoles roles, bool active, string? externalId, string actor, CancellationToken cancellationToken = default)
    {
        email = ValidEmail(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<AppUser>().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ArgumentException($"A user with email {email} already exists.", nameof(email));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = new AppUser
        {
            Email = email,
            DisplayName = CleanName(displayName) ?? email.Split('@')[0],
            IsAdmin = roles.IsAdmin(),
            IsFinance = roles.HasFlag(OrgRoles.Finance),
            IsTimekeeper = roles.HasFlag(OrgRoles.Timekeeper),
            IsResourceManager = roles.HasFlag(OrgRoles.ResourceManager),
            IsProjectManager = roles.HasFlag(OrgRoles.ProjectManager),
            IsHr = roles.HasFlag(OrgRoles.Hr),
            IsMaterials = roles.HasFlag(OrgRoles.Materials),
            CreatedAtUtc = now,
            IsActive = active,
            DeactivatedAtUtc = active ? null : now,
            DeactivatedBy = active ? null : actor,
            ExternalId = externalId,
            ScimManaged = actor == ScimActor,
        };
        db.Set<AppUser>().Add(user);
        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(email);
        await securityLog.RecordAsync(SecurityEventTypes.UserCreated, email, detail: $"Roles: {RoleText(roles)}{(active ? "" : "; inactive")}", actor: actor, cancellationToken: cancellationToken);
        return user;
    }

    /// <summary>Updates the name and external id (SCIM). Doesn't touch roles or status.</summary>
    public async Task<AppUser> UpdateProfileAsync(string email, string? displayName, string? externalId, string actor, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await LoadAsync(db, email, cancellationToken);
        if (CleanName(displayName) is { } name)
        {
            user.DisplayName = name;
        }

        if (externalId is not null)
        {
            user.ExternalId = externalId;
        }

        if (actor == ScimActor)
        {
            user.ScimManaged = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<AppUser> SetRolesAsync(string email, OrgRoles roles, string actor, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await LoadAsync(db, email, cancellationToken);
        var before = AccessControl.ToRoles(user.IsAdmin, user.IsFinance, user.IsTimekeeper, user.IsResourceManager, user.IsProjectManager, user.IsHr, user.IsMaterials);
        if (user.IsAdmin && !roles.IsAdmin())
        {
            if (IsSelf(actor, email))
            {
                throw new ArgumentException("You can't remove your own Admin role. Ask another administrator.", nameof(roles));
            }

            await RequireAnotherActiveAdminAsync(db, email, cancellationToken);
        }

        user.IsAdmin = roles.IsAdmin();
        user.IsFinance = roles.HasFlag(OrgRoles.Finance);
        user.IsTimekeeper = roles.HasFlag(OrgRoles.Timekeeper);
        user.IsResourceManager = roles.HasFlag(OrgRoles.ResourceManager);
        user.IsProjectManager = roles.HasFlag(OrgRoles.ProjectManager);
        user.IsHr = roles.HasFlag(OrgRoles.Hr);
        user.IsMaterials = roles.HasFlag(OrgRoles.Materials);
        if (!before.HasFlag(OrgRoles.ProjectManager) && user.IsProjectManager)
        {
            await ProjectWorkspaceOwnership.EnsureForManagerAsync(db, user, cancellationToken); // Owner of their projects' workspaces
        }

        await db.SaveChangesAsync(cancellationToken);
        if (before != roles)
        {
            await securityLog.RecordAsync(SecurityEventTypes.RolesChanged, email, detail: $"{RoleText(before)} → {RoleText(roles)}", actor: actor, cancellationToken: cancellationToken);
        }

        return user;
    }

    /// <summary>Deactivates or reactivates. Deactivating also ends every session and token at once.</summary>
    public async Task<AppUser> SetActiveAsync(string email, bool active, string actor, string? reason = null, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await LoadAsync(db, email, cancellationToken);
        if (user.IsActive == active)
        {
            return user;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (!active)
        {
            if (IsSelf(actor, email))
            {
                throw new ArgumentException("You can't deactivate yourself.", nameof(email));
            }

            if (user.IsAdmin)
            {
                await RequireAnotherActiveAdminAsync(db, email, cancellationToken);
            }

            user.IsActive = false;
            user.DeactivatedAtUtc = now;
            user.DeactivatedBy = actor;
            user.SessionsRevokedAtUtc = now;
        }
        else
        {
            user.IsActive = true;
            user.DeactivatedAtUtc = null;
            user.DeactivatedBy = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(email);
        await securityLog.RecordAsync(active ? SecurityEventTypes.UserReactivated : SecurityEventTypes.UserDeactivated, email, detail: reason, actor: actor, cancellationToken: cancellationToken);
        return user;
    }

    /// <summary>Rejects every session and token issued before now.</summary>
    public async Task<AppUser> RevokeSessionsAsync(string email, string actor, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await LoadAsync(db, email, cancellationToken);
        user.SessionsRevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(email);
        await securityLog.RecordAsync(SecurityEventTypes.SessionsRevoked, email, actor: actor, cancellationToken: cancellationToken);
        return user;
    }

    public async Task<AppUser> SetEmployeeLinkAsync(string email, EmployeeLinkMode mode, Guid? employeeId, string actor, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await LoadAsync(db, email, cancellationToken);
        string detail;
        switch (mode)
        {
            case EmployeeLinkMode.Explicit:
                var id = employeeId ?? throw new ArgumentException("Choose an employee.", nameof(employeeId));
                var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
                    ?? throw WorkbenchNotFoundException.For("Employee", id);
                var taken = await db.Set<AppUser>().AsNoTracking()
                    .Where(u => u.Email != email && u.EmployeeId == id)
                    .Select(u => u.Email)
                    .FirstOrDefaultAsync(cancellationToken);
                if (taken is not null)
                {
                    throw new ArgumentException($"Employee {employee.EmployeeNumber} is already linked to {taken}.", nameof(employeeId));
                }

                user.EmployeeId = id;
                user.EmployeeUnlinked = false;
                detail = $"Linked to employee {employee.EmployeeNumber} {employee.DisplayName}";
                break;
            case EmployeeLinkMode.Unlinked:
                user.EmployeeId = null;
                user.EmployeeUnlinked = true;
                detail = "Unlinked from any employee";
                break;
            default:
                user.EmployeeId = null;
                user.EmployeeUnlinked = false;
                detail = "Linked by matching email";
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.EmployeeLinkChanged, email, detail: detail, actor: actor, cancellationToken: cancellationToken);
        return user;
    }

    /// <summary>The actor name for changes made through SCIM provisioning.</summary>
    public const string ScimActor = "scim";

    internal static string RoleText(OrgRoles roles)
    {
        var names = OrgRoleChecks.All.Where(r => roles.HasFlag(r)).Select(r => r.ToString()).ToList();
        return names.Count == 0 ? "Employee" : string.Join(", ", names);
    }

    internal static string ValidEmail(string? email)
    {
        email = AccessControl.Normalize(email);
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1 || email.Length > 320 || email.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Enter a valid email address.", nameof(email));
        }

        return email;
    }

    internal static string? CleanName(string? name)
    {
        name = name?.Trim();
        return string.IsNullOrEmpty(name) ? null : name.Length > 200 ? name[..200] : name;
    }

    private static bool IsSelf(string actor, string email) => string.Equals(AccessControl.Normalize(actor), email, StringComparison.Ordinal);

    private static async Task RequireAnotherActiveAdminAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken)
    {
        if (!await db.Set<AppUser>().AnyAsync(u => u.Email != email && u.IsAdmin && u.IsActive, cancellationToken))
        {
            throw new ArgumentException("There must be at least one active administrator.", nameof(email));
        }
    }

    private static async Task<AppUser> LoadAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken) =>
        await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"User {email} was not found.");
}
