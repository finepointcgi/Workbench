using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;

namespace Workbench.Api;

internal static class LayoutEndpoints
{
    public static void MapLayoutEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/customization").WithTags("Customization");

        // Who is asking, and the field permission seam.
        group.MapGet("/me", (ICustomizationAccess access, CancellationToken ct) => access.GetCallerAsync(ct));
        group.MapGet("/roles", (ICustomizationAccess access, CancellationToken ct) => access.ListRolesAsync(ct));
        group.MapPost("/granted", async (GrantedQuery query, ICustomizationAccess access, CancellationToken ct) =>
            (await access.GrantedAsync(query.Keys ?? [], ct)).ToList());
        group.MapPost("/roles/{roleKey}/granted", async (string roleKey, GrantedQuery query, ICustomizationAccess access, IFormLayoutService layouts, CancellationToken ct) =>
        {
            await layouts.ListAsync(CustomizableEntities.Project, ct); // Managers of layouts only (the decorator checks).
            return (await access.GrantedToRoleAsync(roleKey, query.Keys ?? [], ct)).ToList();
        });

        // Field catalog.
        group.MapGet("/entities", (IEntityFieldCatalog catalog) => catalog.EntityTypes);
        group.MapGet("/entities/{entityType}/fields", (string entityType, IEntityFieldCatalog catalog, CancellationToken ct) =>
            catalog.GetFieldsAsync(entityType, ct));
        group.MapGet("/entities/{entityType}/records/{recordId:guid}/fields/{fieldKey}", async (string entityType, Guid recordId, string fieldKey, IEntityFieldCatalog catalog, CancellationToken ct) =>
            new FieldValue(await catalog.GetValueAsync(entityType, recordId, fieldKey, ct)));
        group.MapPut("/entities/{entityType}/records/{recordId:guid}/fields/{fieldKey}", async (string entityType, Guid recordId, string fieldKey, FieldValue body, IEntityFieldCatalog catalog, CancellationToken ct) =>
        {
            await catalog.SetValueAsync(entityType, recordId, fieldKey, body.Value is JsonElement e ? Unwrap(e) : body.Value, ct);
            return Results.NoContent();
        });

        // Form layouts.
        var forms = group.MapGroup("/forms");
        forms.MapGet("/", (IFormLayoutService service, CancellationToken ct) => service.ListEntityTypesAsync(ct));
        forms.MapGet("/{entityType}/fields", (string entityType, IFormLayoutService service, CancellationToken ct) => service.ListFieldsAsync(entityType, ct));
        forms.MapGet("/{entityType}/built-in", (string entityType, IFormLayoutService service, CancellationToken ct) => service.GetBuiltInAsync(entityType, ct));
        forms.MapGet("/{entityType}/layouts", (string entityType, IFormLayoutService service, CancellationToken ct) => service.ListAsync(entityType, ct));
        forms.MapGet("/{entityType}/effective", (string entityType, IFormLayoutService service, CancellationToken ct) => service.GetEffectiveAsync(entityType, ct));
        forms.MapPost("/preview", (PreviewFormLayoutRequest request, IFormLayoutService service, CancellationToken ct) => service.PreviewAsync(request, ct));
        forms.MapPut("/layouts", (SaveFormLayoutRequest request, IFormLayoutService service, CancellationToken ct) => service.SaveAsync(request, ct));
        forms.MapDelete("/layouts/{layoutId:guid}", async (Guid layoutId, IFormLayoutService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(layoutId, ct);
            return Results.NoContent();
        });

        // List views.
        var views = group.MapGroup("/views");
        views.MapGet("/", (IListViewService service, CancellationToken ct) => service.ListAllAsync(ct));
        views.MapGet("/lists/{listKey}", (string listKey, IListViewService service, CancellationToken ct) => service.ListAsync(listKey, ct));
        views.MapPut("/lists/{listKey}/default", async (string listKey, SetDefaultListViewRequest request, IListViewService service, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(listKey, request, ct);
            return Results.NoContent();
        });
        views.MapGet("/{viewId:guid}", (Guid viewId, IListViewService service, CancellationToken ct) => service.GetAsync(viewId, ct));
        views.MapPost("/", async (SaveListViewRequest request, IListViewService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/customization/views/{created.Id}", created);
        });
        views.MapPut("/{viewId:guid}", (Guid viewId, SaveListViewRequest request, IListViewService service, CancellationToken ct) => service.UpdateAsync(viewId, request, ct));
        views.MapDelete("/{viewId:guid}", async (Guid viewId, IListViewService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(viewId, ct);
            return Results.NoContent();
        });
        views.MapPost("/export", (ListExportRequest request, IListViewService service, CancellationToken ct) => service.ExportAsync(request, ct));

        // Home pages.
        var home = group.MapGroup("/home");
        home.MapGet("/", (IHomePageService service, CancellationToken ct) => service.GetMineAsync(ct));
        home.MapPut("/preferences", (SaveHomePreferencesRequest request, IHomePageService service, CancellationToken ct) => service.SavePreferencesAsync(request, ct));
        home.MapDelete("/preferences", (IHomePageService service, CancellationToken ct) => service.ResetPreferencesAsync(ct));
        home.MapGet("/widgets/{widgetId:guid}/data", (Guid widgetId, IHomePageService service, CancellationToken ct) => service.GetWidgetDataAsync(widgetId, ct));
        home.MapGet("/roles", (IHomePageService service, CancellationToken ct) => service.ListRoleHomePagesAsync(ct));
        home.MapGet("/roles/{roleKey}", (string roleKey, IHomePageService service, CancellationToken ct) => service.GetRoleHomePageAsync(roleKey, ct));
        home.MapGet("/roles/{roleKey}/preview", (string roleKey, IHomePageService service, CancellationToken ct) => service.PreviewAsync(roleKey, ct));
        home.MapPost("/roles/{roleKey}", (string roleKey, IHomePageService service, CancellationToken ct) => service.CreateRoleHomePageAsync(roleKey, ct));
        home.MapDelete("/roles/{roleKey}", async (string roleKey, IHomePageService service, CancellationToken ct) =>
        {
            await service.DeleteRoleHomePageAsync(roleKey, ct);
            return Results.NoContent();
        });
        home.MapPost("/roles/{roleKey}/widgets", (string roleKey, SaveWidgetRequest request, IHomePageService service, CancellationToken ct) =>
            service.AddWidgetAsync(roleKey, request, ct));
        home.MapPut("/roles/{roleKey}/widgets/{widgetId:guid}", (string roleKey, Guid widgetId, SaveWidgetRequest request, IHomePageService service, CancellationToken ct) =>
            service.UpdateWidgetAsync(roleKey, widgetId, request, ct));
        home.MapDelete("/roles/{roleKey}/widgets/{widgetId:guid}", async (string roleKey, Guid widgetId, IHomePageService service, CancellationToken ct) =>
        {
            await service.DeleteWidgetAsync(roleKey, widgetId, ct);
            return Results.NoContent();
        });
        home.MapPost("/roles/{roleKey}/widgets/reorder", (string roleKey, ReorderWidgetsRequest request, IHomePageService service, CancellationToken ct) =>
            service.ReorderWidgetsAsync(roleKey, request, ct));
    }

    private static object? Unwrap(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString()).ToList(),
        _ => e.ToString(),
    };
}
