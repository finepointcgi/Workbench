using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.AccountingExport;

namespace Workbench.Api;

internal static class AccountingExportEndpoints
{
    public static void MapAccountingExportEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/accounting-export").WithTags("Accounting export").AddEndpointFilter(MapConflicts);
        g.MapGet("/formats", (IAccountingExportService s, CancellationToken ct) => s.ListFormatsAsync(ct));
        g.MapPost("/preview", (AccountingFileRequest r, IAccountingExportService s, CancellationToken ct) => s.PreviewAsync(r, ct));
        g.MapPost("/export", (AccountingFileRequest r, IAccountingExportService s, CancellationToken ct) => s.ExportAsync(r, ct));
        g.MapGet("/runs", (AccountingExportKind? kind, int? limit, IAccountingExportService s, CancellationToken ct) => s.ListRunsAsync(kind, limit ?? 100, ct));
        g.MapGet("/runs/{runId:guid}/file", (Guid runId, IAccountingExportService s, CancellationToken ct) => s.DownloadRunAsync(runId, ct));
        g.MapGet("/mappings/{target}", (AccountingTarget target, IAccountingMappingService s, CancellationToken ct) => s.GetAsync(target, ct));
        g.MapPut("/mappings/{target}", (AccountingTarget target, SaveAccountingMappingsRequest r, IAccountingMappingService s, CancellationToken ct) => s.SaveAsync(target, r, ct));
    }

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
