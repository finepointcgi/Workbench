using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Materials;

namespace Workbench.Api;

/// <summary>Kitting, backflushing, landed cost, physical inventory and capacity scheduling, under /materials.</summary>
internal static class ShopFloorEndpoints
{
    public static void MapShopFloorEndpoints(this RouteGroupBuilder v1)
    {
        var m = v1.MapGroup("/materials").WithTags("Materials").AddEndpointFilter(MapConflicts);

        // Kits
        m.MapGet("/kits", (bool? openOnly, Guid? workOrderId, Guid? projectId, IKittingService s, CancellationToken ct) =>
            s.ListAsync(new KitQuery(openOnly ?? true, workOrderId, projectId), ct));
        m.MapGet("/kits/{id:guid}", (Guid id, IKittingService s, CancellationToken ct) => s.GetAsync(id, ct));
        m.MapPost("/kits/work-order", (CreateWorkOrderKitRequest r, IKittingService s, CancellationToken ct) => s.CreateForWorkOrderAsync(r, ct));
        m.MapPost("/kits/project", (CreateProjectKitRequest r, IKittingService s, CancellationToken ct) => s.CreateForProjectAsync(r, ct));
        m.MapPost("/kits/{id:guid}/stage", (Guid id, StageKitRequest r, IKittingService s, CancellationToken ct) => s.StageAsync(id, r, ct));
        m.MapPost("/kits/{id:guid}/issue", (Guid id, IssueKitRequest r, IKittingService s, CancellationToken ct) => s.IssueAsync(id, r, ct));
        m.MapPost("/kits/{id:guid}/cancel", (Guid id, CancelKitRequest r, IKittingService s, CancellationToken ct) => s.CancelAsync(id, r, ct));

        // Backflush
        m.MapGet("/backflush/settings", (IBackflushService s, CancellationToken ct) => s.ListSettingsAsync(ct));
        m.MapGet("/backflush/components", (Guid? bomId, IBackflushService s, CancellationToken ct) => s.ListComponentsAsync(bomId, ct));
        m.MapPost("/backflush/settings", (SaveBackflushSettingRequest r, IBackflushService s, CancellationToken ct) => s.SaveSettingAsync(r, ct));
        m.MapDelete("/backflush/settings/{id:guid}", async (Guid id, IBackflushService s, CancellationToken ct) =>
        {
            await s.DeleteSettingAsync(id, ct);
            return Results.NoContent();
        });
        m.MapGet("/backflush/shortages", (Guid? workOrderId, bool? openOnly, IBackflushService s, CancellationToken ct) => s.ListShortagesAsync(workOrderId, openOnly ?? true, ct));
        m.MapPost("/work-orders/{id:guid}/backflush", (Guid id, DateOnly date, IBackflushService s, CancellationToken ct) => s.RetryAsync(id, date, ct));

        // Landed cost
        m.MapGet("/landed-cost", (ILandedCostService s, CancellationToken ct) => s.ListAsync(ct));
        m.MapGet("/landed-cost/options", (ILandedCostService s, CancellationToken ct) => s.GetOptionsAsync(ct));
        m.MapGet("/landed-cost/receipt-lines", (DateOnly? from, string? search, ILandedCostService s, CancellationToken ct) => s.ListReceiptLinesAsync(from, search, ct));
        m.MapGet("/landed-cost/bill-lines", (ILandedCostService s, CancellationToken ct) => s.ListBillLinesAsync(ct));
        m.MapGet("/landed-cost/receipts", (DateOnly? from, ILandedCostService s, CancellationToken ct) => s.ListReceiptsAsync(from, ct));
        m.MapGet("/landed-cost/receipts/{id:guid}", (Guid id, ILandedCostService s, CancellationToken ct) => s.GetReceiptAsync(id, ct));
        m.MapGet("/landed-cost/{id:guid}", (Guid id, ILandedCostService s, CancellationToken ct) => s.GetAsync(id, ct));
        m.MapPost("/landed-cost", (SaveLandedChargeRequest r, ILandedCostService s, CancellationToken ct) => s.CreateAsync(r, ct));
        m.MapPut("/landed-cost/{id:guid}", (Guid id, SaveLandedChargeRequest r, ILandedCostService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        m.MapPost("/landed-cost/{id:guid}/post", (Guid id, ILandedCostService s, CancellationToken ct) => s.PostAsync(id, ct));
        m.MapPost("/landed-cost/{id:guid}/cancel", (Guid id, ILandedCostService s, CancellationToken ct) => s.CancelAsync(id, ct));

        // Physical inventory
        m.MapGet("/physical-inventory", (bool? openOnly, IPhysicalInventoryService s, CancellationToken ct) => s.ListAsync(openOnly ?? false, ct));
        m.MapGet("/physical-inventory/{id:guid}", (Guid id, IPhysicalInventoryService s, CancellationToken ct) => s.GetAsync(id, ct));
        m.MapPost("/physical-inventory", (CreatePhysicalInventoryRequest r, IPhysicalInventoryService s, CancellationToken ct) => s.CreateAsync(r, ct));
        m.MapPost("/physical-inventory/{id:guid}/counts", (Guid id, EnterCountRequest r, IPhysicalInventoryService s, CancellationToken ct) => s.EnterCountAsync(id, r, ct));
        m.MapPost("/physical-inventory/{id:guid}/tags", (Guid id, AddCountTagRequest r, IPhysicalInventoryService s, CancellationToken ct) => s.AddTagAsync(id, r, ct));
        m.MapPost("/physical-inventory/{id:guid}/tags/{tag:int}/void", (Guid id, int tag, IPhysicalInventoryService s, CancellationToken ct) => s.VoidTagAsync(id, tag, ct));
        m.MapGet("/physical-inventory/{id:guid}/count-sheet", (Guid id, IPhysicalInventoryService s, CancellationToken ct) => s.ExportCountSheetAsync(id, ct));
        m.MapPost("/physical-inventory/{id:guid}/post", (Guid id, IPhysicalInventoryService s, CancellationToken ct) => s.PostAsync(id, ct));
        m.MapPost("/physical-inventory/{id:guid}/cancel", (Guid id, CancelPhysicalInventoryBody r, IPhysicalInventoryService s, CancellationToken ct) => s.CancelAsync(id, r.Reason, ct));

        // Capacity scheduling
        m.MapGet("/capacity/calendars", (ICapacitySchedulingService s, CancellationToken ct) => s.ListCalendarsAsync(ct));
        m.MapPut("/capacity/calendars/{workCenterId:guid}", (Guid workCenterId, SaveWorkCenterCalendarRequest r, ICapacitySchedulingService s, CancellationToken ct) =>
            s.SaveCalendarAsync(workCenterId, r, ct));
        m.MapPost("/capacity/calendars/{workCenterId:guid}/exceptions", (Guid workCenterId, SaveCapacityExceptionRequest r, ICapacitySchedulingService s, CancellationToken ct) =>
            s.SaveExceptionAsync(workCenterId, r, ct));
        m.MapDelete("/capacity/exceptions/{id:guid}", (Guid id, ICapacitySchedulingService s, CancellationToken ct) => s.DeleteExceptionAsync(id, ct));
        m.MapGet("/capacity/schedule", (DateOnly? from, int? weeks, ICapacitySchedulingService s, CancellationToken ct) => s.GetAsync(from, weeks ?? 8, ct));
        m.MapPost("/capacity/schedule", (RunScheduleRequest r, ICapacitySchedulingService s, CancellationToken ct) => s.RunAsync(r, ct));
        m.MapPost("/capacity/schedule/{id:guid}/move", (Guid id, MoveOperationRequest r, ICapacitySchedulingService s, CancellationToken ct) => s.MoveAsync(id, r, ct));
        m.MapPost("/capacity/work-centers/{workCenterId:guid}/queue", (Guid workCenterId, ResequenceRequest r, ICapacitySchedulingService s, CancellationToken ct) =>
            s.ResequenceAsync(workCenterId, r, ct));
    }

    internal sealed record CancelPhysicalInventoryBody(string Reason);

    /// <summary>Wrong state (kit already issued, count posted) → 409.</summary>
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
