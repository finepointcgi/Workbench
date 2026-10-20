using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Materials;

namespace Workbench.Api;

internal static class SalesShippingEndpoints
{
    public static void MapSalesShippingEndpoints(this RouteGroupBuilder v1)
    {
        var m = v1.MapGroup("/materials").WithTags("Sales and shipping").AddEndpointFilter(MapConflicts);

        // Sales orders
        m.MapGet("/sales-orders", (SalesOrderState? status, Guid? customerId, Guid? contractId, bool? openOnly, string? search, ISalesOrderService s, CancellationToken ct) =>
            s.ListAsync(new SalesOrderQuery(status, customerId, contractId, openOnly ?? false, search), ct));
        m.MapGet("/sales-orders/options", (ISalesOrderService s, CancellationToken ct) => s.GetOptionsAsync(ct));
        m.MapGet("/sales-orders/{id:guid}", (Guid id, ISalesOrderService s, CancellationToken ct) => s.GetAsync(id, ct));
        m.MapPost("/sales-orders", (SaveSalesOrderRequest r, ISalesOrderService s, CancellationToken ct) => s.CreateAsync(r, ct));
        m.MapPut("/sales-orders/{id:guid}", (Guid id, SaveSalesOrderRequest r, ISalesOrderService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        m.MapPost("/sales-orders/{id:guid}/clin-lines", (Guid id, AddClinLinesRequest r, ISalesOrderService s, CancellationToken ct) => s.AddLinesFromClinsAsync(id, r, ct));
        m.MapPost("/sales-orders/{id:guid}/release", (Guid id, ISalesOrderService s, CancellationToken ct) => s.ReleaseAsync(id, ct));
        m.MapPost("/sales-orders/{id:guid}/hold", (Guid id, SetHoldRequest r, ISalesOrderService s, CancellationToken ct) => s.SetHoldAsync(id, r, ct));
        m.MapPost("/sales-orders/{id:guid}/close", (Guid id, ISalesOrderService s, CancellationToken ct) => s.CloseAsync(id, ct));
        m.MapPost("/sales-orders/{id:guid}/cancel", (Guid id, CancelSalesOrderRequest r, ISalesOrderService s, CancellationToken ct) => s.CancelAsync(id, r, ct));
        m.MapPost("/sales-orders/{id:guid}/pick-lists", (Guid id, CreatePickListRequest r, IShippingService s, CancellationToken ct) => s.CreatePickListAsync(id, r, ct));
        m.MapGet("/sales-order-lines/{id:guid}/stock", (Guid id, IShippingService s, CancellationToken ct) => s.ListPickableStockAsync(id, ct));

        // Pick lists
        m.MapGet("/pick-lists", (bool? openOnly, IShippingService s, CancellationToken ct) => s.ListPickListsAsync(openOnly ?? true, ct));
        m.MapGet("/pick-lists/{id:guid}", (Guid id, IShippingService s, CancellationToken ct) => s.GetPickListAsync(id, ct));
        m.MapPost("/pick-lists/{id:guid}/cancel", (Guid id, IShippingService s, CancellationToken ct) => s.CancelPickListAsync(id, ct));
        m.MapPost("/pick-list-lines/{id:guid}/pick", (Guid id, ConfirmPickRequest r, IShippingService s, CancellationToken ct) => s.ConfirmPickAsync(id, r, ct));

        // Shipments
        m.MapGet("/shipments", (ShipmentState? status, Guid? salesOrderId, DateOnly? from, DateOnly? to, string? search, int? take, IShippingService s, CancellationToken ct) =>
            s.ListShipmentsAsync(new ShipmentQuery(status, salesOrderId, from, to, search, take ?? 500), ct));
        m.MapPost("/shipments", (CreateShipmentRequest r, IShippingService s, CancellationToken ct) => s.CreateShipmentAsync(r, ct));
        m.MapGet("/shipments/{id:guid}", (Guid id, IShippingService s, CancellationToken ct) => s.GetShipmentAsync(id, ct));
        m.MapPut("/shipments/{id:guid}", (Guid id, SaveShipmentRequest r, IShippingService s, CancellationToken ct) => s.SaveShipmentAsync(id, r, ct));
        m.MapPost("/shipments/{id:guid}/confirm", (Guid id, ConfirmShipmentRequest r, IShippingService s, CancellationToken ct) => s.ConfirmShipmentAsync(id, r, ct));
        m.MapPost("/shipments/{id:guid}/cancel", (Guid id, IShippingService s, CancellationToken ct) => s.CancelShipmentAsync(id, ct));
        m.MapGet("/shipments/{id:guid}/dd250", (Guid id, IShippingService s, CancellationToken ct) => s.GetDd250Async(id, ct));
        m.MapGet("/shipments/{id:guid}/dd250.pdf", (Guid id, IShippingService s, CancellationToken ct) => s.GetDd250PdfAsync(id, ct));
        m.MapGet("/shipments/{id:guid}/wawf/validate", (Guid id, WawfDocument type, IShippingService s, CancellationToken ct) => s.ValidateWawfAsync(id, type, ct));
        m.MapPost("/shipments/{id:guid}/wawf/export", (Guid id, WawfDocument type, IShippingService s, CancellationToken ct) => s.ExportWawfAsync(id, type, ct));
        m.MapPost("/shipments/{id:guid}/wawf/submit", (Guid id, WawfDocument type, IShippingService s, CancellationToken ct) => s.SubmitWawfAsync(id, type, ct));
        m.MapPost("/shipments/{id:guid}/wawf/status", (Guid id, SetWawfStatusRequest r, IShippingService s, CancellationToken ct) => s.SetWawfStatusAsync(id, r, ct));
        m.MapPost("/shipments/{id:guid}/invoice", (Guid id, IShippingService s, CancellationToken ct) => s.CreateInvoiceAsync(id, ct));
    }

    /// <summary>Wrong state (order not released, shipment already confirmed) → 409.</summary>
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
