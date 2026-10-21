namespace Workbench.Contracts.Identity;

/// <summary>
/// Every permission a role can grant: the built-in catalog (<see cref="PermissionCatalog"/>) plus the ones features
/// registered at run time for this organization (<see cref="IPermissionRegistry"/>). Admin › Roles lists these.
/// </summary>
public interface IPermissionCatalog
{
    /// <summary>Built-in permissions first, in catalog order, then registered ones by area and name.</summary>
    Task<IReadOnlyList<PermissionDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<PermissionDefinition?> FindAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// A permission a feature registers at run time, for something an administrator made: "view the custom field Clearance
/// level", "run the automation Close the month". <paramref name="Key"/> is lower-case and dotted, starts with the feature's
/// own prefix (<c>custom_field.</c>, <c>automation.</c>, <c>form.</c>, ...) and is never one of the built-in keys.
/// <paramref name="GrantedTo"/> gives it to built-in roles too (Admin holds every permission anyway).
/// </summary>
public sealed record RegisterPermissionRequest(
    string Key,
    string Area,
    string Name,
    string Description,
    string Source,
    OrgRoles GrantedTo = OrgRoles.Employee,
    bool Sensitive = false);

/// <summary>
/// Lets features add permissions for things administrators create, per organization. Registering is idempotent (a second
/// call updates the name, description, area and built-in grants); unregistering removes the permission from every role.
/// No permission checks here: call it from your feature's own (already checked) service, in the same request. Check the
/// permission like any other: <c>AccessControl.HasAsync(key)</c> or <c>CurrentUserInfo.Can(key)</c>. Every change is
/// audit logged.
/// </summary>
public interface IPermissionRegistry : IPermissionCatalog
{
    Task<PermissionDefinition> RegisterAsync(RegisterPermissionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes the permission and takes it away from every role. Unknown keys are ignored.</summary>
    Task UnregisterAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Everything one feature registered (for example to resynchronize them).</summary>
    Task<IReadOnlyList<PermissionDefinition>> ListBySourceAsync(string source, CancellationToken cancellationToken = default);
}
