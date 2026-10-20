using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// The supplier portal contact signed in on this request, from the portal's own sign-in scheme (never the internal one).
/// The web host implements it from the portal cookie; everywhere else nobody is (<see cref="NoSupplierPrincipal"/>).
/// </summary>
public interface ISupplierPrincipal
{
    Guid? ContactId { get; }

    /// <summary>For the portal activity log.</summary>
    string? IpAddress { get; }
}

public sealed class NoSupplierPrincipal : ISupplierPrincipal
{
    public Guid? ContactId => null;

    public string? IpAddress => null;
}

/// <summary>
/// The contact and vendor a portal call runs for, resolved and checked by <see cref="VendorScopedSupplierPortalService"/>
/// (active contact, vendor not inactive). The portal service reads its vendor only from here.
/// </summary>
public sealed class SupplierCaller
{
    private SupplierContact? _contact;
    private Vendor? _vendor;

    public SupplierContact Contact => _contact ?? throw new UnauthorizedAccessException("No supplier portal contact is signed in.");

    public Vendor Vendor => _vendor ?? throw new UnauthorizedAccessException("No supplier portal contact is signed in.");

    public Guid VendorId => Vendor.Id;

    /// <summary>How portal changes are attributed in audit rows and documents: "Name &lt;email&gt; (supplier portal)".</summary>
    public string Actor => SupplierDocuments.PortalActor(Contact);

    public bool IsSet => _contact is not null;

    internal void Set(SupplierContact contact, Vendor vendor) => (_contact, _vendor) = (contact, vendor);
}

/// <summary>Writes the portal activity log (<see cref="SupplierPortalEvent"/>), in its own unit of work so refusals are kept too.</summary>
public sealed class SupplierPortalLog(IWorkbenchDbContextFactory dbFactory, TimeProvider clock)
{
    public async Task RecordAsync(Guid? contactId, Guid? vendorId, string actor, string action, string? target = null, Guid? targetId = null,
        string outcome = "Success", string? detail = null, string? ip = null)
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        db.Set<SupplierPortalEvent>().Add(new SupplierPortalEvent
        {
            ContactId = contactId,
            VendorId = vendorId,
            Actor = Trim(actor, 320) ?? "(unknown)",
            Action = Trim(action, 64)!,
            Target = Trim(target, 64),
            TargetId = targetId,
            Outcome = Trim(outcome, 16)!,
            Detail = Trim(detail, 1000),
            IpAddress = Trim(ip, 64),
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<IReadOnlyList<SupplierPortalEvent>> ListAsync(Guid? vendorId, int take, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var q = db.Set<SupplierPortalEvent>().AsNoTracking();
        if (vendorId is { } v)
        {
            q = q.Where(e => e.VendorId == v);
        }

        return await q.OrderByDescending(e => e.OccurredAtUtc).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
    }

    private static string? Trim(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
