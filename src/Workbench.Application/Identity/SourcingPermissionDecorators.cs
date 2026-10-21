using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Identity;

/// <summary>
/// Sourcing, the supplier portal and EDI. Internally (RFQs, quote comparison and award, portal contacts and activity,
/// acknowledgements, ship notices, EDI partners and log) it's the buyers: Finance or Admin. The supplier portal is a
/// different kind of caller altogether: a vendor contact from the portal's own sign-in, never an internal user, who
/// sees only their own vendor's records (anything else is not found) and whose every call is in the portal activity log.
/// </summary>
public static class SourcingPermissionRegistration
{
    public static IServiceCollection AddWorkbenchSourcingPermissions(this IServiceCollection services)
    {
        services.Decorate<IRfqService, PermissionCheckingRfqService>();
        services.Decorate<ISupplierPortalAdminService, PermissionCheckingSupplierPortalAdminService>();
        services.Decorate<IEdiService, PermissionCheckingEdiService>();
        services.Decorate<ISupplierPortalService, VendorScopedSupplierPortalService>();
        return services;
    }
}

public sealed class PermissionCheckingRfqService(IRfqService inner, AccessControl access) : IRfqService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SourcingManage, "work with RFQs and quotes", ct);

    public async Task<IReadOnlyList<RfqSummaryDto>> ListAsync(RfqQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<RfqDto> GetAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetAsync(rfqId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListSourceableRequisitionLinesAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListSourceableRequisitionLinesAsync(cancellationToken);
    }

    public async Task<RfqDto> CreateAsync(SaveRfqRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<RfqDto> UpdateAsync(Guid rfqId, SaveRfqRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UpdateAsync(rfqId, request, cancellationToken);
    }

    public async Task<RfqDto> IssueAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.IssueAsync(rfqId, cancellationToken);
    }

    public async Task<RfqDto> InviteVendorAsync(Guid rfqId, Guid vendorId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.InviteVendorAsync(rfqId, vendorId, cancellationToken);
    }

    public async Task<RfqDto> RecordQuoteAsync(Guid rfqId, SaveQuoteRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RecordQuoteAsync(rfqId, request, cancellationToken);
    }

    public async Task<RfqComparisonDto> GetComparisonAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetComparisonAsync(rfqId, cancellationToken);
    }

    public async Task<RfqDto> AwardAsync(Guid rfqId, AwardRfqRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.AwardAsync(rfqId, request, cancellationToken);
    }

    public async Task<RfqDto> CancelAsync(Guid rfqId, RfqReasonRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CancelAsync(rfqId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingSupplierPortalAdminService(ISupplierPortalAdminService inner, AccessControl access) : ISupplierPortalAdminService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SourcingManage, "manage the supplier portal", ct);

    public async Task<IReadOnlyList<SupplierContactDto>> ListContactsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListContactsAsync(vendorId, cancellationToken);
    }

    public async Task<SupplierContactDto> AddContactAsync(SaveSupplierContactRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.AddContactAsync(request, cancellationToken);
    }

    public async Task<SupplierContactDto> SendSignInLinkAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SendSignInLinkAsync(contactId, cancellationToken);
    }

    public async Task<SupplierContactDto> DeactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.DeactivateContactAsync(contactId, cancellationToken);
    }

    public async Task<SupplierContactDto> ReactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ReactivateContactAsync(contactId, cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierPortalEventDto>> ListActivityAsync(Guid? vendorId = null, int take = 200, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListActivityAsync(vendorId, take, cancellationToken);
    }

    public async Task<IReadOnlyList<AcknowledgementDto>> ListAcknowledgementsAsync(Guid? purchaseOrderId = null, bool needsReviewOnly = false, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListAcknowledgementsAsync(purchaseOrderId, needsReviewOnly, cancellationToken);
    }

    public async Task<AcknowledgementDto> MarkAcknowledgementReviewedAsync(Guid acknowledgementId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.MarkAcknowledgementReviewedAsync(acknowledgementId, cancellationToken);
    }

    public async Task<IReadOnlyList<ShipNoticeDto>> ListShipNoticesAsync(Guid? purchaseOrderId = null, bool inTransitOnly = false, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListShipNoticesAsync(purchaseOrderId, inTransitOnly, cancellationToken);
    }

    public async Task<ShipNoticeDto> ReceiveShipNoticeAsync(Guid shipNoticeId, DateOnly receivedOn, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ReceiveShipNoticeAsync(shipNoticeId, receivedOn, cancellationToken);
    }
}

public sealed class PermissionCheckingEdiService(IEdiService inner, AccessControl access) : IEdiService
{
    private Task RequireAsync(CancellationToken ct) => access.RequireAsync(Permission.SourcingManage, "work with EDI", ct);

