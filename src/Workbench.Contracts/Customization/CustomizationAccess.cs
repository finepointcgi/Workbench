using Workbench.Contracts.Identity;

namespace Workbench.Contracts.Customization;

// Screen customization: form layouts, saved list views and role home pages. Who may change what is decided behind
// ICustomizationAccess, a small seam: until the permission catalog (custom roles) lands, managing layouts and sharing views
// are Admin only. The permission keys below are the ones the catalog should register.

/// <summary>Permission keys for customization. Register them in the permission catalog (area Administration).</summary>
public static class CustomizationPermissions
{
    /// <summary>Change form layouts and role home pages for everyone (Admin › Customization › Layouts).</summary>
    public const string ManageLayouts = "customization.layouts.manage";

    /// <summary>Share a saved list view with a role or with everyone (anyone may save personal views).</summary>
    public const string ShareViews = "customization.views.share";
}

/// <summary>
/// A role a layout, view or home page can be set for. <see cref="Key"/> is stable: the built-in org role's name
/// (<c>Finance</c>, <c>ProjectManager</c>, <c>Employee</c> for everyone) or, once custom roles exist, a custom role's key.
/// </summary>
public sealed record CustomizationRole(string Key, string Name);

/// <summary>
/// Who is asking, as customization sees it.
/// </summary>
/// <param name="RoleKeys">The caller's roles, most specific first (Admin, Finance, … and always <c>Employee</c> last). The first
/// role with a layout wins.</param>
/// <param name="CompanyId">The company the caller works in (the app bar switcher, else their default company). Layouts set
/// for a company apply there.</param>
public sealed record CustomizationCaller(
    string Email,
    IReadOnlyList<string> RoleKeys,
    Guid? CompanyId,
    bool CanManageLayouts,
    bool CanShareViews);

public interface ICustomizationAccess
{
    Task<CustomizationCaller> GetCallerAsync(CancellationToken ct = default);

    /// <summary>The roles layouts can be set for, in display order (everyone, then each role).</summary>
    Task<IReadOnlyList<CustomizationRole>> ListRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// The field permission keys (<see cref="EntityFieldDto.PermissionKey"/>) the caller holds, out of <paramref name="keys"/>.
    /// A field or column whose key isn't returned must not be shown or offered.
    /// </summary>
    Task<IReadOnlySet<string>> GrantedAsync(IEnumerable<string> keys, CancellationToken ct = default);

    /// <summary>
    /// The same answer for a role rather than the caller (the layout editor's "preview as"). Only those who manage layouts
    /// may ask.
    /// </summary>
    Task<IReadOnlySet<string>> GrantedToRoleAsync(string roleKey, IEnumerable<string> keys, CancellationToken ct = default);
}

/// <summary>Role keys for the built-in org roles. <see cref="Everyone"/> (<c>Employee</c>) is every signed-in person.</summary>
public static class CustomizationRoles
{
    public const string Everyone = "Employee";

    public static string KeyOf(OrgRoles role) => role == OrgRoles.Employee ? Everyone : role.ToString();

    /// <summary>The built-in role a key names, or null for a custom role.</summary>
    public static OrgRoles? BuiltIn(string key) =>
        key == Everyone ? OrgRoles.Employee : Enum.TryParse<OrgRoles>(key, out var role) && OrgRoleChecks.All.Contains(role) ? role : null;

    /// <summary>The caller's built-in roles as keys, most specific first, ending with <see cref="Everyone"/>.</summary>
    public static IReadOnlyList<string> KeysFor(OrgRoles roles) =>
        [.. OrgRoleChecks.All.Where(r => roles.HasFlag(r)).Select(KeyOf), Everyone];

    public static IReadOnlyList<CustomizationRole> BuiltInRoles { get; } =
        [new(Everyone, "Everyone"), .. OrgRoleChecks.All.Select(r => new CustomizationRole(KeyOf(r), OrgRoleChecks.Label(r)))];

    public static string NameOf(string? key) =>
        key is null ? "Everyone" : BuiltInRoles.FirstOrDefault(r => r.Key == key)?.Name ?? key;
}
