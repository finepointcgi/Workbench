using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Customization;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

// Customization: administrators define fields, lists, status labels and numbering; everyone signed in reads the
// definitions. A record's custom values follow the record: whoever can open it reads them (sensitive ones masked
// unless ICustomFieldAccessPolicy allows), and its module's managing roles, or the employee who owns it (timesheets,
// expense reports) or manages it (projects), change them. Lists, exports and filters across records need the managing roles.

public static class CustomizationPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchCustomizationPermissions(this IServiceCollection services)
    {
        services.AddScoped<CustomizationAccess>();
        services.Decorate<ICustomFieldService, PermissionCheckingCustomFieldService>();
        services.Decorate<ICustomListService, PermissionCheckingCustomListService>();
        services.Decorate<INumberingService, PermissionCheckingNumberingService>();
        return services;
    }
}

/// <summary>Record-level checks shared by the customization decorators.</summary>
public sealed class CustomizationAccess(AccessControl access, CustomizableEntityRegistry registry, IServiceProvider services, IWorkbenchDbContextFactory dbFactory)
{
    /// <summary>Records checked one by one for people without the managing roles; more are left out.</summary>
    public const int PerRecordLimit = 200;

    public AccessControl Access => access;

    public CustomizableEntity Entity(string entityType) => registry.Get(entityType);

    public async Task<bool> ManagesAsync(CustomizableEntity entity, CancellationToken ct) => entity.ManageRoles(await access.GetOrgRolesAsync(ct));

    public async Task<bool> CanReadAsync(CustomizableEntity entity, Guid id, CancellationToken ct)
    {
        try
        {
            return await entity.CanReadAsync(services, id, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task EnsureReadAsync(CustomizableEntity entity, Guid id, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!await CanReadAsync(entity, id, ct))
        {
            throw WorkbenchNotFoundException.For(entity.Label, id);
        }
    }

    public async Task EnsureEditAsync(CustomizableEntity entity, Guid id, CancellationToken ct)
    {
        await EnsureReadAsync(entity, id, ct);
        if (await ManagesAsync(entity, ct))
        {
            return;
        }

        if (await access.GetEmployeeIdAsync(ct) is { } me)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            if (await entity.OwnerEmployeeAsync(db, id, ct) == me)
            {
                return;
            }
        }

        await access.RequireRoleAsync(_ => false, $"You can't change this {entity.Label.ToLowerInvariant()}'s details.", ct);
    }

    public async Task EnsureManagesAsync(CustomizableEntity entity, CancellationToken ct) =>
        await access.RequireRoleAsync(entity.ManageRoles, $"You can't work with every {entity.Label.ToLowerInvariant()}'s details.", ct);

    /// <summary>The ids the user may read: all for managers, else checked one by one (up to <see cref="PerRecordLimit"/>).</summary>
    public async Task<List<Guid>> VisibleAsync(CustomizableEntity entity, IEnumerable<Guid> ids, CancellationToken ct)
    {
        if (await ManagesAsync(entity, ct))
        {
            return ids.ToList();
        }

        var visible = new List<Guid>();
        foreach (var id in ids.Distinct().Take(PerRecordLimit))
        {
            if (await CanReadAsync(entity, id, ct))
            {
                visible.Add(id);
            }
        }

        return visible;
    }
}

public sealed class PermissionCheckingCustomFieldService(ICustomFieldService inner, CustomizationAccess check) : ICustomFieldService
{
    public Task<IReadOnlyList<CustomizableEntityDto>> ListEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        return inner.ListEntityTypesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(string entityType, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        return inner.ListDefinitionsAsync(entityType, includeInactive, cancellationToken);
    }

    public async Task<CustomFieldDefinitionDto> SaveDefinitionAsync(Guid? id, SaveCustomFieldRequest request, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.SaveDefinitionAsync(id, request, cancellationToken);
    }

