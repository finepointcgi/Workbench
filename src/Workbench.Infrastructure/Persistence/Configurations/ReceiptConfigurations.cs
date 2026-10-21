using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Files;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ReceiptDocumentConfiguration : IEntityTypeConfiguration<ReceiptDocument>
{
    public void Configure(EntityTypeBuilder<ReceiptDocument> b)
    {
        b.Property(r => r.OwnerEmail).HasMaxLength(320);
        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Vendor).HasMaxLength(200);
        b.Property(r => r.Location).HasMaxLength(200);
        b.Property(r => r.Total).HasPrecision(18, 2);
        b.Property(r => r.ReadSource).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ReadingJson).HasMaxLength(16000);
        b.Property(r => r.RemovedBy).HasMaxLength(320);
        b.HasIndex(r => r.FileId).IsUnique();
        b.HasIndex(r => new { r.OwnerEmail, r.CreatedAtUtc });
        b.HasOne<StoredFile>().WithMany().HasForeignKey(r => r.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}
