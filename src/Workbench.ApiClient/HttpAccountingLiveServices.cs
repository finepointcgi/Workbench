using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;

namespace Workbench.ApiClient;

public sealed class HttpAccountingConnectionService(HttpClient http) : IAccountingConnectionService
{
    private const string Base = "api/v1/accounting-live/connections";

    public async Task<IReadOnlyList<AccountingConnectionDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<List<AccountingConnectionDto>>(cancellationToken);

    public async Task<AccountingAuthorizeStart> StartAsync(AccountingTarget target, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{target}/start", null, cancellationToken)).ReadLedgerAsync<AccountingAuthorizeStart>(cancellationToken);

    public async Task<AccountingConnectionDto> CompleteAsync(AccountingOAuthCallback callback, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/callback", callback, cancellationToken)).ReadLedgerAsync<AccountingConnectionDto>(cancellationToken);

    public async Task<AccountingConnectionDto> ChooseOrganisationAsync(AccountingTarget target, string organisationId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{target}/organisation/{Uri.EscapeDataString(organisationId)}", null, cancellationToken)).ReadLedgerAsync<AccountingConnectionDto>(cancellationToken);

    public async Task DisconnectAsync(AccountingTarget target, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/{target}", cancellationToken)).EnsureOkAsync(cancellationToken);
}

public sealed class HttpAccountingPushService(HttpClient http) : IAccountingPushService
{
    private const string Base = "api/v1/accounting-live";

    public async Task<AccountingPushPreviewDto> PreviewAsync(AccountingPushRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/preview", request, cancellationToken)).ReadLedgerAsync<AccountingPushPreviewDto>(cancellationToken);

    public async Task<AccountingPushRunDto> PushAsync(AccountingPushRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/push", request, cancellationToken)).ReadLedgerAsync<AccountingPushRunDto>(cancellationToken);

    public async Task<AccountingPushRunDto> RetryFailedAsync(Guid runId, bool createMissingContacts = false, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/runs/{runId}/retry?createMissingContacts={createMissingContacts}", null, cancellationToken)).ReadLedgerAsync<AccountingPushRunDto>(cancellationToken);

    public async Task<IReadOnlyList<AccountingPushRunDto>> ListRunsAsync(AccountingTarget? target = null, int limit = 50, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs?limit={limit}" + (target is { } t ? $"&target={t}" : ""), cancellationToken)).ReadLedgerAsync<List<AccountingPushRunDto>>(cancellationToken);

    public async Task<AccountingPushRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}", cancellationToken)).ReadLedgerAsync<AccountingPushRunDto>(cancellationToken);
}

public static class AccountingLiveClientRegistration
{
    public static IServiceCollection AddAccountingLiveClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAccountingConnectionService, HttpAccountingConnectionService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAccountingPushService, HttpAccountingPushService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
