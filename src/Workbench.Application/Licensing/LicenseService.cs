using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

public sealed class LicenseService(IWorkbenchDbContextFactory dbFactory, EditionService editions, LicenseOptions options, ICurrentUser currentUser, TimeProvider clock) : ILicenseService
{
    public async Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await editions.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (full, timeOnly) = await EditionService.CountSeatsAsync(db, cancellationToken);
        var features = FeatureCatalog.All
            .Select(f => new FeatureStatusDto(f.Feature.ToString(), f.Name, f.Description, FeatureCatalog.IsIncluded(state.Edition, f.Feature)))
            .ToList();
        return new LicenseStatusDto(state.Edition.ToString(), state.Source, state.Licensee, state.ExpiresOn, state.InGracePeriod,
            state.FullSeatLimit, full, timeOnly, state.Problem, features);
    }

    public async Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default)
    {
        if (!LicenseKeys.TryVerify(request.Key, options.PublicKeyPem, out var claims, out var problem) || claims is null)
        {
            throw new ArgumentException(problem);
        }

        if (DateOnly.FromDateTime(clock.GetLocalNow().DateTime) > claims.ExpiresOn.AddDays(options.GraceDays))
        {
            throw new ArgumentException($"That license expired on {claims.ExpiresOn:d MMM yyyy}.");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var (full, _) = await EditionService.CountSeatsAsync(db, cancellationToken);
            if (full > claims.Seats)
            {
                throw new ArgumentException($"That license has {claims.Seats} full seats but {full} people already hold a role beyond Employee. Ask for a larger license, or remove some roles first.");
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
