using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Billing;

namespace Workbench.Api;

internal static class CommercialBillingEndpoints
{
    public static void MapCommercialBillingEndpoints(this RouteGroupBuilder v1)
    {
        var b = v1.MapGroup("/billing").WithTags("Commercial billing").AddEndpointFilter(MapConflicts);

        // Agreements without CLINs: the billing plan, fixed-fee milestones, and retainer/recurring periods that are due.
        b.MapGet("/agreements", (IAgreementBillingService s, CancellationToken ct) => s.ListPlansAsync(ct));
        b.MapGet("/contracts/{id:guid}/agreement", async (Guid id, IAgreementBillingService s, CancellationToken ct) =>
            await s.GetPlanAsync(id, ct) is { } plan ? Results.Ok(plan) : Results.NoContent());
        b.MapPut("/contracts/{id:guid}/agreement", (Guid id, SaveAgreementBillingPlanRequest r, IAgreementBillingService s, CancellationToken ct) => s.SavePlanAsync(id, r, ct));
        b.MapDelete("/contracts/{id:guid}/agreement", async (Guid id, IAgreementBillingService s, CancellationToken ct) =>
        {
            await s.RemovePlanAsync(id, ct);
            return Results.NoContent();
        });
        b.MapGet("/contracts/{id:guid}/agreement/milestones", (Guid id, IAgreementBillingService s, CancellationToken ct) => s.ListMilestonesAsync(id, ct));
        b.MapPost("/contracts/{id:guid}/agreement/milestones", (Guid id, SaveAgreementMilestoneRequest r, IAgreementBillingService s, CancellationToken ct) => s.CreateMilestoneAsync(id, r, ct));
        b.MapPut("/agreement-milestones/{id:guid}", (Guid id, SaveAgreementMilestoneRequest r, IAgreementBillingService s, CancellationToken ct) => s.UpdateMilestoneAsync(id, r, ct));
        b.MapPost("/agreement-milestones/{id:guid}/complete", (Guid id, CompleteAgreementMilestoneRequest r, IAgreementBillingService s, CancellationToken ct) => s.CompleteMilestoneAsync(id, r, ct));
        b.MapPost("/agreement-milestones/{id:guid}/reopen", (Guid id, IAgreementBillingService s, CancellationToken ct) => s.ReopenMilestoneAsync(id, ct));
        b.MapDelete("/agreement-milestones/{id:guid}", async (Guid id, IAgreementBillingService s, CancellationToken ct) =>
        {
            await s.DeleteMilestoneAsync(id, ct);
            return Results.NoContent();
        });
        b.MapGet("/agreements/due", (DateOnly? asOf, IAgreementBillingService s, CancellationToken ct) => s.ListDuePeriodsAsync(asOf, ct));
        b.MapPost("/agreements/due/generate", (DateOnly? asOf, Guid? contractId, IAgreementBillingService s, CancellationToken ct) => s.GenerateDueDraftsAsync(asOf, contractId, ct));

        // Customer credits, write-offs and reminders.
        b.MapGet("/receivables/aging", (DateOnly? asOf, IReceivablesService s, CancellationToken ct) => s.GetAgingAsync(asOf, ct));
        b.MapGet("/receivables/credits", (Guid? customerId, IReceivablesService s, CancellationToken ct) => s.ListCreditsAsync(customerId, ct));
        b.MapPost("/payments/{id:guid}/apply-credit", (Guid id, ApplyCreditRequest r, IReceivablesService s, CancellationToken ct) => s.ApplyCreditAsync(id, r, ct));
        b.MapGet("/write-offs", (Guid? invoiceId, Guid? customerId, IReceivablesService s, CancellationToken ct) => s.ListWriteOffsAsync(invoiceId, customerId, ct));
        b.MapPost("/invoices/{id:guid}/write-off", (Guid id, WriteOffInvoiceRequest r, IReceivablesService s, CancellationToken ct) => s.WriteOffAsync(id, r, ct));
        b.MapPost("/write-offs/{id:guid}/reverse", (Guid id, ReverseWriteOffRequest r, IReceivablesService s, CancellationToken ct) => s.ReverseWriteOffAsync(id, r, ct));
        b.MapPost("/receivables/reminders", (IReceivablesService s, CancellationToken ct) => s.SendPaymentRemindersAsync(ct));

        // The organization's invoice terms, remittance details and default layout.
        b.MapGet("/settings", (IBillingSettingsService s, CancellationToken ct) => s.GetAsync(ct));
        b.MapPut("/settings", (SaveBillingSettingsRequest r, IBillingSettingsService s, CancellationToken ct) => s.SaveAsync(r, ct));
    }

    /// <summary>Wrong state (invoice already billed, write-off already reversed) → 409.</summary>
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
