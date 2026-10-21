using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

/// <summary>Roles to give someone: built-in roles as flags plus custom roles by id (invites, SSO groups, API keys).</summary>
public sealed record RoleGrant(OrgRoles BuiltIn, IReadOnlyList<Guid> Custom)
{
    public static RoleGrant None { get; } = new(OrgRoles.Employee, []);

    public static RoleGrant Of(OrgRoles roles, IEnumerable<Guid>? custom = null) => new(roles, (custom ?? []).Distinct().ToList());

    /// <summary>Every role id it grants (Employee is implicit and left out).</summary>
    public IReadOnlyList<Guid> RoleIds => BuiltInRoles.IdsOf(BuiltIn).Concat(Custom).Distinct().ToList();

    public RoleGrant With(OrgRoles roles) => this with { BuiltIn = BuiltIn | roles };

    public RoleGrant Union(RoleGrant other) => new(BuiltIn | other.BuiltIn, Custom.Union(other.Custom).ToList());

    /// <summary>True when both grant the same roles.</summary>
    public bool SameRolesAs(RoleGrant other) => RoleIds.ToHashSet().SetEquals(other.RoleIds);

    /// <summary>Splits role ids into built-in flags and custom ids.</summary>
    public static RoleGrant FromIds(IEnumerable<Guid> roleIds)
    {
        var ids = roleIds.ToList();
        return new RoleGrant(BuiltInRoles.FlagsOf(ids), ids.Where(id => !BuiltInRoles.IsBuiltIn(id)).Distinct().ToList());
    }
}

/// <summary>A user's resolved access: the built-in roles they hold, every role they hold, and the union of their permissions.</summary>
public sealed record ResolvedAccess(OrgRoles BuiltIn, IReadOnlyList<RoleRef> Roles, PermissionSet Permissions)
{
    public static ResolvedAccess None { get; } = new(OrgRoles.Employee, [], PermissionSet.Empty);

    public static ResolvedAccess Everything { get; } = new(OrgRoleChecks.Everything, [], PermissionSet.All);
}

