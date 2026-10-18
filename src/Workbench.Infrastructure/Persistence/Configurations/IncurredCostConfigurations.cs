using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.IncurredCost;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Incurred cost submissions: one package per fiscal year with its frozen schedules and the values typed into it.

internal sealed class IncurredCostSubmissionConfiguration : IEntityTypeConfiguration<IncurredCostSubmission>
{
    public void Configure(EntityTypeBuilder<IncurredCostSubmission> b)
    {
        b.ToTable("IncurredCostSubmissions");
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.SchedulesJson).HasMaxLength(5_000_000);
        b.Property(s => s.LastDiffJson).HasMaxLength(200_000);
        b.Property(s => s.GeneratedBy).HasMaxLength(320);
        b.Property(s => s.ReviewedBy).HasMaxLength(320);
        b.Property(s => s.CertifiedBy).HasMaxLength(320);
        b.Property(s => s.CertifierName).HasMaxLength(200);
        b.Property(s => s.CertifierTitle).HasMaxLength(200);
        b.Property(s => s.CertificateText).HasMaxLength(8000);
        b.Property(s => s.SubmittedBy).HasMaxLength(320);
        b.Property(s => s.SubmissionReference).HasMaxLength(200);
        b.Property(s => s.CreatedBy).HasMaxLength(320);
        b.Ignore(s => s.IsLocked);
        b.HasIndex(s => s.FiscalYear).IsUnique();
    }
}

internal sealed class IncurredCostInputConfiguration : IEntityTypeConfiguration<IncurredCostInput>
{
    public void Configure(EntityTypeBuilder<IncurredCostInput> b)
    {
        b.ToTable("IncurredCostInputs");
        b.Property(i => i.Schedule).HasMaxLength(16);
        b.Property(i => i.RowKey).HasMaxLength(200);
        b.Property(i => i.Column).HasMaxLength(64);
        b.Property(i => i.Value).HasMaxLength(4000);
        b.Property(i => i.UpdatedBy).HasMaxLength(320);
        b.HasIndex(i => new { i.SubmissionId, i.Schedule, i.RowKey, i.Column }).IsUnique();
        b.HasOne<IncurredCostSubmission>().WithMany().HasForeignKey(i => i.SubmissionId).OnDelete(DeleteBehavior.Restrict);
    }
}
