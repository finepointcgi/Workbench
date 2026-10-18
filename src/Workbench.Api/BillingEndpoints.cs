using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Billing;

namespace Workbench.Api;

internal static class BillingEndpoints
{
    public static void MapBillingEndpoints(this RouteGroupBuilder v1)
    {
        var b = v1.MapGroup("/billing").WithTags("Billing").AddEndpointFilter(MapConflicts);

        b.MapGet("/invoices", (Guid? contractId, Guid? customerId, InvoiceState? status, InvoiceKind? type, string? search, bool? openOnly, int? take,
            IInvoiceService s, CancellationToken ct) => s.ListAsync(new InvoiceQuery(contractId, customerId, status, type, search, openOnly ?? false, take ?? 500), ct));
        b.MapGet("/invoices/{id:guid}", (Guid id, IInvoiceService s, CancellationToken ct) => s.GetAsync(id, ct));
        b.MapGet("/contracts", (IInvoiceService s, CancellationToken ct) => s.ListBillingContractsAsync(ct));
        b.MapPost("/invoices/preview", (GenerateInvoiceRequest r, IInvoiceService s, CancellationToken ct) => s.PreviewAsync(r, ct));
        b.MapPost("/invoices", (GenerateInvoiceRequest r, IInvoiceService s, CancellationToken ct) => s.GenerateDraftAsync(r, ct));
        b.MapPost("/invoices/{id:guid}/regenerate", (Guid id, IInvoiceService s, CancellationToken ct) => s.RegenerateDraftAsync(id, ct));
        b.MapPut("/invoices/{id:guid}", (Guid id, UpdateInvoiceDraftRequest r, IInvoiceService s, CancellationToken ct) => s.UpdateDraftAsync(id, r, ct));
        b.MapPost("/invoices/{id:guid}/lines", (Guid id, AddInvoiceLineRequest r, IInvoiceService s, CancellationToken ct) => s.AddLineAsync(id, r, ct));
        b.MapDelete("/invoices/{id:guid}/lines/{lineId:guid}", (Guid id, Guid lineId, IInvoiceService s, CancellationToken ct) => s.RemoveLineAsync(id, lineId, ct));
        b.MapDelete("/invoices/{id:guid}", async (Guid id, IInvoiceService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });
        b.MapPost("/invoices/{id:guid}/approve", (Guid id, ApproveInvoiceRequest r, IInvoiceService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        b.MapPost("/invoices/{id:guid}/sent", (Guid id, IInvoiceService s, CancellationToken ct) => s.MarkSentAsync(id, ct));
        b.MapPost("/invoices/{id:guid}/void", (Guid id, VoidInvoiceRequest r, IInvoiceService s, CancellationToken ct) => s.VoidAsync(id, r, ct));
        b.MapPost("/invoices/{id:guid}/credit-memo", (Guid id, CreditMemoRequest r, IInvoiceService s, CancellationToken ct) => s.CreateCreditMemoAsync(id, r, ct));
        b.MapGet("/invoices/{id:guid}/print", (Guid id, IInvoiceService s, CancellationToken ct) => s.GetPrintAsync(id, ct));

        // JSON-wrapped for the desktop client; the .csv route serves the file to browsers.
        b.MapGet("/invoices/{id:guid}/wawf", (Guid id, IInvoiceService s, CancellationToken ct) => s.ExportWawfCsvAsync(id, ct));
        b.MapGet("/invoices/{id:guid}/wawf.csv", async (Guid id, IInvoiceService s, CancellationToken ct) =>
        {
            var file = await s.ExportWawfCsvAsync(id, ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
        });

        b.MapGet("/contracts/{id:guid}/summary", (Guid id, IInvoiceService s, CancellationToken ct) => s.GetContractSummaryAsync(id, ct));
        b.MapGet("/contracts/{id:guid}/settings", (Guid id, IInvoiceService s, CancellationToken ct) => s.GetSettingsAsync(id, ct));
        b.MapPut("/contracts/{id:guid}/settings", (Guid id, SaveContractBillingSettingsRequest r, IInvoiceService s, CancellationToken ct) => s.SaveSettingsAsync(id, r, ct));
        b.MapGet("/accruals/{periodId:guid}", (Guid periodId, IInvoiceService s, CancellationToken ct) => s.GetUnbilledAccrualAsync(periodId, ct));
        b.MapPost("/accruals/{periodId:guid}", (Guid periodId, IInvoiceService s, CancellationToken ct) => s.PostUnbilledAccrualAsync(periodId, ct));

        b.MapGet("/contracts/{id:guid}/milestones", (Guid id, IBillingMilestoneService s, CancellationToken ct) => s.ListAsync(id, ct));
        b.MapPost("/contracts/{id:guid}/milestones", (Guid id, SaveMilestoneRequest r, IBillingMilestoneService s, CancellationToken ct) => s.CreateAsync(id, r, ct));
        b.MapPut("/milestones/{id:guid}", (Guid id, SaveMilestoneRequest r, IBillingMilestoneService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        b.MapPost("/milestones/{id:guid}/complete", (Guid id, CompleteMilestoneRequest r, IBillingMilestoneService s, CancellationToken ct) => s.CompleteAsync(id, r, ct));
        b.MapPost("/milestones/{id:guid}/reopen", (Guid id, IBillingMilestoneService s, CancellationToken ct) => s.ReopenAsync(id, ct));
        b.MapDelete("/milestones/{id:guid}", async (Guid id, IBillingMilestoneService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        b.MapGet("/payments", (Guid? customerId, DateOnly? from, DateOnly? to, int? take, IPaymentService s, CancellationToken ct) =>
            s.ListAsync(new PaymentQuery(customerId, from, to, take ?? 500), ct));
        b.MapGet("/payments/{id:guid}", (Guid id, IPaymentService s, CancellationToken ct) => s.GetAsync(id, ct));
        b.MapGet("/customers/{id:guid}/open-invoices", (Guid id, IPaymentService s, CancellationToken ct) => s.ListOpenInvoicesAsync(id, ct));
        b.MapPost("/payments", (RecordPaymentRequest r, IPaymentService s, CancellationToken ct) => s.RecordAsync(r, ct));
        b.MapPost("/payments/{id:guid}/void", (Guid id, VoidInvoiceRequest r, IPaymentService s, CancellationToken ct) => s.VoidAsync(id, r, ct));

        b.MapGet("/ar/aging", (DateOnly? asOf, IArReportService s, CancellationToken ct) => s.GetAgingAsync(asOf, ct));
        b.MapGet("/ar/customers", (IArReportService s, CancellationToken ct) => s.ListCustomersAsync(ct));
        b.MapGet("/ar/customers/{id:guid}/statement", (Guid id, DateOnly from, DateOnly to, IArReportService s, CancellationToken ct) => s.GetStatementAsync(id, from, to, ct));
        b.MapPost("/ar/overdue-reminders", (IArReportService s, CancellationToken ct) => s.SendOverdueRemindersAsync(ct));
    }

    /// <summary>Wrong state (already approved, past funding without an override, paid) → 409.</summary>
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
