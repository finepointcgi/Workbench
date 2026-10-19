using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Hr;

namespace Workbench.ApiClient;

public static class HrClientRegistration
{
    public static IServiceCollection AddHrClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IHrAccessService, HttpHrAccessService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IHrPersonnelService, HttpHrPersonnelService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPositionService, HttpPositionService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICompensationService, HttpCompensationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBenefitService, HttpBenefitService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBenefitDeductionService, HttpBenefitDeductionService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IOnboardingService, HttpOnboardingService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IHrReportService, HttpHrReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

internal static class HrHttp
{
    public const string Base = "api/v1/hr";

    public static async Task<T> GetAsync<T>(this HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await response.ReadAsync<T>(ct);
    }

    public static async Task<T> PostAsync<T>(this HttpClient http, string url, object? body, CancellationToken ct)
    {
        using var response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsJsonAsync(url, body, ct);
        return await response.ReadAsync<T>(ct);
    }

    public static async Task<T> PutAsync<T>(this HttpClient http, string url, object body, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync(url, body, ct);
        return await response.ReadAsync<T>(ct);
    }

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd");
}

public sealed class HttpHrAccessService(HttpClient http) : IHrAccessService
{
    public Task<HrAccessDto> GetAsync(CancellationToken cancellationToken = default) => http.GetAsync<HrAccessDto>($"{HrHttp.Base}/access", cancellationToken);
}

public sealed class HttpHrPersonnelService(HttpClient http) : IHrPersonnelService
{
    private const string Base = HrHttp.Base;

    public Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(EmployeeQuery query, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<EmployeeSummaryDto>>($"{Base}/employees?search={Uri.EscapeDataString(query.Search ?? "")}&includeTerminated={(query.IncludeTerminated ? "true" : "false")}"
            + (query.OrgUnitId is { } unit ? $"&orgUnitId={unit}" : ""), cancellationToken);

    public Task<EmployeeRecordDto> GetAsync(Guid employeeId, CancellationToken cancellationToken = default) => http.GetAsync<EmployeeRecordDto>($"{Base}/employees/{employeeId}", cancellationToken);

    public Task<EmployeeRecordDto> GetMineAsync(CancellationToken cancellationToken = default) => http.GetAsync<EmployeeRecordDto>($"{Base}/employees/me", cancellationToken);

    public Task<EmployeeRecordDto> SaveJobAsync(Guid employeeId, SaveJobInfoRequest request, CancellationToken cancellationToken = default) =>
        http.PutAsync<EmployeeRecordDto>($"{Base}/employees/{employeeId}/job", request, cancellationToken);

    public Task<EmployeeRecordDto> SavePersonalAsync(Guid employeeId, SavePersonalInfoRequest request, CancellationToken cancellationToken = default) =>
        http.PutAsync<EmployeeRecordDto>($"{Base}/employees/{employeeId}/personal", request, cancellationToken);

    public Task<EmployeeRecordDto> SaveEmergencyContactsAsync(Guid employeeId, IReadOnlyList<SaveEmergencyContact> contacts, CancellationToken cancellationToken = default) =>
        http.PutAsync<EmployeeRecordDto>($"{Base}/employees/{employeeId}/emergency-contacts", contacts, cancellationToken);

    public Task<EeoRecordDto> GetEeoAsync(Guid employeeId, CancellationToken cancellationToken = default) => http.GetAsync<EeoRecordDto>($"{Base}/employees/{employeeId}/eeo", cancellationToken);

    public Task<EeoRecordDto> SaveEeoAsync(Guid employeeId, SaveEeoRequest request, CancellationToken cancellationToken = default) =>
        http.PutAsync<EeoRecordDto>($"{Base}/employees/{employeeId}/eeo", request, cancellationToken);

    public Task<CredentialDto> SaveCredentialAsync(Guid employeeId, Guid? credentialId, SaveCredentialRequest request, CancellationToken cancellationToken = default) =>
        credentialId is { } id
            ? http.PutAsync<CredentialDto>($"{Base}/employees/{employeeId}/credentials/{id}", request, cancellationToken)
            : http.PostAsync<CredentialDto>($"{Base}/employees/{employeeId}/credentials", request, cancellationToken);

    public async Task DeleteCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"{Base}/credentials/{credentialId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CredentialDto>> ListExpiringCredentialsAsync(int withinDays, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<CredentialDto>>($"{Base}/credentials/expiring?days={withinDays}", cancellationToken);

    public async Task<EmployeeDocumentDto> UploadDocumentAsync(Guid employeeId, string title, string? category, bool employeeVisible, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(AttachmentRules.ContentTypeFor(fileName));
        form.Add(file, "file", fileName);
        form.Add(new StringContent(title ?? ""), "title");
        form.Add(new StringContent(category ?? ""), "category");
        form.Add(new StringContent(employeeVisible ? "true" : "false"), "employeeVisible");
        using var response = await http.PostAsync($"{Base}/employees/{employeeId}/documents", form, cancellationToken);
        return await response.ReadAsync<EmployeeDocumentDto>(cancellationToken);
    }

    public async Task<DocumentContent> OpenDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/documents/{documentId}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var headers = response.Content.Headers;
        var fileName = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? documentId.ToString();
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        return new DocumentContent(fileName, headers.ContentType?.MediaType ?? AttachmentRules.ContentTypeFor(fileName), buffer);
    }

