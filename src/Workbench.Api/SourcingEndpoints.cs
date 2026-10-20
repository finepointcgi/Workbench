using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Purchasing;

namespace Workbench.Api;

/// <summary>An X12 file uploaded by hand to the EDI log.</summary>
public sealed record EdiImportRequest(string FileName, string Content);

public sealed record ReceiveShipNoticeRequest(DateOnly ReceivedOn);

/// <summary>
/// RFQs and quotes, the buyers' side of the supplier portal, and EDI (Finance/Admin). The vendor-facing portal isn't here:
/// it lives under /supplier on the web host, with its own sign-in.
/// </summary>
internal static class SourcingEndpoints
{
    public static void MapSourcingEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/purchasing").WithTags("Sourcing").AddEndpointFilter(MapConflicts);

        // RFQs, quotes, comparison and award
        p.MapGet("/rfqs", (RfqState? status, string? search, IRfqService s, CancellationToken ct) => s.ListAsync(new RfqQuery(status, search), ct));
        p.MapGet("/rfqs/sourceable-lines", (IRfqService s, CancellationToken ct) => s.ListSourceableRequisitionLinesAsync(ct));
        p.MapGet("/rfqs/{id:guid}", (Guid id, IRfqService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/rfqs", (SaveRfqRequest r, IRfqService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/rfqs/{id:guid}", (Guid id, SaveRfqRequest r, IRfqService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPost("/rfqs/{id:guid}/issue", (Guid id, IRfqService s, CancellationToken ct) => s.IssueAsync(id, ct));
        p.MapPost("/rfqs/{id:guid}/vendors/{vendorId:guid}", (Guid id, Guid vendorId, IRfqService s, CancellationToken ct) => s.InviteVendorAsync(id, vendorId, ct));
        p.MapPost("/rfqs/{id:guid}/quotes", (Guid id, SaveQuoteRequest r, IRfqService s, CancellationToken ct) => s.RecordQuoteAsync(id, r, ct));
        p.MapGet("/rfqs/{id:guid}/comparison", (Guid id, IRfqService s, CancellationToken ct) => s.GetComparisonAsync(id, ct));
        p.MapPost("/rfqs/{id:guid}/award", (Guid id, AwardRfqRequest r, IRfqService s, CancellationToken ct) => s.AwardAsync(id, r, ct));
        p.MapPost("/rfqs/{id:guid}/cancel", (Guid id, RfqReasonRequest r, IRfqService s, CancellationToken ct) => s.CancelAsync(id, r, ct));

        // Supplier portal: contacts, activity, acknowledgements and ship notices
        p.MapGet("/supplier-portal/contacts", (Guid? vendorId, ISupplierPortalAdminService s, CancellationToken ct) => s.ListContactsAsync(vendorId, ct));
        p.MapPost("/supplier-portal/contacts", (SaveSupplierContactRequest r, ISupplierPortalAdminService s, CancellationToken ct) => s.AddContactAsync(r, ct));
        p.MapPost("/supplier-portal/contacts/{id:guid}/send-link", (Guid id, ISupplierPortalAdminService s, CancellationToken ct) => s.SendSignInLinkAsync(id, ct));
        p.MapPost("/supplier-portal/contacts/{id:guid}/deactivate", (Guid id, ISupplierPortalAdminService s, CancellationToken ct) => s.DeactivateContactAsync(id, ct));
        p.MapPost("/supplier-portal/contacts/{id:guid}/reactivate", (Guid id, ISupplierPortalAdminService s, CancellationToken ct) => s.ReactivateContactAsync(id, ct));
        p.MapGet("/supplier-portal/activity", (Guid? vendorId, int? take, ISupplierPortalAdminService s, CancellationToken ct) => s.ListActivityAsync(vendorId, take ?? 200, ct));
        p.MapGet("/supplier-portal/acknowledgements", (Guid? purchaseOrderId, bool? needsReview, ISupplierPortalAdminService s, CancellationToken ct) =>
            s.ListAcknowledgementsAsync(purchaseOrderId, needsReview ?? false, ct));
        p.MapPost("/supplier-portal/acknowledgements/{id:guid}/reviewed", (Guid id, ISupplierPortalAdminService s, CancellationToken ct) => s.MarkAcknowledgementReviewedAsync(id, ct));
        p.MapGet("/supplier-portal/ship-notices", (Guid? purchaseOrderId, bool? inTransit, ISupplierPortalAdminService s, CancellationToken ct) =>
            s.ListShipNoticesAsync(purchaseOrderId, inTransit ?? false, ct));
        p.MapPost("/supplier-portal/ship-notices/{id:guid}/receive", (Guid id, ReceiveShipNoticeRequest r, ISupplierPortalAdminService s, CancellationToken ct) =>
            s.ReceiveShipNoticeAsync(id, r.ReceivedOn, ct));

        // EDI
        var e = v1.MapGroup("/edi").WithTags("EDI").AddEndpointFilter(MapConflicts);
        e.MapGet("/partners", (IEdiService s, CancellationToken ct) => s.ListPartnersAsync(ct));
        e.MapPost("/partners", (SaveEdiPartnerRequest r, IEdiService s, CancellationToken ct) => s.SavePartnerAsync(null, r, ct));
        e.MapPut("/partners/{id:guid}", (Guid id, SaveEdiPartnerRequest r, IEdiService s, CancellationToken ct) => s.SavePartnerAsync(id, r, ct));
        e.MapGet("/messages", (EdiFlow? direction, EdiOutcome? status, string? transactionSet, Guid? vendorId, int? take, IEdiService s, CancellationToken ct) =>
            s.ListLogAsync(new EdiLogQuery(direction, status, transactionSet, vendorId, take ?? 200), ct));
        e.MapGet("/messages/{id:guid}", (Guid id, IEdiService s, CancellationToken ct) => s.GetMessageAsync(id, ct));
        e.MapPost("/messages/{id:guid}/retry", (Guid id, IEdiService s, CancellationToken ct) => s.RetryAsync(id, ct));
        e.MapPost("/orders/{purchaseOrderId:guid}/send", (Guid purchaseOrderId, IEdiService s, CancellationToken ct) => s.SendPurchaseOrderAsync(purchaseOrderId, ct));
        e.MapPost("/poll", (IEdiService s, CancellationToken ct) => s.PollAsync(ct));
        e.MapPost("/import", (EdiImportRequest r, IEdiService s, CancellationToken ct) => s.ImportAsync(r.FileName, r.Content, ct));
    }

    /// <summary>Wrong state (already issued, awarded, received) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
