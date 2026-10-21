using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    /// <summary>When the built-in roles were seeded (fixed, so the model doesn't change between builds).</summary>
    private static readonly DateTime SeededAtUtc = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.Property(r => r.Name).HasMaxLength(80);
        b.Property(r => r.NormalizedName).HasMaxLength(80);
        b.Property(r => r.Description).HasMaxLength(500);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.UpdatedBy).HasMaxLength(320);
        b.HasIndex(r => r.NormalizedName).IsUnique();
        b.HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);

        // The built-in roles, in every tenant database. Their permissions are the catalog's (code), not rows.
        b.HasData(BuiltInRoles.All.Select(d => new
        {
            d.Id,
            d.Name,
            NormalizedName = d.Name.ToLowerInvariant(),
            d.Description,
            BuiltIn = (int?)(int)d.Role,
            CreatedAtUtc = SeededAtUtc,
        }));
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("RolePermissions");
        b.Property(p => p.Permission).HasMaxLength(100);
        b.HasIndex(p => new { p.RoleId, p.Permission }).IsUnique();
        b.HasIndex(p => p.Permission);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("UserRoles");
        b.Property(r => r.AssignedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.UserId, r.RoleId }).IsUnique();
        b.HasIndex(r => r.RoleId);
        b.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Role>().WithMany().HasForeignKey(r => r.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}
