using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Billing;

namespace Workbench.ApiClient;

public sealed class HttpGovernmentBillingService(HttpClient http) : IGovernmentBillingService
{
    private const string Base = "api/v1/billing";

    public async Task<GovernmentBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/government", cancellationToken)).ReadLedgerAsync<GovernmentBillingSettingsDto>(cancellationToken);

    public async Task<GovernmentBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveGovernmentBillingSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/contracts/{contractId}/government", request, cancellationToken)).ReadLedgerAsync<GovernmentBillingSettingsDto>(cancellationToken);

    public async Task<FinancingPositionDto> GetFinancingAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/financing", cancellationToken)).ReadLedgerAsync<FinancingPositionDto>(cancellationToken);

    public async Task<VoucherDto> GetVoucherAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}/voucher", cancellationToken)).ReadLedgerAsync<VoucherDto>(cancellationToken);

    public async Task<PieeStatusDto> GetPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}/piee", cancellationToken)).ReadLedgerAsync<PieeStatusDto>(cancellationToken);

    public async Task<BillingExportFile> ExportPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/invoices/{invoiceId}/piee/file", cancellationToken)).ReadLedgerAsync<BillingExportFile>(cancellationToken);

    public async Task<PieeSubmitResultDto> SubmitPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/invoices/{invoiceId}/piee/submit", null, cancellationToken)).ReadLedgerAsync<PieeSubmitResultDto>(cancellationToken);

    public async Task<PieeStatusDto> SetPieeStatusAsync(Guid invoiceId, SetPieeStatusRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/piee/status", request, cancellationToken)).ReadLedgerAsync<PieeStatusDto>(cancellationToken);
}

public sealed class HttpProgressPaymentService(HttpClient http) : IProgressPaymentService
{
    private const string Base = "api/v1/billing";

    public async Task<IReadOnlyList<ProgressPaymentRequestDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/progress-payments", cancellationToken)).ReadLedgerAsync<List<ProgressPaymentRequestDto>>(cancellationToken);

    public async Task<ProgressPaymentRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/progress-payments/{requestId}", cancellationToken)).ReadLedgerAsync<ProgressPaymentRequestDto>(cancellationToken);

    public async Task<Sf1443Dto> PreviewAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/progress-payments/preview", request, cancellationToken)).ReadLedgerAsync<Sf1443Dto>(cancellationToken);

    public async Task<ProgressPaymentRequestDto> CreateAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/progress-payments", request, cancellationToken)).ReadLedgerAsync<ProgressPaymentRequestDto>(cancellationToken);

    public async Task DeleteAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/progress-payments/{requestId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpPerformanceBasedPaymentService(HttpClient http) : IPerformanceBasedPaymentService
{
    private const string Base = "api/v1/billing";

    public async Task<IReadOnlyList<PerformanceEventDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/performance-events", cancellationToken)).ReadLedgerAsync<List<PerformanceEventDto>>(cancellationToken);

    public async Task<PerformanceEventDto> CreateAsync(Guid contractId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/contracts/{contractId}/performance-events", request, cancellationToken)).ReadLedgerAsync<PerformanceEventDto>(cancellationToken);

    public async Task<PerformanceEventDto> UpdateAsync(Guid eventId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/performance-events/{eventId}", request, cancellationToken)).ReadLedgerAsync<PerformanceEventDto>(cancellationToken);

    public async Task<PerformanceEventDto> CompleteAsync(Guid eventId, CompletePerformanceEventRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/performance-events/{eventId}/complete", request, cancellationToken)).ReadLedgerAsync<PerformanceEventDto>(cancellationToken);

    public async Task<PerformanceEventDto> ReopenAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/performance-events/{eventId}/reopen", null, cancellationToken)).ReadLedgerAsync<PerformanceEventDto>(cancellationToken);

    public async Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/performance-events/{eventId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public static class GovernmentBillingClientRegistration
{
    public static IServiceCollection AddGovernmentBillingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IGovernmentBillingService, HttpGovernmentBillingService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProgressPaymentService, HttpProgressPaymentService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPerformanceBasedPaymentService, HttpPerformanceBasedPaymentService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
