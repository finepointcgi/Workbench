using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;

namespace Workbench.ApiClient.Security;

public sealed class HttpUserAdminService(HttpClient http) : IUserAdminService
{
    public async Task<IReadOnlyList<UserAdminDto>> ListUsersAsync(string? query = null, bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/admin/users?query={Uri.EscapeDataString(query ?? "")}&includeInactive={includeInactive}", cancellationToken);
        return await response.ReadAsync<List<UserAdminDto>>(cancellationToken);
    }

    public async Task<UserAdminDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/users/{E(email)}/roles", new { Roles = roles }, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<UserAdminDto> DeactivateAsync(string email, string? reason = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/admin/users/{E(email)}/deactivate", new { Reason = reason }, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<UserAdminDto> ReactivateAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/admin/users/{E(email)}/reactivate", null, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<UserAdminDto> SignOutEverywhereAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/admin/users/{E(email)}/sign-out-everywhere", null, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<UserAdminDto> SetEmployeeLinkAsync(string email, EmployeeLinkMode mode, Guid? employeeId = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/users/{E(email)}/employee", new { Mode = mode, EmployeeId = employeeId }, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<LinkableEmployeeDto>> ListLinkableEmployeesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/linkable-employees", cancellationToken);
        return await response.ReadAsync<List<LinkableEmployeeDto>>(cancellationToken);
    }

    public async Task<UserAdminDto> ResetMfaAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/admin/users/{E(email)}/reset-mfa", null, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    public async Task<UserAdminDto> SendPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/admin/users/{E(email)}/password-reset", null, cancellationToken);
        return await response.ReadAsync<UserAdminDto>(cancellationToken);
    }

    private static string E(string email) => Uri.EscapeDataString(email);
}

public sealed class HttpInviteService(HttpClient http) : IInviteService
{
    public async Task<IReadOnlyList<InviteDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/invites", cancellationToken);
        return await response.ReadAsync<List<InviteDto>>(cancellationToken);
    }

    public async Task<InviteDto> CreateAsync(CreateInviteRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/invites", request, cancellationToken);
        return await response.ReadAsync<InviteDto>(cancellationToken);
    }

    public async Task<InviteDto> RevokeAsync(Guid inviteId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/admin/invites/{inviteId}/revoke", null, cancellationToken);
        return await response.ReadAsync<InviteDto>(cancellationToken);
    }
}

public sealed class HttpAccessReviewService(HttpClient http) : IAccessReviewService
{
    public async Task<IReadOnlyList<AccessReviewSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/access-reviews", cancellationToken);
        return await response.ReadAsync<List<AccessReviewSummaryDto>>(cancellationToken);
    }

    public async Task<AccessReviewDto> GetAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/admin/access-reviews/{reviewId}", cancellationToken);
        return await response.ReadAsync<AccessReviewDto>(cancellationToken);
    }

    public async Task<AccessReviewDto> StartAsync(string? name = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/access-reviews", new { Name = name }, cancellationToken);
        return await response.ReadAsync<AccessReviewDto>(cancellationToken);
    }

    public async Task<AccessReviewItemDto> DecideAsync(Guid reviewId, Guid itemId, AccessReviewDecisionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/access-reviews/{reviewId}/items/{itemId}", request, cancellationToken);
        return await response.ReadAsync<AccessReviewItemDto>(cancellationToken);
    }

    public async Task<AccessReviewDto> CompleteAsync(Guid reviewId, string? notes = null, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/admin/access-reviews/{reviewId}/complete", new { Notes = notes }, cancellationToken);
        return await response.ReadAsync<AccessReviewDto>(cancellationToken);
    }
}

public sealed class HttpSecurityEventService(HttpClient http) : ISecurityEventService
{
    public async Task<IReadOnlyList<SecurityEventDto>> SearchAsync(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/security-events/search", query, cancellationToken);
        return await response.ReadAsync<List<SecurityEventDto>>(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListEventTypesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/security-events/types", cancellationToken);
        return await response.ReadAsync<List<string>>(cancellationToken);
    }

    public async Task<SecurityEventExport> ExportCsvAsync(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/security-events/export", query, cancellationToken);
        return await response.ReadAsync<SecurityEventExport>(cancellationToken);
    }
}

public sealed class HttpRetentionService(HttpClient http) : IRetentionService
{
    public async Task<IReadOnlyList<RetentionPolicyDto>> ListPoliciesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/retention/policies", cancellationToken);
        return await response.ReadAsync<List<RetentionPolicyDto>>(cancellationToken);
    }

    public async Task<RetentionPolicyDto> SetPolicyAsync(string recordType, int retentionDays, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/retention/policies/{Uri.EscapeDataString(recordType)}", new { RetentionDays = retentionDays }, cancellationToken);
        return await response.ReadAsync<RetentionPolicyDto>(cancellationToken);
    }

    public async Task<RetentionReportDto> PreviewAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/retention/preview", cancellationToken);
        return await response.ReadAsync<RetentionReportDto>(cancellationToken);
    }

    public async Task<RetentionReportDto> RunNowAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/v1/admin/retention/run", null, cancellationToken);
        return await response.ReadAsync<RetentionReportDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<LegalHoldDto>> ListHoldsAsync(bool includeReleased = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/admin/retention/holds?includeReleased={includeReleased}", cancellationToken);
        return await response.ReadAsync<List<LegalHoldDto>>(cancellationToken);
    }

    public async Task<IReadOnlyList<HoldableRecordDto>> ListHoldableAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/retention/holdable", cancellationToken);
        return await response.ReadAsync<List<HoldableRecordDto>>(cancellationToken);
    }

    public async Task<LegalHoldDto> PlaceHoldAsync(PlaceLegalHoldRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/admin/retention/holds", request, cancellationToken);
        return await response.ReadAsync<LegalHoldDto>(cancellationToken);
    }

    public async Task<LegalHoldDto> ReleaseHoldAsync(Guid holdId, string reason, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/admin/retention/holds/{holdId}/release", new { Reason = reason }, cancellationToken);
        return await response.ReadAsync<LegalHoldDto>(cancellationToken);
    }

    public async Task<bool> IsOnHoldAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/admin/retention/holds/check?entityType={Uri.EscapeDataString(entityType)}&entityId={entityId}", cancellationToken);
        return await response.ReadAsync<bool>(cancellationToken);
    }
}

public static class SecurityAdminClients
{
    public static IServiceCollection AddSecurityAdminClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IUserAdminService, HttpUserAdminService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IInviteService, HttpInviteService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAccessReviewService, HttpAccessReviewService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISecurityEventService, HttpSecurityEventService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IRetentionService, HttpRetentionService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
