using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.Security;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>Seats of one module: people whose roles grant any permission in it. <see cref="Allowed"/> is null when nothing limits it.</summary>
public sealed record ModuleSeatUsage(string ModuleId, string Name, int Used, int? Allowed);

/// <summary>
/// Module seats and the Community cap. <b>A person uses a seat of a module when their roles grant any permission that
/// <see cref="ModuleMap"/> puts in that module</b>; <c>core</c> has no module seats (its seats are the full and light seats of
/// <see cref="EditionService"/>). A module's seats come from the key's optional <c>moduleSeats</c> object, and from <c>seats</c> when
/// the key doesn't say, so a key that predates module seats behaves as before. Only enforced modules the license includes are limited.
/// Pending invitations hold a seat, API keys take none, and deactivating someone frees theirs.
/// </summary>
public sealed class ModuleSeatService(EditionService editions, LicenseOptions options, TimeProvider clock)
{
    /// <summary>The non-core modules that have seats under <paramref name="state"/>, with each one's limit (null: nothing limits it).</summary>
    private async Task<Dictionary<string, int?>> LimitsAsync(IWorkbenchDbContext db, EditionState state, CancellationToken ct)
    {
        var limits = new Dictionary<string, int?>();
        LicenseClaims? claims = null;
        if (state is { Source: EditionSource.Key, Edition: Edition.Business })
        {
            var stored = await db.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).Select(s => s.Key).FirstOrDefaultAsync(ct);
            if (LicenseKeys.TryVerify(stored, options.PublicKeyPem, out var verified, out _))
            {
                claims = verified;
            }
        }

        foreach (var module in options.Modules.Where(m => m.Id != ModuleIds.Core))
        {
            // Limited only when a key says so: the module is enforced and the key includes it. Seats (or moduleSeats) is the limit.
            limits[module.Id] = claims is not null && module.Enforced && EditionService.IsLicensed(state, module.Id) ? claims.SeatsFor(module.Id) : null;
        }

