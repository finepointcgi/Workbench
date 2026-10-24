using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Financials;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Resourcing;

namespace Workbench.ApiClient;

public sealed class HttpPerDiemRateService(HttpClient http) : IPerDiemRateService
{
    private const string Root = "api/v1/per-diem";

    public async Task<PerDiemRateTableDto> GetTableAsync(PerDiemRateQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new PerDiemRateQuery();
        var url = Query.With($"{Root}/rates", ("fiscalYear", query.FiscalYear?.ToString(CultureInfo.InvariantCulture)),
            ("kind", query.Kind is { } k ? ((int)k).ToString(CultureInfo.InvariantCulture) : null), ("search", query.Search),
            ("take", query.Take.ToString(CultureInfo.InvariantCulture)));
        return await (await http.GetAsync(url, cancellationToken)).ReadAsync<PerDiemRateTableDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<PerDiemLocationDto>> ListLocationsAsync(int fiscalYear, PerDiemTable? kind = null, string? search = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/locations", ("fiscalYear", fiscalYear.ToString(CultureInfo.InvariantCulture)),
            ("kind", kind is { } k ? ((int)k).ToString(CultureInfo.InvariantCulture) : null), ("search", search)), cancellationToken))
            .ReadAsync<List<PerDiemLocationDto>>(cancellationToken);

    public async Task<ResolvedPerDiemRateDto> LookupAsync(PerDiemLocationDto location, DateOnly date, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/lookup", new PerDiemLookupRequest(location, date), cancellationToken)).ReadAsync<ResolvedPerDiemRateDto>(cancellationToken);

    public async Task<PerDiemImportResultDto> ImportFileAsync(ImportPerDiemFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/imports/file", request, cancellationToken)).ReadAsync<PerDiemImportResultDto>(cancellationToken);

    public async Task<PerDiemImportResultDto> ImportFromGsaApiAsync(int fiscalYear, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/imports/gsa/{fiscalYear}", null, cancellationToken)).ReadAsync<PerDiemImportResultDto>(cancellationToken);

    public async Task<PerDiemImportResultDto> InstallSampleRatesAsync(int fiscalYear, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/imports/sample/{fiscalYear}", null, cancellationToken)).ReadAsync<PerDiemImportResultDto>(cancellationToken);
}

public sealed class HttpTravelPerDiemService(HttpClient http) : ITravelPerDiemService
{
    private static string Url(Guid reportId) => $"api/v1/expense-reports/{reportId}/per-diem";

    public async Task<TravelPerDiemDto> GetAsync(Guid expenseReportId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url(expenseReportId), cancellationToken)).ReadConflictAwareAsync<TravelPerDiemDto>(cancellationToken);

    public async Task<TravelPerDiemDto> SaveAsync(Guid expenseReportId, SaveTravelPerDiemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync(Url(expenseReportId), request, cancellationToken)).ReadConflictAwareAsync<TravelPerDiemDto>(cancellationToken);

    public async Task<TravelPerDiemDto> ApplyToReportAsync(Guid expenseReportId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Url(expenseReportId)}/apply", null, cancellationToken)).ReadConflictAwareAsync<TravelPerDiemDto>(cancellationToken);

    public async Task RemoveAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(Url(expenseReportId), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        await response.EnsureOkAsync(cancellationToken);
    }
}

public sealed class HttpResourceAllocationService(HttpClient http) : IResourceAllocationService
{
    private const string Root = "api/v1/resourcing";

    public async Task<IReadOnlyList<AllocationDto>> ListAsync(AllocationQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/allocations", ("from", Date(query.From)), ("to", Date(query.To)),
            ("projectId", query.ProjectId?.ToString()), ("employeeId", query.EmployeeId?.ToString())), cancellationToken))
            .ReadAsync<List<AllocationDto>>(cancellationToken);

    public async Task<IReadOnlyList<AllocationDto>> ListMineAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/allocations/mine", ("from", Date(from)), ("to", Date(to))), cancellationToken))
            .ReadAsync<List<AllocationDto>>(cancellationToken);

    public async Task<IReadOnlyList<AllocationDto>> CreateAsync(SaveAllocationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/allocations", request, cancellationToken)).ReadAsync<List<AllocationDto>>(cancellationToken);

    public async Task<AllocationDto> UpdateAsync(Guid allocationId, SaveAllocationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/allocations/{allocationId}", request, cancellationToken)).ReadAsync<AllocationDto>(cancellationToken);

    public async Task DeleteAsync(Guid allocationId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Root}/allocations/{allocationId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<GenerateAllocationsResultDto> GenerateFromPlanAsync(Guid projectId, GenerateAllocationsRequest? request = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/projects/{projectId}/generate-from-plan", request ?? new GenerateAllocationsRequest(), cancellationToken))
            .ReadAsync<GenerateAllocationsResultDto>(cancellationToken);

    public async Task<bool> CanManageAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/can-manage", cancellationToken)).ReadAsync<bool>(cancellationToken);

    internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed class HttpResourcePlanningService(HttpClient http) : IResourcePlanningService
{
    private const string Root = "api/v1/resourcing";

    public async Task<IReadOnlyList<CapacityWeekDto>> GetCapacityAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? employeeIds = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/capacity", new CapacityRequest(from, to, employeeIds), cancellationToken)).ReadAsync<List<CapacityWeekDto>>(cancellationToken);

    public async Task<HeatMapDto> GetHeatMapAsync(HeatMapQuery query, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/heat-map", query, cancellationToken)).ReadAsync<HeatMapDto>(cancellationToken);

    public async Task<ProjectStaffingDto> GetProjectStaffingAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/staffing", cancellationToken)).ReadAsync<ProjectStaffingDto>(cancellationToken);

    public async Task<UtilizationDto> GetUtilizationAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/utilization", ("from", HttpResourceAllocationService.Date(from)), ("to", HttpResourceAllocationService.Date(to))), cancellationToken))
            .ReadAsync<UtilizationDto>(cancellationToken);

    public async Task<IReadOnlyList<OpenDemandDto>> ListOpenDemandAsync(Guid? projectId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/open-demand", ("projectId", projectId?.ToString())), cancellationToken)).ReadAsync<List<OpenDemandDto>>(cancellationToken);

    public async Task<IReadOnlyList<AvailablePersonDto>> FindAvailablePeopleAsync(FindPeopleQuery query, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/find-people", query, cancellationToken)).ReadAsync<List<AvailablePersonDto>>(cancellationToken);
}

public sealed class HttpAllocationForecastService(HttpClient http) : IAllocationForecastService
{
    public async Task<ProjectFinancialSummaryDto> GetProjectSummaryAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"api/v1/projects/{projectId}/financials/allocation-etc", ("budgetVersionId", budgetVersionId?.ToString())), cancellationToken))
            .ReadAsync<ProjectFinancialSummaryDto>(cancellationToken);

    public async Task<IReadOnlyList<WbsFinancialRowDto>> GetWbsFinancialsAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"api/v1/projects/{projectId}/financials/allocation-etc/wbs", ("budgetVersionId", budgetVersionId?.ToString())), cancellationToken))
            .ReadAsync<List<WbsFinancialRowDto>>(cancellationToken);
}

file static class Query
{
    public static string With(string path, params (string Name, string? Value)[] values)
    {
        var parts = values.Where(v => !string.IsNullOrEmpty(v.Value)).Select(v => $"{v.Name}={Uri.EscapeDataString(v.Value!)}").ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    /// <summary>Maps 409 (a submitted or approved report) to <see cref="InvalidOperationException"/>, like the in-process service.</summary>
    public static async Task<T> ReadConflictAwareAsync<T>(this HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(ct));
        }

        return await response.ReadAsync<T>(ct);
    }
}

public sealed class HttpProjectStaffingService(HttpClient http) : IProjectStaffingService
{
    private const string Root = "api/v1/resourcing";

    public async Task<ProjectStaffingPlanDto> GetPlanAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/staffing-plan", cancellationToken)).ReadAsync<ProjectStaffingPlanDto>(cancellationToken);

    public async Task<ProjectSetupProgressDto> GetSetupProgressAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/setup-progress", cancellationToken)).ReadAsync<ProjectSetupProgressDto>(cancellationToken);

    public async Task<StaffingCheckDto> CheckAsync(StaffingCheckRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/staffing-check", request, cancellationToken)).ReadAsync<StaffingCheckDto>(cancellationToken);

    public async Task<IReadOnlyList<StaffingCandidateDto>> ListCandidatesAsync(Guid projectId, Guid? laborCategoryId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/projects/{projectId}/staffing-candidates", ("laborCategoryId", laborCategoryId?.ToString())), cancellationToken))
            .ReadAsync<List<StaffingCandidateDto>>(cancellationToken);
}

public sealed class HttpStaffingAssignmentService(HttpClient http) : IStaffingAssignmentService
{
    private const string Root = "api/v1/resourcing";

    public async Task<ProjectAssignmentsDto> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/assignments", cancellationToken)).ReadAsync<ProjectAssignmentsDto>(cancellationToken);

    public async Task<AssignmentPreviewDto> PreviewAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/assignments/preview", request, cancellationToken)).ReadAsync<AssignmentPreviewDto>(cancellationToken);

    public async Task<StaffingAssignmentDto> SaveAsync(SaveAssignmentRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/assignments", request, cancellationToken)).ReadAsync<StaffingAssignmentDto>(cancellationToken);

    public async Task<IReadOnlyList<StaffingAssignmentDto>> SaveManyAsync(Guid projectId, IReadOnlyList<SaveAssignmentRequest> requests, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/projects/{projectId}/assignments/bulk", new SaveAssignmentsRequest(requests), cancellationToken))
            .ReadAsync<List<StaffingAssignmentDto>>(cancellationToken);

    public async Task DeleteAsync(Guid assignmentId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Root}/assignments/{assignmentId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<StaffingAssignmentDto> SetWeekAsync(Guid assignmentId, DateOnly weekStart, decimal hours, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/assignments/{assignmentId}/week", new SetAssignmentWeekRequest(weekStart, hours), cancellationToken))
            .ReadAsync<StaffingAssignmentDto>(cancellationToken);

    public async Task<AssignmentPastePreviewDto> PreviewPasteAsync(Guid projectId, string text, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/projects/{projectId}/assignments/paste-preview", new AssignmentPasteRequest(text), cancellationToken))
            .ReadAsync<AssignmentPastePreviewDto>(cancellationToken);

    public async Task<AssignmentPastePreviewDto> PreviewCopyTeamAsync(Guid projectId, Guid fromProjectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/projects/{projectId}/assignments/copy-preview", ("fromProjectId", fromProjectId.ToString())), cancellationToken))
            .ReadAsync<AssignmentPastePreviewDto>(cancellationToken);

    public async Task<IReadOnlyList<AssignmentHoursLeftDto>> ListMyHoursLeftAsync(DateOnly from, DateOnly to, Guid? excludeTimesheetId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Query.With($"{Root}/assignments/mine/hours-left", ("from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("to", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("excludeTimesheetId", excludeTimesheetId?.ToString())), cancellationToken))
            .ReadAsync<List<AssignmentHoursLeftDto>>(cancellationToken);
}

public sealed class HttpResourcePlanService(HttpClient http) : IResourcePlanService
{
    private const string Root = "api/v1/resourcing";

    public async Task<ResourcePlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/resource-plan", cancellationToken)).ReadAsync<ResourcePlanDto>(cancellationToken);

    public async Task<PlanTryDto> TryAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/projects/{projectId}/resource-plan/try", new PlanChangesRequest(changes), cancellationToken)).ReadAsync<PlanTryDto>(cancellationToken);

    public async Task<PlanAppliedDto> ApplyAsync(Guid projectId, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/projects/{projectId}/resource-plan/apply", new PlanChangesRequest(changes), cancellationToken)).ReadAsync<PlanAppliedDto>(cancellationToken);

    public async Task<IReadOnlyList<OverbookingFixDto>> ListFixesAsync(Guid projectId, Guid assignmentId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/projects/{projectId}/resource-plan/fixes/{assignmentId}", cancellationToken)).ReadAsync<List<OverbookingFixDto>>(cancellationToken);
}

public static class PerDiemResourcingClientRegistration
{
    public static IServiceCollection AddPerDiemResourcingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPerDiemRateService, HttpPerDiemRateService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ITravelPerDiemService, HttpTravelPerDiemService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IResourceAllocationService, HttpResourceAllocationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IResourcePlanningService, HttpResourcePlanningService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAllocationForecastService, HttpAllocationForecastService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectStaffingService, HttpProjectStaffingService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IStaffingAssignmentService, HttpStaffingAssignmentService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IResourcePlanService, HttpResourcePlanService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
