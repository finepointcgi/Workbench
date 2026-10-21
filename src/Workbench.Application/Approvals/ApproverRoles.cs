using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Application.Approvals;

/// <summary>
/// The role an approval step routes to, as stored on a task (<c>ApprovalTask.AssigneeRole</c>): an
/// <see cref="ApprovalOrgRole"/> name for the built-in roles (with their old membership: Finance includes administrators,
/// Timekeeper doesn't), or <c>role:{id}</c> for a custom role, whose members are exactly the people holding it.
/// </summary>
public static class ApproverRoles
{
    private const string CustomPrefix = "role:";

    /// <summary>The stored key of the rule's role (an org role, Admin when none is set, or a custom role).</summary>
    public static string KeyOf(ApproverRule rule) =>
        rule.CustomRoleId is { } id ? CustomKey(id) : (rule.Role ?? ApprovalOrgRole.Admin).ToString();

    public static string CustomKey(Guid roleId) => $"{CustomPrefix}{roleId:N}";

    public static Guid? CustomRoleId(string? key) =>
        key is not null && key.StartsWith(CustomPrefix, StringComparison.Ordinal) && Guid.TryParse(key[CustomPrefix.Length..], out var id) ? id : null;

    /// <summary>The built-in roles whose holders are members of an org role, as before custom roles.</summary>
    public static OrgRoles MembersOf(ApprovalOrgRole role) => role switch
    {
        ApprovalOrgRole.Finance => OrgRoles.Finance | OrgRoles.Admin,
        ApprovalOrgRole.Admin => OrgRoles.Admin,
        ApprovalOrgRole.ResourceManager => OrgRoles.ResourceManager | OrgRoles.Admin,
        ApprovalOrgRole.ProjectManager => OrgRoles.ProjectManager | OrgRoles.Admin,
        ApprovalOrgRole.Hr => OrgRoles.Hr | OrgRoles.Admin,
        ApprovalOrgRole.Materials => OrgRoles.Materials | OrgRoles.Admin,
        ApprovalOrgRole.BusinessDevelopment => OrgRoles.BusinessDevelopment | OrgRoles.Admin,
        ApprovalOrgRole.Pricing => OrgRoles.Pricing | OrgRoles.Finance | OrgRoles.Admin,
        _ => OrgRoles.Timekeeper,
    };

    /// <summary>The role ids whose holders are members of the stored role key; empty for an unknown key.</summary>
    public static IReadOnlyList<Guid> RoleIdsOf(string? key) =>
        CustomRoleId(key) is { } id ? [id]
        : Enum.TryParse<ApprovalOrgRole>(key, out var role) ? BuiltInRoles.IdsOf(MembersOf(role))
        : [];

    /// <summary>True when someone holding <paramref name="heldRoleIds"/> is a member of the stored role key.</summary>
    public static bool Holds(IReadOnlyCollection<Guid> heldRoleIds, string? key) => RoleIdsOf(key).Any(heldRoleIds.Contains);

    /// <summary>Emails of the active members of the stored role key.</summary>
    public static async Task<IReadOnlyList<string>> MembersAsync(IWorkbenchDbContext db, string? key, CancellationToken ct) =>
        (await RoleStore.EmailsWithRolesAsync(db, RoleIdsOf(key), ct)).Select(ApprovalEngine.Normalize).ToList();

    /// <summary>The name to show for a stored role key ("Finance", or the custom role's name).</summary>
    public static async Task<string?> LabelAsync(IWorkbenchDbContext db, string? key, CancellationToken ct)
    {
        if (CustomRoleId(key) is not { } id)
        {
            return key;
        }

        return await db.Set<Role>().AsNoTracking().Where(r => r.Id == id).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? "a deleted role";
    }

    /// <summary>Checks a custom role on a rule exists; throws <see cref="ArgumentException"/> otherwise.</summary>
    public static async Task RequireExistsAsync(IWorkbenchDbContext db, Guid roleId, string where, CancellationToken ct)
    {
        if (BuiltInRoles.IsBuiltIn(roleId) || !await db.Set<Role>().AnyAsync(r => r.Id == roleId && r.BuiltIn == null, ct))
        {
            throw new ArgumentException($"{where}: that custom role doesn't exist. Choose an org role for the built-in ones.");
        }
    }
}