    public async Task<IReadOnlyList<EdiPartnerDto>> ListPartnersAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListPartnersAsync(cancellationToken);
    }

    public async Task<EdiPartnerDto> SavePartnerAsync(Guid? partnerId, SaveEdiPartnerRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SavePartnerAsync(partnerId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<EdiMessageDto>> ListLogAsync(EdiLogQuery query, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListLogAsync(query, cancellationToken);
    }

    public async Task<EdiMessageDetailDto> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetMessageAsync(messageId, cancellationToken);
    }

    public async Task<EdiMessageDto> SendPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SendPurchaseOrderAsync(purchaseOrderId, cancellationToken);
    }

    public async Task<EdiPollResultDto> PollAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.PollAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EdiMessageDto>> ImportAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ImportAsync(fileName, content, cancellationToken);
    }

    public async Task<IReadOnlyList<EdiMessageDto>> RetryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RetryAsync(messageId, cancellationToken);
    }
}

/// <summary>
/// The supplier portal's authorization. The caller is the contact from <see cref="ISupplierPrincipal"/> (the portal's
/// own sign-in, never an internal user); it must be active and its vendor not inactive, or the call is refused
/// (<see cref="WorkbenchForbiddenException"/>). An RFQ must be one the vendor was invited to and has been sent; a purchase
/// order must be the vendor's and issued; otherwise it's not found, exactly as if it didn't exist. Every call, allowed,
/// refused or failed, is written to the portal activity log.
/// </summary>
public sealed class VendorScopedSupplierPortalService(
    ISupplierPortalService inner,
    ISupplierPrincipal principal,
    SupplierCaller caller,
    SupplierPortalLog log,
    IWorkbenchDbContextFactory dbFactory) : ISupplierPortalService
{
    /// <summary>Resolved on every call (never trusted from earlier in the scope), so a deactivation takes effect at once.</summary>
    private async Task EnsureCallerAsync(CancellationToken ct)
    {
        if (principal.ContactId is not { } contactId)
        {
            await log.RecordAsync(null, null, "(not signed in)", "portal.denied", outcome: "Denied", detail: "No portal sign-in.", ip: principal.IpAddress);
            throw new WorkbenchForbiddenException("Sign in to the supplier portal.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var contact = await db.Set<SupplierContact>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        var vendor = contact is null ? null : await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == contact.VendorId, ct);
        if (contact is null || vendor is null || !contact.IsActive || vendor.Status == VendorStatus.Inactive)
        {
            await log.RecordAsync(contact?.Id, vendor?.Id, contact?.Email ?? "(unknown contact)", "portal.denied", outcome: "Denied",
                detail: "The contact or vendor is inactive.", ip: principal.IpAddress);
            throw new WorkbenchForbiddenException("Your supplier portal access has ended. Contact your buyer.");
        }

        caller.Set(contact, vendor);
    }

    private async Task<T> RunAsync<T>(string action, Func<Task<T>> call, CancellationToken ct, string? target = null, Guid? targetId = null, Func<T, string?>? describe = null)
    {
        await EnsureCallerAsync(ct);
        try
        {
            var result = await call();
            await log.RecordAsync(caller.Contact.Id, caller.VendorId, caller.Contact.Email, action, target, targetId, detail: describe?.Invoke(result), ip: principal.IpAddress);
            return result;
        }
        catch (Exception ex) when (ex is WorkbenchNotFoundException or UnauthorizedAccessException)
        {
            await log.RecordAsync(caller.Contact.Id, caller.VendorId, caller.Contact.Email, action, target, targetId, "Denied", "Not this vendor's record.", principal.IpAddress);
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await log.RecordAsync(caller.Contact.Id, caller.VendorId, caller.Contact.Email, action, target, targetId, "Failed", ex.Message, principal.IpAddress);
            throw;
        }
    }

    /// <summary>The RFQ was sent to this vendor; else not found.</summary>
    private async Task<string> RequireRfqAsync(Guid rfqId, CancellationToken ct)
    {
        await EnsureCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var number = await db.Set<RfqVendor>().AsNoTracking().Where(v => v.RfqId == rfqId && v.VendorId == caller.VendorId)
            .Join(db.Set<Rfq>(), v => v.RfqId, r => r.Id, (v, r) => r).Where(r => r.Status != RfqStatus.Draft).Select(r => r.Number).FirstOrDefaultAsync(ct);
        if (number is null)
        {
            await log.RecordAsync(caller.Contact.Id, caller.VendorId, caller.Contact.Email, "rfq.denied", targetId: rfqId, outcome: "Denied", detail: "Not invited.", ip: principal.IpAddress);
            throw WorkbenchNotFoundException.For("RFQ", rfqId);
        }

        return number;
    }

    /// <summary>The purchase order is this vendor's and issued; else not found.</summary>
    private async Task<string> RequireOrderAsync(Guid purchaseOrderId, CancellationToken ct)
    {
        await EnsureCallerAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var number = await db.Set<PurchaseOrder>().AsNoTracking()
            .Where(o => o.Id == purchaseOrderId && o.VendorId == caller.VendorId && o.Status != PurchaseOrderStatus.Draft).Select(o => o.Number).FirstOrDefaultAsync(ct);
        if (number is null)
        {
            await log.RecordAsync(caller.Contact.Id, caller.VendorId, caller.Contact.Email, "order.denied", targetId: purchaseOrderId, outcome: "Denied", detail: "Not this vendor's order.", ip: principal.IpAddress);
            throw WorkbenchNotFoundException.For("Purchase order", purchaseOrderId);
        }

        return number;
    }

    public async Task<SupplierSessionDto> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureCallerAsync(cancellationToken);
        return await inner.GetSessionAsync(cancellationToken);
    }

    public Task<SupplierHomeDto> GetHomeAsync(CancellationToken cancellationToken = default) =>
        RunAsync("home.viewed", () => inner.GetHomeAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SupplierRfqSummaryDto>> ListRfqsAsync(CancellationToken cancellationToken = default) =>
        RunAsync("rfqs.listed", () => inner.ListRfqsAsync(cancellationToken), cancellationToken);

    public async Task<SupplierRfqDto> GetRfqAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        var number = await RequireRfqAsync(rfqId, cancellationToken);
        return await RunAsync("rfq.viewed", () => inner.GetRfqAsync(rfqId, cancellationToken), cancellationToken, number, rfqId);
    }

    public async Task<SupplierRfqDto> SubmitQuoteAsync(Guid rfqId, SupplierQuoteRequest request, CancellationToken cancellationToken = default)
    {
        var number = await RequireRfqAsync(rfqId, cancellationToken);
        return await RunAsync("quote.submitted", () => inner.SubmitQuoteAsync(rfqId, request, cancellationToken), cancellationToken, number, rfqId,
            r => $"{r.MyQuote?.Lines.Count(l => !l.NoBid) ?? 0} line(s) quoted");
    }

    public async Task<SupplierRfqDto> DeclineRfqAsync(Guid rfqId, string? reason, CancellationToken cancellationToken = default)
    {
        var number = await RequireRfqAsync(rfqId, cancellationToken);
        return await RunAsync("rfq.declined", () => inner.DeclineRfqAsync(rfqId, reason, cancellationToken), cancellationToken, number, rfqId);
    }

    public Task<IReadOnlyList<SupplierOrderSummaryDto>> ListOrdersAsync(CancellationToken cancellationToken = default) =>
        RunAsync("orders.listed", () => inner.ListOrdersAsync(cancellationToken), cancellationToken);

    public async Task<SupplierOrderDto> GetOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        var number = await RequireOrderAsync(purchaseOrderId, cancellationToken);
        return await RunAsync("order.viewed", () => inner.GetOrderAsync(purchaseOrderId, cancellationToken), cancellationToken, number, purchaseOrderId);
    }

    public async Task<SupplierOrderDto> AcknowledgeOrderAsync(Guid purchaseOrderId, AcknowledgeOrderRequest request, CancellationToken cancellationToken = default)
    {
        var number = await RequireOrderAsync(purchaseOrderId, cancellationToken);
        return await RunAsync("order.acknowledged", () => inner.AcknowledgeOrderAsync(purchaseOrderId, request, cancellationToken), cancellationToken, number, purchaseOrderId,
            _ => request.Status.ToString());
    }

    public async Task<SupplierOrderDto> SubmitShipNoticeAsync(Guid purchaseOrderId, ShipNoticeRequest request, CancellationToken cancellationToken = default)
    {
        var number = await RequireOrderAsync(purchaseOrderId, cancellationToken);
        return await RunAsync("shipnotice.submitted", () => inner.SubmitShipNoticeAsync(purchaseOrderId, request, cancellationToken), cancellationToken, number, purchaseOrderId,
            r => r.ShipNotices.LastOrDefault()?.Number);
    }

    public Task<IReadOnlyList<SupplierInvoiceDto>> ListInvoicesAsync(CancellationToken cancellationToken = default) =>
        RunAsync("invoices.listed", () => inner.ListInvoicesAsync(cancellationToken), cancellationToken);

    public async Task<SupplierInvoiceDto> SubmitInvoiceAsync(SupplierInvoiceRequest request, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var number = await RequireOrderAsync(request.PurchaseOrderId, cancellationToken);
        return await RunAsync("invoice.submitted", () => inner.SubmitInvoiceAsync(request, fileName, content, cancellationToken), cancellationToken, number, request.PurchaseOrderId,
            r => $"Invoice {r.InvoiceNumber}, {r.Amount:N2}");
    }
}
