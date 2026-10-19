using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Assets;

namespace Workbench.Api;

internal static class AssetEndpoints
{
    public static void MapAssetEndpoints(this RouteGroupBuilder v1)
    {
        var a = v1.MapGroup("/assets").WithTags("Fixed assets").AddEndpointFilter(MapConflicts);

        a.MapGet("", (AssetState? status, Guid? categoryId, string? search, bool? governmentPropertyOnly, IAssetService s, CancellationToken ct) =>
            s.ListAsync(new AssetQuery(status, categoryId, search, governmentPropertyOnly ?? false), ct));
        a.MapGet("/settings", (IAssetService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        a.MapGet("/categories", (IAssetService s, CancellationToken ct) => s.ListCategoriesAsync(ct));
        a.MapPost("/categories", (SaveAssetCategoryRequest r, IAssetService s, CancellationToken ct) => s.SaveCategoryAsync(null, r, ct));
        a.MapPut("/categories/{id:guid}", (Guid id, SaveAssetCategoryRequest r, IAssetService s, CancellationToken ct) => s.SaveCategoryAsync(id, r, ct));
        a.MapDelete("/categories/{id:guid}", async (Guid id, IAssetService s, CancellationToken ct) =>
        {
            await s.DeleteCategoryAsync(id, ct);
            return Results.NoContent();
        });
        a.MapGet("/capitalizable", (IAssetService s, CancellationToken ct) => s.ListCapitalizableBillLinesAsync(ct));
        a.MapPost("/capitalize", (CapitalizeBillLineRequest r, IAssetService s, CancellationToken ct) => s.CapitalizeAsync(r, ct));
        a.MapGet("/{id:guid}", (Guid id, IAssetService s, CancellationToken ct) => s.GetAsync(id, ct));
        a.MapPost("", (SaveAssetRequest r, IAssetService s, CancellationToken ct) => s.CreateAsync(r, ct));
        a.MapPut("/{id:guid}", (Guid id, SaveAssetRequest r, IAssetService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        a.MapPost("/{id:guid}/transfer", (Guid id, TransferAssetRequest r, IAssetService s, CancellationToken ct) => s.TransferAsync(id, r, ct));
        a.MapPost("/{id:guid}/dispose", (Guid id, DisposeAssetRequest r, IAssetService s, CancellationToken ct) => s.DisposeAsync(id, r, ct));

        // Depreciation
        a.MapGet("/depreciation/runs", (IDepreciationService s, CancellationToken ct) => s.ListRunsAsync(ct));
        a.MapGet("/depreciation/{periodId:guid}", (Guid periodId, IDepreciationService s, CancellationToken ct) => s.PreviewAsync(periodId, ct));
        a.MapPost("/depreciation/{periodId:guid}/run", async (Guid periodId, IDepreciationService s, CancellationToken ct) =>
        {
            var run = await s.RunAsync(periodId, ct);
            return run is null ? Results.NoContent() : Results.Ok(run);
        });

        // Reports
        a.MapGet("/reports/register", (AssetState? status, Guid? categoryId, string? search, bool? governmentPropertyOnly, IAssetReportService s, CancellationToken ct) =>
            s.GetRegisterAsync(new AssetQuery(status, categoryId, search, governmentPropertyOnly ?? false), ct));
        a.MapGet("/reports/register/export", (IAssetReportService s, CancellationToken ct) => s.ExportRegisterCsvAsync(ct));
        a.MapGet("/reports/schedule/{id:guid}", (Guid id, DepreciationBook? book, IAssetReportService s, CancellationToken ct) => s.GetScheduleAsync(id, book ?? DepreciationBook.Book, ct));
        a.MapGet("/reports/schedule/{id:guid}/export", (Guid id, DepreciationBook? book, IAssetReportService s, CancellationToken ct) => s.ExportScheduleCsvAsync(id, book ?? DepreciationBook.Book, ct));
        a.MapGet("/reports/roll-forward", (DateOnly from, DateOnly to, IAssetReportService s, CancellationToken ct) => s.GetRollForwardAsync(from, to, ct));
        a.MapGet("/reports/roll-forward/export", (DateOnly from, DateOnly to, IAssetReportService s, CancellationToken ct) => s.ExportRollForwardCsvAsync(from, to, ct));
        a.MapGet("/reports/government-property", (IAssetReportService s, CancellationToken ct) => s.GetGovernmentPropertyAsync(ct));
        a.MapGet("/reports/government-property/export", (IAssetReportService s, CancellationToken ct) => s.ExportGovernmentPropertyCsvAsync(ct));
    }

    /// <summary>Wrong state (already disposed, already capitalized) → 409.</summary>
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
