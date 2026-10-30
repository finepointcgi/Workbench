using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Billing;

namespace Workbench.ApiClient;

public sealed class HttpInvoiceService(HttpClient http) : IInvoiceService
{
    private const string Base = "api/v1/billing";

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/invoices", ("contractId", query.ContractId), ("customerId", query.CustomerId), ("status", query.Status),
            ("type", query.Type), ("search", query.Search), ("openOnly", query.OpenOnly ? true : null), ("take", query.Take)), cancellationToken))
            .ReadLedgerAsync<List<InvoiceSummaryDto>>(cancellationToken);

    public async Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}", cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts", cancellationToken)).ReadLedgerAsync<List<BillingContractDto>>(cancellationToken);

    public async Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/preview", request, cancellationToken)).ReadLedgerAsync<InvoicePreviewDto>(cancellationToken);

    public async Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/invoices/{invoiceId}/regenerate", null, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/invoices/{invoiceId}", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/lines", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/invoices/{invoiceId}/lines/{lineId}", cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/invoices/{invoiceId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/approve", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/invoices/{invoiceId}/sent", null, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/void", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/credit-memo", request, cancellationToken)).ReadLedgerAsync<InvoiceDto>(cancellationToken);

    public async Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}/print", cancellationToken)).ReadLedgerAsync<InvoicePrintDto>(cancellationToken);

    public async Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}/wawf", cancellationToken)).ReadLedgerAsync<BillingExportFile>(cancellationToken);

    public async Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/summary", cancellationToken)).ReadLedgerAsync<ContractBillingSummaryDto>(cancellationToken);

    public async Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/settings", cancellationToken)).ReadLedgerAsync<ContractBillingSettingsDto>(cancellationToken);

    public async Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/contracts/{contractId}/settings", request, cancellationToken)).ReadLedgerAsync<ContractBillingSettingsDto>(cancellationToken);

    public async Task<ReadyToBillDto> GetReadyToBillAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/ready-to-bill", cancellationToken)).ReadLedgerAsync<ReadyToBillDto>(cancellationToken);

    public async Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/accruals/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<UnbilledAccrualDto>(cancellationToken);

    public async Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/accruals/{fiscalPeriodId}", null, cancellationToken)).ReadLedgerAsync<UnbilledAccrualDto>(cancellationToken);
}

public sealed class HttpBillingMilestoneService(HttpClient http) : IBillingMilestoneService
{
    private const string Base = "api/v1/billing";

    public async Task<IReadOnlyList<BillingMilestoneDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/milestones", cancellationToken)).ReadLedgerAsync<List<BillingMilestoneDto>>(cancellationToken);

    public async Task<BillingMilestoneDto> CreateAsync(Guid contractId, SaveMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/contracts/{contractId}/milestones", request, cancellationToken)).ReadLedgerAsync<BillingMilestoneDto>(cancellationToken);

    public async Task<BillingMilestoneDto> UpdateAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/milestones/{milestoneId}", request, cancellationToken)).ReadLedgerAsync<BillingMilestoneDto>(cancellationToken);

    public async Task<BillingMilestoneDto> CompleteAsync(Guid milestoneId, CompleteMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/milestones/{milestoneId}/complete", request, cancellationToken)).ReadLedgerAsync<BillingMilestoneDto>(cancellationToken);

    public async Task<BillingMilestoneDto> ReopenAsync(Guid milestoneId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/milestones/{milestoneId}/reopen", null, cancellationToken)).ReadLedgerAsync<BillingMilestoneDto>(cancellationToken);

    public async Task DeleteAsync(Guid milestoneId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/milestones/{milestoneId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpPaymentService(HttpClient http) : IPaymentService
{
    private const string Base = "api/v1/billing";

    public async Task<IReadOnlyList<PaymentDto>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/payments", ("customerId", query.CustomerId), ("from", query.From), ("to", query.To), ("take", query.Take)), cancellationToken))
            .ReadLedgerAsync<List<PaymentDto>>(cancellationToken);

    public async Task<PaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/payments/{paymentId}", cancellationToken)).ReadLedgerAsync<PaymentDto>(cancellationToken);

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListOpenInvoicesAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/customers/{customerId}/open-invoices", cancellationToken)).ReadLedgerAsync<List<InvoiceSummaryDto>>(cancellationToken);

    public async Task<PaymentDto> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payments", request, cancellationToken)).ReadLedgerAsync<PaymentDto>(cancellationToken);

    public async Task<PaymentDto> VoidAsync(Guid paymentId, VoidInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payments/{paymentId}/void", request, cancellationToken)).ReadLedgerAsync<PaymentDto>(cancellationToken);
}

public sealed class HttpArReportService(HttpClient http) : IArReportService
{
    private const string Base = "api/v1/billing/ar";

    public async Task<ArAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/aging", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<ArAgingDto>(cancellationToken);

    public async Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/customers/{customerId}/statement", ("from", from), ("to", to)), cancellationToken))
            .ReadLedgerAsync<CustomerStatementDto>(cancellationToken);

    public async Task<IReadOnlyList<BillingCustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/customers", cancellationToken)).ReadLedgerAsync<List<BillingCustomerDto>>(cancellationToken);

    public async Task<int> SendOverdueRemindersAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/overdue-reminders", null, cancellationToken)).ReadLedgerAsync<int>(cancellationToken);
}

public sealed class HttpCustomerShareLinkService(HttpClient http) : ICustomerShareLinkService
{
    public async Task<IReadOnlyList<CustomerShareLinkDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/billing/contracts/{contractId}/customer-links", cancellationToken)).ReadLedgerAsync<List<CustomerShareLinkDto>>(cancellationToken);

    public async Task<CreatedCustomerShareLinkDto> CreateAsync(Guid contractId, CreateCustomerShareLinkRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/billing/contracts/{contractId}/customer-links", request, cancellationToken)).ReadLedgerAsync<CreatedCustomerShareLinkDto>(cancellationToken);

    public async Task<CustomerShareLinkDto> RevokeAsync(Guid contractId, Guid linkId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/billing/contracts/{contractId}/customer-links/{linkId}/revoke", null, cancellationToken)).ReadLedgerAsync<CustomerShareLinkDto>(cancellationToken);
}

public static class BillingClientRegistration
{
    public static IServiceCollection AddBillingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IInvoiceService, HttpInvoiceService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICustomerShareLinkService, HttpCustomerShareLinkService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBillingMilestoneService, HttpBillingMilestoneService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPaymentService, HttpPaymentService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IArReportService, HttpArReportService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
