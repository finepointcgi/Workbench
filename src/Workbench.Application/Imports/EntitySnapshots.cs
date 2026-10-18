using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Workbench.Application.Imports;

/// <summary>
/// Property snapshots of any mapped entity, taken from the EF model, so an import can record what it changed and a
/// rollback can restore it (or tell that someone changed it since) without per-entity code.
/// </summary>
internal static class EntitySnapshots
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DbContext Context(IWorkbenchDbContext db) =>
        db as DbContext ?? throw new InvalidOperationException("Imports need an EF Core DbContext.");

    public static IEntityType EntityType(DbContext context, string clrName) =>
        context.Model.GetEntityTypes().FirstOrDefault(t => t.ClrType.FullName == clrName && !t.IsOwned())
        ?? throw new InvalidOperationException($"{clrName} isn't a mapped entity.");

    public static IEntityType EntityType(DbContext context, Type type) =>
        context.Model.FindEntityType(type) ?? throw new InvalidOperationException($"{type.Name} isn't a mapped entity.");

    /// <summary>JSON of the entity's non-key properties, or null when it doesn't exist.</summary>
    public static async Task<string?> CaptureAsync(IWorkbenchDbContext db, Type type, Guid id, CancellationToken cancellationToken)
    {
        var context = Context(db);
        var entity = await context.FindAsync(type, [id], cancellationToken);
        return entity is null ? null : Serialize(context, entity);
    }

    public static string Serialize(DbContext context, object entity)
    {
        var entry = context.Entry(entity);
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Metadata.GetProperties().Where(Snapshotted))
        {
            values[property.Name] = entry.CurrentValues[property];
        }

        return JsonSerializer.Serialize(values, Json);
    }

    /// <summary>Writes a snapshot's values onto a tracked entity.</summary>
    public static void Apply(DbContext context, object entity, string snapshotJson)
    {
        var entry = context.Entry(entity);
        using var document = JsonDocument.Parse(snapshotJson);
        foreach (var property in entry.Metadata.GetProperties().Where(Snapshotted))
        {
            if (document.RootElement.TryGetProperty(property.Name, out var value))
            {
                entry.CurrentValues[property] = value.Deserialize(property.ClrType, Json);
            }
        }
    }

    /// <summary>Creates a detached entity from a snapshot (re-inserting something an import removed).</summary>
    public static object Materialize(IEntityType entityType, Guid id, string snapshotJson)
    {
        var entity = Activator.CreateInstance(entityType.ClrType)
            ?? throw new InvalidOperationException($"Can't create {entityType.ClrType.Name}.");
        using var document = JsonDocument.Parse(snapshotJson);
        foreach (var property in entityType.GetProperties())
        {
            if (property.PropertyInfo is not { CanWrite: true } info)
            {
                continue;
            }

            if (property.IsPrimaryKey())
            {
                info.SetValue(entity, id);
            }
            else if (document.RootElement.TryGetProperty(property.Name, out var value))
            {
                info.SetValue(entity, value.Deserialize(property.ClrType, Json));
            }
        }

        return entity;
    }

    /// <summary>Same values, ignoring formatting.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a == b;
        }

        using var x = JsonDocument.Parse(a);
        using var y = JsonDocument.Parse(b);
        return JsonElement.DeepEquals(x.RootElement, y.RootElement);
    }

    private static bool Snapshotted(IProperty p) => !p.IsPrimaryKey() && !p.IsShadowProperty() && !p.IsConcurrencyToken;
}
