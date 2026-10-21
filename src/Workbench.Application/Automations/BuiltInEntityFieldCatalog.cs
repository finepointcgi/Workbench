using System.Globalization;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Customization;

namespace Workbench.Application.Automations;

/// <summary>
/// The field catalog with built-in fields only (scalar properties and computed figures of <see cref="AutomationEntities"/>).
/// Registered with TryAdd: the custom-fields feature registers the full catalog (built-in and admin-defined fields) instead.
/// It doesn't check permissions; automations check the principal before calling <see cref="SetValueAsync"/>.
/// </summary>
public sealed class BuiltInEntityFieldCatalog(IWorkbenchDbContextFactory dbFactory, IServiceProvider services, AuditRecorder audit) : IEntityFieldCatalog
{
    public IReadOnlyList<string> EntityTypes { get; } = AutomationEntities.All.Select(e => e.Key).ToList();

    public Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default) =>
        Task.FromResult(AutomationEntities.Fields(AutomationEntities.Require(entityType)));

    public async Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default)
    {
        var entity = AutomationEntities.Require(entityType);
        await using var db = await dbFactory.CreateAsync(ct);
        var record = await AutomationEntities.LoadAsync(db, entity, recordId, ct) ?? throw WorkbenchNotFoundException.For(entity.Label, recordId);
        return await ReadAsync(db, entity, record, fieldKey, services, ct);
    }

    public async Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default)
    {
        var entity = AutomationEntities.Require(entityType);
        var property = entity.Property(fieldKey) ?? throw new ArgumentException($"{entity.Label} has no field \"{fieldKey}\".");
        if (!entity.Writable.Contains(property.Name))
        {
            throw new ArgumentException($"{entity.Label} › {AutomationEntities.Humanize(property.Name)} can't be set by an automation.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var record = await AutomationEntities.LoadAsync(db, entity, recordId, ct) ?? throw WorkbenchNotFoundException.For(entity.Label, recordId);
        var before = property.GetValue(record);
        var converted = Convert(value, property.PropertyType);
        if (Equals(before, converted))
        {
            return;
        }

        property.SetValue(record, converted);
        var key = AutomationEntities.FieldKey(property);
        audit.Record(db, entity.AuditEntityType, recordId, "FieldSet",
            new Dictionary<string, object?> { [key] = before }, new Dictionary<string, object?> { [key] = converted });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A field's value on a loaded record: a property, or a computed figure.</summary>
    public static async Task<object?> ReadAsync(IWorkbenchDbContext db, AutomationEntity entity, object record, string fieldKey, IServiceProvider services, CancellationToken ct)
    {
        if (entity.Property(fieldKey) is { } property)
        {
            return property.GetValue(record);
        }

        if (entity.Computed.FirstOrDefault(c => string.Equals(c.Key, fieldKey, StringComparison.OrdinalIgnoreCase)) is { } computed)
        {
            return await computed.Read(new AutomationRecordContext(db, record, services, ct));
        }

        throw new ArgumentException($"{entity.Label} has no field \"{fieldKey}\".");
    }

    /// <summary>Converts an invariant string (or a value) to a property's type; blank becomes null for nullable types.</summary>
    public static object? Convert(object? value, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        if (value is null || (value is string s0 && string.IsNullOrWhiteSpace(s0) && target != typeof(string)))
        {
            return nullable ? null : throw new ArgumentException("A value is required.");
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        var text = System.Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
        try
        {
            return target switch
            {
                _ when target == typeof(string) => text,
                _ when target.IsEnum => Enum.Parse(target, text, ignoreCase: true),
                _ when target == typeof(bool) => bool.Parse(text),
                _ when target == typeof(int) => int.Parse(text, CultureInfo.InvariantCulture),
                _ when target == typeof(long) => long.Parse(text, CultureInfo.InvariantCulture),
                _ when target == typeof(decimal) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
                _ when target == typeof(double) => double.Parse(text, CultureInfo.InvariantCulture),
                _ when target == typeof(DateOnly) => DateOnly.Parse(text, CultureInfo.InvariantCulture),
                _ when target == typeof(DateTime) => DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc),
                _ when target == typeof(Guid) => Guid.Parse(text),
                _ => throw new ArgumentException($"Can't set a {target.Name} field."),
            };
        }
        catch (FormatException)
        {
            throw new ArgumentException($"\"{text}\" isn't a valid {AutomationEntities.FieldType(target, "").ToLowerInvariant()} value.");
        }
    }
}
