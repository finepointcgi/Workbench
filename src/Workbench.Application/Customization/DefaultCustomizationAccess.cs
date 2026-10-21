using Workbench.Application.Companies;
using Workbench.Application.Identity;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Customization;

/// <summary>
/// The customization seam until the permission catalog lands: managing layouts and sharing views are Admin only, and field
/// permission keys resolve through <see cref="FieldPermissionGrants"/> (the built-in roles the catalog grants each key to;
/// unknown keys are Admin only). Registered with <c>TryAdd</c>, so the custom roles module replaces it with one that asks
/// <c>AccessControl.HasAsync</c> and adds custom roles to <see cref="CustomizationCaller.RoleKeys"/>.
/// </summary>
public sealed class DefaultCustomizationAccess(
    ICurrentUserContext currentUser,
    AccessControl access,
    CompanyScope companies,
    ICurrentUser user) : ICustomizationAccess
{
    public async Task<CustomizationCaller> GetCallerAsync(CancellationToken ct = default)
    {
        var roles = await RolesAsync(ct);
        var company = (await companies.GetAsync(ct)).ForNewRecords;
        var admin = roles.HasFlag(OrgRoles.Admin);
        return new CustomizationCaller(IsPerson ? access.Email : "", CustomizationRoles.KeysFor(roles), company, admin, admin);
    }

    public Task<IReadOnlyList<CustomizationRole>> ListRolesAsync(CancellationToken ct = default) =>
        Task.FromResult(CustomizationRoles.BuiltInRoles);

    public async Task<IReadOnlySet<string>> GrantedAsync(IEnumerable<string> keys, CancellationToken ct = default) =>
        FieldPermissionGrants.Granted(await RolesAsync(ct), keys);

    public Task<IReadOnlySet<string>> GrantedToRoleAsync(string roleKey, IEnumerable<string> keys, CancellationToken ct = default) =>
        Task.FromResult(FieldPermissionGrants.Granted(CustomizationRoles.BuiltIn(roleKey) ?? OrgRoles.Employee, keys));

    /// <summary>A signed-in person (not the worker's system user, and not a background job reading as the system).</summary>
    private bool IsPerson => access.IsAuthenticated && !access.IsSystemReader && user is not SystemCurrentUser;

    private async Task<OrgRoles> RolesAsync(CancellationToken ct) =>
        IsPerson ? (await currentUser.GetAsync(ct)).Roles : OrgRoles.Employee;
}

/// <summary>Which built-in roles hold the field permission keys used so far (mirrors the permission catalog's grants).</summary>
public static class FieldPermissionGrants
{
    private static readonly IReadOnlyDictionary<string, OrgRoles> Grants = new Dictionary<string, OrgRoles>(StringComparer.Ordinal)
    {
        ["labor.cost.view"] = OrgRoles.Finance,
        ["projects.financials.view"] = OrgRoles.Finance,
        ["setup.contracts"] = OrgRoles.Finance,
        ["setup.bill_rates"] = OrgRoles.Finance,
        ["ap.vendors.tax_ids"] = OrgRoles.Finance,
        ["ap.bills.manage"] = OrgRoles.Finance,
        ["billing.invoices.manage"] = OrgRoles.Finance,
        ["payroll.pay_rates.view"] = OrgRoles.Finance,
        ["bd.pipeline.view"] = OrgRoles.BusinessDevelopment | OrgRoles.Finance,
        ["materials.view"] = OrgRoles.Materials | OrgRoles.Finance,
        ["materials.costing"] = OrgRoles.Finance,
        ["hr.records.manage"] = OrgRoles.Hr,
    };

    public static IReadOnlySet<string> Granted(OrgRoles roles, IEnumerable<string> keys) =>
        keys.Where(k => roles.HasFlag(OrgRoles.Admin) || (Grants.TryGetValue(k, out var to) && (roles & to) != 0)).ToHashSet(StringComparer.Ordinal);
}
