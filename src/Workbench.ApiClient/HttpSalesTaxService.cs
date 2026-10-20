using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.SalesTax;

namespace Workbench.ApiClient;

public sealed class HttpSalesTaxService(HttpClient http) : ISalesTaxService
{
    private const string Base = "api/v1/sales-tax";

    public async Task<IReadOnlyList<TaxJurisdictionDto>> ListJurisdictionsAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/jurisdictions?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<TaxJurisdictionDto>>(cancellationToken);

    public async Task<TaxJurisdictionDto> SaveJurisdictionAsync(Guid? jurisdictionId, SaveTaxJurisdictionRequest request, CancellationToken cancellationToken = default) =>
        await (jurisdictionId is { } id
            ? await http.PutAsJsonAsync($"{Base}/jurisdictions/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/jurisdictions", request, cancellationToken)).ReadLedgerAsync<TaxJurisdictionDto>(cancellationToken);

    public async Task<TaxJurisdictionDto> AddRateAsync(Guid jurisdictionId, AddTaxRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/jurisdictions/{jurisdictionId}/rates", request, cancellationToken)).ReadLedgerAsync<TaxJurisdictionDto>(cancellationToken);

    public async Task<TaxRateImportResultDto> ImportRatesAsync(ImportTaxRatesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/rates/import", request, cancellationToken)).ReadLedgerAsync<TaxRateImportResultDto>(cancellationToken);

    public async Task<TaxQuoteDto> QuoteAsync(TaxQuoteRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/quote", request, cancellationToken)).ReadLedgerAsync<TaxQuoteDto>(cancellationToken);

    public async Task<IReadOnlyList<TaxCodeDto>> ListTaxCodesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/codes", cancellationToken)).ReadLedgerAsync<List<TaxCodeDto>>(cancellationToken);

    public async Task<TaxCodeDto> SaveTaxCodeAsync(Guid? taxCodeId, SaveTaxCodeRequest request, CancellationToken cancellationToken = default) =>
        await (taxCodeId is { } id
            ? await http.PutAsJsonAsync($"{Base}/codes/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/codes", request, cancellationToken)).ReadLedgerAsync<TaxCodeDto>(cancellationToken);

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> ListAssignmentsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/assignments", cancellationToken)).ReadLedgerAsync<List<TaxCodeAssignmentDto>>(cancellationToken);

    public async Task<IReadOnlyList<TaxCodeAssignmentDto>> SaveAssignmentAsync(SaveTaxCodeAssignmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/assignments", request, cancellationToken)).ReadLedgerAsync<List<TaxCodeAssignmentDto>>(cancellationToken);

    public async Task<IReadOnlyList<CustomerTaxDto>> ListCustomersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/customers", cancellationToken)).ReadLedgerAsync<List<CustomerTaxDto>>(cancellationToken);

    public async Task<CustomerTaxDto> SaveCustomerAsync(Guid customerId, SaveCustomerTaxRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/customers/{customerId}", request, cancellationToken)).ReadLedgerAsync<CustomerTaxDto>(cancellationToken);

    public async Task<CustomerTaxDto> SaveCertificateAsync(Guid customerId, Guid? certificateId, SaveTaxExemptionCertificateRequest request, CancellationToken cancellationToken = default) =>
        await (certificateId is { } id
            ? await http.PutAsJsonAsync($"{Base}/customers/{customerId}/certificates/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/customers/{customerId}/certificates", request, cancellationToken)).ReadLedgerAsync<CustomerTaxDto>(cancellationToken);

    public async Task<SalesTaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<SalesTaxSettingsDto>(cancellationToken);

    public async Task<SalesTaxSettingsDto> SaveSettingsAsync(SaveSalesTaxSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/settings", request, cancellationToken)).ReadLedgerAsync<SalesTaxSettingsDto>(cancellationToken);

    public async Task<IReadOnlyList<TaxAccountOptionDto>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/accounts", cancellationToken)).ReadLedgerAsync<List<TaxAccountOptionDto>>(cancellationToken);

    public async Task<InvoiceTaxDto> GetInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}", cancellationToken)).ReadLedgerAsync<InvoiceTaxDto>(cancellationToken);

    public async Task<InvoiceTaxDto> CalculateInvoiceTaxAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/invoices/{invoiceId}/calculate", null, cancellationToken)).ReadLedgerAsync<InvoiceTaxDto>(cancellationToken);

    public async Task<InvoiceTaxDto> SetInvoiceTaxAsync(Guid invoiceId, SetInvoiceTaxRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/invoices/{invoiceId}", request, cancellationToken)).ReadLedgerAsync<InvoiceTaxDto>(cancellationToken);

    public async Task<BillTaxDto> GetBillTaxAsync(Guid billId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/bills/{billId}", cancellationToken)).ReadLedgerAsync<BillTaxDto>(cancellationToken);

    public async Task<BillTaxDto> SetBillTaxAsync(Guid billId, SetBillTaxRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/bills/{billId}", request, cancellationToken)).ReadLedgerAsync<BillTaxDto>(cancellationToken);

    public async Task<SalesTaxReturnDto> GetReturnAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/return", ("from", from), ("to", to), ("state", state)), cancellationToken)).ReadLedgerAsync<SalesTaxReturnDto>(cancellationToken);

    public async Task<SalesTaxExportFile> ExportReturnCsvAsync(DateOnly from, DateOnly to, string? state = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/return/csv", ("from", from), ("to", to), ("state", state)), cancellationToken)).ReadLedgerAsync<SalesTaxExportFile>(cancellationToken);
}

public static class SalesTaxClientRegistration
{
    public static IServiceCollection AddSalesTaxClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ISalesTaxService, HttpSalesTaxService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
