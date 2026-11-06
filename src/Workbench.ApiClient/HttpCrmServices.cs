using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Crm;

namespace Workbench.ApiClient;

public sealed class HttpOpportunityService(HttpClient http) : IOpportunityService
{
    private const string Base = "api/v1/crm/opportunities";

    public async Task<OpportunityDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{id}", cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<IReadOnlyList<OpportunityDto>> ListAsync(OpportunityFilter? filter = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/search", filter ?? new OpportunityFilter(), cancellationToken)).ReadAsync<List<OpportunityDto>>(cancellationToken);

    public async Task<OpportunityDto> CreateAsync(SaveOpportunityRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<OpportunityDto> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{id}", request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<OpportunityDto> ChangeStageAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/stage", request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<OpportunityDto> RecordOutcomeAsync(Guid id, RecordOutcomeRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/outcome", request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<OpportunityDto> ReopenAsync(Guid id, ChangeStageRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/reopen", request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<IReadOnlyList<StageChangeDto>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{id}/history", cancellationToken)).ReadAsync<List<StageChangeDto>>(cancellationToken);

    public async Task<HandoffResultDto> HandoffAsync(Guid id, HandoffRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/handoff", request, cancellationToken)).ReadAsync<HandoffResultDto>(cancellationToken);

    public async Task<OpportunityDto> EnsureBpChargeCodeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{id}/bp-charge-code", null, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);
}

public sealed class HttpCrmAccountService(HttpClient http) : ICrmAccountService
{
    public async Task<IReadOnlyList<CrmAccountDto>> ListAccountsAsync(CrmAccountKind? kind = null, string? search = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/accounts?{Query(("kind", kind?.ToString()), ("search", search))}", cancellationToken)).ReadAsync<List<CrmAccountDto>>(cancellationToken);

    public async Task<CrmAccountDto> GetAccountAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/accounts/{id}", cancellationToken)).ReadAsync<CrmAccountDto>(cancellationToken);

    public async Task<CrmAccountDto> CreateAccountAsync(SaveCrmAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/accounts", request, cancellationToken)).ReadAsync<CrmAccountDto>(cancellationToken);

    public async Task<CrmAccountDto> UpdateAccountAsync(Guid id, SaveCrmAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/accounts/{id}", request, cancellationToken)).ReadAsync<CrmAccountDto>(cancellationToken);

    public async Task<CrmAccountDto> LinkCustomerAsync(Guid id, Guid? customerId, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/accounts/{id}/customer", new { CustomerId = customerId }, cancellationToken)).ReadAsync<CrmAccountDto>(cancellationToken);

    public async Task<IReadOnlyList<CrmContactDto>> ListContactsAsync(Guid? accountId = null, string? search = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/contacts?{Query(("accountId", accountId?.ToString()), ("search", search))}", cancellationToken)).ReadAsync<List<CrmContactDto>>(cancellationToken);

    public async Task<CrmContactDto> CreateContactAsync(SaveCrmContactRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/contacts", request, cancellationToken)).ReadAsync<CrmContactDto>(cancellationToken);

    public async Task<CrmContactDto> UpdateContactAsync(Guid id, SaveCrmContactRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/contacts/{id}", request, cancellationToken)).ReadAsync<CrmContactDto>(cancellationToken);

    public async Task<IReadOnlyList<CrmActivityDto>> ListActivitiesAsync(CrmActivityFilter filter, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/activities/search", filter, cancellationToken)).ReadAsync<List<CrmActivityDto>>(cancellationToken);

    public async Task<CrmActivityDto> LogActivityAsync(SaveCrmActivityRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/activities", request, cancellationToken)).ReadAsync<CrmActivityDto>(cancellationToken);

    public async Task<CrmActivityDto> SetTaskDoneAsync(Guid activityId, bool done, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/crm/activities/{activityId}/done?done={(done ? "true" : "false")}", null, cancellationToken)).ReadAsync<CrmActivityDto>(cancellationToken);

    internal static string Query(params (string Key, string? Value)[] parts) =>
        string.Join("&", parts.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
}

public sealed class HttpOpportunityGateService(HttpClient http) : IOpportunityGateService
{
    public async Task<IReadOnlyList<GateDefinitionDto>> ListGatesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/gates?includeInactive={(includeInactive ? "true" : "false")}", cancellationToken)).ReadAsync<List<GateDefinitionDto>>(cancellationToken);

    public async Task<GateDefinitionDto> CreateGateAsync(SaveGateDefinitionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/gates", request, cancellationToken)).ReadAsync<GateDefinitionDto>(cancellationToken);

    public async Task<GateDefinitionDto> UpdateGateAsync(Guid id, SaveGateDefinitionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/gates/{id}", request, cancellationToken)).ReadAsync<GateDefinitionDto>(cancellationToken);

    public async Task<IReadOnlyList<GateReviewDto>> ListReviewsAsync(Guid opportunityId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/opportunities/{opportunityId}/gate-reviews", cancellationToken)).ReadAsync<List<GateReviewDto>>(cancellationToken);

    public async Task<GateReviewDto> SubmitAsync(Guid opportunityId, SubmitGateReviewRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/crm/opportunities/{opportunityId}/gate-reviews", request, cancellationToken)).ReadAsync<GateReviewDto>(cancellationToken);

    public async Task<GateReviewDto> ApproveAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/crm/gate-reviews/{reviewId}/approve", request, cancellationToken)).ReadAsync<GateReviewDto>(cancellationToken);

    public async Task<GateReviewDto> RejectAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/crm/gate-reviews/{reviewId}/reject", request, cancellationToken)).ReadAsync<GateReviewDto>(cancellationToken);

    public async Task<GateReviewDto> CancelAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/crm/gate-reviews/{reviewId}/cancel", null, cancellationToken)).ReadAsync<GateReviewDto>(cancellationToken);

    public async Task<IReadOnlyList<GateReviewDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/crm/gate-reviews/waiting", cancellationToken)).ReadAsync<List<GateReviewDto>>(cancellationToken);
}

public sealed class HttpOpportunityLeadService(HttpClient http) : IOpportunityLeadService
{
    public async Task<IReadOnlyList<SavedLeadSearchDto>> ListSearchesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/crm/lead-searches", cancellationToken)).ReadAsync<List<SavedLeadSearchDto>>(cancellationToken);

    public async Task<SavedLeadSearchDto> CreateSearchAsync(SaveLeadSearchRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/lead-searches", request, cancellationToken)).ReadAsync<SavedLeadSearchDto>(cancellationToken);

    public async Task<SavedLeadSearchDto> UpdateSearchAsync(Guid id, SaveLeadSearchRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/lead-searches/{id}", request, cancellationToken)).ReadAsync<SavedLeadSearchDto>(cancellationToken);

    public async Task DeleteSearchAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/crm/lead-searches/{id}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<LeadImportResultDto> RunSearchAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/crm/lead-searches/{id}/run", null, cancellationToken)).ReadAsync<LeadImportResultDto>(cancellationToken);

    public async Task<LeadImportResultDto> ImportCsvAsync(string csv, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/crm/leads/import", new { Csv = csv }, cancellationToken)).ReadAsync<LeadImportResultDto>(cancellationToken);

    public async Task<LeadFitResultDto> ScoreLeadsAsync(bool rescoreAll = false, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/crm/leads/score?all={(rescoreAll ? "true" : "false")}", null, cancellationToken)).ReadAsync<LeadFitResultDto>(cancellationToken);

    public async Task<IReadOnlyList<OpportunityLeadDto>> ListLeadsAsync(LeadStatus? status = LeadStatus.New, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(status is { } s ? $"api/v1/crm/leads?status={s}" : "api/v1/crm/leads?all=true", cancellationToken)).ReadAsync<List<OpportunityLeadDto>>(cancellationToken);

    public async Task<OpportunityDto> AcceptAsync(Guid leadId, AcceptLeadRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/crm/leads/{leadId}/accept", request, cancellationToken)).ReadAsync<OpportunityDto>(cancellationToken);

    public async Task<OpportunityLeadDto> DismissAsync(Guid leadId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/crm/leads/{leadId}/dismiss", null, cancellationToken)).ReadAsync<OpportunityLeadDto>(cancellationToken);
}

public sealed class HttpPipelineService(HttpClient http) : IPipelineService
{
    public async Task<PipelineBoardDto> GetBoardAsync(bool includeOutcomes = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/pipeline/board?includeOutcomes={(includeOutcomes ? "true" : "false")}", cancellationToken)).ReadAsync<PipelineBoardDto>(cancellationToken);

    public async Task<PipelineDemandDto> GetStaffingDemandAsync(DateOnly from, int weeks, decimal revenuePerHour = 150m, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/pipeline/staffing-demand?from={from:yyyy-MM-dd}&weeks={weeks}&revenuePerHour={revenuePerHour.ToString(System.Globalization.CultureInfo.InvariantCulture)}", cancellationToken))
            .ReadAsync<PipelineDemandDto>(cancellationToken);

    public async Task<ForecastDto> GetForecastAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/crm/pipeline/forecast", cancellationToken)).ReadAsync<ForecastDto>(cancellationToken);

    public async Task<BdHighlightsDto> GetHighlightsAsync(int windowDays = 30, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/pipeline/highlights?windowDays={windowDays}", cancellationToken)).ReadAsync<BdHighlightsDto>(cancellationToken);

    public async Task<WinLossDto> GetWinLossAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/pipeline/win-loss?{HttpCrmAccountService.Query(("from", from?.ToString("yyyy-MM-dd")), ("to", to?.ToString("yyyy-MM-dd")))}", cancellationToken))
            .ReadAsync<WinLossDto>(cancellationToken);

    public async Task<IReadOnlyList<BpSpendDto>> GetBpSpendAsync(Guid? opportunityId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/crm/pipeline/bp-spend?{HttpCrmAccountService.Query(("opportunityId", opportunityId?.ToString()))}", cancellationToken))
            .ReadAsync<List<BpSpendDto>>(cancellationToken);

    public async Task<CrmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/crm/pipeline/settings", cancellationToken)).ReadAsync<CrmSettingsDto>(cancellationToken);

    public async Task<CrmSettingsDto> SetCompanyProfileAsync(string? profile, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/crm/pipeline/settings/company-profile", new { Profile = profile }, cancellationToken)).ReadAsync<CrmSettingsDto>(cancellationToken);

    public async Task<CrmSettingsDto> SetBpParentAsync(Guid? wbsElementId, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/crm/pipeline/settings/bp-parent", new { WbsElementId = wbsElementId }, cancellationToken)).ReadAsync<CrmSettingsDto>(cancellationToken);

    public async Task<IReadOnlyList<BpParentOptionDto>> ListBpParentOptionsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/crm/pipeline/settings/bp-parent-options", cancellationToken)).ReadAsync<List<BpParentOptionDto>>(cancellationToken);
}

public sealed class HttpProposalService(HttpClient http) : IProposalService
{
    private const string Base = "api/v1/crm/opportunities";

    public async Task<ProposalDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{opportunityId}/proposal", cancellationToken)).ReadAsync<ProposalDto>(cancellationToken);

    public async Task<ProposalDraftResultDto> DraftAsync(Guid opportunityId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{opportunityId}/proposal/draft", null, cancellationToken)).ReadAsync<ProposalDraftResultDto>(cancellationToken);

    public async Task<ProposalSectionDto> RedraftSectionAsync(Guid sectionId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/proposal-sections/{sectionId}/redraft", null, cancellationToken)).ReadAsync<ProposalSectionDto>(cancellationToken);

    public async Task<ProposalSectionDto> SaveSectionAsync(Guid sectionId, SaveProposalSectionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/proposal-sections/{sectionId}", request, cancellationToken)).ReadAsync<ProposalSectionDto>(cancellationToken);

    public async Task<ProposalFileDto> ExportAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"{Base}/{opportunityId}/proposal/export", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "proposal.docx";
        return new ProposalFileDto(name, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }
}

public sealed class HttpCapturePlanService(HttpClient http) : ICapturePlanService
{
    private const string Base = "api/v1/crm/opportunities";

    public async Task<CapturePlanDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{opportunityId}/capture-plan", cancellationToken)).ReadAsync<CapturePlanDto>(cancellationToken);

    public async Task<PlanItemDto> AddItemAsync(Guid opportunityId, SavePlanItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{opportunityId}/capture-plan/items", request, cancellationToken)).ReadAsync<PlanItemDto>(cancellationToken);

    public async Task<PlanItemDto> UpdateItemAsync(Guid itemId, SavePlanItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/capture-plan-items/{itemId}", request, cancellationToken)).ReadAsync<PlanItemDto>(cancellationToken);

    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/crm/capture-plan-items/{itemId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<MilestoneDto> AddMilestoneAsync(Guid opportunityId, SaveMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{opportunityId}/milestones", request, cancellationToken)).ReadAsync<MilestoneDto>(cancellationToken);

    public async Task<MilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/crm/milestones/{milestoneId}", request, cancellationToken)).ReadAsync<MilestoneDto>(cancellationToken);

    public async Task<MilestoneDto> SetMilestoneDoneAsync(Guid milestoneId, bool done, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/crm/milestones/{milestoneId}/done", new { Done = done }, cancellationToken)).ReadAsync<MilestoneDto>(cancellationToken);

    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/crm/milestones/{milestoneId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<IReadOnlyList<MilestoneDto>> AddStandardScheduleAsync(Guid opportunityId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{opportunityId}/milestones/standard", null, cancellationToken)).ReadAsync<List<MilestoneDto>>(cancellationToken);

    public async Task<PwinAssessmentDto> SaveAssessmentAsync(Guid opportunityId, SavePwinAssessmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{opportunityId}/pwin-assessment", request, cancellationToken)).ReadAsync<PwinAssessmentDto>(cancellationToken);
}

public static class CrmClientRegistration
{
    public static IServiceCollection AddCrmClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IOpportunityService, HttpOpportunityService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICrmAccountService, HttpCrmAccountService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IOpportunityGateService, HttpOpportunityGateService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IOpportunityLeadService, HttpOpportunityLeadService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPipelineService, HttpPipelineService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICapturePlanService, HttpCapturePlanService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProposalService, HttpProposalService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}
