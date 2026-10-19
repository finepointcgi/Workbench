using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class SignInSettingsConfiguration : IEntityTypeConfiguration<SignInSettings>
{
    public void Configure(EntityTypeBuilder<SignInSettings> b)
    {
        b.Property(s => s.ProviderKind).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.DisplayName).HasMaxLength(100);
        b.Property(s => s.Authority).HasMaxLength(512);
        b.Property(s => s.ClientId).HasMaxLength(200);
        b.Property(s => s.ProtectedClientSecret).HasMaxLength(4000);
        b.Property(s => s.Scopes).HasMaxLength(500);
        b.Property(s => s.MfaRequirement).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.AcceptedAmrValues).HasMaxLength(500);
        b.Property(s => s.AcceptedAcrValues).HasMaxLength(500);
        b.Property(s => s.RequestedAcrValues).HasMaxLength(500);
        b.Property(s => s.JitMode).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.AllowedEmailDomains).HasMaxLength(1000);
        b.Property(s => s.GroupsClaim).HasMaxLength(100);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}

internal sealed class SsoGroupMappingConfiguration : IEntityTypeConfiguration<SsoGroupMapping>
{
    public void Configure(EntityTypeBuilder<SsoGroupMapping> b)
    {
        b.Property(m => m.GroupValue).HasMaxLength(256);
        b.Property(m => m.Description).HasMaxLength(200);
        b.HasIndex(m => m.GroupValue).IsUnique();
    }
}

internal sealed class ComplianceChecklistEntryConfiguration : IEntityTypeConfiguration<ComplianceChecklistEntry>
{
    public void Configure(EntityTypeBuilder<ComplianceChecklistEntry> b)
    {
        b.Property(e => e.ChecklistKey).HasMaxLength(64);
        b.Property(e => e.ItemKey).HasMaxLength(64);
        b.Property(e => e.Status).HasMaxLength(16);
        b.Property(e => e.Notes).HasMaxLength(4000);
        b.Property(e => e.ReviewedBy).HasMaxLength(320);
        b.HasIndex(e => new { e.ChecklistKey, e.ItemKey, e.ReviewedAtUtc });
    }
}
