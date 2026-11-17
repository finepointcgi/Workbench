using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Billing;

namespace Workbench.ApiClient;

public sealed class HttpAgreementBillingService(HttpClient http) : IAgreementBillingService
{
    private const string Base = "api/v1/billing";

    public async Task<AgreementBillingPlanDto?> GetPlanAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"{Base}/contracts/{contractId}/agreement", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            response.Dispose();
            return null;
        }

        return await response.ReadLedgerAsync<AgreementBillingPlanDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<AgreementBillingPlanDto>> ListPlansAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/agreements", cancellationToken)).ReadLedgerAsync<List<AgreementBillingPlanDto>>(cancellationToken);

    public async Task<AgreementBillingPlanDto> SavePlanAsync(Guid contractId, SaveAgreementBillingPlanRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/contracts/{contractId}/agreement", request, cancellationToken)).ReadLedgerAsync<AgreementBillingPlanDto>(cancellationToken);

    public async Task RemovePlanAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/contracts/{contractId}/agreement", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<AgreementMilestoneDto>> ListMilestonesAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/contracts/{contractId}/agreement/milestones", cancellationToken)).ReadLedgerAsync<List<AgreementMilestoneDto>>(cancellationToken);

    public async Task<AgreementMilestoneDto> CreateMilestoneAsync(Guid contractId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/contracts/{contractId}/agreement/milestones", request, cancellationToken)).ReadLedgerAsync<AgreementMilestoneDto>(cancellationToken);

    public async Task<AgreementMilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/agreement-milestones/{milestoneId}", request, cancellationToken)).ReadLedgerAsync<AgreementMilestoneDto>(cancellationToken);

    public async Task<AgreementMilestoneCompletionDto> CompleteMilestoneAsync(Guid milestoneId, CompleteAgreementMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/agreement-milestones/{milestoneId}/complete", request, cancellationToken)).ReadLedgerAsync<AgreementMilestoneCompletionDto>(cancellationToken);

    public async Task<AgreementMilestoneDto> ReopenMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/agreement-milestones/{milestoneId}/reopen", null, cancellationToken)).ReadLedgerAsync<AgreementMilestoneDto>(cancellationToken);

    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/agreement-milestones/{milestoneId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<DueAgreementPeriodDto>> ListDuePeriodsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/agreements/due", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<List<DueAgreementPeriodDto>>(cancellationToken);

    public async Task<GenerateDueInvoicesResultDto> GenerateDueDraftsAsync(DateOnly? asOf = null, Guid? contractId = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(LedgerUrls.With($"{Base}/agreements/due/generate", ("asOf", asOf), ("contractId", contractId)), null, cancellationToken)).ReadLedgerAsync<GenerateDueInvoicesResultDto>(cancellationToken);
}

public sealed class HttpReceivablesService(HttpClient http) : IReceivablesService
{
    private const string Base = "api/v1/billing";

    public async Task<ReceivablesAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/receivables/aging", ("asOf", asOf)), cancellationToken)).ReadLedgerAsync<ReceivablesAgingDto>(cancellationToken);

    public async Task<IReadOnlyList<CustomerCreditDto>> ListCreditsAsync(Guid? customerId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/receivables/credits", ("customerId", customerId)), cancellationToken)).ReadLedgerAsync<List<CustomerCreditDto>>(cancellationToken);

    public async Task<PaymentDto> ApplyCreditAsync(Guid paymentId, ApplyCreditRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/payments/{paymentId}/apply-credit", request, cancellationToken)).ReadLedgerAsync<PaymentDto>(cancellationToken);

    public async Task<WriteOffDto> WriteOffAsync(Guid invoiceId, WriteOffInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/invoices/{invoiceId}/write-off", request, cancellationToken)).ReadLedgerAsync<WriteOffDto>(cancellationToken);

    public async Task<WriteOffDto> ReverseWriteOffAsync(Guid writeOffId, ReverseWriteOffRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/write-offs/{writeOffId}/reverse", request, cancellationToken)).ReadLedgerAsync<WriteOffDto>(cancellationToken);

    public async Task<IReadOnlyList<WriteOffDto>> ListWriteOffsAsync(Guid? invoiceId = null, Guid? customerId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/write-offs", ("invoiceId", invoiceId), ("customerId", customerId)), cancellationToken)).ReadLedgerAsync<List<WriteOffDto>>(cancellationToken);

    public async Task<int> SendPaymentRemindersAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/receivables/reminders", null, cancellationToken)).ReadLedgerAsync<int>(cancellationToken);
}

public sealed class HttpBillingSettingsService(HttpClient http) : IBillingSettingsService
{
    private const string Base = "api/v1/billing";

    public async Task<BillingSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<BillingSettingsDto>(cancellationToken);

    public async Task<BillingSettingsDto> SaveAsync(SaveBillingSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/settings", request, cancellationToken)).ReadLedgerAsync<BillingSettingsDto>(cancellationToken);
}

public static class CommercialBillingClientRegistration
{
    public static IServiceCollection AddCommercialBillingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAgreementBillingService, HttpAgreementBillingService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IReceivablesService, HttpReceivablesService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBillingSettingsService, HttpBillingSettingsService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
