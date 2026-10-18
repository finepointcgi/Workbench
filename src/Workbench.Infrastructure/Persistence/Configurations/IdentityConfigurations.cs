using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain;
using Workbench.Domain.Identity;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(u => u.Email).HasMaxLength(320);
        b.Property(u => u.DisplayName).HasMaxLength(200);
        b.HasIndex(u => u.Email).IsUnique();
    }
}

internal sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> b)
    {
        b.Property(m => m.UserEmail).HasMaxLength(320);
        b.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(m => new { m.WorkspaceId, m.UserEmail }).IsUnique();
        b.HasIndex(m => m.UserEmail);
        b.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
