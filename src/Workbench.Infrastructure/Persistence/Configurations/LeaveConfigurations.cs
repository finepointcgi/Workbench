using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class LeavePolicyConfiguration : IEntityTypeConfiguration<LeavePolicy>
{
    public void Configure(EntityTypeBuilder<LeavePolicy> b)
    {
        b.Property(p => p.Code).HasMaxLength(32);
        b.Property(p => p.Name).HasMaxLength(200);
        b.Property(p => p.Type).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(24);
        b.Property(p => p.MaxBalance).HasPrecision(9, 2);
        b.Property(p => p.CarryoverLimit).HasPrecision(9, 2);
        b.Property(p => p.EligibleOrgUnitIds).HasMaxLength(4000);
        b.Property(p => p.EligiblePayGroupIds).HasMaxLength(4000);
        b.Property(p => p.EligibleEmploymentTypes).HasMaxLength(200);
        b.HasIndex(p => p.Code).IsUnique();
        b.HasOne<WbsElement>().WithMany().HasForeignKey(p => p.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Tiers).WithOne().HasForeignKey(t => t.PolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeaveAccrualTierConfiguration : IEntityTypeConfiguration<LeaveAccrualTier>
{
    public void Configure(EntityTypeBuilder<LeaveAccrualTier> b)
    {
        b.Property(t => t.Rate).HasPrecision(18, 4);
        b.Property(t => t.MaxBalance).HasPrecision(9, 2);
    }
}

internal sealed class LeaveLedgerEntryConfiguration : IEntityTypeConfiguration<LeaveLedgerEntry>
{
    public void Configure(EntityTypeBuilder<LeaveLedgerEntry> b)
    {
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Hours).Hours();
        b.Property(e => e.SourceKey).HasMaxLength(80);
        b.Property(e => e.Reason).HasMaxLength(500);
        b.Property(e => e.CreatedBy).HasMaxLength(320);
        b.HasIndex(e => new { e.EmployeeId, e.PolicyId, e.Date });
        b.HasIndex(e => e.SourceKey);
        b.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LeavePolicy>().WithMany().HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Hours).Hours();
        b.Property(r => r.Comment).HasMaxLength(1000);
        b.Property(r => r.DecidedBy).HasMaxLength(320);
        b.Property(r => r.DecisionComment).HasMaxLength(1000);
        b.HasIndex(r => new { r.EmployeeId, r.StartDate });
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LeavePolicy>().WithMany().HasForeignKey(r => r.PolicyId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Days).WithOne().HasForeignKey(d => d.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LeaveRequestDayConfiguration : IEntityTypeConfiguration<LeaveRequestDay>
{
    public void Configure(EntityTypeBuilder<LeaveRequestDay> b) => b.Property(d => d.Hours).Hours();
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.Property(h => h.Name).HasMaxLength(200);
        b.Property(h => h.Hours).HasPrecision(9, 2);
        b.HasIndex(h => h.Date).IsUnique();
    }
}
