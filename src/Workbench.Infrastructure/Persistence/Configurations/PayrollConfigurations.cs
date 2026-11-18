using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class PayGroupConfiguration : IEntityTypeConfiguration<PayGroup>
{
    public void Configure(EntityTypeBuilder<PayGroup> b)
    {
        b.Property(g => g.Code).HasMaxLength(32);
        b.Property(g => g.Name).HasMaxLength(200);
        b.Property(g => g.ProviderKey).HasMaxLength(32);
        b.Property(g => g.CompanyCode).HasMaxLength(64);
        b.Property(g => g.Frequency).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(g => g.Code).IsUnique();
    }
}

internal sealed class PayPeriodConfiguration : IEntityTypeConfiguration<PayPeriod>
{
    public void Configure(EntityTypeBuilder<PayPeriod> b)
    {
        b.HasIndex(p => new { p.PayGroupId, p.Start }).IsUnique();
        b.HasOne<PayGroup>().WithMany().HasForeignKey(p => p.PayGroupId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeePayProfileConfiguration : IEntityTypeConfiguration<EmployeePayProfile>
{
    public void Configure(EntityTypeBuilder<EmployeePayProfile> b)
    {
        b.Property(p => p.ProviderEmployeeId).HasMaxLength(64);
        b.Property(p => p.PayType).HasConversion<string>().HasMaxLength(16);
        // Profiles saved before employment types existed are full-time.
        b.Property(p => p.EmploymentType).HasConversion<string>().HasMaxLength(16).HasDefaultValue(EmploymentType.FullTime);
        b.Property(p => p.AnnualSalary).HasPrecision(18, 2);
        b.Property(p => p.HourlyRate).HasPrecision(18, 4);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.Ignore(p => p.EquivalentHourlyRate);
        b.HasIndex(p => p.EmployeeId).IsUnique();
        b.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PayGroup>().WithMany().HasForeignKey(p => p.PayGroupId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LaborCostAdjustmentConfiguration : IEntityTypeConfiguration<LaborCostAdjustment>
{
    public void Configure(EntityTypeBuilder<LaborCostAdjustment> b)
    {
        b.Property(a => a.Amount).HasPrecision(18, 2);
        b.Property(a => a.WorkPeriods).HasMaxLength(500);
        b.Property(a => a.Reason).HasMaxLength(500);
        b.Property(a => a.CreatedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.TimesheetId, a.TimesheetRevision });
        b.HasIndex(a => a.EmployeeId);
        b.HasOne<Timesheet>().WithMany().HasForeignKey(a => a.TimesheetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}
