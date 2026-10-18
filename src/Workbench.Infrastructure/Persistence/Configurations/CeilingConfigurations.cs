using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Billing;
using Workbench.Domain.Ceilings;
using Workbench.Domain.Closeout;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class CeilingConfiguration : IEntityTypeConfiguration<Ceiling>
{
    public void Configure(EntityTypeBuilder<Ceiling> b)
    {
        b.Property(c => c.Level).HasConversion<string>().HasMaxLength(16);
        b.Property(c => c.Enforcement).HasConversion<string>().HasMaxLength(8);
        b.Property(c => c.HoursCeiling).HasPrecision(12, 2);
        b.Property(c => c.CostCeiling).HasPrecision(18, 2);
        b.Property(c => c.BillingCeiling).HasPrecision(18, 2);
        b.Property(c => c.AlertThresholds).HasMaxLength(64);
        b.Property(c => c.Notes).HasMaxLength(1000);
        b.Property(c => c.CreatedBy).HasMaxLength(320);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.ContractId);
        b.HasIndex(c => c.ProjectId);
        b.HasOne<Contract>().WithMany().HasForeignKey(c => c.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(c => c.ClinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(c => c.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(c => c.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CeilingOverrideConfiguration : IEntityTypeConfiguration<CeilingOverride>
{
    public void Configure(EntityTypeBuilder<CeilingOverride> b)
    {
        b.Property(o => o.ExtraHours).HasPrecision(12, 2);
        b.Property(o => o.ExtraCost).HasPrecision(18, 2);
        b.Property(o => o.ExtraBilling).HasPrecision(18, 2);
        b.Property(o => o.Reason).HasMaxLength(1000);
        b.Property(o => o.ApprovedBy).HasMaxLength(320);
        b.Property(o => o.RevokedBy).HasMaxLength(320);
        b.Property(o => o.RevokeReason).HasMaxLength(1000);
        b.HasIndex(o => o.CeilingId);
        b.HasOne<Ceiling>().WithMany().HasForeignKey(o => o.CeilingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CeilingAlertConfiguration : IEntityTypeConfiguration<CeilingAlert>
{
    public void Configure(EntityTypeBuilder<CeilingAlert> b)
    {
        b.Property(a => a.Measure).HasConversion<string>().HasMaxLength(8);
        b.Property(a => a.Limit).HasPrecision(18, 2);
        b.Property(a => a.Consumed).HasPrecision(18, 2);
        b.HasIndex(a => new { a.CeilingId, a.Measure, a.ThresholdPercent, a.Limit }).IsUnique();
        b.HasOne<Ceiling>().WithMany().HasForeignKey(a => a.CeilingId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RateCeilingConfiguration : IEntityTypeConfiguration<RateCeiling>
{
    public void Configure(EntityTypeBuilder<RateCeiling> b)
    {
        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(r => r.MaxRate).HasPrecision(9, 6);
        b.Property(r => r.MaxAmount).HasPrecision(18, 2);
        b.Property(r => r.Notes).HasMaxLength(1000);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => r.ContractId);
        b.HasOne<Contract>().WithMany().HasForeignKey(r => r.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(r => r.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CloseoutRecordConfiguration : IEntityTypeConfiguration<CloseoutRecord>
{
    public void Configure(EntityTypeBuilder<CloseoutRecord> b)
    {
        b.ToTable("Closeouts");
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.DeobligationAmount).HasPrecision(18, 2);
        b.Property(c => c.DeobligationRecordedBy).HasMaxLength(320);
        b.Property(c => c.ReleaseOfClaimsNote).HasMaxLength(1000);
        b.Property(c => c.AssignmentOfRefundsNote).HasMaxLength(1000);
        b.Property(c => c.StatusBeforeClose).HasMaxLength(32);
        b.Property(c => c.ClosedBy).HasMaxLength(320);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.ContractId).IsUnique();
        b.HasIndex(c => c.ProjectId).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(c => c.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(c => c.FinalInvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CloseoutWaiverConfiguration : IEntityTypeConfiguration<CloseoutWaiver>
{
    public void Configure(EntityTypeBuilder<CloseoutWaiver> b)
    {
        b.Property(w => w.Item).HasConversion<string>().HasMaxLength(32);
        b.Property(w => w.Reason).HasMaxLength(1000);
        b.Property(w => w.WaivedBy).HasMaxLength(320);
        b.HasIndex(w => new { w.CloseoutId, w.Item }).IsUnique();
        b.HasOne<CloseoutRecord>().WithMany().HasForeignKey(w => w.CloseoutId).OnDelete(DeleteBehavior.Restrict);
    }
}
