using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Security;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class UserInviteConfiguration : IEntityTypeConfiguration<UserInvite>
{
    public void Configure(EntityTypeBuilder<UserInvite> b)
    {
        b.Property(i => i.Email).HasMaxLength(320);
        b.Property(i => i.DisplayName).HasMaxLength(200);
        b.Property(i => i.InvitedBy).HasMaxLength(320);
        b.Property(i => i.RevokedBy).HasMaxLength(320);
        b.HasIndex(i => i.Email);
    }
}

internal sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> b)
    {
        b.Property(e => e.EventType).HasMaxLength(64);
        b.Property(e => e.Actor).HasMaxLength(320);
        b.Property(e => e.Target).HasMaxLength(512);
        b.Property(e => e.Outcome).HasMaxLength(16);
        b.Property(e => e.IpAddress).HasMaxLength(64);
        b.Property(e => e.UserAgent).HasMaxLength(512);
        b.Property(e => e.Detail).HasMaxLength(2000);
        b.HasIndex(e => e.OccurredAtUtc);
        b.HasIndex(e => new { e.EventType, e.OccurredAtUtc });
        b.HasIndex(e => new { e.Actor, e.OccurredAtUtc });
    }
}

internal sealed class AccessReviewConfiguration : IEntityTypeConfiguration<AccessReview>
{
    public void Configure(EntityTypeBuilder<AccessReview> b)
    {
        b.Property(r => r.Name).HasMaxLength(200);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.StartedBy).HasMaxLength(320);
        b.Property(r => r.CompletedBy).HasMaxLength(320);
        b.Property(r => r.Notes).HasMaxLength(4000);
        b.HasMany(r => r.Items).WithOne().HasForeignKey(i => i.AccessReviewId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(r => r.Items).AutoInclude(false);
    }
}

internal sealed class AccessReviewItemConfiguration : IEntityTypeConfiguration<AccessReviewItem>
{
    public void Configure(EntityTypeBuilder<AccessReviewItem> b)
    {
        b.Property(i => i.UserEmail).HasMaxLength(320);
        b.Property(i => i.DisplayName).HasMaxLength(200);
        b.Property(i => i.OrgRoles).HasMaxLength(100);
        b.Property(i => i.WorkspaceRoles).HasMaxLength(4000);
        b.Property(i => i.EmployeeName).HasMaxLength(200);
        b.Property(i => i.Decision).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Comment).HasMaxLength(2000);
        b.Property(i => i.DecidedBy).HasMaxLength(320);
        b.HasIndex(i => new { i.AccessReviewId, i.UserEmail }).IsUnique();
    }
}

internal sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> b)
    {
        b.Property(p => p.RecordType).HasMaxLength(64);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.RecordType).IsUnique();
    }
}

internal sealed class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> b)
    {
        b.Property(h => h.EntityType).HasMaxLength(32);
        b.Property(h => h.Label).HasMaxLength(200);
        b.Property(h => h.Reason).HasMaxLength(2000);
        b.Property(h => h.PlacedBy).HasMaxLength(320);
        b.Property(h => h.ReleasedBy).HasMaxLength(320);
        b.Ignore(h => h.IsActive);
        b.HasIndex(h => new { h.EntityType, h.EntityId });
    }
}

internal sealed class DataProtectionKeyRecordConfiguration : IEntityTypeConfiguration<DataProtectionKeyRecord>
{
    public void Configure(EntityTypeBuilder<DataProtectionKeyRecord> b)
    {
        b.ToTable("DataProtectionKeys");
        b.Property(k => k.FriendlyName).HasMaxLength(200);
    }
}
