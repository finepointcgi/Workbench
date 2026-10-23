namespace Workbench.Contracts.Customization;

/// <param name="OptionLabels">What to show for each of <paramref name="Options"/> (same order) when the stored codes differ from the labels.</param>
public sealed record EntityFieldDto(string EntityType, string Key, string Label, string FieldType, bool IsCustom, bool IsRequired, IReadOnlyList<string>? Options, string? PermissionKey,
    IReadOnlyList<string>? OptionLabels = null);

public interface IEntityFieldCatalog
{
    IReadOnlyList<string> EntityTypes { get; }
    Task<IReadOnlyList<EntityFieldDto>> GetFieldsAsync(string entityType, CancellationToken ct = default);
    Task<object?> GetValueAsync(string entityType, Guid recordId, string fieldKey, CancellationToken ct = default);
    Task SetValueAsync(string entityType, Guid recordId, string fieldKey, object? value, CancellationToken ct = default);
}
