using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Approvals;

namespace Workbench.ApiClient;

public sealed class HttpApprovalInboxService(HttpClient http) : IApprovalInboxService
{
    private const string Root = "api/v1/approvals";

    public async Task<IReadOnlyList<ApprovalInboxItemDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/inbox", cancellationToken)).ReadAsync<List<ApprovalInboxItemDto>>(cancellationToken);

    public async Task<int> CountMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/inbox/count", cancellationToken)).ReadAsync<int>(cancellationToken);

    public async Task DecideAsync(string documentType, Guid documentId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{Uri.EscapeDataString(documentType)}/{documentId}/decision", request, cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<BulkApprovalResultDto> DecideManyAsync(BulkApprovalRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/decisions", request, cancellationToken)).ReadAsync<BulkApprovalResultDto>(cancellationToken);

    public async Task<IReadOnlyList<ApprovalInstanceDto>> GetHistoryAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{Uri.EscapeDataString(documentType)}/{documentId}/history", cancellationToken)).ReadAsync<List<ApprovalInstanceDto>>(cancellationToken);
}

/// <summary>The token goes in the body, never the URL, so it stays out of request logs.</summary>
public sealed class HttpApprovalEmailLinkService(HttpClient http) : IApprovalEmailLinkService
{
    private const string Root = "api/v1/approvals/email-links";

    public async Task<ApprovalEmailLinkDto> OpenAsync(string token, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/open", new ConfirmApprovalEmailLinkRequest(token), cancellationToken)).ReadAsync<ApprovalEmailLinkDto>(cancellationToken);

    public async Task<ApprovalEmailLinkDto> ConfirmAsync(ConfirmApprovalEmailLinkRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/confirm", request, cancellationToken)).ReadAsync<ApprovalEmailLinkDto>(cancellationToken);
}

public sealed class HttpApprovalDelegationService(HttpClient http) : IApprovalDelegationService
{
    private const string Root = "api/v1/approval-delegations";

    public async Task<IReadOnlyList<ApprovalDelegationDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Root, cancellationToken)).ReadAsync<List<ApprovalDelegationDto>>(cancellationToken);

    public async Task<ApprovalDelegationDto> CreateAsync(SaveApprovalDelegationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Root, request, cancellationToken)).ReadAsync<ApprovalDelegationDto>(cancellationToken);

    public async Task<ApprovalDelegationDto> RevokeAsync(Guid delegationId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{delegationId}/revoke", null, cancellationToken)).ReadAsync<ApprovalDelegationDto>(cancellationToken);
}

public sealed class HttpApprovalWorkflowService(HttpClient http) : IApprovalWorkflowService
{
    private const string Root = "api/v1/approval-workflows";

    public async Task<IReadOnlyList<ApprovalWorkflowSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Root, cancellationToken)).ReadAsync<List<ApprovalWorkflowSummaryDto>>(cancellationToken);

    public async Task<ApprovalWorkflowDto> GetAsync(string documentType, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{Uri.EscapeDataString(documentType)}", cancellationToken)).ReadAsync<ApprovalWorkflowDto>(cancellationToken);

    public async Task<ApprovalWorkflowDefinition> GetVersionAsync(string documentType, int version, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{Uri.EscapeDataString(documentType)}/versions/{version}", cancellationToken)).ReadAsync<ApprovalWorkflowDefinition>(cancellationToken);

    public async Task<ApprovalWorkflowDto> SaveAsync(string documentType, SaveApprovalWorkflowRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{Uri.EscapeDataString(documentType)}", request, cancellationToken)).ReadAsync<ApprovalWorkflowDto>(cancellationToken);

    public async Task<ApprovalWorkflowDto> ResetToDefaultAsync(string documentType, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{Uri.EscapeDataString(documentType)}/reset", null, cancellationToken)).ReadAsync<ApprovalWorkflowDto>(cancellationToken);

    public async Task<ApprovalWorkflowDto> CreateRecordWorkflowAsync(CreateRecordApprovalWorkflowRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/record", request, cancellationToken)).ReadAsync<ApprovalWorkflowDto>(cancellationToken);

    public async Task<IReadOnlyList<OrgUnitApproverDto>> ListOrgUnitApproversAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/org-unit-approvers", cancellationToken)).ReadAsync<List<OrgUnitApproverDto>>(cancellationToken);

    public async Task<OrgUnitApproverDto> SetOrgUnitApproverAsync(Guid orgUnitId, Guid? employeeId, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/org-unit-approvers/{orgUnitId}", new SetOrgUnitApproverRequest(employeeId), cancellationToken)).ReadAsync<OrgUnitApproverDto>(cancellationToken);
}

public static class ApprovalClientRegistration
{
    public static IServiceCollection AddApprovalClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IApprovalInboxService, HttpApprovalInboxService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApprovalEmailLinkService, HttpApprovalEmailLinkService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApprovalDelegationService, HttpApprovalDelegationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApprovalWorkflowService, HttpApprovalWorkflowService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
