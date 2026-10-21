namespace Workbench.Contracts.Customization;

/// <summary>Field permission keys to check over HTTP (<see cref="ICustomizationAccess.GrantedAsync"/>).</summary>
public sealed record GrantedQuery(IReadOnlyList<string>? Keys);

/// <summary>A field value over HTTP (<see cref="IEntityFieldCatalog.GetValueAsync"/>, <see cref="IEntityFieldCatalog.SetValueAsync"/>).</summary>
public sealed record FieldValue(object? Value);