    public async Task<CustomFieldDefinitionDto> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.SetActiveAsync(id, isActive, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReorderAsync(string entityType, IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.ReorderAsync(entityType, orderedIds, cancellationToken);
    }

    public async Task<RecordCustomFieldsDto> GetValuesAsync(string entityType, Guid? recordId, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        if (recordId is { } id)
        {
            await check.EnsureReadAsync(check.Entity(entityType), id, cancellationToken);
        }

        return await inner.GetValuesAsync(entityType, recordId, cancellationToken);
    }

    public async Task<RecordCustomFieldsDto> SaveValuesAsync(string entityType, Guid recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default)
    {
        await check.EnsureEditAsync(check.Entity(entityType), recordId, cancellationToken);
        return await inner.SaveValuesAsync(entityType, recordId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ValidateAsync(string entityType, Guid? recordId, SaveCustomFieldValuesRequest request, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        if (recordId is { } id)
        {
            await check.EnsureReadAsync(check.Entity(entityType), id, cancellationToken);
        }

        return await inner.ValidateAsync(entityType, recordId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FilterAsync(string entityType, IReadOnlyList<CustomFieldFilter> filters, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        var entity = check.Entity(entityType);
        return await check.VisibleAsync(entity, await inner.FilterAsync(entityType, filters, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<CustomFieldSearchHitDto>> SearchAsync(string text, string? entityType = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        var hits = await inner.SearchAsync(text, entityType, limit, cancellationToken);
        var result = new List<CustomFieldSearchHitDto>();
        foreach (var group in hits.GroupBy(h => h.EntityType))
        {
            var visible = (await check.VisibleAsync(check.Entity(group.Key), group.Select(h => h.RecordId), cancellationToken)).ToHashSet();
            result.AddRange(group.Where(h => visible.Contains(h.RecordId)));
        }

        return result;
    }

    public async Task<CustomFieldTableDto> GetTableAsync(string entityType, IReadOnlyList<Guid>? recordIds = null, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        var entity = check.Entity(entityType);
        if (recordIds is null)
        {
            await check.EnsureManagesAsync(entity, cancellationToken);
            return await inner.GetTableAsync(entityType, null, cancellationToken);
        }

        return await inner.GetTableAsync(entityType, await check.VisibleAsync(entity, recordIds, cancellationToken), cancellationToken);
    }

    public async Task<CustomFieldExportDto> ExportCsvAsync(string entityType, CancellationToken cancellationToken = default)
    {
        await check.EnsureManagesAsync(check.Entity(entityType), cancellationToken);
        return await inner.ExportCsvAsync(entityType, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomFieldOptionDto>> LookupRecordsAsync(string entityType, string? text, int limit = 20, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        var entity = check.Entity(entityType);
        var found = await inner.LookupRecordsAsync(entityType, text, Math.Clamp(limit, 1, 200) * 3, cancellationToken);
        var visible = (await check.VisibleAsync(entity, found.Select(f => Guid.Parse(f.Code)), cancellationToken)).ToHashSet();
        return found.Where(f => visible.Contains(Guid.Parse(f.Code))).Take(Math.Clamp(limit, 1, 200)).ToList();
    }

    public async Task CopyValuesAsync(string entityType, Guid fromRecordId, Guid toRecordId, CancellationToken cancellationToken = default)
    {
        var entity = check.Entity(entityType);
        await check.EnsureReadAsync(entity, fromRecordId, cancellationToken);
        await check.EnsureEditAsync(entity, toRecordId, cancellationToken);
        await inner.CopyValuesAsync(entityType, fromRecordId, toRecordId, cancellationToken);
    }
}

public sealed class PermissionCheckingCustomListService(ICustomListService inner, CustomizationAccess check) : ICustomListService
{
    public Task<IReadOnlyList<ValueListDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        return inner.ListAsync(cancellationToken);
    }

    public Task<ValueListDto> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        return inner.GetAsync(key, cancellationToken);
    }

    public async Task<ValueListDto> SaveAsync(SaveValueListRequest request, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        await inner.DeleteAsync(key, cancellationToken);
    }

    public Task<IReadOnlyList<StatusSetDto>> ListStatusSetsAsync(CancellationToken cancellationToken = default)
    {
        check.Access.RequireSignedIn();
        return inner.ListStatusSetsAsync(cancellationToken);
    }

    public async Task<StatusSetDto> SaveStatusSetAsync(string setKey, SaveStatusSetRequest request, CancellationToken cancellationToken = default)
    {
        await check.Access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.SaveStatusSetAsync(setKey, request, cancellationToken);
    }

    public async Task<RecordSubStatusDto> GetSubStatusAsync(string setKey, Guid recordId, CancellationToken cancellationToken = default)
    {
        if (StatusSetCatalog.Get(setKey).EntityType is { } entityType)
        {
            await check.EnsureReadAsync(check.Entity(entityType), recordId, cancellationToken);
        }

        return await inner.GetSubStatusAsync(setKey, recordId, cancellationToken);
    }

    public async Task<RecordSubStatusDto> SetSubStatusAsync(string setKey, Guid recordId, string? code, CancellationToken cancellationToken = default)
    {
        if (StatusSetCatalog.Get(setKey).EntityType is { } entityType)
        {
            await check.EnsureEditAsync(check.Entity(entityType), recordId, cancellationToken);
        }

        return await inner.SetSubStatusAsync(setKey, recordId, code, cancellationToken);
    }
}

public sealed class PermissionCheckingNumberingService(INumberingService inner, AccessControl access) : INumberingService
{
    public Task<IReadOnlyList<NumberingSchemeDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAsync(cancellationToken);
    }

    public async Task<NumberingSchemeDto> SaveAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.SaveAsync(documentType, request, cancellationToken);
    }

    public async Task<NumberingSchemeDto> ResetAsync(string documentType, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.ResetAsync(documentType, cancellationToken);
    }

    public async Task<NumberingPreviewDto> PreviewAsync(string documentType, SaveNumberingSchemeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminCustomization, "manage customization", cancellationToken);
        return await inner.PreviewAsync(documentType, request, cancellationToken);
    }
}
