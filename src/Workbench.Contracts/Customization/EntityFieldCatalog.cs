namespace Workbench.Contracts.Customization;

public sealed record EntityFieldDto(string EntityType, string Key, string Label, string FieldType, bool IsCustom, bool IsRequired, IReadOnlyList<string>? Options, string? PermissionKey);
public interface IEntityFieldCatalog
{
    IReadOnlyList<string> EntityTypes { get; }
    Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default);
    Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default);
    Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default);
}
