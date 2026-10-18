using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Reports;

namespace Workbench.Api;

internal static class ReportEndpoints
{
    public static void MapReportEndpoints(this RouteGroupBuilder v1)
    {
        var reports = v1.MapGroup("/reports").WithTags("Reports");

        reports.MapGet("/standard", (IStandardReportService s, CancellationToken ct) => s.ListAsync(ct));
        reports.MapPost("/standard/{key}/run", (string key, ReportParameters p, IStandardReportService s, CancellationToken ct) => s.RunAsync(key, p, ct));
        reports.MapPost("/standard/{key}/drill", (string key, DrillRequest r, IStandardReportService s, CancellationToken ct) => s.DrillAsync(key, r.Parameters, r.DrillKey, ct));
        // JSON-wrapped (desktop client saves the file itself); the file routes serve it to browsers.
        reports.MapPost("/standard/{key}/export", (string key, ReportFileFormat format, ReportParameters p, IStandardReportService s, CancellationToken ct) => s.ExportAsync(key, p, format, ct));
        reports.MapPost("/standard/{key}/file", async (string key, ReportFileFormat format, ReportParameters p, IStandardReportService s, CancellationToken ct) => File(await s.ExportAsync(key, p, format, ct)));

        reports.MapGet("/sources", (IReportWriterService s, CancellationToken ct) => s.ListSourcesAsync(ct));
        reports.MapPost("/writer/run", (ReportDefinition d, IReportWriterService s, CancellationToken ct) => s.RunAsync(d, ct));
        reports.MapPost("/writer/export", (ReportFileFormat format, ReportDefinition d, IReportWriterService s, CancellationToken ct) => s.ExportAsync(d, format, ct));
        reports.MapPost("/writer/file", async (ReportFileFormat format, ReportDefinition d, IReportWriterService s, CancellationToken ct) => File(await s.ExportAsync(d, format, ct)));

        reports.MapGet("/saved", (ISavedReportService s, CancellationToken ct) => s.ListAsync(ct));
        reports.MapGet("/saved/{id:guid}", (Guid id, ISavedReportService s, CancellationToken ct) => s.GetAsync(id, ct));
        reports.MapPost("/saved", async (SaveReportRequest r, ISavedReportService s, CancellationToken ct) =>
        {
            var saved = await s.CreateAsync(r, ct);
            return Results.Created($"/api/v1/reports/saved/{saved.Id}", saved);
        });
        reports.MapPut("/saved/{id:guid}", (Guid id, SaveReportRequest r, ISavedReportService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        reports.MapDelete("/saved/{id:guid}", async (Guid id, ISavedReportService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        reports.MapPost("/saved/{id:guid}/run", (Guid id, ISavedReportService s, CancellationToken ct) => s.RunAsync(id, ct));
        reports.MapGet("/saved/{id:guid}/export", (Guid id, ReportFileFormat format, ISavedReportService s, CancellationToken ct) => s.ExportAsync(id, format, ct));
        reports.MapGet("/saved/{id:guid}/file", async (Guid id, ReportFileFormat format, ISavedReportService s, CancellationToken ct) => File(await s.ExportAsync(id, format, ct)));
        reports.MapPost("/saved/{id:guid}/schedules", (Guid id, SaveReportScheduleRequest r, ISavedReportService s, CancellationToken ct) => s.AddScheduleAsync(id, r, ct));
        reports.MapPut("/schedules/{id:guid}", (Guid id, SaveReportScheduleRequest r, ISavedReportService s, CancellationToken ct) => s.UpdateScheduleAsync(id, r, ct));
        reports.MapDelete("/schedules/{id:guid}", async (Guid id, ISavedReportService s, CancellationToken ct) =>
        {
            await s.DeleteScheduleAsync(id, ct);
            return Results.NoContent();
        });
        reports.MapPost("/schedules/{id:guid}/send", (Guid id, ISavedReportService s, CancellationToken ct) => s.SendNowAsync(id, ct));
    }

    private static IResult File(ReportFileDto file) => Results.File(file.Content, file.ContentType, file.FileName);
}

/// <summary>Body of a standard report drill-down.</summary>
public sealed record DrillRequest(ReportParameters Parameters, string DrillKey);
