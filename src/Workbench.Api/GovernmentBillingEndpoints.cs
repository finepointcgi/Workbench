using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Billing;

namespace Workbench.Api;

internal static class GovernmentBillingEndpoints
{
    public static void MapGovernmentBillingEndpoints(this RouteGroupBuilder v1)
    {
        var b = v1.MapGroup("/billing").WithTags("Government billing").AddEndpointFilter(MapConflicts);

        b.MapGet("/contracts/{id:guid}/government", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.GetSettingsAsync(id, ct));
        b.MapPut("/contracts/{id:guid}/government", (Guid id, SaveGovernmentBillingSettingsRequest r, IGovernmentBillingService s, CancellationToken ct) => s.SaveSettingsAsync(id, r, ct));
        b.MapGet("/contracts/{id:guid}/financing", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.GetFinancingAsync(id, ct));
        b.MapGet("/invoices/{id:guid}/voucher", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.GetVoucherAsync(id, ct));
        b.MapGet("/invoices/{id:guid}/piee", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.GetPieeAsync(id, ct));
        b.MapPost("/invoices/{id:guid}/piee/submit", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.SubmitPieeAsync(id, ct));
        b.MapPost("/invoices/{id:guid}/piee/status", (Guid id, SetPieeStatusRequest r, IGovernmentBillingService s, CancellationToken ct) => s.SetPieeStatusAsync(id, r, ct));

        // JSON-wrapped for the desktop client; the .xml route serves the file to browsers.
        b.MapGet("/invoices/{id:guid}/piee/file", (Guid id, IGovernmentBillingService s, CancellationToken ct) => s.ExportPieeAsync(id, ct));
        b.MapGet("/invoices/{id:guid}/piee.xml", async (Guid id, IGovernmentBillingService s, CancellationToken ct) =>
        {
            var file = await s.ExportPieeAsync(id, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
        });

        b.MapGet("/contracts/{id:guid}/progress-payments", (Guid id, IProgressPaymentService s, CancellationToken ct) => s.ListAsync(id, ct));
        b.MapGet("/progress-payments/{id:guid}", (Guid id, IProgressPaymentService s, CancellationToken ct) => s.GetAsync(id, ct));
        b.MapPost("/progress-payments/preview", (ProgressPaymentPreviewRequest r, IProgressPaymentService s, CancellationToken ct) => s.PreviewAsync(r, ct));
        b.MapPost("/progress-payments", (ProgressPaymentPreviewRequest r, IProgressPaymentService s, CancellationToken ct) => s.CreateAsync(r, ct));
        b.MapDelete("/progress-payments/{id:guid}", async (Guid id, IProgressPaymentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        b.MapGet("/contracts/{id:guid}/performance-events", (Guid id, IPerformanceBasedPaymentService s, CancellationToken ct) => s.ListAsync(id, ct));
        b.MapPost("/contracts/{id:guid}/performance-events", (Guid id, SavePerformanceEventRequest r, IPerformanceBasedPaymentService s, CancellationToken ct) => s.CreateAsync(id, r, ct));
        b.MapPut("/performance-events/{id:guid}", (Guid id, SavePerformanceEventRequest r, IPerformanceBasedPaymentService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        b.MapPost("/performance-events/{id:guid}/complete", (Guid id, CompletePerformanceEventRequest r, IPerformanceBasedPaymentService s, CancellationToken ct) => s.CompleteAsync(id, r, ct));
        b.MapPost("/performance-events/{id:guid}/reopen", (Guid id, IPerformanceBasedPaymentService s, CancellationToken ct) => s.ReopenAsync(id, ct));
        b.MapDelete("/performance-events/{id:guid}", async (Guid id, IPerformanceBasedPaymentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>Wrong state (not approved, missing PIEE fields, already liquidated) → 409.</summary>
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
