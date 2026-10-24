using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Workbench.Domain.Accounting;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence.Configurations;

/// <summary>The vendor columns accounts payable added (the base mapping is in ContractDetailConfigurations).</summary>
internal sealed class VendorPayablesConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b)
    {
        b.Property(v => v.SocioEconomicCategories).HasMaxLength(200);
        b.Property(v => v.ContactName).HasMaxLength(200);
        b.Property(v => v.ContactPhone).HasMaxLength(40);
        b.Property(v => v.Form1099Box).HasMaxLength(16);
        b.HasIndex(v => v.Uei);
        b.HasOne<StoredFile>().WithMany().HasForeignKey(v => v.W9FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubcontractPayablesConfiguration : IEntityTypeConfiguration<Subcontract>
{
    public void Configure(EntityTypeBuilder<Subcontract> b) => b.Property(s => s.RetainagePercent).HasPrecision(9, 4);
}

internal sealed class VendorTaxIdConfiguration : IEntityTypeConfiguration<VendorTaxId>
{
    public void Configure(EntityTypeBuilder<VendorTaxId> b)
    {
        b.Property(t => t.Kind).HasMaxLength(8);
        b.Property(t => t.ProtectedValue).HasMaxLength(2000);
        b.Property(t => t.UpdatedBy).HasMaxLength(320);
        b.HasIndex(t => t.VendorId).IsUnique();
        b.HasOne<Vendor>().WithMany().HasForeignKey(t => t.VendorId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubcontractFundingActionConfiguration : IEntityTypeConfiguration<SubcontractFundingAction>
{
    public void Configure(EntityTypeBuilder<SubcontractFundingAction> b)
    {
        b.Property(f => f.Number).HasMaxLength(16);
        b.Property(f => f.FundedChange).Money();
        b.Property(f => f.CeilingChange).Money();
        b.Property(f => f.Description).HasMaxLength(500);
        b.Property(f => f.CreatedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.SubcontractId, f.EffectiveDate });
        b.HasOne<Subcontract>().WithMany().HasForeignKey(f => f.SubcontractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubcontractFlowDownConfiguration : IEntityTypeConfiguration<SubcontractFlowDown>
{
    public void Configure(EntityTypeBuilder<SubcontractFlowDown> b)
    {
        b.Property(f => f.ClauseNumber).HasMaxLength(32);
        b.Property(f => f.Title).HasMaxLength(300);
        b.Property(f => f.Note).HasMaxLength(1000);
        b.Property(f => f.UpdatedBy).HasMaxLength(320);
        b.HasIndex(f => new { f.SubcontractId, f.ClauseNumber }).IsUnique();
        b.HasOne<Subcontract>().WithMany().HasForeignKey(f => f.SubcontractId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApBillConfiguration : IEntityTypeConfiguration<ApBill>
{
    public void Configure(EntityTypeBuilder<ApBill> b)
    {
        b.Property(x => x.Number).HasMaxLength(16);
        b.Property(x => x.InvoiceNumber).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Amount).Money();
        b.Property(x => x.RetainageAmount).Money();
        b.Property(x => x.PaidAmount).Money();
        b.Property(x => x.EnteredBy).HasMaxLength(320);
        b.Property(x => x.PostingProblem).HasMaxLength(1000);
        b.Property(x => x.FundingOverrideBy).HasMaxLength(320);
        b.Property(x => x.FundingOverrideReason).HasMaxLength(1000);
        b.Property(x => x.DuplicateConfirmedBy).HasMaxLength(320);
        b.Property(x => x.RejectionReason).HasMaxLength(1000);
        b.Property(x => x.VoidReason).HasMaxLength(1000);
        b.Ignore(x => x.Payable);
        b.Ignore(x => x.Balance);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.VendorId, x.InvoiceNumber });
        b.HasIndex(x => new { x.Status, x.DueDate });
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.AttachmentFileId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.BillId).OnDelete(DeleteBehavior.Cascade);

        // AP credits.
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.AppliedAmount).Money();
        b.Property(x => x.VoidedBy).HasMaxLength(320);
        b.HasIndex(x => new { x.VendorId, x.Kind, x.Status });
        b.HasIndex(x => x.CreditsBillId);
        b.HasOne<ApBill>().WithMany().HasForeignKey(x => x.CreditsBillId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApBillLineConfiguration : IEntityTypeConfiguration<ApBillLine>
{
    public void Configure(EntityTypeBuilder<ApBillLine> b)
    {
        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.Description).HasMaxLength(500);
        b.Property(l => l.Amount).Money();
        b.Property(l => l.RetainageAmount).Money();
        b.Property(l => l.OdcType).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(l => l.ApproverEmail).HasMaxLength(320);
        b.Property(l => l.DecidedBy).HasMaxLength(320);
        b.HasIndex(l => l.OdcEntryId);
        b.HasIndex(l => l.SubcontractId);
        b.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WbsElement>().WithMany().HasForeignKey(l => l.WbsElementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Subcontract>().WithMany().HasForeignKey(l => l.SubcontractId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OdcEntry>().WithMany().HasForeignKey(l => l.OdcEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OrgUnit>().WithMany().HasForeignKey(l => l.OrgUnitId).OnDelete(DeleteBehavior.Restrict);

        // AP credits.
        b.HasIndex(l => l.CreditsLineId);
        b.HasIndex(l => l.ReleasesBillId);
        b.HasOne<ApBillLine>().WithMany().HasForeignKey(l => l.CreditsLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApBill>().WithMany().HasForeignKey(l => l.ReleasesBillId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OdcEntry>().WithMany().HasForeignKey(l => l.ReversalOdcEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApPaymentRunConfiguration : IEntityTypeConfiguration<ApPaymentRun>
{
    public void Configure(EntityTypeBuilder<ApPaymentRun> b)
    {
        b.Property(r => r.Number).HasMaxLength(16);
        b.Property(r => r.Method).HasConversion<string>().HasMaxLength(8);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.CreatedBy).HasMaxLength(320);
        b.Property(r => r.ApprovedBy).HasMaxLength(320);
        b.HasIndex(r => r.Number).IsUnique();
    }
}

internal sealed class ApPaymentConfiguration : IEntityTypeConfiguration<ApPayment>
{
    public void Configure(EntityTypeBuilder<ApPayment> b)
    {
        b.Property(p => p.Amount).Money();
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(8);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(p => p.Reference).HasMaxLength(64);
        b.Property(p => p.PaidBy).HasMaxLength(320);
        b.Property(p => p.VoidReason).HasMaxLength(1000);
        b.Property(p => p.VoidedBy).HasMaxLength(320);
        b.HasIndex(p => p.RunId);
        b.HasIndex(p => new { p.VendorId, p.PaidOn });
        b.HasOne<ApPaymentRun>().WithMany().HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApBill>().WithMany().HasForeignKey(p => p.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApCreditApplicationConfiguration : IEntityTypeConfiguration<ApCreditApplication>
{
    public void Configure(EntityTypeBuilder<ApCreditApplication> b)
    {
        b.Property(a => a.Amount).Money();
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.AppliedBy).HasMaxLength(320);
        b.Property(a => a.ReversedBy).HasMaxLength(320);
        b.Property(a => a.ReversalReason).HasMaxLength(1000);
        b.HasIndex(a => a.CreditId);
        b.HasIndex(a => a.BillId);
        b.HasIndex(a => a.RunId);
        b.HasOne<ApBill>().WithMany().HasForeignKey(a => a.CreditId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApBill>().WithMany().HasForeignKey(a => a.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApPaymentRun>().WithMany().HasForeignKey(a => a.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApRefundConfiguration : IEntityTypeConfiguration<ApRefund>
{
    public void Configure(EntityTypeBuilder<ApRefund> b)
    {
        b.Property(r => r.Amount).Money();
        b.Property(r => r.Reference).HasMaxLength(64);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(r => r.RecordedBy).HasMaxLength(320);
        b.Property(r => r.VoidReason).HasMaxLength(1000);
        b.Property(r => r.VoidedBy).HasMaxLength(320);
        b.HasIndex(r => r.CreditId);
        b.HasIndex(r => new { r.VendorId, r.ReceivedOn });
        b.HasOne<ApBill>().WithMany().HasForeignKey(r => r.CreditId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(r => r.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
    }
}
