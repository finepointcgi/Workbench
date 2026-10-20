using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Identity;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// The buyers' side of the supplier portal: who at each vendor may sign in, what they've done there, and the
/// acknowledgements and ship notices vendors sent (portal or EDI). Receiving a ship notice is an ordinary receipt.
/// </summary>
public sealed class SupplierPortalAdminService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    SupplierSignIn signIn,
    SupplierPortalLog log,
    IPurchaseOrderService orders,
    TimeProvider clock) : ISupplierPortalAdminService
{
    public const string ContactEntity = nameof(SupplierContact);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<SupplierContactDto>> ListContactsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<SupplierContact>().AsNoTracking();
        if (vendorId is { } v)
        {
            q = q.Where(c => c.VendorId == v);
        }

        var contacts = await q.ToListAsync(cancellationToken);
        var vendorIds = contacts.Select(c => c.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(x => vendorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        return contacts.OrderBy(c => vendors[c.VendorId].Name).ThenBy(c => c.Name).Select(c => Dto(c, vendors[c.VendorId])).ToList();
    }

    public async Task<SupplierContactDto> AddContactAsync(SaveSupplierContactRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var email = PurchasingSupport.Normalize(request.Email);
        if (email.Length is 0 or > 320 || !email.Contains('@') || email.Contains(' '))
        {
            throw new ArgumentException("Enter the contact's email address.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, request.VendorId, cancellationToken);
        if (vendor.Status == VendorStatus.Inactive)
        {
            throw new ArgumentException($"{vendor.Name} is inactive.");
        }

        if (await db.Set<SupplierContact>().AnyAsync(c => c.Email == email, cancellationToken))
        {
            throw new ArgumentException($"{email} already has supplier portal access (one vendor per email).");
        }

        // Keep the two kinds of sign-in apart: an internal user's email can't also be a vendor contact.
        if (await db.Set<AppUser>().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ArgumentException($"{email} is a Workbench user. Vendor contacts need their own email address.");
        }

        var contact = new SupplierContact
        {
            VendorId = vendor.Id,
            Email = email,
            Name = PurchasingSupport.Required(request.Name, "Name", 200),
            InvitedBy = access.Email,
            CreatedAtUtc = UtcNow,
        };
        db.Set<SupplierContact>().Add(contact);
        audit.Record(db, ContactEntity, contact.Id, "Added", after: new { Vendor = vendor.Code, contact.Email, contact.Name });
        await db.SaveChangesAsync(cancellationToken);
        if (request.SendSignInLink)
        {
            await signIn.SendLinkAsync(db, contact, vendor, access.Email, cancellationToken);
        }

        return Dto(contact, vendor);
    }

    public async Task<SupplierContactDto> SendSignInLinkAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (contact, vendor) = await LoadAsync(db, contactId, cancellationToken);
        if (!contact.IsActive || vendor.Status == VendorStatus.Inactive)
        {
            throw new InvalidOperationException("Reactivate the contact (and the vendor) first.");
        }

        await signIn.SendLinkAsync(db, contact, vendor, access.Email, cancellationToken);
        return Dto(contact, vendor);
    }

    public async Task<SupplierContactDto> DeactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (contact, vendor) = await LoadAsync(db, contactId, cancellationToken);
        if (!contact.IsActive)
        {
            return Dto(contact, vendor);
        }

        contact.IsActive = false;
        contact.SessionVersion++;
        contact.DeactivatedAtUtc = UtcNow;
        contact.DeactivatedBy = access.Email;
        audit.Record(db, ContactEntity, contact.Id, "Deactivated", new { IsActive = true }, new { IsActive = false });
        await db.SaveChangesAsync(cancellationToken);
        await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "access.ended", detail: $"Deactivated by {access.Email}");
        return Dto(contact, vendor);
    }

    public async Task<SupplierContactDto> ReactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (contact, vendor) = await LoadAsync(db, contactId, cancellationToken);
        if (contact.IsActive)
        {
            return Dto(contact, vendor);
        }

        contact.IsActive = true;
        contact.DeactivatedAtUtc = null;
        contact.DeactivatedBy = null;
        contact.FailedSignIns = 0;
        contact.LockedUntilUtc = null;
        audit.Record(db, ContactEntity, contact.Id, "Reactivated", new { IsActive = false }, new { IsActive = true });
        await db.SaveChangesAsync(cancellationToken);
        return Dto(contact, vendor);
    }

    public async Task<IReadOnlyList<SupplierPortalEventDto>> ListActivityAsync(Guid? vendorId = null, int take = 200, CancellationToken cancellationToken = default)
    {
        var events = await log.ListAsync(vendorId, take, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = events.Where(e => e.VendorId != null).Select(e => e.VendorId!.Value).Distinct().ToList();
        var names = await db.Set<Vendor>().AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        return events.Select(e => new SupplierPortalEventDto(e.OccurredAtUtc, e.Actor, e.VendorId is { } v ? names.GetValueOrDefault(v) : null, e.Action, e.Target, e.Outcome, e.Detail)).ToList();
    }

    public async Task<IReadOnlyList<AcknowledgementDto>> ListAcknowledgementsAsync(Guid? purchaseOrderId = null, bool needsReviewOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<PurchaseOrderAcknowledgement>().AsNoTracking().Include(a => a.Lines).AsQueryable();
        if (purchaseOrderId is { } po)
        {
            q = q.Where(a => a.PurchaseOrderId == po);
        }

        if (needsReviewOnly)
        {
            q = q.Where(a => a.Status != PurchaseOrderAcknowledgementStatus.Accepted && a.ReviewedAtUtc == null);
        }

        var acks = await q.OrderByDescending(a => a.AcknowledgedAtUtc).Take(500).ToListAsync(cancellationToken);
        return await AckDtosAsync(db, acks, cancellationToken);
    }

    public async Task<AcknowledgementDto> MarkAcknowledgementReviewedAsync(Guid acknowledgementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ack = await db.Set<PurchaseOrderAcknowledgement>().Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == acknowledgementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Acknowledgement", acknowledgementId);
        if (ack.ReviewedAtUtc is null)
        {
            ack.ReviewedBy = access.Email;
            ack.ReviewedAtUtc = UtcNow;
            audit.Record(db, ProcurementSupport.OrderEntity, ack.PurchaseOrderId, "AcknowledgementReviewed", after: new { ack.Revision, Status = ack.Status.ToString() });
            await db.SaveChangesAsync(cancellationToken);
        }

        return (await AckDtosAsync(db, [ack], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<ShipNoticeDto>> ListShipNoticesAsync(Guid? purchaseOrderId = null, bool inTransitOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<ShipNotice>().AsNoTracking().Include(s => s.Lines).AsQueryable();
        if (purchaseOrderId is { } po)
        {
            q = q.Where(s => s.PurchaseOrderId == po);
        }

        if (inTransitOnly)
        {
            q = q.Where(s => s.Status == ShipNoticeStatus.InTransit);
        }

        var notices = await q.OrderByDescending(s => s.SubmittedAtUtc).Take(500).ToListAsync(cancellationToken);
        return await NoticeDtosAsync(db, notices, cancellationToken);
    }

    public async Task<ShipNoticeDto> ReceiveShipNoticeAsync(Guid shipNoticeId, DateOnly receivedOn, CancellationToken cancellationToken = default)
    {
        ShipNotice notice;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            notice = await db.Set<ShipNotice>().AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == shipNoticeId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Ship notice", shipNoticeId);
            if (notice.Status != ShipNoticeStatus.InTransit)
            {
                throw new InvalidOperationException($"{notice.Number} is already {(notice.Status == ShipNoticeStatus.Received ? "received" : "cancelled")}.");
            }
        }

        // An ordinary receipt (receiving rules, inventory and permissions apply), then the notice points at it.
        var po = await orders.ReceiveAsync(notice.PurchaseOrderId, new ReceiveRequest(receivedOn,
            notice.Lines.Select(l => new ReceiveLineRequest(l.PurchaseOrderLineId, l.Quantity)).ToList(), $"Ship notice {notice.Number}"), cancellationToken);
        var receipt = po.Receipts.OrderBy(r => r.Number).Last();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var tracked = await db.Set<ShipNotice>().Include(s => s.Lines).FirstAsync(s => s.Id == shipNoticeId, cancellationToken);
            tracked.Status = ShipNoticeStatus.Received;
            tracked.ReceiptId = receipt.Id;
            audit.Record(db, SupplierDocuments.ShipNoticeEntity, tracked.Id, "Received", after: new { Receipt = receipt.Number, ReceivedOn = receivedOn });
            await db.SaveChangesAsync(cancellationToken);
            return (await NoticeDtosAsync(db, [tracked], cancellationToken))[0];
        }
    }

    // ---- Helpers ----

    private static async Task<(SupplierContact Contact, Vendor Vendor)> LoadAsync(IWorkbenchDbContext db, Guid contactId, CancellationToken ct)
    {
        var contact = await db.Set<SupplierContact>().FirstOrDefaultAsync(c => c.Id == contactId, ct) ?? throw WorkbenchNotFoundException.For("Supplier contact", contactId);
        return (contact, await PurchasingSupport.FindVendorAsync(db, contact.VendorId, ct));
    }

    private SupplierContactDto Dto(SupplierContact c, Vendor v) => new(c.Id, v.Id, v.Code, v.Name, c.Email, c.Name, c.IsActive, c.PasswordHash is not null,
        c.LockedUntilUtc is { } until && until > UtcNow, c.LastSignInUtc, c.InvitedBy, c.CreatedAtUtc);

    private static async Task<List<AcknowledgementDto>> AckDtosAsync(IWorkbenchDbContext db, IReadOnlyList<PurchaseOrderAcknowledgement> acks, CancellationToken ct)
    {
        var poIds = acks.Select(a => a.PurchaseOrderId).Distinct().ToList();
        var pos = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var vendorIds = acks.Select(a => a.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        return acks.Select(a =>
        {
            var po = pos[a.PurchaseOrderId];
            return new AcknowledgementDto(a.Id, po.Id, po.Number, vendors.GetValueOrDefault(a.VendorId) ?? "", a.Revision, (AcknowledgementState)(int)a.Status, a.Note,
                (SupplierChannel)(int)a.Source, a.AcknowledgedBy, a.AcknowledgedAtUtc, a.ReviewedBy,
                a.Lines.Select(l =>
                {
                    var pl = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
                    return new AcknowledgementLineDto(pl?.LineNumber ?? 0, pl?.Description ?? "(removed)", pl?.Quantity ?? 0, pl?.UnitPrice ?? 0, pl?.NeededBy,
                        l.PromisedDate, l.Quantity, l.UnitPrice, l.Note);
                }).OrderBy(l => l.LineNumber).ToList());
        }).ToList();
    }

    private static async Task<List<ShipNoticeDto>> NoticeDtosAsync(IWorkbenchDbContext db, IReadOnlyList<ShipNotice> notices, CancellationToken ct)
    {
        var poIds = notices.Select(n => n.PurchaseOrderId).Distinct().ToList();
        var pos = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).Where(o => poIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var vendorIds = notices.Select(n => n.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var receiptIds = notices.Where(n => n.ReceiptId != null).Select(n => n.ReceiptId!.Value).ToList();
        var receipts = await db.Set<PurchaseReceipt>().AsNoTracking().Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        return notices.Select(n =>
        {
            var po = pos[n.PurchaseOrderId];
            return new ShipNoticeDto(n.Id, n.Number, po.Id, po.Number, vendors.GetValueOrDefault(n.VendorId) ?? "", n.ShipmentReference, n.ShipDate, n.ExpectedDelivery,
                n.Carrier, n.TrackingNumber, (ShipNoticeState)(int)n.Status, (SupplierChannel)(int)n.Source, n.SubmittedBy, n.SubmittedAtUtc,
                n.ReceiptId is { } r ? receipts.GetValueOrDefault(r) : null,
                n.Lines.Select(l =>
                {
                    var pl = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
                    return new ShipNoticeLineDto(l.PurchaseOrderLineId, pl?.LineNumber ?? 0, pl?.Description ?? "(removed)", l.Quantity);
                }).OrderBy(l => l.LineNumber).ToList());
        }).ToList();
    }
}
