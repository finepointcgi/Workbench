using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.AccountingExport;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Accounting exports: mappings to the destination's names, the run history and the record of what was exported where.

internal sealed class AccountingMappingConfiguration : IEntityTypeConfiguration<AccountingMapping>
{
    public void Configure(EntityTypeBuilder<AccountingMapping> b)
    {
        b.ToTable("AccountingMappings");
        b.Property(m => m.Target).HasMaxLength(20);
        b.Property(m => m.MapKind).HasMaxLength(20);
        b.Property(m => m.SourceKey).HasMaxLength(100);
        b.Property(m => m.SourceName).HasMaxLength(200);
        b.Property(m => m.DestinationName).HasMaxLength(200);
        b.Property(m => m.DestinationCode).HasMaxLength(50);
        b.Property(m => m.UpdatedBy).HasMaxLength(320);
        b.HasIndex(m => new { m.Target, m.MapKind, m.SourceKey }).IsUnique();
    }
}

internal sealed class AccountingExportRunConfiguration : IEntityTypeConfiguration<AccountingExportRun>
{
    public void Configure(EntityTypeBuilder<AccountingExportRun> b)
    {
        b.ToTable("AccountingExportRuns");
        b.Property(r => r.Target).HasMaxLength(20);
        b.Property(r => r.FormatId).HasMaxLength(40);
        b.Property(r => r.Kind).HasMaxLength(30);
        b.Property(r => r.FileName).HasMaxLength(200);
        b.Property(r => r.ContentType).HasMaxLength(100);
        b.Property(r => r.ControlTotal).HasPrecision(18, 2);
        b.Property(r => r.Checksum).HasMaxLength(64);
        b.Property(r => r.Note).HasMaxLength(1000);
        b.Property(r => r.BlobKey).HasMaxLength(200);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.Target, r.Kind, r.CreatedAtUtc });
    }
}

internal sealed class AccountingExportedRecordConfiguration : IEntityTypeConfiguration<AccountingExportedRecord>
{
    public void Configure(EntityTypeBuilder<AccountingExportedRecord> b)
    {
        b.ToTable("AccountingExportedRecords");
        b.Property(r => r.Target).HasMaxLength(20);
        b.Property(r => r.Kind).HasMaxLength(30);
        b.Property(r => r.RecordNumber).HasMaxLength(60);
        // One mark per record per target: two exports racing for the same record can't both win.
        b.HasIndex(r => new { r.Target, r.Kind, r.RecordId }).IsUnique();
        b.HasOne<AccountingExportRun>().WithMany().HasForeignKey(r => r.FirstRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AccountingExportRun>().WithMany().HasForeignKey(r => r.LastRunId).OnDelete(DeleteBehavior.Restrict);
    }
}
