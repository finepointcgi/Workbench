using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Purchasing;

namespace Workbench.Api;

internal static class PurchasingEndpoints
{
    public static void MapPurchasingEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/purchasing").WithTags("Purchasing").AddEndpointFilter(MapPurchasingErrors);

        // Vendors
        p.MapGet("/vendors", (bool? includeInactive, IVendorService s, CancellationToken ct) => s.ListAsync(includeInactive ?? true, ct));
        p.MapGet("/vendors/duplicates", (string name, string? uei, Guid? excludeId, IVendorService s, CancellationToken ct) => s.FindDuplicatesAsync(name, uei, excludeId, ct));
        p.MapGet("/vendors/{id:guid}", (Guid id, IVendorService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/vendors", (SaveVendorRequest r, IVendorService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/vendors/{id:guid}", (Guid id, SaveVendorRequest r, IVendorService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPut("/vendors/{id:guid}/tax-id", (Guid id, SetVendorTaxIdRequest r, IVendorService s, CancellationToken ct) => s.SetTaxIdAsync(id, r, ct));
        p.MapPost("/vendors/{id:guid}/tax-id/reveal", (Guid id, IVendorService s, CancellationToken ct) => s.RevealTaxIdAsync(id, ct));
        p.MapPost("/vendors/{id:guid}/w9", async (Guid id, HttpRequest http, IVendorService s, CancellationToken ct) =>
            {
                var file = http.HasFormContentType ? (await http.ReadFormAsync(ct)).Files.GetFile("file") : null;
                if (file is null)
                {
                    return Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
                }

                if (file.Length > AttachmentRules.MaxSizeBytes)
                {
                    throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
                }

                await using var stream = file.OpenReadStream();
                return Results.Ok(await s.AttachW9Async(id, file.FileName, stream, ct));
            })
            .DisableAntiforgery();
        p.MapGet("/vendors/{id:guid}/w9", async (Guid id, IVendorService s, CancellationToken ct) =>
        {
            var w9 = await s.OpenW9Async(id, ct);

            // Always a download, never rendered inline.
            return Results.File(w9.Content, w9.ContentType, w9.FileName);
        });

        // Subcontracts
        p.MapGet("/subcontracts", (Guid? vendorId, ISubcontractService s, CancellationToken ct) => s.ListAsync(vendorId, ct));
        p.MapGet("/subcontracts/{id:guid}", (Guid id, ISubcontractService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/subcontracts", (SaveSubcontractRequest r, ISubcontractService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/subcontracts/{id:guid}", (Guid id, SaveSubcontractRequest r, ISubcontractService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapPost("/subcontracts/{id:guid}/activate", (Guid id, ISubcontractService s, CancellationToken ct) => s.ActivateAsync(id, ct));
        p.MapPost("/subcontracts/{id:guid}/close", (Guid id, ISubcontractService s, CancellationToken ct) => s.CloseAsync(id, ct));
        p.MapPost("/subcontracts/{id:guid}/funding", (Guid id, AddSubcontractFundingRequest r, ISubcontractService s, CancellationToken ct) => s.AddFundingAsync(id, r, ct));
        p.MapPut("/subcontracts/{id:guid}/flow-downs", (Guid id, SaveFlowDownRequest r, ISubcontractService s, CancellationToken ct) => s.SaveFlowDownAsync(id, r, ct));
        p.MapDelete("/subcontracts/{id:guid}/flow-downs/{flowDownId:guid}", (Guid id, Guid flowDownId, ISubcontractService s, CancellationToken ct) => s.RemoveFlowDownAsync(id, flowDownId, ct));
        p.MapPost("/subcontracts/{id:guid}/flow-downs/standard", (Guid id, ISubcontractService s, CancellationToken ct) => s.AddStandardFlowDownsAsync(id, ct));

        // Bills
        p.MapGet("/bills", (BillState? status, Guid? vendorId, string? search, int? take, IApBillService s, CancellationToken ct) =>
            s.ListAsync(new BillQuery(status, vendorId, search, take ?? 500), ct));
        p.MapGet("/bills/approvals", (IApBillService s, CancellationToken ct) => s.ListApprovalQueueAsync(ct));
        p.MapPost("/bills/duplicates", (DuplicateCheckRequest r, IApBillService s, CancellationToken ct) => s.CheckDuplicatesAsync(r, ct));
        p.MapPost("/bills/odc-links", (List<Guid> ids, IApBillService s, CancellationToken ct) => s.GetOdcLinksAsync(ids, ct));
        p.MapGet("/bills/linkable-odcs/{vendorId:guid}", (Guid vendorId, IApBillService s, CancellationToken ct) => s.ListLinkableOdcsAsync(vendorId, ct));
        p.MapGet("/bills/{id:guid}", (Guid id, IApBillService s, CancellationToken ct) => s.GetAsync(id, ct));
        p.MapPost("/bills", (SaveBillRequest r, IApBillService s, CancellationToken ct) => s.CreateAsync(r, ct));
        p.MapPut("/bills/{id:guid}", (Guid id, SaveBillRequest r, IApBillService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        p.MapDelete("/bills/{id:guid}", async (Guid id, IApBillService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });
        p.MapPost("/bills/{id:guid}/submit", (Guid id, SubmitBillRequest r, IApBillService s, CancellationToken ct) => s.SubmitAsync(id, r, ct));
        p.MapPost("/bills/{id:guid}/approve", (Guid id, IApBillService s, CancellationToken ct) => s.ApproveAsync(id, ct));
        p.MapPost("/bills/{id:guid}/reject", (Guid id, BillReasonRequest r, IApBillService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        p.MapPost("/bills/{id:guid}/post", (Guid id, IApBillService s, CancellationToken ct) => s.PostAsync(id, ct));
        p.MapPost("/bills/{id:guid}/void", (Guid id, BillReasonRequest r, IApBillService s, CancellationToken ct) => s.VoidAsync(id, r, ct));

        // Payments
        p.MapGet("/payments/due", (DateOnly? dueOnOrBefore, Guid? vendorId, IApPaymentService s, CancellationToken ct) => s.ListDueBillsAsync(new DueBillQuery(dueOnOrBefore, vendorId), ct));
        p.MapGet("/payment-runs", (IApPaymentService s, CancellationToken ct) => s.ListRunsAsync(ct));
        p.MapGet("/payment-runs/{id:guid}", (Guid id, IApPaymentService s, CancellationToken ct) => s.GetRunAsync(id, ct));
        p.MapPost("/payment-runs", (CreatePaymentRunRequest r, IApPaymentService s, CancellationToken ct) => s.CreateRunAsync(r, ct));
        p.MapPost("/payment-runs/{id:guid}/approve", (Guid id, IApPaymentService s, CancellationToken ct) => s.ApproveRunAsync(id, ct));
        p.MapPost("/payment-runs/{id:guid}/cancel", (Guid id, IApPaymentService s, CancellationToken ct) => s.CancelRunAsync(id, ct));
        p.MapPost("/payment-runs/{id:guid}/pay", (Guid id, RecordPaymentsRequest r, IApPaymentService s, CancellationToken ct) => s.RecordPaymentsAsync(id, r, ct));
        p.MapPost("/payments/{id:guid}/void", (Guid id, VoidPaymentRequest r, IApPaymentService s, CancellationToken ct) => s.VoidPaymentAsync(id, r, ct));
        p.MapGet("/payment-runs/{id:guid}/ach", (Guid id, IApPaymentService s, CancellationToken ct) => s.ExportAchAsync(id, ct));
        p.MapGet("/payment-runs/{id:guid}/positive-pay", (Guid id, IApPaymentService s, CancellationToken ct) => s.ExportPositivePayAsync(id, ct));

        // Reports
        p.MapGet("/reports/aging", (DateOnly asOf, IApReportService s, CancellationToken ct) => s.GetAgingAsync(asOf, ct));
        p.MapGet("/reports/aging/export", (DateOnly asOf, IApReportService s, CancellationToken ct) => s.ExportAgingCsvAsync(asOf, ct));
        p.MapGet("/reports/vendor-ledger/{vendorId:guid}", (Guid vendorId, DateOnly? from, DateOnly? to, IApReportService s, CancellationToken ct) => s.GetVendorLedgerAsync(vendorId, from, to, ct));
        p.MapGet("/reports/1099/{year:int}", (int year, IApReportService s, CancellationToken ct) => s.Get1099Async(year, ct));
        p.MapGet("/reports/1099/{year:int}/export", (int year, IApReportService s, CancellationToken ct) => s.Export1099CsvAsync(year, ct));
        p.MapGet("/reports/subcontracts", (IApReportService s, CancellationToken ct) => s.GetSubcontractStatusAsync(ct));
        p.MapGet("/reports/small-business", (DateOnly from, DateOnly to, IApReportService s, CancellationToken ct) => s.GetSmallBusinessAsync(from, to, ct));
    }

    /// <summary>Wrong state (already approved, posted, paid or void) → 409.</summary>
    private static async ValueTask<object?> MapPurchasingErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
