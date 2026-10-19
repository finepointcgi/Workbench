using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Currencies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Multicurrency: the currency tables, plus the currency columns added to existing entities (journal lines, AP, AR and
// expense lines). Those are mapped here in second configuration classes so the owning modules' files stay untouched.

internal static class CurrencyColumns
{
    public const int CodeLength = 3;

    /// <summary>Exchange rates keep 8 decimals (a JPY → USD rate is about 0.0067).</summary>
    public static PropertyBuilder<decimal> FxRate(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 8);

    public static PropertyBuilder<decimal?> FxRate(this PropertyBuilder<decimal?> p) => p.HasPrecision(18, 8);

    public static PropertyBuilder<decimal?> FxMoney(this PropertyBuilder<decimal?> p) => p.HasPrecision(18, 2);
}

internal sealed class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> b)
    {
        b.ToTable("Currencies");
        b.Property(c => c.Code).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(c => c.Name).HasMaxLength(100);
        b.Property(c => c.UpdatedBy).HasMaxLength(320);
        b.HasIndex(c => c.Code).IsUnique();
    }
}

internal sealed class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> b)
    {
        b.ToTable("ExchangeRates");
        b.Property(r => r.FromCurrency).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(r => r.ToCurrency).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(r => r.RateType).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.Rate).FxRate();
        b.Property(r => r.Source).HasMaxLength(100);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.HasIndex(r => new { r.FromCurrency, r.ToCurrency, r.RateType, r.EffectiveDate }).IsUnique();
    }
}

internal sealed class CurrencySettingsConfiguration : IEntityTypeConfiguration<CurrencySettings>
{
    public void Configure(EntityTypeBuilder<CurrencySettings> b)
    {
        b.ToTable("CurrencySettings");
        b.Property(s => s.FunctionalCurrency).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasOne<Account>().WithMany().HasForeignKey(s => s.RealizedGainLossAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(s => s.UnrealizedGainLossAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CurrencyRevaluationConfiguration : IEntityTypeConfiguration<CurrencyRevaluation>
{
    public void Configure(EntityTypeBuilder<CurrencyRevaluation> b)
    {
        b.ToTable("CurrencyRevaluations");
        b.Property(r => r.NetGainLoss).Money();
        b.Property(r => r.PostedBy).HasMaxLength(320);
        b.HasIndex(r => r.FiscalPeriodId).IsUnique();
        b.HasOne<FiscalPeriod>().WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.ReversalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RevaluationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CurrencyRevaluationLineConfiguration : IEntityTypeConfiguration<CurrencyRevaluationLine>
{
    public void Configure(EntityTypeBuilder<CurrencyRevaluationLine> b)
    {
        b.ToTable("CurrencyRevaluationLines");
        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.DocumentNumber).HasMaxLength(100);
        b.Property(l => l.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(l => l.TransactionBalance).Money();
        b.Property(l => l.CarryingAmount).Money();
        b.Property(l => l.Rate).FxRate();
        b.Property(l => l.RevaluedAmount).Money();
        b.Property(l => l.GainLoss).Money();
        b.HasIndex(l => l.RevaluationId);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

// ---- Currency columns on existing entities ----

internal sealed class JournalLineCurrencyConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.Property(l => l.TransactionCurrency).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(l => l.TransactionAmount).FxMoney();
        b.Property(l => l.ExchangeRate).FxRate();
    }
}

internal sealed class ApBillCurrencyConfiguration : IEntityTypeConfiguration<ApBill>
{
    public void Configure(EntityTypeBuilder<ApBill> b)
    {
        b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(x => x.ExchangeRate).FxRate();
        b.Property(x => x.TransactionAmount).Money();
        b.Property(x => x.TransactionRetainage).Money();
        b.Property(x => x.TransactionPaid).Money();
        b.HasIndex(x => x.CurrencyCode);
    }
}

internal sealed class ApBillLineCurrencyConfiguration : IEntityTypeConfiguration<ApBillLine>
{
    public void Configure(EntityTypeBuilder<ApBillLine> b) => b.Property(x => x.TransactionAmount).FxMoney();
}

internal sealed class ApPaymentCurrencyConfiguration : IEntityTypeConfiguration<ApPayment>
{
    public void Configure(EntityTypeBuilder<ApPayment> b)
    {
        b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(x => x.TransactionAmount).Money();
        b.Property(x => x.ExchangeRate).FxRate();
        b.Property(x => x.CashAmount).FxMoney();
        b.Property(x => x.RealizedGainLoss).Money();
    }
}

internal sealed class InvoiceCurrencyConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(x => x.ExchangeRate).FxRate();
        b.Property(x => x.TransactionTotal).Money();
        b.Property(x => x.TransactionPaid).Money();
        b.Property(x => x.TransactionCredited).Money();
        b.HasIndex(x => x.CurrencyCode);
    }
}

internal sealed class InvoiceLineCurrencyConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b) => b.Property(x => x.TransactionAmount).FxMoney();
}

internal sealed class PaymentCurrencyConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(x => x.TransactionAmount).Money();
        b.Property(x => x.ExchangeRate).FxRate();
        b.Property(x => x.RealizedGainLoss).Money();
    }
}

internal sealed class PaymentApplicationCurrencyConfiguration : IEntityTypeConfiguration<PaymentApplication>
{
    public void Configure(EntityTypeBuilder<PaymentApplication> b) => b.Property(x => x.TransactionAmount).FxMoney();
}

internal sealed class CustomerBillingProfileCurrencyConfiguration : IEntityTypeConfiguration<CustomerBillingProfile>
{
    public void Configure(EntityTypeBuilder<CustomerBillingProfile> b) => b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
}

internal sealed class VendorCurrencyConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b) => b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
}

internal sealed class OdcEntryCurrencyConfiguration : IEntityTypeConfiguration<OdcEntry>
{
    public void Configure(EntityTypeBuilder<OdcEntry> b)
    {
        b.Property(x => x.CurrencyCode).HasMaxLength(CurrencyColumns.CodeLength);
        b.Property(x => x.TransactionAmount).FxMoney();
        b.Property(x => x.ExchangeRate).FxRate();
    }
}
