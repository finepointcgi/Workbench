using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Compliance;

namespace Workbench.Api;

internal static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this RouteGroupBuilder v1)
    {
        var c = v1.MapGroup("/compliance").WithTags("Compliance").AddEndpointFilter(MapConflicts);

        // Purchasing system review
        c.MapGet("/cpsr/settings", (ICpsrService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        c.MapGet("/cpsr/readiness", (DateOnly? from, DateOnly? to, int? sampleSize, int? seed, ICpsrService s, CancellationToken ct) =>
            s.GetReadinessAsync(new CpsrReviewRequest(from, to, sampleSize ?? 25, seed ?? 0), ct));
        c.MapGet("/cpsr/export", (DateOnly? from, DateOnly? to, int? sampleSize, int? seed, ICpsrService s, CancellationToken ct) =>
            s.ExportReviewAsync(new CpsrReviewRequest(from, to, sampleSize ?? 25, seed ?? 0), ct));
        c.MapGet("/cpsr/files/{kind}/{id:guid}", (ProcurementDocumentKind kind, Guid id, ICpsrService s, CancellationToken ct) => s.GetFileAsync(kind, id, ct));
        c.MapPut("/cpsr/files/{kind}/{id:guid}", (ProcurementDocumentKind kind, Guid id, SaveProcurementFileRequest r, ICpsrService s, CancellationToken ct) => s.SaveFileAsync(kind, id, r, ct));
        c.MapPost("/cpsr/files/{kind}/{id:guid}/approve", (ProcurementDocumentKind kind, Guid id, ApproveProcurementFileRequest r, ICpsrService s, CancellationToken ct) =>
            s.ApproveAsync(kind, id, r, ct));

        // CAS disclosure statements
        c.MapGet("/disclosure", (DisclosureFormType? form, ICasDisclosureService s, CancellationToken ct) => s.ListVersionsAsync(form, ct));
        c.MapPost("/disclosure", (CreateDisclosureVersionRequest r, ICasDisclosureService s, CancellationToken ct) => s.CreateVersionAsync(r, ct));
        c.MapGet("/disclosure/consistency", (Guid? statementId, ICasDisclosureService s, CancellationToken ct) => s.CheckConsistencyAsync(statementId, ct));
        c.MapGet("/disclosure/{id:guid}", (Guid id, ICasDisclosureService s, CancellationToken ct) => s.GetAsync(id, ct));
        c.MapPut("/disclosure/{id:guid}/answers", (Guid id, List<SaveDisclosureAnswerRequest> r, ICasDisclosureService s, CancellationToken ct) => s.SaveAnswersAsync(id, r, ct));
        c.MapPost("/disclosure/{id:guid}/fill", (Guid id, ICasDisclosureService s, CancellationToken ct) => s.FillFromConfigurationAsync(id, ct));
        c.MapPost("/disclosure/{id:guid}/effective", (Guid id, MakeDisclosureEffectiveRequest r, ICasDisclosureService s, CancellationToken ct) => s.MakeEffectiveAsync(id, r, ct));
        c.MapGet("/disclosure/{id:guid}/export", (Guid id, ICasDisclosureService s, CancellationToken ct) => s.ExportAsync(id, ct));
        c.MapDelete("/disclosure/{id:guid}", async (Guid id, ICasDisclosureService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>Wrong state (an effective statement edited, a level approved twice) → 409.</summary>
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
