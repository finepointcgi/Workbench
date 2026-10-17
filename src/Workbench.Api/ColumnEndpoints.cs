using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

internal static class ColumnEndpoints
{
    public static void MapColumnEndpoints(this RouteGroupBuilder v1)
    {
        var columns = v1.MapGroup("/sheets/{sheetId:guid}/columns").WithTags("Columns");

        columns.MapPost("/", async (Guid sheetId, CreateColumnRequest request, IColumnService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/columns/{created.Id}", created);
        });

        columns.MapPut("/{columnId:guid}", (Guid sheetId, Guid columnId, UpdateColumnRequest request, IColumnService service, CancellationToken ct) =>
            service.UpdateAsync(sheetId, columnId, request, ct));

        columns.MapDelete("/{columnId:guid}", async (Guid sheetId, Guid columnId, IColumnService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, columnId, ct);
            return Results.NoContent();
        });
    }
}
