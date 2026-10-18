using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.Property(i => i.Number).HasMaxLength(32);
        b.Property(i => i.Type).HasConversion<string>().HasMaxLength(32);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(i => i.Total).Money();
        b.Property(i => i.Withheld).Money();
        b.Property(i => i.AmountPaid).Money();
        b.Property(i => i.AmountCredited).Money();
        b.Property(i => i.RateNote).HasMaxLength(200);
        b.Property(i => i.Notes).HasMaxLength(2000);
        b.Property(i => i.GenerationWarnings).HasMaxLength(4000);
        b.Property(i => i.FundingOverrideReason).HasMaxLength(1000);
        b.Property(i => i.VoidReason).HasMaxLength(1000);
        b.Property(i => i.CreatedBy).HasMaxLength(320);
        b.Property(i => i.ApprovedBy).HasMaxLength(320);
        b.Property(i => i.SentBy).HasMaxLength(320);
        b.Property(i => i.VoidedBy).HasMaxLength(320);
        b.HasIndex(i => new { i.ContractId, i.Sequence }).IsUnique();
        b.HasIndex(i => i.Number).IsUnique();
        b.HasIndex(i => new { i.CustomerId, i.Status });
        b.HasOne<Contract>().WithMany().HasForeignKey(i => i.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Customer>().WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(i => i.CreditOfInvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(i => i.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(i => i.Balance);
        b.Ignore(i => i.IsOpenReceivable);
        b.Ignore(i => i.IsBilled);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.OdcType).HasMaxLength(32);
        b.Property(l => l.Quantity).Hours();
        b.Property(l => l.Rate).Rate();
        b.Property(l => l.Amount).Money();
        b.HasIndex(l => new { l.InvoiceId, l.LineNumber });
        b.HasOne<Clin>().WithMany().HasForeignKey(l => l.ClinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LaborCategory>().WithMany().HasForeignKey(l => l.LaborCategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(l => l.Sources).WithOne().HasForeignKey(s => s.InvoiceLineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class BilledSourceConfiguration : IEntityTypeConfiguration<BilledSource>
{
    public void Configure(EntityTypeBuilder<BilledSource> b)
    {
        b.Property(s => s.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(s => s.Quantity).Hours();
        b.Property(s => s.Rate).Rate();
        b.Property(s => s.Amount).Money();
        b.Property(s => s.Description).HasMaxLength(500);
        b.HasIndex(s => new { s.Kind, s.SourceId });
        b.HasIndex(s => s.InvoiceId);
    }
}

internal sealed class BillingMilestoneConfiguration : IEntityTypeConfiguration<BillingMilestone>
{
    public void Configure(EntityTypeBuilder<BillingMilestone> b)
    {
        b.Property(m => m.Description).HasMaxLength(500);
        b.Property(m => m.Amount).Money();
        b.Property(m => m.CompletedBy).HasMaxLength(320);
        b.HasIndex(m => new { m.ContractId, m.ClinId });
        b.HasOne<Contract>().WithMany().HasForeignKey(m => m.ContractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Clin>().WithMany().HasForeignKey(m => m.ClinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(m => m.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ContractBillingSettingsConfiguration : IEntityTypeConfiguration<ContractBillingSettings>
{
    public void Configure(EntityTypeBuilder<ContractBillingSettings> b)
    {
        b.Property(s => s.InvoicePrefix).HasMaxLength(16);
        b.Property(s => s.FeeWithholdRate).HasPrecision(9, 6);
        b.Property(s => s.FeeWithholdCap).HasPrecision(18, 2);
        b.Property(s => s.MaterialHandlingRate).HasPrecision(9, 6);
        b.Property(s => s.PayingOffice).HasMaxLength(500);
        b.Property(s => s.AdminOffice).HasMaxLength(500);
        b.Property(s => s.CageCode).HasMaxLength(5);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.ContractId).IsUnique();
        b.HasOne<Contract>().WithMany().HasForeignKey(s => s.ContractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerBillingProfileConfiguration : IEntityTypeConfiguration<CustomerBillingProfile>
{
    public void Configure(EntityTypeBuilder<CustomerBillingProfile> b)
    {
        b.Property(p => p.BillingAddress).HasMaxLength(1000);
        b.Property(p => p.StatementEmail).HasMaxLength(320);
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.CustomerId).IsUnique();
        b.HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(p => p.Amount).Money();
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Reference).HasMaxLength(100);
        b.Property(p => p.Notes).HasMaxLength(1000);
        b.Property(p => p.CreatedBy).HasMaxLength(320);
        b.Property(p => p.VoidReason).HasMaxLength(1000);
        b.Property(p => p.VoidedBy).HasMaxLength(320);
        b.HasIndex(p => new { p.CustomerId, p.ReceivedOn });
        b.HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Applications).WithOne().HasForeignKey(a => a.PaymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PaymentApplicationConfiguration : IEntityTypeConfiguration<PaymentApplication>
{
    public void Configure(EntityTypeBuilder<PaymentApplication> b)
    {
        b.Property(a => a.Amount).Money();
        b.HasIndex(a => a.InvoiceId);
        b.HasOne<Invoice>().WithMany().HasForeignKey(a => a.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UnbilledAccrualConfiguration : IEntityTypeConfiguration<UnbilledAccrual>
{
    public void Configure(EntityTypeBuilder<UnbilledAccrual> b)
    {
        b.Property(a => a.Amount).Money();
        b.Property(a => a.PostedBy).HasMaxLength(320);
        b.HasIndex(a => a.FiscalPeriodId).IsUnique();
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(a => a.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}
