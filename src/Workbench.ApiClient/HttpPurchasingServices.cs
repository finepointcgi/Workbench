using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;

namespace Workbench.ApiClient;

public sealed class HttpVendorService(HttpClient http) : IVendorService
{
    private const string Base = "api/v1/purchasing/vendors";

    public async Task<IReadOnlyList<VendorDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<VendorDto>>(cancellationToken);

    public async Task<VendorDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{vendorId}", cancellationToken)).ReadLedgerAsync<VendorDto>(cancellationToken);

    public async Task<IReadOnlyList<VendorDuplicateDto>> FindDuplicatesAsync(string name, string? uei, Guid? excludeVendorId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/duplicates", ("name", name), ("uei", uei), ("excludeId", excludeVendorId)), cancellationToken))
            .ReadLedgerAsync<List<VendorDuplicateDto>>(cancellationToken);

    public async Task<VendorDto> CreateAsync(SaveVendorRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<VendorDto>(cancellationToken);

    public async Task<VendorDto> UpdateAsync(Guid vendorId, SaveVendorRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{vendorId}", request, cancellationToken)).ReadLedgerAsync<VendorDto>(cancellationToken);

    public async Task<VendorDto> SetTaxIdAsync(Guid vendorId, SetVendorTaxIdRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{vendorId}/tax-id", request, cancellationToken)).ReadLedgerAsync<VendorDto>(cancellationToken);

    public async Task<VendorTaxIdDto> RevealTaxIdAsync(Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{vendorId}/tax-id/reveal", null, cancellationToken)).ReadLedgerAsync<VendorTaxIdDto>(cancellationToken);

    public async Task<VendorDto> AttachW9Async(Guid vendorId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        return await (await http.PostAsync($"{Base}/{vendorId}/w9", form, cancellationToken)).ReadLedgerAsync<VendorDto>(cancellationToken);
    }

    public async Task<ReceiptContent> OpenW9Async(Guid vendorId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(GetW9Url(vendorId), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var headers = response.Content.Headers;
        var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? $"w9-{vendorId}";
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return new ReceiptContent(fileName, headers.ContentType?.MediaType ?? AttachmentRules.ContentTypeFor(fileName), buffer);
    }

    public string GetW9Url(Guid vendorId) => new Uri(http.BaseAddress!, $"{Base}/{vendorId}/w9").ToString();
}

public sealed class HttpSubcontractService(HttpClient http) : ISubcontractService
{
    private const string Base = "api/v1/purchasing/subcontracts";

    public async Task<IReadOnlyList<SubcontractDto>> ListAsync(Guid? vendorId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("vendorId", vendorId)), cancellationToken)).ReadLedgerAsync<List<SubcontractDto>>(cancellationToken);

    public async Task<SubcontractDetailDto> GetAsync(Guid subcontractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{subcontractId}", cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> CreateAsync(SaveSubcontractRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> UpdateAsync(Guid subcontractId, SaveSubcontractRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{subcontractId}", request, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> ActivateAsync(Guid subcontractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{subcontractId}/activate", null, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> CloseAsync(Guid subcontractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{subcontractId}/close", null, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> AddFundingAsync(Guid subcontractId, AddSubcontractFundingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{subcontractId}/funding", request, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> SaveFlowDownAsync(Guid subcontractId, SaveFlowDownRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{subcontractId}/flow-downs", request, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> RemoveFlowDownAsync(Guid subcontractId, Guid flowDownId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{subcontractId}/flow-downs/{flowDownId}", cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);

    public async Task<SubcontractDetailDto> AddStandardFlowDownsAsync(Guid subcontractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{subcontractId}/flow-downs/standard", null, cancellationToken)).ReadLedgerAsync<SubcontractDetailDto>(cancellationToken);
}

public sealed class HttpApBillService(HttpClient http) : IApBillService
{
    private const string Base = "api/v1/purchasing/bills";

    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With(Base, ("status", query.Status), ("vendorId", query.VendorId), ("search", query.Search), ("take", query.Take)), cancellationToken))
            .ReadLedgerAsync<List<BillSummaryDto>>(cancellationToken);

    public async Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{billId}", cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/duplicates", request, cancellationToken)).ReadLedgerAsync<List<BillDuplicateDto>>(cancellationToken);

    public async Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{billId}", request, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{billId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{billId}/submit", request, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/approvals", cancellationToken)).ReadLedgerAsync<List<BillApprovalItemDto>>(cancellationToken);

    public async Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{billId}/approve", null, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{billId}/reject", request, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{billId}/post", null, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{billId}/void", request, cancellationToken)).ReadLedgerAsync<BillDto>(cancellationToken);

    public async Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/linkable-odcs/{vendorId}", cancellationToken)).ReadLedgerAsync<List<LinkableOdcDto>>(cancellationToken);

    public async Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/odc-links", odcEntryIds, cancellationToken)).ReadLedgerAsync<List<OdcBillLinkDto>>(cancellationToken);
}

public sealed class HttpApPaymentService(HttpClient http) : IApPaymentService
{
    private const string Base = "api/v1/purchasing";

    public async Task<IReadOnlyList<DueBillDto>> ListDueBillsAsync(DueBillQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/payments/due", ("dueOnOrBefore", query.DueOnOrBefore), ("vendorId", query.VendorId)), cancellationToken))
            .ReadLedgerAsync<List<DueBillDto>>(cancellationToken);

    public async Task<IReadOnlyList<PaymentRunDto>> ListRunsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/payment-runs", cancellationToken)).ReadLedgerAsync<List<PaymentRunDto>>(cancellationToken);

    public async Task<PaymentRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/payment-runs/{runId}", cancellationToken)).ReadLedgerAsync<PaymentRunDto>(cancellationToken);

    public async Task<PaymentRunDto> CreateRunAsync(CreatePaymentRunRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payment-runs", request, cancellationToken)).ReadLedgerAsync<PaymentRunDto>(cancellationToken);

    public async Task<PaymentRunDto> ApproveRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/payment-runs/{runId}/approve", null, cancellationToken)).ReadLedgerAsync<PaymentRunDto>(cancellationToken);

    public async Task<PaymentRunDto> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/payment-runs/{runId}/cancel", null, cancellationToken)).ReadLedgerAsync<PaymentRunDto>(cancellationToken);

    public async Task<PaymentRunDto> RecordPaymentsAsync(Guid runId, RecordPaymentsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payment-runs/{runId}/pay", request, cancellationToken)).ReadLedgerAsync<PaymentRunDto>(cancellationToken);

    public async Task<PaymentDto> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payments/{paymentId}/void", request, cancellationToken)).ReadLedgerAsync<PaymentDto>(cancellationToken);

    public async Task<PurchasingExportFile> ExportAchAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/payment-runs/{runId}/ach", cancellationToken)).ReadLedgerAsync<PurchasingExportFile>(cancellationToken);

    public async Task<PurchasingExportFile> ExportPositivePayAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/payment-runs/{runId}/positive-pay", cancellationToken)).ReadLedgerAsync<PurchasingExportFile>(cancellationToken);
}

public sealed class HttpApReportService(HttpClient http) : IApReportService
{
    private const string Base = "api/v1/purchasing/reports";

    public async Task<AgingReportDto> GetAgingAsync(DateOnly asOf, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/aging", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<AgingReportDto>(cancellationToken);

    public async Task<VendorLedgerDto> GetVendorLedgerAsync(Guid vendorId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/vendor-ledger/{vendorId}", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<VendorLedgerDto>(cancellationToken);

    public async Task<Form1099ReportDto> Get1099Async(int year, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/1099/{year}", cancellationToken)).ReadLedgerAsync<Form1099ReportDto>(cancellationToken);

    public async Task<IReadOnlyList<SubcontractDto>> GetSubcontractStatusAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/subcontracts", cancellationToken)).ReadLedgerAsync<List<SubcontractDto>>(cancellationToken);

    public async Task<SmallBusinessReportDto> GetSmallBusinessAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/small-business", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<SmallBusinessReportDto>(cancellationToken);

    public async Task<PurchasingExportFile> ExportAgingCsvAsync(DateOnly asOf, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/aging/export", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<PurchasingExportFile>(cancellationToken);

    public async Task<PurchasingExportFile> Export1099CsvAsync(int year, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/1099/{year}/export", cancellationToken)).ReadLedgerAsync<PurchasingExportFile>(cancellationToken);
}

public static class PurchasingClientRegistration
{
    public static IServiceCollection AddPurchasingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IVendorService, HttpVendorService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISubcontractService, HttpSubcontractService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApBillService, HttpApBillService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApPaymentService, HttpApPaymentService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApReportService, HttpApReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
