using System.Collections;
using System.Globalization;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Customization;

namespace Workbench.Application.Customization;

/// <summary>
/// Built-in and custom fields of every customizable record type, for automations, configurable forms and list views.
/// Custom fields are <c>cf.&lt;key&gt;</c> and go through the permission-checked <see cref="ICustomFieldService"/> (validation,
/// audit, masking); built-in fields are the entity's public scalar properties (camelCase), readable by whoever can open the
/// record and writable only where the entity's registration allows it.
/// </summary>
public sealed class EntityFieldCatalog(
    CustomizableEntityRegistry registry, ICustomFieldService customFields, IServiceProvider services, IWorkbenchDbContextFactory dbFactory,
    AccessControl access, AuditRecorder audit) : IEntityFieldCatalog
{
    public IReadOnlyList<string> EntityTypes => registry.Keys;

    public async Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default)
    {
        var entity = registry.Get(entityType);
        var result = entity.BuiltInFields.Select(b => new EntityFieldDto(entity.Key, b.Key, b.Label, b.FieldType, false, b.IsRequired, b.Options, null)).ToList();
        var defs = await customFields.ListDefinitionsAsync(entity.Key, false, ct);
        var withOptions = defs.Any(d => d.ValueListKey is not null) ? await customFields.GetValuesAsync(entity.Key, null, ct) : null;
        result.AddRange(defs.Select(d =>
        {
            var options = withOptions?.Fields.FirstOrDefault(f => f.FieldId == d.Id)?.Options;
            return new EntityFieldDto(entity.Key, CustomFieldValues.CatalogPrefix + d.Key, d.Label, d.FieldType.ToString(), true, d.IsRequired,
                options?.Select(o => o.Code).ToList(), d.PermissionKey, options?.Select(o => o.Label).ToList());
        }));
        return result;
    }

    public async Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default)
    {
        var entity = registry.Get(entityType);
        if (fieldKey.StartsWith(CustomFieldValues.CatalogPrefix, StringComparison.Ordinal))
        {
            var key = fieldKey[CustomFieldValues.CatalogPrefix.Length..];
            var values = await customFields.GetValuesAsync(entity.Key, recordId, ct);
            var field = values.Fields.FirstOrDefault(f => f.Key == key) ?? throw new ArgumentException($"{entity.LabelPlural} have no custom field '{key}'.");
            return field.IsMasked ? null : Typed(field.FieldType, field.Value);
        }

        var builtIn = entity.BuiltInFields.FirstOrDefault(b => b.Key == fieldKey) ?? throw new ArgumentException($"{entity.LabelPlural} have no field '{fieldKey}'.");
        if (!await entity.CanReadAsync(services, recordId, ct))
        {
            throw WorkbenchNotFoundException.For(entity.Label, recordId);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var record = await entity.LoadAsync(db, recordId, false, ct) ?? throw WorkbenchNotFoundException.For(entity.Label, recordId);
        var value = builtIn.Property.GetValue(record);
        return value is Enum e ? e.ToString() : value;
    }

    public async Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default)
    {
        var entity = registry.Get(entityType);
        if (fieldKey.StartsWith(CustomFieldValues.CatalogPrefix, StringComparison.Ordinal))
        {
            var key = fieldKey[CustomFieldValues.CatalogPrefix.Length..];
            await customFields.SaveValuesAsync(entity.Key, recordId, new SaveCustomFieldValuesRequest(new Dictionary<string, string?> { [key] = ToText(value) }), ct);
            return;
        }

        var builtIn = entity.BuiltInFields.FirstOrDefault(b => b.Key == fieldKey) ?? throw new ArgumentException($"{entity.LabelPlural} have no field '{fieldKey}'.");
        if (!entity.EditableBuiltIns.Contains(builtIn.Key) || !builtIn.Property.CanWrite)
        {
            throw new ArgumentException($"{entity.Label} {builtIn.Label.ToLowerInvariant()} changes only on its own page (its rules live there), not through a field update.");
        }

        if (!await entity.CanReadAsync(services, recordId, ct))
        {
            throw WorkbenchNotFoundException.For(entity.Label, recordId);
        }

        await access.RequireRoleAsync(entity.ManageRoles, $"You can't change {entity.LabelPlural.ToLowerInvariant()}.", ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var record = await entity.LoadAsync(db, recordId, true, ct) ?? throw WorkbenchNotFoundException.For(entity.Label, recordId);
        var before = builtIn.Property.GetValue(record);
        var converted = Convert(builtIn, value);
        builtIn.Property.SetValue(record, converted);
        audit.Record(db, entity.AuditEntityType, recordId, "Field changed", new Dictionary<string, object?> { [builtIn.Key] = before }, new Dictionary<string, object?> { [builtIn.Key] = converted });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Invariant text for a value from code: numbers 1234.5, dates yyyy-MM-dd, flags true/false, lists comma-separated.</summary>
    public static string? ToText(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => DateOnly.FromDateTime(dt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dto => DateOnly.FromDateTime(dto.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable list => string.Join(",", list.Cast<object?>().Select(ToText)),
        _ => value.ToString(),
    };

    private static object? Typed(CustomFieldType type, string? text)
    {
        if (text is null)
        {
            return null;
        }

        return type switch
        {
            CustomFieldType.Number or CustomFieldType.Currency => decimal.Parse(text, CultureInfo.InvariantCulture),
            CustomFieldType.Date => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            CustomFieldType.YesNo => text == "true",
            CustomFieldType.MultiPicklist => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => text,
        };
    }

    private static object? Convert(BuiltInField field, object? value)
    {
        var text = ToText(value);
        var nullable = !field.Property.PropertyType.IsValueType || Nullable.GetUnderlyingType(field.Property.PropertyType) is not null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return nullable ? null : throw new ArgumentException($"{field.Label} can't be blank.");
        }

        var t = field.ClrType;
        try
        {
            return t == typeof(string) ? text.Trim()
                : t == typeof(decimal) ? decimal.Parse(text, CultureInfo.InvariantCulture)
                : t == typeof(int) ? int.Parse(text, CultureInfo.InvariantCulture)
                : t == typeof(bool) ? bool.Parse(text)
                : t == typeof(DateOnly) ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : t == typeof(Guid) ? Guid.Parse(text)
                : t.IsEnum ? Enum.Parse(t, text, ignoreCase: true)
                : throw new ArgumentException($"{field.Label} can't be set this way.");
        }
        catch (FormatException)
        {
            throw new ArgumentException($"{field.Label}: '{text}' isn't a valid value.");
        }
    }
}
