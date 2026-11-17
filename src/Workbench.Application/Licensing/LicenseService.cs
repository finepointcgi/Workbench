using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

public sealed class LicenseService(IWorkbenchDbContextFactory dbFactory, EditionService editions, LicenseOptions options, ICurrentUser currentUser, TimeProvider clock, LicenseRefreshService refresh) : ILicenseService
{
    public async Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await editions.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var usage = await editions.UsageAsync(db, state, includePending: false, cancellationToken);
        var features = FeatureCatalog.All
            .Select(f => new FeatureStatusDto(f.Feature.ToString(), f.Name, f.Description,
                FeatureCatalog.IsIncluded(state.Edition, f.Feature) && EditionService.IsLicensed(state, f.Module), f.Module))
            .ToList();
        var stored = await db.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var modules = editions.Modules
            .Select(m => new ModuleStatusDto(m.Id, m.Name, m.Description, m.Enforced, EditionService.IsLicensed(state, m.Id), editions.HasModule(state, m.Id)))
            .ToList();
        return new LicenseStatusDto(state.Edition.ToString(), state.Source, state.Licensee, state.ExpiresOn, state.InGracePeriod,
            state.SeatLimit, usage.FullSeatsInUse, state.Problem, features, modules, state.LightSeatLimit, usage.LightSeatsInUse, state.Trial, state.DaysLeft, refresh.Describe(stored));
    }

    public async Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default)
    {
        if (!LicenseKeys.TryVerify(request.Key, options.PublicKeyPem, out var claims, out var problem) || claims is null)
        {
            throw new ArgumentException(problem);
        }

        if (DateOnly.FromDateTime(clock.GetLocalNow().DateTime) > claims.ExpiresOn.AddDays(claims.Trial ? 0 : options.GraceDays))
        {
            throw new ArgumentException($"That license expired on {claims.ExpiresOn:d MMM yyyy}.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var usage = await editions.UsageAsync(db, new EditionState(claims.Edition, EditionSource.Key, claims.Licensee, claims.ExpiresOn, false, claims.Seats, null, null, claims.LightSeats), includePending: false, cancellationToken);
            if (usage.FullSeatsInUse > claims.Seats)
            {
                throw new ArgumentException(claims.LightSeats == 0
                    ? $"That license has {claims.Seats} seats but {usage.People} people are active. Ask for a larger license, or deactivate some people first."
                    : $"That license has {claims.Seats} full and {claims.LightSeats} light seats but {usage.FullPeople} full and {usage.LightPeople} light people are active. Ask for a larger license, or deactivate some people first.");
            }

            var row = await db.Set<LicenseSettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
            if (row is null)
            {
                db.Set<LicenseSettings>().Add(row = new LicenseSettings());
            }

            (row.Key, row.ActivatedAtUtc, row.ActivatedBy) = (request.Key.Trim(), clock.GetUtcNow().UtcDateTime, currentUser.Email);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(cancellationToken);
    }

    public async Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            db.Set<LicenseSettings>().RemoveRange(await db.Set<LicenseSettings>().ToListAsync(cancellationToken));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetStatusAsync(cancellationToken);
    }
}
