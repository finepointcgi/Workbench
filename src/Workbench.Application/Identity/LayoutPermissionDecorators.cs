using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Reports;

namespace Workbench.Application.Identity;

// Screen customization. Everyone signed in reads the layout of a form, their own list views and home page. Changing form
// layouts and role home pages takes CustomizationPermissions.ManageLayouts; sharing a list view with a role or everyone
// (or changing a shared one) takes CustomizationPermissions.ShareViews. Both resolve through ICustomizationAccess, which is
// Admin only until the permission catalog decides them.

public sealed class PermissionCheckingFormLayoutService(IFormLayoutService inner, ICustomizationAccess customization, AccessControl access) : IFormLayoutService
{
    public Task<IReadOnlyList<FormEntityTypeDto>> ListEntityTypesAsync(CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ListEntityTypesAsync(ct);
    }

    public Task<IReadOnlyList<EntityFieldDto>> ListFieldsAsync(string entityType, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ListFieldsAsync(entityType, ct);
    }

    public Task<FormLayoutDto> GetBuiltInAsync(string entityType, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.GetBuiltInAsync(entityType, ct);
    }

    public async Task<IReadOnlyList<FormLayoutDto>> ListAsync(string entityType, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.ListAsync(entityType, ct);
    }

    public Task<EffectiveFormLayout> GetEffectiveAsync(string entityType, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.GetEffectiveAsync(entityType, ct);
    }

    public async Task<EffectiveFormLayout> PreviewAsync(PreviewFormLayoutRequest request, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.PreviewAsync(request, ct);
    }

    public async Task<FormLayoutDto> SaveAsync(SaveFormLayoutRequest request, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.SaveAsync(request, ct);
    }

    public async Task DeleteAsync(Guid layoutId, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        await inner.DeleteAsync(layoutId, ct);
    }
}

public sealed class PermissionCheckingListViewService(IListViewService inner, ICustomizationAccess customization, AccessControl access) : IListViewService
{
    public Task<ListViewsDto> ListAsync(string listKey, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ListAsync(listKey, ct);
    }

    public Task<IReadOnlyList<SavedListViewDto>> ListAllAsync(CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ListAllAsync(ct);
    }

    public Task<SavedListViewDto> GetAsync(Guid viewId, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.GetAsync(viewId, ct);
    }

    public async Task<SavedListViewDto> CreateAsync(SaveListViewRequest request, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        await RequireShareAsync(request, ct);
        return await inner.CreateAsync(request, ct);
    }

    public async Task<SavedListViewDto> UpdateAsync(Guid viewId, SaveListViewRequest request, CancellationToken ct = default)
    {
        await RequireEditAsync(viewId, ct);
        await RequireShareAsync(request, ct);
        return await inner.UpdateAsync(viewId, request, ct);
    }

    public async Task DeleteAsync(Guid viewId, CancellationToken ct = default)
    {
        await RequireEditAsync(viewId, ct);
        await inner.DeleteAsync(viewId, ct);
    }

    public Task SetDefaultAsync(string listKey, SetDefaultListViewRequest request, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.SetDefaultAsync(listKey, request, ct);
    }

    public Task<ReportFileDto> ExportAsync(ListExportRequest request, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ExportAsync(request, ct);
    }

    private async Task RequireShareAsync(SaveListViewRequest request, CancellationToken ct)
    {
        if (request?.Visibility is ListViewVisibility.Role or ListViewVisibility.Everyone && !(await customization.GetCallerAsync(ct)).CanShareViews)
        {
            throw await access.DenyAsync("You can save views for yourself; sharing them with a role or everyone is for administrators.", ct);
        }
    }

    private async Task RequireEditAsync(Guid viewId, CancellationToken ct)
    {
        access.RequireSignedIn();
        var view = await inner.GetAsync(viewId, ct);
        if (!view.CanEdit)
        {
            throw await access.DenyAsync(view.IsMine
                ? "Only those who may share views can change a shared view."
                : "Only its owner (or, for a shared view, those who may share views) can change this view.", ct);
        }
    }
}

public sealed class PermissionCheckingHomePageService(IHomePageService inner, ICustomizationAccess customization, AccessControl access) : IHomePageService
{
    public Task<HomePageDto> GetMineAsync(CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.GetMineAsync(ct);
    }

    public Task<HomePageDto> SavePreferencesAsync(SaveHomePreferencesRequest request, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.SavePreferencesAsync(request, ct);
    }

    public Task<HomePageDto> ResetPreferencesAsync(CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.ResetPreferencesAsync(ct);
    }

    public Task<WidgetDataDto> GetWidgetDataAsync(Guid widgetId, CancellationToken ct = default)
    {
        access.RequireSignedIn();
        return inner.GetWidgetDataAsync(widgetId, ct);
    }

    public async Task<IReadOnlyList<RoleHomePageSummary>> ListRoleHomePagesAsync(CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.ListRoleHomePagesAsync(ct);
    }

    public async Task<HomePageDto> GetRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.GetRoleHomePageAsync(roleKey, ct);
    }

    public async Task<HomePageDto> PreviewAsync(string roleKey, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.PreviewAsync(roleKey, ct);
    }

    public async Task<HomePageDto> CreateRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.CreateRoleHomePageAsync(roleKey, ct);
    }

    public async Task DeleteRoleHomePageAsync(string roleKey, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        await inner.DeleteRoleHomePageAsync(roleKey, ct);
    }

    public async Task<WidgetDto> AddWidgetAsync(string roleKey, SaveWidgetRequest request, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.AddWidgetAsync(roleKey, request, ct);
    }

    public async Task<WidgetDto> UpdateWidgetAsync(string roleKey, Guid widgetId, SaveWidgetRequest request, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.UpdateWidgetAsync(roleKey, widgetId, request, ct);
    }

    public async Task DeleteWidgetAsync(string roleKey, Guid widgetId, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        await inner.DeleteWidgetAsync(roleKey, widgetId, ct);
    }

    public async Task<HomePageDto> ReorderWidgetsAsync(string roleKey, ReorderWidgetsRequest request, CancellationToken ct = default)
    {
        await CustomizationGate.RequireManageAsync(customization, access, ct);
        return await inner.ReorderWidgetsAsync(roleKey, request, ct);
    }
}

internal static class CustomizationGate
{
    public static async Task RequireManageAsync(ICustomizationAccess customization, AccessControl access, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!(await customization.GetCallerAsync(ct)).CanManageLayouts)
        {
            throw await access.DenyAsync("Changing layouts and home pages is for administrators.", ct);
        }
    }

    public static IServiceCollection AddWorkbenchLayoutPermissions(this IServiceCollection services)
    {
        services.Decorate<IFormLayoutService, PermissionCheckingFormLayoutService>();
        services.Decorate<IListViewService, PermissionCheckingListViewService>();
        services.Decorate<IHomePageService, PermissionCheckingHomePageService>();
        return services;
    }
}
