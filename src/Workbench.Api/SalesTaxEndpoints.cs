using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.SalesTax;

namespace Workbench.Api;

internal static class SalesTaxEndpoints
{
    public static void MapSalesTaxEndpoints(this RouteGroupBuilder v1)
    {
        var t = v1.MapGroup("/sales-tax").WithTags("Sales and use tax").AddEndpointFilter(MapConflicts);

        t.MapGet("/jurisdictions", (bool? includeInactive, ISalesTaxService s, CancellationToken ct) => s.ListJurisdictionsAsync(includeInactive ?? true, ct));
        t.MapPost("/jurisdictions", (SaveTaxJurisdictionRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveJurisdictionAsync(null, r, ct));
        t.MapPut("/jurisdictions/{id:guid}", (Guid id, SaveTaxJurisdictionRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveJurisdictionAsync(id, r, ct));
        t.MapPost("/jurisdictions/{id:guid}/rates", (Guid id, AddTaxRateRequest r, ISalesTaxService s, CancellationToken ct) => s.AddRateAsync(id, r, ct));
        t.MapPost("/rates/import", (ImportTaxRatesRequest r, ISalesTaxService s, CancellationToken ct) => s.ImportRatesAsync(r, ct));
        t.MapPost("/quote", (TaxQuoteRequest r, ISalesTaxService s, CancellationToken ct) => s.QuoteAsync(r, ct));

        t.MapGet("/codes", (ISalesTaxService s, CancellationToken ct) => s.ListTaxCodesAsync(ct));
        t.MapPost("/codes", (SaveTaxCodeRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveTaxCodeAsync(null, r, ct));
        t.MapPut("/codes/{id:guid}", (Guid id, SaveTaxCodeRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveTaxCodeAsync(id, r, ct));
        t.MapGet("/assignments", (ISalesTaxService s, CancellationToken ct) => s.ListAssignmentsAsync(ct));
        t.MapPut("/assignments", (SaveTaxCodeAssignmentRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveAssignmentAsync(r, ct));

        t.MapGet("/customers", (ISalesTaxService s, CancellationToken ct) => s.ListCustomersAsync(ct));
        t.MapPut("/customers/{customerId:guid}", (Guid customerId, SaveCustomerTaxRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveCustomerAsync(customerId, r, ct));
        t.MapPost("/customers/{customerId:guid}/certificates", (Guid customerId, SaveTaxExemptionCertificateRequest r, ISalesTaxService s, CancellationToken ct) =>
            s.SaveCertificateAsync(customerId, null, r, ct));
        t.MapPut("/customers/{customerId:guid}/certificates/{certificateId:guid}", (Guid customerId, Guid certificateId, SaveTaxExemptionCertificateRequest r, ISalesTaxService s, CancellationToken ct) =>
            s.SaveCertificateAsync(customerId, certificateId, r, ct));

        t.MapGet("/settings", (ISalesTaxService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        t.MapPut("/settings", (SaveSalesTaxSettingsRequest r, ISalesTaxService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
        t.MapGet("/accounts", (ISalesTaxService s, CancellationToken ct) => s.ListAccountsAsync(ct));

        t.MapGet("/invoices/{invoiceId:guid}", (Guid invoiceId, ISalesTaxService s, CancellationToken ct) => s.GetInvoiceTaxAsync(invoiceId, ct));
        t.MapPost("/invoices/{invoiceId:guid}/calculate", (Guid invoiceId, ISalesTaxService s, CancellationToken ct) => s.CalculateInvoiceTaxAsync(invoiceId, ct));
        t.MapPut("/invoices/{invoiceId:guid}", (Guid invoiceId, SetInvoiceTaxRequest r, ISalesTaxService s, CancellationToken ct) => s.SetInvoiceTaxAsync(invoiceId, r, ct));
        t.MapGet("/bills/{billId:guid}", (Guid billId, ISalesTaxService s, CancellationToken ct) => s.GetBillTaxAsync(billId, ct));
        t.MapPut("/bills/{billId:guid}", (Guid billId, SetBillTaxRequest r, ISalesTaxService s, CancellationToken ct) => s.SetBillTaxAsync(billId, r, ct));

        t.MapGet("/return", (DateOnly from, DateOnly to, string? state, ISalesTaxService s, CancellationToken ct) => s.GetReturnAsync(from, to, state, ct));
        t.MapGet("/return/csv", (DateOnly from, DateOnly to, string? state, ISalesTaxService s, CancellationToken ct) => s.ExportReturnCsvAsync(from, to, state, ct));
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
