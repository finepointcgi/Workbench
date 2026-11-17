using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.Licensing;
using Workbench.Domain.Security;

namespace Workbench.Application.Licensing;

/// <summary>Set by the hosting service in MultiTenant mode: the plan sold to the current tenant (e.g. <c>business</c>), or null.</summary>
public interface IHostedPlanSource
{
    Task<string?> GetPlanAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What the hosting service says the current tenant is entitled to (MultiTenant, from the catalog's entitlement record).
/// <see cref="ReadOnly"/> means a lapsed trial, a cancelled or suspended subscription: Business is off and nobody can change data.
/// </summary>
public sealed record HostedEntitlement(
    string Plan,
    IReadOnlySet<string> Modules,
    int Seats,
    int LightSeats,
    bool Trial,
    DateTime? EndsUtc,
    bool ReadOnly,
    bool PastDue,
    string? Message);

/// <summary>Set by the hosting service in MultiTenant mode. Null: the tenant has no entitlement record (use <see cref="IHostedPlanSource"/>).</summary>
public interface IHostedEntitlementSource
{
    Task<HostedEntitlement?> GetAsync(CancellationToken cancellationToken);
}

internal sealed class NoHostedEntitlement : IHostedEntitlementSource
{
    public Task<HostedEntitlement?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<HostedEntitlement?>(null);
}

internal sealed class NoHostedPlan : IHostedPlanSource
{
    public Task<string?> GetPlanAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>
/// What the organization is entitled to. <see cref="Modules"/> is the set of module ids the license includes (null: every
/// module, for WB1 keys, the configuration override and a hosted plan); whether a module is actually <i>required</i> is
/// decided by its <c>Enforced</c> flag (see <see cref="EditionService.HasModule"/>). <see cref="SeatLimit"/> is full seats
/// and <see cref="LightSeatLimit"/> light ones; <see cref="DaysLeft"/> counts to the key's expiry (negative once past).
/// </summary>
public sealed record EditionState(
    Edition Edition,
    EditionSource Source,
    string? Licensee,
    DateOnly? ExpiresOn,
    bool InGracePeriod,
    int? SeatLimit,
    string? Problem,
    IReadOnlySet<string>? Modules = null,
    int LightSeatLimit = 0,
    bool Trial = false,
    int? DaysLeft = null,
    bool ReadOnly = false);

/// <summary>People counted against a license. A light person takes a light seat while one is free, and a full seat otherwise.</summary>
public sealed record SeatUsage(int FullPeople, int LightPeople, int LightSeatLimit)
{
    public int LightSeatsInUse => Math.Min(LightPeople, LightSeatLimit);

    /// <summary>Full people, plus light people who have run out of light seats.</summary>
    public int FullSeatsInUse => FullPeople + LightPeople - LightSeatsInUse;

    public int People => FullPeople + LightPeople;
}

/// <summary>
/// Works out what this organization is entitled to, from the hosting plan or the stored license key: the edition, which
/// modules are licensed, and the seats (full and light). This is the one place that answers "is module X licensed" and
/// "how many seats are used and allowed".
/// </summary>
public sealed class EditionService(IWorkbenchDbContextFactory dbFactory, IHostedPlanSource hosted, LicenseOptions options, TimeProvider clock, IHostedEntitlementSource? entitlements = null)
{
    private static readonly IReadOnlySet<string> CoreOnly = new HashSet<string> { ModuleIds.Core };

    public async Task<EditionState> GetAsync(CancellationToken ct)
    {
        if (string.Equals(options.EditionOverride?.Trim(), "business", StringComparison.OrdinalIgnoreCase))
        {
            return new EditionState(Edition.Business, EditionSource.Configuration, null, null, false, null, null);
        }

        if (entitlements is not null && await entitlements.GetAsync(ct) is { } grant)
        {
            if (grant.ReadOnly)
            {
                return new EditionState(Edition.Community, EditionSource.HostedPlan, null, grant.EndsUtc is { } e ? DateOnly.FromDateTime(e) : null, false, null, grant.Message, CoreOnly,
                    0, grant.Trial, grant.EndsUtc is { } e2 ? DateOnly.FromDateTime(e2).DayNumber - DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber : null, ReadOnly: true);
            }

            var modules = new HashSet<string>(grant.Modules) { ModuleIds.Core };
            var left = grant.EndsUtc is { } end ? DateOnly.FromDateTime(end).DayNumber - DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber : (int?)null;
            return new EditionState(Edition.Business, EditionSource.HostedPlan, null, grant.EndsUtc is { } e3 ? DateOnly.FromDateTime(e3) : null, grant.PastDue,
                grant.Seats > 0 ? grant.Seats : null, grant.Message, modules, grant.LightSeats, grant.Trial, left);
        }

        if (await hosted.GetPlanAsync(ct) is { } plan && string.Equals(plan.Trim(), "business", StringComparison.OrdinalIgnoreCase))
        {
            return new EditionState(Edition.Business, EditionSource.HostedPlan, null, null, false, null, null);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var stored = await db.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).Select(s => s.Key).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new EditionState(Edition.Community, EditionSource.None, null, null, false, null, null, CoreOnly);
        }

        if (!LicenseKeys.TryVerify(stored, options.PublicKeyPem, out var claims, out var problem) || claims is null)
        {
            return new EditionState(Edition.Community, EditionSource.Key, null, null, false, null, problem, CoreOnly);
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var daysLeft = claims.ExpiresOn.DayNumber - today.DayNumber;
        if (today <= claims.ExpiresOn)
        {
            return new EditionState(claims.Edition, EditionSource.Key, claims.Licensee, claims.ExpiresOn, false, claims.Seats, null,
                claims.Modules ?? AllModules, claims.LightSeats, claims.Trial, daysLeft);
        }

        // Expired: keep working for the grace period, then fall back to Community. Nothing is deleted or hidden.
        // A trial has no grace: it is time-boxed, and the banner tells administrators when it lapses.
        if (!claims.Trial && today <= claims.ExpiresOn.AddDays(options.GraceDays))
        {
            return new EditionState(claims.Edition, EditionSource.Key, claims.Licensee, claims.ExpiresOn, true, claims.Seats,
                $"This license expired on {claims.ExpiresOn:d MMM yyyy}. Renew by {claims.ExpiresOn.AddDays(options.GraceDays):d MMM yyyy} to keep Business features.",
                claims.Modules ?? AllModules, claims.LightSeats, false, daysLeft);
        }

        return new EditionState(Edition.Community, EditionSource.Key, claims.Licensee, claims.ExpiresOn, false, null,
            claims.Trial
                ? $"Your trial ended on {claims.ExpiresOn:d MMM yyyy}, so Business features are off. Your data is untouched; add a license key to carry on."
                : $"This license expired on {claims.ExpiresOn:d MMM yyyy}, so Business features are off. Your data is untouched.",
            CoreOnly, 0, claims.Trial, daysLeft);
    }

    private static readonly IReadOnlySet<string> AllModules = ModuleCatalog.AllIds.ToHashSet();

    /// <summary>The modules this build knows, with their <c>Enforced</c> flags (the table in <see cref="ModuleCatalog"/> unless <see cref="LicenseOptions.Modules"/> replaces it).</summary>
    public IReadOnlyList<ModuleInfo> Modules => options.Modules;

    /// <summary>True when the license (or plan) includes <paramref name="moduleId"/>, whether or not the module is enforced. Gated features use this.</summary>
    public static bool IsLicensed(EditionState state, string moduleId) =>
        moduleId == ModuleIds.Core || state.Modules is null || state.Modules.Contains(moduleId);

    /// <summary>True when the organization may use <paramref name="moduleId"/>: it is licensed, or it isn't an enforced module.</summary>
    public bool HasModule(EditionState state, string moduleId) =>
        IsLicensed(state, moduleId) || !(options.Modules.FirstOrDefault(m => m.Id == moduleId)?.Enforced ?? false);

    /// <summary>
    /// True when <paramref name="moduleId"/> is enforced for its whole self (<see cref="ModuleInfo.Enforced"/> and <see cref="ModuleInfo.WholeModule"/>):
    /// its pages leave the menu and every change to its services is refused without the license. False for a module whose only
    /// enforced part is its named features (govcon and enterprise today), and for every module that isn't enforced.
    /// </summary>
    public bool Requires(string moduleId) =>
        options.Modules.FirstOrDefault(m => m.Id == moduleId) is { Enforced: true, WholeModule: true };

    public async Task<bool> HasModuleAsync(string moduleId, CancellationToken ct) => HasModule(await GetAsync(ct), moduleId);

    /// <summary>Active people, split into full and light. API keys are not people and are not counted.</summary>
    public static async Task<(int Full, int Light)> CountPeopleAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var fullRoles = FullRoleIds(db);
        var people = db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive && !db.Set<ApiKey>().Any(k => k.ServiceUserEmail == u.Email));
        var total = await people.CountAsync(ct);
        var full = await people.CountAsync(u => db.Set<UserRole>().Any(ur => ur.UserId == u.Id && fullRoles.Contains(ur.RoleId)), ct);
        return (full, total - full);
    }

    /// <summary>Roles that grant anything beyond the built-in Employee role: every built-in role but Employee, and custom roles with a permission.</summary>
    private static IQueryable<Guid> FullRoleIds(IWorkbenchDbContext db) =>
        db.Set<Role>().AsNoTracking()
            .Where(r => (r.BuiltIn != null && r.BuiltIn != 0) || db.Set<RolePermission>().Any(p => p.RoleId == r.Id))
            .Select(r => r.Id);

    /// <summary>True when roles <paramref name="builtIn"/> and <paramref name="custom"/> grant only what the Employee role does (time, expenses, leave).</summary>
    public static async Task<bool> IsLightGrantAsync(IWorkbenchDbContext db, OrgRoles builtIn, IReadOnlyCollection<Guid> custom, CancellationToken ct)
    {
        if ((builtIn & OrgRoleChecks.Everything) != OrgRoles.Employee)
        {
            return false;
        }

        var ids = custom.ToList();
        return ids.Count == 0 || !await FullRoleIds(db).AnyAsync(id => ids.Contains(id), ct);
    }

    /// <summary>True when the existing user's current roles are all Employee-level.</summary>
    public static async Task<bool> IsLightPersonAsync(IWorkbenchDbContext db, Guid userId, CancellationToken ct)
    {
        var fullRoles = FullRoleIds(db);
        return !await db.Set<UserRole>().AsNoTracking().AnyAsync(ur => ur.UserId == userId && fullRoles.Contains(ur.RoleId), ct);
    }

    /// <summary>Active people plus pending invitations (which hold a seat), measured against <paramref name="state"/>'s light seats.</summary>
    public async Task<SeatUsage> UsageAsync(IWorkbenchDbContext db, EditionState state, bool includePending, CancellationToken ct)
    {
        var (full, light) = await CountPeopleAsync(db, ct);
        if (includePending)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var invites = await db.Set<UserInvite>().AsNoTracking()
                .Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
                .Select(i => new { i.Email, i.Roles, i.CustomRoleIds }).ToListAsync(ct);
            foreach (var invite in invites.GroupBy(i => i.Email).Select(g => g.First()))
            {
                if (await IsLightGrantAsync(db, (OrgRoles)invite.Roles, Identity.RoleStore.ParseIds(invite.CustomRoleIds).ToList(), ct))
                {
                    light++;
                }
                else
                {
                    full++;
                }
            }
        }

        return new SeatUsage(full, light, state.LightSeatLimit);
    }

