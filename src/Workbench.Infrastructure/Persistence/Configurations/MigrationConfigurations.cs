using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Migration;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class MigrationSettingsConfiguration : IEntityTypeConfiguration<MigrationSettings>
{
    public void Configure(EntityTypeBuilder<MigrationSettings> b)
    {
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(s => s.ParallelPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MigrationStepRecordConfiguration : IEntityTypeConfiguration<MigrationStepRecord>
{
    public void Configure(EntityTypeBuilder<MigrationStepRecord> b)
    {
        b.Property(s => s.StepKey).HasMaxLength(64);
        b.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Note).HasMaxLength(1000);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.StepKey).IsUnique();
    }
}

internal sealed class ReconciliationMappingConfiguration : IEntityTypeConfiguration<ReconciliationMapping>
{
    public void Configure(EntityTypeBuilder<ReconciliationMapping> b)
    {
        b.Property(m => m.Report).HasConversion<string>().HasMaxLength(32);
        b.Property(m => m.Name).HasMaxLength(100);
        b.Property(m => m.MappingJson).HasMaxLength(32000);
        b.Property(m => m.UpdatedBy).HasMaxLength(320);
        b.HasIndex(m => new { m.Report, m.Name }).IsUnique();
    }
}

internal sealed class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> b)
    {
        b.Property(r => r.Report).HasConversion<string>().HasMaxLength(32);
        b.Property(r => r.PeriodName).HasMaxLength(64);
        b.Property(r => r.MappingName).HasMaxLength(100);
        b.Property(r => r.MappingJson).HasMaxLength(32000);
        b.Property(r => r.FileName).HasMaxLength(255);
        b.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.PrimaryField).HasMaxLength(64);
        b.Property(r => r.CostpointTotal).HasPrecision(18, 4);
        b.Property(r => r.WorkbenchTotal).HasPrecision(18, 4);
        b.Property(r => r.ResultJson).HasMaxLength(ReconciliationLimits.MaxResultJson);
        b.Property(r => r.RunBy).HasMaxLength(320);
        b.Property(r => r.SignedOffBy).HasMaxLength(320);
        b.Property(r => r.SignOffNote).HasMaxLength(2000);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => new { r.FiscalPeriodId, r.Report, r.RunAtUtc });
    }
}

internal sealed class PayrollFileLayoutConfiguration : IEntityTypeConfiguration<PayrollFileLayout>
{
    public void Configure(EntityTypeBuilder<PayrollFileLayout> b)
    {
        b.Property(l => l.ProviderKey).HasMaxLength(32);
        b.Property(l => l.HoursLayoutJson).HasMaxLength(16000);
        b.Property(l => l.RegisterLayoutJson).HasMaxLength(16000);
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.HasIndex(l => l.ProviderKey).IsUnique();
    }
}

internal sealed class PayrollExportRecordConfiguration : IEntityTypeConfiguration<PayrollExportRecord>
{
    public void Configure(EntityTypeBuilder<PayrollExportRecord> b)
    {
        b.Property(r => r.Method).HasMaxLength(16);
        b.Property(r => r.FileName).HasMaxLength(260);
        b.Property(r => r.Sha256).HasMaxLength(64);
        b.Property(r => r.TotalHours).HasPrecision(12, 2);
        b.Property(r => r.Note).HasMaxLength(1000);
        b.Property(r => r.ExportedBy).HasMaxLength(320);
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => new { r.RunId, r.Sequence }).IsUnique();
        b.HasIndex(r => new { r.PayGroupId, r.PayPeriodId });
    }
}

internal sealed class PaylocityCostCenterMappingConfiguration : IEntityTypeConfiguration<PaylocityCostCenterMapping>
{
    public void Configure(EntityTypeBuilder<PaylocityCostCenterMapping> b)
    {
        b.Property(m => m.Code).HasMaxLength(50);
        b.Property(m => m.UpdatedBy).HasMaxLength(320);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(m => m.OrgUnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(m => m.OrgUnitId).IsUnique();
    }
}

internal sealed class PaylocityRosterSettingConfiguration : IEntityTypeConfiguration<PaylocityRosterSetting>
{
    public void Configure(EntityTypeBuilder<PaylocityRosterSetting> b)
    {
        b.Property(s => s.OwnedFields).HasMaxLength(500);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}
