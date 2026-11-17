using Workbench.Contracts;
using Workbench.Contracts.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>
/// Adds the seats used and allowed in each module to the license status. A decorator so the status code itself stays as it was.
/// Reads only: activating and removing a license pass straight through.
/// </summary>
public sealed class ModuleSeatLicenseService(ILicenseService inner, IWorkbenchDbContextFactory dbFactory, EditionService editions, ModuleSeatService seats) : ILicenseService
{
    public async Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => await WithSeatsAsync(await inner.GetStatusAsync(cancellationToken), cancellationToken);

    public async Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default) =>
        await WithSeatsAsync(await inner.ActivateAsync(request, cancellationToken), cancellationToken);

    public async Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default) => await WithSeatsAsync(await inner.RemoveAsync(cancellationToken), cancellationToken);

    private bool Whole(string id) => editions.Modules.FirstOrDefault(m => m.Id == id)?.WholeModule ?? true;

    private async Task<LicenseStatusDto> WithSeatsAsync(LicenseStatusDto status, CancellationToken ct)
    {
        if (status.Modules is not { Count: > 0 } modules)
        {
            return status;
        }

        var state = await editions.GetAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var usage = (await seats.UsageAsync(db, state, ct)).ToDictionary(u => u.ModuleId);
        return status with
        {
            Modules = modules.Select(m => usage.TryGetValue(m.Id, out var u) ? m with { SeatsUsed = u.Used, SeatsAllowed = u.Allowed, WholeModule = Whole(m.Id) } : m with { WholeModule = Whole(m.Id) }).ToList(),
        };
    }
}