    /// <summary>Seats in use for the license status page: active people only, as full and light.</summary>
    public async Task<SeatUsage> UsageAsync(EditionState state, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await UsageAsync(db, state, includePending: false, ct);
    }

    /// <summary>
    /// Refuses to add one more person when the license has no seat for them. <paramref name="light"/> says whether the new
    /// person would only hold Employee-level access. Pending invitations hold a seat. Community has no limit.
    /// </summary>
    public async Task RequireFreeSeatAsync(IWorkbenchDbContext db, bool light, CancellationToken ct)
    {
        var state = await GetAsync(ct);
        if (state.SeatLimit is not { } limit)
        {
            return;
        }

        var usage = await UsageAsync(db, state, includePending: true, ct);
        var after = new SeatUsage(usage.FullPeople + (light ? 0 : 1), usage.LightPeople + (light ? 1 : 0), state.LightSeatLimit);
        if (after.FullSeatsInUse <= limit)
        {
            return;
        }

        var seats = state.LightSeatLimit == 0 ? $"All {limit} seats in your license are in use." : $"All {limit} full seats and {state.LightSeatLimit} light seats in your license are in use.";
        throw new WorkbenchEditionException($"{seats} Deactivate someone who has left, or add seats under Setup › License.");
    }
}

/// <summary>Stops an action whose module isn't licensed, with a message that says what to do about it.</summary>
public sealed class FeatureGate(EditionService editions)
{
    public async Task RequireAsync(Feature feature, CancellationToken ct)
    {
        var state = await editions.GetAsync(ct);
        if (!Includes(state, feature))
        {
            var info = FeatureCatalog.Find(feature);
            var module = editions.Modules.FirstOrDefault(m => m.Id == info.Module);
            throw new WorkbenchEditionException(state.Edition == Edition.Business && module is not null
                ? $"{info.Name} needs the {module.Name} module, which isn't in your license. An administrator can ask for it under Setup › License."
                : $"{info.Name} is part of Workbench Business. An administrator can add a license under Setup › License.");
        }
    }

