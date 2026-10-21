using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

/// <summary>
/// The built-in catalog plus permissions features register at run time (<see cref="RegisteredPermission"/>, per tenant).
/// Built-in roles a registered permission is granted to get <see cref="RolePermission"/> rows, so resolving someone's
/// permissions stays one query. Registration has no permission check of its own: features call it from their own,
/// already-checked services.
/// </summary>
public sealed partial class PermissionRegistry(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, SecurityEventLog securityLog, TimeProvider clock)
    : IPermissionRegistry
{
    public const string AuditEntity = "RegisteredPermission";

    public async Task<IReadOnlyList<PermissionDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return [.. PermissionCatalog.All, .. await RegisteredAsync(db, null, cancellationToken)];
    }

    public async Task<PermissionDefinition?> FindAsync(string key, CancellationToken cancellationToken = default)
    {
        if (PermissionCatalog.Find(key) is { } builtIn)
        {
            return builtIn;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<RegisteredPermission>().AsNoTracking().Where(p => p.Key == key).Select(p => ToDefinition(p)).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionDefinition>> ListBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RegisteredAsync(db, source, cancellationToken);
    }

    public async Task<PermissionDefinition> RegisterAsync(RegisterPermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key?.Trim() ?? "";
        if (key.Length > 100 || !KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("A permission key is lower-case and dotted, like custom_field.clearance.view (100 characters at most).", nameof(request));
        }

        if (PermissionCatalog.Exists(key))
        {
            throw new ArgumentException($"{key} is a built-in permission.", nameof(request));
        }

        var area = Required(request.Area, 80, "area");
        var name = Required(request.Name, 120, "name");
        var source = Required(request.Source, 100, "source");
        var description = (request.Description ?? "").Trim();
        if (description.Length > 500)
        {
            throw new ArgumentException("The description can be at most 500 characters.", nameof(request));
        }

        var grantedTo = request.GrantedTo & OrgRoleChecks.Everything & ~OrgRoles.Admin; // Admin holds every permission anyway.
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<RegisteredPermission>().FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
        var before = row is null ? null : ToDefinition(row);
        if (row is null)
        {
            row = new RegisteredPermission { Key = key, Area = area, Name = name, Source = source, CreatedAtUtc = now };
            db.Set<RegisteredPermission>().Add(row);
        }
        else if (row.Source != source)
        {
            throw new ArgumentException($"{key} is registered by {row.Source}.", nameof(request));
        }

        (row.Area, row.Name, row.Description, row.GrantedTo, row.Sensitive, row.UpdatedAtUtc) = (area, name, description, (int)grantedTo, request.Sensitive, now);

        // Built-in roles get the permission as rows; ones it's no longer granted to lose them.
        await RoleStore.EnsureBuiltInRolesAsync(db, cancellationToken);
        var wanted = BuiltInRoles.IdsOf(grantedTo).ToHashSet();
        var builtInIds = BuiltInRoles.All.Select(d => d.Id).ToList();
        var existing = await db.Set<RolePermission>().Where(p => p.Permission == key && builtInIds.Contains(p.RoleId)).ToListAsync(cancellationToken);
        db.Set<RolePermission>().RemoveRange(existing.Where(p => !wanted.Contains(p.RoleId)));
        foreach (var roleId in wanted.Where(id => existing.All(p => p.RoleId != id)))
        {
            db.Set<RolePermission>().Add(new RolePermission { RoleId = roleId, Permission = key });
        }

        var after = ToDefinition(row);
        if (before != after)
        {
            audit.Record(db, AuditEntity, row.Id, before is null ? "Registered" : "Updated", before, after);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (before is null)
        {
            await securityLog.RecordAsync(SecurityEventTypes.PermissionRegistered, key, detail: $"{source}: {name}", cancellationToken: cancellationToken);
        }

        return after;
    }

    public async Task UnregisterAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<RegisteredPermission>().FirstOrDefaultAsync(p => p.Key == key, cancellationToken);
        if (row is null)
        {
            return;
        }

        var grants = await db.Set<RolePermission>().Where(p => p.Permission == key).ToListAsync(cancellationToken);
        db.Set<RolePermission>().RemoveRange(grants);
        db.Set<RegisteredPermission>().Remove(row);
        audit.Record(db, AuditEntity, row.Id, "Unregistered", ToDefinition(row));
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.PermissionUnregistered, key, detail: $"{row.Source}: {row.Name}; removed from {grants.Count} role(s)",
            cancellationToken: cancellationToken);
    }

    /// <summary>Registered permissions, by area and name (one source, or all).</summary>
    internal static async Task<List<PermissionDefinition>> RegisteredAsync(IWorkbenchDbContext db, string? source, CancellationToken ct)
    {
        var query = db.Set<RegisteredPermission>().AsNoTracking();
        if (source is not null)
        {
            query = query.Where(p => p.Source == source);
        }

        var rows = await query.ToListAsync(ct);
        return rows.OrderBy(p => p.Area, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(ToDefinition).ToList();
    }

    /// <summary>The keys a role may hold: built-in and registered.</summary>
    internal static async Task<HashSet<string>> KnownKeysAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var keys = PermissionCatalog.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        keys.UnionWith(await db.Set<RegisteredPermission>().AsNoTracking().Select(p => p.Key).ToListAsync(ct));
        return keys;
    }

    private static PermissionDefinition ToDefinition(RegisteredPermission p) =>
        new(p.Key, p.Area, p.Name, p.Description, (OrgRoles)p.GrantedTo, p.Sensitive, p.Source);

    private static string Required(string? value, int max, string what)
    {
        var text = value?.Trim() ?? "";
        return text.Length > 0 && text.Length <= max ? text : throw new ArgumentException($"Give the permission a {what} ({max} characters at most).");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z0-9_\-]+)+$")]
    private static partial Regex KeyPattern();
}
