using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Approvals;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.Security;

namespace Workbench.Application.Identity;

/// <summary>
/// Admin › Roles without permission checks (<see cref="PermissionCheckingRoleService"/> adds them). Built-in roles are read
/// only. Every change to a role is audit logged (entity <see cref="RoleStore.AuditRole"/>, before and after) and recorded
/// as a security event; changes to who holds a role go through <see cref="UserLifecycle"/>, which does the same.
/// </summary>
public sealed class RoleService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    UserLifecycle lifecycle,
    AuditRecorder audit,
    SecurityEventLog securityLog,
    TimeProvider clock) : IRoleService
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 500;

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        var roles = await db.Set<Role>().AsNoTracking().ToListAsync(cancellationToken);
        var dtos = await ToDtosAsync(db, roles, cancellationToken);
        return dtos
            .OrderBy(r => !r.IsBuiltIn)
            .ThenBy(r => r.BuiltIn is { } b ? BuiltInRoles.All.ToList().FindIndex(d => d.Role == b) : 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<RoleRef>> ListNamesAsync(CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken)).Select(r => new RoleRef(r.Id, r.Name, r.IsBuiltIn)).ToList();

    public async Task<RoleDto> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        return await LoadDtoAsync(db, roleId, cancellationToken);
    }

    public async Task<RoleDto> CreateAsync(SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var (name, description, permissions) = Validate(request);
        await using var db = await OpenAsync(cancellationToken);
        await RequireUniqueNameAsync(db, name, null, cancellationToken);
        var role = await AddAsync(db, name, description, permissions, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.RoleCreated, name, detail: Clip($"Permissions: {Describe(permissions)}"), cancellationToken: cancellationToken);
        return await LoadDtoAsync(db, role.Id, cancellationToken);
    }

    public async Task<RoleDto> CloneAsync(Guid roleId, string name, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        var source = await LoadDtoAsync(db, roleId, cancellationToken);
        var (clean, description, permissions) = Validate(new SaveRoleRequest(name, source.Description, source.Permissions));
        await RequireUniqueNameAsync(db, clean, null, cancellationToken);
        var role = await AddAsync(db, clean, description, permissions, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.RoleCreated, clean, detail: Clip($"Cloned from {source.Name}; permissions: {Describe(permissions)}"), cancellationToken: cancellationToken);
        return await LoadDtoAsync(db, role.Id, cancellationToken);
    }

    public async Task<RoleDto> UpdateAsync(Guid roleId, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var (name, description, permissions) = Validate(request);
        await using var db = await OpenAsync(cancellationToken);
        var role = await LoadCustomAsync(db, roleId, "changed", cancellationToken);
        await RequireUniqueNameAsync(db, name, roleId, cancellationToken);
        var before = Snapshot(role);
        role.Name = name;
        role.NormalizedName = RoleStore.NormalizeName(name);
        role.Description = description;
        role.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        role.UpdatedBy = access.Email;
        var removed = role.Permissions.Where(p => !permissions.Contains(p.Permission)).ToList();
        db.Set<RolePermission>().RemoveRange(removed);
        var after = Snapshot(role, permissions);
        foreach (var key in permissions.Where(k => role.Permissions.All(p => p.Permission != k)).ToList())
        {
            // Added through the set: a new row with its id already set would otherwise be taken for an existing one.
            db.Set<RolePermission>().Add(new RolePermission { RoleId = role.Id, Permission = key });
        }

        if (before == after)
        {
            return await LoadDtoAsync(db, roleId, cancellationToken);
        }

        audit.Record(db, RoleStore.AuditRole, role.Id, "Updated", before, after);
        await db.SaveChangesAsync(cancellationToken);
        var added = permissions.Except(before.Permissions).ToList();
        var dropped = before.Permissions.Except(permissions).ToList();
        var detail = string.Join("; ", new[]
        {
            before.Name != after.Name ? $"renamed from {before.Name}" : null,
            added.Count > 0 ? $"added {Describe(added)}" : null,
            dropped.Count > 0 ? $"removed {Describe(dropped)}" : null,
            before.Description != after.Description ? "description changed" : null,
        }.OfType<string>());
        await securityLog.RecordAsync(SecurityEventTypes.RoleChanged, name, detail: Clip(detail), cancellationToken: cancellationToken);
        return await LoadDtoAsync(db, roleId, cancellationToken);
    }

    public async Task DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        var role = await LoadCustomAsync(db, roleId, "deleted", cancellationToken);
        await RequireUnusedAsync(db, role, cancellationToken);

        // Everyone who held it loses it, each change audit logged like any other.
        var holders = await db.Set<UserRole>().AsNoTracking().Where(r => r.RoleId == roleId)
            .Join(db.Set<AppUser>(), r => r.UserId, u => u.Id, (r, u) => new { u.Id, u.Email }).ToListAsync(cancellationToken);
        foreach (var holder in holders)
        {
            var current = await RoleStore.RoleIdsOfAsync(db, holder.Id, cancellationToken);
            await lifecycle.SetRoleIdsAsync(holder.Email, current.Where(id => id != roleId).ToList(), access.Email, cancellationToken);
        }

        var before = Snapshot(role);
        db.Set<Role>().Remove(role);
        audit.Record(db, RoleStore.AuditRole, role.Id, "Deleted", before);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.RoleDeleted, role.Name,
            detail: Clip($"Removed from {holders.Count} user(s); permissions were: {Describe(before.Permissions)}"), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<UserRolesDto>> ListMembersAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenAsync(cancellationToken);
        if (!await db.Set<Role>().AnyAsync(r => r.Id == roleId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Role", roleId);
        }

        // Employee is everyone's.
        var users = roleId == BuiltInRoles.Employee.Id
            ? await db.Set<AppUser>().AsNoTracking().ToListAsync(cancellationToken)
            : await db.Set<AppUser>().AsNoTracking().Where(u => db.Set<UserRole>().Any(r => r.UserId == u.Id && r.RoleId == roleId)).ToListAsync(cancellationToken);
        var result = new List<UserRolesDto>();
        foreach (var user in users.OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName).ThenBy(u => u.Email))
        {
            result.Add(await UserDtoAsync(db, user, cancellationToken));
        }

        return result;
    }

    public async Task<UserRolesDto> GetUserRolesAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await OpenAsync(cancellationToken);
        var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"User {email} was not found.");
        return await UserDtoAsync(db, user, cancellationToken);
    }

    public async Task<UserRolesDto> SetUserRolesAsync(string email, IReadOnlyList<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        await lifecycle.SetRoleIdsAsync(email, (roleIds ?? []).ToList(), access.Email, cancellationToken);
        return await GetUserRolesAsync(email, cancellationToken);
    }

    // ---- Internals ----

    private sealed record RoleSnapshot(string Name, string Description, IReadOnlyList<string> Permissions)
    {
        public bool Equals(RoleSnapshot? other) =>
            other is not null && Name == other.Name && Description == other.Description && Permissions.SequenceEqual(other.Permissions);

        public override int GetHashCode() => HashCode.Combine(Name, Description, Permissions.Count);
    }

    private static RoleSnapshot Snapshot(Role role, IEnumerable<string>? permissions = null) =>
        new(role.Name, role.Description, (permissions ?? role.Permissions.Select(p => p.Permission)).Order(StringComparer.Ordinal).ToList());

    private async Task<IWorkbenchDbContext> OpenAsync(CancellationToken ct)
    {
        var db = await dbFactory.CreateAsync(ct);
        await RoleStore.EnsureBuiltInRolesAsync(db, ct);
        if (db.Set<Role>().Local.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return db;
    }

    private async Task<Role> AddAsync(IWorkbenchDbContext db, string name, string description, IReadOnlyList<string> permissions, CancellationToken ct)
    {
        var role = new Role
        {
            Name = name,
            NormalizedName = RoleStore.NormalizeName(name),
            Description = description,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            CreatedBy = access.Email,
        };
        role.Permissions.AddRange(permissions.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        db.Set<Role>().Add(role);
        audit.Record(db, RoleStore.AuditRole, role.Id, "Created", after: Snapshot(role));
        await db.SaveChangesAsync(ct);
        return role;
    }

    private static (string Name, string Description, IReadOnlyList<string> Permissions) Validate(SaveRoleRequest? request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw new ArgumentException("Give the role a name.", nameof(request));
        }

        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"The name can be at most {MaxNameLength} characters.", nameof(request));
        }

        var description = request.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"The description can be at most {MaxDescriptionLength} characters.", nameof(request));
        }

        var permissions = (request.Permissions ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (permissions.FirstOrDefault(p => !PermissionCatalog.Exists(p)) is { } unknown)
        {
            throw new ArgumentException($"There's no permission called {unknown}.", nameof(request));
        }

        return (name, description, permissions.Order(StringComparer.Ordinal).ToList());
    }

    private static async Task RequireUniqueNameAsync(IWorkbenchDbContext db, string name, Guid? except, CancellationToken ct)
    {
        var normalized = RoleStore.NormalizeName(name);
        if (await db.Set<Role>().AnyAsync(r => r.NormalizedName == normalized && r.Id != except, ct)
            || BuiltInRoles.All.Any(d => RoleStore.NormalizeName(d.Name) == normalized && d.Id != except))
        {
            throw new ArgumentException($"There's already a role called {name}.", nameof(name));
        }
    }

    private static async Task<Role> LoadCustomAsync(IWorkbenchDbContext db, Guid roleId, string verb, CancellationToken ct)
    {
        var role = await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw WorkbenchNotFoundException.For("Role", roleId);
        if (role.BuiltIn is not null)
        {
            throw new ArgumentException($"{role.Name} is a built-in role and can't be {verb}. Clone it to make a role you can change.", nameof(roleId));
        }

        return role;
    }

    /// <summary>A role that routes approvals, maps SSO groups, backs an API key or a pending invite can't be deleted.</summary>
    private static async Task RequireUnusedAsync(IWorkbenchDbContext db, Role role, CancellationToken ct)
    {
        var id = role.Id.ToString();
        var key = ApproverRoles.CustomKey(role.Id);
        var uses = new List<string>();
        var current = await db.Set<ApprovalWorkflow>().AsNoTracking().Where(w => w.CurrentVersionId != null).Select(w => new { w.DocumentType, w.CurrentVersionId }).ToListAsync(ct);
        var versionIds = current.Select(c => c.CurrentVersionId!.Value).ToList();
        var versions = await db.Set<ApprovalWorkflowVersion>().AsNoTracking().Where(v => versionIds.Contains(v.Id)).Select(v => new { v.Id, v.DefinitionJson }).ToListAsync(ct);
        foreach (var v in versions.Where(v => v.DefinitionJson.Contains(id, StringComparison.OrdinalIgnoreCase)))
        {
            uses.Add($"the {Contracts.Approvals.ApprovalDocumentTypes.Label(current.First(c => c.CurrentVersionId == v.Id).DocumentType).ToLowerInvariant()} approval workflow");
        }

        if (await db.Set<ApprovalTask>().AnyAsync(t => t.AssigneeRole == key && t.Status == ApprovalTaskStatus.Waiting, ct))
        {
            uses.Add("approvals waiting on it");
        }

        if ((await db.Set<SsoGroupMapping>().AsNoTracking().Select(m => m.CustomRoleIds).ToListAsync(ct)).Any(c => RoleStore.ParseIds(c).Contains(role.Id)))
        {
            uses.Add("an SSO group mapping");
        }

        if ((await db.Set<ApiKey>().AsNoTracking().Where(k => k.RevokedAtUtc == null).Select(k => k.CustomRoleIds).ToListAsync(ct)).Any(c => RoleStore.ParseIds(c).Contains(role.Id)))
        {
            uses.Add("an API key");
        }

        var now = DateTime.UtcNow;
        if ((await db.Set<UserInvite>().AsNoTracking().Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now).Select(i => i.CustomRoleIds).ToListAsync(ct))
            .Any(c => RoleStore.ParseIds(c).Contains(role.Id)))
        {
            uses.Add("a pending invite");
        }

        if (uses.Count > 0)
        {
            throw new ArgumentException($"{role.Name} is still used by {string.Join(", ", uses)}. Change those first.", nameof(role));
        }
    }

    private async Task<RoleDto> LoadDtoAsync(IWorkbenchDbContext db, Guid roleId, CancellationToken ct)
    {
        var role = await db.Set<Role>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw WorkbenchNotFoundException.For("Role", roleId);
        return (await ToDtosAsync(db, [role], ct))[0];
    }

    private static async Task<List<RoleDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<Role> roles, CancellationToken ct)
    {
        var ids = roles.Select(r => r.Id).ToList();
        var permissions = (await db.Set<RolePermission>().AsNoTracking().Where(p => ids.Contains(p.RoleId)).ToListAsync(ct)).ToLookup(p => p.RoleId, p => p.Permission);
        var counts = await db.Set<UserRole>().AsNoTracking()
            .Where(r => ids.Contains(r.RoleId))
            .Join(db.Set<AppUser>().Where(u => u.IsActive), r => r.UserId, u => u.Id, (r, u) => r.RoleId)
            .GroupBy(id => id)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var everyone = roles.Any(r => r.Id == BuiltInRoles.Employee.Id) ? await db.Set<AppUser>().CountAsync(u => u.IsActive, ct) : 0;
        return roles.Select(r =>
        {
            var builtIn = r.BuiltIn is { } b ? (OrgRoles?)b : null;
            IReadOnlyList<string> keys = builtIn is { } flags
                ? PermissionCatalog.All.Where(p => PermissionCatalog.ForBuiltInRoles(flags).Has(p.Key)).Select(p => p.Key).ToList()
                : PermissionCatalog.All.Where(p => permissions[r.Id].Contains(p.Key)).Select(p => p.Key).ToList();
            var users = r.Id == BuiltInRoles.Employee.Id ? everyone : counts.GetValueOrDefault(r.Id);
            return new RoleDto(r.Id, r.Name, r.Description, builtIn, keys, users, r.CreatedAtUtc, r.CreatedBy, r.UpdatedAtUtc, r.UpdatedBy);
        }).ToList();
    }

    private static async Task<UserRolesDto> UserDtoAsync(IWorkbenchDbContext db, AppUser user, CancellationToken ct)
    {
        var ids = await RoleStore.RoleIdsOfAsync(db, user.Id, ct);
        var resolved = await RoleStore.ResolveRoleIdsAsync(db, ids, ct);
        var permissions = PermissionCatalog.All.Where(p => resolved.Permissions.Has(p.Key)).Select(p => p.Key).ToList();
        return new UserRolesDto(user.Email, user.DisplayName, user.IsActive, resolved.Roles, permissions);
    }

    private static string Describe(IEnumerable<string> permissions)
    {
        var names = permissions.Select(PermissionCatalog.NameOf).ToList();
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string Clip(string text) => text.Length <= 1000 ? text : text[..999] + "…";
}
