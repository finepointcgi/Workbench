using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Customization;

namespace Workbench.Api;

internal static class CustomizationEndpoints
{
    public static void MapCustomizationEndpoints(this RouteGroupBuilder v1)
    {
        var c = v1.MapGroup("/customization").WithTags("Customization").AddEndpointFilter(MapConflicts);

        c.MapGet("/entity-types", (ICustomFieldService s, CancellationToken ct) => s.ListEntityTypesAsync(ct));
        c.MapGet("/fields/{entityType}", (string entityType, bool? includeInactive, ICustomFieldService s, CancellationToken ct) => s.ListDefinitionsAsync(entityType, includeInactive ?? false, ct));
        c.MapPost("/fields", (SaveCustomFieldRequest r, ICustomFieldService s, CancellationToken ct) => s.SaveDefinitionAsync(null, r, ct));
        c.MapPut("/fields/{id:guid}", (Guid id, SaveCustomFieldRequest r, ICustomFieldService s, CancellationToken ct) => s.SaveDefinitionAsync(id, r, ct));
        c.MapPut("/fields/{id:guid}/active", (Guid id, bool isActive, ICustomFieldService s, CancellationToken ct) => s.SetActiveAsync(id, isActive, ct));
        c.MapPut("/fields/{entityType}/order", (string entityType, List<Guid> ids, ICustomFieldService s, CancellationToken ct) => s.ReorderAsync(entityType, ids, ct));

        c.MapGet("/values/{entityType}/new", (string entityType, ICustomFieldService s, CancellationToken ct) => s.GetValuesAsync(entityType, null, ct));
        c.MapGet("/values/{entityType}/{recordId:guid}", (string entityType, Guid recordId, ICustomFieldService s, CancellationToken ct) => s.GetValuesAsync(entityType, recordId, ct));
        c.MapPut("/values/{entityType}/{recordId:guid}", (string entityType, Guid recordId, SaveCustomFieldValuesRequest r, ICustomFieldService s, CancellationToken ct) =>
            s.SaveValuesAsync(entityType, recordId, r, ct));
        c.MapPost("/values/{entityType}/validate", (string entityType, Guid? recordId, SaveCustomFieldValuesRequest r, ICustomFieldService s, CancellationToken ct) =>
            s.ValidateAsync(entityType, recordId, r, ct));
        c.MapPost("/values/{entityType}/filter", (string entityType, List<CustomFieldFilter> filters, ICustomFieldService s, CancellationToken ct) => s.FilterAsync(entityType, filters, ct));
        c.MapPost("/values/{entityType}/table", (string entityType, CustomFieldTableRequest r, ICustomFieldService s, CancellationToken ct) => s.GetTableAsync(entityType, r.RecordIds, ct));
        c.MapGet("/values/{entityType}/export", (string entityType, ICustomFieldService s, CancellationToken ct) => s.ExportCsvAsync(entityType, ct));
        c.MapGet("/values/{entityType}/export.csv", async (string entityType, ICustomFieldService s, CancellationToken ct) =>
        {
            var file = await s.ExportCsvAsync(entityType, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        c.MapPost("/values/{entityType}/copy", async (string entityType, Guid from, Guid to, ICustomFieldService s, CancellationToken ct) =>
        {
            await s.CopyValuesAsync(entityType, from, to, ct);
            return Results.NoContent();
        });
        c.MapGet("/records/{entityType}", (string entityType, string? text, int? limit, ICustomFieldService s, CancellationToken ct) => s.LookupRecordsAsync(entityType, text, limit ?? 20, ct));
        c.MapGet("/search", (string text, string? entityType, int? limit, ICustomFieldService s, CancellationToken ct) => s.SearchAsync(text, entityType, limit ?? 50, ct));

        c.MapGet("/lists", (ICustomListService s, CancellationToken ct) => s.ListAsync(ct));
        c.MapGet("/lists/{key}", (string key, ICustomListService s, CancellationToken ct) => s.GetAsync(key, ct));
        c.MapPut("/lists", (SaveValueListRequest r, ICustomListService s, CancellationToken ct) => s.SaveAsync(r, ct));
        c.MapDelete("/lists/{key}", async (string key, ICustomListService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(key, ct);
            return Results.NoContent();
        });
        c.MapGet("/statuses", (ICustomListService s, CancellationToken ct) => s.ListStatusSetsAsync(ct));
        c.MapPut("/statuses/{setKey}", (string setKey, SaveStatusSetRequest r, ICustomListService s, CancellationToken ct) => s.SaveStatusSetAsync(setKey, r, ct));
        c.MapGet("/statuses/{setKey}/{recordId:guid}", (string setKey, Guid recordId, ICustomListService s, CancellationToken ct) => s.GetSubStatusAsync(setKey, recordId, ct));
        c.MapPut("/statuses/{setKey}/{recordId:guid}", (string setKey, Guid recordId, SetSubStatusRequest r, ICustomListService s, CancellationToken ct) =>
            s.SetSubStatusAsync(setKey, recordId, r.Code, ct));

        c.MapGet("/numbering", (INumberingService s, CancellationToken ct) => s.ListAsync(ct));
        c.MapPut("/numbering/{documentType}", (string documentType, SaveNumberingSchemeRequest r, INumberingService s, CancellationToken ct) => s.SaveAsync(documentType, r, ct));
        c.MapDelete("/numbering/{documentType}", (string documentType, INumberingService s, CancellationToken ct) => s.ResetAsync(documentType, ct));
        c.MapPost("/numbering/{documentType}/preview", (string documentType, SaveNumberingSchemeRequest r, INumberingService s, CancellationToken ct) => s.PreviewAsync(documentType, r, ct));

        c.MapGet("/catalog", (IEntityFieldCatalog s) => s.EntityTypes);
        c.MapGet("/catalog/{entityType}", (string entityType, IEntityFieldCatalog s, CancellationToken ct) => s.GetFieldsAsync(entityType, ct));
    }

    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
