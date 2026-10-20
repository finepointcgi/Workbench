using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.SalesTax;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Sales and use tax. Tax rates are fractions with six decimals (0.053000 = 5.3%); amounts are money.

internal static class TaxPrecision
{
    public static PropertyBuilder<decimal> TaxRate(this PropertyBuilder<decimal> p) => p.HasPrecision(9, 6);
}

internal sealed class TaxJurisdictionConfiguration : IEntityTypeConfiguration<TaxJurisdiction>
{
    public void Configure(EntityTypeBuilder<TaxJurisdiction> b)
    {
        b.ToTable("TaxJurisdictions");
        b.Property(j => j.Code).HasMaxLength(64);
        b.Property(j => j.Name).HasMaxLength(200);
        b.Property(j => j.State).HasMaxLength(2);
        b.Property(j => j.County).HasMaxLength(100);
        b.Property(j => j.City).HasMaxLength(100);
        b.HasIndex(j => j.Code).IsUnique();
    }
}

internal sealed class TaxRateConfiguration : IEntityTypeConfiguration<TaxRate>
{
    public void Configure(EntityTypeBuilder<TaxRate> b)
    {
        b.ToTable("TaxRates");
        b.Property(r => r.StateRate).TaxRate();
        b.Property(r => r.CountyRate).TaxRate();
        b.Property(r => r.CityRate).TaxRate();
        b.Property(r => r.SpecialRate).TaxRate();
        b.Ignore(r => r.CombinedRate);
        b.Property(r => r.Source).HasMaxLength(50);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.JurisdictionId, r.EffectiveDate });
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(r => r.JurisdictionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaxCodeConfiguration : IEntityTypeConfiguration<TaxCode>
{
    public void Configure(EntityTypeBuilder<TaxCode> b)
    {
        b.ToTable("TaxCodes");
        b.Property(c => c.Code).HasMaxLength(32);
        b.Property(c => c.Name).HasMaxLength(200);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.Code).IsUnique();
    }
}

internal sealed class TaxCodeAssignmentConfiguration : IEntityTypeConfiguration<TaxCodeAssignment>
{
    public void Configure(EntityTypeBuilder<TaxCodeAssignment> b)
    {
        b.ToTable("TaxCodeAssignments");
        b.Property(a => a.TargetKey).HasMaxLength(64);
        b.Property(a => a.UpdatedBy).HasMaxLength(320);
        b.HasIndex(a => new { a.Target, a.TargetKey }).IsUnique();
        b.HasOne<TaxCode>().WithMany().HasForeignKey(a => a.TaxCodeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerTaxProfileConfiguration : IEntityTypeConfiguration<CustomerTaxProfile>
{
    public void Configure(EntityTypeBuilder<CustomerTaxProfile> b)
    {
        b.ToTable("CustomerTaxProfiles");
        b.Property(p => p.UpdatedBy).HasMaxLength(320);
        b.HasIndex(p => p.CustomerId).IsUnique();
        b.HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(p => p.ShipToJurisdictionId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaxExemptionCertificateConfiguration : IEntityTypeConfiguration<TaxExemptionCertificate>
{
    public void Configure(EntityTypeBuilder<TaxExemptionCertificate> b)
    {
        b.ToTable("TaxExemptionCertificates");
        b.Property(c => c.Number).HasMaxLength(64);
        b.Property(c => c.State).HasMaxLength(2);
        b.Property(c => c.Reason).HasMaxLength(100);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.CustomerId);
        b.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesTaxSettingsConfiguration : IEntityTypeConfiguration<SalesTaxSettings>
{
    public void Configure(EntityTypeBuilder<SalesTaxSettings> b)
    {
        b.ToTable("SalesTaxSettings");
        b.Property(s => s.GovernmentCategories).HasMaxLength(500);
        b.Property(s => s.Engine).HasMaxLength(50);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasOne<Account>().WithMany().HasForeignKey(s => s.SalesTaxPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(s => s.UseTaxPayableAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(s => s.UseTaxExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(s => s.HomeJurisdictionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxCode>().WithMany().HasForeignKey(s => s.DefaultSalesTaxCodeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxCode>().WithMany().HasForeignKey(s => s.DefaultPurchaseTaxCodeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceTaxConfiguration : IEntityTypeConfiguration<InvoiceTax>
{
    public void Configure(EntityTypeBuilder<InvoiceTax> b)
    {
        b.ToTable("InvoiceTaxes");
        b.Property(t => t.TaxableAmount).Money();
        b.Property(t => t.ExemptAmount).Money();
        b.Property(t => t.Rate).TaxRate();
        b.Property(t => t.StateTax).Money();
        b.Property(t => t.CountyTax).Money();
        b.Property(t => t.CityTax).Money();
        b.Property(t => t.SpecialTax).Money();
        b.Property(t => t.TaxAmount).Money();
        b.Property(t => t.ExemptionReason).HasMaxLength(200);
        b.Property(t => t.Engine).HasMaxLength(50);
        b.Property(t => t.CalculatedBy).HasMaxLength(320);
        b.HasIndex(t => t.InvoiceId).IsUnique();
        b.HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(t => t.JurisdictionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(t => t.JurisdictionOverrideId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxExemptionCertificate>().WithMany().HasForeignKey(t => t.CertificateId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApBillTaxConfiguration : IEntityTypeConfiguration<ApBillTax>
{
    public void Configure(EntityTypeBuilder<ApBillTax> b)
    {
        b.ToTable("ApBillTaxes");
        b.Property(t => t.VendorChargedTax).Money();
        b.Property(t => t.TaxableAmount).Money();
        b.Property(t => t.Rate).TaxRate();
        b.Property(t => t.UseTaxAmount).Money();
        b.Property(t => t.UpdatedBy).HasMaxLength(320);
        b.HasIndex(t => t.BillId).IsUnique();
        b.HasOne<ApBill>().WithMany().HasForeignKey(t => t.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxJurisdiction>().WithMany().HasForeignKey(t => t.JurisdictionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TaxCode>().WithMany().HasForeignKey(t => t.TaxCodeOverrideId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(t => t.UseTaxJournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}
