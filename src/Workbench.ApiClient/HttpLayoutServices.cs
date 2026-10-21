using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Reports;

namespace Workbench.ApiClient;

public sealed class HttpCustomizationAccess(HttpClient http) : ICustomizationAccess
{
    public async Task<CustomizationCaller> GetCallerAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/customization/me", ct);
        return await response.ReadAsync<CustomizationCaller>(ct);
    }

    public async Task<IReadOnlyList<CustomizationRole>> ListRolesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/customization/roles", ct);
        return await response.ReadAsync<List<CustomizationRole>>(ct);
    }

    public async Task<IReadOnlySet<string>> GrantedAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/customization/granted", new GrantedQuery(keys.ToList()), ct);
        return (await response.ReadAsync<List<string>>(ct)).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> GrantedToRoleAsync(string roleKey, IEnumerable<string> keys, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/customization/roles/{Uri.EscapeDataString(roleKey)}/granted", new GrantedQuery(keys.ToList()), ct);
        return (await response.ReadAsync<List<string>>(ct)).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>The field catalog over HTTP. <see cref="EntityTypes"/> is the known list (the server's may add more).</summary>
public sealed class HttpEntityFieldCatalog(HttpClient http) : IEntityFieldCatalog
{
    public IReadOnlyList<string> EntityTypes { get; } = typeof(CustomizableEntities).GetFields()
        .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

    public async Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/entities/{Uri.EscapeDataString(entityType)}/fields", ct);
        return await response.ReadAsync<List<EntityFieldDto>>(ct);
    }

    public async Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Path(entityType, recordId, fieldKey), ct);
        var value = (await response.ReadAsync<FieldValue>(ct)).Value;
        return value is JsonElement { ValueKind: JsonValueKind.Null } ? null : value;
    }

    public async Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(Path(entityType, recordId, fieldKey), new FieldValue(value), ct);
        await response.EnsureOkAsync(ct);
    }

    private static string Path(string entityType, Guid recordId, string fieldKey) =>
        $"api/v1/customization/entities/{Uri.EscapeDataString(entityType)}/records/{recordId}/fields/{Uri.EscapeDataString(fieldKey)}";
}

public sealed class HttpFormLayoutService(HttpClient http) : IFormLayoutService
{
    public async Task<IReadOnlyList<FormEntityTypeDto>> ListEntityTypesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/customization/forms", ct);
        return await response.ReadAsync<List<FormEntityTypeDto>>(ct);
    }

    public async Task<IReadOnlyList<EntityFieldDto>> ListFieldsAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/forms/{Uri.EscapeDataString(entityType)}/fields", ct);
        return await response.ReadAsync<List<EntityFieldDto>>(ct);
    }

    public async Task<FormLayoutDto> GetBuiltInAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/forms/{Uri.EscapeDataString(entityType)}/built-in", ct);
        return await response.ReadAsync<FormLayoutDto>(ct);
    }

    public async Task<IReadOnlyList<FormLayoutDto>> ListAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/forms/{Uri.EscapeDataString(entityType)}/layouts", ct);
        return await response.ReadAsync<List<FormLayoutDto>>(ct);
    }

    public async Task<EffectiveFormLayout> GetEffectiveAsync(string entityType, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/forms/{Uri.EscapeDataString(entityType)}/effective", ct);
        return await response.ReadAsync<EffectiveFormLayout>(ct);
    }

    public async Task<EffectiveFormLayout> PreviewAsync(PreviewFormLayoutRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/customization/forms/preview", request, ct);
        return await response.ReadAsync<EffectiveFormLayout>(ct);
    }

    public async Task<FormLayoutDto> SaveAsync(SaveFormLayoutRequest request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/customization/forms/layouts", request, ct);
        return await response.ReadAsync<FormLayoutDto>(ct);
    }

    public async Task DeleteAsync(Guid layoutId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/v1/customization/forms/layouts/{layoutId}", ct);
        await response.EnsureOkAsync(ct);
    }
}