/// <summary>
/// Role assignments and effective permissions, read straight from the database (nothing is cached, so changes apply
/// on the next call). Built-in roles' permissions come from <see cref="PermissionCatalog"/>; custom roles' from
/// <see cref="RolePermission"/> rows. No permission checks here: callers decide who may change what.
/// </summary>
public static class RoleStore
{
    /// <summary>
    /// Adds any missing built-in role rows to the context (not saved). Migrated databases are seeded with them; InMemory
    /// test databases and databases created before roles existed get them on first use.
    /// </summary>
    public static async Task EnsureBuiltInRolesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var ids = BuiltInRoles.All.Select(d => d.Id).ToList();
        var existing = await db.Set<Role>().Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        var tracked = db.Set<Role>().Local.Select(r => r.Id).ToHashSet();
        foreach (var d in BuiltInRoles.All.Where(d => !existing.Contains(d.Id) && !tracked.Contains(d.Id)))
        {
            db.Set<Role>().Add(new Role
            {
                Id = d.Id,
                Name = d.Name,
                NormalizedName = NormalizeName(d.Name),
                Description = d.Description,
                BuiltIn = (int)d.Role,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Adds <paramref name="user"/> holding the built-in <paramref name="roles"/> (seeds and tests). Tracked, not saved.</summary>
    public static async Task<AppUser> AddUserAsync(IWorkbenchDbContext db, AppUser user, OrgRoles roles, CancellationToken ct = default)
    {
        db.Set<AppUser>().Add(user);
        await GrantBuiltInAsync(db, user, roles, ct);
        return user;
    }

    /// <summary>Gives an existing or new user the built-in <paramref name="roles"/> too (tracked, not saved; no audit row: seeds and tests).</summary>
    public static async Task GrantBuiltInAsync(IWorkbenchDbContext db, AppUser user, OrgRoles roles, CancellationToken ct = default)
    {
        var ids = BuiltInRoles.IdsOf(roles);
        if (ids.Count == 0)
        {
            return;
        }

        await EnsureBuiltInRolesAsync(db, ct);
        var held = await db.Set<UserRole>().Where(r => r.UserId == user.Id).Select(r => r.RoleId).ToListAsync(ct);
        held.AddRange(db.Set<UserRole>().Local.Where(r => r.UserId == user.Id).Select(r => r.RoleId));
        foreach (var id in ids.Where(id => !held.Contains(id)))
        {
            db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = id, AssignedAtUtc = DateTime.UtcNow, AssignedBy = "seed" });
        }
    }

    /// <summary>The access of the active user with <paramref name="email"/> (normalized); <see cref="ResolvedAccess.None"/> when there's none.</summary>
    public static async Task<ResolvedAccess> ResolveAsync(IWorkbenchDbContext db, string email, CancellationToken ct)
    {
        if (email.Length == 0)
        {
            return ResolvedAccess.None;
        }

        var held = await (
            from u in db.Set<AppUser>().AsNoTracking()
            where u.Email == email && u.IsActive
            join ur in db.Set<UserRole>().AsNoTracking() on u.Id equals ur.UserId
            join r in db.Set<Role>().AsNoTracking() on ur.RoleId equals r.Id
            select new { r.Id, r.Name, r.BuiltIn }).ToListAsync(ct);
        return await ResolveRolesAsync(db, held.Select(h => (h.Id, h.Name, h.BuiltIn)), ct);
    }

    /// <summary>The access the given role ids grant together (for invites, SSO mappings and API keys).</summary>
    public static async Task<ResolvedAccess> ResolveRoleIdsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        if (roleIds.Count == 0)
        {
            return ResolvedAccess.None;
        }

        var ids = roleIds.ToList();
        var roles = await db.Set<Role>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Name, r.BuiltIn }).ToListAsync(ct);
        var found = roles.Select(r => (r.Id, r.Name, r.BuiltIn)).ToList();

        // Built-in roles count even before their rows exist (InMemory test databases).
        found.AddRange(ids.Where(id => roles.All(r => r.Id != id)).Select(BuiltInRoles.Find).OfType<BuiltInRoles.Definition>()
            .Select(d => (d.Id, d.Name, (int?)(int)d.Role)));
        return await ResolveRolesAsync(db, found, ct);
    }

    private static async Task<ResolvedAccess> ResolveRolesAsync(IWorkbenchDbContext db, IEnumerable<(Guid Id, string Name, int? BuiltIn)> held, CancellationToken ct)
    {
        var list = held.DistinctBy(h => h.Id).ToList();
        var builtIn = list.Where(h => h.BuiltIn is not null).Aggregate(OrgRoles.Employee, (all, h) => all | (OrgRoles)h.BuiltIn!.Value);
        var permissions = PermissionCatalog.ForBuiltInRoles(builtIn);
        var custom = list.Where(h => h.BuiltIn is null).Select(h => h.Id).ToList();
        if (custom.Count > 0)
        {
            var keys = await db.Set<RolePermission>().AsNoTracking().Where(p => custom.Contains(p.RoleId)).Select(p => p.Permission).Distinct().ToListAsync(ct);
            permissions = permissions.Union(PermissionSet.Of(keys.Where(PermissionCatalog.Exists)));
        }

        var refs = list
            .OrderBy(h => h.BuiltIn is null)
            .ThenBy(h => h.BuiltIn is { } b ? BuiltInRoles.All.ToList().FindIndex(d => (int)d.Role == b) : 0)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Select(h => new RoleRef(h.Id, h.Name, h.BuiltIn is not null))
            .ToList();
        return new ResolvedAccess(builtIn, refs, permissions);
    }

    /// <summary>The built-in roles held by the active user with <paramref name="email"/>.</summary>
    public static async Task<OrgRoles> BuiltInRolesOfAsync(IWorkbenchDbContext db, string email, CancellationToken ct) =>
        (await ResolveAsync(db, email, ct)).BuiltIn;

    /// <summary>Ids of the roles the user holds (active or not).</summary>
    public static async Task<List<Guid>> RoleIdsOfAsync(IWorkbenchDbContext db, Guid userId, CancellationToken ct) =>
        await db.Set<UserRole>().AsNoTracking().Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct);

    /// <summary>Role ids that grant <paramref name="permission"/>: built-in roles from the catalog, custom roles from their rows.</summary>
    public static async Task<List<Guid>> RolesGrantingAsync(IWorkbenchDbContext db, string permission, CancellationToken ct)
    {
        var ids = BuiltInRoles.All.Where(d => d.Permissions.Has(permission)).Select(d => d.Id).ToList();
        ids.AddRange(await db.Set<RolePermission>().AsNoTracking().Where(p => p.Permission == permission).Select(p => p.RoleId).Distinct().ToListAsync(ct));
        return ids;
    }

    /// <summary>Emails of the active users holding <paramref name="permission"/> (for notifying them).</summary>
    /// <remarks>
    /// With <paramref name="includeAdmins"/> false, holding the built-in Admin role alone doesn't count (for notices that
    /// went to the Finance role only, not to every administrator).
    /// </remarks>
    public static async Task<List<string>> EmailsWithPermissionAsync(IWorkbenchDbContext db, string permission, CancellationToken ct, bool includeAdmins = true)
    {
        var roles = await RolesGrantingAsync(db, permission, ct);
        if (!includeAdmins)
        {
            roles.Remove(BuiltInRoles.Admin.Id);
        }

        return await EmailsWithRolesAsync(db, roles, ct);
    }

    /// <summary>Emails of the active users holding any of <paramref name="roleIds"/>.</summary>
    public static async Task<List<string>> EmailsWithRolesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        if (roleIds.Count == 0)
        {
            return [];
        }

        var ids = roleIds.ToList();
        return await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.IsActive && db.Set<UserRole>().Any(r => r.UserId == u.Id && ids.Contains(r.RoleId)))
            .Select(u => u.Email)
            .ToListAsync(ct);
    }

    /// <summary>
    /// True when the active user with <paramref name="email"/> holds <paramref name="permission"/>. With
    /// <paramref name="includeAdmins"/> false, the built-in Admin role alone doesn't count.
    /// </summary>
    public static async Task<bool> HoldsAsync(IWorkbenchDbContext db, string email, string permission, CancellationToken ct, bool includeAdmins = true)
    {
        var roles = await RolesGrantingAsync(db, permission, ct);
        if (!includeAdmins)
        {
            roles.Remove(BuiltInRoles.Admin.Id);
        }

        return await db.Set<AppUser>().AnyAsync(u => u.Email == email && u.IsActive && db.Set<UserRole>().Any(r => r.UserId == u.Id && roles.Contains(r.RoleId)), ct);
    }

    /// <summary>True when an active user other than <paramref name="exceptEmail"/> holds the built-in Admin role.</summary>
    public static Task<bool> AnotherActiveAdminAsync(IWorkbenchDbContext db, string exceptEmail, CancellationToken ct)
    {
        var admin = BuiltInRoles.Admin.Id;
        return db.Set<AppUser>().AnyAsync(u => u.Email != exceptEmail && u.IsActive && db.Set<UserRole>().Any(r => r.UserId == u.Id && r.RoleId == admin), ct);
    }

    /// <summary>
    /// Makes <paramref name="user"/> hold exactly <paramref name="roleIds"/> (Employee is implicit and dropped). Changes are
    /// tracked, not saved. Unknown role ids throw <see cref="ArgumentException"/>. Returns the role ids held before and after.
    /// </summary>
    public static async Task<(IReadOnlyList<Guid> Before, IReadOnlyList<Guid> After)> ReplaceAsync(
        IWorkbenchDbContext db, AppUser user, IEnumerable<Guid> roleIds, string? actor, DateTime now, CancellationToken ct)
    {
        var target = roleIds.Where(id => id != BuiltInRoles.Employee.Id).Distinct().ToList();
        await EnsureBuiltInRolesAsync(db, ct);
        var known = await db.Set<Role>().Where(r => target.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        known.AddRange(db.Set<Role>().Local.Where(r => target.Contains(r.Id)).Select(r => r.Id));
        if (target.FirstOrDefault(id => !known.Contains(id)) is var missing && missing != Guid.Empty)
        {
            throw new ArgumentException($"Role {missing} doesn't exist.", nameof(roleIds));
        }

        var current = await db.Set<UserRole>().Where(r => r.UserId == user.Id).ToListAsync(ct);
        var before = current.Select(r => r.RoleId).ToList();
        db.Set<UserRole>().RemoveRange(current.Where(r => !target.Contains(r.RoleId)));
        foreach (var id in target.Where(id => !before.Contains(id)))
        {
            db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = id, AssignedAtUtc = now, AssignedBy = actor });
        }

        return (before, target);
    }

    /// <summary>
    /// Replaces only the built-in roles a user holds with <paramref name="roles"/>, keeping their custom roles (tracked,
    /// not saved). Returns the role ids held before and after.
    /// </summary>
    public static async Task<(IReadOnlyList<Guid> Before, IReadOnlyList<Guid> After)> ReplaceBuiltInAsync(
        IWorkbenchDbContext db, AppUser user, OrgRoles roles, string? actor, DateTime now, CancellationToken ct)
    {
        var current = await RoleIdsOfAsync(db, user.Id, ct);
        var target = current.Where(id => !BuiltInRoles.IsBuiltIn(id)).Concat(BuiltInRoles.IdsOf(roles));
        return await ReplaceAsync(db, user, target, actor, now, ct);
    }

    /// <summary>Names of roles by id, for audit details and display (unknown ids are left out).</summary>
    public static async Task<List<RoleRef>> RefsAsync(IWorkbenchDbContext db, IEnumerable<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Set<Role>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Name, r.BuiltIn }).ToListAsync(ct);
        var refs = rows.Select(r => new RoleRef(r.Id, r.Name, r.BuiltIn is not null)).ToList();
        refs.AddRange(ids.Where(id => rows.All(r => r.Id != id)).Select(BuiltInRoles.Find).OfType<BuiltInRoles.Definition>().Select(d => new RoleRef(d.Id, d.Name, true)));
        return refs.OrderBy(r => !r.IsBuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Audit row for a change to the roles a user holds (added to the context, saved with the caller's changes).</summary>
    public static void RecordAssignmentAudit(IWorkbenchDbContext db, AppUser user, IReadOnlyList<RoleRef> before, IReadOnlyList<RoleRef> after, string actor, DateTime now) =>
        db.Set<Domain.ProjectAccounting.AuditEvent>().Add(new Domain.ProjectAccounting.AuditEvent
        {
            EntityType = AuditUserRoles,
            EntityId = user.Id,
            Action = "RolesChanged",
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { user.Email, Roles = before.Select(r => r.Name) }, AuditJson),
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { user.Email, Roles = after.Select(r => r.Name) }, AuditJson),
            Actor = actor.Length > 0 ? actor : "system",
            OccurredAtUtc = now,
        });

    /// <summary>Audit entity type of a user's role assignments (entity id = the user's id).</summary>
    public const string AuditUserRoles = "UserRoles";

    /// <summary>Audit entity type of a role definition (entity id = the role's id).</summary>
    public const string AuditRole = "Role";

    private static readonly System.Text.Json.JsonSerializerOptions AuditJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>"Finance, Approvers" or "Employee" when there are none.</summary>
    public static string Describe(IEnumerable<RoleRef> roles)
    {
        var names = roles.Where(r => r.Id != BuiltInRoles.Employee.Id).Select(r => r.Name).ToList();
        return names.Count == 0 ? "Employee" : string.Join(", ", names);
    }

    /// <summary>Custom role ids from a comma-separated column.</summary>
    public static IReadOnlyList<Guid> ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();

    /// <summary>The comma-separated column value, or null when empty.</summary>
    public static string? FormatIds(IEnumerable<Guid>? ids)
    {
        var list = (ids ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }

    /// <summary>
    /// Checks that <paramref name="customRoleIds"/> are existing custom roles (built-in ones go in the flags) and returns
    /// them; throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ValidateCustomAsync(IWorkbenchDbContext db, IReadOnlyList<Guid>? customRoleIds, CancellationToken ct)
    {
        var ids = (customRoleIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return ids;
        }

        if (ids.Any(BuiltInRoles.IsBuiltIn))
        {
            throw new ArgumentException("Choose built-in roles as roles, not as custom roles.", nameof(customRoleIds));
        }

        var found = await db.Set<Role>().AsNoTracking().Where(r => ids.Contains(r.Id) && r.BuiltIn == null).Select(r => r.Id).ToListAsync(ct);
        if (ids.FirstOrDefault(id => !found.Contains(id)) is var missing && missing != Guid.Empty)
        {
            throw new ArgumentException($"Role {missing} doesn't exist.", nameof(customRoleIds));
        }

        return ids;
    }

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();
}
