using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Compliance;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CmmcPracticeConfiguration : IEntityTypeConfiguration<CmmcPractice>
{
    public void Configure(EntityTypeBuilder<CmmcPractice> b)
    {
        b.Property(p => p.PracticeId).HasMaxLength(16);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Implementation).HasMaxLength(2000);
        b.Property(p => p.Evidence).HasMaxLength(2000);
        b.Property(p => p.OwnerEmail).HasMaxLength(320);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.PracticeId).IsUnique();
    }
}

internal sealed class CmmcPoamItemConfiguration : IEntityTypeConfiguration<CmmcPoamItem>
{
    public void Configure(EntityTypeBuilder<CmmcPoamItem> b)
    {
        b.Property(p => p.PracticeId).HasMaxLength(16);
        b.Property(p => p.Milestone).HasMaxLength(500);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.OwnerEmail).HasMaxLength(320);
        b.Property(p => p.Notes).HasMaxLength(2000);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.PracticeId);
    }
}
