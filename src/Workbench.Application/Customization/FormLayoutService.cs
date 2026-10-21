using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Domain.Companies;
using Workbench.Domain.Customization;

namespace Workbench.Application.Customization;

/// <summary>
/// Form layouts. Reads are for everyone (a form needs its layout); who may preview, save and delete is checked by
/// <c>PermissionCheckingFormLayoutService</c>. Fields come from <see cref="IEntityFieldCatalog"/>, so custom fields appear
/// as soon as they exist.
/// </summary>
public sealed class FormLayoutService(
    IWorkbenchDbContextFactory dbFactory,
    IEntityFieldCatalog catalog,
    ICustomizationAccess access,
    TimeProvider clock) : IFormLayoutService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<FormEntityTypeDto>> ListEntityTypesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FormEntityTypeDto>>(BuiltInForms.All.Select(f => new FormEntityTypeDto(f.EntityType, f.Name, f.ShownOn)).ToList());

    public async Task<IReadOnlyList<EntityFieldDto>> ListFieldsAsync(string entityType, CancellationToken ct = default)
    {
        BuiltInForms.Get(entityType);
        return await catalog.GetFieldsAsync(entityType, ct);
    }

    public Task<FormLayoutDto> GetBuiltInAsync(string entityType, CancellationToken ct = default) =>
        Task.FromResult(BuiltInForms.Layout(BuiltInForms.Get(entityType)));

    public async Task<IReadOnlyList<FormLayoutDto>> ListAsync(string entityType, CancellationToken ct = default)
    {
        BuiltInForms.Get(entityType);
        await using var db = await dbFactory.CreateAsync(ct);
        var rows = await db.Set<FormLayout>().AsNoTracking().Where(l => l.EntityType == entityType).ToListAsync(ct);
        return rows.OrderBy(l => l.CompanyId is not null).ThenBy(l => l.RoleKey is not null).ThenBy(l => l.RoleKey, StringComparer.Ordinal).Select(ToDto).ToList();
    }

    public async Task<EffectiveFormLayout> GetEffectiveAsync(string entityType, CancellationToken ct = default)
    {
        var caller = await access.GetCallerAsync(ct);
        var fields = await ListFieldsAsync(entityType, ct);
        var granted = await access.GrantedAsync(PermissionKeys(fields), ct);
        return await ResolveAsync(entityType, caller.CompanyId, caller.RoleKeys, fields, granted, draft: null, ct);
    }

    public async Task<EffectiveFormLayout> PreviewAsync(PreviewFormLayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = await ListFieldsAsync(request.EntityType, ct);
        if (request.Draft is { } draft)
        {
            FormLayoutEngine.Validate(draft.Sections, fields);
        }

        var granted = await access.GrantedToRoleAsync(request.RoleKey, PermissionKeys(fields), ct);
        var roles = request.RoleKey == CustomizationRoles.Everyone ? [CustomizationRoles.Everyone] : new[] { request.RoleKey, CustomizationRoles.Everyone };
        return await ResolveAsync(request.EntityType, request.CompanyId, roles, fields, granted, request.Draft, ct);
    }

    public async Task<FormLayoutDto> SaveAsync(SaveFormLayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = await ListFieldsAsync(request.EntityType, ct);
        FormLayoutEngine.Validate(request.Sections, fields);
        var roleKey = NormalizeRole(request.RoleKey);
        if (roleKey is not null && !(await access.ListRolesAsync(ct)).Any(r => r.Key == roleKey))
        {
            throw new ArgumentException($"There is no role \"{roleKey}\".", nameof(request));
        }

        await using var db = await dbFactory.CreateAsync(ct);
        if (request.CompanyId is { } companyId && !await db.Set<Company>().AnyAsync(c => c.Id == companyId, ct))
        {
            throw WorkbenchNotFoundException.For("Company", companyId);
        }

        var scope = FormLayout.ScopeKeyFor(request.CompanyId, roleKey);
        var caller = await access.GetCallerAsync(ct);
        var layout = await db.Set<FormLayout>().FirstOrDefaultAsync(l => l.EntityType == request.EntityType && l.ScopeKey == scope, ct);
        if (layout is null)
        {
            layout = new FormLayout
            {
                EntityType = request.EntityType,
                CompanyId = request.CompanyId,
                RoleKey = roleKey,
                ScopeKey = scope,
                UpdatedBy = caller.Email,
            };
            db.Set<FormLayout>().Add(layout);
        }

        var sections = request.Sections
            .Select(s => new FormSectionLayout(s.Key.Trim(), (s.Title ?? "").Trim(), s.Fields ?? []))
            .ToList();
        layout.SectionsJson = JsonSerializer.Serialize(sections, Json);
        layout.UpdatedBy = caller.Email;
        layout.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return ToDto(layout);
    }

    public async Task DeleteAsync(Guid layoutId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var layout = await db.Set<FormLayout>().FirstOrDefaultAsync(l => l.Id == layoutId, ct)
            ?? throw WorkbenchNotFoundException.For("Form layout", layoutId);
        db.Set<FormLayout>().Remove(layout);
        await db.SaveChangesAsync(ct);
    }

    private async Task<EffectiveFormLayout> ResolveAsync(
        string entityType,
        Guid? companyId,
        IReadOnlyList<string> roleKeys,
        IReadOnlyList<EntityFieldDto> fields,
        IReadOnlySet<string> granted,
        SaveFormLayoutRequest? draft,
        CancellationToken ct)
    {
        var builtIn = BuiltInForms.Layout(BuiltInForms.Get(entityType));
        await using var db = await dbFactory.CreateAsync(ct);
        var saved = await db.Set<FormLayout>().AsNoTracking().Where(l => l.EntityType == entityType).ToListAsync(ct);
        if (draft is not null)
        {
            var scope = FormLayout.ScopeKeyFor(draft.CompanyId, NormalizeRole(draft.RoleKey));
            saved.RemoveAll(l => l.ScopeKey == scope);
            saved.Add(new FormLayout
            {
                Id = Guid.Empty,
                EntityType = entityType,
                CompanyId = draft.CompanyId,
                RoleKey = NormalizeRole(draft.RoleKey),
                ScopeKey = scope,
                SectionsJson = JsonSerializer.Serialize(draft.Sections, Json),
                UpdatedBy = "",
            });
        }

        var picked = FormLayoutEngine.Pick(saved, l => l.CompanyId, l => l.RoleKey, companyId, roleKeys);
        if (picked is null)
        {
            return FormLayoutEngine.Effective(entityType, null, "Built-in", FormLayoutEngine.Normalize([], builtIn, fields), fields, granted);
        }

        var companyCode = picked.CompanyId is { } id ? await db.Set<Company>().Where(c => c.Id == id).Select(c => c.Code).FirstOrDefaultAsync(ct) : null;
        return FormLayoutEngine.Effective(entityType, picked.Id, FormLayoutEngine.Describe(picked.RoleKey, companyCode),
            FormLayoutEngine.Normalize(ReadSections(picked.SectionsJson), builtIn, fields), fields, granted);
    }

    private static string? NormalizeRole(string? roleKey) =>
        string.IsNullOrWhiteSpace(roleKey) || roleKey == CustomizationRoles.Everyone ? null : roleKey.Trim();

    private static IEnumerable<string> PermissionKeys(IEnumerable<EntityFieldDto> fields) =>
        fields.Select(f => f.PermissionKey).OfType<string>().Distinct();

    internal static IReadOnlyList<FormSectionLayout> ReadSections(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FormSectionLayout>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static FormLayoutDto ToDto(FormLayout l) =>
        new(l.Id, l.EntityType, l.CompanyId, l.RoleKey, ReadSections(l.SectionsJson), l.UpdatedBy, l.UpdatedAtUtc);
}