public sealed class HttpListViewService(HttpClient http) : IListViewService
{
    public async Task<ListViewsDto> ListAsync(string listKey, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/views/lists/{Uri.EscapeDataString(listKey)}", ct);
        return await response.ReadAsync<ListViewsDto>(ct);
    }

    public async Task<IReadOnlyList<SavedListViewDto>> ListAllAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/customization/views", ct);
        return await response.ReadAsync<List<SavedListViewDto>>(ct);
    }

    public async Task<SavedListViewDto> GetAsync(Guid viewId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/customization/views/{viewId}", ct);
        return await response.ReadAsync<SavedListViewDto>(ct);
    }

    public async Task<SavedListViewDto> CreateAsync(SaveListViewRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/customization/views", request, ct);
        return await response.ReadAsync<SavedListViewDto>(ct);
    }

    public async Task<SavedListViewDto> UpdateAsync(Guid viewId, SaveListViewRequest request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/customization/views/{viewId}", request, ct);
        return await response.ReadAsync<SavedListViewDto>(ct);
    }

    public async Task DeleteAsync(Guid viewId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/v1/customization/views/{viewId}", ct);
        await response.EnsureOkAsync(ct);
    }

    public async Task SetDefaultAsync(string listKey, SetDefaultListViewRequest request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/customization/views/lists/{Uri.EscapeDataString(listKey)}/default", request, ct);
        await response.EnsureOkAsync(ct);
    }

    public async Task<ReportFileDto> ExportAsync(ListExportRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/customization/views/export", request, ct);
        return await response.ReadAsync<ReportFileDto>(ct);
    }
}

public sealed class HttpHomePageService(HttpClient http) : IHomePageService
{
    private const string Base = "api/v1/customization/home";

    public async Task<HomePageDto> GetMineAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync(Base, ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task<HomePageDto> SavePreferencesAsync(SaveHomePreferencesRequest request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Base}/preferences", request, ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task<HomePageDto> ResetPreferencesAsync(CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Base}/preferences", ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task<WidgetDataDto> GetWidgetDataAsync(Guid widgetId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Base}/widgets/{widgetId}/data", ct);
        return await response.ReadAsync<WidgetDataDto>(ct);
    }

    public async Task<IReadOnlyList<RoleHomePageSummary>> ListRoleHomePagesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Base}/roles", ct);
        return await response.ReadAsync<List<RoleHomePageSummary>>(ct);
    }

    public async Task<HomePageDto> GetRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}", ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task<HomePageDto> PreviewAsync(string roleKey, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}/preview", ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task<HomePageDto> CreateRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}", null, ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }

    public async Task DeleteRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}", ct);
        await response.EnsureOkAsync(ct);
    }

    public async Task<WidgetDto> AddWidgetAsync(string roleKey, SaveWidgetRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}/widgets", request, ct);
        return await response.ReadAsync<WidgetDto>(ct);
    }

    public async Task<WidgetDto> UpdateWidgetAsync(string roleKey, Guid widgetId, SaveWidgetRequest request, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}/widgets/{widgetId}", request, ct);
        return await response.ReadAsync<WidgetDto>(ct);
    }

    public async Task DeleteWidgetAsync(string roleKey, Guid widgetId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}/widgets/{widgetId}", ct);
        await response.EnsureOkAsync(ct);
    }

    public async Task<HomePageDto> ReorderWidgetsAsync(string roleKey, ReorderWidgetsRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"{Base}/roles/{Uri.EscapeDataString(roleKey)}/widgets/reorder", request, ct);
        return await response.ReadAsync<HomePageDto>(ct);
    }
}

public static class LayoutClientRegistration
{
    public static IServiceCollection AddLayoutClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICustomizationAccess, HttpCustomizationAccess>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IEntityFieldCatalog, HttpEntityFieldCatalog>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IFormLayoutService, HttpFormLayoutService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IListViewService, HttpListViewService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IHomePageService, HttpHomePageService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}
