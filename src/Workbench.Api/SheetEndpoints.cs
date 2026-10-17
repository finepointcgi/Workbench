using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

internal static class SheetEndpoints
{
    public static void MapSheetEndpoints(this RouteGroupBuilder v1)
    {
        var sheets = v1.MapGroup("/sheets").WithTags("Sheets");

        sheets.MapGet("/", (ISheetService service, CancellationToken ct) => service.ListAsync(ct));

        sheets.MapGet("/{sheetId:guid}", async (Guid sheetId, ISheetService service, CancellationToken ct) =>
            await service.GetAsync(sheetId, ct) is { } sheet ? Results.Ok(sheet) : Results.NotFound());

        sheets.MapPost("/", async (CreateSheetRequest request, ISheetService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/sheets/{created.Id}", created);
        });
    }
}