    public async Task<bool> HasAsync(Feature feature, CancellationToken ct) => Includes(await editions.GetAsync(ct), feature);

    /// <summary>
    /// Stops changes to an enforced module the license doesn't include. Wire it to a service the way features are
    /// (<c>EditionGates</c>) when a module's flag is flipped on; a module that isn't enforced always passes.
    /// </summary>
    public async Task RequireModuleAsync(string moduleId, CancellationToken ct)
    {
        if (!editions.Requires(moduleId))
        {
            return; // Not enforced: no need to read the license.
        }

        var state = await editions.GetAsync(ct);
        if (!editions.HasModule(state, moduleId))
        {
            var name = editions.Modules.FirstOrDefault(m => m.Id == moduleId)?.Name ?? moduleId;
            throw new WorkbenchEditionException($"This needs the {name} module, which isn't in your license. An administrator can ask for it under Setup › License.");
        }
    }

    private static bool Includes(EditionState state, Feature feature) =>
        FeatureCatalog.IsIncluded(state.Edition, feature) && EditionService.IsLicensed(state, FeatureCatalog.ModuleOf(feature));
}

public static class LicensingRegistration
{
    public static IServiceCollection AddWorkbenchLicensing(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new LicenseOptions());
        services.TryAddSingleton<IHostedPlanSource, NoHostedPlan>();
        services.TryAddSingleton<IHostedEntitlementSource, NoHostedEntitlement>();
        services.AddScoped<EditionService>();
        services.AddScoped<FeatureGate>();
        services.AddScoped<ILicenseService, LicenseService>();

        // Connected installs only: with no License:ServerUrl the background service never makes a call.
        services.AddHttpClient(LicenseRefreshService.HttpClientName);
        services.AddScoped<LicenseRefreshService>();
        services.AddHostedService<LicenseRefreshBackgroundService>();
        return services;
    }
}
