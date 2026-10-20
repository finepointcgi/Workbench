using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.BankFiles;

namespace Workbench.ApiClient;

public sealed class HttpVendorBankService(HttpClient http) : IVendorBankService
{
    private const string Base = "api/v1/vendor-bank";

    public async Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{vendorId}", cancellationToken)).ReadLedgerAsync<VendorBankDetailsDto>(cancellationToken);

    public async Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/changes/pending", cancellationToken)).ReadLedgerAsync<List<VendorBankChangeDto>>(cancellationToken);

    public async Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{vendorId}/changes", request, cancellationToken)).ReadLedgerAsync<VendorBankChangeDto>(cancellationToken);

    public async Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/changes/{changeId}/approve", request, cancellationToken)).ReadLedgerAsync<VendorBankChangeDto>(cancellationToken);

    public async Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/changes/{changeId}/reject", request, cancellationToken)).ReadLedgerAsync<VendorBankChangeDto>(cancellationToken);

    public async Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/changes/{changeId}/cancel", null, cancellationToken)).ReadLedgerAsync<VendorBankChangeDto>(cancellationToken);

    public async Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{vendorId}/reveal", null, cancellationToken)).ReadLedgerAsync<VendorBankRevealDto>(cancellationToken);
}

public sealed class HttpPaymentFileService(HttpClient http) : IPaymentFileService
{
    private const string Base = "api/v1/bank-files";

    public async Task<IReadOnlyList<BankFileSettingsDto>> ListBankAccountsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/accounts", cancellationToken)).ReadLedgerAsync<List<BankFileSettingsDto>>(cancellationToken);

    public async Task<BankFileSettingsDto> GetSettingsAsync(Guid bankAccountId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/accounts/{bankAccountId}", cancellationToken)).ReadLedgerAsync<BankFileSettingsDto>(cancellationToken);

    public async Task<BankFileSettingsDto> SaveSettingsAsync(Guid bankAccountId, SaveBankFileSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/accounts/{bankAccountId}", request, cancellationToken)).ReadLedgerAsync<BankFileSettingsDto>(cancellationToken);

    public async Task<IReadOnlyList<PositivePayLayoutDto>> ListLayoutsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/layouts", cancellationToken)).ReadLedgerAsync<List<PositivePayLayoutDto>>(cancellationToken);

    public async Task<PositivePayLayoutDto> SaveLayoutAsync(Guid? layoutId, SavePositivePayLayoutRequest request, CancellationToken cancellationToken = default) =>
        await (layoutId is { } id
            ? await http.PutAsJsonAsync($"{Base}/layouts/{id}", request, cancellationToken)
            : await http.PostAsJsonAsync($"{Base}/layouts", request, cancellationToken)).ReadLedgerAsync<PositivePayLayoutDto>(cancellationToken);

    public async Task<PaymentRunFilesDto> GetRunFilesAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}", cancellationToken)).ReadLedgerAsync<PaymentRunFilesDto>(cancellationToken);

    public async Task<PaymentFileDto> GenerateAsync(Guid runId, GeneratePaymentFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/runs/{runId}/files", request, cancellationToken)).ReadLedgerAsync<PaymentFileDto>(cancellationToken);

    public async Task<RemittanceResultDto> SendRemittanceAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/runs/{runId}/remittance", null, cancellationToken)).ReadLedgerAsync<RemittanceResultDto>(cancellationToken);
}

public static class BankFileClientRegistration
{
    public static IServiceCollection AddBankFileClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IVendorBankService, HttpVendorBankService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPaymentFileService, HttpPaymentFileService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
