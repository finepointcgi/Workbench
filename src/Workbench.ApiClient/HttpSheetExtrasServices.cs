using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Forms;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetLinkService(HttpClient http) : ISheetLinkService
{
    public async Task<SheetLinksDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/links", cancellationToken);
        return await response.ReadAsync<SheetLinksDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<LinkableSheetDto>> ListSheetsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/sheets/linkable", cancellationToken);
        return await response.ReadAsync<List<LinkableSheetDto>>(cancellationToken);
    }
}

public sealed class HttpSheetFormulaService(HttpClient http) : ISheetFormulaService
{
    public async Task<IReadOnlyList<SheetReferenceDto>> ListReferencesAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/references", cancellationToken);
        return await response.ReadAsync<List<SheetReferenceDto>>(cancellationToken);
    }

    public async Task<SheetReferenceDto> CreateReferenceAsync(Guid sheetId, CreateSheetReferenceRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/references", request, cancellationToken);
        return await response.ReadAsync<SheetReferenceDto>(cancellationToken);
    }

    public async Task DeleteReferenceAsync(Guid sheetId, Guid referenceId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/references/{referenceId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<FormulaCheckDto> CheckAsync(Guid sheetId, string formula, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/formula-check", new { formula }, cancellationToken);
        return await response.ReadAsync<FormulaCheckDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<FormulaFunctionDto>> ListFunctionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/sheets/formula-functions", cancellationToken);
        return await response.ReadAsync<List<FormulaFunctionDto>>(cancellationToken);
    }
}

public sealed class HttpSheetTemplateService(HttpClient http) : ISheetTemplateService
{
    public async Task<IReadOnlyList<SheetTemplateDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/sheets/templates", cancellationToken);
        return await response.ReadAsync<List<SheetTemplateDto>>(cancellationToken);
    }

    public async Task<SheetSummary> CreateSheetAsync(CreateSheetFromTemplateRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/sheets/templates/create-sheet", request, cancellationToken);
        return await response.ReadAsync<SheetSummary>(cancellationToken);
    }

    public async Task<SheetTemplateDto> SaveAsTemplateAsync(Guid sheetId, SaveSheetTemplateRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/save-as-template", request, cancellationToken);
        return await response.ReadAsync<SheetTemplateDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/templates/{templateId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<SheetSummary> CopyAsync(Guid sheetId, CopySheetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/copy", request, cancellationToken);
        return await response.ReadAsync<SheetSummary>(cancellationToken);
    }
}

public sealed class HttpSheetReportService(HttpClient http) : ISheetReportService
{
    public async Task<IReadOnlyList<SheetReportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/sheets/reports", cancellationToken);
        return await response.ReadAsync<List<SheetReportDto>>(cancellationToken);
    }

    public async Task<SheetReportDto?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/reports/{reportId}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.ReadAsync<SheetReportDto>(cancellationToken);
    }

    public async Task<SheetReportDto> CreateAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/sheets/reports", request, cancellationToken);
        return await response.ReadAsync<SheetReportDto>(cancellationToken);
    }

    public async Task<SheetReportDto> UpdateAsync(Guid reportId, SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/reports/{reportId}", request, cancellationToken);
        return await response.ReadAsync<SheetReportDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/reports/{reportId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<SheetReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/reports/{reportId}/run", cancellationToken);
        return await response.ReadAsync<SheetReportResultDto>(cancellationToken);
    }

    public async Task<SheetReportResultDto> PreviewAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/sheets/reports/preview", request, cancellationToken);
        return await response.ReadAsync<SheetReportResultDto>(cancellationToken);
    }
}

public sealed class HttpFormService(HttpClient http) : IFormService
{
    public async Task<IReadOnlyList<FormDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/forms", cancellationToken);
        return await response.ReadAsync<List<FormDto>>(cancellationToken);
    }

    public async Task<FormDto> CreateAsync(Guid sheetId, SaveFormRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/forms", request, cancellationToken);
        return await response.ReadAsync<FormDto>(cancellationToken);
    }

    public async Task<bool> CanPublishAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/forms/can-publish", cancellationToken);
        return await response.ReadAsync<bool>(cancellationToken);
    }

    public async Task<FormDto> UpdateAsync(Guid formId, SaveFormRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/forms/{formId}", request, cancellationToken);
        return await response.ReadAsync<FormDto>(cancellationToken);
    }

    public async Task<FormDto> ResetPublicLinkAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/sheets/forms/{formId}/reset-link", null, cancellationToken);
        return await response.ReadAsync<FormDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/forms/{formId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<FillFormDto> GetForFillingAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/forms/{formId}/fill", cancellationToken);
        return await response.ReadAsync<FillFormDto>(cancellationToken);
    }

    public async Task<FormSubmittedDto> SubmitAsync(Guid formId, SubmitFormRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/forms/{formId}/submit", request, cancellationToken);
        return await response.ReadAsync<FormSubmittedDto>(cancellationToken);
    }
}

public static class SheetExtrasClientRegistration
{
    public static IServiceCollection AddSheetExtrasClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ISheetLinkService, HttpSheetLinkService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetFormulaService, HttpSheetFormulaService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetTemplateService, HttpSheetTemplateService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetReportService, HttpSheetReportService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IFormService, HttpFormService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
