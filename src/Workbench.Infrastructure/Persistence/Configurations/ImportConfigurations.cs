using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Imports;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Import jobs deliberately have no foreign keys to the entities they import: a job record must never count as a
// reference that blocks deleting (or rolling back) what it imported.

internal sealed class ImportJobConfiguration : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> b)
    {
        b.Property(j => j.Source).HasConversion<string>().HasMaxLength(32);
        b.Property(j => j.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(j => j.EncryptedToken).HasMaxLength(4000);
        b.Property(j => j.Message).HasMaxLength(4000);
        b.Property(j => j.CreatedBy).HasMaxLength(320);
        b.Property(j => j.ImportedBy).HasMaxLength(320);
        b.Property(j => j.RolledBackBy).HasMaxLength(320);
        b.HasMany(j => j.Files).WithOne().HasForeignKey(f => f.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(j => j.CreatedAtUtc);
    }
}

internal sealed class ImportJobFileConfiguration : IEntityTypeConfiguration<ImportJobFile>
{
    public void Configure(EntityTypeBuilder<ImportJobFile> b)
    {
        b.Property(f => f.FileName).HasMaxLength(255);
        b.Property(f => f.ContentType).HasMaxLength(127);
    }
}

internal sealed class ImportJobRecordConfiguration : IEntityTypeConfiguration<ImportJobRecord>
{
    public void Configure(EntityTypeBuilder<ImportJobRecord> b)
    {
        b.Property(r => r.EntityType).HasMaxLength(200);
        b.Property(r => r.Action).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Label).HasMaxLength(300);
        b.HasOne<ImportJob>().WithMany().HasForeignKey(r => r.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.ImportJobId, r.Sequence });
        b.HasIndex(r => r.EntityId);
    }
}

internal sealed class ImportKeyMapConfiguration : IEntityTypeConfiguration<ImportKeyMap>
{
    public void Configure(EntityTypeBuilder<ImportKeyMap> b)
    {
        b.Property(k => k.Scope).HasMaxLength(64);
        b.Property(k => k.ExternalId).HasMaxLength(128);
        b.HasIndex(k => new { k.Scope, k.ExternalId });
        b.HasIndex(k => k.EntityId);
    }
}
