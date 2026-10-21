using Workbench.Contracts.Customization;
using E = Workbench.Contracts.Customization.CustomizableEntities;

namespace Workbench.Application.Customization;

/// <summary>
/// The field catalog with built-in fields only: the fields of each customizable form (<see cref="BuiltInForms"/>) and,
/// for list-only entity types, their list columns. Registered with <c>TryAdd</c>; the custom fields module registers the
/// full catalog (built-in plus custom fields and their values) instead. Built-in values are read and saved through each
/// record's own permission-checked service, never through the catalog, so <see cref="GetValueAsync"/> answers null for
/// them and <see cref="SetValueAsync"/> refuses them.
/// </summary>
public sealed class BuiltInEntityFieldCatalog : IEntityFieldCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EntityFieldDto>> Fields = Build();

    public IReadOnlyList<string> EntityTypes { get; } = [.. BuiltInForms.All.Select(f => f.EntityType), .. BuiltInForms.ListOnly];

    public Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default) =>
        Task.FromResult(For(entityType));

    /// <summary>The built-in form (or list) fields of an entity type.</summary>
    public static IReadOnlyList<EntityFieldDto> For(string entityType) =>
        Fields.TryGetValue(entityType, out var fields)
            ? fields
            : throw new ArgumentException($"Unknown entity type \"{entityType}\".", nameof(entityType));

    public async Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default)
    {
        var fields = await GetFieldsAsync(entityType, ct);
        return fields.Any(f => f.Key == fieldKey)
            ? null
            : throw new ArgumentException($"{entityType} has no field \"{fieldKey}\".", nameof(fieldKey));
    }

    public async Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default)
    {
        var fields = await GetFieldsAsync(entityType, ct);
        throw fields.Any(f => f.Key == fieldKey)
            ? new ArgumentException($"\"{fieldKey}\" is a built-in field: save it with the {entityType.ToLowerInvariant()} itself.", nameof(fieldKey))
            : new ArgumentException($"{entityType} has no field \"{fieldKey}\".", nameof(fieldKey));
    }

    private static Dictionary<string, IReadOnlyList<EntityFieldDto>> Build()
    {
        var all = BuiltInForms.All.ToDictionary(f => f.EntityType, f => (IReadOnlyList<EntityFieldDto>)f.Fields.Select(x => BuiltInForms.ToDto(f.EntityType, x)).ToList());
        all[E.Timesheet] =
        [
            new(E.Timesheet, "employeeName", "Employee", BuiltInForms.Person, false, false, null, null),
            new(E.Timesheet, "periodStart", "Period", BuiltInForms.Date, false, false, null, null),
            new(E.Timesheet, "status", "Status", BuiltInForms.Select, false, false, null, null),
            new(E.Timesheet, "totalHours", "Hours", BuiltInForms.Number, false, false, null, null),
        ];
        all[E.SalesOrder] =
        [
            new(E.SalesOrder, "number", "Order", BuiltInForms.Text, false, false, null, null),
            new(E.SalesOrder, "customerName", "Customer", BuiltInForms.Text, false, false, null, null),
            new(E.SalesOrder, "status", "Status", BuiltInForms.Select, false, false, null, null),
            new(E.SalesOrder, "total", "Total", BuiltInForms.Money, false, false, null, null),
        ];
        return all;
    }
}
