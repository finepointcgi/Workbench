using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class RevenueSetupConfiguration : IEntityTypeConfiguration<RevenueSetup>
{
    public void Configure(EntityTypeBuilder<RevenueSetup> b)
    {
        b.Property(s => s.Method).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.FeeRate).HasPrecision(18, 4);
        b.Property(s => s.OdcMarkupRate).HasPrecision(18, 4);
        b.Property(s => s.TransactionPrice).HasPrecision(18, 2);
        b.Property(s => s.UnitPrice).HasPrecision(18, 2);
        b.Property(s => s.Notes).HasMaxLength(1000);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => new { s.ProjectId, s.ClinId }).IsUnique();
        b.HasOne<Project>().WithMany().HasForeignKey(s => s.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(s => s.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RevenueEstimateConfiguration : IEntityTypeConfiguration<RevenueEstimate>
{
    public void Configure(EntityTypeBuilder<RevenueEstimate> b)
    {
        b.Property(e => e.EstimatedCost).Money();
        b.Property(e => e.EstimatedRevenue).Money();
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Notes).HasMaxLength(2000);
        b.Property(e => e.SubmittedBy).HasMaxLength(320);
        b.Property(e => e.DecidedBy).HasMaxLength(320);
        b.Property(e => e.DecisionNote).HasMaxLength(1000);
        b.HasIndex(e => new { e.ProjectId, e.ClinId, e.AsOf });
        b.HasIndex(e => e.Status);
        b.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(e => e.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RevenueDeliveryConfiguration : IEntityTypeConfiguration<RevenueDelivery>
{
    public void Configure(EntityTypeBuilder<RevenueDelivery> b)
    {
        b.Property(d => d.Quantity).HasPrecision(18, 4);
        b.Property(d => d.Description).HasMaxLength(500);
        b.Property(d => d.RecordedBy).HasMaxLength(320);
        b.HasIndex(d => new { d.ProjectId, d.DeliveredOn });
        b.HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(d => d.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RevenueRunConfiguration : IEntityTypeConfiguration<RevenueRun>
{
    public void Configure(EntityTypeBuilder<RevenueRun> b)
    {
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.PostedKey).HasMaxLength(80);
        b.Property(r => r.RecognizedToDate).Money();
        b.Property(r => r.PeriodRevenue).Money();
        b.Property(r => r.BilledToDate).Money();
        b.Property(r => r.RevenueAdjustment).Money();
        b.Property(r => r.UnbilledBalance).Money();
        b.Property(r => r.BillingsInExcessBalance).Money();
        b.Property(r => r.LossProvisionBalance).Money();
        b.Property(r => r.PostedBy).HasMaxLength(320);
        b.Property(r => r.ReversedBy).HasMaxLength(320);
        b.Property(r => r.ReversalReason).HasMaxLength(1000);

        // Unique while posted (null once reversed): one posted run per project and period.
        b.HasIndex(r => r.PostedKey).IsUnique();
        b.HasIndex(r => new { r.ProjectId, r.FiscalPeriodId });
        b.HasIndex(r => r.FiscalPeriodId);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.ReversalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RevenueRunLineConfiguration : IEntityTypeConfiguration<RevenueRunLine>
{
    public void Configure(EntityTypeBuilder<RevenueRunLine> b)
    {
        b.Property(l => l.Method).HasConversion<string>().HasMaxLength(32);
        b.Property(l => l.CostToDate).Money();
        b.Property(l => l.EstimatedCost).HasPrecision(18, 2);
        b.Property(l => l.EstimatedRevenue).HasPrecision(18, 2);
        b.Property(l => l.PercentComplete).HasPrecision(9, 6);
        b.Property(l => l.RecognizedToDate).Money();
        b.Property(l => l.BilledToDate).Money();
        b.Property(l => l.ExpectedLoss).Money();
        b.Property(l => l.LossProvision).Money();
        b.Property(l => l.Note).HasMaxLength(1000);
        b.HasOne<Clin>().WithMany().HasForeignKey(l => l.ClinId).OnDelete(DeleteBehavior.Restrict);
    }
}
