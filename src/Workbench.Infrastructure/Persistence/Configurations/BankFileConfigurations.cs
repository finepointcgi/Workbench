using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Cash;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

// Bank payment files: vendor ACH details (encrypted), their approved changes, per-bank-account NACHA settings, positive
// pay layouts and the download and remittance logs.

internal sealed class VendorBankAccountConfiguration : IEntityTypeConfiguration<VendorBankAccount>
{
    public void Configure(EntityTypeBuilder<VendorBankAccount> b)
    {
        b.ToTable("VendorBankAccounts");
        b.Property(a => a.AccountHolderName).HasMaxLength(200);
        b.Property(a => a.ProtectedRoutingNumber).HasMaxLength(2000);
        b.Property(a => a.ProtectedAccountNumber).HasMaxLength(2000);
        b.Property(a => a.RoutingLast4).HasMaxLength(4);
        b.Property(a => a.AccountLast4).HasMaxLength(4);
        b.Property(a => a.RemittanceEmail).HasMaxLength(320);
        b.Property(a => a.ApprovedBy).HasMaxLength(320);
        b.HasIndex(a => a.VendorId).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class VendorBankChangeConfiguration : IEntityTypeConfiguration<VendorBankChange>
{
    public void Configure(EntityTypeBuilder<VendorBankChange> b)
    {
        b.ToTable("VendorBankChanges");
        b.Property(c => c.AccountHolderName).HasMaxLength(200);
        b.Property(c => c.ProtectedRoutingNumber).HasMaxLength(2000);
        b.Property(c => c.ProtectedAccountNumber).HasMaxLength(2000);
        b.Property(c => c.RoutingLast4).HasMaxLength(4);
        b.Property(c => c.AccountLast4).HasMaxLength(4);
        b.Property(c => c.RemittanceEmail).HasMaxLength(320);
        b.Property(c => c.Reason).HasMaxLength(1000);
        b.Property(c => c.RequestedBy).HasMaxLength(320);
        b.Property(c => c.DecidedBy).HasMaxLength(320);
        b.Property(c => c.DecisionComment).HasMaxLength(1000);
        b.HasIndex(c => new { c.VendorId, c.Status });
        b.HasOne<Vendor>().WithMany().HasForeignKey(c => c.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class BankAchSettingsConfiguration : IEntityTypeConfiguration<BankAchSettings>
{
    public void Configure(EntityTypeBuilder<BankAchSettings> b)
    {
        b.ToTable("BankAchSettings");
        b.Property(s => s.ImmediateDestination).HasMaxLength(9);
        b.Property(s => s.ImmediateDestinationName).HasMaxLength(23);
        b.Property(s => s.ImmediateOrigin).HasMaxLength(10);
        b.Property(s => s.ImmediateOriginName).HasMaxLength(23);
        b.Property(s => s.CompanyIdentification).HasMaxLength(10);
        b.Property(s => s.CompanyName).HasMaxLength(16);
        b.Property(s => s.EntryDescription).HasMaxLength(10);
        b.Property(s => s.OriginatingDfi).HasMaxLength(8);
        b.Property(s => s.UpdatedBy).HasMaxLength(320);
        b.HasIndex(s => s.BankAccountId).IsUnique();
        b.HasOne<BankAccount>().WithMany().HasForeignKey(s => s.BankAccountId).OnDelete(DeleteBehavior.Restrict);
        // PositivePayLayoutId is a stored layout or a built-in preset's fixed id, so it has no foreign key.
    }
}

internal sealed class PositivePayLayoutConfiguration : IEntityTypeConfiguration<PositivePayLayout>
{
    public void Configure(EntityTypeBuilder<PositivePayLayout> b)
    {
        b.ToTable("PositivePayLayouts");
        b.Property(l => l.Name).HasMaxLength(100);
        b.Property(l => l.FieldsJson).HasMaxLength(8000);
        b.Property(l => l.IssueCode).HasMaxLength(4);
        b.Property(l => l.VoidCode).HasMaxLength(4);
        b.Property(l => l.UpdatedBy).HasMaxLength(320);
        b.HasIndex(l => l.Name).IsUnique();
    }
}

internal sealed class PaymentFileDownloadConfiguration : IEntityTypeConfiguration<PaymentFileDownload>
{
    public void Configure(EntityTypeBuilder<PaymentFileDownload> b)
    {
        b.ToTable("PaymentFileDownloads");
        b.Property(d => d.FileName).HasMaxLength(260);
        b.Property(d => d.FileIdModifier).HasMaxLength(1);
        b.Property(d => d.TotalAmount).Money();
        b.Property(d => d.Sha256).HasMaxLength(64);
        b.Property(d => d.DownloadedBy).HasMaxLength(320);
        b.HasIndex(d => d.RunId);
        b.HasIndex(d => new { d.BankAccountId, d.DownloadedAtUtc });
        b.HasOne<ApPaymentRun>().WithMany().HasForeignKey(d => d.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BankAccount>().WithMany().HasForeignKey(d => d.BankAccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RemittanceAdviceConfiguration : IEntityTypeConfiguration<RemittanceAdvice>
{
    public void Configure(EntityTypeBuilder<RemittanceAdvice> b)
    {
        b.ToTable("RemittanceAdvices");
        b.Property(r => r.Email).HasMaxLength(320);
        b.Property(r => r.Amount).Money();
        b.Property(r => r.SentBy).HasMaxLength(320);
        b.HasIndex(r => r.RunId);
        b.HasOne<ApPaymentRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(r => r.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}
