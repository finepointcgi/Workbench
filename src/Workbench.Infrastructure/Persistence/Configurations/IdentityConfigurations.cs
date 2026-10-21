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

internal sealed class PasswordCredentialConfiguration : IEntityTypeConfiguration<PasswordCredential>
{
    public void Configure(EntityTypeBuilder<PasswordCredential> b)
    {
        b.Property(c => c.Email).HasMaxLength(320);
        b.Property(c => c.PasswordHash).HasMaxLength(512);
        b.Property(c => c.ProtectedTotpSecret).HasMaxLength(1000);
        b.Property(c => c.ProtectedPendingTotpSecret).HasMaxLength(1000);
        b.Property(c => c.RecoveryCodeHashes).HasMaxLength(2000);
        b.HasIndex(c => c.Email).IsUnique();
    }
}

internal sealed class AccountTokenConfiguration : IEntityTypeConfiguration<AccountToken>
{
    public void Configure(EntityTypeBuilder<AccountToken> b)
    {
        b.Property(t => t.Email).HasMaxLength(320);
        b.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(32);
        b.Property(t => t.TokenHash).HasMaxLength(64);
        b.Property(t => t.UsedAtUtc).IsConcurrencyToken(); // a link works once, even when two requests race
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasIndex(t => t.Email);
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
