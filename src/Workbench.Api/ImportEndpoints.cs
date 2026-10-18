using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Imports;

namespace Workbench.Api;

internal static class ImportEndpoints
{
    public static void MapImportEndpoints(this RouteGroupBuilder v1)
    {
        var imports = v1.MapGroup("/imports").WithTags("Imports");
        imports.MapGet("/kinds", (IImportService s, CancellationToken ct) => s.ListKindsAsync(ct));
        imports.MapGet("", (IImportService s, CancellationToken ct) => s.ListJobsAsync(ct));
        imports.MapGet("/{id:guid}", async (Guid id, IImportService s, CancellationToken ct) =>
            await s.GetJobAsync(id, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        imports.MapPost("", (UploadImportRequest r, IImportService s, CancellationToken ct) => s.UploadAsync(r, ct));
        imports.MapPost("/smartsheet/connect", (ConnectSmartsheetRequest r, IImportService s, CancellationToken ct) => s.ConnectSmartsheetAsync(r, ct));
        imports.MapPost("/{id:guid}/smartsheet/sheets", (Guid id, SelectSmartsheetSheetsRequest r, IImportService s, CancellationToken ct) => s.SelectSmartsheetSheetsAsync(id, r, ct));
        imports.MapPut("/{id:guid}/mapping", (Guid id, SaveImportMappingRequest r, IImportService s, CancellationToken ct) => s.SaveMappingAsync(id, r, ct));
        imports.MapPost("/{id:guid}/validate", (Guid id, IImportService s, CancellationToken ct) => s.ValidateAsync(id, ct));
        imports.MapPost("/{id:guid}/dry-run", (Guid id, IImportService s, CancellationToken ct) => s.DryRunAsync(id, ct));
        imports.MapPost("/{id:guid}/import", (Guid id, IImportService s, CancellationToken ct) => s.ImportAsync(id, ct));
        imports.MapGet("/{id:guid}/records", (Guid id, IImportService s, CancellationToken ct) => s.ListRecordsAsync(id, ct));
        imports.MapGet("/{id:guid}/rollback", (Guid id, IImportService s, CancellationToken ct) => s.CheckRollbackAsync(id, ct));
        imports.MapPost("/{id:guid}/rollback", (Guid id, IImportService s, CancellationToken ct) => s.RollBackAsync(id, ct));
        imports.MapDelete("/{id:guid}", async (Guid id, IImportService s, CancellationToken ct) =>
        {
            await s.DeleteJobAsync(id, ct);
            return Results.NoContent();
        });
    }
}
