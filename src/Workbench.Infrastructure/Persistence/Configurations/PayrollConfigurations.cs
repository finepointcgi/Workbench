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

internal sealed class EmployeePayRateConfiguration : IEntityTypeConfiguration<EmployeePayRate>
{
    public void Configure(EntityTypeBuilder<EmployeePayRate> b)
    {
        b.Property(r => r.PayType).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.AnnualSalary).HasPrecision(18, 2);
        b.Property(r => r.HourlyRate).HasPrecision(18, 4);
        b.Property(r => r.Reason).HasMaxLength(500);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Ignore(r => r.EquivalentHourlyRate);
        b.HasIndex(r => new { r.EmployeeId, r.EffectiveFrom });
        b.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollProviderSettingConfiguration : IEntityTypeConfiguration<PayrollProviderSetting>
{
    public void Configure(EntityTypeBuilder<PayrollProviderSetting> b)
    {
        b.Property(s => s.ProviderKey).HasMaxLength(32);
        b.Property(s => s.EarningCodes).HasMaxLength(2000);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.ProviderKey).IsUnique();
    }
}

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(24);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.ExportedBy).HasMaxLength(320);
        b.Property(r => r.PostedBy).HasMaxLength(320);
        b.Property(r => r.VoidedBy).HasMaxLength(320);
        b.Property(r => r.ExportFileName).HasMaxLength(260);
        b.Property(r => r.VoidReason).HasMaxLength(1000);
        b.HasIndex(r => new { r.PayGroupId, r.PayPeriodId });
        b.HasOne<PayGroup>().WithMany().HasForeignKey(r => r.PayGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PayPeriod>().WithMany().HasForeignKey(r => r.PayPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.ExportLines).WithOne().HasForeignKey(l => l.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PayrollExportLineConfiguration : IEntityTypeConfiguration<PayrollExportLine>
{
    public void Configure(EntityTypeBuilder<PayrollExportLine> b)
    {
        b.Property(l => l.Category).HasMaxLength(64);
        b.Property(l => l.EarningCode).HasMaxLength(32);
        b.Property(l => l.Hours).Hours();
        b.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollRegisterConfiguration : IEntityTypeConfiguration<PayrollRegister>
{
    public void Configure(EntityTypeBuilder<PayrollRegister> b)
    {
        b.Property(r => r.FileName).HasMaxLength(260);
        b.Property(r => r.ContentHash).HasMaxLength(64);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ImportedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.RunId, r.ContentHash });
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RegisterId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PayrollRegisterLineConfiguration : IEntityTypeConfiguration<PayrollRegisterLine>
{
    public void Configure(EntityTypeBuilder<PayrollRegisterLine> b)
    {
        b.Property(l => l.ProviderEmployeeId).HasMaxLength(64);
        b.Property(l => l.EmployeeName).HasMaxLength(200);
        b.Property(l => l.GrossWages).Money();
        b.Property(l => l.EmployeeTaxes).Money();
        b.Property(l => l.EmployerTaxes).Money();
        b.Property(l => l.Deductions).Money();
        b.Property(l => l.EmployerBenefits).Money();
        b.Property(l => l.NetPay).Money();
        b.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollProviderConnectionConfiguration : IEntityTypeConfiguration<PayrollProviderConnection>
{
    public void Configure(EntityTypeBuilder<PayrollProviderConnection> b)
    {
        b.Property(c => c.ProviderKey).HasMaxLength(32);
        b.Property(c => c.Environment).HasMaxLength(16);
        b.Property(c => c.Summary).HasMaxLength(200);
        b.Property(c => c.ConnectedBy).HasMaxLength(320);
        b.Property(c => c.LastCheckMessage).HasMaxLength(500);
        b.HasIndex(c => c.ProviderKey).IsUnique();
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
