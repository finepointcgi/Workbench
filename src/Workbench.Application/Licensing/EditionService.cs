using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts;
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

internal sealed class NoHostedPlan : IHostedPlanSource
{
    public Task<string?> GetPlanAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

public sealed record EditionState(
    Edition Edition,
    EditionSource Source,
    string? Licensee,
    DateOnly? ExpiresOn,
    bool InGracePeriod,
    int? SeatLimit,
    string? Problem);

/// <summary>Works out which edition this organization has, from the hosting plan or the stored license key.</summary>
public sealed class EditionService(IWorkbenchDbContextFactory dbFactory, IHostedPlanSource hosted, LicenseOptions options, TimeProvider clock)
{
    public async Task<EditionState> GetAsync(CancellationToken ct)
    {
        if (string.Equals(options.EditionOverride?.Trim(), "business", StringComparison.OrdinalIgnoreCase))
        {
            return new EditionState(Edition.Business, EditionSource.Configuration, null, null, false, null, null);
        }

        if (await hosted.GetPlanAsync(ct) is { } plan && string.Equals(plan.Trim(), "business", StringComparison.OrdinalIgnoreCase))
        {
            return new EditionState(Edition.Business, EditionSource.HostedPlan, null, null, false, null, null);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var stored = await db.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).Select(s => s.Key).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new EditionState(Edition.Community, EditionSource.None, null, null, false, null, null);
        }

        if (!LicenseKeys.TryVerify(stored, options.PublicKeyPem, out var claims, out var problem) || claims is null)
        {
            return new EditionState(Edition.Community, EditionSource.Key, null, null, false, null, problem);
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (today <= claims.ExpiresOn)
        {
            return new EditionState(claims.Edition, EditionSource.Key, claims.Licensee, claims.ExpiresOn, false, claims.Seats, null);
        }

        // Expired: keep working for the grace period, then fall back to Community. Nothing is deleted or hidden.
        if (today <= claims.ExpiresOn.AddDays(options.GraceDays))
        {
            return new EditionState(claims.Edition, EditionSource.Key, claims.Licensee, claims.ExpiresOn, true, claims.Seats,
                $"This license expired on {claims.ExpiresOn:d MMM yyyy}. Renew by {claims.ExpiresOn.AddDays(options.GraceDays):d MMM yyyy} to keep Business features.");
        }

        return new EditionState(Edition.Community, EditionSource.Key, claims.Licensee, claims.ExpiresOn, false, null,
            $"This license expired on {claims.ExpiresOn:d MMM yyyy}, so Business features are off. Your data is untouched.");
    }

    /// <summary>Active people. Everyone who can sign in takes a seat, whatever their role; API keys are not people and are not counted.</summary>
    public static Task<int> CountSeatsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        db.Set<AppUser>().AsNoTracking().CountAsync(u => u.IsActive && !db.Set<ApiKey>().Any(k => k.ServiceUserEmail == u.Email), ct);

    /// <summary>Refuses to add one more person when every seat in the license is taken. Pending invitations hold a seat.</summary>
    public async Task RequireFreeSeatAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if ((await GetAsync(ct)).SeatLimit is not { } limit)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await db.Set<UserInvite>().AsNoTracking()
            .Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
            .Select(i => i.Email).Distinct().CountAsync(ct);
        if (await CountSeatsAsync(db, ct) + pending >= limit)
        {
            throw new WorkbenchEditionException($"All {limit} seats in your license are in use. Deactivate someone who has left, or add seats under Setup › License.");
        }
    }
}

/// <summary>Stops a Business-only action on the Community edition, with a message that says what to do about it.</summary>
public sealed class FeatureGate(EditionService editions)
{
    public async Task RequireAsync(Feature feature, CancellationToken ct)
    {
        var state = await editions.GetAsync(ct);
        if (!FeatureCatalog.IsIncluded(state.Edition, feature))
        {
            var info = FeatureCatalog.Find(feature);
            throw new WorkbenchEditionException($"{info.Name} is part of Workbench Business. An administrator can add a license under Setup › License.");
        }
    }

    public async Task<bool> HasAsync(Feature feature, CancellationToken ct) =>
        FeatureCatalog.IsIncluded((await editions.GetAsync(ct)).Edition, feature);
}

public static class LicensingRegistration
{
    public static IServiceCollection AddWorkbenchLicensing(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new LicenseOptions());
        services.TryAddSingleton<IHostedPlanSource, NoHostedPlan>();
        services.AddScoped<EditionService>();
        services.AddScoped<FeatureGate>();
        services.AddScoped<ILicenseService, LicenseService>();
        return services;
    }
}
