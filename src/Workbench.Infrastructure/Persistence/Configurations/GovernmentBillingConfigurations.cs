using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class ContractGovernmentBillingConfiguration : IEntityTypeConfiguration<ContractGovernmentBilling>
{
    public void Configure(EntityTypeBuilder<ContractGovernmentBilling> b)
    {
        b.Property(s => s.Format).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Financing).HasConversion<string>().HasMaxLength(24);
        b.Property(s => s.CostBasis).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.DeliveryDocument).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.ProgressPaymentRate).HasPrecision(9, 6);
        b.Property(s => s.LiquidationRate).HasPrecision(9, 6);
        b.Property(s => s.ContractPrice).HasPrecision(18, 2);
        b.Property(s => s.IssueByDodaac).HasMaxLength(6);
        b.Property(s => s.AdminByDodaac).HasMaxLength(6);
        b.Property(s => s.PayOfficeDodaac).HasMaxLength(6);
        b.Property(s => s.ShipToDodaac).HasMaxLength(6);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.ContractId).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(s => s.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProgressPaymentRequestConfiguration : IEntityTypeConfiguration<ProgressPaymentRequest>
{
    public void Configure(EntityTypeBuilder<ProgressPaymentRequest> b)
    {
        b.Property(r => r.CostBasis).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.ProgressPaymentRate).HasPrecision(9, 6);
        b.Property(r => r.LiquidationRate).HasPrecision(9, 6);
        b.Property(r => r.ContractPrice).Money();
        b.Property(r => r.PaidCosts).Money();
        b.Property(r => r.UnpaidCosts).Money();
        b.Property(r => r.IncurredToDate).Money();
        b.Property(r => r.EstimateToComplete).Money();
        b.Property(r => r.SubcontractorPaid).Money();
        b.Property(r => r.SubcontractorLiquidated).Money();
        b.Property(r => r.SubcontractorApproved).Money();
        b.Property(r => r.PreviousRequested).Money();
        b.Property(r => r.CostsOfDelivered).Money();
        b.Property(r => r.PriceOfDelivered).Money();
        b.Property(r => r.UnliquidatedAdvances).Money();
        b.Property(r => r.LiquidatedToDate).Money();
        b.Property(r => r.Amount).Money();
        b.Property(r => r.Notes).HasMaxLength(2000);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.ContractId, r.Number }).IsUnique();
        b.HasIndex(r => r.InvoiceId);
        b.HasOne<Contract>().WithMany().HasForeignKey(r => r.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PerformanceBasedPaymentEventConfiguration : IEntityTypeConfiguration<PerformanceBasedPaymentEvent>
{
    public void Configure(EntityTypeBuilder<PerformanceBasedPaymentEvent> b)
    {
        b.Property(e => e.Description).HasMaxLength(500);
        b.Property(e => e.Criteria).HasMaxLength(2000);
        b.Property(e => e.Amount).Money();
        b.Property(e => e.CompletedBy).HasMaxLength(320);
        b.Property(e => e.Evidence).HasMaxLength(2000);
        b.Property(e => e.EvidenceReference).HasMaxLength(500);
        b.HasIndex(e => new { e.ContractId, e.Order });
        b.HasOne<Contract>().WithMany().HasForeignKey(e => e.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(e => e.ClinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PieeSubmissionConfiguration : IEntityTypeConfiguration<PieeSubmission>
{
    public void Configure(EntityTypeBuilder<PieeSubmission> b)
    {
        b.Property(s => s.DocumentType).HasConversion<string>().HasMaxLength(32);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.FileName).HasMaxLength(200);
        b.Property(s => s.ExportedBy).HasMaxLength(320);
        b.Property(s => s.Reference).HasMaxLength(100);
        b.Property(s => s.SubmittedBy).HasMaxLength(320);
        b.Property(s => s.RejectionReason).HasMaxLength(1000);
        b.Property(s => s.StatusChangedBy).HasMaxLength(320);
        b.HasIndex(s => s.InvoiceId).IsUnique();
        b.HasOne<Invoice>().WithMany().HasForeignKey(s => s.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
