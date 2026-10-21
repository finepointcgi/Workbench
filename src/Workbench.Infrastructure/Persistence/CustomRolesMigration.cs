using Microsoft.EntityFrameworkCore.Migrations;
using Workbench.Contracts.Identity;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// The data half of the migration that introduced custom roles: each user's old <c>AppUser.Is*</c> role flags become
/// <c>UserRoles</c> rows for the matching built-in role, and each invite's <c>UserInvite.Is*</c> flags become its
/// <c>Roles</c> value. The built-in role rows themselves are seeded by the model (<c>HasData</c> in
/// <c>RoleConfiguration</c>), so they're inserted by the same migration.
/// </summary>
/// <remarks>
/// Call <see cref="ConvertOrgRoleFlags"/> from the generated migration's <c>Up</c> after the <c>Roles</c> and
/// <c>UserRoles</c> tables, the built-in role rows and <c>UserInvite.Roles</c> exist, and before the old flag columns are
/// dropped. EF scaffolds the drops first (and may guess <c>UserInvite.IsTimekeeper</c> was renamed to <c>Roles</c>): move
/// the drops to the end and replace that rename with an <c>AddColumn&lt;int&gt;("Roles", defaultValue: 0)</c>.
/// A database created from a fresh baseline has no old columns and doesn't call this.
/// </remarks>
public static class CustomRolesMigration
{
    /// <summary>The old flag columns and the built-in role each one meant.</summary>
    public static IReadOnlyList<(string Column, OrgRoles Role)> Flags { get; } =
    [
        ("IsAdmin", OrgRoles.Admin),
        ("IsFinance", OrgRoles.Finance),
        ("IsTimekeeper", OrgRoles.Timekeeper),
        ("IsResourceManager", OrgRoles.ResourceManager),
        ("IsProjectManager", OrgRoles.ProjectManager),
        ("IsHr", OrgRoles.Hr),
        ("IsMaterials", OrgRoles.Materials),
        ("IsBusinessDevelopment", OrgRoles.BusinessDevelopment),
        ("IsPricing", OrgRoles.Pricing),
    ];

    public static void ConvertOrgRoleFlags(MigrationBuilder migrationBuilder)
    {
        var postgres = migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        var isTrue = postgres ? "TRUE" : "1";

        // EF stores Guids in SQLite as upper-case text and timestamps as text; PostgreSQL as uuid and timestamptz.
        var newId = postgres
            ? "gen_random_uuid()"
            : "upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89AB', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";
        var now = postgres ? "now()" : "strftime('%Y-%m-%d %H:%M:%S', 'now')";
        string Id(Guid id) => postgres ? $"'{id}'" : $"'{id.ToString().ToUpperInvariant()}'";

        foreach (var (column, role) in Flags)
        {
            migrationBuilder.Sql(
                // ScopeKind 0: the role applies everywhere, as the flag did.
                $"INSERT INTO \"UserRoles\" (\"Id\", \"UserId\", \"RoleId\", \"ScopeKind\", \"AssignedAtUtc\", \"AssignedBy\") " +
                $"SELECT {newId}, u.\"Id\", {Id(BuiltInRoles.IdOf(role))}, 0, {now}, 'migration' FROM \"AppUser\" u " +
                $"WHERE u.\"{column}\" = {isTrue} AND NOT EXISTS (SELECT 1 FROM \"UserRoles\" r WHERE r.\"UserId\" = u.\"Id\" AND r.\"RoleId\" = {Id(BuiltInRoles.IdOf(role))});");
        }

        var sum = string.Join(" + ", Flags.Select(f => $"(CASE WHEN \"{f.Column}\" = {isTrue} THEN {(int)f.Role} ELSE 0 END)"));
        migrationBuilder.Sql($"UPDATE \"UserInvite\" SET \"Roles\" = {sum};");
    }
}
