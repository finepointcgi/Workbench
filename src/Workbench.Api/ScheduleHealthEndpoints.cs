using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Scheduling;

namespace Workbench.Api;

/// <summary>The DCMA 14-point schedule health check of a plan, its exports, and task float (all Viewer).</summary>
internal static class ScheduleHealthEndpoints
{
    public static void MapScheduleHealthEndpoints(this RouteGroupBuilder sheet)
    {
        sheet.MapPost("/plan/health", (Guid sheetId, ScheduleHealthRequest? request, IScheduleHealthService service, CancellationToken ct) =>
            service.GetAsync(sheetId, request, ct));

        sheet.MapGet("/plan/health", (Guid sheetId, DateOnly? statusDate, Guid? baseline, IScheduleHealthService service, CancellationToken ct) =>
            service.GetAsync(sheetId, new ScheduleHealthRequest(statusDate, baseline), ct));

        sheet.MapGet("/plan/float", (Guid sheetId, IScheduleHealthService service, CancellationToken ct) => service.GetFloatAsync(sheetId, ct));

        sheet.MapPost("/plan/health/export/pdf", (Guid sheetId, ScheduleHealthRequest? request, IScheduleHealthService service, CancellationToken ct) =>
            service.ExportPdfAsync(sheetId, request, ct));

        sheet.MapPost("/plan/health/export/xlsx", (Guid sheetId, ScheduleHealthRequest? request, IScheduleHealthService service, CancellationToken ct) =>
            service.ExportExcelAsync(sheetId, request, ct));

        sheet.MapGet("/schedule-health.pdf", async (Guid sheetId, DateOnly? statusDate, Guid? baseline, IScheduleHealthService service, CancellationToken ct) =>
        {
            var file = await service.ExportPdfAsync(sheetId, new ScheduleHealthRequest(statusDate, baseline), ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        sheet.MapGet("/schedule-health.xlsx", async (Guid sheetId, DateOnly? statusDate, Guid? baseline, IScheduleHealthService service, CancellationToken ct) =>
        {
            var file = await service.ExportExcelAsync(sheetId, new ScheduleHealthRequest(statusDate, baseline), ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }
}
