using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class AgreementBillingPlanConfiguration : IEntityTypeConfiguration<AgreementBillingPlan>
{
    public void Configure(EntityTypeBuilder<AgreementBillingPlan> b)
    {
        b.Property(p => p.Model).HasConversion<string>().HasMaxLength(32);
        b.Property(p => p.Cadence).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.UnusedHours).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Amount).Money();
        b.Property(p => p.IncludedHours).HasPrecision(9, 2);
        b.Property(p => p.OverageRate).HasPrecision(18, 4);
        b.Property(p => p.Description).HasMaxLength(200);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.ContractId).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(p => p.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgreementMilestoneConfiguration : IEntityTypeConfiguration<AgreementMilestone>
{
    public void Configure(EntityTypeBuilder<AgreementMilestone> b)
    {
        b.Property(m => m.Description).HasMaxLength(500);
        b.Property(m => m.Amount).Money();
        b.Property(m => m.CompletedBy).HasMaxLength(320);
        b.HasIndex(m => m.ContractId);
        b.HasOne<Contract>().WithMany().HasForeignKey(m => m.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(m => m.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceWriteOffConfiguration : IEntityTypeConfiguration<InvoiceWriteOff>
{
    public void Configure(EntityTypeBuilder<InvoiceWriteOff> b)
    {
        b.Property(w => w.Amount).Money();
        b.Property(w => w.Reason).HasMaxLength(1000);
        b.Property(w => w.CreatedBy).HasMaxLength(320);
        b.Property(w => w.ReversalReason).HasMaxLength(1000);
        b.Property(w => w.ReversedBy).HasMaxLength(320);
        b.HasIndex(w => w.InvoiceId);
        b.HasIndex(w => new { w.CustomerId, w.WrittenOffOn });
        b.HasOne<Invoice>().WithMany().HasForeignKey(w => w.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(w => w.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(w => w.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BillingOrganizationSettingsConfiguration : IEntityTypeConfiguration<BillingOrganizationSettings>
{
    public void Configure(EntityTypeBuilder<BillingOrganizationSettings> b)
    {
        b.Property(s => s.LegalName).HasMaxLength(200);
        b.Property(s => s.RemitToAddress).HasMaxLength(1000);
        b.Property(s => s.RemittanceInstructions).HasMaxLength(2000);
        b.Property(s => s.PaymentTermsText).HasMaxLength(1000);
        b.Property(s => s.InvoiceFooter).HasMaxLength(1000);
        b.Property(s => s.BillingContact).HasMaxLength(500);
        b.Property(s => s.DefaultLayout).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
    }
}
