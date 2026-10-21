namespace Workbench.Contracts.Identity;

/// <summary>
/// Marks a contract property as a sensitive field: callers without <see cref="Permission"/> get it masked (null, or zero
/// for a non-nullable number) wherever the permission decorators apply the field masker. On a positional record, put it
/// on the property: <c>[property: SensitiveField(Permission.FieldsSalaries)] decimal? SalaryMin</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveFieldAttribute(string permission) : Attribute
{
    /// <summary>The permission (a <see cref="Contracts.Identity.Permission"/> key) that reveals the field.</summary>
    public string Permission { get; } = permission;
}