    public Task<HrChangeRequestDto> RequestChangeAsync(SubmitHrChangeRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrChangeRequestDto>($"{Base}/requests", request, cancellationToken);

    public Task<IReadOnlyList<HrChangeRequestDto>> ListChangeRequestsAsync(bool pendingOnly = true, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<HrChangeRequestDto>>($"{Base}/requests?pendingOnly={(pendingOnly ? "true" : "false")}", cancellationToken);

    public Task<HrChangeRequestDto> ApproveChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrChangeRequestDto>($"{Base}/requests/{requestId}/approve", request, cancellationToken);

    public Task<HrChangeRequestDto> RejectChangeAsync(Guid requestId, HrDecisionRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrChangeRequestDto>($"{Base}/requests/{requestId}/reject", request, cancellationToken);

    public Task<HrChangeRequestDto> CancelChangeAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrChangeRequestDto>($"{Base}/requests/{requestId}/cancel", null, cancellationToken);

    public Task<IReadOnlyList<OrgChartNodeDto>> GetOrgChartAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<OrgChartNodeDto>>($"{Base}/org-chart", cancellationToken);
}

public sealed class HttpPositionService(HttpClient http) : IPositionService
{
    public Task<IReadOnlyList<PositionDto>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<PositionDto>>($"{HrHttp.Base}/positions?includeClosed={(includeClosed ? "true" : "false")}", cancellationToken);

    public Task<PositionDto> GetAsync(Guid positionId, CancellationToken cancellationToken = default) => http.GetAsync<PositionDto>($"{HrHttp.Base}/positions/{positionId}", cancellationToken);

    public Task<PositionDto> CreateAsync(SavePositionRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<PositionDto>($"{HrHttp.Base}/positions", request, cancellationToken);

    public Task<PositionDto> UpdateAsync(Guid positionId, SavePositionRequest request, CancellationToken cancellationToken = default) =>
        http.PutAsync<PositionDto>($"{HrHttp.Base}/positions/{positionId}", request, cancellationToken);
}

public sealed class HttpCompensationService(HttpClient http) : ICompensationService
{
    private const string Base = HrHttp.Base;

    public Task<CompensationHistoryDto> GetHistoryAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        http.GetAsync<CompensationHistoryDto>($"{Base}/compensation/employees/{employeeId}", cancellationToken);

    public Task<IReadOnlyList<CompensationChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<CompensationChangeDto>>($"{Base}/compensation/pending", cancellationToken);

    public Task<CompensationChangeDto> ProposeAsync(Guid employeeId, ProposeCompensationRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<CompensationChangeDto>($"{Base}/compensation/employees/{employeeId}", request, cancellationToken);

    public Task<CompensationChangeDto> ApproveAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<CompensationChangeDto>($"{Base}/compensation/{changeId}/approve", request, cancellationToken);

    public Task<CompensationChangeDto> RejectAsync(Guid changeId, CompensationDecisionRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<CompensationChangeDto>($"{Base}/compensation/{changeId}/reject", request, cancellationToken);

    public Task<CompensationChangeDto> CancelAsync(Guid changeId, CancellationToken cancellationToken = default) =>
        http.PostAsync<CompensationChangeDto>($"{Base}/compensation/{changeId}/cancel", null, cancellationToken);

    public Task<IReadOnlyList<MeritCycleDto>> ListCyclesAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<MeritCycleDto>>($"{Base}/merit-cycles", cancellationToken);

    public Task<MeritCycleDto> GetCycleAsync(Guid cycleId, CancellationToken cancellationToken = default) => http.GetAsync<MeritCycleDto>($"{Base}/merit-cycles/{cycleId}", cancellationToken);

    public Task<MeritCycleDto> CreateCycleAsync(SaveMeritCycleRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<MeritCycleDto>($"{Base}/merit-cycles", request, cancellationToken);

    public Task<MeritCycleDto> AddProposalsAsync(Guid cycleId, AddMeritProposalsRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<MeritCycleDto>($"{Base}/merit-cycles/{cycleId}/proposals", request, cancellationToken);

    public Task<MeritCycleDto> SubmitCycleAsync(Guid cycleId, CancellationToken cancellationToken = default) =>
        http.PostAsync<MeritCycleDto>($"{Base}/merit-cycles/{cycleId}/submit", null, cancellationToken);
}

public sealed class HttpBenefitService(HttpClient http) : IBenefitService
{
    private const string Base = HrHttp.Base;

    public Task<IReadOnlyList<BenefitPlanDto>> ListPlansAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<BenefitPlanDto>>($"{Base}/benefit-plans?includeInactive={(includeInactive ? "true" : "false")}", cancellationToken);

    public Task<BenefitPlanDto> SavePlanAsync(Guid? planId, SaveBenefitPlanRequest request, CancellationToken cancellationToken = default) =>
        planId is { } id ? http.PutAsync<BenefitPlanDto>($"{Base}/benefit-plans/{id}", request, cancellationToken) : http.PostAsync<BenefitPlanDto>($"{Base}/benefit-plans", request, cancellationToken);

    public Task<IReadOnlyList<BenefitAccountOptionDto>> ListExpenseAccountsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<BenefitAccountOptionDto>>($"{Base}/benefit-accounts", cancellationToken);

    public Task<BenefitPlanDto> SetPlanAccountAsync(Guid planId, Guid? expenseAccountId, CancellationToken cancellationToken = default) =>
        http.PutAsync<BenefitPlanDto>($"{Base}/benefit-plans/{planId}/account", new SetPlanAccountRequest(expenseAccountId), cancellationToken);

    public Task<IReadOnlyList<OpenEnrollmentWindowDto>> ListWindowsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<OpenEnrollmentWindowDto>>($"{Base}/enrollment-windows", cancellationToken);

    public Task<OpenEnrollmentWindowDto> SaveWindowAsync(Guid? windowId, SaveOpenEnrollmentWindowRequest request, CancellationToken cancellationToken = default) =>
        windowId is { } id
            ? http.PutAsync<OpenEnrollmentWindowDto>($"{Base}/enrollment-windows/{id}", request, cancellationToken)
            : http.PostAsync<OpenEnrollmentWindowDto>($"{Base}/enrollment-windows", request, cancellationToken);

    public Task<EmployeeBenefitsDto> GetMineAsync(CancellationToken cancellationToken = default) => http.GetAsync<EmployeeBenefitsDto>($"{Base}/benefits/me", cancellationToken);

    public Task<EmployeeBenefitsDto> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default) =>
        http.GetAsync<EmployeeBenefitsDto>($"{Base}/benefits/employees/{employeeId}", cancellationToken);

    public Task<DependentDto> SaveDependentAsync(Guid employeeId, Guid? dependentId, SaveDependentRequest request, CancellationToken cancellationToken = default) =>
        dependentId is { } id
            ? http.PutAsync<DependentDto>($"{Base}/benefits/employees/{employeeId}/dependents/{id}", request, cancellationToken)
            : http.PostAsync<DependentDto>($"{Base}/benefits/employees/{employeeId}/dependents", request, cancellationToken);

    public Task<ElectionDto> ElectAsync(Guid employeeId, ElectBenefitRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<ElectionDto>($"{Base}/benefits/employees/{employeeId}/elections", request, cancellationToken);

    public Task<LifeEventDto> ReportLifeEventAsync(Guid employeeId, ReportLifeEventRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<LifeEventDto>($"{Base}/benefits/employees/{employeeId}/life-events", request, cancellationToken);

    public Task<IReadOnlyList<LifeEventDto>> ListPendingLifeEventsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<LifeEventDto>>($"{Base}/life-events/pending", cancellationToken);

    public Task<LifeEventDto> DecideLifeEventAsync(Guid lifeEventId, DecideLifeEventRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<LifeEventDto>($"{Base}/life-events/{lifeEventId}/decide", request, cancellationToken);
}

public sealed class HttpBenefitDeductionService(HttpClient http) : IBenefitDeductionService
{
    public Task<IReadOnlyList<DeductionPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<DeductionPeriodDto>>($"{HrHttp.Base}/deductions/periods", cancellationToken);

    public Task<DeductionPreviewDto> PreviewAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default) =>
        http.GetAsync<DeductionPreviewDto>($"{HrHttp.Base}/deductions/{payGroupId}/{payPeriodId}", cancellationToken);

    public Task<DeductionFileDto> ExportAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default) =>
        http.PostAsync<DeductionFileDto>($"{HrHttp.Base}/deductions/{payGroupId}/{payPeriodId}/export", null, cancellationToken);
}

public sealed class HttpOnboardingService(HttpClient http) : IOnboardingService
{
    private const string Base = HrHttp.Base;

    public Task<IReadOnlyList<ChecklistTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<ChecklistTemplateDto>>($"{Base}/checklist-templates", cancellationToken);

    public Task<ChecklistTemplateDto> SaveTemplateAsync(Guid? templateId, SaveChecklistTemplateRequest request, CancellationToken cancellationToken = default) =>
        templateId is { } id
            ? http.PutAsync<ChecklistTemplateDto>($"{Base}/checklist-templates/{id}", request, cancellationToken)
            : http.PostAsync<ChecklistTemplateDto>($"{Base}/checklist-templates", request, cancellationToken);

    public Task<EmployeeChecklistDto> StartOnboardingAsync(StartOnboardingRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<EmployeeChecklistDto>($"{Base}/checklists/onboarding", request, cancellationToken);

    public Task<EmployeeChecklistDto> StartOffboardingAsync(StartOffboardingRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<EmployeeChecklistDto>($"{Base}/checklists/offboarding", request, cancellationToken);

    public Task<IReadOnlyList<EmployeeChecklistDto>> ListAsync(bool openOnly = true, CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<EmployeeChecklistDto>>($"{Base}/checklists?openOnly={(openOnly ? "true" : "false")}", cancellationToken);

    public Task<EmployeeChecklistDto> GetAsync(Guid checklistId, CancellationToken cancellationToken = default) =>
        http.GetAsync<EmployeeChecklistDto>($"{Base}/checklists/{checklistId}", cancellationToken);

    public Task<IReadOnlyList<MyChecklistTaskDto>> ListMyTasksAsync(CancellationToken cancellationToken = default) =>
        http.GetAsync<IReadOnlyList<MyChecklistTaskDto>>($"{Base}/checklists/mine", cancellationToken);

    public Task<EmployeeChecklistDto> CompleteTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<EmployeeChecklistDto>($"{Base}/checklist-tasks/{taskId}/complete", request, cancellationToken);

    public Task<EmployeeChecklistDto> SkipTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default) =>
        http.PostAsync<EmployeeChecklistDto>($"{Base}/checklist-tasks/{taskId}/skip", request, cancellationToken);
}

public sealed class HttpHrReportService(HttpClient http) : IHrReportService
{
    private const string Base = HrHttp.Base + "/reports";

    public Task<HeadcountReportDto> GetHeadcountAsync(DateOnly asOf, CancellationToken cancellationToken = default) =>
        http.GetAsync<HeadcountReportDto>($"{Base}/headcount?asOf={HrHttp.Date(asOf)}", cancellationToken);

    public Task<TurnoverReportDto> GetTurnoverAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        http.GetAsync<TurnoverReportDto>($"{Base}/turnover?from={HrHttp.Date(from)}&to={HrHttp.Date(to)}", cancellationToken);

    public Task<Eeo1ReportDto> GetEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default) =>
        http.GetAsync<Eeo1ReportDto>($"{Base}/eeo1?asOf={HrHttp.Date(snapshotDate)}", cancellationToken);

    public Task<HrExportFile> ExportEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrExportFile>($"{Base}/eeo1/export?asOf={HrHttp.Date(snapshotDate)}", null, cancellationToken);

    public Task<Vets4212ReportDto> GetVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        http.GetAsync<Vets4212ReportDto>($"{Base}/vets4212?periodEnd={HrHttp.Date(periodEnd)}", cancellationToken);

    public Task<HrExportFile> ExportVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrExportFile>($"{Base}/vets4212/export?periodEnd={HrHttp.Date(periodEnd)}", null, cancellationToken);

    public Task<BenefitSummaryDto> GetBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default) =>
        http.GetAsync<BenefitSummaryDto>($"{Base}/benefits?asOf={HrHttp.Date(asOf)}", cancellationToken);

    public Task<HrExportFile> ExportBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default) =>
        http.PostAsync<HrExportFile>($"{Base}/benefits/export?asOf={HrHttp.Date(asOf)}", null, cancellationToken);
}
