using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

internal static class ItemEndpoints
{
    public static void MapItemEndpoints(this RouteGroupBuilder v1)
    {
        var items = v1.MapGroup("/sheets/{sheetId:guid}/items").WithTags("Items");

        items.MapGet("/", (Guid sheetId, IItemService service, CancellationToken ct) => service.ListAsync(sheetId, ct));

        items.MapPost("/", async (Guid sheetId, CreateItemRequest request, IItemService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/items/{created.Id}", created);
        });

        items.MapPut("/{itemId:guid}", (Guid sheetId, Guid itemId, UpdateItemRequest request, IItemService service, CancellationToken ct) =>
            service.UpdateAsync(sheetId, itemId, request, ct));

        items.MapPost("/{itemId:guid}/move", (Guid sheetId, Guid itemId, MoveItemRequest request, IItemService service, CancellationToken ct) =>
            service.MoveAsync(sheetId, itemId, request, ct));

        items.MapDelete("/{itemId:guid}", async (Guid sheetId, Guid itemId, IItemService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, itemId, ct);
            return Results.NoContent();
        });
    }
}
