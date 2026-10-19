using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Materials;

namespace Workbench.Api;

internal static class MaterialsEndpoints
{
    public static void MapMaterialsEndpoints(this RouteGroupBuilder v1)
    {
        var m = v1.MapGroup("/materials").WithTags("Materials").AddEndpointFilter(MapConflicts);

        // Parts, warehouses, demand
        m.MapGet("/parts", (string? search, bool? activeOnly, PartSourceKind? source, IPartService s, CancellationToken ct) =>
            s.ListAsync(new PartQuery(search, activeOnly ?? true, source), ct));
        m.MapGet("/parts/{id:guid}", (Guid id, IPartService s, CancellationToken ct) => s.GetAsync(id, ct));
        m.MapPost("/parts", (SavePartRequest r, IPartService s, CancellationToken ct) => s.CreateAsync(r, ct));
        m.MapPut("/parts/{id:guid}", (Guid id, SavePartRequest r, IPartService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        m.MapPost("/parts/{id:guid}/standard-cost", (Guid id, ChangeStandardCostRequest r, IInventoryService s, CancellationToken ct) => s.ChangeStandardCostAsync(id, r, ct));
        m.MapGet("/warehouses", (IPartService s, CancellationToken ct) => s.ListWarehousesAsync(ct));
        m.MapPost("/warehouses", (SaveWarehouseRequest r, IPartService s, CancellationToken ct) => s.SaveWarehouseAsync(null, r, ct));
        m.MapPut("/warehouses/{id:guid}", (Guid id, SaveWarehouseRequest r, IPartService s, CancellationToken ct) => s.SaveWarehouseAsync(id, r, ct));
        m.MapPost("/warehouses/{id:guid}/bins", (Guid id, SaveBinRequest r, IPartService s, CancellationToken ct) => s.SaveBinAsync(id, null, r, ct));
        m.MapPut("/warehouses/{id:guid}/bins/{binId:guid}", (Guid id, Guid binId, SaveBinRequest r, IPartService s, CancellationToken ct) => s.SaveBinAsync(id, binId, r, ct));
        m.MapGet("/forecasts", (Guid? partId, IPartService s, CancellationToken ct) => s.ListForecastsAsync(partId, ct));
        m.MapPost("/forecasts", (SaveForecastRequest r, IPartService s, CancellationToken ct) => s.AddForecastAsync(r, ct));
        m.MapDelete("/forecasts/{id:guid}", async (Guid id, IPartService s, CancellationToken ct) =>
        {
            await s.DeleteForecastAsync(id, ct);
            return Results.NoContent();
        });
        m.MapGet("/requirements", (Guid? projectId, bool? openOnly, IPartService s, CancellationToken ct) => s.ListRequirementsAsync(projectId, openOnly ?? true, ct));
        m.MapPost("/requirements", (SaveMaterialRequirementRequest r, IPartService s, CancellationToken ct) => s.AddRequirementAsync(r, ct));
        m.MapPost("/requirements/{id:guid}/close", (Guid id, IPartService s, CancellationToken ct) => s.CloseRequirementAsync(id, ct));
        m.MapGet("/options", (IPartService s, CancellationToken ct) => s.GetOptionsAsync(ct));

        // Inventory
        m.MapGet("/inventory/balances", (Guid? partId, Guid? warehouseId, StockOwnership? ownership, Guid? projectId, string? search, IInventoryService s, CancellationToken ct) =>
            s.ListBalancesAsync(new BalanceQuery(partId, warehouseId, ownership, projectId, search), ct));
        m.MapGet("/inventory/transactions", (Guid? partId, Guid? workOrderId, Guid? projectId, InventoryMovement? type, DateOnly? from, DateOnly? to, string? search, int? take,
            IInventoryService s, CancellationToken ct) => s.ListTransactionsAsync(new TransactionQuery(partId, workOrderId, projectId, type, from, to, search, take ?? 200), ct));
        m.MapGet("/inventory/receivable", (IInventoryService s, CancellationToken ct) => s.ListReceivableLinesAsync(ct));
        m.MapGet("/inventory/linkable", (IInventoryService s, CancellationToken ct) => s.ListLinkableLinesAsync(ct));
        m.MapPost("/inventory/link", async (LinkPurchaseLineRequest r, IInventoryService s, CancellationToken ct) =>
        {
            await s.LinkPurchaseOrderLineAsync(r, ct);
            return Results.NoContent();
        });
        m.MapPost("/inventory/receive", (ReceiveIntoInventoryRequest r, IInventoryService s, CancellationToken ct) => s.ReceivePurchaseOrderAsync(r, ct));
        m.MapPost("/inventory/receive-government", (GovernmentReceiptRequest r, IInventoryService s, CancellationToken ct) => s.ReceiveGovernmentFurnishedAsync(r, ct));
        m.MapPost("/inventory/issue", (IssueToProjectRequest r, IInventoryService s, CancellationToken ct) => s.IssueToProjectAsync(r, ct));
        m.MapPost("/inventory/transfer", (TransferRequest r, IInventoryService s, CancellationToken ct) => s.TransferAsync(r, ct));
        m.MapPost("/inventory/adjust", (AdjustInventoryRequest r, IInventoryService s, CancellationToken ct) => s.AdjustAsync(r, ct));
        m.MapPost("/inventory/count", async (CycleCountRequest r, IInventoryService s, CancellationToken ct) =>
            await s.CycleCountAsync(r, ct) is { } t ? Results.Ok(t) : Results.NoContent());
        m.MapGet("/inventory/trace", (string? lot, string? serial, Guid? partId, IInventoryService s, CancellationToken ct) => s.TraceAsync(lot, serial, partId, ct));

        // BOMs, routings, work orders
        m.MapGet("/boms", (Guid? partId, IManufacturingService s, CancellationToken ct) => s.ListBomsAsync(partId, ct));
        m.MapGet("/boms/{id:guid}", (Guid id, IManufacturingService s, CancellationToken ct) => s.GetBomAsync(id, ct));
        m.MapPost("/boms", (SaveBomRequest r, IManufacturingService s, CancellationToken ct) => s.SaveBomAsync(null, r, ct));
        m.MapPut("/boms/{id:guid}", (Guid id, SaveBomRequest r, IManufacturingService s, CancellationToken ct) => s.SaveBomAsync(id, r, ct));
        m.MapPost("/boms/{id:guid}/activate", (Guid id, IManufacturingService s, CancellationToken ct) => s.ActivateBomAsync(id, ct));
        m.MapPost("/boms/{id:guid}/copy", (Guid id, string revision, IManufacturingService s, CancellationToken ct) => s.CopyBomAsync(id, revision, ct));
        m.MapGet("/parts/{id:guid}/explode", (Guid id, decimal? quantity, DateOnly? asOf, IManufacturingService s, CancellationToken ct) =>
            s.ExplodeAsync(id, quantity ?? 1, asOf ?? DateOnly.FromDateTime(DateTime.UtcNow), ct));
        m.MapGet("/parts/{id:guid}/where-used", (Guid id, IManufacturingService s, CancellationToken ct) => s.WhereUsedAsync(id, ct));
        m.MapGet("/parts/{id:guid}/routings", (Guid id, IManufacturingService s, CancellationToken ct) => s.ListRoutingsAsync(id, ct));
        m.MapPost("/routings", (SaveRoutingRequest r, IManufacturingService s, CancellationToken ct) => s.SaveRoutingAsync(null, r, ct));
        m.MapPut("/routings/{id:guid}", (Guid id, SaveRoutingRequest r, IManufacturingService s, CancellationToken ct) => s.SaveRoutingAsync(id, r, ct));
        m.MapGet("/work-centers", (IManufacturingService s, CancellationToken ct) => s.ListWorkCentersAsync(ct));
        m.MapPost("/work-centers", (SaveWorkCenterRequest r, IManufacturingService s, CancellationToken ct) => s.SaveWorkCenterAsync(null, r, ct));
        m.MapPut("/work-centers/{id:guid}", (Guid id, SaveWorkCenterRequest r, IManufacturingService s, CancellationToken ct) => s.SaveWorkCenterAsync(id, r, ct));
        m.MapGet("/work-orders", (WorkOrderState? status, Guid? partId, Guid? projectId, bool? openOnly, string? search, IManufacturingService s, CancellationToken ct) =>
            s.ListWorkOrdersAsync(new WorkOrderQuery(status, partId, projectId, openOnly ?? false, search), ct));
        m.MapGet("/work-orders/{id:guid}", (Guid id, IManufacturingService s, CancellationToken ct) => s.GetWorkOrderAsync(id, ct));
        m.MapPost("/work-orders", (SaveWorkOrderRequest r, IManufacturingService s, CancellationToken ct) => s.CreateWorkOrderAsync(r, ct));
        m.MapPost("/work-orders/{id:guid}/release", (Guid id, IManufacturingService s, CancellationToken ct) => s.ReleaseAsync(id, ct));
        m.MapPost("/work-orders/{id:guid}/issue", (Guid id, IssueComponentsRequest r, IManufacturingService s, CancellationToken ct) => s.IssueComponentsAsync(id, r, ct));
        m.MapPost("/work-orders/{id:guid}/labor", (Guid id, ReportLaborRequest r, IManufacturingService s, CancellationToken ct) => s.ReportLaborAsync(id, r, ct));
        m.MapPost("/work-orders/{id:guid}/scrap", (Guid id, ScrapRequest r, IManufacturingService s, CancellationToken ct) => s.ScrapAsync(id, r, ct));
        m.MapPost("/work-orders/{id:guid}/complete", (Guid id, CompleteWorkOrderRequest r, IManufacturingService s, CancellationToken ct) => s.CompleteAsync(id, r, ct));
        m.MapPost("/work-orders/{id:guid}/close", (Guid id, CloseWorkOrderRequest r, IManufacturingService s, CancellationToken ct) => s.CloseAsync(id, r, ct));
        m.MapPost("/work-orders/{id:guid}/cancel", (Guid id, CancelWorkOrderRequest r, IManufacturingService s, CancellationToken ct) => s.CancelAsync(id, r, ct));

        // MRP
        m.MapPost("/mrp/run", (RunMrpRequest r, IMrpService s, CancellationToken ct) => s.RunAsync(r, ct));
        m.MapGet("/mrp/latest", async (IMrpService s, CancellationToken ct) => await s.GetLatestAsync(ct) is { } run ? Results.Ok(run) : Results.NoContent());
        m.MapPost("/mrp/firm", (FirmPlannedOrdersRequest r, IMrpService s, CancellationToken ct) => s.FirmAsync(r, ct));

        // Reports
        m.MapGet("/reports/valuation", (IMaterialReportService s, CancellationToken ct) => s.ValuationAsync(ct));
        m.MapGet("/reports/aging", (DateOnly? asOf, IMaterialReportService s, CancellationToken ct) => s.AgingAsync(asOf, ct));
        m.MapGet("/reports/government-furnished", (Guid? projectId, IMaterialReportService s, CancellationToken ct) => s.GovernmentFurnishedAsync(projectId, ct));
        m.MapGet("/reports/wip", (IMaterialReportService s, CancellationToken ct) => s.WipAsync(ct));
        m.MapGet("/reports/projects/{id:guid}", (Guid id, IMaterialReportService s, CancellationToken ct) => s.ProjectMaterialCostAsync(id, ct));
    }

    /// <summary>Wrong state (work order not released, planned order already firmed) → 409.</summary>
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
