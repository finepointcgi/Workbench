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

// Live push: the OAuth connection, pending authorisations, the provider ids linked to records and the per-record results of a run.

internal sealed class AccountingConnectionConfiguration : IEntityTypeConfiguration<AccountingConnection>
{
    public void Configure(EntityTypeBuilder<AccountingConnection> b)
    {
        b.ToTable("AccountingConnections");
        b.Property(c => c.Target).HasMaxLength(20);
        b.Property(c => c.Status).HasMaxLength(30);
        b.Property(c => c.CompanyId).HasMaxLength(100);
        b.Property(c => c.CompanyName).HasMaxLength(300);
        b.Property(c => c.AccessTokenProtected).HasMaxLength(12000);
        b.Property(c => c.RefreshTokenProtected).HasMaxLength(4000);
        b.Property(c => c.ChoicesJson).HasMaxLength(8000);
        b.Property(c => c.LastError).HasMaxLength(1000);
        b.Property(c => c.LastSyncStatus).HasMaxLength(30);
        b.Property(c => c.LastSyncMessage).HasMaxLength(1000);
        b.Property(c => c.ConnectedBy).HasMaxLength(320);
        b.Property(c => c.TokenVersion).IsConcurrencyToken();
        b.HasIndex(c => c.Target).IsUnique();
    }
}

internal sealed class AccountingOAuthStateConfiguration : IEntityTypeConfiguration<AccountingOAuthState>
{
    public void Configure(EntityTypeBuilder<AccountingOAuthState> b)
    {
        b.ToTable("AccountingOAuthStates");
        b.Property(s => s.StateHash).HasMaxLength(64);
        b.Property(s => s.Target).HasMaxLength(20);
        b.Property(s => s.CreatedBy).HasMaxLength(320);
        b.HasIndex(s => s.StateHash).IsUnique();
    }
}

internal sealed class AccountingProviderLinkConfiguration : IEntityTypeConfiguration<AccountingProviderLink>
{
    public void Configure(EntityTypeBuilder<AccountingProviderLink> b)
    {
        b.ToTable("AccountingProviderLinks");
        b.Property(l => l.Target).HasMaxLength(20);
        b.Property(l => l.Kind).HasMaxLength(30);
        b.Property(l => l.ProviderId).HasMaxLength(100);
        b.Property(l => l.ProviderNumber).HasMaxLength(100);
        b.HasIndex(l => new { l.Target, l.Kind, l.RecordId }).IsUnique();
    }
}

internal sealed class AccountingPushRunConfiguration : IEntityTypeConfiguration<AccountingPushRun>
{
    public void Configure(EntityTypeBuilder<AccountingPushRun> b)
    {
        b.ToTable("AccountingPushRuns");
        b.Property(r => r.Target).HasMaxLength(20);
        b.Property(r => r.Kind).HasMaxLength(30);
        b.Property(r => r.Status).HasMaxLength(30);
        b.Property(r => r.Message).HasMaxLength(1000);
        b.Property(r => r.StartedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.Target, r.StartedAtUtc });
    }
}

internal sealed class AccountingPushItemConfiguration : IEntityTypeConfiguration<AccountingPushItem>
{
    public void Configure(EntityTypeBuilder<AccountingPushItem> b)
    {
        b.ToTable("AccountingPushItems");
        b.Property(i => i.RecordNumber).HasMaxLength(60);
        b.Property(i => i.Status).HasMaxLength(20);
        b.Property(i => i.ProviderId).HasMaxLength(100);
        b.Property(i => i.ProviderNumber).HasMaxLength(100);
        b.Property(i => i.Message).HasMaxLength(2000);
        b.HasIndex(i => new { i.RunId, i.Status });
        b.HasOne<AccountingPushRun>().WithMany().HasForeignKey(i => i.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
