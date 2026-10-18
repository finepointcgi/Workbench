using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ExpenseReportConfiguration : IEntityTypeConfiguration<ExpenseReport>
{
    public void Configure(EntityTypeBuilder<ExpenseReport> b)
    {
        b.Property(r => r.Title).HasMaxLength(200);
        b.Property(r => r.Purpose).HasMaxLength(1000);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.SubmittedBy).HasMaxLength(320);
        b.Property(r => r.ApprovedBy).HasMaxLength(320);
        b.Property(r => r.RejectedBy).HasMaxLength(320);
        b.Property(r => r.RejectionReason).HasMaxLength(1000);
        b.HasIndex(r => new { r.EmployeeId, r.Status });
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> b)
    {
        b.Property(f => f.OwnerType).HasMaxLength(64);
        b.Property(f => f.FileName).HasMaxLength(255);
        b.Property(f => f.ContentType).HasMaxLength(200);
        b.Property(f => f.StorageKey).HasMaxLength(200);
        b.Property(f => f.UploadedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.OwnerType, f.OwnerId });
    }
}