        return limits;
    }

    /// <summary>The modules each role's permissions fall in. Built-in roles come from the permission catalog, custom roles from their rows.</summary>
    private static async Task<Dictionary<Guid, HashSet<string>>> ModulesByRoleAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var roles = await db.Set<Role>().AsNoTracking().Select(r => new { r.Id, r.BuiltIn }).ToListAsync(ct);
        var rows = (await db.Set<RolePermission>().AsNoTracking().Select(p => new { p.RoleId, p.Permission }).ToListAsync(ct))
            .GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => g.Select(p => p.Permission).ToList());
        var result = new Dictionary<Guid, HashSet<string>>();
        foreach (var role in roles)
        {
            var permissions = (role.BuiltIn is { } flag ? PermissionCatalog.ForBuiltInRoles((OrgRoles)flag).AsEnumerable() : [])
                .Concat(rows.GetValueOrDefault(role.Id) ?? []);
            result[role.Id] = ModulesOf(permissions);
        }

        return result;
    }

    /// <summary>The non-core modules <paramref name="permissions"/> reach.</summary>
    public static HashSet<string> ModulesOf(IEnumerable<string> permissions) =>
        permissions.Select(ModuleMap.ForPermission).OfType<string>().Where(m => m != ModuleKeys.Core).ToHashSet();

    private static HashSet<string> ModulesOf(IEnumerable<Guid> roleIds, Dictionary<Guid, HashSet<string>> byRole) =>
        roleIds.SelectMany(id => byRole.GetValueOrDefault(id) ?? []).ToHashSet();

    /// <summary>Everyone counted: active people, and with <paramref name="includePending"/> invitations, as the roles each holds.</summary>
    private async Task<List<(string Key, List<Guid> Roles)>> PeopleAsync(IWorkbenchDbContext db, bool includePending, CancellationToken ct)
    {
        var people = await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.IsActive && !db.Set<ApiKey>().Any(k => k.ServiceUserEmail == u.Email))
            .Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        var ids = people.Select(p => p.Id).ToList();
        var held = (await db.Set<UserRole>().AsNoTracking().Where(r => ids.Contains(r.UserId)).Select(r => new { r.UserId, r.RoleId }).ToListAsync(ct))
            .GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(r => r.RoleId).Distinct().ToList());
        var result = people.Select(p => (Key: p.Email, Roles: held.GetValueOrDefault(p.Id) ?? [])).ToList();
        if (includePending)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var invites = await db.Set<UserInvite>().AsNoTracking()
                .Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
                .Select(i => new { i.Email, i.Roles, i.CustomRoleIds }).ToListAsync(ct);
            foreach (var invite in invites.GroupBy(i => i.Email).Select(g => g.First()).Where(i => result.All(r => r.Key != i.Email)))
            {
                result.Add((invite.Email, [.. BuiltInRoles.IdsOf((OrgRoles)invite.Roles), .. Identity.RoleStore.ParseIds(invite.CustomRoleIds)]));
            }
        }

        return result;
    }

    /// <summary>Seats used and allowed in each non-core module, for Setup › License.</summary>
    public async Task<IReadOnlyList<ModuleSeatUsage>> UsageAsync(IWorkbenchDbContext db, EditionState state, CancellationToken ct)
    {
        var limits = await LimitsAsync(db, state, ct);
        var byRole = await ModulesByRoleAsync(db, ct);
        var people = await PeopleAsync(db, includePending: false, ct);
        return options.Modules.Where(m => m.Id != ModuleIds.Core)
            .Select(m => new ModuleSeatUsage(m.Id, m.Name, people.Count(p => ModulesOf(p.Roles, byRole).Contains(m.Id)), limits.GetValueOrDefault(m.Id)))
            .ToList();
    }

    /// <summary>
    /// Refuses a change that would put someone beyond a seat limit. <paramref name="roleIds"/> is what the person will hold.
    /// <paramref name="existingUserId"/> is the active person whose roles are changing (they already use what they hold);
    /// null for someone being added, invited or reactivated, who needs a seat for everything. <paramref name="addsPerson"/>
    /// also applies the Community cap.
    /// </summary>
    public async Task RequireAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> roleIds, Guid? existingUserId, bool addsPerson, CancellationToken ct)
    {
        var state = await editions.GetAsync(ct);
        if (addsPerson && state.Source == EditionSource.None && options.CommunityMaxPeople > 0)
        {
            var count = (await PeopleAsync(db, includePending: true, ct)).Count;
            if (count + 1 > options.CommunityMaxPeople)
            {
                throw new WorkbenchEditionException(
                    $"The free Community edition is limited to {options.CommunityMaxPeople} people on this server, and all {options.CommunityMaxPeople} are taken (invitations count). " +
                    "Deactivate someone who has left, or enter a license key under Setup › License.");
            }
        }

        var limits = await LimitsAsync(db, state, ct);
        if (limits.Values.All(l => l is null))
        {
            return;
        }

        var byRole = await ModulesByRoleAsync(db, ct);
        var needed = ModulesOf(roleIds, byRole);
        if (existingUserId is { } existing)
        {
            var current = await db.Set<UserRole>().AsNoTracking().Where(r => r.UserId == existing).Select(r => r.RoleId).ToListAsync(ct);
            needed.ExceptWith(ModulesOf(current, byRole));
        }

        needed.RemoveWhere(m => limits.GetValueOrDefault(m) is null);
        if (needed.Count == 0)
        {
            return;
        }

        var people = await PeopleAsync(db, includePending: true, ct);
        var full = needed.Where(m => people.Count(p => ModulesOf(p.Roles, byRole).Contains(m)) + 1 > limits[m]!.Value).ToList();
        if (full.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", full.Select(m => $"{options.Modules.First(x => x.Id == m).Name} ({limits[m]})"));
        throw new WorkbenchEditionException(
            $"No seat is free in the {names} module{(full.Count > 1 ? "s" : "")}: everyone the license covers there already has one. " +
            $"Give this person roles without {(full.Count > 1 ? "those modules'" : "that module's")} permissions, deactivate someone who has left, or add seats under Setup › License.");
    }
}
