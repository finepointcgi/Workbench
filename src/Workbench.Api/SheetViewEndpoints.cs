using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

internal static class SheetViewEndpoints
{
    public static void MapSheetViewEndpoints(this RouteGroupBuilder v1)
    {
        var views = v1.MapGroup("/sheets/{sheetId:guid}/views").WithTags("Sheet views");

        views.MapGet("/", (Guid sheetId, ISheetViewService service, CancellationToken ct) => service.ListAsync(sheetId, ct));

        views.MapPost("/", async (Guid sheetId, SaveSheetViewRequest request, ISheetViewService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/views/{created.Id}", created);
        });

        views.MapPut("/{viewId:guid}", (Guid sheetId, Guid viewId, SaveSheetViewRequest request, ISheetViewService service, CancellationToken ct) =>
            service.UpdateAsync(sheetId, viewId, request, ct));

        views.MapDelete("/{viewId:guid}", async (Guid sheetId, Guid viewId, ISheetViewService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(sheetId, viewId, ct);
            return Results.NoContent();
        });

        views.MapPut("/my-default", async (Guid sheetId, SetDefaultSheetViewRequest request, ISheetViewService service, CancellationToken ct) =>
        {
            await service.SetMyDefaultAsync(sheetId, request, ct);
            return Results.NoContent();
        });

        views.MapPut("/sheet-default", async (Guid sheetId, SetDefaultSheetViewRequest request, ISheetViewService service, CancellationToken ct) =>
        {
            await service.SetSheetDefaultAsync(sheetId, request, ct);
            return Results.NoContent();
        });

        var sheet = v1.MapGroup("/sheets/{sheetId:guid}").WithTags("Sheet views");

        // The file as JSON (the desktop app and the grid send the view on screen)...
        sheet.MapPost("/export", (Guid sheetId, SheetExportRequest request, ISheetViewService service, CancellationToken ct) =>
            service.ExportAsync(sheetId, request, ct));

        // ...or the whole sheet as a download: GET /api/v1/sheets/{id}/export?format=pdf (or xlsx).
        sheet.MapGet("/export", async (Guid sheetId, SheetExportFormat? format, ISheetViewService service, CancellationToken ct) =>
        {
            var file = await service.ExportAsync(sheetId, new SheetExportRequest(format ?? SheetExportFormat.Xlsx), ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }
}
