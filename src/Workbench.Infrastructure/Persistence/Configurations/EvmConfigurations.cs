using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class EvmBaselineConfiguration : IEntityTypeConfiguration<EvmBaseline>
{
    public void Configure(EntityTypeBuilder<EvmBaseline> b)
    {
        b.Property(x => x.ManagementReserve).Money();
        b.Property(x => x.UndistributedBudget).Money();
        b.Property(x => x.ThresholdPercent).HasPrecision(9, 2);
        b.Property(x => x.ThresholdAmount).Money();
        b.Property(x => x.CreatedBy).HasMaxLength(320);
        b.HasIndex(x => x.ProjectId).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BudgetVersion>().WithMany().HasForeignKey(x => x.BudgetVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EvmWorkPackageConfiguration : IEntityTypeConfiguration<EvmWorkPackage>
{
    public void Configure(EntityTypeBuilder<EvmWorkPackage> b)
    {
        b.Property(x => x.Technique).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Budget).Money();
        b.Property(x => x.PercentCompleteOverride).HasPrecision(7, 2);
        b.Property(x => x.ProgressUpdatedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.BaselineId, x.WbsElementId }).IsUnique();
        b.HasOne<EvmBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.ControlAccountWbsId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EvmWorkPackage>().WithMany().HasForeignKey(x => x.ApportionedToId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Milestones).WithOne().HasForeignKey(m => m.WorkPackageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EvmMilestoneConfiguration : IEntityTypeConfiguration<EvmMilestone>
{
    public void Configure(EntityTypeBuilder<EvmMilestone> b)
    {
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Weight).HasPrecision(7, 2);
        b.HasIndex(x => new { x.WorkPackageId, x.Order });
    }
}

internal sealed class EvmStatusPeriodConfiguration : IEntityTypeConfiguration<EvmStatusPeriod>
{
    public void Configure(EntityTypeBuilder<EvmStatusPeriod> b)
    {
        b.Property(x => x.ManagementReserve).Money();
        b.Property(x => x.UndistributedBudget).Money();
        b.Property(x => x.ClosedBy).HasMaxLength(320);
        b.Property(x => x.ReopenedBy).HasMaxLength(320);
        b.Property(x => x.ReopenReason).HasMaxLength(1000);
        b.HasIndex(x => new { x.ProjectId, x.PeriodStart });
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(x => x.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Snapshots).WithOne().HasForeignKey(s => s.StatusPeriodId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EvmPeriodSnapshotConfiguration : IEntityTypeConfiguration<EvmPeriodSnapshot>
{
    public void Configure(EntityTypeBuilder<EvmPeriodSnapshot> b)
    {
        b.Property(x => x.Technique).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Budget).Money();
        b.Property(x => x.Bcws).Money();
        b.Property(x => x.Bcwp).Money();
        b.Property(x => x.Acwp).Money();
        b.Property(x => x.PercentComplete).HasPrecision(7, 2);
        b.HasIndex(x => x.StatusPeriodId);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EvmVarianceAnalysisConfiguration : IEntityTypeConfiguration<EvmVarianceAnalysis>
{
    public void Configure(EntityTypeBuilder<EvmVarianceAnalysis> b)
    {
        b.Property(x => x.Cause).HasMaxLength(4000);
        b.Property(x => x.Impact).HasMaxLength(4000);
        b.Property(x => x.CorrectiveAction).HasMaxLength(4000);
        b.Property(x => x.ManagerEac).HasPrecision(18, 2);
        b.Property(x => x.UpdatedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.ProjectId, x.FiscalPeriodId, x.ControlAccountWbsId }).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(x => x.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.ControlAccountWbsId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EvmChangeRequestConfiguration : IEntityTypeConfiguration<EvmChangeRequest>
{
    public void Configure(EntityTypeBuilder<EvmChangeRequest> b)
    {
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Justification).HasMaxLength(4000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.RequestedBy).HasMaxLength(320);
        b.Property(x => x.DecidedBy).HasMaxLength(320);
        b.Property(x => x.DecisionNote).HasMaxLength(2000);
        b.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<EvmBaseline>().WithMany().HasForeignKey(x => x.BaselineId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ChangeRequestId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EvmChangeRequestLineConfiguration : IEntityTypeConfiguration<EvmChangeRequestLine>
{
    public void Configure(EntityTypeBuilder<EvmChangeRequestLine> b)
    {
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.NewTechnique).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.BudgetChange).Money();
        b.HasOne<WbsElement>().WithMany().HasForeignKey(x => x.WbsElementId).OnDelete(DeleteBehavior.Restrict);
    }
}
